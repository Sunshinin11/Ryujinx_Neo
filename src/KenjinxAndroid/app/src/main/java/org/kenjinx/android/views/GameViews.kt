package org.kenjinx.android.views

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.PointerEventType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.compose.ui.window.Popup
import androidx.compose.ui.draw.alpha
import compose.icons.CssGgIcons
import compose.icons.cssggicons.ToolbarBottom
import org.kenjinx.android.GameController
import org.kenjinx.android.GameController2
import org.kenjinx.android.GameController3
import org.kenjinx.android.GameController4
import org.kenjinx.android.GameController5
import org.kenjinx.android.GameController6
import org.kenjinx.android.GameHost
import org.kenjinx.android.Icons
import org.kenjinx.android.MainActivity
import org.kenjinx.android.KenjinxNative
import org.kenjinx.android.viewmodels.MainViewModel
import org.kenjinx.android.viewmodels.QuickSettings
import org.kenjinx.android.viewmodels.VSyncMode
import org.kenjinx.android.widgets.SimpleAlertDialog
import java.util.Locale
import kotlin.math.roundToInt
import android.widget.Toast
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.ui.platform.LocalConfiguration
import org.kenjinx.android.viewmodels.QuickSettings.VirtualControllerPreset
import androidx.core.net.toUri


class GameViews {
    companion object {
        @Composable
        fun Main() {
            Surface(
                modifier = Modifier.fillMaxSize(),
                color = Color.Black
            ) {
                GameView(mainViewModel = MainActivity.mainViewModel!!)
            }
        }

        @Composable
        fun GameView(mainViewModel: MainViewModel) {
            val cfg = LocalConfiguration.current
            val isLandscape = cfg.screenWidthDp >= cfg.screenHeightDp
            val isLarge = (cfg.smallestScreenWidthDp >= 600) || (cfg.screenWidthDp >= 900)

            // Setting from the preferences (read at game start)
            val stretch = QuickSettings(mainViewModel.activity).stretchToFullscreen

            // Standard aspect ratio (Switch 16:9). If you want to read the aspect ratio dynamically from the renderer later,
            // you can update gameAspect here at runtime.
            val gameAspect = 16f / 9f

            if (stretch) {
                // Stretch to full screen (no letterboxing), anchored at the top
                Box(
                    modifier = Modifier.fillMaxSize(),
                    contentAlignment = Alignment.TopCenter
                ) {
                    AndroidView(
                        modifier = Modifier
                            .fillMaxSize()
                            .align(Alignment.TopCenter),
                        factory = { context -> GameHost(context, mainViewModel) }
                    )
                    GameOverlay(mainViewModel)
                }
            } else {
                // Maintain letterboxing, but fix the top edge. Phones: smart-fit,
                // Tablets/Foldables in Landscape: force fitWidth (as requested).
                BoxWithConstraints(modifier = Modifier.fillMaxSize()) {
                    val containerAspect = maxWidth.value / maxHeight.value

                    val useFitWidth =
                        if (isLandscape && isLarge) true
                        else containerAspect < gameAspect

                    val fitModifier =
                        if (useFitWidth) {
                            Modifier
                                .fillMaxWidth()
                                .aspectRatio(gameAspect)
                                .align(Alignment.TopCenter)
                        } else {
                            Modifier
                                .fillMaxHeight()
                                .aspectRatio(gameAspect)
                                .align(Alignment.TopCenter)
                        }

                    Box(
                        modifier = Modifier.fillMaxSize(),
                        contentAlignment = Alignment.TopCenter
                    ) {
                        AndroidView(
                            modifier = fitModifier,
                            factory = { context -> GameHost(context, mainViewModel) }
                        )
                        GameOverlay(mainViewModel)
                    }
                }
            }
        }


        @OptIn(ExperimentalMaterial3Api::class)
        @Composable
        fun GameOverlay(mainViewModel: MainViewModel) {
            Box(modifier = Modifier.fillMaxSize()) {
                val showStats = remember { mutableStateOf(false) }
                val showController = remember { mutableStateOf(QuickSettings(mainViewModel.activity).useVirtualController) }
                val vSyncMode = remember { mutableStateOf(QuickSettings(mainViewModel.activity).vSyncMode) }
                val enableMotion = remember { mutableStateOf(QuickSettings(mainViewModel.activity).enableMotion) }
                val showMore = remember { mutableStateOf(false) }
                val showLoading = remember { mutableStateOf(true) }
                val progressValue = remember { mutableFloatStateOf(0.0f) }
                val progress = remember { mutableStateOf("Loading") }

                // --- Read overlay settings
                val overlayPositionState = remember {
                    mutableStateOf(QuickSettings(mainViewModel.activity).overlayMenuPosition)
                }
                val overlayOpacityState = remember {
                    mutableFloatStateOf(QuickSettings(mainViewModel.activity).overlayMenuOpacity.coerceIn(0f, 1f))
                }

                // Auxiliary mapping position → alignment
                fun overlayAlignment(): Alignment {
                    return when (overlayPositionState.value) {
                        QuickSettings.OverlayMenuPosition.BottomMiddle -> Alignment.BottomCenter
                        QuickSettings.OverlayMenuPosition.BottomLeft   -> Alignment.BottomStart
                        QuickSettings.OverlayMenuPosition.BottomRight  -> Alignment.BottomEnd
                        QuickSettings.OverlayMenuPosition.TopMiddle    -> Alignment.TopCenter
                        QuickSettings.OverlayMenuPosition.TopLeft      -> Alignment.TopStart
                        QuickSettings.OverlayMenuPosition.TopRight     -> Alignment.TopEnd
                    }
                }

                // helper: slot label
                fun qsLabel(name: String?, slot: Int): String =
                    if (name.isNullOrBlank()) "Slot $slot" else name

                if (showStats.value) {
                    GameStats(mainViewModel)
                }

                mainViewModel.setProgressStates(showLoading, progressValue, progress)

                // touch surface
                Surface(color = Color.Transparent, modifier = Modifier
                    .fillMaxSize()
                    .padding(0.dp)
                    .pointerInput(Unit) {
                        awaitPointerEventScope {
                            while (true) {
                                val event = awaitPointerEvent()
                                if (showController.value)
                                    continue

                                val change = event
                                    .component1()
                                    .firstOrNull()
                                change?.apply {
                                    val position = this.position

                                    when (event.type) {
                                        PointerEventType.Press -> {
                                            KenjinxNative.inputSetTouchPoint(
                                                position.x.roundToInt(),
                                                position.y.roundToInt()
                                            )
                                        }
                                        PointerEventType.Release -> {
                                            KenjinxNative.inputReleaseTouchPoint()
                                        }
                                        PointerEventType.Move -> {
                                            KenjinxNative.inputSetTouchPoint(
                                                position.x.roundToInt(),
                                                position.y.roundToInt()
                                            )
                                        }
                                    }
                                }
                            }
                        }
                    }) {
                }

                if (!showLoading.value) {
                    // Retrieve the current preset from QuickSettings (this happens again with every recomposition – so changes made after saving will also be reflected)
                    val preset = QuickSettings(mainViewModel.activity).virtualControllerPreset

                    when (preset) {
                        VirtualControllerPreset.Default -> GameController.Compose(mainViewModel)
                        VirtualControllerPreset.Layout2  -> GameController2.Compose(mainViewModel)
                        VirtualControllerPreset.Layout3  -> GameController3.Compose(mainViewModel)
                        VirtualControllerPreset.Layout4  -> GameController4.Compose(mainViewModel)
                        VirtualControllerPreset.Layout5  -> GameController5.Compose(mainViewModel)
                        VirtualControllerPreset.Layout6  -> GameController6.Compose(mainViewModel)
                    }

                    // --- Button at any corner/edge + transparency
                    Row(
                        modifier = Modifier
                            .align(overlayAlignment())
                            .padding(8.dp)
                            .alpha(overlayOpacityState.floatValue) // 0f = invisible, but still clickable
                    ) {
                        IconButton(modifier = Modifier.padding(4.dp), onClick = {
                            showMore.value = true
                        }) {
                            Icon(
                                imageVector = CssGgIcons.ToolbarBottom,
                                contentDescription = "Open Panel"
                            )
                        }
                    }

                    if (showMore.value) {
                        Popup(
                            alignment = overlayAlignment(), // --- Panel in the same position
                            onDismissRequest = { showMore.value = false }
                        ) {
                            Surface(
                                modifier = Modifier.padding(16.dp),
                                shape = MaterialTheme.shapes.medium
                            ) {
                                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                    Row(
                                        modifier = Modifier.padding(8.dp),
                                        horizontalArrangement = Arrangement.SpaceBetween
                                    ) {
                                        IconButton(modifier = Modifier.padding(4.dp), onClick = {
                                            showMore.value = false
                                            showController.value = !showController.value
                                            KenjinxNative.inputReleaseTouchPoint()
                                            mainViewModel.controller?.setVisible(showController.value)
                                        }) {
                                            Icon(
                                                imageVector = Icons.videoGame(),
                                                tint = if (showController.value) Color.Green else Color.Red,
                                                contentDescription = "Toggle Virtual Pad"
                                            )
                                        }
                                        IconButton(modifier = Modifier.padding(4.dp), onClick = {
                                            showMore.value = false
                                            if(vSyncMode.value == VSyncMode.Switch) {
                                                vSyncMode.value= VSyncMode.Unbounded
                                            } else {
                                                vSyncMode.value= VSyncMode.Switch
                                            }
                                            KenjinxNative.graphicsRendererSetVsync(
                                                vSyncMode.value.ordinal
                                            )
                                        }) {
                                            Icon(
                                                imageVector = Icons.vSync(),
                                                tint = if (vSyncMode.value == VSyncMode.Switch) Color.Green else Color.Red,
                                                contentDescription = "Toggle VSync"
                                            )
                                        }
                                        IconButton(modifier = Modifier.padding(4.dp), onClick = {
                                            showMore.value = false
                                            enableMotion.value = !enableMotion.value
                                            val settings = QuickSettings(mainViewModel.activity)
                                            settings.enableMotion = enableMotion.value
                                            settings.save()
                                            if (enableMotion.value)
                                                mainViewModel.motionSensorManager?.register()
                                            else
                                                mainViewModel.motionSensorManager?.unregister()
                                        }) {
                                            Icon(
                                                imageVector = Icons.motionSensor(),
                                                tint = if (enableMotion.value) Color.Green else Color.Red,
                                                contentDescription = "Toggle Motion Sensor"
                                            )
                                        }
                                        IconButton(modifier = Modifier.padding(4.dp), onClick = {
                                            showMore.value = false
                                            showStats.value = !showStats.value
                                        }) {
                                            Icon(
                                                imageVector = Icons.barChart(),
                                                tint = if (showStats.value) Color.Green else Color.Red,
                                                contentDescription = "Toggle Game Stats"
                                            )
                                        }
                                    }

                                    // MINIMAL ADD: Amiibo slot buttons
                                    Column(modifier = Modifier.padding(bottom = 8.dp)) {
                                        Text(text = "Amiibo Slots")

                                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                            androidx.compose.material3.Button(onClick = {
                                                val qs = QuickSettings(mainViewModel.activity)
                                                val u = qs.amiibo1Uri
                                                val name = qs.amiibo1Name ?: "Slot 1"
                                                if (u.isNullOrEmpty()) {
                                                    Toast.makeText(mainViewModel.activity, "Slot 1 is empty.", Toast.LENGTH_SHORT).show()
                                                } else {
                                                    try {
                                                        val bytes = mainViewModel.activity.contentResolver.openInputStream(u.toUri())?.use { it.readBytes() }
                                                        if (bytes != null && bytes.isNotEmpty()) {
                                                            val ok = KenjinxNative.amiiboLoadBin(bytes, bytes.size)
                                                            if (ok) Toast.makeText(mainViewModel.activity, "Loaded: $name", Toast.LENGTH_SHORT).show()
                                                            else     Toast.makeText(mainViewModel.activity, "Load failed (check log)", Toast.LENGTH_SHORT).show()
                                                        } else Toast.makeText(mainViewModel.activity, "File not readable.", Toast.LENGTH_SHORT).show()
                                                    } catch (t: Throwable) { Toast.makeText(mainViewModel.activity, "Error: ${t.message}", Toast.LENGTH_SHORT).show() }
                                                }
                                            }) { Text(qsLabel(QuickSettings(mainViewModel.activity).amiibo1Name, 1)) }

                                            androidx.compose.material3.Button(onClick = {
                                                val qs = QuickSettings(mainViewModel.activity)
                                                val u = qs.amiibo2Uri
                                                val name = qs.amiibo2Name ?: "Slot 2"
                                                if (u.isNullOrEmpty()) {
                                                    Toast.makeText(mainViewModel.activity, "Slot 2 is empty.", Toast.LENGTH_SHORT).show()
                                                } else {
                                                    try {
                                                        val bytes = mainViewModel.activity.contentResolver.openInputStream(u.toUri())?.use { it.readBytes() }
                                                        if (bytes != null && bytes.isNotEmpty()) {
                                                            val ok = KenjinxNative.amiiboLoadBin(bytes, bytes.size)
                                                            if (ok) Toast.makeText(mainViewModel.activity, "Loaded: $name", Toast.LENGTH_SHORT).show()
                                                            else     Toast.makeText(mainViewModel.activity, "Load failed (check log)", Toast.LENGTH_SHORT).show()
                                                        } else Toast.makeText(mainViewModel.activity, "File not readable.", Toast.LENGTH_SHORT).show()
                                                    } catch (t: Throwable) { Toast.makeText(mainViewModel.activity, "Error: ${t.message}", Toast.LENGTH_SHORT).show() }
                                                }
                                            }) { Text(qsLabel(QuickSettings(mainViewModel.activity).amiibo2Name, 2)) }

                                            androidx.compose.material3.Button(onClick = {
                                                val qs = QuickSettings(mainViewModel.activity)
                                                val u = qs.amiibo3Uri
                                                val name = qs.amiibo3Name ?: "Slot 3"
                                                if (u.isNullOrEmpty()) {
                                                    Toast.makeText(mainViewModel.activity, "Slot 3 is empty.", Toast.LENGTH_SHORT).show()
                                                } else {
                                                    try {
                                                        val bytes = mainViewModel.activity.contentResolver.openInputStream(u.toUri())?.use { it.readBytes() }
                                                        if (bytes != null && bytes.isNotEmpty()) {
                                                            val ok = KenjinxNative.amiiboLoadBin(bytes, bytes.size)
                                                            if (ok) Toast.makeText(mainViewModel.activity, "Loaded: $name", Toast.LENGTH_SHORT).show()
                                                            else     Toast.makeText(mainViewModel.activity, "Load failed (check log)", Toast.LENGTH_SHORT).show()
                                                        } else Toast.makeText(mainViewModel.activity, "File not readable.", Toast.LENGTH_SHORT).show()
                                                    } catch (t: Throwable) { Toast.makeText(mainViewModel.activity, "Error: ${t.message}", Toast.LENGTH_SHORT).show() }
                                                }
                                            }) { Text(qsLabel(QuickSettings(mainViewModel.activity).amiibo3Name, 3)) }
                                        }

                                        Row(modifier = Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                            androidx.compose.material3.Button(onClick = {
                                                val qs = QuickSettings(mainViewModel.activity)
                                                val u = qs.amiibo4Uri
                                                val name = qs.amiibo4Name ?: "Slot 4"
                                                if (u.isNullOrEmpty()) {
                                                    Toast.makeText(mainViewModel.activity, "Slot 4 is empty.", Toast.LENGTH_SHORT).show()
                                                } else {
                                                    try {
                                                        val bytes = mainViewModel.activity.contentResolver.openInputStream(u.toUri())?.use { it.readBytes() }
                                                        if (bytes != null && bytes.isNotEmpty()) {
                                                            val ok = KenjinxNative.amiiboLoadBin(bytes, bytes.size)
                                                            if (ok) Toast.makeText(mainViewModel.activity, "Loaded: $name", Toast.LENGTH_SHORT).show()
                                                            else     Toast.makeText(mainViewModel.activity, "Load failed (check log)", Toast.LENGTH_SHORT).show()
                                                        } else Toast.makeText(mainViewModel.activity, "File not readable.", Toast.LENGTH_SHORT).show()
                                                    } catch (t: Throwable) { Toast.makeText(mainViewModel.activity, "Error: ${t.message}", Toast.LENGTH_SHORT).show() }
                                                }
                                            }) { Text(qsLabel(QuickSettings(mainViewModel.activity).amiibo4Name, 4)) }

                                            androidx.compose.material3.Button(onClick = {
                                                val qs = QuickSettings(mainViewModel.activity)
                                                val u = qs.amiibo5Uri
                                                val name = qs.amiibo5Name ?: "Slot 5"
                                                if (u.isNullOrEmpty()) {
                                                    Toast.makeText(mainViewModel.activity, "Slot 5 is empty.", Toast.LENGTH_SHORT).show()
                                                } else {
                                                    try {
                                                        val bytes = mainViewModel.activity.contentResolver.openInputStream(u.toUri())?.use { it.readBytes() }
                                                        if (bytes != null && bytes.isNotEmpty()) {
                                                            val ok = KenjinxNative.amiiboLoadBin(bytes, bytes.size)
                                                            if (ok) Toast.makeText(mainViewModel.activity, "Loaded: $name", Toast.LENGTH_SHORT).show()
                                                            else     Toast.makeText(mainViewModel.activity, "Load failed (check log)", Toast.LENGTH_SHORT).show()
                                                        } else Toast.makeText(mainViewModel.activity, "File not readable.", Toast.LENGTH_SHORT).show()
                                                    } catch (t: Throwable) { Toast.makeText(mainViewModel.activity, "Error: ${t.message}", Toast.LENGTH_SHORT).show() }
                                                }
                                            }) { Text(qsLabel(QuickSettings(mainViewModel.activity).amiibo5Name, 5)) }

                                            androidx.compose.material3.OutlinedButton(onClick = {
                                                KenjinxNative.amiiboClear()
                                                Toast.makeText(mainViewModel.activity, "Amiibo cleared", Toast.LENGTH_SHORT).show()
                                            }) { Text("Clear") }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                val showBackNotice = remember { mutableStateOf(false) }

                // If the software keyboard is open, catch Back and close ONLY the dialog.
                val uiHandler = mainViewModel.activity.uiHandler
                BackHandler(enabled = uiHandler.showMessage.value) {
                    KenjinxNative.uiHandlerSetResponse(false, "")
                    uiHandler.showMessage.value = false
                }
                BackHandler {
                    showBackNotice.value = true
                }

                SimpleAlertDialog.Progress(
                    showDialog = showLoading,
                    progressText = progress.value,
                    progressValue = progressValue.floatValue
                )

                SimpleAlertDialog.Confirmation(
                    showDialog = showBackNotice,
                    title = "Exit Game",
                    message = "Are you sure you want to exit the game? All unsaved data will be lost!",
                    confirmText = "Exit Game",
                    dismissText = "Dismiss",
                    onConfirm = {
                        mainViewModel.closeGame()
                        mainViewModel.navController?.popBackStack()
                        mainViewModel.activity.isGameRunning = false
                    }
                )

                mainViewModel.activity.uiHandler.Compose()
            }
        }

        @Composable
        fun GameStats(mainViewModel: MainViewModel) {
            val fifo = remember { mutableDoubleStateOf(0.0) }
            val gameFps = remember { mutableDoubleStateOf(0.0) }
            val gameTime = remember { mutableDoubleStateOf(0.0) }
            val usedMem = remember { mutableIntStateOf(0) }
            val totalMem = remember { mutableIntStateOf(0) }
            val frequencies = remember { mutableListOf<Double>() }

            Surface(
                modifier = Modifier.padding(16.dp),
                color = MaterialTheme.colorScheme.background.copy(0.4f)
            ) {
                CompositionLocalProvider(LocalTextStyle provides TextStyle(fontSize = 10.sp)) {
                    Column {
                        var gameTimeVal = 0.0
                        if (!gameTime.doubleValue.isInfinite())
                            gameTimeVal = gameTime.doubleValue
                        Text(text = "${String.format(Locale.getDefault(), "%.3f", fifo.doubleValue)} %")
                        Text(text = "${String.format(Locale.getDefault(), "%.3f", gameFps.doubleValue)} FPS")
                        Text(text = "${String.format(Locale.getDefault(), "%.3f", gameTimeVal)} ms")
                        Box(modifier = Modifier.width(96.dp)) {
                            Column {
                                LazyColumn {
                                    items(count = frequencies.size) { i ->
                                        if (i < frequencies.size) {
                                            val t = frequencies[i]
                                            Row {
                                                Text(modifier = Modifier.padding(2.dp), text = "CPU $i")
                                                Spacer(Modifier.weight(1f))
                                                Text(text = "$t MHz")
                                            }
                                        }
                                    }
                                }
                                Row {
                                    Text(modifier = Modifier.padding(2.dp), text = "Used")
                                    Spacer(Modifier.weight(1f))
                                    Text(text = "${usedMem.intValue} MB")
                                }
                                Row {
                                    Text(modifier = Modifier.padding(2.dp), text = "Total")
                                    Spacer(Modifier.weight(1f))
                                    Text(text = "${totalMem.intValue} MB")
                                }
                            }
                        }
                    }
                }
            }

            mainViewModel.setStatStates(fifo, gameFps, gameTime, usedMem, totalMem, frequencies)
        }
    }
}
