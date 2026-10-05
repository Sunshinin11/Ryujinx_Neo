package org.kenjinx.android.driver

import android.content.Context
import android.util.Log
import com.google.gson.Gson
import com.google.gson.JsonObject
import org.kenjinx.android.NativeHelpers
import org.kenjinx.android.viewmodels.DriverMetadata
import java.io.File

object DriverPreparationManager {

    private const val TAG = "Kenjinx"
    private const val MARKER_FILE_NAME = ".neo_installed_marker"
    private val gson = Gson()

    data class DriverLoadResult(
        val handle: Long,
        val isFallback: Boolean,
        val isCustomDriver: Boolean,
        val message: String
    )

    fun prepareAndLoadDriver(
        context: Context,
        selectedDriverPath: String,
        availableDrivers: List<DriverMetadata>
    ): DriverLoadResult {
        val nativeLibraryDir = context.applicationInfo.nativeLibraryDir ?: ""

        // 1. Caso seja o driver do sistema (selecao vazia)
        if (selectedDriverPath.isEmpty()) {
            Log.i(TAG, "[DriverLoader] Modo: Driver do sistema | Nome: Sistema Android | Status: Sucesso")
            return DriverLoadResult(
                handle = 0L,
                isFallback = false,
                isCustomDriver = false,
                message = "Usando driver Vulkan padrao do sistema."
            )
        }

        // 2. Localiza metadados do driver selecionado
        val metadata = availableDrivers.find { it.driverPath == selectedDriverPath }
            ?: run {
                Log.w(TAG, "[DriverLoader] Driver '$selectedDriverPath' nao encontrado na lista de drivers disponiveis. Fallback para sistema.")
                return DriverLoadResult(
                    handle = 0L,
                    isFallback = true,
                    isCustomDriver = false,
                    message = "Driver selecionado nao encontrado. Usando driver do sistema."
                )
            }

        val driverFile = File(selectedDriverPath)
        val driverParent = driverFile.parentFile
        if (driverParent == null || !driverParent.exists()) {
            Log.e(TAG, "[DriverLoader] Diretorio do driver nao existe: $selectedDriverPath. Fallback para sistema.")
            return DriverLoadResult(0L, isFallback = true, isCustomDriver = false, "Diretorio do driver invalido.")
        }

        val privatePath = context.filesDir
        val privateDriverDir = File(privatePath, "driver")
        val stagingDir = File(privatePath, "driver_staging_${System.currentTimeMillis()}")
        val markerFile = File(privateDriverDir, MARKER_FILE_NAME)

        // Validacao de caminhos canonicos
        if (!DriverSecurityValidator.isPathInside(privateDriverDir, privatePath) ||
            !DriverSecurityValidator.isPathInside(stagingDir, privatePath)) {
            Log.e(TAG, "[DriverLoader] Caminho fora de filesDir detectado.")
            return DriverLoadResult(0L, isFallback = true, isCustomDriver = false, "Caminho de instalacao invalido.")
        }

        // Calcula ou le SHA-256 do driver
        val sha256 = resolveDriverSha256(driverParent, driverFile)
        val shortSha = if (sha256.length >= 12) sha256.substring(0, 12) else sha256.ifEmpty { "desconhecido" }
        val catalogId = metadata.catalogId.ifEmpty { "manual" }
        val version = metadata.driverVersion.ifBlank { metadata.packageVersion }.ifBlank { "1.0" }
        val author = metadata.author.ifBlank { "Desconhecido" }
        val name = metadata.name.ifBlank { metadata.libraryName }

        // Log estruturado ANTES de tentar carregar
        Log.i(
            TAG,
            "[DriverLoader] Preparando driver: Tipo='Driver personalizado' | Nome='$name' | Autor='$author' | Versao='$version' | Library='${metadata.libraryName}' | Catalogo='$catalogId' | SHA-256='$shortSha'"
        )

        // 3. Verifica se ja esta preparado com o marcador correto
        val needsDeploy = !isAlreadyInstalled(privateDriverDir, markerFile, selectedDriverPath, sha256, metadata.libraryName)

        if (needsDeploy) {
            try {
                stagingDir.deleteRecursively()
                stagingDir.mkdirs()

                // Copia preservando estrutura relativa (sem achatar subdiretorios)
                copyPreservingHierarchy(driverParent, stagingDir)

                // Grava novo marcador
                val markerData = JsonObject().apply {
                    addProperty("sourcePath", selectedDriverPath)
                    addProperty("sha256", sha256)
                    addProperty("libraryName", metadata.libraryName)
                    addProperty("installedAt", System.currentTimeMillis())
                }
                File(stagingDir, MARKER_FILE_NAME).writeText(gson.toJson(markerData))

                // Substituicao transacional: backup da instalacao anterior com reversao em caso de falha
                val swapResult = DriverSecurityValidator.transactionalDirectorySwap(
                    stagingDir = stagingDir,
                    targetDir = privateDriverDir,
                    allowedParent = privatePath
                )
                if (swapResult.isFailure) {
                    throw swapResult.exceptionOrNull() ?: IllegalStateException("Falha no swap transacional")
                }
                Log.i(TAG, "[DriverLoader] Driver preparado e copiado com sucesso para ${privateDriverDir.canonicalPath}")
            } catch (e: Exception) {
                Log.e(TAG, "[DriverLoader] Erro ao preparar diretorio do driver: ${e.message}. Preservando instalacao anterior caso exista.", e)
                stagingDir.deleteRecursively()
            }
        } else {
            Log.i(TAG, "[DriverLoader] Reutilizando instalacao existente de '$name' (SHA: $shortSha) sem recopiar.")
        }

        // 4. Executa loadDriver via JNI / adrenotools
        val privateDriverPathString = privateDriverDir.canonicalPath + "/"
        val driverHandle = NativeHelpers.instance.loadDriver(
            nativeLibraryDir + "/",
            privateDriverPathString,
            metadata.libraryName
        )

        // 5. Analisa o resultado e registra no log
        return if (driverHandle == 0L) {
            Log.e(
                TAG,
                "[DriverLoader] Status: FALLBACK | Falha ao inicializar driver personalizado '$name' via adrenotools_open_libvulkan. Executando com seguranca no driver do sistema."
            )
            DriverLoadResult(
                handle = 0L,
                isFallback = true,
                isCustomDriver = true,
                message = "Falha ao carregar driver '$name'. Usando driver do sistema."
            )
        } else {
            Log.i(
                TAG,
                "[DriverLoader] Status: SUCESSO | Driver personalizado '$name' ativo (handle=$driverHandle, SHA=$shortSha)."
            )
            DriverLoadResult(
                handle = driverHandle,
                isFallback = false,
                isCustomDriver = true,
                message = "Driver '$name' carregado com sucesso."
            )
        }
    }

    private fun isAlreadyInstalled(
        targetDir: File,
        markerFile: File,
        sourcePath: String,
        sha256: String,
        libraryName: String
    ): Boolean {
        if (!targetDir.exists() || !markerFile.exists()) return false
        val libFile = File(targetDir, libraryName)
        if (!libFile.exists()) return false

        return try {
            val json = gson.fromJson(markerFile.readText(), JsonObject::class.java)
            val markerSource = json.get("sourcePath")?.asString.orEmpty()
            val markerSha = json.get("sha256")?.asString.orEmpty()
            val markerLib = json.get("libraryName")?.asString.orEmpty()

            markerSource == sourcePath && markerSha == sha256 && markerLib == libraryName
        } catch (_: Exception) {
            false
        }
    }

    private fun resolveDriverSha256(parentDir: File, driverFile: File): String {
        val neoMeta = File(parentDir, ".neometa.json")
        if (neoMeta.exists()) {
            try {
                val json = gson.fromJson(neoMeta.readText(), JsonObject::class.java)
                val sha = json.get("sha256")?.asString.orEmpty()
                if (sha.isNotEmpty()) return sha
            } catch (_: Exception) {}
        }

        return try {
            DriverSecurityValidator.computeFileSha256(driverFile)
        } catch (_: Exception) {
            ""
        }
    }

    private fun copyPreservingHierarchy(sourceRoot: File, destinationRoot: File) {
        for (file in sourceRoot.walkTopDown()) {
            if (file.absolutePath == sourceRoot.absolutePath) continue
            val relativePath = file.relativeTo(sourceRoot).path
            val destFile = File(destinationRoot, relativePath)

            if (file.isDirectory) {
                destFile.mkdirs()
            } else {
                destFile.parentFile?.mkdirs()
                file.copyTo(destFile, overwrite = true)
            }
        }
    }
}