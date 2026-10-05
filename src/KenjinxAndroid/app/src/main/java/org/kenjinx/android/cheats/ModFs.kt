package org.kenjinx.android.cheats

import android.app.Activity
import android.content.ContentResolver
import android.net.Uri
import android.provider.OpenableColumns
import android.provider.DocumentsContract
import android.util.Log
import androidx.documentfile.provider.DocumentFile
import java.io.File
import java.util.Locale
import java.util.UUID
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
    val ok: Boolean,
    val error: String? = null
)

/** Copy a selected mod folder (or a collection of mod folders) through Android SAF. */
fun importModsFolder(
    activity: Activity,
    titleId: String,
    treeUri: Uri,
    onProgress: (ImportProgress) -> Unit
): ImportModsResult {
    var staging: File? = null
    val published = mutableListOf<File>()
    try {
        val id = titleId.trim().uppercase(Locale.ROOT)
        require(id.matches(Regex("[0-9A-F]{16}"))) { "Identificador de jogo inválido." }
        requireNotNull(activity.getExternalFilesDir(null)) { "O armazenamento do aplicativo está indisponível." }
        val selected = DocumentFile.fromTreeUri(activity, treeUri)
            ?: error("Não foi possível acessar a pasta selecionada.")
        require(selected.isDirectory && selected.canRead()) { "A pasta selecionada não pode ser lida." }
        // DocumentFile.listFiles can turn a provider error into an empty directory.
        // Query explicitly so incomplete listings fail rather than silently installing a partial mod.
        fun children(directory: DocumentFile): List<DocumentFile> {
            val id = DocumentsContract.getDocumentId(directory.uri)
            val uri = DocumentsContract.buildChildDocumentsUriUsingTree(directory.uri, id)
            return activity.contentResolver.query(uri, arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID), null, null, null).use { cursor ->
                requireNotNull(cursor) { "Não foi possível listar ${directory.name}." }
                buildList {
                    while (cursor.moveToNext()) {
                        val childUri = DocumentsContract.buildDocumentUriUsingTree(directory.uri, cursor.getString(0))
                        add(DocumentFile.fromSingleUri(activity, childUri) ?: error("Documento inválido na pasta."))
                    }
                }
            }
        }
        fun isMod(directory: DocumentFile): Boolean = directory.isDirectory && children(directory).any {
            val name = it.name?.lowercase(Locale.ROOT)
            (it.isDirectory && name in ModFolderRules.payloadDirectories) ||
                (it.isFile && name in ModFolderRules.payloadFiles)
        }
        val folders = if (isMod(selected)) listOf(selected) else children(selected).filter { isMod(it) }
        require(folders.isNotEmpty()) { "Selecione a pasta do mod que contém romfs, exefs ou cheats, ou uma pasta com vários mods." }
        val names = folders.map { it.name ?: error("Uma pasta está sem nome.") }
        require(names.all(ModFolderRules::isSafeName) && names.map { it.lowercase(Locale.ROOT) }.distinct().size == names.size) {
            "Há nomes de mod inválidos ou repetidos."
        }
        val destinationRoot = modsTitleDir(activity, id)
        val existing = destinationRoot.listFiles()?.map { it.name.lowercase(Locale.ROOT) }.orEmpty().toSet()
        val conflict = names.firstOrNull { it.lowercase(Locale.ROOT) in existing }
        require(conflict == null) { "O mod \"$conflict\" já existe. Remova-o no gerenciador ou renomeie a pasta antes de importar." }
        staging = File(modsRootExternal(activity).parentFile, ".mod-import-${UUID.randomUUID()}")
        require(staging.mkdirs()) { "Não foi possível preparar a importação." }
        var bytes = 0L
        var files = 0
        var lastProgress = 0L
        val visited = mutableSetOf<String>()
        fun copy(directory: DocumentFile, target: File, depth: Int, payload: Boolean = false): Int {
            require(depth <= 64 && visited.add(directory.uri.toString())) { "A pasta é muito profunda ou contém um ciclo." }
            require(directory.canRead() && (target.isDirectory || target.mkdirs())) { "Não foi possível copiar a pasta ${directory.name}." }
            val childNames = mutableSetOf<String>()
            var payloadFiles = 0
            for (child in children(directory)) {
                val rawName = child.name ?: error("Arquivo sem nome na pasta selecionada.")
                require(ModFolderRules.isSafeName(rawName)) { "Nome de arquivo inválido: $rawName" }
                val normalized = if (depth == 0 && rawName.lowercase(Locale.ROOT) in
                    (ModFolderRules.payloadDirectories + ModFolderRules.payloadFiles)) rawName.lowercase(Locale.ROOT) else rawName
                require(childNames.add(normalized.lowercase(Locale.ROOT))) { "Nomes de arquivo repetidos: $normalized" }
                val output = File(target, normalized)
                val inPayload = payload || (depth == 0 && normalized in ModFolderRules.payloadDirectories)
                if (child.isDirectory) payloadFiles += copy(child, output, depth + 1, inPayload)
                else {
                    require(child.isFile && child.canRead()) { "Não foi possível ler $rawName." }
                    val expected = child.length()
                    var copied = 0L
                    activity.contentResolver.openInputStream(child.uri).use { input ->
                        requireNotNull(input) { "Não foi possível abrir $rawName." }
                        output.outputStream().use { stream ->
                            val buffer = ByteArray(64 * 1024)
                            while (true) {
                                val count = input.read(buffer)
                                if (count < 0) break
                                if (count == 0) continue
                                stream.write(buffer, 0, count)
                                copied += count
                                bytes += count
                                val now = System.nanoTime()
                                if (now - lastProgress >= 100_000_000L) {
                                    onProgress(ImportProgress(bytes, 0, "$normalized · ${bytes / (1024 * 1024)} MB"))
                                    lastProgress = now
                                }
                            }
                            stream.fd.sync()
                        }
                    }
                    require(expected <= 0 || expected == copied) { "Cópia incompleta de $rawName." }
                    files++
                    if (inPayload || (depth == 0 && normalized in ModFolderRules.payloadFiles)) payloadFiles++
                }
            }
            return payloadFiles
        }
        folders.zip(names).forEach { (folder, name) ->
            require(copy(folder, File(staging, name), 0) > 0) { "O mod \"$name\" não contém arquivos em romfs, exefs ou cheats." }
        }
        require(destinationRoot.isDirectory || destinationRoot.mkdirs()) { "Não foi possível criar a pasta de mods do jogo." }
        names.forEach { name ->
            val destination = File(destinationRoot, name)
            require(!destination.exists() && File(staging, name).renameTo(destination)) { "Não foi possível instalar \"$name\"; nenhum mod existente foi substituído." }
            published += destination
        }
        onProgress(ImportProgress(bytes, bytes, "$files arquivos copiados"))
        return ImportModsResult(names, true)
    } catch (exception: Exception) {
        published.forEach { it.safeDeleteRecursively() }
        Log.w("ModFs", "Folder import failed", exception)
        return ImportModsResult(emptyList(), false, exception.message ?: "Não foi possível importar a pasta.")
    } finally {
        staging?.safeDeleteRecursively()
    }
}

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
