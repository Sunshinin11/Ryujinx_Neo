package org.kenjinx.android

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.system.Os
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.documentfile.provider.DocumentFile
import java.util.Locale
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

// NEOV2-NCZ2: conversor NSP -> NSZ dentro do emulador.
// Gera "<jogo>.nsz.part" na mesma pasta, o C# comprime e verifica (SHA-256 de cada arquivo),
// e so entao renomeia para .nsz e exclui o NSP original. Em erro ou cancelamento, o original fica intacto.
object NszConverter {
    const val LEVEL_FAST = 8
    const val LEVEL_MAX = 18

    const val PHASE_COMPRESSING = 1
    const val PHASE_VERIFYING = 2

    val running = mutableStateOf(false)
    val permille = mutableIntStateOf(0)
    val phase = mutableIntStateOf(0)
    val gameName = mutableStateOf("")
    val optimizing = mutableStateOf(false)
    private val cancelOptimization = AtomicBoolean(false)
    val decompressing = mutableStateOf(false)
    private val cancelDecompression = AtomicBoolean(false)

    fun cancel() {
        if (running.value) {
            try {
                if (optimizing.value) {
                    cancelOptimization.set(true)
                    KenjinxNative.deviceOptimizeNszCancel()
                } else if (decompressing.value) {
                    cancelDecompression.set(true)
                    KenjinxNative.deviceDecompressCancel()
                } else KenjinxNative.deviceCompressCancel()
            } catch (_: Throwable) {
            }
        }
    }

    fun start(context: Context, source: DocumentFile, level: Int, onFinished: (Boolean, String) -> Unit) {
        val main = Handler(Looper.getMainLooper())

        if (running.value) {
            main.post { onFinished(false, "Já existe uma conversão em andamento.") }
            return
        }

        running.value = true
        decompressing.value = false
        optimizing.value = false
        permille.intValue = 0
        phase.intValue = PHASE_COMPRESSING
        gameName.value = source.name ?: ""

        thread(name = "NszConverter") {
            var part: DocumentFile? = null
            var success = false
            var message: String

            try {
                val name = source.name ?: throw IllegalStateException("Nome de arquivo inválido.")

                if (!name.lowercase(Locale.ROOT).endsWith(".nsp")) {
                    throw IllegalStateException("Somente arquivos NSP podem ser comprimidos.")
                }

                val parent = source.parentFile
                    ?: throw IllegalStateException("Não foi possível acessar a pasta do jogo.")

                val baseName = name.substring(0, name.length - 4)
                val outName = "$baseName.nsz"
                val partName = "$outName.part"

                if (parent.findFile(outName) != null) {
                    throw IllegalStateException("Já existe \"$outName\" nessa pasta.")
                }

                parent.findFile(partName)?.delete()

                part = parent.createFile("application/octet-stream", partName)
                    ?: throw IllegalStateException("Não foi possível criar o arquivo temporário.")

                val sourceSize = source.length()
                val resolver = context.contentResolver
                var result = 1

                resolver.openFileDescriptor(source.uri, "rw").use { inPfd ->
                    resolver.openFileDescriptor(part!!.uri, "rw").use { outPfd ->
                        if (inPfd == null || outPfd == null) {
                            throw IllegalStateException("Não foi possível abrir os arquivos.")
                        }

                        val stat = Os.fstatvfs(outPfd.fileDescriptor)
                        val free = stat.f_bavail * stat.f_bsize

                        if (free < sourceSize) {
                            throw IllegalStateException(
                                "Espaço insuficiente: são necessários ${sourceSize / (1024 * 1024)} MB livres, há ${free / (1024 * 1024)} MB."
                            )
                        }

                        val polling = AtomicBoolean(true)

                        val poller = thread(name = "NszConverterProgress") {
                            while (polling.get()) {
                                try {
                                    permille.intValue = KenjinxNative.deviceCompressProgress()
                                    val p = KenjinxNative.deviceCompressPhase()
                                    if (p != 0) phase.intValue = p
                                    Thread.sleep(250)
                                } catch (_: Throwable) {
                                }
                            }
                        }

                        try {
                            result = KenjinxNative.deviceCompressNsp(inPfd.fd, outPfd.fd, level)
                        } finally {
                            polling.set(false)
                            poller.join(1000)
                        }
                    }
                }

                when (result) {
                    0 -> {
                        val candidateSize = part!!.length()
                        if (candidateSize <= 0L || candidateSize >= sourceSize) {
                            throw IllegalStateException("Não houve redução de tamanho. O NSP original foi mantido.")
                        }
                        if (!part!!.renameTo(outName)) {
                            throw IllegalStateException("Não foi possível renomear o arquivo comprimido. O original foi mantido.")
                        }

                        part = null

                        val newSize = parent.findFile(outName)?.length() ?: 0L
                        val sizes = "${sourceSize / (1024 * 1024)} MB → ${newSize / (1024 * 1024)} MB"

                        message = if (source.delete()) {
                            "Jogo comprimido ($sizes). O NSP original foi excluído."
                        } else {
                            "Jogo comprimido ($sizes), mas não foi possível excluir o NSP original."
                        }

                        success = true
                    }
                    2 -> message = "Conversão cancelada. O arquivo original foi mantido."
                    3 -> message = "Não houve redução de tamanho. O NSP original foi mantido."
                    else -> message = "Falha na conversão. O arquivo original foi mantido."
                }
            } catch (t: Throwable) {
                message = t.message ?: "Falha na conversão. O arquivo original foi mantido."
            } finally {
                if (!success) {
                    try {
                        part?.delete()
                    } catch (_: Throwable) {
                    }
                }

                running.value = false
                phase.intValue = 0
            }

            main.post { onFinished(success, message) }
        }
    }

    // NSZ -> NSP and standalone NCZ -> NCA. Delete the source only after verified publication.
    fun decompress(context: Context, source: DocumentFile, folder: DocumentFile, onFinished: (Boolean, String) -> Unit) {
        val main = Handler(Looper.getMainLooper())
        if (running.value) {
            onFinished(false, "Já existe uma conversão em andamento.")
            return
        }
        running.value = true
        decompressing.value = true
        optimizing.value = false
        cancelDecompression.set(false)
        permille.intValue = 0
        phase.intValue = 3
        gameName.value = source.name.orEmpty()
        val appContext = context.applicationContext
        thread(name = "NczDecompressor") {
            var part: DocumentFile? = null
            var success = false
            var message: String
            try {
                val name = source.name ?: error("Nome de arquivo inválido.")
                val extension = name.substringAfterLast('.', "").lowercase(Locale.ROOT)
                require(extension == "nsz" || extension == "ncz") { "Selecione um arquivo NSZ ou NCZ." }
                require(folder.isDirectory && folder.canWrite()) { "A pasta de destino precisa permitir gravação." }
                val outputName = name.dropLast(4) + if (extension == "nsz") ".nsp" else ".nca"
                require(folder.findFile(outputName) == null) { "Já existe \"$outputName\" no destino. Selecione outra pasta." }
                val standalone = if (extension == "ncz") 1 else 0
                val resolver = appContext.contentResolver
                val expected = resolver.openFileDescriptor(source.uri, "r").use { input ->
                    requireNotNull(input) { "Não foi possível abrir a origem." }
                    val expected = KenjinxNative.deviceDecompressSize(input.fd, standalone)
                    require(expected > 0) { "O arquivo é inválido ou não pode ser descompactado. Consulte o log." }
                    if (cancelDecompression.get()) error("Descompactação cancelada. O original foi mantido.")
                    part = folder.createFile("application/octet-stream", "$outputName.${UUID.randomUUID()}.part")
                        ?: error("Não foi possível criar o arquivo temporário.")
                    resolver.openFileDescriptor(part!!.uri, "rw").use { output ->
                        requireNotNull(output) { "Não foi possível abrir o destino para gravação e verificação." }
                        val stat = Os.fstatvfs(output.fileDescriptor)
                        val free = Math.multiplyExact(stat.f_bavail, stat.f_bsize)
                        require(free >= expected) { "Espaço insuficiente: precisa de ${expected / (1024 * 1024)} MB, há ${free / (1024 * 1024)} MB livres." }
                        val polling = AtomicBoolean(true)
                        val poller = thread(name = "NczDecompressionProgress") {
                            while (polling.get()) {
                                try {
                                    if (cancelDecompression.get()) KenjinxNative.deviceDecompressCancel()
                                    val progress = KenjinxNative.deviceDecompressProgress()
                                    val currentPhase = KenjinxNative.deviceDecompressPhase()
                                    main.post { permille.intValue = progress; if (currentPhase != 0) phase.intValue = currentPhase }
                                } catch (_: Throwable) { }
                                // Keep polling bounded even if a stale native library lacks the exports.
                                Thread.sleep(250)
                            }
                        }
                        val result = try {
                            KenjinxNative.deviceDecompress(input.fd, output.fd, standalone, name)
                        } finally {
                            polling.set(false)
                            poller.join()
                        }
                        if (result == 2 || cancelDecompression.get()) error("Descompactação cancelada. O original foi mantido.")
                        require(result == 0) { "Falha na descompactação ou na verificação. O original foi mantido." }
                        Os.fsync(output.fileDescriptor)
                    }
                    expected
                }
                // Close both descriptors before publication/deletion, including on restrictive providers.
                require(part!!.length() == expected) { "O tamanho do destino não corresponde ao esperado." }
                if (cancelDecompression.get()) error("Descompactação cancelada. O original foi mantido.")
                // Recheck after a long conversion; never intentionally replace an existing destination.
                require(folder.findFile(outputName) == null) { "O destino apareceu durante a conversão. O original foi mantido." }
                require(part!!.renameTo(outputName)) { "Não foi possível publicar o arquivo. O original foi mantido." }
                val published = folder.findFile(outputName)
                require(published != null && published.length() == expected && part!!.name == outputName) { "O provedor não confirmou o nome/tamanho do arquivo final." }
                part = null
                if (cancelDecompression.get()) error("Descompactação cancelada. O original foi mantido; $outputName também está disponível e verificado.")
                val removed = runCatching { source.delete() }.getOrDefault(false)
                success = true
                message = if (removed) {
                    "Descompactado para $outputName. O arquivo compactado original foi excluído."
                } else {
                    "Descompactado para $outputName, mas não foi possível excluir o original. Os dois arquivos foram mantidos."
                }
            } catch (exception: Throwable) {
                message = exception.message ?: "Falha na descompactação. O original foi mantido."
            } finally {
                if (!success) runCatching { part?.delete() }
            }
            main.post {
                running.value = false
                decompressing.value = false
                phase.intValue = 0
                onFinished(success, message)
            }
        }
    }

    // Called on an IO dispatcher; metadata only, no playback cache or source modification.
    fun inspectBlocks(context: Context, source: DocumentFile): Long = try {
        context.contentResolver.openFileDescriptor(source.uri, "r").use { input ->
            if (input == null) -1L else KenjinxNative.deviceOptimizeNszSize(input.fd)
        }
    } catch (_: Throwable) { -1L }

    fun optimizeBlocks(context: Context, source: DocumentFile, onFinished: (Boolean, String) -> Unit) {
        val main = Handler(Looper.getMainLooper())
        if (running.value) {
            main.post { onFinished(false, "Já existe uma conversão em andamento.") }
            return
        }
        running.value = true
        optimizing.value = true
        decompressing.value = false
        cancelOptimization.set(false)
        permille.intValue = 0
        phase.intValue = PHASE_COMPRESSING
        gameName.value = source.name.orEmpty()
        val appContext = context.applicationContext
        thread(name = "NszBlockOptimizer") {
            var part: DocumentFile? = null
            var success = false
            var message: String
            try {
                val name = source.name ?: error("Nome de arquivo inválido.")
                require(name.lowercase(Locale.ROOT).endsWith(".nsz")) { "Selecione um arquivo NSZ." }
                val folder = source.parentFile ?: error("Não foi possível acessar a pasta do jogo.")
                require(folder.isDirectory && folder.canWrite()) { "A pasta do jogo precisa permitir gravação." }
                val outputName = name.dropLast(4) + ".blocks.nsz"
                require(folder.findFile(outputName) == null) { "Já existe \"$outputName\" nessa pasta. O original foi mantido." }
                val resolver = appContext.contentResolver
                var required = 0L
                val originalSize = source.length()
                resolver.openFileDescriptor(source.uri, "r").use { input ->
                    requireNotNull(input) { "Não foi possível abrir a origem." }
                    required = KenjinxNative.deviceOptimizeNszSize(input.fd)
                    require(required != 0L) { "Este arquivo já está otimizado. O original foi mantido." }
                    require(required > 0L) { "O NSZ é inválido ou não pode ser otimizado. Consulte o log." }
                    if (cancelOptimization.get()) error("Otimização cancelada. O original foi mantido.")
                    part = folder.createFile("application/octet-stream", "$outputName.${UUID.randomUUID()}.part")
                        ?: error("Não foi possível criar o arquivo temporário.")
                    resolver.openFileDescriptor(part!!.uri, "rw").use { output ->
                        requireNotNull(output) { "Não foi possível abrir o destino para gravação e verificação." }
                        val stat = Os.fstatvfs(output.fileDescriptor)
                        val free = Math.multiplyExact(stat.f_bavail, stat.f_bsize)
                        require(free >= required) { "Espaço insuficiente: precisa de até ${required / (1024 * 1024)} MB livres, há ${free / (1024 * 1024)} MB." }
                        val polling = AtomicBoolean(true)
                        val poller = thread(name = "NszBlockOptimizationProgress") {
                            while (polling.get()) {
                                try {
                                    if (cancelOptimization.get()) KenjinxNative.deviceOptimizeNszCancel()
                                    val progress = KenjinxNative.deviceOptimizeNszProgress()
                                    val currentPhase = KenjinxNative.deviceOptimizeNszPhase()
                                    main.post { permille.intValue = progress; if (currentPhase != 0) phase.intValue = currentPhase }
                                } catch (_: Throwable) { }
                                Thread.sleep(250)
                            }
                        }
                        val result = try { KenjinxNative.deviceOptimizeNsz(input.fd, output.fd) }
                            finally { polling.set(false); poller.join() }
                        if (result == 2 || cancelOptimization.get()) error("Otimização cancelada. O original foi mantido.")
                        require(result == 0) { "Falha na otimização ou na verificação. O original foi mantido." }
                        Os.fsync(output.fileDescriptor)
                    }
                }
                val newSize = part!!.length()
                require(newSize > 0L && newSize <= required) { "O tamanho do destino é inválido. O original foi mantido." }
                if (cancelOptimization.get()) error("Otimização cancelada. O original foi mantido.")
                require(folder.findFile(outputName) == null) { "O destino apareceu durante a conversão. O original foi mantido." }
                require(part!!.renameTo(outputName)) { "Não foi possível publicar o arquivo. O original foi mantido." }
                val published = folder.findFile(outputName)
                require(published != null && published.length() == newSize && part!!.name == outputName) { "O provedor não confirmou o arquivo final. O original foi mantido." }
                part = null
                if (cancelOptimization.get()) error("Otimização cancelada. O original foi mantido; $outputName também está disponível e verificado.")
                val removed = runCatching { source.delete() }.getOrDefault(false)
                val sizes = "${originalSize / (1024 * 1024)} MB → ${newSize / (1024 * 1024)} MB"
                success = true
                message = if (removed) "NSZ otimizado para blocos ($sizes). O original foi excluído."
                    else "NSZ otimizado para blocos ($sizes), mas não foi possível excluir o original. Os dois arquivos foram mantidos."
            } catch (exception: Throwable) {
                message = exception.message ?: "Falha na otimização. O original foi mantido."
            } finally {
                if (!success) runCatching { part?.delete() }
            }
            main.post {
                running.value = false
                optimizing.value = false
                phase.intValue = 0
                onFinished(success, message)
            }
        }
    }
}
