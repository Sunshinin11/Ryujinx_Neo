package org.kenjinx.android.viewmodels

import android.content.Intent
import android.net.Uri
import android.os.Environment
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.text.intl.Locale
import androidx.compose.ui.text.toLowerCase
import androidx.core.net.toUri
import com.anggrayudi.storage.SimpleStorageHelper
import com.anggrayudi.storage.file.extension
import com.google.gson.Gson
import org.kenjinx.android.MainActivity
import java.io.File

class TitleUpdateViewModel(val titleId: String) {
    private var canClose: MutableState<Boolean>? = null
    private var basePath: String = MainActivity.AppPath + "/games/" + titleId.toLowerCase(Locale.current)
    private var updateJsonName = "updates.json"
    private var storageHelper: SimpleStorageHelper
    private var currentPaths: MutableList<String> = mutableListOf()
    private var pathsState: SnapshotStateList<String>? = null

    companion object {
        const val UpdateRequestCode = 1002
    }

    fun remove(index: Int) {
        if (index <= 0) {
            return
        }

        val updatesData = data
        if (updatesData != null && updatesData.paths.isNotEmpty() && index - 1 < updatesData.paths.size) {
            val str = updatesData.paths.removeAt(index - 1)

            currentPaths = ArrayList(updatesData.paths)

            pathsState?.clear()
            pathsState?.addAll(updatesData.paths)

            str.toUri().let { uri ->
                try {
                    storageHelper.storage.context.contentResolver.releasePersistableUriPermission(
                        uri,
                        Intent.FLAG_GRANT_READ_URI_PERMISSION
                    )
                } catch (_: SecurityException) {
                }
            }

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
                                }
                                else if(relativePath.startsWith("primary:")) {
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
                                val isDuplicate = currentPaths.contains(path) || data?.paths?.contains(path) == true

                                if (!isDuplicate) {
                                    currentPaths.add(path)
                                }
                            }
                        }
                    }

                    refreshPaths()
                    saveChanges()
                }
            }
        }
        storageHelper.openFilePicker(UpdateRequestCode)
    }

    fun save(index: Int,openDialog: MutableState<Boolean>) {
        data?.apply {
            this.selected = ""
            if (paths.isNotEmpty() && index > 0) {
                val ind = index - 1
                this.selected = paths[ind]
            }

            saveChanges()
            openDialog.value = false
        }
    }

    fun setPaths(paths: SnapshotStateList<String>, canClose: MutableState<Boolean>) {
        pathsState = paths
        this.canClose = canClose
        refreshPaths()
    }

    private fun refreshPaths() {
        data?.apply {
            val existingPaths = mutableListOf<String>()
            currentPaths.forEach {
                existingPaths.add(it)
            }

            if (!existingPaths.contains(selected)) {
                selected = ""
            }
            pathsState?.clear()
            pathsState?.addAll(existingPaths)
            paths = existingPaths
            canClose?.apply {
                value = true
            }
        }
    }

    fun saveChanges() {
        val metadata = data ?: TitleUpdateMetadata()
        val gson = Gson()

        File(basePath).mkdirs()

        val json = gson.toJson(metadata)
        File("$basePath/$updateJsonName").writeText(json)
    }

    var data: TitleUpdateMetadata? = null
    private var jsonPath: String = "${basePath}/${updateJsonName}"

    init {

        data = TitleUpdateMetadata()
        if (File(jsonPath).exists()) {
            val gson = Gson()
            data = gson.fromJson(File(jsonPath).readText(), TitleUpdateMetadata::class.java)

        }
        currentPaths = data?.paths ?: mutableListOf()
        storageHelper = MainActivity.StorageHelper!!
        refreshPaths()

        File("$basePath/update").deleteRecursively()
    }
}

data class TitleUpdateMetadata(
    var selected: String = "",
    var paths: MutableList<String> = mutableListOf()
)
