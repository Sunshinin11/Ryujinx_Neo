package org.kenjinx.android.viewmodels

import org.kenjinx.android.driver.DriverPreparationManager

import android.annotation.SuppressLint
import android.net.Uri
import android.os.SystemClock
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableStateOf
import androidx.navigation.NavHostController
import androidx.preference.PreferenceManager
import com.anggrayudi.storage.extension.launchOnUiThread
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.sync.Semaphore
import org.kenjinx.android.IGameController
import org.kenjinx.android.GameHost
import org.kenjinx.android.Logging
import org.kenjinx.android.MainActivity
import org.kenjinx.android.MotionSensorManager
import org.kenjinx.android.NativeGraphicsInterop
import org.kenjinx.android.NativeHelpers
import org.kenjinx.android.PerformanceManager
import org.kenjinx.android.PhysicalControllerManager
import org.kenjinx.android.RegionCode
import org.kenjinx.android.KenjinxNative
import org.kenjinx.android.PerformanceMonitor
import org.kenjinx.android.SystemLanguage
import org.kenjinx.android.UiHandler
import android.util.Log
import java.io.File
import java.util.TimeZone
import androidx.core.content.edit
import androidx.core.net.toUri

@SuppressLint("WrongConstant")
class MainViewModel(val activity: MainActivity) {
    var physicalControllerManager: PhysicalControllerManager? = null
    var motionSensorManager: MotionSensorManager? = null
    var gameModel: GameModel? = null
    var controller: IGameController? = null
    var performanceManager: PerformanceManager? = null
    var selected: GameModel? = null
    val loadGameModel: MutableState<GameModel?> = mutableStateOf(null)
    val bootPath: MutableState<String?> = mutableStateOf(null)
    val forceNceAndPptc: MutableState<Boolean> = mutableStateOf(false)
    var isMiiEditorLaunched = false
    val userViewModel = UserViewModel()
    val logging = Logging(this)
    var firmwareVersion = ""
    private var gameTimeState: MutableState<Double>? = null
    private var gameFpsState: MutableState<Double>? = null
    private var fifoState: MutableState<Double>? = null
    private var usedMemState: MutableState<Int>? = null
    private var totalMemState: MutableState<Int>? = null
    private var progress: MutableState<String>? = null
    private var progressValue: MutableState<Float>? = null
    private var showLoading: MutableState<Boolean>? = null
    private var refreshUser: MutableState<Boolean>? = null
    @Volatile var rendererReady: Boolean = false
    @Volatile var emulationRunning: Boolean = false
    @Volatile var emulationClosing: Boolean = false
    val gameExitRequested: MutableState<Boolean> = mutableStateOf(false)
    private var gameExitGeneration = 0L

    // Default Game Folder
    var defaultGameFolderUri: Uri? = null
        set(value) {
            field = value
            val prefs = PreferenceManager.getDefaultSharedPreferences(activity)
            prefs.edit { putString("defaultGameFolderUri", value?.toString() ?: "") }
        }
    var gameHost: GameHost? = null
        set(value) {
            field = value
            field?.setProgressStates(showLoading, progressValue, progress)
        }
    var navController: NavHostController? = null

    var homeViewModel: HomeViewModel = HomeViewModel(activity, this)

    init {
        performanceManager = PerformanceManager(activity)

        val prefs = PreferenceManager.getDefaultSharedPreferences(activity)
        val saved = prefs.getString("defaultGameFolderUri", "") ?: ""
        if (saved.isNotEmpty()) {
            defaultGameFolderUri = saved.toUri()
        }
    }

    fun refreshFirmwareVersion() {
        firmwareVersion = KenjinxNative.deviceGetInstalledFirmwareVersion()
    }

    // UI-thread entry shared by the dialog and the hotkey. The claim lasts until the next game screen.
    fun requestGameExit(): Boolean {
        val nav = navController ?: return false
        if (nav.currentDestination?.route != "game" || gameExitRequested.value) return false

        gameExitRequested.value = true
        val generation = gameExitGeneration
        // Let Compose remove the exit controls before closeGame's existing joins can block the UI.
        // Allow an intervening frame without moving teardown to a new thread.
        activity.window.decorView.postOnAnimation {
            activity.window.decorView.postOnAnimation finishExit@{
                if (generation != gameExitGeneration || nav.currentDestination?.route != "game") return@finishExit
                try {
                    closeGame()
                    // NEOV2-CLOSE2 remains: never pop the library from a repeated/stale exit.
                    if (nav.currentDestination?.route == "game") nav.popBackStack()
                    activity.isGameRunning = false
                } catch (error: Exception) {
                    Log.e("MainViewModel", "Game exit failed; duplicate exit remains blocked.", error)
                }
            }
        }
        return true
    }

    fun closeGame() {
        // NEOV2-CLOSE1: ignora um segundo pedido enquanto o fechamento anterior ainda esta em andamento.
        if (emulationClosing && emulationRunning) return

        emulationClosing = true
        KenjinxNative.deviceSignalEmulationClose()
        gameHost?.close()
        motionSensorManager?.unregister()
        physicalControllerManager?.disconnect()
        motionSensorManager?.setControllerId(-1)
        rendererReady = false
    }

    internal fun onEmulationStarted() {
        emulationRunning = true
    }

    internal fun onEmulationStopped() {
        emulationRunning = false
        emulationClosing = false
    }

    private fun waitForEmulationStop(timeoutMs: Long): Boolean {
        if (!emulationRunning) {
            emulationClosing = false
            return true
        }

        val deadline = SystemClock.uptimeMillis() + timeoutMs
        while (emulationRunning && SystemClock.uptimeMillis() < deadline) {
            try { Thread.sleep(50) } catch (_: Throwable) {}
        }

        if (emulationRunning) {
            Log.w("MainViewModel", "Emulation still running after timeout; forcing close.")
            try { KenjinxNative.deviceCloseEmulation() } catch (_: Throwable) {}
            emulationRunning = false
            emulationClosing = false
            return true
        }

        emulationClosing = false
        return true
    }

    // ---- Load language/region from Preferences (Defaults: AmericanEnglish/USA) ----
    private fun loadSystemLanguage(): SystemLanguage {
        val prefs = PreferenceManager.getDefaultSharedPreferences(activity)
        val stored = prefs.getString("system_language", "AmericanEnglish") ?: "AmericanEnglish"
        val global = runCatching { SystemLanguage.valueOf(stored) }.getOrElse { SystemLanguage.AmericanEnglish }
        val titleId = gameModel?.titleId.orEmpty()
        return if (titleId.isBlank()) global else GameSettingsStore(activity, titleId).resolveLanguage(global)
    }

    private fun loadRegionCode(): RegionCode {
        val prefs = PreferenceManager.getDefaultSharedPreferences(activity)
        val stored = prefs.getString("region_code", "USA") ?: "USA"
        return runCatching { RegionCode.valueOf(stored) }.getOrElse { RegionCode.USA }
    }

    fun getSettingsForGame(game: GameModel? = gameModel): QuickSettings {
        val settings = QuickSettings(activity)
        val titleId = game?.titleId.orEmpty()
        if (titleId.isNotBlank()) {
            GameSettingsStore(activity, titleId).applyTo(settings)
        }
        return settings
    }

    fun updateActiveGameSettings(update: (QuickSettings) -> Unit) {
        val settings = getSettingsForGame()
        update(settings)
        val titleId = gameModel?.titleId.orEmpty()
        if (titleId.isNotBlank()) {
            val store = GameSettingsStore(activity, titleId)
            if (store.isEnabled) {
                store.save(settings)
                return
            }
        }
        settings.save()
    }
    // -------------------------------------------------------------------------------

    fun loadGame(game: GameModel, overrideSettings: Boolean? = false, forceNceAndPptc: Boolean? = false): Int {
        if (emulationClosing || emulationRunning) {
            if (!waitForEmulationStop(5000)) {
                return 0
            }
        }

        // Opcoes por jogo "Compatibilidade grafica 2D" (MARVEL Cosmic Invasion) e "Modo sem internet" (DOOM 2016).
        val experimentalStore = GameSettingsStore(activity, game.titleId.orEmpty())
        KenjinxNative.deviceSetExperimentalGameFixes(experimentalStore.isEnabled && experimentalStore.experimentalGameFixesEnabled)
        KenjinxNative.deviceSetNifmOffline(experimentalStore.isEnabled && experimentalStore.nifmOfflineEnabled)
        // Opcao por jogo "Termos offline" (BioShock).
        KenjinxNative.deviceSetWebOfflineTerms(experimentalStore.isEnabled && experimentalStore.webOfflineTermsEnabled)

        KenjinxNative.deviceReinitEmulation()
        MainActivity.mainViewModel?.activity?.uiHandler = UiHandler()

        val descriptor = game.open()

        if (descriptor == 0)
            return 0

        val update = game.openUpdate()

        if(update == -2)
        {
            return -2
        }

        gameModel = game
        isMiiEditorLaunched = false

        val settings = getSettingsForGame(game)

        if(overrideSettings == true)
        {
            settings.overrideSettings(forceNceAndPptc)
        }

        // 0=Auto, 1=SingleThread, 2=Threaded
        val backendMode = if (settings.disableThreadedRendering) 1 else 2

        // NEOV2 AUTOFLUSH1: valor efetivo ja resolvido por getSettingsForGame (global + override).
        // Global e override crus vao junto apenas para a linha de log [NEOV2-DRAW-FLUSH].
        val drawFlushGlobal = QuickSettings(activity).drawFlushMode
        val drawFlushStore = GameSettingsStore(activity, game.titleId.orEmpty())
        val drawFlushOverride =
            if (drawFlushStore.isEnabled && drawFlushStore.drawFlushMode > 0) {
                drawFlushStore.drawFlushMode - 1
            } else {
                -1
            }

        // NEOV2 BUFFER-READBACK1: mesma resolucao, global cru e override cru apenas para o log.
        val strictSyncGlobal = QuickSettings(activity).strictSyncMode
        val strictSyncOverride =
            if (drawFlushStore.isEnabled && drawFlushStore.strictSyncMode > 0) {
                drawFlushStore.strictSyncMode - 1
            } else {
                -1
            }

        val bufferReadbackGlobal = QuickSettings(activity).bufferReadbackMode
        val bufferReadbackOverride =
            if (drawFlushStore.isEnabled && drawFlushStore.bufferReadbackMode > 0) {
                drawFlushStore.bufferReadbackMode - 1
            } else {
                -1
            }

        // NEOV2 NCE-PROT1: valor efetivo ja resolvido por getSettingsForGame (global + override por jogo).
        try {
            KenjinxNative.deviceSetNceTrackingProtection(settings.enableNceTrackingProtection)
        } catch (_: Throwable) {
        }

        // NEOV2 DMA-RELAX1: valor efetivo (global + override por jogo).
        try {
            KenjinxNative.deviceSetRelaxedDmaCopy(settings.enableRelaxedDmaCopy)
        } catch (_: Throwable) {
        }
        // NEOV2-GPUMEM1: memoria de GPU estilo Eden, valor efetivo (global + override por jogo).
        try {
            KenjinxNative.deviceSetEdenGpuMemory(settings.enableEdenGpuMemory)
        } catch (_: Throwable) {
        }
        // NEOV2-VP9EDEN1: VP9 pelo FFmpeg (chave geral).
        try {
            KenjinxNative.deviceSetVp9FFmpeg(settings.enableVp9FFmpeg)
        } catch (_: Throwable) {
        }
        // NEOV2-AUDIO1: amplificacao do audio (100% a 200%).
        try {
            KenjinxNative.deviceSetAudioBoost(settings.audioBoost)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetRejectUnmappedDescriptors(settings.enableRejectUnmappedDescriptors)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetAsyncLarge3DTextureUpload(settings.enableAsyncLarge3DUpload)
        } catch (_: Throwable) {
        }
        // Opcao "Otimizacoes de GPU", antes de criar o renderer. TEXBUF-ALIGN1 e correcao e fica sempre ligado.
        try {
            KenjinxNative.deviceSetTurnipFence(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetConditionalRenderNoWait(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetCommandFetchNoFlush(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetTexelBufferExactOffset(true)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetQueueHysteresis(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetTimelineLazy(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetStagingBufferLock(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetTexturePoolKeepInvalid(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }

        var success = KenjinxNative.graphicsInitialize(
            enableMacroHLE = settings.enableMacroHLE,
            enableShaderCache = settings.enableShaderCache,
            enableShaderLoopSafety = settings.enableShaderLoopSafety,
            enableTextureRecompression = settings.enableTextureRecompression,
            enableVtgViaCompute = settings.enableVtgViaCompute,
            drawFlushMode = settings.drawFlushMode,
            drawFlushGlobalMode = drawFlushGlobal,
            drawFlushGameOverride = drawFlushOverride,
            bufferReadbackMode = settings.bufferReadbackMode,
            bufferReadbackGlobalMode = bufferReadbackGlobal,
            bufferReadbackGameOverride = bufferReadbackOverride,
            strictSyncMode = settings.strictSyncMode,
            strictSyncGlobalMode = strictSyncGlobal,
            strictSyncGameOverride = strictSyncOverride,
            rescale = settings.resScale,
            maxAnisotropy = settings.maxAnisotropy,
            backendThreading = backendMode
        )

        if (!success)
            return 0

        val nativeHelpers = NativeHelpers.instance
        val nativeInterop = NativeGraphicsInterop()

        nativeInterop.VkRequiredExtensions = arrayOf("VK_KHR_surface", "VK_KHR_android_surface")
        nativeInterop.VkCreateSurface = nativeHelpers.getCreateSurfacePtr()
        nativeInterop.SurfaceHandle = 0

        val driverViewModel = VulkanDriverViewModel(activity)
        val drivers = driverViewModel.getAvailableDrivers()
        val gameDriver = GameSettingsStore(activity, game.titleId.orEmpty())
        val selectedDriverPath = when {
            // Per game "Driver do sistema": an empty path loads the system Vulkan driver.
            gameDriver.isEnabled &&
                gameDriver.driverOverride == GameSettingsStore.SYSTEM_DRIVER -> ""

            gameDriver.isEnabled &&
                gameDriver.driverOverride.isNotEmpty() &&
                drivers.any { it.driverPath == gameDriver.driverOverride } -> gameDriver.driverOverride

            else -> driverViewModel.selected
        }

        val driverLoadResult = DriverPreparationManager.prepareAndLoadDriver(
            activity,
            selectedDriverPath,
            drivers
        )
        val driverHandle = driverLoadResult.handle

        val extensions = nativeInterop.VkRequiredExtensions

        success = KenjinxNative.graphicsInitializeRenderer(
            extensions!!,
            extensions.size,
            driverHandle
        )
        rendererReady = success
        if (!success)
            return 0

        val semaphore = Semaphore(1, 0)
        runBlocking {
            semaphore.acquire()
            launchOnUiThread {
                // We are only able to initialize the emulation context on the main thread
                val tzId = TimeZone.getDefault().id
                success = KenjinxNative.deviceInitialize(
                    settings.memoryManagerMode.ordinal,
                    settings.useNce,
                    settings.memoryConfiguration.ordinal,
                    if (settings.memorySlackEnabled) 12 else 0,
                    /* OLD: was fixed -> SystemLanguage.AmericanEnglish.ordinal */
                    loadSystemLanguage().ordinal,
                    /* OLD: was fixed -> RegionCode.USA.ordinal */
                    loadRegionCode().ordinal,
                    settings.vSyncMode.ordinal,
                    settings.enableDocked,
                    settings.enablePptc,
                    settings.enableLowPowerPptc,
                    settings.enableJitCacheEviction,
                    false,
                    settings.enableFsIntegrityChecks,
                    settings.fsGlobalAccessLogMode,
                    tzId, // <<< Pass through Android device time zone
                    settings.ignoreMissingServices
                )

                semaphore.release()
            }
            semaphore.acquire()
            semaphore.release()
        }

        if (!success)
            return 0

        success = KenjinxNative.deviceLoadDescriptor(descriptor, game.type.ordinal, update)

        return if (success) 1 else 0
    }

    fun loadMiiEditor(): Boolean {
        gameModel = null
        isMiiEditorLaunched = true

        val settings = QuickSettings(activity)

        KenjinxNative.deviceSetExperimentalGameFixes(false)
        KenjinxNative.deviceSetNifmOffline(false)

        // NEOV2 NCE-PROT1: sem perfil de jogo, vale a configuracao global.
        try {
            KenjinxNative.deviceSetNceTrackingProtection(settings.enableNceTrackingProtection)
        } catch (_: Throwable) {
        }

        // NEOV2 DMA-RELAX1: sem perfil de jogo, vale a configuracao global.
        try {
            KenjinxNative.deviceSetRelaxedDmaCopy(settings.enableRelaxedDmaCopy)
        } catch (_: Throwable) {
        }
        // NEOV2-GPUMEM1: sem perfil de jogo, vale a configuracao global.
        try {
            KenjinxNative.deviceSetEdenGpuMemory(settings.enableEdenGpuMemory)
        } catch (_: Throwable) {
        }
        // NEOV2-VP9EDEN1: VP9 pelo FFmpeg (chave geral).
        try {
            KenjinxNative.deviceSetVp9FFmpeg(settings.enableVp9FFmpeg)
        } catch (_: Throwable) {
        }
        // NEOV2-AUDIO1: amplificacao do audio (100% a 200%).
        try {
            KenjinxNative.deviceSetAudioBoost(settings.audioBoost)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetRejectUnmappedDescriptors(settings.enableRejectUnmappedDescriptors)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetAsyncLarge3DTextureUpload(settings.enableAsyncLarge3DUpload)
        } catch (_: Throwable) {
        }
        // Opcao "Otimizacoes de GPU", antes de criar o renderer. TEXBUF-ALIGN1 e correcao e fica sempre ligado.
        try {
            KenjinxNative.deviceSetTurnipFence(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetConditionalRenderNoWait(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetCommandFetchNoFlush(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetTexelBufferExactOffset(true)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetQueueHysteresis(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetTimelineLazy(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetStagingBufferLock(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }
        try {
            KenjinxNative.deviceSetTexturePoolKeepInvalid(settings.enableGpuOptimizations)
        } catch (_: Throwable) {
        }

        // 0=Auto, 1=SingleThread, 2=Threaded
        val backendMode = if (settings.disableThreadedRendering) 1 else 2

        var success = KenjinxNative.graphicsInitialize(
            enableMacroHLE = settings.enableMacroHLE,
            enableShaderCache = settings.enableShaderCache,
            enableShaderLoopSafety = settings.enableShaderLoopSafety,
            enableTextureRecompression = settings.enableTextureRecompression,
            enableVtgViaCompute = settings.enableVtgViaCompute,
            drawFlushMode = settings.drawFlushMode,
            drawFlushGlobalMode = settings.drawFlushMode,
            drawFlushGameOverride = -1,
            bufferReadbackMode = settings.bufferReadbackMode,
            bufferReadbackGlobalMode = settings.bufferReadbackMode,
            bufferReadbackGameOverride = -1,
            strictSyncMode = settings.strictSyncMode,
            strictSyncGlobalMode = settings.strictSyncMode,
            strictSyncGameOverride = -1,
            rescale = settings.resScale,
            maxAnisotropy = settings.maxAnisotropy,
            backendThreading = backendMode
        )

        if (!success)
            return false

        val nativeHelpers = NativeHelpers.instance
        val nativeInterop = NativeGraphicsInterop()

        nativeInterop.VkRequiredExtensions = arrayOf("VK_KHR_surface", "VK_KHR_android_surface")
        nativeInterop.VkCreateSurface = nativeHelpers.getCreateSurfacePtr()
        nativeInterop.SurfaceHandle = 0

        val driverViewModel = VulkanDriverViewModel(activity)
        val drivers = driverViewModel.getAvailableDrivers()

        val driverLoadResult = DriverPreparationManager.prepareAndLoadDriver(
            activity,
            driverViewModel.selected,
            drivers
        )
        val driverHandle = driverLoadResult.handle

        val extensions = nativeInterop.VkRequiredExtensions

        success = KenjinxNative.graphicsInitializeRenderer(
            extensions!!,
            extensions.size,
            driverHandle
        )
        rendererReady = success
        if (!success)
            return false

        val semaphore = Semaphore(1, 0)
        runBlocking {
            semaphore.acquire()
            launchOnUiThread {
                // We are only able to initialize the emulation context on the main thread
                val tzId = TimeZone.getDefault().id
                success = KenjinxNative.deviceInitialize(
                    settings.memoryManagerMode.ordinal,
                    settings.useNce,
                    settings.memoryConfiguration.ordinal,
                    if (settings.memorySlackEnabled) 12 else 0,
                    /* OLD: was fixed -> SystemLanguage.AmericanEnglish.ordinal */
                    loadSystemLanguage().ordinal,
                    /* OLD: was fixed -> RegionCode.USA.ordinal */
                    loadRegionCode().ordinal,
                    settings.vSyncMode.ordinal,
                    settings.enableDocked,
                    settings.enablePptc,
                    settings.enableLowPowerPptc,
                    settings.enableJitCacheEviction,
                    false,
                    settings.enableFsIntegrityChecks,
                    settings.fsGlobalAccessLogMode,
                    tzId, // <<< Pass through Android device time zone
                    settings.ignoreMissingServices
                )

                semaphore.release()
            }
            semaphore.acquire()
            semaphore.release()
        }

        if (!success)
            return false

        success = KenjinxNative.deviceLaunchMiiEditor()

        return success
    }

    fun clearPptcCache(titleId: String) {
        if (titleId.isNotEmpty()) {
            val basePath = MainActivity.AppPath + "/games/$titleId/cache/cpu"
            if (File(basePath).exists()) {
                var caches = mutableListOf<String>()

                val mainCache = basePath + "${File.separator}0"
                File(mainCache).listFiles()?.forEach {
                    if (it.isFile && it.name.endsWith(".cache"))
                        caches.add(it.absolutePath)
                }
                val backupCache = basePath + "${File.separator}1"
                File(backupCache).listFiles()?.forEach {
                    if (it.isFile && it.name.endsWith(".cache"))
                        caches.add(it.absolutePath)
                }
                for (path in caches)
                    File(path).delete()
            }
        }
    }

    fun purgeShaderCache(titleId: String) {
        if (titleId.isNotEmpty()) {
            val basePath = MainActivity.AppPath + "/games/$titleId/cache/shader"
        }
    }

    fun deleteCache(titleId: String) {
        fun deleteDirectory(directory: File) {
            if (directory.exists() && directory.isDirectory) {
                directory.listFiles()?.forEach { file ->
                    if (file.isDirectory) {
                        deleteDirectory(file)
                    } else {
                        file.delete()
                    }
                }
                directory.delete()
            }
        }
        if (titleId.isNotEmpty()) {
            val basePath = MainActivity.AppPath + "/games/$titleId/cache"
            if (File(basePath).exists()) {
                deleteDirectory(File(basePath))
            }
        }
    }

    fun setStatStates(
        fifo: MutableState<Double>,
        gameFps: MutableState<Double>,
        gameTime: MutableState<Double>,
        usedMem: MutableState<Int>,
        totalMem: MutableState<Int>
    ) {
        fifoState = fifo
        gameFpsState = gameFps
        gameTimeState = gameTime
        usedMemState = usedMem
        totalMemState = totalMem
    }

    fun updateStats(
        fifo: Double,
        gameFps: Double,
        gameTime: Double
    ) {
        fifoState?.apply {
            this.value = fifo
        }
        gameFpsState?.apply {
            this.value = gameFps
        }
        gameTimeState?.apply {
            this.value = gameTime
        }
        usedMemState?.let { usedMem ->
            totalMemState?.let { totalMem ->
                PerformanceMonitor.getMemoryUsage(
                    usedMem,
                    totalMem
                )
            }
        }
    }

    fun setGameController(controller: IGameController) {
        this.controller = controller
    }

    fun navigateToGame() {
        gameExitGeneration++
        gameExitRequested.value = false
        navController?.navigate("game")
        activity.isGameRunning = true
        if (getSettingsForGame().enableMotion)
            motionSensorManager?.register()
    }

    fun setProgressStates(
        showLoading: MutableState<Boolean>,
        progressValue: MutableState<Float>,
        progress: MutableState<String>
    ) {
        this.showLoading = showLoading
        this.progressValue = progressValue
        this.progress = progress
        gameHost?.setProgressStates(showLoading, progressValue, progress)
    }
}
