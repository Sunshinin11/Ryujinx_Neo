package org.kenjinx.android.views

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Archive
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.FolderOpen
import androidx.compose.material.icons.filled.InsertDriveFile
import androidx.compose.material.icons.filled.KeyboardReturn
import androidx.compose.material3.BasicAlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.DialogProperties
import androidx.documentfile.provider.DocumentFile
import org.kenjinx.android.NszConverter
import java.util.Locale

// File conversion from settings. Library games use this panel inside GameActionsDialog.
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DecompressionDialog(
    source: DocumentFile? = null,
    onDismiss: () -> Unit,
    onFinished: (Boolean, String) -> Unit
) {
    val config = LocalConfiguration.current
    val compact = config.screenWidthDp < 600
    val busy = NszConverter.running.value
    var fileName by remember(source?.uri) { mutableStateOf(source?.name.orEmpty()) }
    BasicAlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        properties = DialogProperties(usePlatformDefaultWidth = false)
    ) {
        Surface(
            shape = RoundedCornerShape(18.dp),
            color = MaterialTheme.colorScheme.surface,
            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)),
            modifier = if (compact) {
                Modifier.fillMaxWidth(0.96f).heightIn(max = (config.screenHeightDp * 0.85f).dp).padding(8.dp)
            } else {
                Modifier.width(620.dp).height(440.dp).padding(12.dp)
            }
        ) {
            Column(modifier = Modifier.fillMaxSize().padding(20.dp)) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(bottom = 16.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Surface(
                        shape = RoundedCornerShape(12.dp),
                        color = MaterialTheme.colorScheme.primaryContainer,
                        modifier = Modifier.size(54.dp)
                    ) {
                        Box(contentAlignment = Alignment.Center) {
                            Icon(
                                Icons.Default.Archive,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.onPrimaryContainer,
                                modifier = Modifier.size(28.dp)
                            )
                        }
                    }
                    Spacer(Modifier.width(14.dp))
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = fileName.ifBlank { "Descompactar arquivos" },
                            style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.Bold,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                        Text(
                            text = "Descompactar NSZ ou NCZ",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                    }
                    if (!busy) {
                        IconButton(onClick = onDismiss) {
                            Icon(Icons.Default.Close, contentDescription = "Fechar")
                        }
                    }
                }
                DecompressionPanel(
                    source = source,
                    modifier = Modifier.weight(1f).fillMaxWidth(),
                    backSubtitle = "Configurações",
                    onFileSelected = { fileName = it },
                    onBack = onDismiss,
                    onFinished = { success, result ->
                        onFinished(success, result)
                        if (success) onDismiss()
                    }
                )
            }
        }
    }
}

@Composable
internal fun DecompressionPanel(
    source: DocumentFile? = null,
    modifier: Modifier = Modifier,
    backSubtitle: String = "Ações rápidas",
    onFileSelected: (String) -> Unit = {},
    onBack: () -> Unit,
    onFinished: (Boolean, String) -> Unit
) {
    val context = LocalContext.current
    val compact = LocalConfiguration.current.screenWidthDp < 600
    var input by remember(source?.uri) { mutableStateOf(source) }
    var destination by remember(source?.uri) { mutableStateOf(source?.parentFile) }
    var message by remember { mutableStateOf("") }
    val chooseInput = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) {
            val file = DocumentFile.fromSingleUri(context, uri)
            val extension = file?.name?.substringAfterLast('.', "")?.lowercase(Locale.ROOT)
            if (file != null && extension in setOf("nsz", "ncz")) {
                input = file
                message = ""
                onFileSelected(file.name.orEmpty())
            } else message = "Selecione um arquivo .nsz ou .ncz."
        }
    }
    val chooseDestination = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) destination = DocumentFile.fromTreeUri(context, uri)
    }
    Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(12.dp)) {
        if (NszConverter.running.value) {
            ConversionProgressContent()
        } else {
            val ncz = input?.name?.lowercase(Locale.ROOT)?.endsWith(".ncz") == true
            val outputKind = if (ncz) "NCA" else "NSP"
            Text(
                text = if (input == null) {
                    "Escolha um arquivo e uma pasta de destino. Depois que o arquivo novo for verificado, o original compactado será excluído."
                } else {
                    "O arquivo será convertido para $outputKind na pasta selecionada. Depois que o arquivo novo for verificado, o original compactado será excluído."
                },
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            if (message.isNotEmpty()) Text(message, style = MaterialTheme.typography.bodySmall)
            LazyVerticalGrid(
                columns = GridCells.Fixed(if (compact) 1 else 2),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                item {
                    ActionCard(
                        icon = Icons.Default.Archive,
                        title = "Descompactar",
                        subtitle = if (input == null) "Selecione o arquivo e o destino" else "Converter para $outputKind",
                        isPrimary = true,
                        enabled = input != null && destination != null,
                        onClick = {
                            NszConverter.decompress(context, input!!, destination!!) { success, result ->
                                message = result
                                onFinished(success, result)
                            }
                        }
                    )
                }
                if (source == null) {
                    item {
                        ActionCard(
                            icon = Icons.Default.InsertDriveFile,
                            title = "Escolher arquivo",
                            subtitle = input?.name ?: "NSZ ou NCZ",
                            onClick = { chooseInput.launch(arrayOf("*/*")) }
                        )
                    }
                }
                item {
                    ActionCard(
                        icon = Icons.Default.FolderOpen,
                        title = "Pasta de destino",
                        subtitle = destination?.name ?: "Selecione uma pasta",
                        onClick = { chooseDestination.launch(null) }
                    )
                }
                item {
                    ActionCard(
                        icon = Icons.Default.KeyboardReturn,
                        title = "Voltar",
                        subtitle = backSubtitle,
                        onClick = onBack
                    )
                }
            }
        }
    }
}
