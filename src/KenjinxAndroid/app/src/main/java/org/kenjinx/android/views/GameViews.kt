package org.kenjinx.android.views

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
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
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.net.toUri
import android.widget.Toast
import org.kenjinx.android.GameController
import org.kenjinx.android.GameController2
import org.kenjinx.android.GameController3
import org.kenjinx.android.GameController4
import org.kenjinx.android.GameController5
import org.kenjinx.android.GameController6
import org.kenjinx.android.GameHost
import org.kenjinx.android.MainActivity
import org.kenjinx.android.KenjinxNative
import org.kenjinx.android.viewmodels.MainViewModel
import org.kenjinx.android.viewmodels.QuickSettings
import org.kenjinx.android.viewmodels.QuickSettings.VirtualControllerPreset
import org.kenjinx.android.viewmodels.VSyncMode
import org.kenjinx.android.widgets.SimpleAlertDialog
import java.util.Locale
import kotlin.math.roundToInt

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

            val stretch = mainViewModel.getSettingsForGame().stretchToFullscreen
            val gameAspect = 16f / 9f

            if (stretch) {
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
                BoxWithConstraints(modifier = Modifier.fillMaxSize()) {
                    val containerAspect = maxWidth.value / maxHeight.value
                    val useFitWidth = if (isLandscape && isLarge) true else containerAspect < gameAspect

                    val fitModifier = if (useFitWidth) {
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
                val initialSettings = remember { mainViewModel.getSettingsForGame() }
                val showStats = remember { mutableStateOf(initialSettings.showPerformanceOverlay) }
                val showController = remember { mutableStateOf(initialSettings.useVirtualController) }
                val vSyncMode = remember { mutableStateOf(initialSettings.vSyncMode) }
                val enableMotion = remember { mutableStateOf(initialSettings.enableMotion) }
                val showMore = remember { mutableStateOf(false) }
                val showBackNotice = remember { mutableStateOf(false) }
                val gameExitRequested = mainViewModel.gameExitRequested
                val showLoading = remember { mutableStateOf(true) }
                val progressValue = remember { mutableFloatStateOf(0.0f) }
                val progress = remember { mutableStateOf("Loading") }

                fun qsLabel(name: String?, slot: Int): String =
                    if (name.isNullOrBlank()) "Slot $slot" else name

                if (showStats.value) {
                    GameStats(
                        mainViewModel,
                        modifier = Modifier
                            .align(Alignment.TopEnd)
                            .padding(top = 8.dp, end = 10.dp)
                    )
                }

                mainViewModel.setProgressStates(showLoading, progressValue, progress)

                // Touch surface when virtual controller is disabled
                Surface(
                    color = Color.Transparent,
                    modifier = Modifier
                        .fillMaxSize()
                        .padding(0.dp)
                        .pointerInput(Unit) {
                            awaitPointerEventScope {
                                while (true) {
                                    val event = awaitPointerEvent()
                                    if (showController.value) continue

                                    val change = event.component1().firstOrNull()
                                    change?.apply {
                                        val position = this.position
                                        when (event.type) {
                                            PointerEventType.Press -> KenjinxNative.inputSetTouchPoint(
                                                position.x.roundToInt(),
                                                position.y.roundToInt()
                                            )
                                            PointerEventType.Release -> KenjinxNative.inputReleaseTouchPoint()
                                            PointerEventType.Move -> KenjinxNative.inputSetTouchPoint(
                                                position.x.roundToInt(),
                                                position.y.roundToInt()
                                            )
                                        }
                                    }
                                }
                            }
                        }
                ) {}

                if (!showLoading.value) {
                    val preset = initialSettings.virtualControllerPreset
                    when (preset) {
                        VirtualControllerPreset.Default -> GameController.Compose(mainViewModel)
                        VirtualControllerPreset.Layout2  -> GameController2.Compose(mainViewModel)
                        VirtualControllerPreset.Layout3  -> GameController3.Compose(mainViewModel)
                        VirtualControllerPreset.Layout4  -> GameController4.Compose(mainViewModel)
                        VirtualControllerPreset.Layout5  -> GameController5.Compose(mainViewModel)
                        VirtualControllerPreset.Layout6  -> GameController6.Compose(mainViewModel)
                    }

                    // Invisible edge target: 20dp left edge gesture swiping right
                    Box(
                        modifier = Modifier
                            .align(Alignment.CenterStart)
                            .width(32.dp)
                            .fillMaxHeight()
                            .pointerInput(Unit) {
                                var distance = 0f
                                detectHorizontalDragGestures(
                                    onDragStart = { distance = 0f },
                                    onHorizontalDrag = { _, amount ->
                                        if (amount > 0f) {
                                            distance += amount
                                        }
                                    },
                                    onDragEnd = {
                                        if (distance >= 72f && !gameExitRequested.value) showMore.value = true
                                    }
                                )
                            }
                    )

                    if (showMore.value && !gameExitRequested.value) {
                        Box(
                            modifier = Modifier
                                .fillMaxSize()
                                .background(Color.Black.copy(alpha = 0.55f))
                                .clickable { showMore.value = false }
                        )
                        Surface(
                            modifier = Modifier
                                .align(Alignment.CenterStart)
                                .fillMaxHeight()
                                .width(320.dp),
                            color = MaterialTheme.colorScheme.surface,
                            tonalElevation = 8.dp
                        ) {
                            LazyColumn(
                                modifier = Modifier.padding(horizontal = 18.dp, vertical = 24.dp),
                                verticalArrangement = Arrangement.spacedBy(10.dp)
                            ) {
                                item {
                                    Text(
                                        "Controles rápidos",
                                        style = MaterialTheme.typography.titleLarge,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                                item {
                                    DrawerToggle("Controle na tela", showController.value) {
                                        showController.value = it
                                        KenjinxNative.inputReleaseTouchPoint()
                                        mainViewModel.controller?.setVisible(it)
                                        mainViewModel.updateActiveGameSettings { settings -> settings.useVirtualController = it }
                                    }
                                }
                                item {
                                    DrawerToggle("Estatísticas", showStats.value) {
                                        showStats.value = it
                                        mainViewModel.updateActiveGameSettings { settings -> settings.showPerformanceOverlay = it }
                                    }
                                }
                                item {
                                    DrawerToggle("VSync", vSyncMode.value == VSyncMode.Switch) { enabled ->
                                        vSyncMode.value = if (enabled) VSyncMode.Switch else VSyncMode.Unbounded
                                        KenjinxNative.graphicsRendererSetVsync(vSyncMode.value.ordinal)
                                        mainViewModel.updateActiveGameSettings { settings -> settings.vSyncMode = vSyncMode.value }
                                    }
                                }
                                item {
                                    DrawerToggle("Movimento", enableMotion.value) {
                                        enableMotion.value = it
                                        mainViewModel.updateActiveGameSettings { settings -> settings.enableMotion = it }
                                        if (it) mainViewModel.motionSensorManager?.register()
                                        else mainViewModel.motionSensorManager?.unregister()
                                    }
                                }
                                item {
                                    Text("Amiibo", style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 8.dp))
                                    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                                        (1..5).chunked(3).forEach { row ->
                                            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                                                row.forEach { slot ->
                                                    androidx.compose.material3.OutlinedButton(onClick = {
                                                        loadAmiiboSlot(mainViewModel, slot)
                                                    }) { Text(qsLabel(amiiboName(initialSettings, slot), slot)) }
                                                }
                                            }
                                        }
                                        androidx.compose.material3.TextButton(onClick = {
                                            KenjinxNative.amiiboClear()
                                            Toast.makeText(mainViewModel.activity, "Amiibo removido", Toast.LENGTH_SHORT).show()
                                        }) { Text("Remover Amiibo") }
                                    }
                                }
                                item {
                                    Surface(
                                        shape = MaterialTheme.shapes.medium,
                                        color = MaterialTheme.colorScheme.errorContainer.copy(alpha = 0.2f),
                                        border = BorderStroke(1.dp, MaterialTheme.colorScheme.error.copy(alpha = 0.5f)),
                                        modifier = Modifier
                                            .fillMaxWidth()
                                            .clickable {
                                                showMore.value = false
                                                showBackNotice.value = true
                                            }
                                    ) {
                                        Row(
                                            modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 12.dp),
                                            horizontalArrangement = Arrangement.SpaceBetween,
                                            verticalAlignment = Alignment.CenterVertically
                                        ) {
                                            Text("Sair do jogo", color = MaterialTheme.colorScheme.error)
                                            Icon(Icons.Default.Close, contentDescription = null, tint = MaterialTheme.colorScheme.error)
                                        }
                                    }
                                }
                                item {
                                    androidx.compose.material3.Button(
                                        modifier = Modifier.fillMaxWidth(),
                                        onClick = { showMore.value = false }
                                    ) { Text("Fechar") }
                                }
                            }
                        }
                    }
                }


                // Close drawer if open on back press
                BackHandler(enabled = showMore.value) {
                    showMore.value = false
                }

                // If the software keyboard is open, catch Back and close ONLY the dialog.
                val uiHandler = mainViewModel.activity.uiHandler
                BackHandler(enabled = uiHandler.showMessage.value) {
                    KenjinxNative.uiHandlerSetResponse(false, "")
                    uiHandler.showMessage.value = false
 }
 BackHandler(enabled = !showMore.value && !uiHandler.showMessage.value) {
 showBackNotice.value = true
 }

 SimpleAlertDialog.Progress(
 showDialog = showLoading,
 progressText = progress.value,
 progressValue = progressValue.floatValue
 )

 // Absorb repeated Back presses while the accepted exit is waiting for teardown.
 BackHandler(enabled = gameExitRequested.value) { }

 if (!gameExitRequested.value) {
 SimpleAlertDialog.Confirmation(
 showDialog = showBackNotice,
 title = "Sair do jogo",
                    message = "Tem certeza de que deseja sair do jogo? Todo progresso não salvo será perdido!",
 confirmText = "Sair do jogo",
 dismissText = "Continuar jogando",
 onConfirm = {
 // Hide the exit controls immediately; the shared gate also rejects already queued clicks.
 showMore.value = false
 showBackNotice.value = false
 mainViewModel.requestGameExit()
 }
 )
 }

 mainViewModel.activity.uiHandler.Compose()
 }
 }

 @Composable
 private fun DrawerToggle(label: String, checked: Boolean, onCheckedChange: (Boolean) -> Unit) {
 Surface(
 shape = MaterialTheme.shapes.medium,
 color = MaterialTheme.colorScheme.surfaceVariant,
 modifier = Modifier
 .fillMaxWidth()
 .clickable { onCheckedChange(!checked) }
 ) {
 Row(
 modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 10.dp),
 horizontalArrangement = Arrangement.SpaceBetween,
 verticalAlignment = Alignment.CenterVertically
 ) {
 Text(label)
 Switch(checked = checked, onCheckedChange = null)
 }
 }
 }

 private fun amiiboName(settings: QuickSettings, slot: Int): String? = when (slot) {
 1 -> settings.amiibo1Name
 2 -> settings.amiibo2Name
 3 -> settings.amiibo3Name
 4 -> settings.amiibo4Name
 5 -> settings.amiibo5Name
 else -> null
 }

 private fun loadAmiiboSlot(mainViewModel: MainViewModel, slot: Int) {
 val settings = mainViewModel.getSettingsForGame()
 val uri = when (slot) {
 1 -> settings.amiibo1Uri
 2 -> settings.amiibo2Uri
 3 -> settings.amiibo3Uri
 4 -> settings.amiibo4Uri
 5 -> settings.amiibo5Uri
 else -> null
 }
 val name = amiiboName(settings, slot) ?: "Slot $slot"
 if (uri.isNullOrBlank()) {
 Toast.makeText(mainViewModel.activity, "Slot $slot vazio", Toast.LENGTH_SHORT).show()
 return
 }
 try {
 val bytes = mainViewModel.activity.contentResolver.openInputStream(uri.toUri())?.use { it.readBytes() }
 val loaded = bytes != null && bytes.isNotEmpty() && KenjinxNative.amiiboLoadBin(bytes, bytes.size)
 Toast.makeText(
 mainViewModel.activity,
 if (loaded) "Carregado: $name" else "Falha ao carregar Amiibo",
 Toast.LENGTH_SHORT
 ).show()
 } catch (error: Throwable) {
 Toast.makeText(mainViewModel.activity, "Erro: ${error.message}", Toast.LENGTH_SHORT).show()
 }
 }

 @Composable
 fun GameStats(mainViewModel: MainViewModel, modifier: Modifier = Modifier) {
 val fifo = remember { mutableDoubleStateOf(0.0) }
 val gameFps = remember { mutableDoubleStateOf(0.0) }
 val gameTime = remember { mutableDoubleStateOf(0.0) }
 val usedMem = remember { mutableIntStateOf(0) }
 val totalMem = remember { mutableIntStateOf(0) }
 val cpuMode = remember { if (mainViewModel.getSettingsForGame().useNce) "NCE" else "JIT" }

 val frameTime = gameTime.doubleValue.takeIf { it.isFinite() && it >= 0.0 } ?: 0.0
 val usedRamGb = usedMem.intValue / 1024.0
 val totalRamGb = totalMem.intValue / 1024.0

 Surface(
 modifier = modifier,
 shape = CircleShape,
 color = Color.Black.copy(alpha = 0.58f),
 border = BorderStroke(1.dp, Color.White.copy(alpha = 0.18f))
 ) {
     Row(
         modifier = Modifier
             .padding(horizontal = 12.dp, vertical = 6.dp),
         horizontalArrangement = Arrangement.spacedBy(8.dp),
         verticalAlignment = Alignment.CenterVertically
     ) {
         StatMetric("CPU", cpuMode)
         StatDivider()
         StatMetric("FPS", String.format(Locale.getDefault(), "%.1f", gameFps.doubleValue))
         StatDivider()
         StatMetric("QUADRO", "${String.format(Locale.getDefault(), "%.1f", frameTime)} ms")
         StatDivider()
         StatMetric("FIFO", "${String.format(Locale.getDefault(), "%.0f", fifo.doubleValue)}%")
         StatDivider()
         StatMetric(
             "RAM",
             "${String.format(Locale.getDefault(), "%.1f", usedRamGb)}/${String.format(Locale.getDefault(), "%.1f", totalRamGb)} GB"
         )
     }
 }

 mainViewModel.setStatStates(fifo, gameFps, gameTime, usedMem, totalMem)
 }

 @Composable
 private fun StatMetric(label: String, value: String) {
     Row(
         verticalAlignment = Alignment.CenterVertically,
         horizontalArrangement = Arrangement.spacedBy(6.dp)
     ) {
         Text(label, color = Color.White.copy(alpha = 0.64f), fontSize = 9.sp, fontWeight = FontWeight.Bold)
         Text(value, color = Color.White, fontSize = 11.sp, fontWeight = FontWeight.SemiBold)
     }
 }

 @Composable
 private fun StatDivider() {
     Text("•", color = Color.White.copy(alpha = 0.32f), fontSize = 9.sp)
 }
 }
}
