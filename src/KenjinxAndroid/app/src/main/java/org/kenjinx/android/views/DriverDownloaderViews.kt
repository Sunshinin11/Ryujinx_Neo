package org.kenjinx.android.views

import android.content.Intent
import android.net.Uri
import android.widget.Toast
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.staggeredgrid.LazyVerticalStaggeredGrid
import androidx.compose.foundation.lazy.staggeredgrid.StaggeredGridCells
import androidx.compose.foundation.lazy.staggeredgrid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.CloudDownload
import androidx.compose.material.icons.filled.CloudOff
import androidx.compose.material.icons.filled.OpenInBrowser
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.BasicAlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.DialogProperties
import kotlinx.coroutines.launch
import org.kenjinx.android.MainActivity
import org.kenjinx.android.driver.DriverCatalog
import org.kenjinx.android.driver.DriverCatalogEntry
import org.kenjinx.android.driver.DriverCatalogRepository
import org.kenjinx.android.driver.DriverDownloadState
import org.kenjinx.android.driver.DriverDownloader
import org.kenjinx.android.viewmodels.VulkanDriverViewModel
import java.io.File
import java.util.Locale

private fun showDriverToast(activity: MainActivity, message: String, duration: Int) {
    activity.runOnUiThread {
        Toast.makeText(activity, message, duration).show()
    }
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)
@Composable
fun DriverDownloaderDialog(
    activity: MainActivity,
    onDismiss: () -> Unit,
    onDriverInstalled: () -> Unit
) {
    val scope = rememberCoroutineScope()
    val repo = remember { DriverCatalogRepository(activity) }
    val driversDir = remember {
        val appPath = MainActivity.AppPath + "/" + VulkanDriverViewModel.DriverFolder
        File(appPath).apply { if (!exists()) mkdirs() }
    }
    val downloader = remember { DriverDownloader(activity, driversDir) }

    var catalog by remember { mutableStateOf<DriverCatalog?>(null) }
    var catalogSource by remember { mutableStateOf<DriverCatalogRepository.CatalogSource?>(null) }
    var isLoading by remember { mutableStateOf(true) }
    var selectedAuthorFilter by remember { mutableStateOf("Todos") }

    fun refreshCatalog() {
        isLoading = true
        scope.launch {
            val (loadedCatalog, source) = repo.loadCatalog()
            catalog = loadedCatalog
            catalogSource = source
            isLoading = false
        }
    }

    LaunchedEffect(Unit) {
        refreshCatalog()
    }

    DisposableEffect(Unit) {
        onDispose {
            downloader.cancelAll()
        }
    }

    val handleDismiss = {
        downloader.cancelAll()
        onDismiss()
    }

    BasicAlertDialog(
        onDismissRequest = handleDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false)
    ) {
        Surface(
            modifier = Modifier
                .fillMaxWidth(0.96f)
                .fillMaxHeight(0.98f),
            shape = RoundedCornerShape(24.dp),
            color = MaterialTheme.colorScheme.surface,
            tonalElevation = 6.dp,
            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))
        ) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(horizontal = 24.dp, vertical = 20.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                // Header (Switch Style)
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Surface(
                        modifier = Modifier.size(54.dp),
                        shape = RoundedCornerShape(12.dp),
                        color = MaterialTheme.colorScheme.primaryContainer
                    ) {
                        Box(contentAlignment = Alignment.Center) {
                            Icon(
                                imageVector = Icons.Default.CloudDownload,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.onPrimaryContainer,
                                modifier = Modifier.size(28.dp)
                            )
                        }
                    }
                    Spacer(Modifier.width(14.dp))

                    val allDrivers = catalog?.drivers.orEmpty()
                    val sourceLabel = when (catalogSource) {
                        DriverCatalogRepository.CatalogSource.REMOTE -> "Online (GitHub)"
                        DriverCatalogRepository.CatalogSource.CACHE -> "Cache Local"
                        DriverCatalogRepository.CatalogSource.EMBEDDED_FALLBACK -> "Fallback Embarcado"
                        else -> "Carregando"
                    }

                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = "Baixar Drivers Vulkan",
                            style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.Bold,
                            color = MaterialTheme.colorScheme.onSurface,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                        val subtitle = "Catálogo oficial • ${allDrivers.size} drivers disponíveis • $sourceLabel"
                        Text(
                            text = subtitle,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                    }

                    IconButton(onClick = { refreshCatalog() }) {
                        Icon(Icons.Default.Refresh, contentDescription = "Atualizar Catálogo", tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    IconButton(onClick = handleDismiss) {
                        Icon(Icons.Default.Close, contentDescription = "Fechar", tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }

                // Author Filter Chips
                val authors = listOf("Todos", "MrPurple666", "K11MCH1", "whitebelyash")
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    authors.forEach { author ->
                        FilterChip(
                            selected = selectedAuthorFilter == author,
                            onClick = { selectedAuthorFilter = author },
                            label = { Text(author) },
                            colors = FilterChipDefaults.filterChipColors(
                                selectedContainerColor = MaterialTheme.colorScheme.primaryContainer,
                                selectedLabelColor = MaterialTheme.colorScheme.onPrimaryContainer
                            )
                        )
                    }
                }

                // Drivers Content
                if (isLoading) {
                    Box(modifier = Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
                            CircularProgressIndicator()
                            Text("Carregando catálogo de drivers...", style = MaterialTheme.typography.bodyMedium)
                        }
                    }
                } else {
                    val allDrivers = catalog?.drivers.orEmpty()
                    val filteredDrivers = if (selectedAuthorFilter == "Todos") {
                        allDrivers
                    } else {
                        allDrivers.filter { it.author.equals(selectedAuthorFilter, ignoreCase = true) }
                    }

                    if (filteredDrivers.isEmpty()) {
                        // Empty State (Switch Style)
                        Surface(
                            modifier = Modifier
                                .fillMaxWidth()
                                .weight(1f),
                            shape = RoundedCornerShape(16.dp),
                            color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.4f),
                            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.3f))
                        ) {
                            Column(
                                modifier = Modifier
                                    .fillMaxSize()
                                    .padding(20.dp),
                                horizontalAlignment = Alignment.CenterHorizontally,
                                verticalArrangement = Arrangement.Center
                            ) {
                                Icon(
                                    imageVector = Icons.Default.CloudOff,
                                    contentDescription = null,
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.7f),
                                    modifier = Modifier.size(44.dp)
                                )
                                Spacer(Modifier.height(10.dp))
                                Text(
                                    text = "Nenhum driver encontrado",
                                    style = MaterialTheme.typography.titleSmall,
                                    fontWeight = FontWeight.SemiBold,
                                    color = MaterialTheme.colorScheme.onSurface
                                )
                                Spacer(Modifier.height(4.dp))
                                Text(
                                    text = "Não há drivers disponíveis para o autor selecionado.",
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    textAlign = TextAlign.Center,
                                    modifier = Modifier.padding(horizontal = 24.dp)
                                )
                            }
                        }
                    } else {
                        LazyVerticalStaggeredGrid(
                            columns = StaggeredGridCells.Fixed(2),
                            modifier = Modifier.weight(1f).fillMaxWidth(),
                            contentPadding = PaddingValues(2.dp),
                            horizontalArrangement = Arrangement.spacedBy(12.dp),
                            verticalItemSpacing = 12.dp
                        ) {
                            items(filteredDrivers, key = { it.id }) { driver ->
                                DriverCatalogCard(
                                    activity = activity,
                                    entry = driver,
                                    downloader = downloader,
                                    onInstalled = {
                                        onDriverInstalled()
                                    }
                                )
                            }
                        }
                    }
                }

                // Attribution discreta
                Text(
                    text = "Os drivers são de autoria de seus respectivos desenvolvedores e baixados diretamente do GitHub.",
                    modifier = Modifier.fillMaxWidth(),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.8f),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
        }
    }
}

@Composable
fun DriverCatalogCard(
    activity: MainActivity,
    entry: DriverCatalogEntry,
    downloader: DriverDownloader,
    onInstalled: () -> Unit
) {
    val stateFlow = remember(entry.id) { downloader.getState(entry.id) }
    val downloadState by stateFlow.collectAsState()

    val isRecommended = entry.isRecommendedForAdreno740

    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 210.dp)
            .clip(RoundedCornerShape(14.dp)),
        shape = RoundedCornerShape(14.dp),
        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f),
        border = BorderStroke(
            width = if (isRecommended) 2.dp else 1.dp,
            color = if (isRecommended) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)
        )
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            // Title and Author
            Column(
                modifier = Modifier.fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                Text(
                    text = entry.name,
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.fillMaxWidth(),
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis
                )
                Surface(
                    modifier = Modifier.align(Alignment.Start),
                    shape = RoundedCornerShape(6.dp),
                    color = MaterialTheme.colorScheme.primary.copy(alpha = 0.12f)
                ) {
                    Text(
                        text = entry.author,
                        style = MaterialTheme.typography.labelSmall,
                        fontWeight = FontWeight.SemiBold,
                        color = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.padding(horizontal = 6.dp, vertical = 2.dp)
                    )
                }
            }

            // Recommendation Badge
            if (isRecommended) {
                Surface(
                    shape = RoundedCornerShape(6.dp),
                    color = MaterialTheme.colorScheme.primaryContainer
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(4.dp),
                        modifier = Modifier.padding(horizontal = 8.dp, vertical = 3.dp)
                    ) {
                        Icon(
                            Icons.Default.Star,
                            contentDescription = null,
                            tint = MaterialTheme.colorScheme.primary,
                            modifier = Modifier.size(14.dp)
                        )
                        Text(
                            text = "Recomendado para Adreno 740 / Thor",
                            style = MaterialTheme.typography.labelSmall,
                            fontWeight = FontWeight.Bold,
                            color = MaterialTheme.colorScheme.onPrimaryContainer
                        )
                    }
                }
            }

            // Specs Row
            Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                if (entry.mesaVersion.isNotBlank()) {
                    Text(
                        text = "Mesa: ${entry.mesaVersion}",
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                if (entry.vulkanVersion.isNotBlank()) {
                    Text(
                        text = "Vulkan: ${entry.vulkanVersion}",
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }

            // Notes
            if (entry.notes.isNotBlank()) {
                Text(
                    text = entry.notes,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis
                )
            }

            Spacer(modifier = Modifier.height(2.dp))

            // State Action Controls
            when (val state = downloadState) {
                is DriverDownloadState.Available -> {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        if (entry.releasePage.isNotBlank()) {
                            TextButton(
                                onClick = {
                                    try {
                                        activity.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(entry.releasePage)))
                                    } catch (_: Exception) {}
                                },
                                contentPadding = PaddingValues(horizontal = 6.dp, vertical = 2.dp)
                            ) {
                                Icon(Icons.Default.OpenInBrowser, contentDescription = null, modifier = Modifier.size(14.dp))
                                Spacer(modifier = Modifier.width(4.dp))
                                Text("GitHub", fontSize = 12.sp)
                            }
                        } else {
                            Spacer(modifier = Modifier.width(1.dp))
                        }

                        Button(
                            onClick = {
                                downloader.downloadAndInstall(
                                    entry = entry,
                                    onSuccess = {
                                        showDriverToast(activity, "Driver '${entry.name}' instalado com sucesso!", Toast.LENGTH_SHORT)
                                        onInstalled()
                                    },
                                    onError = { err ->
                                        showDriverToast(activity, "Erro: $err", Toast.LENGTH_LONG)
                                    }
                                )
                            },
                            contentPadding = PaddingValues(horizontal = 14.dp, vertical = 6.dp)
                        ) {
                            Icon(Icons.Default.CloudDownload, contentDescription = null, modifier = Modifier.size(16.dp))
                            Spacer(modifier = Modifier.width(6.dp))
                            Text("Instalar")
                        }
                    }
                }
                is DriverDownloadState.Downloading -> {
                    Column(
                        modifier = Modifier.fillMaxWidth(),
                        verticalArrangement = Arrangement.spacedBy(4.dp)
                    ) {
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween
                        ) {
                            val percent = (state.progress * 100).toInt()
                            Text(
                                text = "Baixando: $percent%",
                                style = MaterialTheme.typography.labelSmall,
                                fontWeight = FontWeight.SemiBold
                            )
                            val downloadedMB = "%.1f".format(Locale.US, state.downloadedBytes / (1024f * 1024f))
                            val totalMB = "%.1f".format(Locale.US, state.totalBytes / (1024f * 1024f))
                            Text(
                                text = "$downloadedMB / $totalMB MB",
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        LinearProgressIndicator(
                            progress = { state.progress },
                            modifier = Modifier.fillMaxWidth().height(6.dp).clip(RoundedCornerShape(3.dp))
                        )
                        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                            TextButton(onClick = { downloader.cancelDownload(entry.id) }) {
                                Text("Cancelar", color = MaterialTheme.colorScheme.error, fontSize = 12.sp)
                            }
                        }
                    }
                }
                is DriverDownloadState.Validating -> {
                    Column(
                        modifier = Modifier.fillMaxWidth(),
                        verticalArrangement = Arrangement.spacedBy(4.dp)
                    ) {
                        Text(
                            text = state.message,
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.primary
                        )
                        LinearProgressIndicator(
                            modifier = Modifier.fillMaxWidth().height(6.dp).clip(RoundedCornerShape(3.dp))
                        )
                    }
                }
                is DriverDownloadState.Installed -> {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(6.dp)
                        ) {
                            Surface(
                                shape = CircleShape,
                                color = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.size(20.dp)
                            ) {
                                Box(contentAlignment = Alignment.Center) {
                                    Icon(
                                        Icons.Default.Check,
                                        contentDescription = null,
                                        tint = MaterialTheme.colorScheme.onPrimary,
                                        modifier = Modifier.size(13.dp)
                                    )
                                }
                            }
                            Text(
                                text = "Instalado",
                                style = MaterialTheme.typography.labelMedium,
                                fontWeight = FontWeight.Bold,
                                color = MaterialTheme.colorScheme.primary
                            )
                        }

                        OutlinedButton(
                            onClick = {
                                downloader.downloadAndInstall(
                                    entry = entry,
                                    onSuccess = {
                                        showDriverToast(activity, "Driver '${entry.name}' reinstalado com sucesso!", Toast.LENGTH_SHORT)
                                        onInstalled()
                                    },
                                    onError = { err ->
                                        showDriverToast(activity, "Erro: $err", Toast.LENGTH_LONG)
                                    }
                                )
                            },
                            contentPadding = PaddingValues(horizontal = 10.dp, vertical = 4.dp)
                        ) {
                            Text("Reinstalar", fontSize = 12.sp)
                        }
                    }
                }
                is DriverDownloadState.Error -> {
                    Column(
                        modifier = Modifier.fillMaxWidth(),
                        verticalArrangement = Arrangement.spacedBy(4.dp)
                    ) {
                        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                            Icon(Icons.Default.Warning, contentDescription = null, tint = MaterialTheme.colorScheme.error, modifier = Modifier.size(14.dp))
                            Text(
                                text = state.message,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.error,
                                maxLines = 2,
                                overflow = TextOverflow.Ellipsis
                            )
                        }
                        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                            Button(
                                onClick = {
                                    downloader.downloadAndInstall(
                                        entry = entry,
                                        onSuccess = {
                                            showDriverToast(activity, "Driver '${entry.name}' instalado com sucesso!", Toast.LENGTH_SHORT)
                                            onInstalled()
                                        },
                                        onError = { err ->
                                            showDriverToast(activity, "Erro: $err", Toast.LENGTH_LONG)
                                        }
                                    )
                                },
                                contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp)
                            ) {
                                Text("Tentar novamente", fontSize = 12.sp)
                            }
                        }
                    }
                }
            }
        }
    }
}