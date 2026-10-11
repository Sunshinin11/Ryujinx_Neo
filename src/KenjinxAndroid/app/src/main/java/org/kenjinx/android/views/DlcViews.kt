package org.kenjinx.android.views

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import org.kenjinx.android.viewmodels.DlcItem
import org.kenjinx.android.viewmodels.DlcViewModel

class DlcViews {
    companion object {
        @Composable
        fun Main(titleId: String, name: String, openDialog: MutableState<Boolean>, canClose: MutableState<Boolean>) {
            val viewModel = DlcViewModel(titleId)
            val dlcItems = remember { SnapshotStateList<DlcItem>() }

            Column(modifier = Modifier.padding(16.dp)) {
                Row(modifier = Modifier.align(Alignment.Start)) {
                    IconButton(
                        modifier = Modifier.padding(4.dp),
                        onClick = {
                            viewModel.add()
                        },
                    ) {
                        Icon(
                            Icons.Filled.Add,
                            contentDescription = "Add"
                        )
                    }
                    IconButton(
                        modifier = Modifier.padding(4.dp),
                        onClick = {
                            canClose.value = true
                            viewModel.save(openDialog)
                        },
                    ) {
                        Icon(
                            Icons.Filled.Edit,
                            contentDescription = "Save"
                        )
                    }
                }
                Spacer(modifier = Modifier.height(8.dp))
                Column {
                    Text(text = "DLC for $name", textAlign = TextAlign.Center)
                    Box(
                        modifier = Modifier
                            .padding(8.dp)
                            .height(150.dp)
                    ) {
                        val scrollState = rememberScrollState()

                        val needsScrollbar by remember {
                            derivedStateOf {
                                scrollState.maxValue > 0
                            }
                        }

                        viewModel.setDlcItems(dlcItems, canClose)

                        Surface(
                            modifier = Modifier
                                .fillMaxWidth()
                                .fillMaxHeight()
                                .padding(end = if (needsScrollbar) 16.dp else 0.dp),
                            color = MaterialTheme.colorScheme.surfaceVariant,
                            shape = MaterialTheme.shapes.medium
                        ) {
                            Column(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .verticalScroll(scrollState)
                                    .padding(8.dp)
                            ) {
                                dlcItems.forEach { dlcItem ->
                                    Row(
                                        modifier = Modifier
                                            .padding(vertical = 4.dp)
                                            .fillMaxWidth()
                                    ) {
                                        Checkbox(
                                            checked = dlcItem.isEnabled.value,
                                            onCheckedChange = { dlcItem.isEnabled.value = it }
                                        )
                                        Text(
                                            text = dlcItem.name,
                                            modifier = Modifier
                                                .align(Alignment.CenterVertically)
                                                .weight(1f)
                                        )
                                        IconButton(
                                            onClick = {
                                                viewModel.remove(dlcItem)
                                            }
                                        ) {
                                            Icon(
                                                Icons.Filled.Delete,
                                                contentDescription = "remove"
                                            )
                                        }
                                    }
                                }

                                Spacer(modifier = Modifier.height(8.dp))
                            }
                        }

                        if (needsScrollbar) {
                            Box(
                                modifier = Modifier
                                    .align(Alignment.CenterEnd)
                                    .width(6.dp)
                                    .fillMaxHeight()
                                    .background(Color.LightGray.copy(alpha = 0.3f))
                            ) {
                                val thumbRatio = 150.dp.value / (150.dp.value + scrollState.maxValue)
                                val thumbSize = (150.dp.value * thumbRatio).coerceAtLeast(40f)
                                val scrollRatio = scrollState.value.toFloat() / scrollState.maxValue.toFloat()
                                val maxOffset = 150.dp.value - (thumbSize * 0.7f)
                                val thumbOffset = maxOffset * scrollRatio

                                Box(
                                    modifier = Modifier
                                        .width(6.dp)
                                        .height((thumbSize * 0.7f).dp)
                                        .offset(y = thumbOffset.dp)
                                        .background(
                                            color = MaterialTheme.colorScheme.tertiary,
                                            shape = MaterialTheme.shapes.small
                                        )
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}
