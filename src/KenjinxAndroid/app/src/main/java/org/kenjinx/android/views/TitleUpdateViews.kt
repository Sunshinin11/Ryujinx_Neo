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
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import org.kenjinx.android.viewmodels.TitleUpdateViewModel
import java.io.File

class TitleUpdateViews {
    companion object {
        @Composable
        fun Main(titleId: String, name: String, openDialog: MutableState<Boolean>, canClose: MutableState<Boolean>) {
            val viewModel = TitleUpdateViewModel(titleId)

            var selectedIndex by remember { mutableIntStateOf(0) }
            viewModel.data?.apply {
                selectedIndex = paths.indexOf(this.selected) + 1
            }

            Column(modifier = Modifier.padding(16.dp)) {
                Row(modifier = Modifier.align(Alignment.Start)) {
                    IconButton(
                        onClick = {
                            viewModel.remove(selectedIndex)
                        }
                    ) {
                        Icon(
                            Icons.Filled.Delete,
                            contentDescription = "Remove"
                        )
                    }
                    IconButton(
                        onClick = {
                            viewModel.add()
                        }
                    ) {
                        Icon(
                            Icons.Filled.Add,
                            contentDescription = "Add"
                        )
                    }
                    IconButton(
                        onClick = {
                            canClose.value = true
                            viewModel.save(selectedIndex, openDialog)
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
                    Text(text = "Updates for $name", textAlign = TextAlign.Center)
                    Box(
                        modifier = Modifier
                            .padding(8.dp)
                            .height(150.dp)
                    ) {
                        val scrollState = rememberScrollState()
                        val paths = remember { mutableStateListOf<String>() }
                        viewModel.setPaths(paths, canClose)

                        Surface(
                            modifier = Modifier
                                .fillMaxWidth()
                                .fillMaxHeight()
                                .padding(end = 16.dp),
                            color = MaterialTheme.colorScheme.surfaceVariant,
                            shape = MaterialTheme.shapes.medium
                        ) {
                            Column(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .verticalScroll(scrollState)
                                    .padding(8.dp)
                            ) {
                                Row(modifier = Modifier.padding(vertical = 4.dp)) {
                                    RadioButton(
                                        selected = (selectedIndex == 0),
                                        onClick = { selectedIndex = 0 }
                                    )
                                    Text(
                                        text = "None",
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .align(Alignment.CenterVertically)
                                    )
                                }

                                paths.forEachIndexed { index, path ->
                                    val itemIndex = index + 1
                                    val file = File(path)
                                    if (file.exists()) {
                                        Row(modifier = Modifier.padding(vertical = 4.dp)) {
                                            RadioButton(
                                                selected = (selectedIndex == itemIndex),
                                                onClick = { selectedIndex = itemIndex }
                                            )
                                            Text(
                                                text = file.name,
                                                modifier = Modifier
                                                    .fillMaxWidth()
                                                    .align(Alignment.CenterVertically)
                                            )
                                        }
                                    }
                                }

                                Spacer(modifier = Modifier.height(8.dp))
                            }
                        }

                        if (scrollState.maxValue > 0) {
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
