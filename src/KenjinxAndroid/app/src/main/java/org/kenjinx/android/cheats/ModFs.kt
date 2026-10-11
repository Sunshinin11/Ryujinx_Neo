package org.kenjinx.android.cheats

import android.app.Activity
import android.content.ContentResolver
import android.net.Uri
import android.provider.OpenableColumns
import android.util.Log
import java.io.File
import java.util.zip.ZipInputStream

/* -------- Paths -------- */

private fun modsRootExternal(activity: Activity): File {
    // /storage/emulated/0/Android/data/<pkg>/files/sdcard/atmosphere/contents
    return File(activity.getExternalFilesDir(null), "sdcard/atmosphere/contents")
}

private fun modsTitleDir(activity: Activity, titleIdUpper: String): File {
    // TITLEID must be written in capital letters
    return File(modsRootExternal(activity), titleIdUpper)
}

private fun modDir(activity: Activity, titleIdUpper: String, modName: String): File {
    return File(modsTitleDir(activity, titleIdUpper), modName)
}

/* -------- List & Delete -------- */

fun listMods(activity: Activity, titleId: String): List<String> {
    val titleIdUpper = titleId.trim().uppercase()
    val dir = modsTitleDir(activity, titleIdUpper)
    if (!dir.exists() || !dir.isDirectory) return emptyList()

    return dir.listFiles { f -> f.isDirectory } // NAME-Ordner
        ?.map { it.name }
        ?.sortedBy { it.lowercase() }
        ?: emptyList()
}

fun deleteMod(activity: Activity, titleId: String, modName: String): Boolean {
    val target = modDir(activity, titleId.trim().uppercase(), modName)
    return target.safeDeleteRecursively()
}

private fun File.safeDeleteRecursively(): Boolean {
    if (!exists()) return true
    return try {
        walkBottomUp().forEach {
            runCatching { if (it.isDirectory) it.delete() else it.delete() }
        }
        !exists()
    } catch (_: Throwable) {
        false
    }
}

/* -------- Import ZIP -------- */

data class ImportProgress(
    val bytesRead: Long,
    val totalBytes: Long,
    val currentEntry: String = ""
) {
    val fraction: Float
        get() = if (totalBytes <= 0) 0f else (bytesRead.coerceAtMost(totalBytes).toFloat() / totalBytes.toFloat())
}

// Multi-import. The top-level folders in the ZIP file are the mod names.
data class ImportModsResult(
    val imported: List<String>,
    val ok: Boolean
)

fun importModsZip(
    activity: Activity,
    titleId: String,
    zipUri: Uri,
    onProgress: (ImportProgress) -> Unit
): ImportModsResult {
    val titleIdUpper = titleId.trim().uppercase()
    val baseDir = modsTitleDir(activity, titleIdUpper).apply { mkdirs() }

    val (_, totalBytes) = resolveDisplayNameAndSize(activity.contentResolver, zipUri)
    var bytes = 0L
    fun bump(read: Int, entryName: String = "") {
        if (read > 0) {
            bytes += read
            onProgress(ImportProgress(bytesRead = bytes, totalBytes = totalBytes, currentEntry = entryName))
        }
    }

    // Prepare this once for each top-level folder (mod name) (delete the old folder if necessary).
    val preparedMods = mutableSetOf<String>()
    val importedMods = linkedSetOf<String>() // Reihenfolge stabil

    return try {
        activity.contentResolver.openInputStream(zipUri).use { raw ->
            if (raw == null) return@use
            ZipInputStream(raw).use { zis ->
                var entry = zis.nextEntry
                val buffer = ByteArray(DEFAULT_BUFFER_SIZE)

                while (entry != null) {
                    val rawName = entry.name.replace('\\', '/') // normalize
                    // Skip security filters and empty names
                    if (rawName.isBlank() || rawName.startsWith("/") || rawName.contains("..")) {
                        zis.closeEntry()
                        entry = zis.nextEntry
                        continue
                    }

                    // Top-Level: First segment before the first '/'
                    val slash = rawName.indexOf('/')
                    val topLevel = if (slash > 0) rawName.substring(0, slash) else rawName
                    if (topLevel.isBlank()) {
                        zis.closeEntry()
                        entry = zis.nextEntry
                        continue
                    }

                    // Remaining path within the mod folder
                    val relPath = if (slash >= 0 && slash + 1 < rawName.length) rawName.substring(slash + 1) else ""

                    // Only process entries that are located within a mod folder (we want NAME/... structures)
                    if (relPath.isBlank() && entry.isDirectory.not()) {
                        // Ignore files directly in the top-level directory (e.g., NAME.txt)
                        zis.closeEntry()
                        entry = zis.nextEntry
                        continue
                    }

                    // Prepare the mod folder (one-time process: remove the old folder if necessary)
                    if (preparedMods.add(topLevel)) {
                        val modFolder = modDir(activity, titleIdUpper, topLevel)
                        if (modFolder.exists()) modFolder.safeDeleteRecursively()
                        modFolder.mkdirs()
                        importedMods += topLevel
                    }

                    // Target path: .../TITLEID/<topLevel>/<relPath>
                    val dest = if (relPath.isBlank()) {
                        // only one folder entry (NAME/ or NAME/exefs/)
                        File(modDir(activity, titleIdUpper, topLevel), "")
                    } else {
                        File(modDir(activity, titleIdUpper, topLevel), relPath)
                    }

                    if (entry.isDirectory) {
                        dest.mkdirs()
                    } else {
                        dest.parentFile?.mkdirs()
                        dest.outputStream().use { os ->
                            var n = zis.read(buffer)
                            while (n > 0) {
                                os.write(buffer, 0, n)
                                bump(n, rawName)
                                n = zis.read(buffer)
                            }
                        }
                    }

                    zis.closeEntry()
                    entry = zis.nextEntry
                }
            }
        }

        ImportModsResult(imported = importedMods.toList(), ok = importedMods.isNotEmpty())
    } catch (t: Throwable) {
        Log.w("ModFs", "importModsZip failed: ${t.message}")
        // Best effort: Cleanly remove any mods that have already been installed
        importedMods.forEach { name ->
            runCatching { modDir(activity, titleIdUpper, name).safeDeleteRecursively() }
        }
        ImportModsResult(imported = emptyList(), ok = false)
    }
}

private fun resolveDisplayNameAndSize(cr: ContentResolver, uri: Uri): Pair<String?, Long> {
    var name: String? = null
    var size: Long = -1
    try {
        cr.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
            if (c.moveToFirst()) {
                val nameIdx = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                val sizeIdx = c.getColumnIndex(OpenableColumns.SIZE)
                if (nameIdx >= 0) name = c.getString(nameIdx)
                if (sizeIdx >= 0) size = c.getLong(sizeIdx)
            }
        }
    } catch (_: Throwable) {}
    return name to size
}
