package org.kenjinx.android.viewmodels

import android.content.Intent
import android.net.Uri
import android.os.Environment
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.text.intl.Locale
import androidx.compose.ui.text.toLowerCase
import com.anggrayudi.storage.SimpleStorageHelper
import com.anggrayudi.storage.file.extension
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import org.kenjinx.android.MainActivity
import org.kenjinx.android.KenjinxNative
import java.io.File

class DlcViewModel(val titleId: String) {
    private var canClose: MutableState<Boolean>? = null
    private var storageHelper: SimpleStorageHelper
    private var dlcItemsState: SnapshotStateList<DlcItem>? = null

    companion object {
        const val UpdateRequestCode = 1002
    }

    fun remove(item: DlcItem) {
        data?.apply {
            this.removeAll { it.path == item.containerPath }
            refreshDlcItems()
            saveChanges()

            canClose?.let {
                it.value = false
                it.value = true
            }
        }
    }

    fun add() {
        val callBack = storageHelper.onFileSelected

        storageHelper.onFileSelected = { requestCode, files ->
            run {
                storageHelper.onFileSelected = callBack
                if (requestCode == UpdateRequestCode) {
                    val file = files.firstOrNull()
                    file?.apply {
                        if (file.extension == "nsp" || file.extension == "xci") {
                            storageHelper.storage.context.contentResolver.takePersistableUriPermission(
                                file.uri,
                                Intent.FLAG_GRANT_READ_URI_PERMISSION
                            )

                            val uri = file.uri

                            var filePath: String? = null

                            var path = uri.pathSegments.joinToString("/")

                            if (path.startsWith("document/")) {
                                val relativePath = Uri.decode(path.substring("document/".length))

                                if (relativePath.startsWith("root/")) {
                                    val rootRelativePath = relativePath.substring("root/".length)

                                    val baseDirectories = listOf(
                                        storageHelper.storage.context.filesDir,
                                        storageHelper.storage.context.getExternalFilesDir(null),
                                        Environment.getExternalStorageDirectory()
                                    )

                                    for (baseDir in baseDirectories) {
                                        val potentialFile = File(baseDir, rootRelativePath)
                                        if (potentialFile.exists()) {
                                            filePath = potentialFile.absolutePath
                                            break
                                        }
                                    }
                                } else if (relativePath.startsWith("primary:")) {
                                    val rootRelativePath = relativePath.substring("primary:".length)

                                    val baseDirectories = listOf(
                                        storageHelper.storage.context.filesDir,
                                        storageHelper.storage.context.getExternalFilesDir(null),
                                        Environment.getExternalStorageDirectory()
                                    )

                                    for (baseDir in baseDirectories) {
                                        val potentialFile = File(baseDir, rootRelativePath)
                                        if (potentialFile.exists()) {
                                            filePath = potentialFile.absolutePath
                                            break
                                        }
                                    }
                                }
                            }

                            path = filePath!!
                            if (path.isNotEmpty()) {
                                data?.apply {
                                    val isDuplicate = this.any { it.path == path }

                                    if (!isDuplicate) {
                                        val contents =
                                            KenjinxNative.deviceGetDlcContentList(
                                                path,
                                                titleId.toLong(16)
                                            )

                                        if (contents.isNotEmpty()) {
                                            val contentPath = path
                                            val container = DlcContainerList(contentPath)

                                            for (content in contents)
                                                container.dlc_nca_list.add(
                                                    DlcContainer(
                                                        true,
                                                        KenjinxNative.deviceGetDlcTitleId(
                                                            contentPath,
                                                            content
                                                        ).toLong(16),
                                                        content
                                                    )
                                                )

                                            this.add(container)
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                refreshDlcItems()
                saveChanges()
            }
        }

        storageHelper.openFilePicker(UpdateRequestCode)
    }

    fun save(openDialog: MutableState<Boolean>) {
        saveChanges()
        openDialog.value = false
    }

    fun setDlcItems(items: SnapshotStateList<DlcItem>, canClose: MutableState<Boolean>) {
        dlcItemsState = items
        this.canClose = canClose
        refreshDlcItems()
    }

    private fun refreshDlcItems() {
        val items = mutableListOf<DlcItem>()

        data?.apply {
            for (container in this) {
                val containerPath = container.path

                if (!File(containerPath).exists())
                    continue

                for (dlc in container.dlc_nca_list) {
                    val enabled = mutableStateOf(dlc.is_enabled)
                    items.add(
                        DlcItem(
                            File(containerPath).name,
                            enabled,
                            containerPath,
                            dlc.path,
                            KenjinxNative.deviceGetDlcTitleId(
                                containerPath,
                                dlc.path
                            )
                        )
                    )
                }
            }
        }

        dlcItemsState?.clear()
        dlcItemsState?.addAll(items)
        canClose?.apply {
            value = true
        }
    }

    fun saveChanges() {
        data?.apply {
            dlcItemsState?.forEach { item ->
                for (container in this) {
                    if (container.path == item.containerPath) {
                        for (dlc in container.dlc_nca_list) {
                            if (dlc.path == item.fullPath) {
                                dlc.is_enabled = item.isEnabled.value
                                break
                            }
                        }
                    }
                }
            }

            val gson = Gson()
            val json = gson.toJson(this)
            val savePath = MainActivity.AppPath + "/games/" + titleId.toLowerCase(Locale.current)
            File(savePath).mkdirs()
            File("$savePath/dlc.json").writeText(json)
        }
    }

    var data: MutableList<DlcContainerList>? = null
    private var jsonPath: String = MainActivity.AppPath + "/games/" + titleId.toLowerCase(Locale.current) + "/dlc.json"

    init {
        storageHelper = MainActivity.StorageHelper!!

        reloadFromDisk()
    }

    private fun reloadFromDisk() {
        data = mutableListOf()
        if (File(jsonPath).exists()) {
            val gson = Gson()
            val typeToken = object : TypeToken<MutableList<DlcContainerList>>() {}.type
            data =
                gson.fromJson<MutableList<DlcContainerList>>(File(jsonPath).readText(), typeToken)
        }
    }
}

data class DlcContainerList(
    var path: String = "",
    var dlc_nca_list: MutableList<DlcContainer> = mutableListOf()
)

data class DlcContainer(
    var is_enabled: Boolean = false,
    var title_id: Long = 0,
    var path: String = ""
)

data class DlcItem(
    var name: String = "",
    var isEnabled: MutableState<Boolean> = mutableStateOf(false),
    var containerPath: String = "",
    var fullPath: String = "",
    var titleId: String = ""
)
