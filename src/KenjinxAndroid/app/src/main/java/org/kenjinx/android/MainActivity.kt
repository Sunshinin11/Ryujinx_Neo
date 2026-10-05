package org.kenjinx.android

import android.annotation.SuppressLint
import android.os.Bundle
import android.os.Environment
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.WindowManager
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.util.Log
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.ui.Modifier
import androidx.core.net.toUri
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.documentfile.provider.DocumentFile
import com.anggrayudi.storage.SimpleStorageHelper
import com.sun.jna.JNIEnv
import org.kenjinx.android.ui.theme.KenjinxAndroidTheme
import org.kenjinx.android.ui.theme.NeoThemeState
import org.kenjinx.android.viewmodels.MainViewModel
import org.kenjinx.android.viewmodels.QuickSettings
import org.kenjinx.android.viewmodels.GameModel
import org.kenjinx.android.views.MainView
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.hardware.display.DisplayManager
import android.net.Uri
import android.view.Surface
import androidx.preference.PreferenceManager
import java.io.File
import androidx.activity.result.contract.ActivityResultContracts
import android.Manifest
import android.content.pm.PackageManager
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import android.content.BroadcastReceiver
import android.content.IntentFilter
import android.content.Context
import android.os.Build
import androidx.annotation.RequiresApi
import org.kenjinx.android.service.EmulationService
import androidx.core.content.edit

class MainActivity : BaseActivity() {
    private var physicalControllerManager: PhysicalControllerManager =
        PhysicalControllerManager(this)
    private lateinit var motionSensorManager: MotionSensorManager
    private var _isInit: Boolean = false
    private val handler = Handler(Looper.getMainLooper())
    private val ENABLE_PRESENT_DELAY_MS = 400L
    private val REATTACH_DELAY_MS = 300L
    private var wantPresentEnabled = false
    private val TAG_FG = "FgPresent"
    private val delayedHandleIntent = object : Runnable { override fun run() { handleIntent() } }
    var storedIntent: Intent = Intent()
    var isGameRunning = false
    var isActive = false
    var storageHelper: SimpleStorageHelper? = null
    lateinit var uiHandler: UiHandler

    // Persistence for zombie detection
    private val PREFS = "emu_core"
    private val KEY_EMU_RUNNING = "emu_running"

    // Display Rotation + Orientation Handling
    private lateinit var displayManager: DisplayManager
    private var lastKnownRotation: Int? = null
    private var pulsingOrientation = false
    private var lastPulseAt = 0L

    private val TAG_ROT = "RotationDebug"

    private fun rotLog(msg: String) {
        val enabled = BuildConfig.DEBUG && QuickSettings(this).enableDebugLogs
        if (enabled) Log.d(TAG_ROT, msg)
    }

    private val serviceStopReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (intent?.action == EmulationService.ACTION_STOPPED) {
                handler.removeCallbacks(reattachWindowWhenReady)
                handler.removeCallbacks(enablePresentWhenReady)
                clearEmuRunningFlag()
                hardColdReset("service stopped broadcast")
            }
        }
    }

    private val displayListener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) {}
        override fun onDisplayRemoved(displayId: Int) {}
        override fun onDisplayChanged(displayId: Int) {
            if (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    display?.displayId != displayId
                } else {
                    @Suppress("DEPRECATION")
                    return
                }
            ) return
            val rot = display?.rotation
            if (rot == lastKnownRotation) return

            rotLog("onDisplayChanged: display.rotation=$rot → ${deg(rot)}°")

            val pref = QuickSettings(this@MainActivity).orientationPreference
            val old = lastKnownRotation
            lastKnownRotation = rot

            // 1) Inform Native/Renderer (applies to Sensor & SensorLandscape)
            try { KenjinxNative.setSurfaceRotationByAndroidRotation(rot) } catch (_: Throwable) {}

            // 2) Initiate host resize (applies to Sensor & SensorLandscape)
            if (isGameRunning) {
                handler.post {
                    try { mainViewModel?.gameHost?.onOrientationOrSizeChanged(rot) } catch (_: Throwable) {}
                }
            }

            // 3) Only with SENSOR_LANDSCAPE: gentle pulse with real 90↔270 flip
            if (pref == QuickSettings.OrientationPreference.SensorLandscape && old != null && rot != null) {
                val isSideFlip = (old == Surface.ROTATION_90 && rot == Surface.ROTATION_270) ||
                    (old == Surface.ROTATION_270 && rot == Surface.ROTATION_90)
                if (isSideFlip) doOrientationPulse(rot)
            }
        }
    }

    private fun deg(r: Int?): Int = when (r) {
        Surface.ROTATION_0 -> 0
        Surface.ROTATION_90 -> 90
        Surface.ROTATION_180 -> 180
        Surface.ROTATION_270 -> 270
        else -> -1
    }

    private fun setPresentEnabled(enabled: Boolean, reason: String) {
        wantPresentEnabled = enabled
        try {
            KenjinxNative.graphicsSetPresentEnabled(enabled)
            Log.d(TAG_FG, "present=${if (enabled) "ENABLED" else "DISABLED"} ($reason)")
        } catch (_: Throwable) {
            Log.d(TAG_FG, "native toggle not available ($reason)")
        }
    }

    private val enablePresentWhenReady = object : Runnable {
        override fun run() {
            val isReallyResumed = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) && isActive
            val hasFocusNow = hasWindowFocus()
            val rendererReady = mainViewModel?.rendererReady == true

            if (!isReallyResumed || !hasFocusNow || !rendererReady) {
                handler.postDelayed(this, ENABLE_PRESENT_DELAY_MS)
                return
            }
            setPresentEnabled(true, "focus regained + delay")
        }
    }

    private val reattachWindowWhenReady = object : Runnable {
        override fun run() {
            val isReallyResumed = lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) && isActive
            val hasFocusNow = hasWindowFocus()
            if (!isReallyResumed || !hasFocusNow) {
                handler.postDelayed(this, REATTACH_DELAY_MS)
                return
            }

            try { mainViewModel?.gameHost?.rebindNativeWindow(force = true) } catch (_: Throwable) {}

            if (!KenjinxNative.reattachWindowIfReady()) {
                handler.postDelayed(this, REATTACH_DELAY_MS)
                return
            }
            Log.d(TAG_FG, "window reattached")
        }
    }

    private fun doOrientationPulse(currentRot: Int) {
        val now = android.os.SystemClock.uptimeMillis()
        if (pulsingOrientation || now - lastPulseAt < 350L) return
        pulsingOrientation = true
        lastPulseAt = now

        // Short lock on the target page (prevents flickering)
        val lock = if (currentRot == Surface.ROTATION_90)
            ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
        else
            ActivityInfo.SCREEN_ORIENTATION_REVERSE_LANDSCAPE

        try { requestedOrientation = lock } catch (_: Throwable) {}
        handler.post {
            if (isGameRunning) {
                try { KenjinxNative.setSurfaceRotationByAndroidRotation(currentRot) } catch (_: Throwable) {}
                try { mainViewModel?.gameHost?.onOrientationOrSizeChanged(currentRot) } catch (_: Throwable) {}
            }
        }

        // After a short time back to SENSOR_LANDSCAPE
        handler.postDelayed({
            try { requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE } catch (_: Throwable) {}
            handler.post {
                if (isGameRunning) {
                    try {
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                            KenjinxNative.setSurfaceRotationByAndroidRotation(display?.rotation)
                        }
                    } catch (_: Throwable) {}
                    try {
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                            mainViewModel?.gameHost?.onOrientationOrSizeChanged(display?.rotation)
                        }
                    } catch (_: Throwable) {}
                }
            }
            pulsingOrientation = false
        }, 250)
    }

    companion object {
        var mainViewModel: MainViewModel? = null
        var AppPath: String = ""
        var StorageHelper: SimpleStorageHelper? = null

        const val EXTRA_BOOT_PATH = "bootPath"
        const val EXTRA_FORCE_NCE_PPTC = "forceNceAndPptc"
        const val EXTRA_TITLE_ID = "titleId"
        const val EXTRA_TITLE_NAME = "titleName"

        @RequiresApi(Build.VERSION_CODES.R)
        @JvmStatic
        fun frameEnded() {
            mainViewModel?.activity?.apply {
                if (isActive && QuickSettings(this).enablePerformanceMode) {
                    mainViewModel?.performanceManager?.setTurboMode(true)
                }
            }
            mainViewModel?.gameHost?.hideProgressIndicator()
        }

        // <<< is called from the Native/Lib page to set the loading progress
        @JvmStatic
        fun updateProgress(info: String, percent: Float) {
            // Route directly via the GameHost – it takes care of the progress states
            mainViewModel?.gameHost?.setProgress(info, percent)
        }
    }

    init {
        storageHelper = SimpleStorageHelper(this)
        StorageHelper = storageHelper
        System.loadLibrary("kenjinxjni")
        initVm()
    }

    private external fun initVm()

    private fun initialize() {
        if (_isInit) return
        val appPath: String = AppPath

        val quickSettings = QuickSettings(this)
        KenjinxNative.loggingSetEnabled(LogLevel.Info, quickSettings.enableInfoLogs)
        KenjinxNative.loggingSetEnabled(LogLevel.Stub, quickSettings.enableStubLogs)
        KenjinxNative.loggingSetEnabled(LogLevel.Warning, quickSettings.enableWarningLogs)
        KenjinxNative.loggingSetEnabled(LogLevel.Error, quickSettings.enableErrorLogs)
        KenjinxNative.loggingSetEnabled(LogLevel.AccessLog, quickSettings.enableFsAccessLogs)
        KenjinxNative.loggingSetEnabled(LogLevel.Guest, quickSettings.enableGuestLogs)
        KenjinxNative.loggingSetEnabled(LogLevel.Trace, quickSettings.enableTraceLogs)
        KenjinxNative.loggingSetEnabled(LogLevel.Debug, quickSettings.enableDebugLogs)
        KenjinxNative.loggingEnabledGraphicsLog(quickSettings.enableGraphicsLogs)

        _isInit = KenjinxNative.javaInitialize(appPath, JNIEnv.CURRENT)
    }

    @RequiresApi(Build.VERSION_CODES.R)
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ensureNotificationPermission()

        motionSensorManager = MotionSensorManager(this)
        Thread.setDefaultUncaughtExceptionHandler(crashHandler)

        if (!Environment.isExternalStorageManager()
        ) {
            storageHelper?.storage?.requestFullStorageAccess()
        }

        AppPath = this.getExternalFilesDir(null)!!.absolutePath

        coldResetIfZombie("onCreate")
        initialize()

        window.attributes.layoutInDisplayCutoutMode =
            WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        // Apply alignment
        applyOrientationPreference()

        WindowInsetsControllerCompat(window, window.decorView).let { controller ->
            controller.hide(WindowInsetsCompat.Type.systemBars())
            controller.systemBarsBehavior =
                WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }

        uiHandler = UiHandler()
        displayManager = getSystemService(DISPLAY_SERVICE) as DisplayManager

        mainViewModel = MainViewModel(this)
        mainViewModel!!.physicalControllerManager = physicalControllerManager
        mainViewModel!!.motionSensorManager = motionSensorManager
        mainViewModel!!.refreshFirmwareVersion()

        NeoThemeState.initialize(this)

        mainViewModel?.apply {
            setContent {
                KenjinxAndroidTheme(darkTheme = NeoThemeState.isDark) {
                    Surface(
                        modifier = Modifier.fillMaxSize(),
                        color = MaterialTheme.colorScheme.background
                    ) {
                        MainView.Main(mainViewModel = this)
                    }
                }
            }
        }

        storedIntent = intent
        rotLog("onCreate: initial display.rotation=${display?.rotation} → ${deg(display?.rotation)}°")
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        storedIntent = intent
    }

    override fun onSaveInstanceState(outState: Bundle) {
        storageHelper?.onSaveInstanceState(outState)
        super.onSaveInstanceState(outState)
    }

    override fun onRestoreInstanceState(savedInstanceState: Bundle) {
        super.onRestoreInstanceState(savedInstanceState)
        storageHelper?.onRestoreInstanceState(savedInstanceState)
    }

    @SuppressLint("RestrictedApi")
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        // NEOV2-HOTKEY2: atalho do usuario para fechar o emulador (e captura do atalho nas configuracoes).
        if (ExitHotkey.onKeyEvent(this, event)) return true

        // Fora da emulação, os botões físicos pertencem à navegação da UI
        // (biblioteca, diálogos e configurações). Só encaminhe ao backend
        // quando houver um jogo efetivamente em execução.
        if (isGameRunning) {
            event.apply { if (physicalControllerManager.onKeyEvent(this)) return true }
        }
        return super.dispatchKeyEvent(event)
    }

    override fun dispatchGenericMotionEvent(ev: MotionEvent?): Boolean {
        ev?.apply { physicalControllerManager.onMotionEvent(this) }
        return super.dispatchGenericMotionEvent(ev)
    }

    // --- Audio foreground/background gating ---
    private fun setAudioForegroundState(inForeground: Boolean) {
        // Preferred: pause instead of just muting
        try { KenjinxNative.audioSetPaused(!inForeground) } catch (_: Throwable) {}
        // Fallback: Master Mute
        try { KenjinxNative.audioSetMuted(!inForeground) } catch (_: Throwable) {}
    }

    // --------- BACKGROUND STABILITY: Present gating ---------
    override fun onStart() {
        super.onStart()
        coldResetIfZombie("onStart")

        if (isGameRunning && mainViewModel?.rendererReady == true) {
            try {
                KenjinxNative.graphicsSetPresentEnabled(true)
                Log.d(TAG_FG, "present=ENABLED (onStart)")
            } catch (_: Throwable) {}
        } else {
            Log.d(TAG_FG, "skip enable present (onStart) — rendererReady=${mainViewModel?.rendererReady}")
            setPresentEnabled(false, "cold reset: onStart (no game)")
        }
    }

    override fun onStop() {
        super.onStop()
        if (isGameRunning) {
            setAudioForegroundState(false)
            handler.removeCallbacks(reattachWindowWhenReady)
            handler.removeCallbacks(enablePresentWhenReady)
            setPresentEnabled(false, "onStop")
            try { KenjinxNative.detachWindow() } catch (_: Throwable) {}
        }
        // IMPORTANT: Safely release the binding (prevents memory leak)
        try { mainViewModel?.gameHost?.shutdownBinding() } catch (_: Throwable) {}
    }

    override fun onTrimMemory(level: Int) {
        super.onTrimMemory(level)
        if (level >= TRIM_MEMORY_UI_HIDDEN && isGameRunning) {
            setAudioForegroundState(false)
            if (mainViewModel?.rendererReady == true) {
                try {
                    setPresentEnabled(false, "onTrimMemory:$level")
                    KenjinxNative.detachWindow()
                } catch (_: Throwable) {}
            } else {
                Log.d(TAG_FG, "skip disable present (onTrimMemory) — rendererReady=${mainViewModel?.rendererReady}")
            }
        }
    }
    // --------------------------------------------------------

    override fun onResume() {
        super.onResume()
        isActive = true
        setAudioForegroundState(true)

        coldResetIfZombie("onResume")

        applyOrientationPreference()

        // Enable display listener
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            lastKnownRotation = display?.rotation
            rotLog("onResume: display.rotation=${display?.rotation} → ${deg(display?.rotation)}°")
        }

        try { displayManager.registerDisplayListener(displayListener, handler) } catch (_: Throwable) {}

        try {
            if (Build.VERSION.SDK_INT >= 33) {
                registerReceiver(
                    serviceStopReceiver,
                    IntentFilter(EmulationService.ACTION_STOPPED),
                    RECEIVER_EXPORTED
                )
            } else {
                @Suppress("DEPRECATION")
                registerReceiver(serviceStopReceiver, IntentFilter(EmulationService.ACTION_STOPPED))
            }
        } catch (_: Throwable) {}

        handler.removeCallbacks(reattachWindowWhenReady)
        handler.removeCallbacks(enablePresentWhenReady)

        try { mainViewModel?.gameHost?.rebindNativeWindow(force = true) } catch (_: Throwable) {}

        handler.postDelayed(delayedHandleIntent, 10)

        if (isGameRunning && QuickSettings(this).enableMotion) {
            motionSensorManager.register()
        }

        if (isGameRunning) {
            handler.postDelayed(reattachWindowWhenReady, REATTACH_DELAY_MS)
            if (hasWindowFocus()) {
                handler.postDelayed(enablePresentWhenReady, ENABLE_PRESENT_DELAY_MS)
            }
        } else {
            setPresentEnabled(false, "cold reset: onResume (no game)")
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!isGameRunning) return

        handler.removeCallbacks(reattachWindowWhenReady)
        handler.removeCallbacks(enablePresentWhenReady)

        if (hasFocus && isActive) {
            setAudioForegroundState(true)
            // First ensure that the binding exists
            try { mainViewModel?.gameHost?.ensureServiceStartedAndBound() } catch (_: Throwable) {}

            setPresentEnabled(false, "focus gained → pre-rebind")
            try { mainViewModel?.gameHost?.rebindNativeWindow(force = true) } catch (_: Throwable) {}
            handler.postDelayed(reattachWindowWhenReady, 150L)
            val rot = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) display?.rotation else null
            handler.postDelayed({ try { mainViewModel?.gameHost?.postReattachKicks(rot) } catch (_: Throwable) {} }, 200L)
            handler.postDelayed(enablePresentWhenReady, 450L)
        } else {
            setAudioForegroundState(false)
            setPresentEnabled(false, "focus lost")
            try { KenjinxNative.detachWindow() } catch (_: Throwable) {}
        }
    }

    @RequiresApi(Build.VERSION_CODES.R)
    override fun onPause() {
        super.onPause()
        isActive = false
        setAudioForegroundState(false)

        handler.removeCallbacks(reattachWindowWhenReady)
        handler.removeCallbacks(enablePresentWhenReady)

        if (isGameRunning) {
            setPresentEnabled(false, "onPause")
            try { KenjinxNative.detachWindow() } catch (_: Throwable) {}
            mainViewModel?.performanceManager?.setTurboMode(false)
            motionSensorManager.unregister()
        }

        try { displayManager.unregisterDisplayListener(displayListener) } catch (_: Throwable) {}
        try { unregisterReceiver(serviceStopReceiver) } catch (_: Throwable) {}

        // Clean up binding (prevents memory leak when swiping away the task)
        try { mainViewModel?.gameHost?.shutdownBinding() } catch (_: Throwable) {}
    }

    private fun handleIntent() {
        when (storedIntent.action) {
            Intent.ACTION_VIEW, "org.kenjinx.android.LAUNCH_GAME" -> {
                val bootPathExtra = storedIntent.getStringExtra(EXTRA_BOOT_PATH)
                val forceNceAndPptc = storedIntent.getBooleanExtra(EXTRA_FORCE_NCE_PPTC, false)
                val titleId = storedIntent.getStringExtra(EXTRA_TITLE_ID) ?: ""
                val titleName = storedIntent.getStringExtra(EXTRA_TITLE_NAME) ?: ""
                val dataUri: Uri? = storedIntent.data

                val chosenUri: Uri? = when {
                    !bootPathExtra.isNullOrEmpty() -> bootPathExtra.toUri()
                    dataUri != null -> dataUri
                    else -> null
                }

                if (chosenUri != null) {
                    val doc = when (chosenUri.scheme?.lowercase()) {
                        "content" -> DocumentFile.fromSingleUri(this, chosenUri)
                        "file" -> chosenUri.path?.let { File(it) }?.let { DocumentFile.fromFile(it) }
                        else -> {
                            chosenUri.path?.let { File(it) }?.takeIf { it.exists() }?.let { DocumentFile.fromFile(it) }
                                ?: DocumentFile.fromSingleUri(this, chosenUri)
                        }
                    }

                    if (doc != null && doc.exists()) {
                        val gameModel = GameModel(doc, this)
                        gameModel.getGameInfo()
                        mainViewModel?.loadGameModel?.value = gameModel
                        mainViewModel?.bootPath?.value = "gameItem_${gameModel.titleName}"
                        mainViewModel?.forceNceAndPptc?.value = forceNceAndPptc
                        storedIntent = Intent()
                        return
                    } else {
                        Log.w("ShortcutDebug", "DocumentFile not found or not accessible: $chosenUri")
                    }
                }

                if (titleId.isNotEmpty() || titleName.isNotEmpty()) {
                    resolveGameByTitleIdOrName(titleId, titleName)?.let { doc ->
                        val gameModel = GameModel(doc, this)
                        gameModel.getGameInfo()
                        mainViewModel?.loadGameModel?.value = gameModel
                        mainViewModel?.bootPath?.value = "gameItem_${gameModel.titleName}"
                        mainViewModel?.forceNceAndPptc?.value = forceNceAndPptc
                        storedIntent = Intent()
                        return
                    }
                }
            }
        }
    }

    private fun applyOrientationPreference() {
        val pref = QuickSettings(this).orientationPreference
        requestedOrientation = pref.value
        val rot = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            this.display?.rotation
        } else {
            @Suppress("DEPRECATION")
            null
        }
        rotLog("applyOrientationPreference: rot=$rot → ${deg(rot)}°, pref=${pref.name}")
        try { KenjinxNative.setSurfaceRotationByAndroidRotation(rot) } catch (_: Throwable) {}
    }

    @RequiresApi(Build.VERSION_CODES.R)
    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        val rot = this.display?.rotation
        val old = lastKnownRotation
        lastKnownRotation = rot

        rotLog("onConfigurationChanged: display.rotation=$rot → ${deg(rot)}°")

        try { KenjinxNative.setSurfaceRotationByAndroidRotation(rot) } catch (_: Throwable) {}

        val pref = QuickSettings(this).orientationPreference
        val shouldPropagate =
            pref == QuickSettings.OrientationPreference.Sensor ||
                pref == QuickSettings.OrientationPreference.SensorLandscape

        if (shouldPropagate && isGameRunning) {
            handler.post { try { mainViewModel?.gameHost?.onOrientationOrSizeChanged(rot) } catch (_: Throwable) {} }
        }

        if (pref == QuickSettings.OrientationPreference.SensorLandscape && old != null && rot != null) {
            val isSideFlip = (old == Surface.ROTATION_90 && rot == Surface.ROTATION_270) ||
                (old == Surface.ROTATION_270 && rot == Surface.ROTATION_90)
            if (isSideFlip) doOrientationPulse(rot)
        }
    }

    private val requestNotifPerm = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { /* optional: Log/Toast */ }

    private fun ensureNotificationPermission() {
        if (Build.VERSION.SDK_INT >= 33) {
            val granted = ContextCompat.checkSelfPermission(
                this, Manifest.permission.POST_NOTIFICATIONS
            ) == PackageManager.PERMISSION_GRANTED
            if (!granted) {
                requestNotifPerm.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
    }

    private fun resolveGameByTitleIdOrName(titleIdHex: String?, displayName: String?): DocumentFile? {
        val gamesRoot = getDefaultGamesTree() ?: return null
        for (child in gamesRoot.listFiles()) {
            if (!child.isFile) continue
            if (!displayName.isNullOrBlank()) {
                val n = child.name ?: ""
                if (n.contains(displayName, ignoreCase = true)) return child
            }
            if (!titleIdHex.isNullOrBlank()) {
                val tid = getTitleIdFast(child)
                if (tid != null && tid.equals(titleIdHex, ignoreCase = true)) return child
            }
        }
        if (!titleIdHex.isNullOrBlank()) {
            for (child in gamesRoot.listFiles()) {
                if (!child.isFile) continue
                val tid = getTitleIdFast(child)
                if (tid != null && tid.equals(titleIdHex, ignoreCase = true)) return child
            }
        }
        return null
    }

    private fun getDefaultGamesTree(): DocumentFile? {
        val vm = mainViewModel
        if (vm?.defaultGameFolderUri != null) {
            return DocumentFile.fromTreeUri(this, vm.defaultGameFolderUri!!)
        }
        val prefs = PreferenceManager.getDefaultSharedPreferences(this)
        val legacyPath = prefs.getString("gameFolder", null)
        if (!legacyPath.isNullOrEmpty()) {
            // Without a SAF URI, tree listing is not possible
        }
        return null
    }

    private fun getTitleIdFast(file: DocumentFile): String? {
        val name = file.name ?: return null
        val dot = name.lastIndexOf('.')
        if (dot <= 0 || dot >= name.length - 1) return null
        val ext = name.substring(dot + 1).lowercase()
        return try {
            contentResolver.openFileDescriptor(file.uri, "r")?.use { pfd ->
                val info = org.kenjinx.android.viewmodels.GameInfo()
                KenjinxNative.deviceGetGameInfo(pfd.fd, ext, info)
                info.TitleId?.lowercase()
            }
        } catch (_: Exception) { null }
    }

    @RequiresApi(Build.VERSION_CODES.R)
    fun shutdownAndRestart() {
        val packageManager = packageManager
        val intent = packageManager.getLaunchIntentForPackage(packageName)
        val componentName = intent?.component
        val restartIntent = Intent.makeRestartActivityTask(componentName)
        mainViewModel?.let { it.performanceManager?.setTurboMode(false) }
        startActivity(restartIntent)
        Runtime.getRuntime().exit(0)
    }

    override fun onDestroy() {
        handler.removeCallbacks(enablePresentWhenReady)
        handler.removeCallbacks(reattachWindowWhenReady)
        // If the activity dies → the binding is guaranteed to be released
        try { mainViewModel?.gameHost?.shutdownBinding() } catch (_: Throwable) {}
        super.onDestroy()
    }

    // ---------- Helpers ----------

    private fun setEmuRunningFlag(value: Boolean) {
        try {
            getSharedPreferences(PREFS, MODE_PRIVATE)
                .edit {
                    putBoolean(KEY_EMU_RUNNING, value)
                }
        } catch (_: Throwable) { }
    }

    private fun clearEmuRunningFlag() = setEmuRunningFlag(false)

    private fun hardColdReset(reason: String) {
        Log.d(TAG_FG, "Cold graphics reset ($reason)")
        isGameRunning = false
        mainViewModel?.rendererReady = false

        try { setPresentEnabled(false, "cold reset: $reason") } catch (_: Throwable) {}
        try { KenjinxNative.detachWindow() } catch (_: Throwable) {}

        try { stopService(Intent(this, EmulationService::class.java)) } catch (_: Throwable) {}

        try { mainViewModel?.loadGameModel?.value = null } catch (_: Throwable) {}
        try { mainViewModel?.bootPath?.value = "" } catch (_: Throwable) {}
        try { mainViewModel?.forceNceAndPptc?.value = false } catch (_: Throwable) {}
        storedIntent = Intent()
    }

    private fun coldResetIfZombie(phase: String) {
        try {
            val zombie = getSharedPreferences(PREFS, MODE_PRIVATE)
                .getBoolean(KEY_EMU_RUNNING, false)
            if (zombie) {
                clearEmuRunningFlag()
                setPresentEnabled(false, "kill stray: $phase")
                hardColdReset("kill stray: $phase")
            }
        } catch (_: Throwable) { }
    }
}
