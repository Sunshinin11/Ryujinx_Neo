package org.kenjinx.android.viewmodels

import android.content.SharedPreferences
import androidx.compose.runtime.MutableState
import androidx.documentfile.provider.DocumentFile
import androidx.preference.PreferenceManager
import com.anggrayudi.storage.file.FileFullPath
import com.anggrayudi.storage.file.extension
import com.anggrayudi.storage.file.getAbsolutePath
import com.anggrayudi.storage.file.openInputStream
import net.lingala.zip4j.io.inputstream.ZipInputStream
import org.kenjinx.android.LogLevel
import org.kenjinx.android.MainActivity
import org.kenjinx.android.KenjinxNative
import java.io.BufferedOutputStream
import java.io.File
import java.io.FileOutputStream
import kotlin.concurrent.thread
import androidx.core.content.edit

// Import enums
import org.kenjinx.android.SystemLanguage
import org.kenjinx.android.RegionCode

class SettingsViewModel(val activity: MainActivity) {
    var selectedFirmwareVersion: String = ""
    private var previousFileCallback: ((requestCode: Int, files: List<DocumentFile>) -> Unit)?
    private var previousFolderCallback: ((requestCode: Int, folder: DocumentFile) -> Unit)?
    private var sharedPref: SharedPreferences
    var selectedKeyFile: DocumentFile? = null
    var selectedFirmwareFile: DocumentFile? = null

    init {
        sharedPref = getPreferences()
        previousFolderCallback = activity.storageHelper!!.onFolderSelected
        previousFileCallback = activity.storageHelper!!.onFileSelected
    }

    private fun getPreferences(): SharedPreferences {
        return PreferenceManager.getDefaultSharedPreferences(activity)
    }

    fun initializeState(
        memoryManagerMode: MutableState<MemoryManagerMode>,
        useNce: MutableState<Boolean>,
        memoryConfiguration: MutableState<MemoryConfiguration>,
        memorySlackEnabled: MutableState<Boolean>,
        vSyncMode: MutableState<VSyncMode>,
        enableDocked: MutableState<Boolean>,
        enablePptc: MutableState<Boolean>,
        enableLowPowerPptc: MutableState<Boolean>,
        enableJitCacheEviction: MutableState<Boolean>,
        enableFsIntegrityChecks: MutableState<Boolean>,
        fsGlobalAccessLogMode: MutableState<Int>,
        ignoreMissingServices: MutableState<Boolean>,
        enableShaderCache: MutableState<Boolean>,
        enableShaderLoopSafety: MutableState<Boolean>,
        enableTextureRecompression: MutableState<Boolean>,
        enableVtgViaCompute: MutableState<Boolean>,
        drawFlushMode: MutableState<Int>,
        bufferReadbackMode: MutableState<Int>,
        strictSyncMode: MutableState<Int>,
        enableMacroHLE: MutableState<Boolean>,
        stretchToFullscreen: MutableState<Boolean>,
        resScale: MutableState<Float>,
        maxAnisotropy: MutableState<Float>,
        useVirtualController: MutableState<Boolean>,
        isGrid: MutableState<Boolean>,
        useSwitchLayout: MutableState<Boolean>,
        enableMotion: MutableState<Boolean>,
        enablePerformanceMode: MutableState<Boolean>,
        controllerStickSensitivity: MutableState<Float>,
        enableStubLogs: MutableState<Boolean>,
        enableInfoLogs: MutableState<Boolean>,
        enableWarningLogs: MutableState<Boolean>,
        enableErrorLogs: MutableState<Boolean>,
        enableGuestLogs: MutableState<Boolean>,
        enableFsAccessLogs: MutableState<Boolean>,
        enableTraceLogs: MutableState<Boolean>,
        enableDebugLogs: MutableState<Boolean>,
        enableGraphicsLogs: MutableState<Boolean>,
        showPerformanceOverlay: MutableState<Boolean>,
        virtualControllerPreset: MutableState<QuickSettings.VirtualControllerPreset>,
        systemLanguage: MutableState<SystemLanguage>,
        regionCode: MutableState<RegionCode>
    ) {
        memoryManagerMode.value = MemoryManagerMode.entries.toTypedArray()[sharedPref.getInt("memoryManagerMode", MemoryManagerMode.HostMappedUnsafe.ordinal)]
        useNce.value = sharedPref.getBoolean("useNce", false)
        memoryConfiguration.value = MemoryConfiguration.entries.toTypedArray()[sharedPref.getInt("memoryConfiguration", MemoryConfiguration.MemoryConfiguration4GiB.ordinal)]
        memorySlackEnabled.value = if (sharedPref.contains("memorySlackEnabled")) {
            sharedPref.getBoolean("memorySlackEnabled", true)
        } else {
            sharedPref.getInt("memorySlack", 3) != 0
        }
        vSyncMode.value = VSyncMode.entries.toTypedArray()[sharedPref.getInt("vSyncMode", VSyncMode.Switch.ordinal)]
        enableDocked.value = sharedPref.getBoolean("enableDocked", true)
        enablePptc.value = sharedPref.getBoolean("enablePptc", true)
        enableLowPowerPptc.value = sharedPref.getBoolean("enableLowPowerPptc", false)
        enableJitCacheEviction.value = sharedPref.getBoolean("enableJitCacheEviction", false)
        enableFsIntegrityChecks.value = sharedPref.getBoolean("enableFsIntegrityChecks", false)
        fsGlobalAccessLogMode.value = sharedPref.getInt("fsGlobalAccessLogMode", 0)
        ignoreMissingServices.value = sharedPref.getBoolean("ignoreMissingServices", false)
        enableShaderCache.value = sharedPref.getBoolean("enableShaderCache", true)
        enableShaderLoopSafety.value = sharedPref.getBoolean("enableShaderLoopSafety", false)
        enableTextureRecompression.value = sharedPref.getBoolean("enableTextureRecompression", false)
        enableVtgViaCompute.value = sharedPref.getBoolean("enableVtgViaCompute", true)
        drawFlushMode.value = sharedPref.getInt("drawFlushMode", 0)
        bufferReadbackMode.value = sharedPref.getInt("bufferReadbackMode", 0)
        strictSyncMode.value = sharedPref.getInt("strictSyncMode", 0)
        enableMacroHLE.value = sharedPref.getBoolean("enableMacroHLE", false)
        stretchToFullscreen.value = sharedPref.getBoolean("stretchToFullscreen", false)
        resScale.value = sharedPref.getFloat("resScale", 1f)
        maxAnisotropy.value = sharedPref.getFloat("maxAnisotropy", 0f)
        useVirtualController.value = sharedPref.getBoolean("useVirtualController", true)
        isGrid.value = sharedPref.getBoolean("isGrid", true)
        useSwitchLayout.value = sharedPref.getBoolean("useSwitchLayout", false)
        enableMotion.value = sharedPref.getBoolean("enableMotion", true)
        enablePerformanceMode.value = sharedPref.getBoolean("enablePerformanceMode", false)
        controllerStickSensitivity.value = sharedPref.getFloat("controllerStickSensitivity", 1.0f)
        enableStubLogs.value = sharedPref.getBoolean("enableStubLogs", false)
        enableInfoLogs.value = sharedPref.getBoolean("enableInfoLogs", false)
        enableWarningLogs.value = sharedPref.getBoolean("enableWarningLogs", false)
        enableErrorLogs.value = sharedPref.getBoolean("enableErrorLogs", false)
        enableGuestLogs.value = sharedPref.getBoolean("enableGuestLogs", false)
        enableFsAccessLogs.value = sharedPref.getBoolean("enableFsAccessLogs", false)
        enableTraceLogs.value = sharedPref.getBoolean("enableTraceLogs", false)
        enableDebugLogs.value = sharedPref.getBoolean("enableDebugLogs", false)
        enableGraphicsLogs.value = sharedPref.getBoolean("enableGraphicsLogs", false)

        showPerformanceOverlay.value = sharedPref.getBoolean("showPerformanceOverlay", false)
        virtualControllerPreset.value = QuickSettings.VirtualControllerPreset.entries.getOrElse(
            sharedPref.getInt("virtualControllerPreset", QuickSettings.VirtualControllerPreset.Default.ordinal)
        ) { QuickSettings.VirtualControllerPreset.Default }

        // Load language/region (strings, fallback to defaults, then .valueOf)
        val langName = sharedPref.getString("system_language", "AmericanEnglish") ?: "AmericanEnglish"
        val regionName = sharedPref.getString("region_code", "USA") ?: "USA"
        systemLanguage.value = runCatching { SystemLanguage.valueOf(langName) }.getOrElse { SystemLanguage.AmericanEnglish }
        regionCode.value = runCatching { RegionCode.valueOf(regionName) }.getOrElse { RegionCode.USA }
    }

    fun save(
        memoryManagerMode: MutableState<MemoryManagerMode>,
        useNce: MutableState<Boolean>,
        memoryConfiguration: MutableState<MemoryConfiguration>,
        memorySlackEnabled: MutableState<Boolean>,
        vSyncMode: MutableState<VSyncMode>,
        enableDocked: MutableState<Boolean>,
        enablePptc: MutableState<Boolean>,
        enableLowPowerPptc: MutableState<Boolean>,
        enableJitCacheEviction: MutableState<Boolean>,
        enableFsIntegrityChecks: MutableState<Boolean>,
        fsGlobalAccessLogMode: MutableState<Int>,
        ignoreMissingServices: MutableState<Boolean>,
        enableShaderCache: MutableState<Boolean>,
        enableShaderLoopSafety: MutableState<Boolean>,
        enableTextureRecompression: MutableState<Boolean>,
        enableVtgViaCompute: MutableState<Boolean>,
        drawFlushMode: MutableState<Int>,
        bufferReadbackMode: MutableState<Int>,
        strictSyncMode: MutableState<Int>,
        enableMacroHLE: MutableState<Boolean>,
        stretchToFullscreen: MutableState<Boolean>,
        resScale: MutableState<Float>,
        maxAnisotropy: MutableState<Float>,
        useVirtualController: MutableState<Boolean>,
        isGrid: MutableState<Boolean>,
        useSwitchLayout: MutableState<Boolean>,
        enableMotion: MutableState<Boolean>,
        enablePerformanceMode: MutableState<Boolean>,
        controllerStickSensitivity: MutableState<Float>,
        enableStubLogs: MutableState<Boolean>,
        enableInfoLogs: MutableState<Boolean>,
        enableWarningLogs: MutableState<Boolean>,
        enableErrorLogs: MutableState<Boolean>,
        enableGuestLogs: MutableState<Boolean>,
        enableFsAccessLogs: MutableState<Boolean>,
        enableTraceLogs: MutableState<Boolean>,
        enableDebugLogs: MutableState<Boolean>,
        enableGraphicsLogs: MutableState<Boolean>,
        showPerformanceOverlay: MutableState<Boolean>,
        virtualControllerPreset: MutableState<QuickSettings.VirtualControllerPreset>,
        systemLanguage: MutableState<SystemLanguage>,
        regionCode: MutableState<RegionCode>
    ) {
        sharedPref.edit {

            putInt("memoryManagerMode", memoryManagerMode.value.ordinal)
            putBoolean("useNce", useNce.value)
            putInt("memoryConfiguration", memoryConfiguration.value.ordinal)
            putBoolean("memorySlackEnabled", memorySlackEnabled.value)
            remove("memorySlack")
            putInt("vSyncMode", vSyncMode.value.ordinal)
            putBoolean("enableDocked", enableDocked.value)
            putBoolean("enablePptc", enablePptc.value)
            putBoolean("enableLowPowerPptc", enableLowPowerPptc.value)
            putBoolean("enableJitCacheEviction", enableJitCacheEviction.value)
            putBoolean("enableFsIntegrityChecks", enableFsIntegrityChecks.value)
            putInt("fsGlobalAccessLogMode", fsGlobalAccessLogMode.value)
            putBoolean("ignoreMissingServices", ignoreMissingServices.value)
            putBoolean("enableShaderCache", enableShaderCache.value)
            putBoolean("enableShaderLoopSafety", enableShaderLoopSafety.value)
            putBoolean("enableTextureRecompression", enableTextureRecompression.value)
            putBoolean("enableVtgViaCompute", enableVtgViaCompute.value)
            putInt("drawFlushMode", drawFlushMode.value)
            putInt("bufferReadbackMode", bufferReadbackMode.value)
            putInt("strictSyncMode", strictSyncMode.value)
            putBoolean("enableMacroHLE", enableMacroHLE.value)
            putBoolean("stretchToFullscreen", stretchToFullscreen.value)
            putFloat("resScale", resScale.value)
            putFloat("maxAnisotropy", maxAnisotropy.value)
            putBoolean("useVirtualController", useVirtualController.value)
            putBoolean("isGrid", isGrid.value)
            putBoolean("useSwitchLayout", useSwitchLayout.value)
            putBoolean("enableMotion", enableMotion.value)
            putBoolean("enablePerformanceMode", enablePerformanceMode.value)
            putFloat("controllerStickSensitivity", controllerStickSensitivity.value)
            putBoolean("enableStubLogs", enableStubLogs.value)
            putBoolean("enableInfoLogs", enableInfoLogs.value)
            putBoolean("enableWarningLogs", enableWarningLogs.value)
            putBoolean("enableErrorLogs", enableErrorLogs.value)
            putBoolean("enableGuestLogs", enableGuestLogs.value)
            putBoolean("enableFsAccessLogs", enableFsAccessLogs.value)
            putBoolean("enableTraceLogs", enableTraceLogs.value)
            putBoolean("enableDebugLogs", enableDebugLogs.value)
            putBoolean("enableGraphicsLogs", enableGraphicsLogs.value)

            putBoolean("showPerformanceOverlay", showPerformanceOverlay.value)
            putInt("virtualControllerPreset", virtualControllerPreset.value.ordinal)

            // Save language/region as string (enum name)
            putString("system_language", systemLanguage.value.name)
            putString("region_code", regionCode.value.name)
        }
        activity.storageHelper!!.onFolderSelected = previousFolderCallback

        KenjinxNative.loggingSetEnabled(LogLevel.Info, enableInfoLogs.value)
        KenjinxNative.loggingSetEnabled(LogLevel.Stub, enableStubLogs.value)
        KenjinxNative.loggingSetEnabled(LogLevel.Warning, enableWarningLogs.value)
        KenjinxNative.loggingSetEnabled(LogLevel.Error, enableErrorLogs.value)
        KenjinxNative.loggingSetEnabled(LogLevel.AccessLog, enableFsAccessLogs.value)
        KenjinxNative.loggingSetEnabled(LogLevel.Guest, enableGuestLogs.value)
        KenjinxNative.loggingSetEnabled(LogLevel.Trace, enableTraceLogs.value)
        KenjinxNative.loggingSetEnabled(LogLevel.Debug, enableDebugLogs.value)
        KenjinxNative.loggingEnabledGraphicsLog(enableGraphicsLogs.value)
    }

    fun openGameFolder() {
        val path = sharedPref.getString("gameFolder", "") ?: ""

        activity.storageHelper!!.onFolderSelected = { _, folder ->
            val p = folder.getAbsolutePath(activity)
            sharedPref.edit {
                putString("gameFolder", p)
            }
            runCatching {
                MainActivity.mainViewModel?.defaultGameFolderUri = folder.uri
            }
            activity.storageHelper!!.onFolderSelected = previousFolderCallback
        }

        if (path.isEmpty())
            activity.storageHelper?.storage?.openFolderPicker()
        else
            activity.storageHelper?.storage?.openFolderPicker(
                activity.storageHelper!!.storage.requestCodeFolderPicker,
                FileFullPath(activity, path)
            )
    }

    fun openUpdatesFolder() {
        val path = sharedPref.getString("updatesFolder", "") ?: ""

        activity.storageHelper!!.onFolderSelected = { _, folder ->
            val p = folder.getAbsolutePath(activity)
            sharedPref.edit {
                putString("updatesFolder", p)
            }
            activity.storageHelper!!.onFolderSelected = previousFolderCallback
            
            // Trigger a reload of the game list to apply the new updates/DLC folder
            MainActivity.mainViewModel?.homeViewModel?.requestReload()
            MainActivity.mainViewModel?.homeViewModel?.ensureReloadIfNecessary()
        }

        if (path.isEmpty())
            activity.storageHelper?.storage?.openFolderPicker()
        else
            activity.storageHelper?.storage?.openFolderPicker(
                activity.storageHelper!!.storage.requestCodeFolderPicker,
                FileFullPath(activity, path)
            )
    }

    fun selectKey(installState: MutableState<KeyInstallState>) {
        if (installState.value != KeyInstallState.File)
            return

        activity.storageHelper!!.onFileSelected = { _, files ->
            val file = files.firstOrNull()
            file?.apply {
                if (name == "prod.keys") {
                    selectedKeyFile = file
                    installState.value = KeyInstallState.Query
                } else {
                    installState.value = KeyInstallState.Cancelled
                }
            }
            activity.storageHelper!!.onFileSelected = previousFileCallback
        }
        activity.storageHelper?.storage?.openFilePicker()
    }

    fun installKey(installState: MutableState<KeyInstallState>) {
        if (installState.value != KeyInstallState.Query)
            return
        if (selectedKeyFile == null) {
            installState.value = KeyInstallState.File
            return
        }
        selectedKeyFile?.apply {
            val outputFolder = File(MainActivity.AppPath + "/system")
            val outputFile = File(MainActivity.AppPath + "/system/" + name)
            outputFile.delete()
            installState.value = KeyInstallState.Install
            thread {
                Thread.sleep(1000)
                try {
                    this.openInputStream(activity)?.use { input ->
                        outputFile.outputStream().use { output ->
                            input.copyTo(output)
                        }
                    }

                    KenjinxNative.deviceReloadFilesystem()
                    installState.value = KeyInstallState.Done
                } catch (e: Exception) {
                    installState.value = KeyInstallState.Cancelled
                }
            }
        }
    }

    fun clearKeySelection(installState: MutableState<KeyInstallState>) {
        selectedKeyFile = null
        installState.value = KeyInstallState.File
    }

    fun selectFirmware(installState: MutableState<FirmwareInstallState>) {
        if (installState.value != FirmwareInstallState.File)
            return

        activity.storageHelper!!.onFileSelected = { _, files ->
            val file = files.firstOrNull()
            file?.apply {
                if (extension == "xci" || extension == "zip") {
                    installState.value = FirmwareInstallState.Verifying
                    thread {
                        val descriptor = activity.contentResolver.openFileDescriptor(file.uri, "rw")
                        descriptor?.use { d ->
                            selectedFirmwareVersion = KenjinxNative.deviceVerifyFirmware(d.fd, extension == "xci")
                            selectedFirmwareFile = file
                            if (!selectedFirmwareVersion.isEmpty()) {
                                installState.value = FirmwareInstallState.Query
                            } else {
                                installState.value = FirmwareInstallState.Cancelled
                            }
                        }
                    }
                } else {
                    installState.value = FirmwareInstallState.Cancelled
                }
            }
            activity.storageHelper!!.onFileSelected = previousFileCallback
        }
        activity.storageHelper?.storage?.openFilePicker()
    }

    fun installFirmware(installState: MutableState<FirmwareInstallState>) {
        if (installState.value != FirmwareInstallState.Query)
            return
        if (selectedFirmwareFile == null) {
            installState.value = FirmwareInstallState.File
            return
        }
        selectedFirmwareFile?.apply {
            val descriptor = activity.contentResolver.openFileDescriptor(uri, "rw")

            if(descriptor != null)
            {
                installState.value = FirmwareInstallState.Install
                thread {
                    Thread.sleep(1000)

                    try {
                        KenjinxNative.deviceInstallFirmware(descriptor.fd,extension == "xci")
                    } finally {
                        MainActivity.mainViewModel?.refreshFirmwareVersion()
                        installState.value = FirmwareInstallState.Done
                        descriptor.close()
                    }
                }
            }
        }
    }

    fun clearFirmwareSelection(installState: MutableState<FirmwareInstallState>) {
        selectedFirmwareFile = null
        selectedFirmwareVersion = ""
        installState.value = FirmwareInstallState.File
    }

    fun resetAppData(
        dataResetState: MutableState<DataResetState>
    ) {
        dataResetState.value = DataResetState.Reset
        thread {
            Thread.sleep(1000)

            try {
                MainActivity.StorageHelper?.apply {
                    val folders = listOf("bis", "games", "profiles", "system")
                    for (f in folders) {
                        val dir = File(MainActivity.AppPath + "${File.separator}${f}")
                        if (dir.exists()) {
                            dir.deleteRecursively()
                        }

                        dir.mkdirs()
                    }
                }
            } finally {
                dataResetState.value = DataResetState.Done
                KenjinxNative.deviceReloadFilesystem()
                MainActivity.mainViewModel?.refreshFirmwareVersion()
            }
        }
    }

    fun importAppData(
        file: DocumentFile,
        dataImportState: MutableState<DataImportState>
    ) {
        dataImportState.value = DataImportState.Import
        thread {
            Thread.sleep(1000)

            try {
                MainActivity.StorageHelper?.apply {
                    val stream = file.openInputStream(storage.context)
                    stream?.apply {
                        val folders = listOf("bis", "games", "profiles", "system")
                        for (f in folders) {
                            val dir = File(MainActivity.AppPath + "${File.separator}${f}")
                            if (dir.exists()) {
                                dir.deleteRecursively()
                            }

                            dir.mkdirs()
                        }
                        ZipInputStream(stream).use { zip ->
                            while (true) {
                                val header = zip.nextEntry ?: break
                                if (!folders.any { header.fileName.startsWith(it) }) {
                                    continue
                                }
                                val filePath =
                                    MainActivity.AppPath + File.separator + header.fileName

                                if (!header.isDirectory) {
                                    val bos = BufferedOutputStream(FileOutputStream(filePath))
                                    val bytesIn = ByteArray(4096)
                                    var read = 0
                                    while (zip.read(bytesIn).also { read = it } > 0) {
                                        bos.write(bytesIn, 0, read)
                                    }
                                    bos.close()
                                } else {
                                    val dir = File(filePath)
                                    dir.mkdir()
                                }
                            }
                        }
                        stream.close()
                    }
                }
            } finally {
                dataImportState.value = DataImportState.Done
                KenjinxNative.deviceReloadFilesystem()
                MainActivity.mainViewModel?.refreshFirmwareVersion()
            }
        }
    }
}

enum class KeyInstallState {
    File,
    Cancelled,
    Query,
    Install,
    Done
}

enum class FirmwareInstallState {
    File,
    Cancelled,
    Verifying,
    Query,
    Install,
    Done
}

enum class DataResetState {
    Query,
    Reset,
    Done
}

enum class DataImportState {
    File,
    Query,
    Import,
    Done
}
