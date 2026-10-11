package org.kenjinx.android

import android.os.Build
import com.sun.jna.JNIEnv
import com.sun.jna.Library
import com.sun.jna.Native
import org.kenjinx.android.viewmodels.GameInfo
import java.util.Collections
import android.view.Surface
import android.util.Log
import androidx.annotation.RequiresApi

interface KenjinxNativeJna : Library {
    fun deviceInitialize(
        memoryManagerMode: Int,
        useNce: Boolean,
        memoryConfiguration: Int,
        memorySlackGiB: Int,
        systemLanguage: Int,
        regionCode: Int,
        vSyncMode: Int,
        enableDockedMode: Boolean,
        enablePptc: Boolean,
        enableLowPowerPptc: Boolean,
        enableJitCacheEviction: Boolean,
        enableInternetAccess: Boolean,
        enableFsIntegrityChecks: Boolean,
        fsGlobalAccessLogMode: Int,
        timeZone: String,
        ignoreMissingServices: Boolean
    ): Boolean

    fun graphicsInitialize(
        rescale: Float = 1f,
        maxAnisotropy: Float = 0f,
        fastGpuTime: Boolean = true,
        fast2DCopy: Boolean = true,
        enableMacroJit: Boolean = false,
        enableMacroHLE: Boolean = true,
        enableShaderCache: Boolean = true,
        enableTextureRecompression: Boolean = false,
        backendThreading: Int = BackendThreading.Auto.ordinal
    ): Boolean

    fun graphicsInitializeRenderer(
        extensions: Array<String>,
        extensionsLength: Int,
        driver: Long
    ): Boolean

    fun javaInitialize(appPath: String, env: JNIEnv): Boolean
    fun deviceLaunchMiiEditor(): Boolean
    fun deviceGetGameFrameRate(): Double
    fun deviceGetGameFrameTime(): Double
    fun deviceGetGameFifo(): Double
    fun deviceLoadDescriptor(fileDescriptor: Int, gameType: Int, updateDescriptor: Int): Boolean
    fun graphicsRendererSetSize(width: Int, height: Int)
    fun graphicsRendererSetVsync(vSyncMode: Int)
    fun graphicsRendererRunLoop()
    fun graphicsSetFullscreenStretch(enable: Boolean)
    fun deviceReloadFilesystem()
    fun inputInitialize(width: Int, height: Int)
    fun inputSetClientSize(width: Int, height: Int)
    fun inputSetTouchPoint(x: Int, y: Int)
    fun inputReleaseTouchPoint()
    fun inputUpdate()
    fun inputSetButtonPressed(button: Int, id: Int)
    fun inputSetButtonReleased(button: Int, id: Int)
    fun inputConnectGamepad(index: Int): Int
    fun inputSetStickAxis(stick: Int, x: Float, y: Float, id: Int)
    fun inputSetAccelerometerData(x: Float, y: Float, z: Float, id: Int)
    fun inputSetGyroData(x: Float, y: Float, z: Float, id: Int)
    fun deviceCloseEmulation()
    fun deviceReinitEmulation()
    fun deviceSignalEmulationClose()

    fun detachWindow()
    // >>> Rendering-related additions for the toggle:
    fun deviceWaitForGpuDone(timeoutMs: Int)
    fun deviceRecreateSwapchain()
    fun graphicsSetBackendThreading(mode: Int)
    fun graphicsSetPresentEnabled(enabled: Boolean)
    // <<<

    fun userGetOpenedUser(): String
    fun userGetUserPicture(userId: String): String
    fun userSetUserPicture(userId: String, picture: String)
    fun userGetUserName(userId: String): String
    fun userSetUserName(userId: String, userName: String)
    fun userAddUser(username: String, picture: String)
    fun userDeleteUser(userId: String)
    fun userOpenUser(userId: String)
    fun userCloseUser(userId: String)
    fun loggingSetEnabled(logLevel: Int, enabled: Boolean)
    fun deviceVerifyFirmware(fileDescriptor: Int, isXci: Boolean): String
    fun deviceInstallFirmware(fileDescriptor: Int, isXci: Boolean)
    fun deviceGetInstalledFirmwareVersion(): String
    fun uiHandlerSetup()
    fun uiHandlerSetResponse(isOkPressed: Boolean, input: String)
    fun deviceGetDlcTitleId(path: String, ncaPath: String): String
    fun deviceGetGameInfo(fileDescriptor: Int, extension: String, info: GameInfo)
    fun userGetAllUsers(): Array<String>
    fun deviceGetDlcContentList(path: String, titleId: Long): Array<String>
    fun loggingEnabledGraphicsLog(enabled: Boolean)
    // Surface rotation (0/90/180/270 degrees)
    fun deviceSetSurfaceRotation(degrees: Int)
    // (optional alias): compact resize shortcut
    fun deviceResize(width: Int, height: Int)
    // Set window handle after each query
    fun deviceSetWindowHandle(handle: Long)
    // Amiibo
    fun amiiboLoadBin(bytes: ByteArray, length: Int): Boolean
    fun amiiboClear()

    // AUDIO: Direct JNA bridge to C# exports
    fun audioSetPaused(paused: Boolean)
    fun audioSetMuted(muted: Boolean)
}

val jnaInstance: KenjinxNativeJna = Native.load(
    "kenjinx",
    KenjinxNativeJna::class.java,
    Collections.singletonMap(Library.OPTION_ALLOW_OBJECTS, true)
)

object KenjinxNative : KenjinxNativeJna by jnaInstance {

    fun loggingSetEnabled(logLevel: LogLevel, enabled: Boolean) =
        loggingSetEnabled(logLevel.ordinal, enabled)

    // --- Rendering: Single-threaded option & secure wrappers --------------------

    // 0 = Auto, 1 = SingleThread (Disable Threaded), 2 = Threaded
    private const val THREADING_AUTO = 0
    private const val THREADING_SINGLE = 1
    private const val THREADING_THREADED = 2

    override fun graphicsSetBackendThreading(mode: Int) {
        try {
            jnaInstance.graphicsSetBackendThreading(mode)
        } catch (_: Throwable) {
            Log.w("KenjinxNative", "graphicsSetBackendThreading not available")
        }
    }

    override fun deviceRecreateSwapchain() {
        try { jnaInstance.deviceRecreateSwapchain() } catch (_: Throwable) { /* ignore */ }
    }

    override fun deviceWaitForGpuDone(timeoutMs: Int) {
        try { jnaInstance.deviceWaitForGpuDone(timeoutMs) } catch (_: Throwable) { /* ignore */ }
    }

    override fun graphicsSetPresentEnabled(enabled: Boolean) {
        try { jnaInstance.graphicsSetPresentEnabled(enabled) } catch (_: Throwable) { /* ignore */ }
    }

    // Secure deviceResize implementation (pure rendering)
    override fun deviceResize(width: Int, height: Int) {
        try {
            graphicsRendererSetSize(width, height)
            inputSetClientSize(width, height)
        } catch (_: Throwable) { /* ignore */ }
    }

    // Robust graphicsInitialize with QCOM heuristics + fallback → SingleThread
    override fun graphicsInitialize(
        rescale: Float,
        maxAnisotropy: Float,
        fastGpuTime: Boolean,
        fast2DCopy: Boolean,
        enableMacroJit: Boolean,
        enableMacroHLE: Boolean,
        enableShaderCache: Boolean,
        enableTextureRecompression: Boolean,
        backendThreading: Int
    ): Boolean {
        val requested = backendThreading
        val isQcom = "qcom".equals(Build.HARDWARE, true)

        // Heuristic: On QCOM, when "Auto" is selected, first try single-threaded mode;
        // explicitly set values remain unchanged.
        val firstChoice =
            if (isQcom && requested == THREADING_AUTO) THREADING_SINGLE else requested

        Log.i(
            "KenjinxNative",
            "graphicsInitialize: request=$requested firstChoice=$firstChoice hw=${Build.HARDWARE}"
        )

        return try {
            jnaInstance.graphicsInitialize(
                rescale,
                maxAnisotropy,
                fastGpuTime,
                fast2DCopy,
                enableMacroJit,
                enableMacroHLE,
                enableShaderCache,
                enableTextureRecompression,
                firstChoice
            )
        } catch (t: Throwable) {
            Log.e(
                "KenjinxNative",
                "graphicsInitialize failed (firstChoice=$firstChoice). Fallback → SingleThread",
                t
            )
            try {
                jnaInstance.graphicsInitialize(
                    rescale,
                    maxAnisotropy,
                    fastGpuTime,
                    fast2DCopy,
                    enableMacroJit,
                    enableMacroHLE,
                    enableShaderCache,
                    enableTextureRecompression,
                    THREADING_SINGLE
                )
            } catch (t2: Throwable) {
                Log.e("KenjinxNative", "graphicsInitialize fallback failed", t2)
                false
            }
        }
    }

    // --- Optional wrappers for audio (safer logging) ---
    override fun audioSetPaused(paused: Boolean) {
        try { jnaInstance.audioSetPaused(paused) }
        catch (t: Throwable) { Log.w("KenjinxNative", "audioSetPaused unavailable", t) }
    }

    override fun audioSetMuted(muted: Boolean) {
        try { jnaInstance.audioSetMuted(muted) }
        catch (t: Throwable) { Log.w("KenjinxNative", "audioSetMuted unavailable", t) }
    }
    // ----------------------------------------------------

    @RequiresApi(Build.VERSION_CODES.R)
    @JvmStatic
    fun frameEnded() = MainActivity.frameEnded()

    @JvmStatic
    fun test() { /* no-op */ }

    @JvmStatic
    fun getSurfacePtr(): Long = MainActivity.mainViewModel?.gameHost?.currentSurface ?: -1

    @JvmStatic
    fun getWindowHandle(): Long =
        MainActivity.mainViewModel?.gameHost?.currentWindowHandle ?: -1

    @JvmStatic
    fun updateProgress(infoPtr: Long, progress: Float) {
        // Get string from native pointer and push into progress overlay
        val text = NativeHelpers.instance.getStringJava(infoPtr)
        MainActivity.mainViewModel?.gameHost?.setProgress(text, progress)
    }

    @JvmStatic
    fun onSurfaceSizeChanged(width: Int, height: Int) {
        // No-Op: Placeholder – Hook if needed.
    }

    @JvmStatic
    fun setSurfaceRotationByAndroidRotation(androidRotation: Int?) {
        val degrees = when (androidRotation) {
            Surface.ROTATION_0   -> 0
            Surface.ROTATION_90  -> 90
            Surface.ROTATION_180 -> 180
            Surface.ROTATION_270 -> 270
            else -> 0
        }
        try { deviceSetSurfaceRotation(degrees) } catch (_: Throwable) {}
    }

    @JvmStatic
    fun resizeRendererAndInput(width: Int, height: Int) {
        try {
            // Alternatively: deviceResize(width, height)
            graphicsRendererSetSize(width, height)
            inputSetClientSize(width, height)
        } catch (_: Throwable) {}
    }

    override fun detachWindow() {
        try { graphicsSetPresentEnabled(false) } catch (_: Throwable) {}
        try { deviceWaitForGpuDone(100) } catch (_: Throwable) {}
        try { jnaInstance.detachWindow() } catch (_: Throwable) {}
        try { deviceSetWindowHandle(0) } catch (_: Throwable) {}
    }

    @JvmStatic
    fun reattachWindowIfReady(): Boolean {
        return try {
            val handle = getWindowHandle()
            if (handle <= 0) return false
            deviceSetWindowHandle(handle)
            deviceRecreateSwapchain()
            true
        } catch (_: Throwable) {
            false
        }
    }

    /**
     * Variant A (Pointer → Strings via NativeHelpers).
     * Used by older JNI/Interop paths.
     */
    @JvmStatic
    fun updateUiHandler(
        newTitlePointer: Long,
        newMessagePointer: Long,
        newWatermarkPointer: Long,
        newType: Int,
        min: Int,
        max: Int,
        nMode: Int,
        newSubtitlePointer: Long,
        newInitialTextPointer: Long
    ) {
        val title = NativeHelpers.instance.getStringJava(newTitlePointer)
        val message = NativeHelpers.instance.getStringJava(newMessagePointer)
        val watermark = NativeHelpers.instance.getStringJava(newWatermarkPointer)
        val subtitle = NativeHelpers.instance.getStringJava(newSubtitlePointer)
        val initialText = NativeHelpers.instance.getStringJava(newInitialTextPointer)
        val mode = KeyboardMode.entries.getOrNull(nMode) ?: KeyboardMode.Default

        MainActivity.mainViewModel?.activity?.uiHandler?.update(
            newTitle = title,
            newMessage = message,
            newWatermark = watermark,
            newType = newType,
            min = min,
            max = max,
            newMode = mode,
            newSubtitle = subtitle,
            newInitialText = initialText
        )
    }

    /**
     * Variant B (strings directly). Used by newer JNI/interop paths.
     * Signature exactly matches the C# call in AndroidUIHandler.cs / Interop.UpdateUiHandler(...).
     */
    @JvmStatic
    fun uiHandlerUpdate(
        title: String,
        message: String,
        watermark: String,
        type: Int,
        min: Int,
        max: Int,
        nMode: Int,
        subtitle: String,
        initialText: String
    ) {
        val mode = KeyboardMode.entries.getOrNull(nMode) ?: KeyboardMode.Default
        MainActivity.mainViewModel?.activity?.uiHandler?.update(
            newTitle = title,
            newMessage = message,
            newWatermark = watermark,
            newType = type,
            min = min,
            max = max,
            newMode = mode,
            newSubtitle = subtitle,
            newInitialText = initialText
        )
    }
}
