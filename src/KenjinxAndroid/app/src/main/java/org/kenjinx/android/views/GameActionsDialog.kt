package org.kenjinx.android.views

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AddBox
import androidx.compose.material.icons.filled.Archive
import androidx.compose.material.icons.filled.Bolt
import androidx.compose.material.icons.filled.Compress
import androidx.compose.material.icons.filled.KeyboardReturn
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Code
import androidx.compose.material.icons.filled.Contactless
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.DeleteSweep
import androidx.compose.material.icons.filled.Extension
import androidx.compose.material.icons.filled.Memory
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Save
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.SportsEsports
import androidx.compose.material.icons.filled.SystemUpdate
import androidx.compose.material.icons.filled.ViewModule
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.BasicAlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.DialogProperties
import org.kenjinx.android.NszConverter
import org.kenjinx.android.viewmodels.GameModel
import java.io.File
import java.util.Locale
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun GameActionsDialog(
    game: GameModel,
    onDismiss: () -> Unit,
    onPlay: () -> Unit,
    onConfigure: () -> Unit,
    onUpdates: () -> Unit,
    onDlc: () -> Unit,
    onMods: () -> Unit,
    onCheats: () -> Unit,
    onSaves: () -> Unit,
    onAmiibo: () -> Unit,
    onCreateShortcut: () -> Unit,
    onClearPptc: () -> Unit,
    onPurgeShader: () -> Unit,
    onDeleteCache: () -> Unit,
    // NEOV2-NCZ2: chamado ao terminar a conversao para NSZ (sucesso, mensagem).
    onCompressed: (Boolean, String) -> Unit = { _, _ -> }
) {
    var showDeleteCacheConfirm by remember { mutableStateOf(false) }
    var showCompressChoice by remember { mutableStateOf(false) }
    var showDecompression by remember { mutableStateOf(false) }
    val isNsp = game.file.name?.lowercase(Locale.ROOT)?.endsWith(".nsp") == true
    val isNsz = game.file.name?.lowercase(Locale.ROOT)?.endsWith(".nsz") == true
    val context = LocalContext.current
    var blockSpace by remember(game.file.uri, showCompressChoice) { mutableStateOf<Long?>(null) }
    LaunchedEffect(showCompressChoice, game.file.uri) {
        if (showCompressChoice && isNsz) {
            blockSpace = withContext(Dispatchers.IO) { NszConverter.inspectBlocks(context.applicationContext, game.file) }
        }
    }

    val bitmap = remember(game.titleId) {
        try {
            val iconFile = File(context.filesDir, "game_icons/${game.titleId}.jpg")
            if (iconFile.exists()) {
                android.graphics.BitmapFactory.decodeFile(iconFile.absolutePath)
            } else if (game.icon?.isNotEmpty() == true) {
                // NEOV2-UI1: mesma origem da capa na lista de jogos (icone embutido no jogo, em base64).
                val bytes = java.util.Base64.getDecoder().decode(game.icon)
                android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
            } else null
        } catch (_: Throwable) {
            null
        }
    }

    val actions = listOfNotNull(
        DialogAction(Icons.Default.PlayArrow, "Jogar", "Iniciar emulação", true, false, onPlay),
        DialogAction(Icons.Default.Settings, "Configurar", "Perfil deste jogo", false, false, onConfigure),
        DialogAction(Icons.Default.SystemUpdate, "Atualizações", "Patches e versões", false, false, onUpdates),
        DialogAction(Icons.Default.Extension, "DLCs", "Conteúdos adicionais", false, false, onDlc),
        DialogAction(Icons.Default.ViewModule, "Mods", "Gerenciar modificações", false, false, onMods),
        DialogAction(Icons.Default.Code, "Cheats", "Códigos de trapaça", false, false, onCheats),
        DialogAction(Icons.Default.Save, "Saves", "Importar ou exportar", false, false, onSaves),
        DialogAction(Icons.Default.Contactless, "Amiibo", "Configurar slots", false, false, onAmiibo),
        DialogAction(Icons.Default.AddBox, "Criar atalho", "Adicionar à tela inicial", false, false, onCreateShortcut),
        // NSP uses compression levels; NSZ uses solid-to-block optimization.
        if (isNsp || isNsz) DialogAction(Icons.Default.Archive, "Comprimir", if (isNsz) "Otimizar NSZ para blocos" else "Converter para NSZ", false, false, { showCompressChoice = true }) else null,
        if (isNsz) DialogAction(Icons.Default.Archive, "Descompactar", "Converter NSZ para NSP", false, false, { showDecompression = true }) else null,
        DialogAction(Icons.Default.Memory, "Limpar cache PPTC", "Descarta compilações PPTC", false, false, onClearPptc),
        DialogAction(Icons.Default.DeleteSweep, "Limpar shaders", "Descarta shaders compilados", false, false, onPurgeShader),
        DialogAction(Icons.Default.Delete, "Excluir todo o cache", "Remove PPTC, shaders e arquivos", false, true, { showDeleteCacheConfirm = true })
    )

    // NEOV2-UI1: no celular em pe o popup ocupa a largura da tela e mostra as acoes em 1 coluna.
    val config = androidx.compose.ui.platform.LocalConfiguration.current
    val compact = config.screenWidthDp < 600

    BasicAlertDialog(
        // NEOV2-NCZ2: nao fecha enquanto a conversao estiver em andamento.
        onDismissRequest = { if (!NszConverter.running.value) onDismiss() },
        properties = DialogProperties(usePlatformDefaultWidth = false)
    ) {
        Surface(
            shape = RoundedCornerShape(18.dp),
            color = MaterialTheme.colorScheme.surface,
            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)),
            modifier = if (compact) {
                Modifier
                    .fillMaxWidth(0.96f)
                    .heightIn(max = (config.screenHeightDp * 0.85f).dp)
                    .padding(8.dp)
            } else {
                Modifier
                    .width(620.dp)
                    .height(440.dp)
                    .padding(12.dp)
            }
        ) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(20.dp)
            ) {
                // Header (Switch identity matching DlcViews)
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(bottom = 16.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Surface(
                        shape = RoundedCornerShape(12.dp),
                        color = MaterialTheme.colorScheme.primaryContainer,
                        modifier = Modifier.size(54.dp)
                    ) {
                        if (bitmap != null) {
                            Image(
                                bitmap = bitmap.asImageBitmap(),
                                contentDescription = null,
                                contentScale = ContentScale.Crop,
                                modifier = Modifier.fillMaxSize()
                            )
                        } else {
                            Box(contentAlignment = Alignment.Center) {
                                Icon(
                                    imageVector = Icons.Default.SportsEsports,
                                    contentDescription = null,
                                    tint = MaterialTheme.colorScheme.onPrimaryContainer,
                                    modifier = Modifier.size(28.dp)
                                )
                            }
                        }
                    }

                    Spacer(Modifier.width(14.dp))

                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = game.titleName ?: "Gerenciar Jogo",
                            style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.Bold,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                        val titleIdText = game.titleId?.uppercase(Locale.ROOT) ?: ""
                        // NEOV2-NCZ2: o subtitulo acompanha o painel atual.
                        val panelName = when {
                            showDecompression || (NszConverter.running.value && NszConverter.decompressing.value) -> "Descompactar para NSP"
                            showCompressChoice || NszConverter.running.value -> if (isNsz || NszConverter.optimizing.value) "Otimizar NSZ para blocos" else "Comprimir para NSZ"
                            else -> "Ações rápidas"
                        }
                        Text(
                            text = if (titleIdText.isNotBlank()) "$titleIdText • $panelName" else panelName,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                    }

                    if (!NszConverter.running.value) {
                        IconButton(onClick = {
                            when {
                                showDecompression -> showDecompression = false
                                showCompressChoice -> showCompressChoice = false
                                else -> onDismiss()
                            }
                        }) {
                            Icon(Icons.Default.Close, contentDescription = "Fechar")
                        }
                    }
                }

                // NEOV2-NCZ2: paineis de compressao no mesmo estilo dos cartoes de acao.
                if (showDecompression) {
                    DecompressionPanel(
                        source = game.file,
                        modifier = Modifier.weight(1f).fillMaxWidth(),
                        onBack = { showDecompression = false },
                        onFinished = onCompressed
                    )
                } else if (NszConverter.running.value) {

                    Column(
                        modifier = Modifier.weight(1f).fillMaxWidth(),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                        ConversionProgressContent()
                    }
                } else if (showCompressChoice) {
                    Column(
                        modifier = Modifier.weight(1f).fillMaxWidth(),
                        verticalArrangement = Arrangement.spacedBy(12.dp)
                    ) {
                        Text(
                            text = if (isNsz) "Mantém o jogo comprimido e prepara o arquivo para leitura por blocos. O arquivo pode ficar maior. Depois da verificação, será salvo como .blocks.nsz na mesma pasta e o original será excluído."
                                else "O jogo será convertido para NSZ na mesma pasta. Depois que o arquivo novo for verificado, o NSP original será excluído.",
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )

                        val startCompression: (Int) -> Unit = { level ->
                            showCompressChoice = false
                            NszConverter.start(context, game.file, level) { success, message ->
                                onCompressed(success, message)
                            }
                        }

                        LazyVerticalGrid(
                            columns = GridCells.Fixed(if (compact) 1 else 2),
                            horizontalArrangement = Arrangement.spacedBy(12.dp),
                            verticalArrangement = Arrangement.spacedBy(12.dp)
                        ) {
                            if (isNsz) {
                                item {
                                    ActionCard(
                                        icon = Icons.Default.Compress,
                                        title = if (blockSpace == 0L) "Já otimizado" else "Otimizar NSZ para blocos",
                                        subtitle = when {
                                            blockSpace == null -> "Verificando o formato do arquivo..."
                                            blockSpace == 0L -> "O NSZ já usa leitura por blocos"
                                            blockSpace!! < 0L -> "Não foi possível analisar o NSZ. Reabra este painel para tentar novamente."
                                            else -> "Mantém o jogo comprimido • blocos de 1 MB"
                                        },
                                        isPrimary = (blockSpace ?: 0L) > 0L,
                                        enabled = (blockSpace ?: 0L) > 0L,
                                        onClick = {
                                            showCompressChoice = false
                                            NszConverter.optimizeBlocks(context, game.file, onCompressed)
                                        }
                                    )
                                }
                            } else {
                                item {
                                    ActionCard(
                                        icon = Icons.Default.Bolt,
                                        title = "Rápido",
                                        subtitle = "Recomendado",
                                        isPrimary = true,
                                        onClick = { startCompression(NszConverter.LEVEL_FAST) }
                                    )
                                }
                                item {
                                    ActionCard(
                                        icon = Icons.Default.Compress,
                                        title = "Máximo",
                                        subtitle = "Arquivo menor, bem mais lento",
                                        onClick = { startCompression(NszConverter.LEVEL_MAX) }
                                    )
                                }
                            }
                            item {
                                ActionCard(
                                    icon = Icons.Default.KeyboardReturn,
                                    title = "Voltar",
                                    subtitle = "Ações rápidas",
                                    onClick = { showCompressChoice = false }
                                )
                            }
                        }
                    }
                } else
                // Landscape 4:3 action area, arranged in two columns for handhelds.
                LazyVerticalGrid(
                    columns = GridCells.Fixed(if (compact) 1 else 2),
                    modifier = Modifier.weight(1f),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    items(actions) { action ->
                        ActionCard(
                            icon = action.icon,
                            title = action.title,
                            subtitle = action.subtitle,
                            isPrimary = action.isPrimary,
                            isError = action.isError,
                            onClick = action.onClick
                        )
                    }
                }
            }
        }
    }

    // Standardized Delete Confirmation Dialog matching DlcViews
    if (showDeleteCacheConfirm) {
        AlertDialog(
            onDismissRequest = { showDeleteCacheConfirm = false },
            title = { Text("Excluir cache") },
            text = {
                Text("Deseja excluir todo o cache de \"${game.titleName ?: "deste jogo"}\"? Os dados de PPTC, shaders compilados e arquivos temporários serão apagados.")
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        onDeleteCache()
                        showDeleteCacheConfirm = false
                    }
                ) {
                    Text("Excluir", color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = {
                TextButton(onClick = { showDeleteCacheConfirm = false }) {
                    Text("Cancelar", maxLines = 1, softWrap = false)
                }
            }
        )
    }
}

@Composable
internal fun ConversionProgressContent() {
    val fraction = NszConverter.permille.intValue / 1000f
    val verifying = NszConverter.phase.intValue == NszConverter.PHASE_VERIFYING
    val decompressing = NszConverter.decompressing.value
    val optimizing = NszConverter.optimizing.value
    Surface(
        shape = RoundedCornerShape(14.dp),
        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f),
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(modifier = Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    imageVector = if (verifying) Icons.Default.Compress else Icons.Default.Archive,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(22.dp)
                )
                Spacer(Modifier.width(10.dp))
                Text(
                    text = when {
                        verifying -> if (optimizing) "Verificando o NSZ otimizado" else if (decompressing) "Verificando o arquivo descompactado" else "Verificando o arquivo comprimido"
                        optimizing -> "Otimizando NSZ para blocos"
                        decompressing -> "Descompactando"
                        else -> "Comprimindo"
                    },
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.weight(1f)
                )
                Text(
                    text = String.format(Locale.getDefault(), "%.1f%%", fraction * 100f),
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.primary
                )
            }
            Spacer(Modifier.height(12.dp))
            LinearProgressIndicator(
                progress = { fraction },
                modifier = Modifier
                    .fillMaxWidth()
                    .height(8.dp)
                    .clip(RoundedCornerShape(4.dp))
            )
            Spacer(Modifier.height(10.dp))
            Text(
                text = NszConverter.gameName.value,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
    }

    ActionCard(
        icon = Icons.Default.Close,
        title = "Cancelar",
        subtitle = "O arquivo original é mantido",
        isError = true,
        onClick = { NszConverter.cancel() }
    )
}

@Composable
internal fun ActionCard(
    icon: ImageVector,
    title: String,
    subtitle: String,
    isPrimary: Boolean = false,
    isError: Boolean = false,
    enabled: Boolean = true,
    onClick: () -> Unit
) {
    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 76.dp)
            .clip(RoundedCornerShape(14.dp))
            .alpha(if (enabled) 1f else 0.45f)
            .clickable(enabled = enabled, onClick = onClick),
        shape = RoundedCornerShape(14.dp),
        color = when {
            isPrimary -> MaterialTheme.colorScheme.primaryContainer
            isError -> MaterialTheme.colorScheme.errorContainer.copy(alpha = 0.25f)
            else -> MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f)
        },
        border = BorderStroke(
            1.dp,
            when {
                isPrimary -> MaterialTheme.colorScheme.primary.copy(alpha = 0.5f)
                isError -> MaterialTheme.colorScheme.error.copy(alpha = 0.4f)
                else -> MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)
            }
        )
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 14.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                tint = when {
                    isPrimary -> MaterialTheme.colorScheme.onPrimaryContainer
                    isError -> MaterialTheme.colorScheme.error
                    else -> MaterialTheme.colorScheme.onSurfaceVariant
                },
                modifier = Modifier.size(24.dp)
            )
            Spacer(Modifier.width(14.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = title,
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = FontWeight.SemiBold,
                    color = when {
                        isPrimary -> MaterialTheme.colorScheme.onPrimaryContainer
                        isError -> MaterialTheme.colorScheme.error
                        else -> MaterialTheme.colorScheme.onSurface
                    },
                    maxLines = 1,
                    softWrap = false,
                    overflow = TextOverflow.Ellipsis
                )
                Text(
                    text = subtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = when {
                        isPrimary -> MaterialTheme.colorScheme.onPrimaryContainer.copy(alpha = 0.7f)
                        isError -> MaterialTheme.colorScheme.error.copy(alpha = 0.75f)
                        else -> MaterialTheme.colorScheme.onSurfaceVariant
                    },
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis
                )
            }
        }
    }
}

private data class DialogAction(
    val icon: ImageVector,
    val title: String,
    val subtitle: String,
    val isPrimary: Boolean,
    val isError: Boolean = false,
    val onClick: () -> Unit
)
