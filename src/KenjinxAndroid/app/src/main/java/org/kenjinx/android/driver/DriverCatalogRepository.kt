package org.kenjinx.android.driver

import android.content.Context
import android.util.Log
import com.google.gson.Gson
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.nio.file.Files
import java.nio.file.StandardCopyOption

class DriverCatalogRepository(private val context: Context) {

    companion object {
        private const val TAG = "DriverCatalogRepo"
        const val REMOTE_CATALOG_URL = "https://raw.githubusercontent.com/Sunshinin11/Ryujinx_Neo/neo-v2-nce2/drivers/catalog.json"
        private const val CACHE_FILE_NAME = "driver_catalog_cache.json"
        private const val TIMEOUT_MS = 6000
    }

    private val gson = Gson()
    private val cacheFile by lazy { File(context.filesDir, CACHE_FILE_NAME) }

    suspend fun loadCatalog(): Pair<DriverCatalog, CatalogSource> = withContext(Dispatchers.IO) {
        // 1. Tentar baixar catalogo remoto
        try {
            val remoteResult = fetchRemoteCatalog()
            if (remoteResult != null && remoteResult.drivers.isNotEmpty()) {
                val sanitized = sanitizeCatalog(remoteResult)
                if (sanitized.drivers.isNotEmpty()) {
                    saveToCache(sanitized)
                    Log.i(TAG, "Catalogo de drivers carregado remotamente com sucesso (${sanitized.drivers.size} drivers).")
                    return@withContext Pair(sanitized, CatalogSource.REMOTE)
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "Falha ao obter catalogo remoto (${e.message}). Tentando cache local...")
        }

        // 2. Tentar ler cache local valido
        try {
            if (cacheFile.exists() && DriverSecurityValidator.isPathInside(cacheFile, context.filesDir)) {
                val cachedJson = cacheFile.readText(Charsets.UTF_8)
                val cachedCatalog = gson.fromJson(cachedJson, DriverCatalog::class.java)
                if (cachedCatalog != null) {
                    val sanitized = sanitizeCatalog(cachedCatalog)
                    if (sanitized.drivers.isNotEmpty()) {
                        Log.i(TAG, "Catalogo carregado do cache local (${sanitized.drivers.size} drivers).")
                        return@withContext Pair(sanitized, CatalogSource.CACHE)
                    }
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "Cache local invalido ou corrompido: ${e.message}")
        }

        // 3. Fallback embarcado no APK (res/raw/driver_catalog_fallback.json)
        try {
            val fallbackJson = readRawFallback()
            val fallbackCatalog = gson.fromJson(fallbackJson, DriverCatalog::class.java)
            val sanitized = sanitizeCatalog(fallbackCatalog)
            Log.i(TAG, "Catalogo carregado do fallback embarcado (${sanitized.drivers.size} drivers).")
            return@withContext Pair(sanitized, CatalogSource.EMBEDDED_FALLBACK)
        } catch (e: Exception) {
            Log.e(TAG, "Erro critico ao carregar catalogo fallback embarcado: ${e.message}", e)
            return@withContext Pair(DriverCatalog(), CatalogSource.ERROR)
        }
    }

    private fun fetchRemoteCatalog(): DriverCatalog? {
        val url = URL(REMOTE_CATALOG_URL)
        val conn = (url.openConnection() as HttpURLConnection).apply {
            connectTimeout = TIMEOUT_MS
            readTimeout = TIMEOUT_MS
            requestMethod = "GET"
            setRequestProperty("User-Agent", "RyujinxNeo-DriverDownloader")
            instanceFollowRedirects = true
        }

        return try {
            val code = conn.responseCode
            if (code == HttpURLConnection.HTTP_OK) {
                val contentLength = conn.contentLengthLong
                if (contentLength > DriverSecurityValidator.MAX_REMOTE_JSON_BYTES) {
                    Log.w(TAG, "JSON remoto rejeitado: excede o limite maximo de ${DriverSecurityValidator.MAX_REMOTE_JSON_BYTES} bytes.")
                    return null
                }

                val baos = ByteArrayOutputStream()
                conn.inputStream.use { input ->
                    val buffer = ByteArray(4096)
                    var read: Int
                    var total = 0L
                    while (input.read(buffer).also { read = it } != -1) {
                        total += read
                        if (total > DriverSecurityValidator.MAX_REMOTE_JSON_BYTES) {
                            Log.w(TAG, "Stream do JSON remoto excedeu ${DriverSecurityValidator.MAX_REMOTE_JSON_BYTES} bytes durante a leitura.")
                            return null
                        }
                        baos.write(buffer, 0, read)
                    }
                }

                val json = baos.toString(Charsets.UTF_8.name())
                gson.fromJson(json, DriverCatalog::class.java)
            } else {
                Log.w(TAG, "Resposta HTTP $code ao buscar catalogo remoto (tratando como indisponivel).")
                null
            }
        } catch (e: Exception) {
            Log.w(TAG, "Excecao de rede ao buscar catalogo remoto: ${e.message}")
            null
        } finally {
            conn.disconnect()
        }
    }

    private fun readRawFallback(): String {
        val resId = context.resources.getIdentifier("driver_catalog_fallback", "raw", context.packageName)
        if (resId == 0) {
            throw IllegalStateException("Recurso raw/driver_catalog_fallback nao encontrado.")
        }
        return context.resources.openRawResource(resId).bufferedReader(Charsets.UTF_8).use { it.readText() }
    }

    private fun saveToCache(catalog: DriverCatalog) {
        try {
            if (!DriverSecurityValidator.isPathInside(cacheFile, context.filesDir)) {
                Log.e(TAG, "Tentativa de escrita de cache fora do diretorio permitido.")
                return
            }

            val tmpFile = File(context.filesDir, "${CACHE_FILE_NAME}.tmp")
            if (!DriverSecurityValidator.isPathInside(tmpFile, context.filesDir)) {
                return
            }

            tmpFile.writeText(gson.toJson(catalog), Charsets.UTF_8)

            // Substituicao estritamente atomica no mesmo sistema de arquivos
            try {
                Files.move(
                    tmpFile.toPath(),
                    cacheFile.toPath(),
                    StandardCopyOption.ATOMIC_MOVE,
                    StandardCopyOption.REPLACE_EXISTING
                )
            } catch (e: Exception) {
                if (!tmpFile.renameTo(cacheFile)) {
                    tmpFile.delete()
                    Log.e(TAG, "Falha na substituicao atomica do arquivo de cache: ${e.message}")
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "Falha ao salvar cache do catalogo: ${e.message}")
        }
    }

    private fun sanitizeCatalog(catalog: DriverCatalog): DriverCatalog {
        val validation = DriverSecurityValidator.validateCatalog(catalog)
        return if (validation.isSuccess) {
            validation.getOrThrow()
        } else {
            Log.w(TAG, "Catalogo rejeitado pela validacao: ${validation.exceptionOrNull()?.message}")
            DriverCatalog(catalogVersion = 0, drivers = emptyList())
        }
    }

    enum class CatalogSource {
        REMOTE,
        CACHE,
        EMBEDDED_FALLBACK,
        ERROR
    }
}