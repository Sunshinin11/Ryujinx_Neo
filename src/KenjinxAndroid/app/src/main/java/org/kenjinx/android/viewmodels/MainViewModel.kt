package org.kenjinx.android.viewmodels

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
    private var frequenciesState: MutableList<Double>? = null
    private var progress: MutableState<String>? = null
    private var progressValue: MutableState<Float>? = null
    private var showLoading: MutableState<Boolean>? = null
    private var refreshUser: MutableState<Boolean>? = null
    @Volatile var rendererReady: Boolean = false
    @Volatile var emulationRunning: Boolean = false
    @Volatile var emulationClosing: Boolean = false

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

    fun closeGame() {
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
        return runCatching { SystemLanguage.valueOf(stored) }.getOrElse { SystemLanguage.AmericanEnglish }
    }

    private fun loadRegionCode(): RegionCode {
        val prefs = PreferenceManager.getDefaultSharedPreferences(activity)
        val stored = prefs.getString("region_code", "USA") ?: "USA"
        return runCatching { RegionCode.valueOf(stored) }.getOrElse { RegionCode.USA }
    }
    // -------------------------------------------------------------------------------

    fun loadGame(game: GameModel, overrideSettings: Boolean? = false, forceNceAndPptc: Boolean? = false): Int {
        if (emulationClosing || emulationRunning) {
            if (!waitForEmulationStop(5000)) {
                return 0
            }
        }

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

        val settings = QuickSettings(activity)

        if(overrideSettings == true)
        {
            settings.overrideSettings(forceNceAndPptc)
        }

        // 0=Auto, 1=SingleThread, 2=Threaded
        val backendMode = if (settings.disableThreadedRendering) 1 else 2

        var success = KenjinxNative.graphicsInitialize(
            enableMacroHLE = settings.enableMacroHLE,
            enableShaderCache = settings.enableShaderCache,
            enableTextureRecompression = settings.enableTextureRecompression,
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
        var driverHandle = 0L

        if (driverViewModel.selected.isNotEmpty()) {
            val metaData = drivers.find { it.driverPath == driverViewModel.selected }

            metaData?.apply {
                val privatePath = activity.filesDir
                val privateDriverPath = privatePath.canonicalPath + "/driver/"
                val pD = File(privateDriverPath)
                if (pD.exists())
                    pD.deleteRecursively()

                pD.mkdirs()

                val driver = File(driverViewModel.selected)
                val parent = driver.parentFile
                if (parent != null) {
                    for (file in parent.walkTopDown()) {
                        if (file.absolutePath == parent.absolutePath)
                            continue
                        file.copyTo(File(privateDriverPath + file.name), true)
                    }
                }

                driverHandle = NativeHelpers.instance.loadDriver(
                    activity.applicationInfo.nativeLibraryDir!! + "/",
                    privateDriverPath,
                    this.libraryName
                )
            }

        }

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
                    settings.memorySlack.gigabytes,
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

        // 0=Auto, 1=SingleThread, 2=Threaded
        val backendMode = if (settings.disableThreadedRendering) 1 else 2

        var success = KenjinxNative.graphicsInitialize(
            enableMacroHLE = settings.enableMacroHLE,
            enableShaderCache = settings.enableShaderCache,
            enableTextureRecompression = settings.enableTextureRecompression,
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

        var driverHandle = 0L

        if (driverViewModel.selected.isNotEmpty()) {
            val metaData = drivers.find { it.driverPath == driverViewModel.selected }

            metaData?.apply {
                val privatePath = activity.filesDir
                val privateDriverPath = privatePath.canonicalPath + "/driver/"
                val pD = File(privateDriverPath)
                if (pD.exists())
                    pD.deleteRecursively()

                pD.mkdirs()

                val driver = File(driverViewModel.selected)
                val parent = driver.parentFile
                if (parent != null) {
                    for (file in parent.walkTopDown()) {
                        if (file.absolutePath == parent.absolutePath)
                            continue
                        file.copyTo(File(privateDriverPath + file.name), true)
                    }
                }

                driverHandle = NativeHelpers.instance.loadDriver(
                    activity.applicationInfo.nativeLibraryDir!! + "/",
                    privateDriverPath,
                    this.libraryName
                )
            }

        }

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
                    settings.memorySlack.gigabytes,
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
        totalMem: MutableState<Int>,
        frequencies: MutableList<Double>
    ) {
        fifoState = fifo
        gameFpsState = gameFps
        gameTimeState = gameTime
        usedMemState = usedMem
        totalMemState = totalMem
        frequenciesState = frequencies
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
        frequenciesState?.let { PerformanceMonitor.getFrequencies(it) }
    }

    fun setGameController(controller: IGameController) {
        this.controller = controller
    }

    fun navigateToGame() {
        navController?.navigate("game")
        activity.isGameRunning = true
        if (QuickSettings(activity).enableMotion)
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
