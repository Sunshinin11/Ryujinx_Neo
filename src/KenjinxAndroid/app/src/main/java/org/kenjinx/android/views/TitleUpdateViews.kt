package org.kenjinx.android.views

import android.graphics.BitmapFactory
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.SystemUpdate
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import org.kenjinx.android.MainActivity
import org.kenjinx.android.viewmodels.TitleUpdateViewModel
import java.io.File
import java.util.Base64

class TitleUpdateViews {
    companion object {
        private fun cleanUpdateTitle(fileName: String, gameTitle: String): String {
            var name = fileName.substringBeforeLast('.')
            val versionMatch = Regex("""\[v([0-9.]+)\]""", RegexOption.IGNORE_CASE).find(name)
            val versionText = versionMatch?.groupValues?.get(1)

            name = name.replace(Regex("""\[[0-9a-fA-F]{16}\]"""), "")
            name = name.replace(Regex("""\[v[0-9.]+\]""", RegexOption.IGNORE_CASE), "")
            name = name.replace(Regex("""\[(UPD|Update)\]""", RegexOption.IGNORE_CASE), "")

            if (gameTitle.isNotBlank()) {
                val trimmed = name.trim()
                if (trimmed.startsWith(gameTitle, ignoreCase = true)) {
                    val rem = trimmed.substring(gameTitle.length).trim().removePrefix("-").trim()
                    if (rem.isNotBlank()) {
                        name = rem
                    }
                }
            }

            name = name.replace(Regex("""^[-_\s\(\)\[\]]+|[-_\s\(\)\[\]]+$"""), "")
            name = name.replace(Regex("""\s+"""), " ")

            return when {
                name.isNotBlank() && versionText != null -> "$name (v$versionText)"
                name.isNotBlank() -> name
                versionText != null -> "Atualização v$versionText"
                else -> fileName
            }
        }

        @Composable
        fun Main(titleId: String, name: String, openDialog: MutableState<Boolean>, canClose: MutableState<Boolean>) {
            val viewModel = remember(titleId) { TitleUpdateViewModel(titleId) }
            val paths = remember { mutableStateListOf<String>() }

            var selectedIndex by remember { mutableIntStateOf(0) }

            // NEOV2-UI1: carrega a lista e a atualizacao ativa uma unica vez. Antes isso rodava a cada
            // recomposicao e desfazia na hora a escolha feita no switch (parecia que nao dava para trocar).
            val initialized = remember(titleId) { mutableStateOf(false) }
            if (!initialized.value) {
                viewModel.setPaths(paths, canClose)
                viewModel.data?.apply {
                    selectedIndex = paths.indexOf(this.selected) + 1
                }
                initialized.value = true
            }

            var indexToDelete by remember { mutableStateOf<Int?>(null) }
            var expandedIndex by remember { mutableStateOf<Int?>(null) }

            val selectedGame = MainActivity.mainViewModel?.selected
            val bitmap = remember(selectedGame?.titleId) {
                runCatching {
                    selectedGame?.icon?.let { base64 ->
                        val decoded = Base64.getDecoder().decode(base64)
                        BitmapFactory.decodeByteArray(decoded, 0, decoded.size)
                    }
                }.getOrNull()
            }

            val validFiles = paths.mapIndexedNotNull { index, path ->
                val file = File(path)
                if (file.exists()) Triple(index + 1, path, file) else null
            }

            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 24.dp, vertical = 20.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp)
            ) {
                // Header
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    if (bitmap != null) {
                        Image(
                            bitmap = bitmap.asImageBitmap(),
                            contentDescription = null,
                            contentScale = ContentScale.Crop,
                            modifier = Modifier
                                .size(54.dp)
                                .clip(RoundedCornerShape(12.dp))
                        )
                        Spacer(Modifier.width(14.dp))
                    } else {
                        Surface(
                            modifier = Modifier.size(54.dp),
                            shape = RoundedCornerShape(12.dp),
                            color = MaterialTheme.colorScheme.primaryContainer
                        ) {
                            Box(contentAlignment = Alignment.Center) {
                                Icon(
                                    imageVector = Icons.Default.SystemUpdate,
                                    contentDescription = null,
                                    tint = MaterialTheme.colorScheme.onPrimaryContainer,
                                    modifier = Modifier.size(28.dp)
                                )
                            }
                        }
                        Spacer(Modifier.width(14.dp))
                    }

                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = name.ifBlank { "Atualizações do jogo" },
                            style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.Bold,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                        val subtitle = buildList {
                            if (titleId.isNotBlank()) add(titleId.uppercase())
                            add("Atualizações")
                            if (selectedIndex == 0) add("Versão base ativa")
                            else add("Atualização $selectedIndex ativa")
                        }.joinToString(" • ")

                        Text(
                            text = subtitle,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis
                        )
                    }

                    IconButton(onClick = { openDialog.value = false }) {
                        Icon(Icons.Default.Close, contentDescription = "Fechar")
                    }
                }

                // Content Area
                if (validFiles.isEmpty()) {
                    // Empty State
                    Surface(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(230.dp),
                        shape = RoundedCornerShape(16.dp),
                        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.4f),
                        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.3f))
                    ) {
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(20.dp),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.Center
                        ) {
                            Icon(
                                imageVector = Icons.Default.SystemUpdate,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.7f),
                                modifier = Modifier.size(44.dp)
                            )
                            Spacer(Modifier.height(10.dp))
                            Text(
                                text = "Nenhuma atualização instalada",
                                style = MaterialTheme.typography.titleSmall,
                                fontWeight = FontWeight.SemiBold,
                                color = MaterialTheme.colorScheme.onSurface
                            )
                            Spacer(Modifier.height(4.dp))
                            Text(
                                text = "O jogo está rodando em sua versão base original. Adicione um arquivo de atualização para aplicar patches e correções.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                textAlign = TextAlign.Center,
                                modifier = Modifier.padding(horizontal = 24.dp)
                            )
                        }
                    }
                } else {
                    val scrollState = rememberScrollState()
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .heightIn(min = 180.dp, max = 280.dp)
                            .verticalScroll(scrollState),
                        verticalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        // Base version card
                        val isBaseSelected = selectedIndex == 0
                        Surface(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(12.dp))
                                .clickable { selectedIndex = 0 },
                            shape = RoundedCornerShape(12.dp),
                            color = if (isBaseSelected) {
                                MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.7f)
                            } else {
                                MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.3f)
                            },
                            border = BorderStroke(
                                1.dp,
                                if (isBaseSelected) MaterialTheme.colorScheme.primary.copy(alpha = 0.5f)
                                else MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)
                            )
                        ) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Switch(
                                    checked = isBaseSelected,
                                    onCheckedChange = { if (it) selectedIndex = 0 }
                                )
                                Spacer(Modifier.width(12.dp))
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "Versão base (1.0.0)",
                                        style = MaterialTheme.typography.bodyMedium,
                                        fontWeight = FontWeight.SemiBold,
                                        color = MaterialTheme.colorScheme.onSurface
                                    )
                                    Text(
                                        text = "Sem pacote de atualização aplicado",
                                        style = MaterialTheme.typography.labelSmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                            }
                        }

                        // Installed updates cards
                        validFiles.forEach { (itemIndex, path, file) ->
                            val isSelected = selectedIndex == itemIndex
                            val cleanName = cleanUpdateTitle(file.name, name)
                            val isExpanded = expandedIndex == itemIndex

                            Surface(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clip(RoundedCornerShape(12.dp))
                                    .clickable {
                                        expandedIndex = if (isExpanded) null else itemIndex
                                    },
                                shape = RoundedCornerShape(12.dp),
                                color = if (isSelected) {
                                    MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.7f)
                                } else {
                                    MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.3f)
                                },
                                border = BorderStroke(
                                    1.dp,
                                    if (isSelected) MaterialTheme.colorScheme.primary.copy(alpha = 0.5f)
                                    else MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)
                                )
                            ) {
                                Column(modifier = Modifier.padding(12.dp)) {
                                    Row(
                                        verticalAlignment = Alignment.CenterVertically,
                                        modifier = Modifier.fillMaxWidth()
                                    ) {
                                        Switch(
                                            checked = isSelected,
                                            onCheckedChange = { checked ->
                                                selectedIndex = if (checked) itemIndex else 0
                                            }
                                        )

                                        Spacer(Modifier.width(12.dp))

                                        Column(modifier = Modifier.weight(1f)) {
                                            Text(
                                                text = cleanName,
                                                style = MaterialTheme.typography.bodyMedium,
                                                fontWeight = FontWeight.SemiBold,
                                                color = MaterialTheme.colorScheme.onSurface,
                                                maxLines = if (isExpanded) 4 else 2,
                                                overflow = TextOverflow.Ellipsis
                                            )
                                            Text(
                                                text = if (isSelected) "Ativa atualmente" else "Disponível",
                                                style = MaterialTheme.typography.labelSmall,
                                                color = if (isSelected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant
                                            )
                                        }

                                        IconButton(
                                            onClick = {
                                                expandedIndex = if (isExpanded) null else itemIndex
                                            }
                                        ) {
                                            Icon(
                                                imageVector = if (isExpanded) Icons.Default.KeyboardArrowUp else Icons.Default.KeyboardArrowDown,
                                                contentDescription = if (isExpanded) "Ocultar detalhes" else "Ver detalhes",
                                                tint = MaterialTheme.colorScheme.onSurfaceVariant
                                            )
                                        }

                                        IconButton(
                                            onClick = {
                                                indexToDelete = itemIndex
                                            }
                                        ) {
                                            Icon(
                                                imageVector = Icons.Default.Delete,
                                                contentDescription = "Excluir atualização",
                                                tint = MaterialTheme.colorScheme.error
                                            )
                                        }
                                    }

                                    if (isExpanded) {
                                        Spacer(Modifier.height(8.dp))
                                        Surface(
                                            shape = RoundedCornerShape(8.dp),
                                            color = MaterialTheme.colorScheme.surface.copy(alpha = 0.6f),
                                            modifier = Modifier.fillMaxWidth()
                                        ) {
                                            Column(modifier = Modifier.padding(8.dp)) {
                                                Text(
                                                    text = "Arquivo: ${file.name}",
                                                    style = MaterialTheme.typography.bodySmall,
                                                    color = MaterialTheme.colorScheme.onSurface
                                                )
                                                Spacer(Modifier.height(2.dp))
                                                Text(
                                                    text = "Caminho: $path",
                                                    style = MaterialTheme.typography.labelSmall,
                                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                                )
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Bottom Action Bar
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    OutlinedButton(onClick = { viewModel.add() }) {
                        Icon(
                            imageVector = Icons.Default.Add,
                            contentDescription = null,
                            modifier = Modifier.size(18.dp)
                        )
                        Spacer(modifier = Modifier.width(6.dp))
                        Text("Adicionar atualização")
                    }

                    Spacer(modifier = Modifier.weight(1f))

                    OutlinedButton(onClick = { openDialog.value = false }) {
                        Text("Cancelar")
                    }

                    Button(
                        onClick = {
                            canClose.value = true
                            viewModel.save(selectedIndex, openDialog)
                        },
                        colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.primary)
                    ) {
                        Icon(
                            imageVector = Icons.Default.Check,
                            contentDescription = null,
                            modifier = Modifier.size(18.dp)
                        )
                        Spacer(modifier = Modifier.width(6.dp))
                        Text("Salvar")
                    }
                }
            }

            // Delete Confirmation Dialog
            indexToDelete?.let { targetIdx ->
                val targetFile = validFiles.firstOrNull { it.first == targetIdx }
                val targetName = targetFile?.third?.name?.let { cleanUpdateTitle(it, name) } ?: "esta atualização"

                AlertDialog(
                    onDismissRequest = { indexToDelete = null },
                    title = { Text("Excluir atualização") },
                    text = {
                        Text("Deseja remover \"$targetName\"? O arquivo será desvinculado deste jogo.")
                    },
                    confirmButton = {
                        TextButton(
                            onClick = {
                                if (selectedIndex == targetIdx) {
                                    selectedIndex = 0
                                } else if (selectedIndex > targetIdx) {
                                    selectedIndex -= 1
                                }
                                viewModel.remove(targetIdx)
                                indexToDelete = null
                            }
                        ) {
                            Text("Excluir", color = MaterialTheme.colorScheme.error)
                        }
                    },
                    dismissButton = {
                        TextButton(onClick = { indexToDelete = null }) {
                            Text("Cancelar")
                        }
                    }
                )
            }
        }
    }
}
