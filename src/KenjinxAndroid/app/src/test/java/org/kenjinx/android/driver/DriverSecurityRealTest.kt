package org.kenjinx.android.driver

import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Test
import java.io.BufferedReader
import java.io.File
import java.io.FileOutputStream
import java.io.InputStreamReader
import java.io.RandomAccessFile
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

class DriverSecurityRealTest {

    @Test
    fun testAllowedRedirectRealDomain() {
        assertTrue(DriverSecurityValidator.isAllowedRedirect("https://release-assets.githubusercontent.com/123/file.zip"))
        assertTrue(DriverSecurityValidator.isAllowedRedirect("https://objects.githubusercontent.com/456/file.zip"))
        assertTrue(DriverSecurityValidator.isAllowedRedirect("https://github.com/MrPurple666/purple-turnip/releases/download/v1/file.zip"))
        // Insecure HTTP must be blocked
        assertFalse(DriverSecurityValidator.isAllowedRedirect("http://release-assets.githubusercontent.com/123/file.zip"))
        // Unapproved domains must be blocked
        assertFalse(DriverSecurityValidator.isAllowedRedirect("https://evil-server.com/malicious.zip"))
    }

    @Test
    fun testValidateCatalogEntrySecurity() {
        val validEntry = DriverCatalogEntry(
            id = "mrpurple-turnip-t30",
            name = "Turnip T30",
            author = "MrPurple666",
            sourceRepository = "MrPurple666/purple-turnip",
            releasePage = "https://github.com/MrPurple666/purple-turnip/releases/tag/vturnip_mrpurple_T30-toasted.adpkg",
            downloadUrl = "https://github.com/MrPurple666/purple-turnip/releases/download/vturnip_mrpurple_T30-toasted.adpkg/turnip_mrpurple_T30-toasted.adpkg.zip",
            fileName = "turnip_mrpurple_T30-toasted.adpkg.zip",
            sha256 = "f65b2d3353fd4aa7190bb5426b94468e99ffea7a58a830bc0c4651db89353227",
            packageVersion = "T-30",
            mesaVersion = "26.3.0-devel",
            vulkanVersion = "1.4.359",
            minApi = 30
        )
        assertTrue(DriverSecurityValidator.validateCatalogEntry(validEntry).isSuccess)

        // Forged repo in downloadUrl
        val forgedDl = validEntry.copy(downloadUrl = "https://github.com/eviluser/purple-turnip/releases/download/v1/turnip.zip")
        assertTrue(DriverSecurityValidator.validateCatalogEntry(forgedDl).isFailure)

        // Forged repo in releasePage
        val forgedRel = validEntry.copy(releasePage = "https://github.com/eviluser/other-repo/releases/tag/v1")
        assertTrue(DriverSecurityValidator.validateCatalogEntry(forgedRel).isFailure)

        // Non-whitelisted repository
        val badRepo = validEntry.copy(sourceRepository = "unknown/freedreno-turnip-CI")
        assertTrue(DriverSecurityValidator.validateCatalogEntry(badRepo).isFailure)

        // Invalid SHA
        val badSha = validEntry.copy(sha256 = "invalid_hash")
        assertTrue(DriverSecurityValidator.validateCatalogEntry(badSha).isFailure)
    }

    @Test
    fun testValidateIdStrictRegex() {
        assertTrue(DriverSecurityValidator.validateId("turnip-v30").isSuccess)
        assertTrue(DriverSecurityValidator.validateId("k11mch1-qualcomm-v840").isSuccess)
        assertTrue(DriverSecurityValidator.validateId("driver_v32.1").isSuccess)
        assertTrue(DriverSecurityValidator.validateId("a").isSuccess)

        assertFalse(DriverSecurityValidator.validateId("../evil").isSuccess)
        assertFalse(DriverSecurityValidator.validateId("driver/../evil").isSuccess)
        assertFalse(DriverSecurityValidator.validateId(".hidden").isSuccess)
        assertFalse(DriverSecurityValidator.validateId("-hyphen-start").isSuccess)
        assertFalse(DriverSecurityValidator.validateId("DRIVER_CAPS").isSuccess)
        assertFalse(DriverSecurityValidator.validateId("a".repeat(65)).isSuccess)
    }

    @Test
    fun testCanonicalPathInside() {
        val baseDir = File(System.getProperty("java.io.tmpdir"), "test_base_${System.currentTimeMillis()}")
        baseDir.mkdirs()
        try {
            val insideFile = File(baseDir, "driver_1")
            val outsideFile = File(baseDir, "../outside_${System.currentTimeMillis()}")
            assertTrue(DriverSecurityValidator.isPathInside(insideFile, baseDir))
            assertFalse(DriverSecurityValidator.isPathInside(outsideFile, baseDir))
        } finally {
            baseDir.deleteRecursively()
        }
    }

    @Test
    fun testZipSlipPreventionRealExtraction() {
        val tmpDir = File(System.getProperty("java.io.tmpdir"), "zip_slip_test_${System.currentTimeMillis()}")
        val destDir = File(tmpDir, "extracted")
        tmpDir.mkdirs()
        destDir.mkdirs()

        val slipZip = File(tmpDir, "slip.zip")
        try {
            ZipOutputStream(FileOutputStream(slipZip)).use { zos ->
                zos.putNextEntry(ZipEntry("../../escaped.txt"))
                zos.write("malicious payload".toByteArray())
                zos.closeEntry()
            }

            val result = DriverSecurityValidator.safeExtractZip(slipZip, destDir)
            assertTrue("Zip Slip deve falhar na extracao", result.isFailure)
            assertFalse("Arquivo nao pode ter escapado", File(tmpDir, "escaped.txt").exists())
        } finally {
            tmpDir.deleteRecursively()
        }
    }

    @Test
    fun testNormalizeExtractedDirectoryUnwrapsNested() {
        val staging = File(System.getProperty("java.io.tmpdir"), "norm_test_${System.currentTimeMillis()}")
        val nested = File(staging, "turnip-t30-nested")
        nested.mkdirs()
        try {
            File(nested, "meta.json").writeText("{\"name\":\"Turnip\"}")
            File(nested, "vulkan.adreno.so").writeText("ELF_PAYLOAD")

            DriverSecurityValidator.normalizeExtractedDirectory(staging)

            assertTrue(File(staging, "meta.json").exists())
            assertTrue(File(staging, "vulkan.adreno.so").exists())
            assertFalse(nested.exists())
        } finally {
            staging.deleteRecursively()
        }
    }

    @Test
    fun testTransactionalDirectorySwapRollbackOnFailure() {
        val parent = File(System.getProperty("java.io.tmpdir"), "trans_test_${System.currentTimeMillis()}")
        parent.mkdirs()
        val target = File(parent, "installed_driver")
        target.mkdirs()
        File(target, "meta.json").writeText("{\"name\":\"Original Driver\"}")
        File(target, "vulkan.so").writeText("ORIGINAL_LIB")

        val invalidStaging = File(parent, "non_existent_staging")
        try {
            val result = DriverSecurityValidator.transactionalDirectorySwap(
                stagingDir = invalidStaging,
                targetDir = target,
                allowedParent = parent
            )
            assertTrue("Operacao com staging invalido deve falhar", result.isFailure)
            assertTrue("Driver original deve ser mantido", File(target, "meta.json").exists())
            assertEquals("{\"name\":\"Original Driver\"}", File(target, "meta.json").readText())
        } finally {
            parent.deleteRecursively()
        }
    }

    @Test
    fun testTransactionalSwapPreservesDriverOnActivationFailure() {
        val parent = File(System.getProperty("java.io.tmpdir"), "trans_fail_${System.currentTimeMillis()}")
        parent.mkdirs()
        val target = File(parent, "installed_driver").apply { mkdirs() }
        File(target, "meta.json").writeText("{\"name\":\"Original Working Driver\"}")
        File(target, "vulkan.so").writeText("ORIGINAL_BINARY")

        val staging = File(parent, "staging_driver").apply { mkdirs() }
        File(staging, "meta.json").writeText("{\"name\":\"New Broken Driver\"}")
        val lockedFile = File(staging, "vulkan.so").apply { writeText("NEW_BINARY") }

        // Bloqueia o arquivo dentro do staging para forcar falha durante a ativacao (rename e copy)
        val raf = RandomAccessFile(lockedFile, "rw")
        val fileChannel = raf.channel
        val fileLock = fileChannel.lock()

        try {
            val result = DriverSecurityValidator.transactionalDirectorySwap(
                stagingDir = staging,
                targetDir = target,
                allowedParent = parent
            )

            assertTrue("A ativacao com arquivo bloqueado deve falhar", result.isFailure)

            // Verifica que o driver anterior foi completamente preservado e restaurado
            assertTrue("Diretorio de destino deve existir", target.exists())
            assertTrue("meta.json do driver original deve existir", File(target, "meta.json").exists())
            assertEquals(
                "Conteudo do driver original deve ser preservado integralmente",
                "{\"name\":\"Original Working Driver\"}",
                File(target, "meta.json").readText()
            )
            assertEquals(
                "Binario do driver original deve ser preservado integralmente",
                "ORIGINAL_BINARY",
                File(target, "vulkan.so").readText()
            )
        } finally {
            try { fileLock?.release() } catch (_: Throwable) {}
            try { fileChannel?.close() } catch (_: Throwable) {}
            try { raf.close() } catch (_: Throwable) {}
            parent.deleteRecursively()
        }
    }

    @Test
    fun testContainsZipSymlinksDetectsOnSecondEntry() {
        val tmpDir = File(System.getProperty("java.io.tmpdir"), "symlink_test_${System.currentTimeMillis()}")
        tmpDir.mkdirs()
        val zipFile = File(tmpDir, "test_two_entries.zip")

        try {
            // 1. Cria um arquivo ZIP com duas entradas comuns
            ZipOutputStream(FileOutputStream(zipFile)).use { zos ->
                zos.putNextEntry(ZipEntry("first_file.txt"))
                zos.write("regular content 1".toByteArray())
                zos.closeEntry()

                zos.putNextEntry(ZipEntry("second_symlink.so"))
                zos.write("symlink_target.so".toByteArray())
                zos.closeEntry()
            }

            // Antes de alterar atributos, deve retornar false
            assertFalse(DriverSecurityValidator.containsZipSymlinks(zipFile))

            // 2. Modifica a 2a entrada no Central Directory para marcar como symlink (0xA000 = S_IFLNK)
            RandomAccessFile(zipFile, "rw").use { raf ->
                val len = raf.length()
                var eocdPos = -1L
                for (pos in (len - 22) downTo maxOf(0L, len - 1024)) {
                    raf.seek(pos)
                    if (raf.readInt() == 0x504B0506) {
                        eocdPos = pos
                        break
                    }
                }
                assertTrue("EOCD deve ser encontrado", eocdPos != -1L)

                // Le offset do Central Directory
                raf.seek(eocdPos + 16)
                val cdOffset = java.lang.Integer.reverseBytes(raf.readInt()).toLong() and 0xFFFFFFFFL

                // Navega pela 1a entrada no Central Directory
                raf.seek(cdOffset)
                assertEquals(0x02014B50, java.lang.Integer.reverseBytes(raf.readInt()))
                raf.skipBytes(24) // pula ate offset 28 (file_name_length)
                val fnLen1 = java.lang.Short.reverseBytes(raf.readShort()).toInt() and 0xFFFF
                val extraLen1 = java.lang.Short.reverseBytes(raf.readShort()).toInt() and 0xFFFF
                val commentLen1 = java.lang.Short.reverseBytes(raf.readShort()).toInt() and 0xFFFF
                raf.skipBytes(4) // internal + disk
                raf.skipBytes(4) // external_attributes
                raf.skipBytes(4) // relative_offset_of_local_header (exatamente 4 bytes!)
                raf.skipBytes(fnLen1 + extraLen1 + commentLen1)

                // Agora estamos exatamente no inicio da 2a entrada!
                assertEquals(0x02014B50, java.lang.Integer.reverseBytes(raf.readInt()))
                raf.skipBytes(24) // offset 28
                val fnLen2 = java.lang.Short.reverseBytes(raf.readShort()).toInt() and 0xFFFF
                val extraLen2 = java.lang.Short.reverseBytes(raf.readShort()).toInt() and 0xFFFF
                val commentLen2 = java.lang.Short.reverseBytes(raf.readShort()).toInt() and 0xFFFF
                raf.skipBytes(4) // internal + disk

                // Escreve atributos externos com bitmask S_IFLNK (0120000 octal = 0xA000)
                // Little Endian: bytes 38..41 -> 0x00, 0x00, 0x00, 0xA0 (upper 16 bits = 0xA000)
                raf.write(byteArrayOf(0x00, 0x00, 0x00, 0xA0.toByte()))
            }

            // Com o symlink na 2a entrada e o salto correto de 4 bytes, DEVE detectar!
            assertTrue(
                "DriverSecurityValidator.containsZipSymlinks deve detectar o symlink na 2a entrada",
                DriverSecurityValidator.containsZipSymlinks(zipFile)
            )

            // Valida tambem que safeExtractZip rejeita o arquivo
            val destDir = File(tmpDir, "extracted")
            val extractResult = DriverSecurityValidator.safeExtractZip(zipFile, destDir)
            assertTrue("safeExtractZip deve falhar para ZIP com symlink", extractResult.isFailure)
        } finally {
            tmpDir.deleteRecursively()
        }
    }

    @Test
    fun testDriverDownloaderRealCancellation() = runBlocking {
        val tmpDir = File(System.getProperty("java.io.tmpdir"), "downloader_cancel_${System.currentTimeMillis()}")
        val driversDir = File(tmpDir, "drivers").apply { mkdirs() }
        val cacheDir = File(tmpDir, "cache").apply { mkdirs() }

        try {
            val downloader = DriverDownloader(
                context = null,
                driversDir = driversDir,
                customCacheDir = cacheDir
            )

            val entry = DriverCatalogEntry(
                id = "mrpurple-turnip-t30",
                name = "Turnip T30",
                author = "MrPurple666",
                sourceRepository = "MrPurple666/purple-turnip",
                downloadUrl = "https://github.com/MrPurple666/purple-turnip/releases/download/vturnip_mrpurple_T30-toasted.adpkg/turnip_mrpurple_T30-toasted.adpkg.zip",
                fileName = "turnip_mrpurple_T30-toasted.adpkg.zip",
                sha256 = "f65b2d3353fd4aa7190bb5426b94468e99ffea7a58a830bc0c4651db89353227",
                packageVersion = "T-30",
                mesaVersion = "26.3.0-devel",
                vulkanVersion = "1.4.359",
                minApi = 30
            )

            var successCalled = false

            // Inicia download
            downloader.downloadAndInstall(
                entry = entry,
                onSuccess = { successCalled = true }
            )

            // Cancela imediatamente
            downloader.cancelDownload(entry.id)

            // Delay para a corrotina processar
            delay(150)

            // O estado deve permanecer Available
            val state = downloader.getState(entry.id).value
            assertTrue("Estado deve ser Available apos cancelamento, obtido: $state", state is DriverDownloadState.Available)
            assertFalse("onSuccess nao deve ter sido chamado", successCalled)

            // Testa cancelAll
            downloader.downloadAndInstall(entry)
            downloader.cancelAll()
            delay(150)
            val stateAfterAll = downloader.getState(entry.id).value
            assertTrue("Estado deve ser Available apos cancelAll", stateAfterAll is DriverDownloadState.Available)
        } finally {
            tmpDir.deleteRecursively()
        }
    }

    @Test
    fun testValidateArm64ElfRealBytes() {
        val tmpDir = File(System.getProperty("java.io.tmpdir"), "elf_test_${System.currentTimeMillis()}")
        tmpDir.mkdirs()
        val elfFile = File(tmpDir, "libvulkan.so")

        try {
            // 1. Non-ELF
            elfFile.writeBytes(byteArrayOf(0, 1, 2, 3, 4, 5))
            assertTrue(DriverSecurityValidator.validateArm64Elf(elfFile).isFailure)

            // 2. 32-bit ELF (class = 1)
            val elf32 = ByteArray(20)
            elf32[0] = 0x7F; elf32[1] = 'E'.code.toByte(); elf32[2] = 'L'.code.toByte(); elf32[3] = 'F'.code.toByte()
            elf32[4] = 1 // 32-bit
            elf32[5] = 1 // Little Endian
            elfFile.writeBytes(elf32)
            assertTrue(DriverSecurityValidator.validateArm64Elf(elfFile).isFailure)

            // 3. x86_64 ELF (machine = 62)
            val elfX64 = ByteArray(20)
            elfX64[0] = 0x7F; elfX64[1] = 'E'.code.toByte(); elfX64[2] = 'L'.code.toByte(); elfX64[3] = 'F'.code.toByte()
            elfX64[4] = 2 // 64-bit
            elfX64[5] = 1 // Little Endian
            elfX64[18] = 62; elfX64[19] = 0 // x86_64
            elfFile.writeBytes(elfX64)
            assertTrue(DriverSecurityValidator.validateArm64Elf(elfFile).isFailure)

            // 4. ARM64 ELF (machine = 183)
            val elfArm64 = ByteArray(20)
            elfArm64[0] = 0x7F; elfArm64[1] = 'E'.code.toByte(); elfArm64[2] = 'L'.code.toByte(); elfArm64[3] = 'F'.code.toByte()
            elfArm64[4] = 2 // 64-bit
            elfArm64[5] = 1 // Little Endian
            elfArm64[18] = 0xB7.toByte(); elfArm64[19] = 0x00 // 183 = EM_AARCH64
            elfFile.writeBytes(elfArm64)
            assertTrue(DriverSecurityValidator.validateArm64Elf(elfFile).isSuccess)
        } finally {
            tmpDir.deleteRecursively()
        }
    }

    @Test
    fun testValidateCatalogDuplicateIdDetection() {
        val d1 = DriverCatalogEntry(
            id = "driver-1",
            name = "Driver 1",
            author = "MrPurple666",
            sourceRepository = "MrPurple666/purple-turnip",
            downloadUrl = "https://github.com/MrPurple666/purple-turnip/releases/download/v1/driver.zip",
            fileName = "driver.zip",
            sha256 = "a".repeat(64),
            packageVersion = "1.0",
            mesaVersion = "26.0",
            vulkanVersion = "1.4",
            minApi = 30
        )
        val d2 = d1.copy(name = "Driver 1 Duplicate")
        val catalogWithDupes = DriverCatalog(
            catalogVersion = 1,
            drivers = listOf(d1, d2)
        )
        val result = DriverSecurityValidator.validateCatalog(catalogWithDupes)
        assertTrue("Catalogo com ID duplicado deve falhar", result.isFailure)
        assertTrue(result.exceptionOrNull()?.message?.contains("duplicado") == true)
    }

    @Test
    fun testDriverDownloaderCancellationDuringActiveStreamingTransfer() = runBlocking {
        DriverSecurityValidator.allowLocalhostForTesting = true

        val serverSocket = ServerSocket(0, 50, InetAddress.getByName("127.0.0.1"))
        val port = serverSocket.localPort

        val transferStartedLatch = CountDownLatch(1)
        val connectionInterruptedLatch = CountDownLatch(1)
        val connectionInterrupted = AtomicBoolean(false)
        val bytesSent = AtomicLong(0L)
        val serverRunning = AtomicBoolean(true)
        val clientSocketRef = AtomicReference<Socket?>(null)

        val serverThread = Thread {
            try {
                val client = serverSocket.accept()
                clientSocketRef.set(client)

                val reader = BufferedReader(InputStreamReader(client.getInputStream()))
                // Le cabecalhos HTTP ate o fim do header
                while (true) {
                    val line = reader.readLine() ?: break
                    if (line.isEmpty()) break
                }

                val out = client.getOutputStream()
                val responseHeader = "HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: 10485760\r\nConnection: close\r\n\r\n"
                out.write(responseHeader.toByteArray(Charsets.UTF_8))
                out.flush()

                // Inicia monitor para detectar encerramento/interrupcao da conexao do lado do cliente apos o handshake
                val monitorThread = Thread {
                    try {
                        val readByte = client.getInputStream().read()
                        if (readByte == -1) {
                            connectionInterrupted.set(true)
                            connectionInterruptedLatch.countDown()
                        }
                    } catch (_: Throwable) {
                        connectionInterrupted.set(true)
                        connectionInterruptedLatch.countDown()
                    }
                }
                monitorThread.isDaemon = true
                monitorThread.start()

                // Transfere blocos controlados lentamente para manter a conexao ativa
                val chunk = ByteArray(8192) { 0xAA.toByte() }
                while (serverRunning.get()) {
                    out.write(chunk)
                    out.flush()
                    bytesSent.addAndGet(chunk.size.toLong())
                    transferStartedLatch.countDown()
                    Thread.sleep(25)
                }
            } catch (se: java.net.SocketException) {
                connectionInterrupted.set(true)
                connectionInterruptedLatch.countDown()
            } catch (ioe: java.io.IOException) {
                connectionInterrupted.set(true)
                connectionInterruptedLatch.countDown()
            }
        }
        serverThread.isDaemon = true
        serverThread.start()

        val tmpDir = File(System.getProperty("java.io.tmpdir") ?: ".", "stream_cancel_${System.currentTimeMillis()}").apply { mkdirs() }
        val driversDir = File(tmpDir, "drivers").apply { mkdirs() }
        val cacheDir = File(tmpDir, "cache").apply { mkdirs() }

        try {
            val downloader = DriverDownloader(
                context = null,
                driversDir = driversDir,
                customCacheDir = cacheDir
            )

            val driverId = "test-streaming-turnip"
            val entry = DriverCatalogEntry(
                id = driverId,
                name = "Turnip Active Streaming",
                author = "MrPurple666",
                sourceRepository = "MrPurple666/purple-turnip",
                downloadUrl = "http://127.0.0.1:$port/MrPurple666/purple-turnip/releases/download/v1.0/driver.zip",
                fileName = "driver.zip",
                sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                packageVersion = "1.0",
                mesaVersion = "26.0",
                vulkanVersion = "1.4",
                minApi = 30
            )

            val successCalled = AtomicBoolean(false)
            val errorCalled = AtomicBoolean(false)

            // Dispara download e streaming ativo
            downloader.downloadAndInstall(
                entry = entry,
                onSuccess = { successCalled.set(true) },
                onError = { errorCalled.set(true) }
            )

            // 1. Aguarda transferencia de dados estar comprovadamente ativa
            val transferStarted = transferStartedLatch.await(5, TimeUnit.SECONDS)
            assertTrue("A transferencia de streaming com o servidor HTTP local deve ter iniciado", transferStarted)
            assertTrue("O servidor deve ter transmitido dados durante o streaming ativo", bytesSent.get() > 0)

            // 2. Confirma que o job esta ativo durante o streaming
            val activeJob = downloader.getActiveJob(driverId)
            assertNotNull("O Job do download deve existir durante a transferencia", activeJob)
            assertTrue("O Job deve estar ativo (isActive) durante o streaming", activeJob!!.isActive)

            // 3. Executa o cancelamento durante o streaming ativo
            downloader.cancelDownload(driverId)

            // 4. Confirma que a conexao TCP foi interrompida
            val interrupted = connectionInterruptedLatch.await(5, TimeUnit.SECONDS)
            assertTrue("A conexao de rede deve ter sido interrompida imediatamente pelo cancelamento", interrupted)
            assertTrue("A flag connectionInterrupted deve ser true", connectionInterrupted.get())

            // 5. Confirma que o job foi cancelado e finalizado
            withTimeout(5000) {
                activeJob.join()
            }
            assertTrue("O job deve ser marcado como cancelado", activeJob.isCancelled)

            // 6. Confirma que nenhum callback de sucesso ocorreu
            assertFalse("Nenhum callback de sucesso deve ocorrer em caso de cancelamento", successCalled.get())

            // 7. Confirma que os arquivos temporarios foram removidos do disco
            val remainingTemps = cacheDir.listFiles { _, name -> name.contains(driverId) }
            assertTrue(
                "Todos os arquivos temporarios e staging devem ser removidos apos o cancelamento. Encontrados: ${remainingTemps?.map { it.name }}",
                remainingTemps.isNullOrEmpty()
            )
            assertFalse(
                "Nenhum diretorio final do driver deve ser instalado",
                File(driversDir, driverId).exists()
            )

            // 8. O estado final deve retornar a Available
            val finalState = downloader.getState(driverId).value
            assertTrue("O estado final deve ser Available, obtido: $finalState", finalState is DriverDownloadState.Available)

        } finally {
            serverRunning.set(false)
            try { clientSocketRef.get()?.close() } catch (_: Throwable) {}
            try { serverSocket.close() } catch (_: Throwable) {}
            serverThread.interrupt()
            DriverSecurityValidator.allowLocalhostForTesting = false
            tmpDir.deleteRecursively()
        }
    }
}
