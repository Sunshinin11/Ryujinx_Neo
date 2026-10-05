package org.kenjinx.android.driver

import android.os.Build
import android.util.Log
import com.google.gson.Gson
import com.google.gson.JsonObject
import kotlinx.coroutines.CancellationException
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.RandomAccessFile
import java.net.URI
import java.nio.file.Files
import java.security.MessageDigest
import java.util.Locale
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream

object DriverSecurityValidator {

    private const val TAG = "DriverSecurityValidator"

    val ALLOWED_REPOSITORIES = setOf(
        "MrPurple666/purple-turnip",
        "K11MCH1/AdrenoToolsDrivers",
        "whitebelyash/AdrenoToolsDrivers"
    )

    private val ALLOWED_DOMAINS = setOf(
        "github.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-production-release-asset-2e65be.s3.amazonaws.com"
    )

    private val ID_REGEX = Regex("^[a-z0-9][a-z0-9._-]{0,63}$")

    @Volatile
    internal var allowLocalhostForTesting: Boolean = false

    const val MAX_REMOTE_JSON_BYTES = 1024 * 1024 // 1 MB
    private const val MIN_FILE_SIZE_BYTES = 1024L // 1 KB
    private const val MAX_FILE_SIZE_BYTES = 100L * 1024L * 1024L // 100 MB
    private const val MAX_EXTRACTED_ENTRIES = 200
    private const val MAX_UNCOMPRESSED_SIZE_BYTES = 250L * 1024L * 1024L // 250 MB

    fun validateId(id: String): Result<Unit> {
        if (!ID_REGEX.matches(id)) {
            return Result.failure(IllegalArgumentException("ID de driver invalido: '$id'. Deve corresponder ao padrao ^[a-z0-9][a-z0-9._-]{0,63}$"))
        }
        return Result.success(Unit)
    }

    fun isPathInside(targetFile: File, allowedParent: File): Boolean {
        val targetCanonical = targetFile.canonicalPath
        val parentCanonical = allowedParent.canonicalPath
        return targetCanonical.startsWith(parentCanonical + File.separator) || targetCanonical == parentCanonical
    }

    fun validateCatalog(catalog: DriverCatalog): Result<DriverCatalog> {
        if (catalog.catalogVersion < 1) {
            return Result.failure(IllegalArgumentException("catalogVersion deve ser >= 1 (obtido: ${catalog.catalogVersion})."))
        }

        val seenIds = mutableSetOf<String>()
        val sanitizedDrivers = mutableListOf<DriverCatalogEntry>()

        for (driver in catalog.drivers) {
            if (driver.name.isBlank()) {
                return Result.failure(IllegalArgumentException("Driver com ID '${driver.id}' possui nome em branco."))
            }

            if (!seenIds.add(driver.id)) {
                return Result.failure(IllegalArgumentException("ID duplicado detectado no catalogo: '${driver.id}'."))
            }

            val entryValidation = validateCatalogEntry(driver)
            if (entryValidation.isFailure) {
                return Result.failure(entryValidation.exceptionOrNull()!!)
            }

            sanitizedDrivers.add(driver)
        }

        return Result.success(catalog.copy(drivers = sanitizedDrivers))
    }

    fun validateCatalogEntry(entry: DriverCatalogEntry): Result<Unit> {
        // 1. Validar ID
        val idVal = validateId(entry.id)
        if (idVal.isFailure) return idVal

        // 2. Validar sourceRepository contra a whitelist estrita
        val repo = entry.sourceRepository.trim()
        if (!ALLOWED_REPOSITORIES.contains(repo)) {
            return Result.failure(SecurityException("Repositorio '$repo' nao permitido na whitelist de seguranca."))
        }

        // 3. Validar downloadUrl
        if (!allowLocalhostForTesting && !entry.downloadUrl.startsWith("https://", ignoreCase = true)) {
            return Result.failure(SecurityException("URL de download deve utilizar estritamente protocolo HTTPS."))
        }

        val downloadUri = try {
            URI(entry.downloadUrl)
        } catch (e: Exception) {
            return Result.failure(SecurityException("URL de download malformada: ${e.message}"))
        }

        val dlHost = downloadUri.host?.lowercase(Locale.ROOT)
        val isAllowedHost = dlHost == "github.com" || (allowLocalhostForTesting && (dlHost == "127.0.0.1" || dlHost == "localhost"))
        if (!isAllowedHost) {
            return Result.failure(SecurityException("URL inicial de download deve ser hospedada em github.com (obtido: $dlHost)."))
        }

        // Validar que o caminho no GitHub contem exatamente o /owner/repository/ declarado sem escapes ou diferencas
        val dlPath = downloadUri.rawPath ?: ""
        if (dlPath.contains("%") || dlPath.contains("..")) {
            return Result.failure(SecurityException("URL de download contem caracteres de escape ou navegacao proibidos."))
        }

        val expectedDlPrefix = "/$repo/releases/download/"
        if (!dlPath.startsWith(expectedDlPrefix)) {
            return Result.failure(SecurityException("URL de download nao corresponde ao repositorio declarado '$repo'. Esperado prefixo: $expectedDlPrefix"))
        }

        // 4. Validar releasePage
        if (entry.releasePage.isNotBlank()) {
            if (!entry.releasePage.startsWith("https://", ignoreCase = true)) {
                return Result.failure(SecurityException("releasePage deve utilizar estritamente HTTPS."))
            }

            val releaseUri = try {
                URI(entry.releasePage)
            } catch (e: Exception) {
                return Result.failure(SecurityException("releasePage malformada: ${e.message}"))
            }

            val relHost = releaseUri.host?.lowercase(Locale.ROOT)
            if (relHost != "github.com") {
                return Result.failure(SecurityException("releasePage deve ser hospedada em github.com (obtido: $relHost)."))
            }

            val relPath = releaseUri.rawPath ?: ""
            if (relPath.contains("%") || relPath.contains("..")) {
                return Result.failure(SecurityException("releasePage contem caracteres de escape ou navegacao proibidos."))
            }

            val expectedRelPrefix = "/$repo/"
            if (!relPath.startsWith(expectedRelPrefix)) {
                return Result.failure(SecurityException("releasePage nao pertence ao repositorio declarado '$repo'."))
            }
        }

        // 5. Validar SHA-256
        if (!entry.sha256.matches(Regex("^[a-fA-F0-9]{64}$"))) {
            return Result.failure(IllegalArgumentException("Assinatura SHA-256 invalida para o driver '${entry.id}'."))
        }

        return Result.success(Unit)
    }

    fun isAllowedRedirect(redirectUrl: String): Boolean {
        if (!allowLocalhostForTesting && !redirectUrl.startsWith("https://", ignoreCase = true)) {
            return false
        }

        val uri = try {
            URI(redirectUrl)
        } catch (_: Exception) {
            return false
        }

        val host = uri.host?.lowercase(Locale.ROOT) ?: return false
        if (allowLocalhostForTesting && (host == "127.0.0.1" || host == "localhost")) return true
        return ALLOWED_DOMAINS.any { allowed -> host == allowed || host.endsWith(".$allowed") }
    }

    fun computeFileSha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        FileInputStream(file).use { fis ->
            val buffer = ByteArray(8192)
            var bytesRead: Int
            while (fis.read(buffer).also { bytesRead = it } != -1) {
                digest.update(buffer, 0, bytesRead)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    fun validateDownloadedZip(file: File, expectedSha256: String, calculatedSha256DuringStream: String? = null): Result<Unit> {
        val len = file.length()
        if (len < MIN_FILE_SIZE_BYTES) {
            return Result.failure(IllegalStateException("Arquivo baixado muito pequeno ($len bytes)."))
        }
        if (len > MAX_FILE_SIZE_BYTES) {
            return Result.failure(IllegalStateException("Arquivo baixado excede o limite maximo de 100 MB ($len bytes)."))
        }

        // Valida Magic Bytes de ZIP: 0x50 0x4B (PK)
        FileInputStream(file).use { fis ->
            val header = ByteArray(4)
            val read = fis.read(header)
            if (read < 4 || header[0] != 0x50.toByte() || header[1] != 0x4B.toByte()) {
                return Result.failure(IllegalArgumentException("O arquivo baixado nao possui cabecalho ZIP valido."))
            }
        }

        val actualSha = calculatedSha256DuringStream ?: computeFileSha256(file)
        if (!actualSha.equals(expectedSha256.trim(), ignoreCase = true)) {
            return Result.failure(SecurityException("Falha na verificacao de integridade SHA-256. Esperado: $expectedSha256, Obtido: $actualSha"))
        }

        return Result.success(Unit)
    }

    /**
     * Inspeciona os atributos externos de arquivo no diretorio central do ZIP.
     * Retorna true caso alguma entrada possua o bitmask de arquivo simbolico Unix (S_IFLNK = 0120000).
     */
    fun containsZipSymlinks(zipFile: File): Boolean {
        try {
            RandomAccessFile(zipFile, "r").use { raf ->
                val len = raf.length()
                if (len < 22) return false

                // Localiza o registro EOCD (End of Central Directory)
                val maxScan = minOf(len, 65536L + 22L)
                val scanStart = len - maxScan
                var eocdOffset = -1L

                for (pos in (len - 22) downTo scanStart) {
                    raf.seek(pos)
                    if (raf.readInt() == 0x504B0506) { // Little Endian PK\x05\x06 in Big Endian readInt = 0x504B0506
                        eocdOffset = pos
                        break
                    }
                }

                if (eocdOffset == -1L) return false

                // Le offset do Central Directory e numero de entradas a partir do EOCD
                raf.seek(eocdOffset + 16)
                val cdOffset = java.lang.Integer.reverseBytes(raf.readInt()).toLong() and 0xFFFFFFFFL
                raf.seek(eocdOffset + 10)
                val totalEntries = java.lang.Short.reverseBytes(raf.readShort()).toInt() and 0xFFFF

                raf.seek(cdOffset)
                for (i in 0 until totalEntries) {
                    val sig = java.lang.Integer.reverseBytes(raf.readInt())
                    if (sig != 0x02014B50) break // Fim ou formato inesperado

                    raf.skipBytes(24) // Pula ate file_name_length (offset 28)
                    val fnLen = java.lang.Short.reverseBytes(raf.readShort()).toInt() and 0xFFFF
                    val extraLen = java.lang.Short.reverseBytes(raf.readShort()).toInt() and 0xFFFF
                    val commentLen = java.lang.Short.reverseBytes(raf.readShort()).toInt() and 0xFFFF
                    raf.skipBytes(4) // Pula disk_number_start (2) e internal_attributes (2)

                    val externalAttrs = java.lang.Integer.reverseBytes(raf.readInt()).toLong() and 0xFFFFFFFFL
                    val unixMode = (externalAttrs ushr 16) and 0xFFFFL
                    // 0120000 octal = 0xA000 = S_IFLNK (Symbolic Link)
                    if ((unixMode and 0xF000L) == 0xA000L) {
                        return true
                    }

                    raf.skipBytes(4) // relative_offset_of_local_header (4 bytes)
                    raf.skipBytes(fnLen + extraLen + commentLen)
                }
            }
        } catch (_: Throwable) {
            // Em caso de erro na leitura binaria direta do central directory, continua para a verificacao em tempo de extracao
        }
        return false
    }

    fun safeExtractZip(
        zipFile: File,
        destinationDir: File,
        isCancelled: () -> Boolean = { false }
    ): Result<Unit> {
        if (!destinationDir.exists()) destinationDir.mkdirs()
        val destCanonical = destinationDir.canonicalPath

        // 1. Verificacao preventiva de symlinks no Central Directory do ZIP
        if (containsZipSymlinks(zipFile)) {
            destinationDir.deleteRecursively()
            return Result.failure(SecurityException("Links simbolicos (S_IFLNK) detectados e rejeitados no arquivo compactado."))
        }

        var entryCount = 0
        var totalBytes = 0L

        try {
            FileInputStream(zipFile).use { fis ->
                BufferedInputStream(fis).use { bis ->
                    ZipInputStream(bis).use { zis ->
                        var entry: ZipEntry? = zis.nextEntry
                        while (entry != null) {
                            if (isCancelled()) {
                                destinationDir.deleteRecursively()
                                return Result.failure(CancellationException("Extracao cancelada pelo usuario."))
                            }

                            entryCount++
                            if (entryCount > MAX_EXTRACTED_ENTRIES) {
                                destinationDir.deleteRecursively()
                                return Result.failure(SecurityException("O pacote excede o limite maximo de $MAX_EXTRACTED_ENTRIES arquivos."))
                            }

                            val cleanName = entry.name.replace('\\', '/')
                            if (cleanName.contains("..") || cleanName.startsWith("/")) {
                                destinationDir.deleteRecursively()
                                return Result.failure(SecurityException("Caminho suspeito detectado no arquivo compactado: $cleanName"))
                            }

                            val targetFile = File(destinationDir, cleanName)
                            val entryCanonical = targetFile.canonicalPath
                            if (!entryCanonical.startsWith(destCanonical + File.separator) && entryCanonical != destCanonical) {
                                destinationDir.deleteRecursively()
                                return Result.failure(SecurityException("Tentativa de Zip Slip detectada na entrada: ${entry.name}"))
                            }

                            if (entry.isDirectory) {
                                targetFile.mkdirs()
                            } else {
                                targetFile.parentFile?.mkdirs()
                                BufferedOutputStream(FileOutputStream(targetFile)).use { bos ->
                                    val buffer = ByteArray(8192)
                                    var read: Int
                                    while (zis.read(buffer).also { read = it } != -1) {
                                        if (isCancelled()) {
                                            destinationDir.deleteRecursively()
                                            return Result.failure(CancellationException("Extracao cancelada pelo usuario."))
                                        }
                                        totalBytes += read
                                        if (totalBytes > MAX_UNCOMPRESSED_SIZE_BYTES) {
                                            destinationDir.deleteRecursively()
                                            return Result.failure(SecurityException("Tamanho descompactado excede o limite maximo de 250 MB."))
                                        }
                                        bos.write(buffer, 0, read)
                                    }
                                }

                                // Verificacao explicita no filesystem
                                try {
                                    if (Files.isSymbolicLink(targetFile.toPath())) {
                                        targetFile.delete()
                                        destinationDir.deleteRecursively()
                                        return Result.failure(SecurityException("Links simbolicos nao sao permitidos no pacote."))
                                    }
                                } catch (_: Throwable) {}
                            }
                            zis.closeEntry()
                            entry = zis.nextEntry
                        }
                    }
                }
            }
        } catch (ce: CancellationException) {
            destinationDir.deleteRecursively()
            return Result.failure(ce)
        } catch (e: Exception) {
            destinationDir.deleteRecursively()
            return Result.failure(e)
        }

        return Result.success(Unit)
    }

    fun normalizeExtractedDirectory(dir: File) {
        if (!File(dir, "meta.json").exists()) {
            val subdirs = dir.listFiles { f -> f.isDirectory } ?: emptyArray()
            if (subdirs.size == 1 && File(subdirs[0], "meta.json").exists()) {
                val inner = subdirs[0]
                for (file in inner.listFiles() ?: emptyArray()) {
                    val target = File(dir, file.name)
                    if (file.renameTo(target)) {
                        // Movido com sucesso
                    } else {
                        file.copyRecursively(target, overwrite = true)
                        file.deleteRecursively()
                    }
                }
                inner.deleteRecursively()
            }
        }
    }

    fun transactionalDirectorySwap(
        stagingDir: File,
        targetDir: File,
        allowedParent: File
    ): Result<Unit> {
        if (!isPathInside(targetDir, allowedParent)) {
            return Result.failure(SecurityException("Diretorio de destino fora do caminho permitido: ${targetDir.canonicalPath}"))
        }
        if (!stagingDir.exists() || !stagingDir.isDirectory) {
            return Result.failure(IllegalStateException("Diretorio de staging inexistente ou invalido: ${stagingDir.canonicalPath}"))
        }

        var backupDir: File? = null

        // 1. Se o diretorio de destino ja existe, criar backup
        if (targetDir.exists()) {
            val candidateBackup = File(targetDir.parentFile, "${targetDir.name}_backup_${System.currentTimeMillis()}")
            if (!isPathInside(candidateBackup, allowedParent)) {
                return Result.failure(SecurityException("Diretorio de backup fora do caminho permitido."))
            }

            val renamed = targetDir.renameTo(candidateBackup)
            if (renamed) {
                backupDir = candidateBackup
            } else {
                // Tenta copiar para o backup
                val copied = targetDir.copyRecursively(candidateBackup, overwrite = true)
                if (!copied) {
                    // Falha no backup! Nao apagar o driver existente
                    candidateBackup.deleteRecursively()
                    return Result.failure(IllegalStateException("Falha ao criar backup do driver existente. Operacao abortada para proteger a instalacao anterior."))
                }
                // Se copiou com sucesso, agora podemos limpar o targetDir com seguranca
                if (!targetDir.deleteRecursively()) {
                    candidateBackup.deleteRecursively()
                    return Result.failure(IllegalStateException("Falha ao preparar diretorio de destino apos backup."))
                }
                backupDir = candidateBackup
            }
        }

        // 2. Tentar ativar o novo diretorio a partir de staging
        var activated = false
        try {
            if (stagingDir.renameTo(targetDir)) {
                activated = true
            } else {
                val copied = stagingDir.copyRecursively(targetDir, overwrite = true)
                if (copied) {
                    activated = true
                    stagingDir.deleteRecursively()
                } else {
                    activated = false
                }
            }
        } catch (e: Exception) {
            activated = false
        }

        // 3. Verificar resultado da ativacao
        if (!activated) {
            // Limpa instalacao parcial do targetDir
            targetDir.deleteRecursively()

            if (backupDir != null && backupDir.exists()) {
                val restored = backupDir.renameTo(targetDir) || (backupDir.copyRecursively(targetDir, overwrite = true) && backupDir.deleteRecursively())
                if (!restored) {
                    try {
                        Log.e(TAG, "CRITICO: Falha ao restaurar backup do driver em ${targetDir.absolutePath}. O backup foi preservado em: ${backupDir.absolutePath}")
                    } catch (_: Throwable) {
                        System.err.println("CRITICO: Falha ao restaurar backup do driver em ${targetDir.absolutePath}. O backup foi preservado em: ${backupDir.absolutePath}")
                    }
                    return Result.failure(IllegalStateException("Falha ao ativar novo driver e falha na restauracao do backup. Backup preservado em: ${backupDir.absolutePath}"))
                }
            }

            return Result.failure(IllegalStateException("Falha ao mover/copiar conteudo do staging para o destino final. Instalacao cancelada sem perda."))
        }

        // 4. Sucesso: remover backup anterior caso tenha sido criado
        backupDir?.deleteRecursively()
        return Result.success(Unit)
    }

    data class PackageValidationResult(
        val metaFile: File,
        val libraryFile: File,
        val libraryName: String,
        val driverName: String,
        val author: String,
        val version: String,
        val minApi: Int
    )

    fun validateExtractedPackage(extractedDir: File): Result<PackageValidationResult> {
        // 1. Normalizar se o conteudo estiver envelopado em uma unica pasta superior
        normalizeExtractedDirectory(extractedDir)

        val metaFile = File(extractedDir, "meta.json")
        if (!metaFile.exists()) {
            return Result.failure(IllegalStateException("meta.json nao encontrado na raiz do pacote do driver."))
        }

        val json = try {
            Gson().fromJson(metaFile.readText(), JsonObject::class.java)
        } catch (e: Exception) {
            return Result.failure(IllegalArgumentException("meta.json corrompido ou invalido: ${e.message}"))
        }

        val libraryName = json.get("libraryName")?.asString?.trim().orEmpty()
        if (libraryName.isEmpty()) {
            return Result.failure(IllegalArgumentException("meta.json nao define a propriedade obrigatoria 'libraryName'."))
        }

        val name = json.get("name")?.asString?.trim().orEmpty()
        val author = json.get("author")?.asString?.trim().orEmpty()
        val version = json.get("driverVersion")?.asString?.trim()
            ?: json.get("packageVersion")?.asString?.trim().orEmpty()
        val minApi = json.get("minApi")?.asInt ?: 29

        val deviceSdk = try { Build.VERSION.SDK_INT } catch (_: Throwable) { 0 }
        if (deviceSdk > 0 && minApi > deviceSdk) {
            return Result.failure(IllegalStateException("Este driver requer Android API $minApi, mas seu dispositivo possui API $deviceSdk."))
        }

        val libraryFile = File(extractedDir, libraryName)
        if (!libraryFile.exists() || !libraryFile.isFile) {
            return Result.failure(IllegalStateException("Biblioteca compartilhada '$libraryName' declarada em meta.json nao foi encontrada no pacote."))
        }

        // 2. Valida se a biblioteca e um binario ELF valido de 64 bits para ARM64
        val elfValidation = validateArm64Elf(libraryFile)
        if (elfValidation.isFailure) {
            return Result.failure(elfValidation.exceptionOrNull()!!)
        }

        return Result.success(
            PackageValidationResult(
                metaFile = metaFile,
                libraryFile = libraryFile,
                libraryName = libraryName,
                driverName = name,
                author = author,
                version = version,
                minApi = minApi
            )
        )
    }

    fun validateArm64Elf(libraryFile: File): Result<Unit> {
        try {
            RandomAccessFile(libraryFile, "r").use { raf ->
                if (raf.length() < 20) {
                    return Result.failure(IllegalArgumentException("Arquivo binario do driver muito pequeno."))
                }

                val header = ByteArray(20)
                raf.readFully(header)

                // Magic bytes: 0x7F 'E' 'L' 'F'
                if (header[0] != 0x7F.toByte() || header[1] != 'E'.code.toByte() ||
                    header[2] != 'L'.code.toByte() || header[3] != 'F'.code.toByte()) {
                    return Result.failure(SecurityException("O arquivo de driver '${libraryFile.name}' nao e um ELF binario valido."))
                }

                // EI_CLASS: 2 = 64-bit
                if (header[4] != 2.toByte()) {
                    return Result.failure(SecurityException("O driver deve ser estritamente 64-bit (ELFCLASS64)."))
                }

                // EI_DATA: 1 = Little Endian
                if (header[5] != 1.toByte()) {
                    return Result.failure(SecurityException("Formato de endianness invalido (esperado Little Endian)."))
                }

                // e_machine: bytes 18..19 (Little Endian: 0xB7 0x00 = 183 = EM_AARCH64)
                val eMachine = (header[18].toInt() and 0xFF) or ((header[19].toInt() and 0xFF) shl 8)
                if (eMachine != 183) {
                    return Result.failure(SecurityException("Arquitetura incompativel (e_machine=$eMachine). O driver deve ser compilado para ARM64/AArch64."))
                }
            }
        } catch (e: Exception) {
            return Result.failure(e)
        }

        return Result.success(Unit)
    }
}
