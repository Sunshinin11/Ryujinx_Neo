package org.kenjinx.android.viewmodels

import android.app.Activity
import android.content.SharedPreferences
import android.content.pm.ActivityInfo
import androidx.core.content.edit
import androidx.preference.PreferenceManager

class QuickSettings(val activity: Activity) {
    // --- Alignment
    enum class OrientationPreference(val value: Int) {
        Sensor(ActivityInfo.SCREEN_ORIENTATION_SENSOR),
        SensorLandscape(ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE),
        SensorPortrait(ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT);

        companion object {
            fun fromValue(v: Int): OrientationPreference =
                entries.firstOrNull { it.value == v } ?: Sensor
        }
    }

    // --- Overlay Position
    enum class OverlayMenuPosition {
        BottomMiddle, BottomLeft, BottomRight, TopMiddle, TopLeft, TopRight
    }

    // --- Virtual Controller Preset
    enum class VirtualControllerPreset {
        Default, Layout2, Layout3, Layout4, Layout5, Layout6
    }

    var virtualControllerPreset: VirtualControllerPreset

    var orientationPreference: OrientationPreference

    // --- Overlay Settings
    var overlayMenuPosition: OverlayMenuPosition
    var overlayMenuOpacity: Float

    var ignoreMissingServices: Boolean
    var enablePptc: Boolean
    var enableLowPowerPptc: Boolean
    var enableJitCacheEviction: Boolean
    var enableFsIntegrityChecks: Boolean
    var fsGlobalAccessLogMode: Int
    var enableDocked: Boolean
    var vSyncMode: VSyncMode
    var useNce: Boolean
    var memoryConfiguration: MemoryConfiguration
    var memorySlack: MemorySlack
    var useVirtualController: Boolean
    // Amiibo slots (URIs + names)
    var amiibo1Uri: String?
    var amiibo1Name: String?
    var amiibo2Uri: String?
    var amiibo2Name: String?
    var amiibo3Uri: String?
    var amiibo3Name: String?
    var amiibo4Uri: String?
    var amiibo4Name: String?
    var amiibo5Uri: String?
    var amiibo5Name: String?

    var memoryManagerMode: MemoryManagerMode
    var enableShaderCache: Boolean
    var enableTextureRecompression: Boolean
    var enableMacroHLE: Boolean
    var stretchToFullscreen: Boolean
    var resScale: Float
    var maxAnisotropy: Float
    var isGrid: Boolean
    var useSwitchLayout: Boolean
    var enableMotion: Boolean
    var enablePerformanceMode: Boolean
    var controllerStickSensitivity: Float

    // --- Controller Scale (0.5f..1.5f, Default 1.0f)
    var controllerScale: Float

    var enableStubLogs: Boolean
    var enableInfoLogs: Boolean
    var enableWarningLogs: Boolean
    var enableErrorLogs: Boolean
    var enableGuestLogs: Boolean
    var enableFsAccessLogs: Boolean
    var enableTraceLogs: Boolean
    var enableDebugLogs: Boolean
    var enableGraphicsLogs: Boolean

    // --- Threaded Rendering Toggle (persists)
    var disableThreadedRendering: Boolean

    private var sharedPref: SharedPreferences = PreferenceManager.getDefaultSharedPreferences(activity)

    init {
        // Load Amiibo slots
        amiibo1Uri = sharedPref.getString("amiibo1Uri", null)
        amiibo1Name = sharedPref.getString("amiibo1Name", null)
        amiibo2Uri = sharedPref.getString("amiibo2Uri", null)
        amiibo2Name = sharedPref.getString("amiibo2Name", null)
        amiibo3Uri = sharedPref.getString("amiibo3Uri", null)
        amiibo3Name = sharedPref.getString("amiibo3Name", null)
        amiibo4Uri = sharedPref.getString("amiibo4Uri", null)
        amiibo4Name = sharedPref.getString("amiibo4Name", null)
        amiibo5Uri = sharedPref.getString("amiibo5Uri", null)
        amiibo5Name = sharedPref.getString("amiibo5Name", null)

        // --- Load alignment (Default: Sensor)
        val oriValue = sharedPref.getInt("orientationPreference", ActivityInfo.SCREEN_ORIENTATION_SENSOR)
        orientationPreference = OrientationPreference.fromValue(oriValue)

        // --- Overlay Settings laden
        overlayMenuPosition = OverlayMenuPosition.entries[
            sharedPref.getInt("overlayMenuPosition", OverlayMenuPosition.BottomMiddle.ordinal)
        ]
        overlayMenuOpacity = sharedPref.getFloat("overlayMenuOpacity", 1f).coerceIn(0f, 1f)

        memoryManagerMode = MemoryManagerMode.entries.toTypedArray()[sharedPref.getInt("memoryManagerMode", MemoryManagerMode.HostMappedUnsafe.ordinal)]
        useNce = sharedPref.getBoolean("useNce", false)
        memoryConfiguration = MemoryConfiguration.entries.toTypedArray()[sharedPref.getInt("memoryConfiguration", MemoryConfiguration.MemoryConfiguration4GiB.ordinal)]
        memorySlack = MemorySlack.entries.toTypedArray()[sharedPref.getInt("memorySlack", MemorySlack.Slack12GiB.ordinal)]
        vSyncMode = VSyncMode.entries.toTypedArray()[sharedPref.getInt("vSyncMode", VSyncMode.Switch.ordinal)]
        enableDocked = sharedPref.getBoolean("enableDocked", true)
        enablePptc = sharedPref.getBoolean("enablePptc", true)
        enableLowPowerPptc = sharedPref.getBoolean("enableLowPowerPptc", false)
        enableJitCacheEviction = sharedPref.getBoolean("enableJitCacheEviction", true)
        enableFsIntegrityChecks = sharedPref.getBoolean("enableFsIntegrityChecks", false)
        fsGlobalAccessLogMode = sharedPref.getInt("fsGlobalAccessLogMode", 0)
        ignoreMissingServices = sharedPref.getBoolean("ignoreMissingServices", false)
        enableShaderCache = sharedPref.getBoolean("enableShaderCache", true)
        enableTextureRecompression = sharedPref.getBoolean("enableTextureRecompression", false)
        enableMacroHLE = sharedPref.getBoolean("enableMacroHLE", true)
        stretchToFullscreen = sharedPref.getBoolean("stretchToFullscreen", false)
        resScale = sharedPref.getFloat("resScale", 1f)
        maxAnisotropy = sharedPref.getFloat("maxAnisotropy", 0f)
        useVirtualController = sharedPref.getBoolean("useVirtualController", true)
        virtualControllerPreset = VirtualControllerPreset.entries[
            sharedPref.getInt("virtualControllerPreset", VirtualControllerPreset.Default.ordinal)
        ]
        isGrid = sharedPref.getBoolean("isGrid", true)
        useSwitchLayout = sharedPref.getBoolean("useSwitchLayout", true)
        enableMotion = sharedPref.getBoolean("enableMotion", true)
        enablePerformanceMode = sharedPref.getBoolean("enablePerformanceMode", true)
        controllerStickSensitivity = sharedPref.getFloat("controllerStickSensitivity", 1.0f)

        // --- Reload: Controller Scale
        controllerScale = sharedPref.getFloat("controllerScale", 1.0f).coerceIn(0.5f, 1.5f)

        enableStubLogs = sharedPref.getBoolean("enableStubLogs", false)
        enableInfoLogs = sharedPref.getBoolean("enableInfoLogs", true)
        enableWarningLogs = sharedPref.getBoolean("enableWarningLogs", true)
        enableErrorLogs = sharedPref.getBoolean("enableErrorLogs", true)
        enableGuestLogs = sharedPref.getBoolean("enableGuestLogs", true)
        enableFsAccessLogs = sharedPref.getBoolean("enableFsAccessLogs", false)
        enableTraceLogs = sharedPref.getBoolean("enableStubLogs", false)
        enableDebugLogs = sharedPref.getBoolean("enableDebugLogs", false)
        enableGraphicsLogs = sharedPref.getBoolean("enableGraphicsLogs", false)

        // --- Reload
        disableThreadedRendering = sharedPref.getBoolean("disableThreadedRendering", false)
    }

    fun save() {
        sharedPref.edit {
            // Amiibo slots
            putString("amiibo1Uri", amiibo1Uri)
            putString("amiibo1Name", amiibo1Name)
            putString("amiibo2Uri", amiibo2Uri)
            putString("amiibo2Name", amiibo2Name)
            putString("amiibo3Uri", amiibo3Uri)
            putString("amiibo3Name", amiibo3Name)
            putString("amiibo4Uri", amiibo4Uri)
            putString("amiibo4Name", amiibo4Name)
            putString("amiibo5Uri", amiibo5Uri)
            putString("amiibo5Name", amiibo5Name)

            // --- Save orientation
            putInt("orientationPreference", orientationPreference.value)

            // --- Save overlay settings
            putInt("overlayMenuPosition", overlayMenuPosition.ordinal)
            putFloat("overlayMenuOpacity", overlayMenuOpacity.coerceIn(0f, 1f))

            putInt("memoryManagerMode", memoryManagerMode.ordinal)
            putBoolean("useNce", useNce)
            putInt("memoryConfiguration", memoryConfiguration.ordinal)
            putInt("memorySlack", memorySlack.ordinal)
            putInt("vSyncMode", vSyncMode.ordinal)
            putBoolean("enableDocked", enableDocked)
            putBoolean("enablePptc", enablePptc)
            putBoolean("enableLowPowerPptc", enableLowPowerPptc)
            putBoolean("enableJitCacheEviction", enableJitCacheEviction)
            putBoolean("enableFsIntegrityChecks", enableFsIntegrityChecks)
            putInt("fsGlobalAccessLogMode", fsGlobalAccessLogMode)
            putBoolean("ignoreMissingServices", ignoreMissingServices)
            putBoolean("enableShaderCache", enableShaderCache)
            putBoolean("enableTextureRecompression", enableTextureRecompression)
            putBoolean("enableMacroHLE", enableMacroHLE)
            putBoolean("stretchToFullscreen", stretchToFullscreen)
            putFloat("resScale", resScale)
            putFloat("maxAnisotropy", maxAnisotropy)
            putBoolean("useVirtualController", useVirtualController)
            putBoolean("isGrid", isGrid)
            putBoolean("useSwitchLayout", useSwitchLayout)
            putBoolean("enableMotion", enableMotion)
            putBoolean("enablePerformanceMode", enablePerformanceMode)
            putFloat("controllerStickSensitivity", controllerStickSensitivity)

            // --- Save Controller Scale
            putFloat("controllerScale", controllerScale.coerceIn(0.5f, 1.5f))

            putBoolean("enableStubLogs", enableStubLogs)
            putBoolean("enableInfoLogs", enableInfoLogs)
            putBoolean("enableWarningLogs", enableWarningLogs)
            putBoolean("enableErrorLogs", enableErrorLogs)
            putBoolean("enableGuestLogs", enableGuestLogs)
            putBoolean("enableFsAccessLogs", enableFsAccessLogs)
            putBoolean("enableTraceLogs", enableTraceLogs)
            putBoolean("enableDebugLogs", enableDebugLogs)
            putBoolean("enableGraphicsLogs", enableGraphicsLogs)
            putInt("virtualControllerPreset", virtualControllerPreset.ordinal)

            // --- Save
            putBoolean("disableThreadedRendering", disableThreadedRendering)
        }
    }

    fun overrideSettings(forceNceAndPptc: Boolean?)
    {
        if(forceNceAndPptc == true)
        {
            enablePptc = true
            useNce = true
        }
        else
        {
            enablePptc = false
            useNce = false
        }
    }
}
