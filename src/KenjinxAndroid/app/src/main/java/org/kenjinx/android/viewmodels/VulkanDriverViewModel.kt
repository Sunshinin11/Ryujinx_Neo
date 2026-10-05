package org.kenjinx.android.viewmodels

import android.util.Log
import androidx.compose.runtime.MutableState
import com.anggrayudi.storage.file.extension
import com.anggrayudi.storage.file.openInputStream
import com.google.gson.Gson
import com.google.gson.JsonObject
import org.kenjinx.android.MainActivity
import org.kenjinx.android.driver.DriverSecurityValidator
import java.io.File
import java.io.FileOutputStream
import java.util.Locale

class VulkanDriverViewModel(val activity: MainActivity) {
    var selected: String = ""

    companion object {
        private const val TAG = "VulkanDriverViewModel"
        const val DriverRequestCode: Int = 1003
        const val DriverFolder: String = "drivers"
    }

    private fun getAppPath(): String {
        var appPath = MainActivity.AppPath
        appPath += "/"
        return appPath
    }

    fun ensureDriverPath(): File {
        val driverPath = getAppPath() + DriverFolder
        val driverFolder = File(driverPath)
        if (!driverFolder.exists())
            driverFolder.mkdirs()
        return driverFolder
    }

    fun getAvailableDrivers(): MutableList<DriverMetadata> {
        val driverFolder = ensureDriverPath()
        val folders = driverFolder.walkTopDown()
        val drivers = mutableListOf<DriverMetadata>()

        val selectedDriverFile = File(driverFolder.absolutePath + "/selected")
        if (selectedDriverFile.exists()) {
            selected = selectedDriverFile.readText().trim()

            if (!File(selected).exists()) {
                selected = ""
                saveSelected()
            }
        }

        val gson = Gson()

        for (folder in folders) {
            if (folder.isDirectory && folder.parent == driverFolder.absolutePath) {
                val meta = File(folder.absolutePath + "/meta.json")

                if (meta.exists()) {
                    try {
                        val metadata = gson.fromJson(meta.readText(), DriverMetadata::class.java)
                        if (metadata.name.isNotEmpty()) {
                            val driver = folder.absolutePath + "/${metadata.libraryName}"
                            metadata.driverPath = driver
                            if (File(driver).exists()) {
                                // Tenta carregar sidecar .neometa.json com metadados de catalogo
                                val neoMetaFile = File(folder, ".neometa.json")
                                if (neoMetaFile.exists()) {
                                    try {
                                        val neoJson = gson.fromJson(neoMetaFile.readText(), JsonObject::class.java)
                                        metadata.catalogId = neoJson.get("catalogId")?.asString.orEmpty()
                                        metadata.sourceTag = neoJson.get("sourceTag")?.asString.orEmpty().ifEmpty { metadata.author }
                                        metadata.sha256 = neoJson.get("sha256")?.asString.orEmpty()
                                        metadata.isCustomDownloaded = neoJson.get("isCustomDownloaded")?.asBoolean ?: true
                                    } catch (_: Exception) {}
                                } else {
                                    metadata.sourceTag = if (metadata.author.isNotBlank()) metadata.author else "Manual"
                                    metadata.isCustomDownloaded = false
                                }
                                drivers.add(metadata)
                            }
                        }
                    } catch (e: Exception) {
                        Log.w(TAG, "Falha ao ler meta.json em ${folder.name}: ${e.message}")
                    }
                }
            }
        }

        return drivers
    }

    fun saveSelected() {
        val driverFolder = ensureDriverPath()
        val selectedDriverFile = File(driverFolder.absolutePath + "/selected")
        if (DriverSecurityValidator.isPathInside(selectedDriverFile, driverFolder)) {
            selectedDriverFile.writeText(selected)
        }
    }

    fun removeSelected() {
        if (selected.isNotEmpty()) {
            val deletedPath = selected
            val sel = File(selected)
            val driverFolder = ensureDriverPath()
            val parent = sel.parentFile
            if (parent != null && parent.exists() && DriverSecurityValidator.isPathInside(parent, driverFolder)) {
                parent.deleteRecursively()
            }
            selected = ""
            saveSelected()

            // Limpa com seguranca referencias orfas em perfis por jogo
            GameSettingsStore.cleanupOrphanedDriver(activity, deletedPath)
        }
    }

    fun add(refresh: MutableState<Boolean>) {
        activity.storageHelper?.apply {
            val callBack = this.onFileSelected

            onFileSelected = { requestCode, files ->
                run {
                    onFileSelected = callBack
                    if (requestCode == DriverRequestCode) {
                        val file = files.firstOrNull()
                        file?.apply {
                            val stream = file.openInputStream(storage.context)
                            stream?.use { input ->
                                val rawName = file.name?.removeSuffix("." + file.extension) ?: "custom_${System.currentTimeMillis()}"
                                val sanitizedCandidate = rawName.lowercase(Locale.ROOT).replace(Regex("[^a-z0-9._-]"), "_").take(64).trimStart('.', '-', '_')
                                val driverId = if (sanitizedCandidate.isNotEmpty() && DriverSecurityValidator.validateId(sanitizedCandidate).isSuccess) {
                                    sanitizedCandidate
                                } else {
                                    "manual_${System.currentTimeMillis()}"
                                }

                                val driverFolder = ensureDriverPath()
                                val targetFolder = File(driverFolder, driverId)
                                val tempZip = File(activity.cacheDir, "manual_import_${driverId}_${System.currentTimeMillis()}.zip")
                                val stagingFolder = File(activity.cacheDir, "staging_manual_${driverId}_${System.currentTimeMillis()}")

                                try {
                                    // Verificacao de caminhos canonicos
                                    if (!DriverSecurityValidator.isPathInside(tempZip, activity.cacheDir) ||
                                        !DriverSecurityValidator.isPathInside(stagingFolder, activity.cacheDir) ||
                                        !DriverSecurityValidator.isPathInside(targetFolder, driverFolder)) {
                                        Log.e(TAG, "Tentativa de importacao com caminho fora dos diretorios autorizados.")
                                        return@use
                                    }

                                    // Salva em arquivo temporario
                                    FileOutputStream(tempZip).use { fos ->
                                        input.copyTo(fos)
                                    }

                                    // Validacao e extracao segura contra Zip Slip e symlinks
                                    stagingFolder.mkdirs()
                                    val extractResult = DriverSecurityValidator.safeExtractZip(tempZip, stagingFolder)
                                    if (extractResult.isFailure) {
                                        Log.e(TAG, "Falha ao descompactar driver manual: ${extractResult.exceptionOrNull()?.message}")
                                        return@use
                                    }

                                    // Validacao do pacote e ELF ARM64 (com normalizacao de subpasta superior)
                                    val pkgResult = DriverSecurityValidator.validateExtractedPackage(stagingFolder)
                                    if (pkgResult.isFailure) {
                                        Log.e(TAG, "Pacote de driver manual invalido: ${pkgResult.exceptionOrNull()?.message}")
                                        return@use
                                    }

                                    // Grava sidecar identificando origem manual
                                    val pkgInfo = pkgResult.getOrNull()
                                    val neoMeta = JsonObject().apply {
                                        addProperty("catalogId", driverId)
                                        addProperty("author", pkgInfo?.author.orEmpty().ifEmpty { "Manual" })
                                        addProperty("sourceTag", "Manual")
                                        addProperty("isCustomDownloaded", false)
                                    }
                                    File(stagingFolder, ".neometa.json").writeText(Gson().toJson(neoMeta))

                                    // Instalacao transacional protegida contra falha
                                    val swapResult = DriverSecurityValidator.transactionalDirectorySwap(
                                        stagingDir = stagingFolder,
                                        targetDir = targetFolder,
                                        allowedParent = driverFolder
                                    )
                                    if (swapResult.isFailure) {
                                        Log.e(TAG, "Falha na instalacao transacional de driver manual: ${swapResult.exceptionOrNull()?.message}")
                                        return@use
                                    }

                                    Log.i(TAG, "Driver manual '$driverId' instalado com sucesso em: ${targetFolder.absolutePath}")
                                } catch (e: Exception) {
                                    Log.e(TAG, "Erro na instalacao de driver manual: ${e.message}", e)
                                } finally {
                                    tempZip.delete()
                                    stagingFolder.deleteRecursively()
                                }
                            }
                        }
                        refresh.value = true
                    }
                }
            }
            openFilePicker(
                DriverRequestCode,
                filterMimeTypes = arrayOf("application/zip")
            )
        }
    }
}

data class DriverMetadata(
    var schemaVersion: Int = 0,
    var name: String = "",
    var description: String = "",
    var author: String = "",
    var packageVersion: String = "",
    var vendor: String = "",
    var driverVersion: String = "",
    var minApi: Int = 0,
    var libraryName: String = "",
    var driverPath: String = "",
    var sourceTag: String = "Manual",
    var catalogId: String = "",
    var sha256: String = "",
    var isCustomDownloaded: Boolean = false
)