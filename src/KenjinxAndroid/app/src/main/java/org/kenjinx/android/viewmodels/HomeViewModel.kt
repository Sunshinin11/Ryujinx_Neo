package org.kenjinx.android.viewmodels

import android.content.SharedPreferences
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.documentfile.provider.DocumentFile
import androidx.preference.PreferenceManager
import com.anggrayudi.storage.file.DocumentFileCompat
import com.anggrayudi.storage.file.DocumentFileType
import com.anggrayudi.storage.file.extension
import com.anggrayudi.storage.file.search
import kotlinx.coroutines.DelicateCoroutinesApi
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.GlobalScope
import kotlinx.coroutines.launch
import org.kenjinx.android.KenjinxNative
import org.kenjinx.android.MainActivity
import java.io.File
import java.util.Locale
import kotlin.concurrent.thread

class HomeViewModel(
    val activity: MainActivity? = null,
    val mainViewModel: MainViewModel? = null
) {
    private var shouldReload: Boolean = false
    private var savedFolder: String = ""
    private var loadedCache: MutableList<GameModel> = mutableListOf()
    private var gameFolderPath: DocumentFile? = null
    private var sharedPref: SharedPreferences? = null
    val gameList: SnapshotStateList<GameModel> = SnapshotStateList()
    val isLoading: MutableState<Boolean> = mutableStateOf(false)

    init {
        if (activity != null) {
            sharedPref = PreferenceManager.getDefaultSharedPreferences(activity)
        }
    }

    fun ensureReloadIfNecessary() {
        val oldFolder = savedFolder
        savedFolder = sharedPref?.getString("gameFolder", "") ?: ""

        if (savedFolder.isNotEmpty() && (shouldReload || savedFolder != oldFolder)) {
            gameFolderPath = DocumentFileCompat.fromFullPath(
                mainViewModel?.activity!!,
                savedFolder,
                documentType = DocumentFileType.FOLDER,
                requiresWriteAccess = true
            )

            reloadGameList()
        }
    }

    fun filter(query: String) {
        gameList.clear()
        gameList.addAll(loadedCache.filter {
            it.titleName != null && it.titleName!!.isNotEmpty() && (query.trim()
                .isEmpty() || it.titleName!!.lowercase(Locale.getDefault())
                .contains(query))
        })
    }

    fun requestReload() {
        shouldReload = true
    }

    @OptIn(DelicateCoroutinesApi::class)
    private fun reloadGameList() {
        activity?.storageHelper ?: return
        val folder = gameFolderPath ?: return

        shouldReload = false
        if (isLoading.value)
            return

        gameList.clear()
        loadedCache.clear()
        isLoading.value = true

        thread {
            try {
                val validExtensions = setOf("xci", "nsp", "nro")
                val queue = mutableListOf(folder)

                while (queue.isNotEmpty()) {
                    val current = queue.removeAt(0)
                    current.listFiles().forEach { file ->
                        when {
                            file.isFile && file.extension?.lowercase() in validExtensions -> {
                                val game = GameModel(file, activity)

                                if(!game.isUnknown()) {
                                    loadedCache.add(game)
                                }
                            }
                            file.isDirectory -> queue.add(file)
                        }
                    }
                }

                loadedCache.sortWith { itemA, itemB ->
                    val strA = itemA.fileBaseName.toString().lowercase(Locale.ROOT)
                    val strB = itemB.fileBaseName.toString().lowercase(Locale.ROOT)
                    val minLength = minOf(strA.length, strB.length)

                    for (i in 0 until minLength) {
                        val charA = strA[i]
                        val charB = strB[i]

                        if (charA != charB) {
                            return@sortWith charA.code - charB.code
                        }
                    }

                    return@sortWith strA.length - strB.length
                }

                for (game in loadedCache) {
                    game.getGameInfo()
                }

                // Auto-load DLCs and Title Updates from configured directories (if any)
                try {
                    autoloadContent()
                } catch (_: Throwable) { }
            } finally {
                isLoading.value = false
                GlobalScope.launch(Dispatchers.Main){
                    filter("")
                }
            }
        }
    }

    // Helper function to compare update versions from filenames
    // Returns true if newPath represents a newer version than currentPath
    private fun shouldSelectNewerUpdate(currentPath: String, newPath: String): Boolean {
        // Extract version numbers from filenames using regex pattern [vXXXXXX]
        val versionPattern = Regex("\\[v(\\d+)]")

        val currentVersion = versionPattern.find(currentPath.lowercase(Locale.getDefault()))?.groupValues?.get(1)?.toIntOrNull() ?: 0
        val newVersion = versionPattern.find(newPath.lowercase(Locale.getDefault()))?.groupValues?.get(1)?.toIntOrNull() ?: 0

        return newVersion > currentVersion
    }

    // Scans configured directory for NSPs containing DLCs/Updates and associates them to known titles.
    private fun autoloadContent() {
        val prefs = sharedPref ?: return

        val updatesFolder = prefs.getString("updatesFolder", "") ?: ""

        if (updatesFolder.isEmpty()) return

        // Build a map of titleId -> helpers
        val gamesByTitle = loadedCache.mapNotNull { g ->
            val tid = g.titleId
            if (!tid.isNullOrBlank()) tid.lowercase(Locale.getDefault()) to tid else null
        }.toMap()

        var updatesAdded = 0
        var dlcAdded = 0

        val base = File(updatesFolder)
        if (!base.exists() || !base.isDirectory) return

        base.walkTopDown().forEach fileLoop@{ f ->
            if (!f.isFile) return@fileLoop
            val name = f.name.lowercase(Locale.getDefault())
            if (!name.endsWith(".nsp")) return@fileLoop

            // Extract title ID from filename
            val tidPattern = Regex("\\[([0-9a-fA-F]{16})]")
            val tidMatch = tidPattern.find(name) ?: return@fileLoop
            val fileTid = tidMatch.groupValues[1].lowercase(Locale.getDefault())

            // Try to find DLC content for all games
            var isDlc = false
            try {
                for ((_, tidOrig) in gamesByTitle) {
                    val contents = KenjinxNative.deviceGetDlcContentList(f.absolutePath, tidOrig.toLong(16))

                    if (contents.isNotEmpty()) {
                        isDlc = true
                        val containerPath = f.absolutePath
                        val vm = DlcViewModel(tidOrig)
                        val already = vm.data?.any { it.path == containerPath } == true

                        if (!already) {
                            val container = DlcContainerList(containerPath)
                            for (content in contents) {
                                container.dlc_nca_list.add(
                                    DlcContainer(
                                        true,
                                        KenjinxNative.deviceGetDlcTitleId(containerPath, content).toLong(16),
                                        content
                                    )
                                )
                            }
                            vm.data?.add(container)
                            vm.saveChanges()
                            dlcAdded++
                        }
                        break
                    }
                }
            } catch (_: Throwable) { }

            if (isDlc) return@fileLoop

            // Treat as Title Update - convert update ID to base ID
            // Update title IDs end in 800, base game IDs end in 000
            val baseTid = if (fileTid.endsWith("800")) {
                fileTid.substring(0, fileTid.length - 3) + "000"
            } else {
                fileTid
            }

            val originalTid = gamesByTitle[baseTid]
            if (originalTid != null) {
                val vm = TitleUpdateViewModel(originalTid)
                val path = f.absolutePath
                val exists = (vm.data?.paths?.contains(path) == true)

                if (!exists) {
                    // Add the new update path
                    vm.data?.paths?.add(path)

                    // Auto-select this update if it's newer than the currently selected one
                    // or if no update is currently selected
                    val currentSelected = vm.data?.selected ?: ""
                    val shouldSelect = currentSelected.isEmpty() ||
                        shouldSelectNewerUpdate(currentSelected, path)

                    if (shouldSelect) {
                        vm.data?.selected = path
                    }

                    vm.saveChanges()
                    updatesAdded++
                }
            }
        }
    }
}
