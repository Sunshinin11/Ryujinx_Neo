package org.kenjinx.android.driver

data class DriverCatalog(
    val catalogVersion: Int = 1,
    val updatedAt: String = "",
    val repositoryWhitelist: List<String> = emptyList(),
    val drivers: List<DriverCatalogEntry> = emptyList()
)

data class DriverCatalogEntry(
    val id: String = "",
    val name: String = "",
    val author: String = "",
    val sourceRepository: String = "",
    val releasePage: String = "",
    val downloadUrl: String = "",
    val fileName: String = "",
    val sha256: String = "",
    val packageVersion: String = "",
    val mesaVersion: String = "",
    val vulkanVersion: String = "",
    val minApi: Int = 29,
    val supportedGpus: List<String> = emptyList(),
    val recommendedDevices: List<String> = emptyList(),
    val channel: String = "stable",
    val notes: String = ""
) {
    val isRecommendedForAdreno740: Boolean
        get() = recommendedDevices.any { it.contains("740", ignoreCase = true) || it.contains("Thor", ignoreCase = true) }
}

sealed class DriverDownloadState {
    data object Available : DriverDownloadState()
    data class Downloading(val progress: Float, val downloadedBytes: Long, val totalBytes: Long) : DriverDownloadState()
    data class Validating(val message: String) : DriverDownloadState()
    data object Installed : DriverDownloadState()
    data class Error(val message: String) : DriverDownloadState()
}