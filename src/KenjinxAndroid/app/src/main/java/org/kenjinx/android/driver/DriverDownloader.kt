package org.kenjinx.android.driver

import android.content.Context
import android.util.Log
import com.google.gson.Gson
import com.google.gson.JsonObject
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.File
import java.io.FileOutputStream
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

class DriverDownloader(
    private val context: Context?,
    private val driversDir: File,
    private val customCacheDir: File? = null
) {
    private val effectiveCacheDir: File
        get() = customCacheDir ?: context?.cacheDir ?: File(System.getProperty("java.io.tmpdir") ?: ".")

    companion object {
        private const val TAG = "DriverDownloader"
        private const val BUFFER_SIZE = 8192
        private const val MAX_REDIRECTS = 5
    private suspend fun safeNotifyError(onError: (String) -> Unit, msg: String) {
        try {
            onError(msg)
        } catch (_: Throwable) {
            // O callback pertence à camada de UI; uma falha nele não pode
            // transformar uma instalação concluída em erro do download.
        }
    }

    private suspend fun safeNotifySuccess(onSuccess: () -> Unit) {
        try {
            onSuccess()
        } catch (_: Throwable) {
            // O callback pertence à camada de UI e não deve alterar o estado
            // da instalação caso a UI esteja sendo encerrada.
        }
    }

    private fun logI(msg: String) {
        if (org.kenjinx.android.BuildConfig.DEBUG) {
            try { Log.i(TAG, msg) } catch (_: Throwable) {}
        }
    }

    private fun logW(msg: String) {
        if (org.kenjinx.android.BuildConfig.DEBUG) {
            try { Log.w(TAG, msg) } catch (_: Throwable) {}
        }
    }

    private fun logE(msg: String, tr: Throwable? = null) {
        try { Log.e(TAG, msg, tr) } catch (_: Throwable) {}
    }
    }

    private class ActiveDownload(
        val driverId: String,
        val generation: Long,
        var job: Job? = null,
        val connection: AtomicReference<HttpURLConnection?> = AtomicReference(null),
        val isCancelled: AtomicBoolean = AtomicBoolean(false)
    )

    private val gson = Gson()
    private val scope = CoroutineScope(Dispatchers.IO + Job())
    private val generationCounter = AtomicLong(0)
    private val activeDownloads = ConcurrentHashMap<String, ActiveDownload>()

    private val _downloadStates = ConcurrentHashMap<String, MutableStateFlow<DriverDownloadState>>()

    fun getState(driverId: String): StateFlow<DriverDownloadState> {
        val flow = _downloadStates.computeIfAbsent(driverId) {
            MutableStateFlow(if (isDriverInstalled(driverId)) DriverDownloadState.Installed else DriverDownloadState.Available)
        }
        if (flow.value is DriverDownloadState.Available && isDriverInstalled(driverId)) {
            flow.value = DriverDownloadState.Installed
        }
        return flow.asStateFlow()
    }

    fun isDriverInstalled(driverId: String): Boolean {
        val targetFolder = File(driversDir, driverId)
        if (!targetFolder.exists() || !targetFolder.isDirectory) return false
        val meta = File(targetFolder, "meta.json")
        return meta.exists()
    }

    fun downloadAndInstall(
        entry: DriverCatalogEntry,
        onSuccess: () -> Unit = {},
        onError: (String) -> Unit = {}
    ) {
        val driverId = entry.id
        val gen = generationCounter.incrementAndGet()

        val stateFlow = _downloadStates.computeIfAbsent(driverId) {
            MutableStateFlow(DriverDownloadState.Available)
        }

        // Cancelar de forma deterministica download anterior para este mesmo driver
        val previous = activeDownloads.remove(driverId)
        previous?.let {
            it.isCancelled.set(true)
            it.job?.cancel()
            try { it.connection.get()?.disconnect() } catch (_: Exception) {}
        }

        val currentDownload = ActiveDownload(
            driverId = driverId,
            generation = gen
        )
        activeDownloads[driverId] = currentDownload

        val job = scope.launch {
            currentDownload.job = coroutineContext[Job]!!
            if (currentDownload.isCancelled.get()) {
                stateFlow.value = DriverDownloadState.Available
                return@launch
            }

            val tempZipFile = File(effectiveCacheDir, "temp_driver_${driverId}_${gen}.zip")
            val stagingDir = File(effectiveCacheDir, "staging_driver_${driverId}_${gen}")

            try {
                // 0. Validacao de seguranca de caminhos
                if (!DriverSecurityValidator.isPathInside(tempZipFile, effectiveCacheDir) ||
                    !DriverSecurityValidator.isPathInside(stagingDir, effectiveCacheDir)) {
                    val err = "Caminhos temporarios fora do cacheDir do aplicativo."
                    stateFlow.value = DriverDownloadState.Error(err)
                    safeNotifyError(onError, err)
                    return@launch
                }

                // 1. Validar Catalogo
                stateFlow.value = DriverDownloadState.Validating("Validando metadados do catalogo...")
                val catalogCheck = DriverSecurityValidator.validateCatalogEntry(entry)
                if (catalogCheck.isFailure) {
                    val err = catalogCheck.exceptionOrNull()?.message ?: "Falha na validacao do catalogo"
                    stateFlow.value = DriverDownloadState.Error(err)
                    safeNotifyError(onError, err)
                    return@launch
                }

                coroutineContext.ensureActive()
                if (currentDownload.isCancelled.get()) {
                    throw CancellationException("Download cancelado pelo usuario.")
                }

                // 2. Download com streaming continuo e calculo de SHA-256 em tempo real
                if (currentDownload.isCancelled.get()) {
                    stateFlow.value = DriverDownloadState.Available
                    return@launch
                }
                stateFlow.value = DriverDownloadState.Downloading(0f, 0L, 0L)
                val calculatedSha = downloadFileWithHash(
                    currentDownload = currentDownload,
                    urlString = entry.downloadUrl,
                    destinationFile = tempZipFile
                ) { downloaded, total ->
                    val progress = if (total > 0) (downloaded.toFloat() / total.toFloat()).coerceIn(0f, 1f) else 0f
                    stateFlow.value = DriverDownloadState.Downloading(progress, downloaded, total)
                }

                coroutineContext.ensureActive()
                if (currentDownload.isCancelled.get()) {
                    throw CancellationException("Download cancelado pelo usuario.")
                }

                // 3. Validar integridade final do ZIP baixado
                stateFlow.value = DriverDownloadState.Validating("Validando integridade SHA-256 e formato ZIP...")
                val zipCheck = DriverSecurityValidator.validateDownloadedZip(
                    file = tempZipFile,
                    expectedSha256 = entry.sha256,
                    calculatedSha256DuringStream = calculatedSha
                )
                if (zipCheck.isFailure) {
                    val err = zipCheck.exceptionOrNull()?.message ?: "Falha ao validar integridade do pacote"
                    stateFlow.value = DriverDownloadState.Error(err)
                    safeNotifyError(onError, err)
                    return@launch
                }

                coroutineContext.ensureActive()
                if (currentDownload.isCancelled.get()) {
                    throw CancellationException("Download cancelado pelo usuario.")
                }

                // 4. Extrair para pasta de staging com protecao Zip Slip e rejeicao estrita de symlinks
                stateFlow.value = DriverDownloadState.Validating("Descompactando pacote com seguranca...")
                stagingDir.mkdirs()
                val extractResult = DriverSecurityValidator.safeExtractZip(
                    zipFile = tempZipFile,
                    destinationDir = stagingDir,
                    isCancelled = { currentDownload.isCancelled.get() || !coroutineContext.isActive }
                )
                if (extractResult.isFailure) {
                    val err = extractResult.exceptionOrNull()?.message ?: "Falha ao extrair arquivos"
                    stateFlow.value = DriverDownloadState.Error(err)
                    safeNotifyError(onError, err)
                    return@launch
                }

                coroutineContext.ensureActive()
                if (currentDownload.isCancelled.get()) {
                    throw CancellationException("Download cancelado pelo usuario.")
                }

                // 5. Validar meta.json, normalizar pasta superior e validar ELF ARM64 64-bit
                stateFlow.value = DriverDownloadState.Validating("Validando compatibilidade ELF e meta.json...")
                val pkgValidation = DriverSecurityValidator.validateExtractedPackage(stagingDir)
                if (pkgValidation.isFailure) {
                    val err = pkgValidation.exceptionOrNull()?.message ?: "Pacote do driver invalido"
                    stateFlow.value = DriverDownloadState.Error(err)
                    safeNotifyError(onError, err)
                    return@launch
                }

                coroutineContext.ensureActive()
                if (currentDownload.isCancelled.get()) {
                    throw CancellationException("Download cancelado pelo usuario.")
                }

                // 6. Gravar metadados adicionais de proveniencia (.neometa.json) diretamente no staging
                val neoMeta = JsonObject().apply {
                    addProperty("catalogId", entry.id)
                    addProperty("author", entry.author)
                    addProperty("sourceRepository", entry.sourceRepository)
                    addProperty("sha256", entry.sha256)
                    addProperty("packageVersion", entry.packageVersion)
                    addProperty("mesaVersion", entry.mesaVersion)
                    addProperty("vulkanVersion", entry.vulkanVersion)
                    addProperty("sourceTag", entry.author)
                    addProperty("isCustomDownloaded", true)
                }
                File(stagingDir, ".neometa.json").writeText(gson.toJson(neoMeta))

                coroutineContext.ensureActive()
                if (currentDownload.isCancelled.get()) {
                    throw CancellationException("Download cancelado pelo usuario.")
                }

                // 7. Instalacao transacional em drivers/<driverId>
                stateFlow.value = DriverDownloadState.Validating("Finalizando instalacao transacional...")
                val targetDir = File(driversDir, driverId)
                val installResult = DriverSecurityValidator.transactionalDirectorySwap(
                    stagingDir = stagingDir,
                    targetDir = targetDir,
                    allowedParent = driversDir
                )
                if (installResult.isFailure) {
                    val err = installResult.exceptionOrNull()?.message ?: "Falha na instalacao transacional do driver"
                    stateFlow.value = DriverDownloadState.Error(err)
                    safeNotifyError(onError, err)
                    return@launch
                }

                coroutineContext.ensureActive()
                if (currentDownload.isCancelled.get()) {
                    throw CancellationException("Download cancelado pelo usuario.")
                }

                logI( "Driver '${entry.name}' instalado com sucesso em: ${targetDir.absolutePath}")
                stateFlow.value = DriverDownloadState.Installed
                safeNotifySuccess(onSuccess)

            } catch (ce: CancellationException) {
                logW( "Download do driver '$driverId' cancelado.")
                stateFlow.value = DriverDownloadState.Available
            } catch (e: Exception) {
                if (currentDownload.isCancelled.get() || !coroutineContext.isActive) {
                    logW( "Excecao suprimida apos cancelamento do driver '$driverId': ${e.message}")
                    stateFlow.value = DriverDownloadState.Available
                } else {
                    if (org.kenjinx.android.BuildConfig.DEBUG) {
                        println("DEBUG_DOWNLOADER_ERROR: ${e.javaClass.name}: ${e.message}")
                        e.printStackTrace()
                    }
                    val msg = e.localizedMessage ?: "Erro desconhecido durante a instalacao."
                    stateFlow.value = DriverDownloadState.Error(msg)
                    safeNotifyError(onError, msg)
                }
            } finally {
                // Remove do mapa apenas se ainda for esta exata instancia geracional
                activeDownloads.remove(driverId, currentDownload)
                try {
                    currentDownload.connection.get()?.disconnect()
                } catch (_: Exception) {}
                tempZipFile.delete()
                stagingDir.deleteRecursively()
                if (currentDownload.isCancelled.get()) {
                    stateFlow.value = DriverDownloadState.Available
                }
            }
        }
        currentDownload.job = job
    }

    fun cancelDownload(driverId: String) {
        val download = activeDownloads.remove(driverId)
        download?.isCancelled?.set(true)
        download?.job?.cancel()
        try {
            download?.connection?.get()?.disconnect()
        } catch (_: Exception) {}
        _downloadStates[driverId]?.value = DriverDownloadState.Available
    }

    fun cancelAll() {
        val allTasks = ArrayList(activeDownloads.values)
        activeDownloads.clear()
        for (task in allTasks) {
            task.isCancelled.set(true)
            task.job?.cancel()
            try {
                task.connection.get()?.disconnect()
            } catch (_: Exception) {}
            _downloadStates[task.driverId]?.value = DriverDownloadState.Available
        }
    }

    internal fun getActiveJob(driverId: String): kotlinx.coroutines.Job? {
        return activeDownloads[driverId]?.job
    }

    private suspend fun downloadFileWithHash(
        currentDownload: ActiveDownload,
        urlString: String,
        destinationFile: File,
        onProgress: (downloadedBytes: Long, totalBytes: Long) -> Unit
    ): String = withContext(Dispatchers.IO) {
        var currentUrl = urlString
        var redirects = 0

        while (redirects < MAX_REDIRECTS) {
            coroutineContext.ensureActive()
            if (currentDownload.isCancelled.get()) {
                throw CancellationException("Download cancelado pelo usuario.")
            }

            val url = URL(currentUrl)
            val conn = (url.openConnection() as HttpURLConnection).apply {
                connectTimeout = 10000
                readTimeout = 20000
                requestMethod = "GET"
                setRequestProperty("User-Agent", "RyujinxNeo-DriverDownloader")
                instanceFollowRedirects = false
            }

            currentDownload.connection.set(conn)

            try {
                val code = conn.responseCode
                if (code in 301..308) {
                    val location = conn.getHeaderField("Location")
                    if (location == null || !DriverSecurityValidator.isAllowedRedirect(location)) {
                        throw SecurityException("Redirecionamento invalido ou nao autorizado para: $location")
                    }
                    currentUrl = location
                    redirects++
                    continue
                }

                if (code != HttpURLConnection.HTTP_OK) {
                    throw IllegalStateException("Servidor retornou codigo HTTP $code ao baixar driver.")
                }

                val totalBytes = conn.contentLengthLong
                destinationFile.parentFile?.mkdirs()

                val digest = MessageDigest.getInstance("SHA-256")

                BufferedInputStream(conn.inputStream).use { bis ->
                    FileOutputStream(destinationFile).use { fos ->
                        val buffer = ByteArray(BUFFER_SIZE)
                        var downloaded = 0L
                        var read: Int

                        while (bis.read(buffer).also { read = it } != -1) {
                            coroutineContext.ensureActive()
                            if (currentDownload.isCancelled.get()) {
                                throw CancellationException("Download cancelado pelo usuario durante a transferencia.")
                            }

                            fos.write(buffer, 0, read)
                            digest.update(buffer, 0, read)
                            downloaded += read
                            onProgress(downloaded, totalBytes)
                        }
                    }
                }

                return@withContext digest.digest().joinToString("") { "%02x".format(it) }
            } finally {
                currentDownload.connection.set(null)
                conn.disconnect()
            }
        }

        throw IllegalStateException("Numero excessivo de redirecionamentos HTTP ($redirects).")
    }
}
