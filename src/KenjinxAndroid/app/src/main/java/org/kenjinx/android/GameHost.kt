package org.kenjinx.android

import android.annotation.SuppressLint
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.compose.runtime.MutableState
import org.kenjinx.android.service.EmulationService
import org.kenjinx.android.viewmodels.GameModel
import org.kenjinx.android.viewmodels.MainViewModel
import kotlin.concurrent.thread

@SuppressLint("ViewConstructor")
class GameHost(context: Context?, private val mainViewModel: MainViewModel) : SurfaceView(context),
    SurfaceHolder.Callback {

    private var isProgressHidden: Boolean = false
    private var progress: MutableState<String>? = null
    private var progressValue: MutableState<Float>? = null
    private var showLoading: MutableState<Boolean>? = null
    private var game: GameModel? = null

    private var _isClosed: Boolean = false
    private var _renderingThreadWatcher: Thread? = null
    private var _height: Int = 0
    private var _width: Int = 0
    private var _updateThread: Thread? = null
    private var _guestThread: Thread? = null
    private var _isInit: Boolean = false
    private var _isStarted: Boolean = false
    private val _nativeWindow: NativeWindow

    private val mainHandler = Handler(Looper.getMainLooper())

    // ---- Foreground service binding ----
    private var emuBound = false
    private var emuBinder: EmulationService.LocalBinder? = null
    private var _startedViaService = false
    private var _inputInitialized: Boolean = false

    private val emuConn = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName, service: IBinder) {
            emuBinder = service as EmulationService.LocalBinder
            emuBound = true
            ghLog("EmulationService bound")

            // If the start has already been prepared and no loop is currently running → start it in the service now
            if (_isStarted && !_startedViaService && _guestThread == null) {
                startRunLoopInService()
            }
        }

        override fun onServiceDisconnected(name: ComponentName) {
            ghLog("EmulationService unbound")
            emuBound = false
            emuBinder = null
            _startedViaService = false
        }
    }

    // Resize Stabilizer
    private var stabilizerActive = false

    // last known Android rotation (0,1,2,3)
    private var lastRotation: Int? = null

    // Debounce for resize kick
    private var lastKickAt = 0L

    var currentSurface: Long = -1
        private set

    val currentWindowHandle: Long
        get() = _nativeWindow.nativePointer

    init {
        holder.addCallback(this)
        _nativeWindow = NativeWindow(this)
        mainViewModel.gameHost = this
    }

    private fun ghLog(msg: String) {
        val enabled = BuildConfig.DEBUG && org.kenjinx.android.viewmodels.QuickSettings(mainViewModel.activity).enableDebugLogs
        if (enabled) Log.d("GameHost", msg)
    }

    /**
     * (Re)bind the current ANativeWindow to the renderer.
     * Forces a fresh native pointer and passes it to C#.
     */
    fun rebindNativeWindow(force: Boolean = false) {
        if (_isClosed) return
        try {
            currentSurface = _nativeWindow.requeryWindowHandle()
            _nativeWindow.swapInterval = 0
            KenjinxNative.deviceSetWindowHandle(currentWindowHandle)

            val w = if (holder.surfaceFrame.width() > 0) holder.surfaceFrame.width() else width
            val h = if (holder.surfaceFrame.height() > 0) holder.surfaceFrame.height() else height
            if (w > 0 && h > 0) {
                if (MainActivity.mainViewModel?.rendererReady == true) {
                    try { KenjinxNative.graphicsRendererSetSize(w, h) } catch (_: Throwable) {}
                }
                if (_inputInitialized) {
                    try { KenjinxNative.inputSetClientSize(w, h) } catch (_: Throwable) {}
                }
            }
        } catch (_: Throwable) { }
    }

    /**
     * After a successful reattachment, safely "wake up" the swapchain/viewport:
     * - Set rotation
     * - Two consecutive resize kicks
     */
    fun postReattachKicks(rotation: Int?) {
        if (_isClosed) return
        try {
            KenjinxNative.setSurfaceRotationByAndroidRotation(rotation ?: 0)
            val w = if (holder.surfaceFrame.width() > 0) holder.surfaceFrame.width() else width
            val h = if (holder.surfaceFrame.height() > 0) holder.surfaceFrame.height() else height
            if (w > 0 && h > 0 &&
                MainActivity.mainViewModel?.rendererReady == true &&
                _isStarted && _inputInitialized
            ) {
                try { KenjinxNative.resizeRendererAndInput(w, h) } catch (_: Throwable) {}
                mainHandler.postDelayed({
                    try { KenjinxNative.resizeRendererAndInput(w, h) } catch (_: Throwable) {}
                }, 32)
            }
        } catch (_: Throwable) { }
    }

    // -------- Surface Lifecycle --------

    override fun surfaceCreated(holder: SurfaceHolder) {
        ghLog("surfaceCreated")
        // Bind early so that the service is already in place before we start
        ensureServiceStartedAndBound()
        rebindNativeWindow(force = true)
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        ghLog("surfaceChanged ${width}x$height")
        if (_isClosed) return

        // Always rebind – even if the size remains the same
        rebindNativeWindow(force = true)

        val sizeChanged = (_width != width || _height != height)
        _width = width
        _height = height

        // Ensure service availability & start rendering
        ensureServiceStartedAndBound()
        start(holder)

        // Stabilize resizing (adopts a plausible final size setting)
        startStabilizedResize(expectedRotation = lastRotation)
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        ghLog("surfaceDestroyed → shutdownBinding()")
        try { KenjinxNative.detachWindow() } catch (_: Throwable) {}
        // Always bind and unbind (prevents leaks when swiping away tasks)
        shutdownBinding()
        // The actual emulator termination happens via close() / Exit Game
    }

    override fun onWindowVisibilityChanged(visibility: Int) {
        super.onWindowVisibilityChanged(visibility)
        if (visibility != VISIBLE) {
            ghLog("window not visible → shutdownBinding()")
            try { KenjinxNative.detachWindow() } catch (_: Throwable) {}
            shutdownBinding()
        }
    }

    // -------- UI Progress --------

    fun setProgress(info: String, progressVal: Float) {
        showLoading?.apply {
            progressValue?.apply { this.value = progressVal }
            progress?.apply { this.value = info }
        }
    }

    fun setProgressStates(
        showLoading: MutableState<Boolean>?,
        progressValue: MutableState<Float>?,
        progress: MutableState<String>?
    ) {
        this.showLoading = showLoading
        this.progressValue = progressValue
        this.progress = progress
        showLoading?.apply { value = !isProgressHidden }
    }

    fun hideProgressIndicator() {
        isProgressHidden = true
        showLoading?.apply {
            if (value == isProgressHidden) value = !isProgressHidden
        }
    }

    // -------- Start/Stop Emulation --------

    private fun start(surfaceHolder: SurfaceHolder) {
        if (_isStarted) return

        // Do NOT set _isStarted = true immediately → first prepare everything
        rebindNativeWindow(force = true)

        game = if (mainViewModel.isMiiEditorLaunched) null else mainViewModel.gameModel

        // Input initialize
        KenjinxNative.inputInitialize(width, height)
        _inputInitialized = true

        val id = mainViewModel.physicalControllerManager?.connect()
        mainViewModel.motionSensorManager?.setControllerId(id ?: -1)

        val currentRot = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            mainViewModel.activity.display?.rotation
        } else null
        lastRotation = currentRot

        try {
            KenjinxNative.setSurfaceRotationByAndroidRotation(currentRot ?: 0)
            try { KenjinxNative.deviceSetWindowHandle(currentWindowHandle) } catch (_: Throwable) {}

            // Gentle kick only when renderer is READY **and** input is initialized
            if (width > 0 && height > 0 &&
                MainActivity.mainViewModel?.rendererReady == true &&
                _inputInitialized
            ) {
                try { KenjinxNative.resizeRendererAndInput(width, height) } catch (_: Throwable) {}
            }
        } catch (_: Throwable) {}

        val qs = org.kenjinx.android.viewmodels.QuickSettings(mainViewModel.activity)
        try { KenjinxNative.graphicsSetFullscreenStretch(qs.stretchToFullscreen) } catch (_: Throwable) {}

        // The host is now considered "started".
        _isStarted = true

        // Always try starting with the service first; if binding is not yet complete → wait briefly, then fall back
        if (emuBound) {
            startRunLoopInService()
        } else {
            ghLog("Service not yet bound → delayed runloop start")
            mainHandler.postDelayed({
                if (!_isStarted) return@postDelayed
                if (emuBound) {
                    startRunLoopInService()
                } else {
                    // Fallback: Local thread (this should rarely happen)
                    ghLog("Fallback: starting RunLoop in local thread")
                    _guestThread = thread(start = true, name = "KenjinxGuest") { runGame() }
                }
            }, 150)
        }

        _updateThread = thread(start = true, name = "KenjinxInput/Stats") {
            var c = 0
            while (_isStarted) {
                KenjinxNative.inputUpdate()
                Thread.sleep(1)
                if (++c >= 1000) {
                    if (progressValue?.value == -1f) {
                        progress?.apply {
                            this.value = "Loading ${if (mainViewModel.isMiiEditorLaunched) "Mii Editor" else game?.titleName ?: ""}"
                        }
                    }
                    c = 0
                    mainViewModel.updateStats(
                        KenjinxNative.deviceGetGameFifo(),
                        KenjinxNative.deviceGetGameFrameRate(),
                        KenjinxNative.deviceGetGameFrameTime()
                    )
                }
            }
        }
    }

    private fun runGame() {
        mainViewModel.onEmulationStarted()
        try {
            KenjinxNative.graphicsRendererRunLoop()
        } finally {
            mainViewModel.onEmulationStopped()
        }
        game?.close()
    }

    fun close() {
        ghLog("close()")
        _isClosed = true
        _isInit = false
        _isStarted = false
        _inputInitialized = false

        KenjinxNative.uiHandlerSetResponse(false, "")

        // Stop emulation in the service (if it was started there)
        try {
            if (emuBound && _startedViaService) {
                emuBinder?.stopEmulation {
                    // Close is handled in the service hard-close path.
                }
            }
        } catch (_: Throwable) { }

        // Fallback: Terminate local thread
        try { _updateThread?.join(200) } catch (_: Throwable) {}
        try { _renderingThreadWatcher?.join(200) } catch (_: Throwable) {}
        try { _guestThread?.join(1000) } catch (_: Throwable) {}

        if (!(emuBound && _startedViaService)) {
            try { KenjinxNative.deviceCloseEmulation() } catch (_: Throwable) {}
            mainViewModel.onEmulationStopped()
        }

        // Release the bond
        shutdownBinding()

        // Explicitly terminate the service (if it is still running)
        try {
            mainViewModel.activity.stopService(Intent(mainViewModel.activity, EmulationService::class.java))
        } catch (_: Throwable) { }
    }

    // -------- Orientation / Resize --------

    /**
     * Securely setting the renderer/input size
     */
    @Synchronized
    private fun safeSetSize(w: Int, h: Int) {
        if (_isClosed || w <= 0 || h <= 0) return
        try {
            ghLog("safeSetSize: ${w}x$h (started=$_isStarted, inputInit=$_inputInitialized)")
            KenjinxNative.graphicsRendererSetSize(w, h)
            if (_isStarted && _inputInitialized) {
                KenjinxNative.inputSetClientSize(w, h)
            }
        } catch (t: Throwable) {
            Log.e("GameHost", "safeSetSize failed: ${t.message}", t)
        }
    }

    /**
     * Called by the activity when the rotation/layout changes.
     * Detects 90°↔270° and forces (debounces) a requery/resize.
     */
    fun onOrientationOrSizeChanged(rotation: Int? = null) {
        if (_isClosed) return

        val old = lastRotation
        lastRotation = rotation

        val isSideFlip = (old == 1 && rotation == 3) || (old == 3 && rotation == 1)

        if (isSideFlip) {
            try { KenjinxNative.setSurfaceRotationByAndroidRotation(rotation) } catch (_: Throwable) {}
            rebindNativeWindow(force = true)
            val now = android.os.SystemClock.uptimeMillis()
            if (now - lastKickAt >= 300L && _inputInitialized && MainActivity.mainViewModel?.rendererReady == true) {
                lastKickAt = now
                val w = if (holder.surfaceFrame.width() > 0) holder.surfaceFrame.width() else width
                val h = if (holder.surfaceFrame.height() > 0) holder.surfaceFrame.height() else height
                if (w > 0 && h > 0) {
                    try { KenjinxNative.resizeRendererAndInput(w, h) } catch (_: Throwable) {}
                }
            }
        }

        startStabilizedResize(rotation)
    }

    /**
     * Wait a moment until the surface has its final dimensions after rotation,
     * checks plausibility (portrait/landscape) and only then sets the size.
     */
    private fun startStabilizedResize(expectedRotation: Int?) {
        if (_isClosed) return

        // Restart if already active
        if (stabilizerActive) {
            stabilizerActive = false
        }
        stabilizerActive = true

        var attempts = 0
        var stableCount = 0
        var lastW = -1
        var lastH = -1

        val task = object : Runnable {
            override fun run() {
                if (!_isStarted || _isClosed) {
                    stabilizerActive = false
                    return
                }

                // Prefer real frame size
                var w = holder.surfaceFrame.width()
                var h = holder.surfaceFrame.height()

                // Fallbacks
                if (w <= 0 || h <= 0) {
                    w = width
                    h = height
                }

                // If rotation is known: Force plausibility (Landscape ↔ Portrait)
                expectedRotation?.let { rot ->
                    val landscape = (rot == 1 || rot == 3) // ROTATION_90/270
                    if (landscape && h > w) {
                        val t = w; w = h; h = t
                    } else if (!landscape && w > h) {
                        val t = w; w = h; h = t
                    }
                }

                // Stability test
                if (w == lastW && h == lastH && w > 0 && h > 0) {
                    stableCount++
                } else {
                    stableCount = 0
                    lastW = w
                    lastH = h
                }

                attempts++

                // 1 stable tick or a maximum of 12 attempts
                if ((stableCount >= 1 || attempts >= 12) && w > 0 && h > 0) {
                    ghLog("resize stabilized after $attempts ticks → ${w}x$h")
                    safeSetSize(w, h)
                    stabilizerActive = false
                    return
                }

                if (stabilizerActive) {
                    mainHandler.postDelayed(this, 16)
                }
            }
        }

        mainHandler.post(task)
    }

    // ===== Service helpers =====

    /** Callable from Activity/Surface lifecycle to ensure FGS is running */
    fun ensureServiceStartedAndBound() {
        val act = mainViewModel.activity
        val intent = Intent(act, EmulationService::class.java)
        try {
            act.startForegroundService(intent)
        } catch (_: Throwable) { }

        try {
            if (!emuBound) {
                act.bindService(intent, emuConn, Context.BIND_AUTO_CREATE)
            }
        } catch (_: Throwable) { }
    }

    /** Call this from the Activity in onPause/onStop/onDestroy (and internally in surfaceDestroyed/onWindowVisibilityChanged) */
    fun shutdownBinding() {
        if (emuBound) {
            try {
                mainViewModel.activity.unbindService(emuConn)
            } catch (_: Throwable) { }
            emuBound = false
            emuBinder = null
            _startedViaService = false
            ghLog("shutdownBinding() → unbound")
        }
    }

    private fun startRunLoopInService() {
        if (!emuBound) return
        if (_startedViaService) return
        _startedViaService = true

        emuBinder?.startEmulation {
            try {
                mainViewModel.onEmulationStarted()
                KenjinxNative.graphicsRendererRunLoop()
            } catch (t: Throwable) {
                Log.e("GameHost", "RunLoop crash in service", t)
            } finally {
                _startedViaService = false
                mainViewModel.onEmulationStopped()
            }
        }
        ghLog("RunLoop started in EmulationService")
    }
}
