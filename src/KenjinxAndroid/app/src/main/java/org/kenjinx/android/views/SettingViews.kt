package org.kenjinx.android.views
import androidx.compose.foundation.layout.PaddingValues

import org.kenjinx.android.viewmodels.QuickSettings.VirtualControllerPreset

import org.kenjinx.android.viewmodels.QuickSettings.OrientationPreference

import kotlin.concurrent.thread

import androidx.compose.ui.text.style.TextAlign

import org.kenjinx.android.views.VulkanDriverViews

import com.anggrayudi.storage.file.extension

import androidx.compose.material3.LinearProgressIndicator

import androidx.compose.ui.window.DialogProperties

import org.kenjinx.android.widgets.SimpleAlertDialog

import org.kenjinx.android.KenjinxNative
import org.kenjinx.android.RegionCode

import org.kenjinx.android.SystemLanguage

import org.kenjinx.android.MainActivity



import android.content.ActivityNotFoundException

import android.content.Intent

import android.os.Build

import android.provider.DocumentsContract

import androidx.activity.compose.BackHandler

import androidx.compose.foundation.BorderStroke

import androidx.compose.foundation.Image

import androidx.compose.foundation.background

import androidx.compose.foundation.clickable

import androidx.compose.foundation.interaction.MutableInteractionSource

import androidx.compose.foundation.layout.Arrangement

import androidx.compose.foundation.layout.Box

import androidx.compose.foundation.layout.Column

import androidx.compose.foundation.layout.Row

import androidx.compose.foundation.layout.Spacer

import androidx.compose.foundation.layout.fillMaxSize

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.widthIn

import androidx.compose.foundation.layout.height

import androidx.compose.foundation.layout.padding

import androidx.compose.foundation.layout.size

import androidx.compose.foundation.layout.width

import androidx.compose.foundation.lazy.grid.GridCells

import androidx.compose.foundation.lazy.grid.LazyVerticalGrid

import androidx.compose.foundation.lazy.grid.items

import androidx.compose.foundation.rememberScrollState

import androidx.compose.foundation.shape.CircleShape

import androidx.compose.foundation.shape.RoundedCornerShape

import androidx.compose.foundation.verticalScroll

import androidx.compose.material.icons.Icons

import androidx.compose.material.icons.automirrored.filled.ArrowBack

import androidx.compose.material.icons.automirrored.filled.ArrowForward

import androidx.compose.material.icons.filled.Build

import androidx.compose.material.icons.filled.Devices

import androidx.compose.material.icons.filled.Folder

import androidx.compose.material.icons.filled.Info

import androidx.compose.material.icons.filled.Memory

import androidx.compose.material.icons.filled.Palette

import androidx.compose.material.icons.filled.SportsEsports

import androidx.compose.material.icons.filled.Tune

import androidx.compose.material.icons.filled.ArrowDropDown

import androidx.compose.material.icons.filled.Check

import androidx.compose.material.icons.filled.SystemUpdate

import androidx.compose.material.icons.filled.Close

import androidx.compose.material.icons.filled.Delete

import androidx.compose.material.icons.filled.DeleteSweep

import androidx.compose.material.icons.filled.FolderOpen

import androidx.compose.material.icons.filled.VpnKey

import androidx.compose.material.icons.filled.CloudDownload

import androidx.compose.material3.OutlinedButton



import androidx.compose.material3.Button

import androidx.compose.material3.ButtonDefaults

import androidx.compose.material3.Icon

import androidx.compose.material3.IconButton

import androidx.compose.material3.MaterialTheme

import androidx.compose.material3.Slider

import androidx.compose.material3.Surface

import androidx.compose.material3.Switch

import androidx.compose.material3.SwitchDefaults

import androidx.compose.material3.Text

import androidx.compose.material3.TextButton

import androidx.compose.material3.DropdownMenu

import androidx.compose.material3.DropdownMenuItem

import androidx.compose.runtime.Composable

import androidx.compose.runtime.LaunchedEffect

import androidx.compose.runtime.MutableState

import androidx.compose.runtime.getValue

import androidx.compose.runtime.mutableFloatStateOf

import androidx.compose.runtime.mutableIntStateOf

import androidx.compose.runtime.mutableStateOf

import androidx.compose.runtime.remember

import androidx.compose.runtime.setValue

import androidx.compose.ui.Alignment

import androidx.compose.ui.Modifier

import androidx.compose.ui.draw.clip

import androidx.compose.ui.graphics.Color

import androidx.compose.ui.graphics.Brush

import androidx.compose.ui.graphics.vector.ImageVector

import androidx.compose.ui.layout.ContentScale

import androidx.compose.ui.res.painterResource

import androidx.compose.ui.text.font.FontWeight

import androidx.compose.ui.text.style.TextOverflow

import androidx.compose.ui.unit.dp

import androidx.compose.ui.unit.sp

import androidx.documentfile.provider.DocumentFile

import org.kenjinx.android.R

import org.kenjinx.android.viewmodels.DataImportState

import org.kenjinx.android.viewmodels.DataResetState

import org.kenjinx.android.viewmodels.FirmwareInstallState

import org.kenjinx.android.viewmodels.KeyInstallState

import org.kenjinx.android.viewmodels.MainViewModel

import org.kenjinx.android.viewmodels.MemoryConfiguration

import org.kenjinx.android.viewmodels.MemoryManagerMode

import org.kenjinx.android.viewmodels.QuickSettings

import org.kenjinx.android.viewmodels.SettingsViewModel

import org.kenjinx.android.viewmodels.VSyncMode



enum class SettingsCategory(

    val title: String,

    val description: String,

    val icon: ImageVector,

    val iconRes: Int? = null

) {

    Geral(

        title = "Geral",

        description = "Tela, estatísticas e desempenho",

        icon = Icons.Default.Tune

    ),

    Emulacao(

        title = "Emulação e CPU",

        description = "NCE, JIT, PPTC e memória",

        icon = Icons.Default.Memory

    ),

    Graficos(

        title = "Gráficos",

        description = "Driver, VSync, resolução e shaders",

        icon = Icons.Default.Palette

    ),

    Controles(

        title = "Controles",

        description = "Layout, toque, giroscópio e sensibilidade",

        icon = Icons.Default.SportsEsports

    ),

    Sistema(

        title = "Sistema",

        description = "Idioma, região, firmware e chaves",

        icon = Icons.Default.Devices

    ),

    Jogos(

        title = "Jogos e Dados",

        description = "Pastas, atualizações, DLCs e dados",

        icon = Icons.Default.Folder

    ),

    Avancado(

        title = "Avançado",

        description = "Logs, integridade e compatibilidade",

        icon = Icons.Default.Build

    ),

    Sobre(

        title = "Sobre",

        description = "Versão, projeto e créditos",

        icon = Icons.Default.Info

    )

}



class SettingViews {

    companion object {

        const val EXPANSTION_TRANSITION_DURATION = 450

        const val IMPORT_CODE = 12341

        @Composable

        fun Main(

            settingsViewModel: SettingsViewModel,

            mainViewModel: MainViewModel,

            initialCategory: String? = null

        ) {

            val loaded = remember { mutableStateOf(false) }

            val memoryManagerMode = remember { mutableStateOf(MemoryManagerMode.HostMappedUnsafe) }

            val useNce = remember { mutableStateOf(false) }

            val memoryConfiguration = remember { mutableStateOf(MemoryConfiguration.MemoryConfiguration4GiB) }

            val memorySlackEnabled = remember { mutableStateOf(true) }

            val vSyncMode = remember { mutableStateOf(VSyncMode.Switch) }

            val enableDocked = remember { mutableStateOf(false) }

            val enablePptc = remember { mutableStateOf(false) }

            val enableLowPowerPptc = remember { mutableStateOf(false) }

            val enableJitCacheEviction = remember { mutableStateOf(false) }

            val enableFsIntegrityChecks = remember { mutableStateOf(false) }

            val fsGlobalAccessLogMode = remember { mutableIntStateOf(0) }

            val ignoreMissingServices = remember { mutableStateOf(false) }

            val enableShaderCache = remember { mutableStateOf(false) }

            val enableShaderLoopSafety = remember { mutableStateOf(false) }

            val enableTextureRecompression = remember { mutableStateOf(false) }
            val enableVtgViaCompute = remember { mutableStateOf(QuickSettings(mainViewModel.activity).enableVtgViaCompute) }
            val enableNceTrackingProtection = remember { mutableStateOf(QuickSettings(mainViewModel.activity).enableNceTrackingProtection) }
            val enableRelaxedDmaCopy = remember { mutableStateOf(QuickSettings(mainViewModel.activity).enableRelaxedDmaCopy) }
            val enableVp9FFmpeg = remember { mutableStateOf(QuickSettings(mainViewModel.activity).enableVp9FFmpeg) }
            val enableEdenGpuMemory = remember { mutableStateOf(QuickSettings(mainViewModel.activity).enableEdenGpuMemory) }
            val audioBoost = remember { mutableFloatStateOf(QuickSettings(mainViewModel.activity).audioBoost) }
            val enableRejectUnmappedDescriptors = remember { mutableStateOf(QuickSettings(mainViewModel.activity).enableRejectUnmappedDescriptors) }
            val enableAsyncLarge3DUpload = remember { mutableStateOf(QuickSettings(mainViewModel.activity).enableAsyncLarge3DUpload) }

            val drawFlushMode = remember { mutableStateOf(QuickSettings(mainViewModel.activity).drawFlushMode) }

            val bufferReadbackMode = remember { mutableStateOf(QuickSettings(mainViewModel.activity).bufferReadbackMode) }

            val strictSyncMode = remember { mutableStateOf(QuickSettings(mainViewModel.activity).strictSyncMode) }

            val enableMacroHLE = remember { mutableStateOf(false) }

            val stretchToFullscreen = remember { mutableStateOf(false) }

            val resScale = remember { mutableFloatStateOf(1f) }

            val maxAnisotropy = remember { mutableFloatStateOf(0f) }

            val useVirtualController = remember { mutableStateOf(true) }

            val showKeyDialog = remember { mutableStateOf(false) }

            val keyInstallState = remember { mutableStateOf(KeyInstallState.File) }

            val showFirwmareDialog = remember { mutableStateOf(false) }

            val firmwareInstallState = remember { mutableStateOf(FirmwareInstallState.File) }

            val firmwareVersion = remember { mutableStateOf(mainViewModel.firmwareVersion) }

            val showDataResetDialog = remember { mutableStateOf(false) }
            val showDecompressionDialog = remember { mutableStateOf(false) }

            if (showDecompressionDialog.value) {
                DecompressionDialog(
                    onDismiss = { showDecompressionDialog.value = false },
                    onFinished = { success, message ->
                        android.widget.Toast.makeText(mainViewModel.activity, message, android.widget.Toast.LENGTH_LONG).show()
                        if (success) mainViewModel.homeViewModel.requestReload()
                    }
                )
            }

            val showDataImportDialog = remember { mutableStateOf(false) }

            val dataResetState = remember { mutableStateOf(DataResetState.Query) }

            val dataImportState = remember { mutableStateOf(DataImportState.File) }

            val dataFile = remember { mutableStateOf<DocumentFile?>(null) }

            val isGrid = remember { mutableStateOf(true) }

            val useSwitchLayout = remember { mutableStateOf(false) }

            val enableMotion = remember { mutableStateOf(true) }

            val vcPreset = remember {

                mutableStateOf(QuickSettings(mainViewModel.activity).virtualControllerPreset)

            }

            val enablePerformanceMode = remember { mutableStateOf(true) }

            val showPerformanceOverlay = remember {

                mutableStateOf(QuickSettings(mainViewModel.activity).showPerformanceOverlay)

            }

            val controllerStickSensitivity = remember { mutableFloatStateOf(1.0f) }

            val controllerScale = remember {

                mutableFloatStateOf(QuickSettings(mainViewModel.activity).controllerScale)

            }



            val enableStubLogs = remember { mutableStateOf(true) }

            val enableInfoLogs = remember { mutableStateOf(true) }

            val enableWarningLogs = remember { mutableStateOf(true) }

            val enableErrorLogs = remember { mutableStateOf(true) }

            val enableGuestLogs = remember { mutableStateOf(true) }

            val enableFsAccessLogs = remember { mutableStateOf(true) }

            val enableTraceLogs = remember { mutableStateOf(true) }

            val enableDebugLogs = remember { mutableStateOf(true) }

            val enableGraphicsLogs = remember { mutableStateOf(true) }



            val orientationPref = remember {

                mutableStateOf(QuickSettings(mainViewModel.activity).orientationPreference)

            }

            val systemLanguage = remember { mutableStateOf(SystemLanguage.AmericanEnglish) }

            val regionCode = remember { mutableStateOf(RegionCode.USA) }



            val disableThreadedRendering = remember {

                mutableStateOf(QuickSettings(mainViewModel.activity).disableThreadedRendering)

            }

            val threadToggleInitialized = remember { mutableStateOf(false) }

            val isDriverSelectorOpen = remember { mutableStateOf(false) }



            val selectedCategory = remember(initialCategory) {

                mutableStateOf(SettingsCategory.entries.firstOrNull { it.name == initialCategory })

            }



            if (!loaded.value) {

                settingsViewModel.initializeState(

                    memoryManagerMode,

                    useNce,

                    memoryConfiguration,

                    memorySlackEnabled,

                    vSyncMode,

                    enableDocked,

                    enablePptc,

                    enableLowPowerPptc,

                    enableJitCacheEviction,

                    enableFsIntegrityChecks,

                    fsGlobalAccessLogMode,

                    ignoreMissingServices,

                    enableShaderCache,

                    enableShaderLoopSafety,

                    enableTextureRecompression,
                    enableVtgViaCompute,
                    drawFlushMode,
                    bufferReadbackMode,
                    strictSyncMode,

                    enableMacroHLE,

                    stretchToFullscreen,

                    resScale,

                    maxAnisotropy,

                    useVirtualController,

                    isGrid,

                    useSwitchLayout,

                    enableMotion,

                    enablePerformanceMode,

                    controllerStickSensitivity,

                    enableStubLogs,

                    enableInfoLogs,

                    enableWarningLogs,

                    enableErrorLogs,

                    enableGuestLogs,

                    enableFsAccessLogs,

                    enableTraceLogs,

                    enableDebugLogs,

                    enableGraphicsLogs,

                    showPerformanceOverlay,

                    vcPreset,

                    systemLanguage,

                    regionCode

                )

                loaded.value = true

            }



            val save = {

                settingsViewModel.save(

                    memoryManagerMode,

                    useNce,

                    memoryConfiguration,

                    memorySlackEnabled,

                    vSyncMode,

                    enableDocked,

                    enablePptc,

                    enableLowPowerPptc,

                    enableJitCacheEviction,

                    enableFsIntegrityChecks,

                    fsGlobalAccessLogMode,

                    ignoreMissingServices,

                    enableShaderCache,

                    enableShaderLoopSafety,

                    enableTextureRecompression,
                    enableVtgViaCompute,
                    drawFlushMode,
                    bufferReadbackMode,
                    strictSyncMode,

                    enableMacroHLE,

                    stretchToFullscreen,

                    resScale,

                    maxAnisotropy,

                    useVirtualController,

                    isGrid,

                    useSwitchLayout,

                    enableMotion,

                    enablePerformanceMode,

                    controllerStickSensitivity,

                    enableStubLogs,

                    enableInfoLogs,

                    enableWarningLogs,

                    enableErrorLogs,

                    enableGuestLogs,

                    enableFsAccessLogs,

                    enableTraceLogs,

                    enableDebugLogs,

                    enableGraphicsLogs,

                    showPerformanceOverlay,

                    vcPreset,

                    systemLanguage,

                    regionCode

                )

                val qs = QuickSettings(mainViewModel.activity)

                qs.controllerScale = controllerScale.floatValue

                qs.disableThreadedRendering = disableThreadedRendering.value

                qs.showPerformanceOverlay = showPerformanceOverlay.value

                qs.virtualControllerPreset = vcPreset.value

                qs.orientationPreference = orientationPref.value
                qs.enableVtgViaCompute = enableVtgViaCompute.value
                qs.enableNceTrackingProtection = enableNceTrackingProtection.value
                qs.enableRelaxedDmaCopy = enableRelaxedDmaCopy.value
                qs.enableVp9FFmpeg = enableVp9FFmpeg.value
                qs.enableEdenGpuMemory = enableEdenGpuMemory.value
                qs.audioBoost = audioBoost.floatValue
                qs.enableRejectUnmappedDescriptors = enableRejectUnmappedDescriptors.value
                qs.enableAsyncLarge3DUpload = enableAsyncLarge3DUpload.value
                qs.drawFlushMode = drawFlushMode.value
                qs.bufferReadbackMode = bufferReadbackMode.value
                qs.strictSyncMode = strictSyncMode.value

                qs.save()

            }



            // Hardware Back handling

            BackHandler(enabled = selectedCategory.value != null) {

                save()

                selectedCategory.value = null

            }

            BackHandler(enabled = selectedCategory.value == null) {

                save()

                mainViewModel.navController?.popBackStack()

            }



            // Main Dual Panel Container

            Box(

                modifier = Modifier

                    .fillMaxSize()

                    .background(

                        Brush.verticalGradient(

                            listOf(

                                MaterialTheme.colorScheme.background,

                                MaterialTheme.colorScheme.surfaceVariant

                            )

                        )

                    )

            ) {

                Column(

                    modifier = Modifier

                        .fillMaxSize()

                        .padding(horizontal = 16.dp, vertical = 10.dp),

                    verticalArrangement = Arrangement.spacedBy(10.dp)

                ) {

                    val activeCat = selectedCategory.value



                    // Caixinha Superior (Header Panel)

                    Surface(

                        modifier = Modifier

                            .fillMaxWidth()

                            .height(60.dp),

                        shape = RoundedCornerShape(0.dp),

                        color = Color.Transparent,

                        shadowElevation = 0.dp

                    ) {

                        Row(

                            modifier = Modifier

                                .fillMaxSize()

                                .padding(horizontal = 16.dp),

                            verticalAlignment = Alignment.CenterVertically,

                            horizontalArrangement = Arrangement.SpaceBetween

                        ) {

                            if (activeCat == null) {

                                // Root header: clean navigation, without duplicated branding.

                                Row(

                                    verticalAlignment = Alignment.CenterVertically,

                                    horizontalArrangement = Arrangement.spacedBy(12.dp)

                                ) {

                                    IconButton(

                                        onClick = {

                                            save()

                                            mainViewModel.navController?.popBackStack()

                                        }

                                    ) {

                                        Icon(

                                            imageVector = Icons.AutoMirrored.Filled.ArrowBack,

                                            contentDescription = "Voltar",

                                            tint = MaterialTheme.colorScheme.onBackground

                                        )

                                    }

                                    Text(

                                        "Configurações",

                                        fontWeight = FontWeight.Bold,

                                        fontSize = 22.sp,

                                        color = MaterialTheme.colorScheme.onBackground

                                    )

                                }

                            } else {

                                // Dedicated Section Header: Back Arrow, Title, Subtitle, Save button

                                Row(

                                    verticalAlignment = Alignment.CenterVertically,

                                    horizontalArrangement = Arrangement.spacedBy(12.dp),

                                    modifier = Modifier.weight(1f, fill = false)

                                ) {

                                    IconButton(

                                        onClick = {

                                            save()

                                            selectedCategory.value = null

                                        }

                                    ) {

                                        Icon(

                                            imageVector = Icons.AutoMirrored.Filled.ArrowBack,

                                            contentDescription = "Voltar às Configurações",

                                            tint = MaterialTheme.colorScheme.onBackground

                                        )

                                    }

                                    Column(modifier = Modifier.weight(1f, fill = false)) {

                                        Text(

                                            text = activeCat.title,

                                            fontWeight = FontWeight.Bold,

                                            fontSize = 18.sp,

                                            color = MaterialTheme.colorScheme.onBackground,

                                            maxLines = 1,

                                            overflow = TextOverflow.Ellipsis

                                        )

                                        Text(

                                            text = activeCat.description,

                                            fontSize = 12.sp,

                                            color = MaterialTheme.colorScheme.onSurfaceVariant,

                                            maxLines = 1,

                                            overflow = TextOverflow.Ellipsis

                                        )

                                    }

                                }

                                Button(

                                    onClick = {

                                        save()

                                        selectedCategory.value = null

                                    },

                                    contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),

                                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.primary)

                                ) {

                                    Icon(Icons.Default.Check, contentDescription = null, modifier = Modifier.size(18.dp))

                                    Spacer(Modifier.width(6.dp))

                                    Text("Salvar", fontWeight = FontWeight.Bold, maxLines = 1, softWrap = false)

                                }

                            }

                        }

                    }



                    // Caixinha Principal (Content Panel)

                    Surface(

                        modifier = Modifier

                            .fillMaxWidth()

                            .weight(1f),

                        shape = RoundedCornerShape(0.dp),

                        color = Color.Transparent,

                        shadowElevation = 0.dp

                    ) {

                        if (activeCat == null) {

                            // Grade de Cards de Navegação (Menu Principal de Configurações)

                            LazyVerticalGrid(

                                columns = GridCells.Adaptive(minSize = 280.dp),

                                contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),

                                horizontalArrangement = Arrangement.spacedBy(14.dp),

                                verticalArrangement = Arrangement.spacedBy(14.dp),

                                modifier = Modifier.fillMaxSize()

                            ) {

                                items(SettingsCategory.entries) { cat ->

                                    CategoryNavCard(cat) {

                                        selectedCategory.value = cat

                                    }

                                }

                            }

                        } else {

                            // Focused category content. Navigation stays in the main settings menu.

                            Row(

                                modifier = Modifier

                                    .fillMaxSize()

                                    .padding(12.dp)

                            ) {

                                Surface(

                                    modifier = Modifier.fillMaxSize(),

                                    shape = RoundedCornerShape(24.dp),

                                    color = MaterialTheme.colorScheme.surface,

                                    border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline),

                                    shadowElevation = 1.dp

                                ) {

                                    Column(

                                        modifier = Modifier

                                            .fillMaxSize()

                                            .padding(horizontal = 24.dp, vertical = 18.dp)

                                            .verticalScroll(rememberScrollState()),

                                        verticalArrangement = Arrangement.spacedBy(16.dp)

                                    ) {

                                    when (activeCat) {

                                    SettingsCategory.Geral -> {

                                        SectionTitle("Orientação da Tela")

                                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {

                                            listOf<Pair<OrientationPreference, String>>(

                                                OrientationPreference.SensorLandscape to "Horizontal",

                                                OrientationPreference.SensorPortrait to "Vertical",

                                                OrientationPreference.Sensor to "Sensor"

                                            ).forEach { (pref, label) ->

                                                ChoiceCard(

                                                    label = label,

                                                    selected = orientationPref.value == pref,

                                                    modifier = Modifier.weight(1f)

                                                ) {

                                                    orientationPref.value = pref

                                                    val qsOri = QuickSettings(mainViewModel.activity); qsOri.orientationPreference = pref; qsOri.save()

                                                }

                                            }

                                        }



                                        HighContrastToggle(

                                            title = "Mostrar estatísticas de desempenho",

                                            description = "Exibe FPS, tempos de quadro e uso de memória sobre a tela do jogo",

                                            checked = showPerformanceOverlay.value,

                                            onCheckedChange = {
                                                showPerformanceOverlay.value = it
                                                val qsSpo = QuickSettings(mainViewModel.activity); qsSpo.showPerformanceOverlay = it; qsSpo.save()
                                            }

                                        )



                                        HighContrastToggle(

                                            title = "Modo de alto desempenho",

                                            description = "Solicita maior prioridade de execução para reduzir quedas de desempenho",

                                            checked = enablePerformanceMode.value,

                                            onCheckedChange = { enablePerformanceMode.value = it }

                                        )

                                    }



                                    SettingsCategory.Emulacao -> {

                                        SectionTitle("Execução da CPU")

                                        HighContrastToggle(

                                            title = "Usar execução nativa (NCE)",

                                            description = "Ativado usa NCE; desativado usa compilação dinâmica JIT",

                                            checked = useNce.value,

                                            onCheckedChange = { useNce.value = it }

                                        )

                                        HighContrastToggle(

                                            title = "Proteção de memória NCE",

                                            description = "Mantém o rastreamento de memória da GPU quando o jogo muda permissões de memória em NCE. Corrige tela preta em alguns jogos (ex.: MUA3), mas pode causar travamentos em outros (ex.: MP4). Pode ser ajustado por jogo. (Requer reiniciar o jogo)",

                                            checked = enableNceTrackingProtection.value,

                                            onCheckedChange = {

                                                enableNceTrackingProtection.value = it

                                                val qsNceProtection = QuickSettings(mainViewModel.activity)

                                                qsNceProtection.enableNceTrackingProtection = it

                                                qsNceProtection.save()

                                            }

                                        )



                                        HighContrastToggle(

                                            title = "Cache PPTC (Compilação em Perfil)",

                                            description = "Salva código traduzido em disco para reduzir o tempo de inicialização futura",

                                            checked = enablePptc.value,

                                            onCheckedChange = { enablePptc.value = it }

                                        )



                                        HighContrastToggle(

                                            title = "PPTC de baixo consumo",

                                            description = "Reduz o consumo de bateria limitando threads de compilação em segundo plano",

                                            checked = enableLowPowerPptc.value,

                                            onCheckedChange = { enableLowPowerPptc.value = it }

                                        )



                                        HighContrastToggle(

                                            title = "Limpeza de cache do JIT",

                                            description = "Descarta periodicamente blocos de tradução órfãos na memória",

                                            checked = enableJitCacheEviction.value,

                                            onCheckedChange = { enableJitCacheEviction.value = it }

                                        )



                                        HighContrastToggle(

                                            title = "Memory Slack de 12 GiB",

                                            description = "Amplia o espaço de endereçamento reservado; desativado usa 0 GiB adicional",

                                            checked = memorySlackEnabled.value,

                                            onCheckedChange = { memorySlackEnabled.value = it }

                                        )



                                        HighContrastToggle(

                                            title = "Gerenciador de memória seguro",

                                            description = "Ativado usa tabela de páginas por software; desativado usa host mapeado, mais rápido",

                                            checked = memoryManagerMode.value == MemoryManagerMode.SoftwarePageTable,

                                            onCheckedChange = {

                                                memoryManagerMode.value = if (it) MemoryManagerMode.SoftwarePageTable else MemoryManagerMode.HostMappedUnsafe

                                            }

                                        )



                                        SectionTitle("Tamanho da DRAM")

                                        val dramOptions = listOf<Pair<MemoryConfiguration, String>>(

                                            MemoryConfiguration.MemoryConfiguration4GiB to "4 GiB (Padrão)",

                                            MemoryConfiguration.MemoryConfiguration6GiB to "6 GiB",

                                            MemoryConfiguration.MemoryConfiguration8GiB to "8 GiB",

                                            MemoryConfiguration.MemoryConfiguration12GiB to "12 GiB"

                                        )

                                        CompactSelect(

                                            selectedLabel = dramOptions.first { it.first == memoryConfiguration.value }.second,

                                            options = dramOptions,

                                            onSelected = { memoryConfiguration.value = it }

                                        )

                                    }



                                    SettingsCategory.Graficos -> {

                                        SectionTitle("Driver Vulkan")

                                        ActionButtonRow(

                                            title = "Selecionar Driver Gráfico",

                                            description = "Gerenciar drivers Vulkan personalizados e do sistema",

                                            buttonText = "Configurar driver"

                                        ) { isDriverSelectorOpen.value = true }



                                        HighContrastToggle(

                                            title = "Limitar à velocidade do Switch",

                                            description = "Ativado respeita o limite do jogo; desativado libera a taxa de quadros",

                                            checked = vSyncMode.value == VSyncMode.Switch,

                                            onCheckedChange = { vSyncMode.value = if (it) VSyncMode.Switch else VSyncMode.Unbounded }

                                        )



                                        SectionTitle("Escala de Resolução")

                                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {

                                            listOf<Pair<Float, String>>(0.5f to "0.5x", 0.75f to "0.75x", 1.0f to "1x", 2.0f to "2x", 3.0f to "3x").forEach { (scale, label) ->

                                                ChoiceCard(

                                                    label = label,

                                                    selected = resScale.floatValue == scale,

                                                    modifier = Modifier.weight(1f)

                                                ) { resScale.floatValue = scale }

                                            }

                                        }



                                        SectionTitle("Filtro Anisotrópico")

                                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {

                                            listOf<Pair<Float, String>>(-1.0f to "Automático", 4.0f to "4x", 8.0f to "8x", 16.0f to "16x").forEach { (aniso, label) ->

                                                ChoiceCard(

                                                    label = label,

                                                    selected = maxAnisotropy.floatValue == aniso,

                                                    modifier = Modifier.weight(1f)

                                                ) { maxAnisotropy.floatValue = aniso }

                                            }

                                        }



                                        HighContrastToggle(

                                            title = "Modo Docked (TV)",

                                            description = "Aumenta resolução interna para perfil docked quando suportado",

                                            checked = enableDocked.value,

                                            onCheckedChange = { enableDocked.value = it }

                                        )



                                        HighContrastToggle(

                                            title = "Cache de Shaders",

                                            description = "Reutiliza shaders compilados para reduzir recompilações e engasgos",

                                            checked = enableShaderCache.value,

                                            onCheckedChange = { enableShaderCache.value = it }

                                        )



                                        HighContrastToggle(

                                            title = "Proteção de Loops em Shaders (Shader Loop Safety)",

                                            description = "Limita loops de shader que podem prender a GPU; pode causar falhas visuais em alguns jogos",

                                            checked = enableShaderLoopSafety.value,

                                            onCheckedChange = { enableShaderLoopSafety.value = it }

                                        )



                                        HighContrastToggle(

                                            title = "Recompressão de Texturas",

                                            description = "Reduz o uso de VRAM recomprimindo formatos pesados de textura",

                                            checked = enableTextureRecompression.value,

                                            onCheckedChange = { enableTextureRecompression.value = it }

                                        )

                                        HighContrastToggle(
                                            title = "VTG via Compute",
                                            description = "Executa determinados caminhos de Vertex/Geometry através de Compute para melhorar compatibilidade com shaders que dependem de semânticas não preservadas corretamente pelo caminho gráfico nativo. (Requer reiniciar o jogo)",
                                            checked = enableVtgViaCompute.value,
                                            onCheckedChange = {
                                                enableVtgViaCompute.value = it
                                                val qs = QuickSettings(mainViewModel.activity)
                                                qs.enableVtgViaCompute = it
                                                qs.save()
                                            }
                                        )

                                        SectionTitle("Sincronização rápida de Draw")

                                        val drawFlushOptions = listOf<Pair<Int, String>>(
                                            0 to "Padrão",
                                            1 to "Reduzido",
                                            2 to "Desativado"
                                        )

                                        CompactSelect(
                                            selectedLabel = drawFlushOptions.first { it.first == drawFlushMode.value }.second,
                                            options = drawFlushOptions,
                                            onSelected = {
                                                drawFlushMode.value = it
                                                val qsDrawFlush = QuickSettings(mainViewModel.activity)
                                                qsDrawFlush.drawFlushMode = it
                                                qsDrawFlush.save()
                                            }
                                        )

                                        SectionTitle("Leitura de Buffer da GPU")

                                        val bufferReadbackOptions = listOf<Pair<Int, String>>(
                                            0 to "Padrão",
                                            1 to "Relaxada"
                                        )

                                        CompactSelect(
                                            selectedLabel = bufferReadbackOptions.first { it.first == bufferReadbackMode.value }.second,
                                            options = bufferReadbackOptions,
                                            onSelected = {
                                                bufferReadbackMode.value = it
                                                val qsBufferReadback = QuickSettings(mainViewModel.activity)
                                                qsBufferReadback.bufferReadbackMode = it
                                                qsBufferReadback.save()
                                            }
                                        )
                                        HighContrastToggle(
                                            title = "Cópia DMA relaxada",
                                            description = "Cópias DMA deixam de baixar da GPU as texturas que serão sobrescritas, como no Eden. Aumenta o desempenho em alguns jogos (ex.: Diablo II), mas pode causar erros gráficos em outros. Pode ser ajustado por jogo. (Requer reiniciar o jogo)",
                                            checked = enableRelaxedDmaCopy.value,
                                            onCheckedChange = {
                                                enableRelaxedDmaCopy.value = it
                                                val qsRelaxedDma = QuickSettings(mainViewModel.activity)
                                                qsRelaxedDma.enableRelaxedDmaCopy = it
                                                qsRelaxedDma.save()
                                            }
                                        )
                                        HighContrastToggle(
                                            title = "Memória de GPU estilo Eden",
                                            description = "Como no Eden: buffers e texturas sem uso são liberados quando a memória de GPU passa de 1,6 GB (mais forte a partir de 3,2 GB). Se a RAM do Android acabar, o jogo é fechado em vez de travar o aparelho (ex.: DOOM Eternal). Ligado por padrão; pode ser ajustado por jogo. (Requer reiniciar o jogo)",
                                            checked = enableEdenGpuMemory.value,
                                            onCheckedChange = {
                                                enableEdenGpuMemory.value = it
                                                val qsEdenGpuMemory = QuickSettings(mainViewModel.activity)
                                                qsEdenGpuMemory.enableEdenGpuMemory = it
                                                qsEdenGpuMemory.save()
                                            }
                                        )
                                        HighContrastToggle(
                                            title = "VP9 pelo FFmpeg (Eden)",
                                            description = "Vídeos VP9 dos jogos (ex.: abertura do Zelda: Link's Awakening) passam a ser decodificados pelo FFmpeg, como no Eden, em vez do decodificador interno. Ligado por padrão. Se o vídeo de algum jogo ficar com erros (ex.: GRID Autosport), desligue nas configurações daquele jogo. (Requer reiniciar o jogo)",
                                            checked = enableVp9FFmpeg.value,
                                            onCheckedChange = {
                                                enableVp9FFmpeg.value = it
                                                val qsVp9 = QuickSettings(mainViewModel.activity)
                                                qsVp9.enableVp9FFmpeg = it
                                                qsVp9.save()
                                            }
                                        )
                                        HighContrastToggle(
                                            title = "Rejeitar descritores não mapeados",
                                            description = "Descritores de textura com endereço não mapeado são descartados antes de ler o formato. Desligado por padrão global. Pode ser ajustado por jogo. (Requer reiniciar o jogo)",
                                            checked = enableRejectUnmappedDescriptors.value,
                                            onCheckedChange = {
                                                enableRejectUnmappedDescriptors.value = it
                                                val qsUp = QuickSettings(mainViewModel.activity)
                                                qsUp.enableRejectUnmappedDescriptors = it
                                                qsUp.save()
                                            }
                                        )
                                        HighContrastToggle(
                                            title = "Upload assíncrono de texturas 3D (estilo Eden)",
                                            description = "Texturas 3D grandes (4 MB ou mais) são convertidas em segundo plano e enviadas à GPU num frame seguinte. Ativado por padrão global. Pode ser ajustado por jogo. (Requer reiniciar o jogo)",
                                            checked = enableAsyncLarge3DUpload.value,
                                            onCheckedChange = {
                                                enableAsyncLarge3DUpload.value = it
                                                val qsUp = QuickSettings(mainViewModel.activity)
                                                qsUp.enableAsyncLarge3DUpload = it
                                                qsUp.save()
                                            }
                                        )

                                        SectionTitle("Sincronização estrita da GPU")

                                        val strictSyncOptions = listOf<Pair<Int, String>>(
                                            0 to "Padrão",
                                            1 to "Imediata"
                                        )

                                        CompactSelect(
                                            selectedLabel = strictSyncOptions.first { it.first == strictSyncMode.value }.second,
                                            options = strictSyncOptions,
                                            onSelected = {
                                                strictSyncMode.value = it
                                                val qsStrictSync = QuickSettings(mainViewModel.activity)
                                                qsStrictSync.strictSyncMode = it
                                                qsStrictSync.save()
                                            }
                                        )



                                        HighContrastToggle(

                                            title = "Macro HLE",

                                            description = "Acelera operações gráficas através de emulação de alto nível",

                                            checked = enableMacroHLE.value,

                                            onCheckedChange = { enableMacroHLE.value = it }

                                        )



                                        HighContrastToggle(

                                            title = "Esticar para Tela Inteira",

                                            description = "Preenche as bordas da tela ignorando a proporção 16:9",

                                            checked = stretchToFullscreen.value,

                                            onCheckedChange = { stretchToFullscreen.value = it }

                                        )



                                        HighContrastToggle(

                                            title = "Renderização em Múltiplas Threads",

                                            description = "Processa comandos gráficos em uma thread dedicada; pode melhorar fluidez ou compatibilidade",

                                            checked = !disableThreadedRendering.value,

                                            onCheckedChange = {

                                                disableThreadedRendering.value = !it

                                                val qsDtr = QuickSettings(mainViewModel.activity); qsDtr.disableThreadedRendering = !it; qsDtr.save()

                                            }

                                        )

                                    }



                                    SettingsCategory.Controles -> {

                                        HighContrastToggle(

                                            title = "Controle Virtual na Tela",

                                            description = "Exibe os botões virtuais sobre a tela do jogo",

                                            checked = useVirtualController.value,

                                            onCheckedChange = { useVirtualController.value = it }

                                        )



                                        SectionTitle("Layout Predefinido do Controle")

                                        val controllerLayouts = listOf<Pair<QuickSettings.VirtualControllerPreset, String>>(

                                            QuickSettings.VirtualControllerPreset.Default to "Padrão",

                                            QuickSettings.VirtualControllerPreset.Layout2 to "Layout 2",

                                            QuickSettings.VirtualControllerPreset.Layout3 to "Layout 3",

                                            QuickSettings.VirtualControllerPreset.Layout4 to "Layout 4",

                                            QuickSettings.VirtualControllerPreset.Layout5 to "Layout 5",

                                            QuickSettings.VirtualControllerPreset.Layout6 to "Layout 6"

                                        )

                                        CompactSelect(

                                            selectedLabel = controllerLayouts.first { it.first == vcPreset.value }.second,

                                            options = controllerLayouts,

                                            onSelected = {

                                                vcPreset.value = it

                                                val qsVc = QuickSettings(mainViewModel.activity); qsVc.virtualControllerPreset = it; qsVc.save()

                                            }

                                        )



                                        HighContrastToggle(

                                            title = "Inverter A/B e X/Y (Layout Switch)",

                                            description = "Troca a posição física dos botões; desativado por padrão",

                                            checked = useSwitchLayout.value,

                                            onCheckedChange = { useSwitchLayout.value = it }

                                        )



                                        HighContrastToggle(

                                            title = "Giroscópio / Sensor de Movimento",

                                            description = "Utiliza os sensores do aparelho como sensor de movimento dos Joy-Cons",

                                            checked = enableMotion.value,

                                            onCheckedChange = { enableMotion.value = it }

                                        )

                                        // NEOV2-HOTKEY2: o proprio usuario define o botao ou a combinacao que fecha o emulador.
                                        SectionTitle("Atalho para voltar à lista de jogos")

                                        val exitHotkeyKeys = remember { mutableStateOf(org.kenjinx.android.ExitHotkey.load(mainViewModel.activity)) }
                                        val capturingExitHotkey = remember { mutableStateOf(false) }

                                        Text(
                                            text = "Atual: " + org.kenjinx.android.ExitHotkey.describe(exitHotkeyKeys.value),
                                            modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp)
                                        )

                                        if (capturingExitHotkey.value) {
                                            val heldKeys = remember { HashSet<Int>() }
                                            val capturedKeys = remember { mutableStateOf(listOf<Int>()) }

                                            androidx.compose.runtime.DisposableEffect(Unit) {
                                                org.kenjinx.android.ExitHotkey.captureListener = { event ->
                                                    val code = event.keyCode

                                                    if (!org.kenjinx.android.ExitHotkey.isCapturable(code)) {
                                                        false
                                                    } else {
                                                        if (event.action == android.view.KeyEvent.ACTION_DOWN && event.repeatCount == 0) {
                                                            heldKeys.add(code)
                                                            if (!capturedKeys.value.contains(code) &&
                                                                capturedKeys.value.size < org.kenjinx.android.ExitHotkey.MAX_KEYS
                                                            ) {
                                                                capturedKeys.value = capturedKeys.value + code
                                                            }
                                                        } else if (event.action == android.view.KeyEvent.ACTION_UP) {
                                                            heldKeys.remove(code)

                                                            if (heldKeys.isEmpty() && capturedKeys.value.isNotEmpty()) {
                                                                org.kenjinx.android.ExitHotkey.save(mainViewModel.activity, capturedKeys.value)
                                                                exitHotkeyKeys.value = capturedKeys.value
                                                                org.kenjinx.android.ExitHotkey.captureListener = null
                                                                capturingExitHotkey.value = false
                                                            }
                                                        }

                                                        true
                                                    }
                                                }

                                                onDispose {
                                                    org.kenjinx.android.ExitHotkey.captureListener = null
                                                }
                                            }

                                            Text(
                                                text = "Aperte e segure no controle o botão ou a combinação (até 4 botões) e depois solte. " +
                                                    "Capturado: " + org.kenjinx.android.ExitHotkey.describe(capturedKeys.value),
                                                modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp)
                                            )

                                            TextButton(
                                                onClick = { capturingExitHotkey.value = false },
                                                modifier = Modifier.padding(horizontal = 8.dp)
                                            ) {
                                                Text("Cancelar")
                                            }
                                        } else {
                                            Row(
                                                modifier = Modifier
                                                    .fillMaxWidth()
                                                    .padding(horizontal = 16.dp, vertical = 4.dp),
                                                horizontalArrangement = Arrangement.spacedBy(8.dp)
                                            ) {
                                                Button(onClick = { capturingExitHotkey.value = true }) {
                                                    Text("Definir atalho")
                                                }

                                                OutlinedButton(
                                                    onClick = {
                                                        org.kenjinx.android.ExitHotkey.clear(mainViewModel.activity)
                                                        exitHotkeyKeys.value = emptyList()
                                                    }
                                                ) {
                                                    Text("Remover")
                                                }
                                            }
                                        }



                                        SectionTitle("Sensibilidade dos Analógicos: %.2fx".format(controllerStickSensitivity.floatValue))

                                        Slider(

                                            value = controllerStickSensitivity.floatValue,

                                            onValueChange = { controllerStickSensitivity.floatValue = it },

                                            valueRange = 0.5f..2.0f,

                                            steps = 15,

                                            modifier = Modifier.fillMaxWidth()

                                        )



                                        SectionTitle("Escala dos Botões Touch: %.2fx".format(controllerScale.floatValue))

                                        Slider(

                                            value = controllerScale.floatValue,

                                            onValueChange = {

                                                controllerScale.floatValue = it

                                                val qsCs = QuickSettings(mainViewModel.activity); qsCs.controllerScale = it; qsCs.save()

                                            },

                                            valueRange = 0.5f..2.0f,

                                            steps = 15,

                                            modifier = Modifier.fillMaxWidth()

                                        )

                                    }



                                    SettingsCategory.Sistema -> {

                                        // NEOV2-AUDIO1: amplificacao do audio (na aba Sistema).
                                        SectionTitle("Volume do áudio: %d%%".format((audioBoost.floatValue * 100).toInt()))
                                        Slider(
                                            value = audioBoost.floatValue,
                                            onValueChange = {
                                                audioBoost.floatValue = it
                                                val qsAudio = QuickSettings(mainViewModel.activity)
                                                qsAudio.audioBoost = it
                                                qsAudio.save()
                                                try {
                                                    KenjinxNative.deviceSetAudioBoost(it)
                                                } catch (_: Throwable) {
                                                }
                                            },
                                            valueRange = 1.0f..2.0f,
                                            steps = 9,
                                            modifier = Modifier.fillMaxWidth()
                                        )


                                        SectionTitle("Idioma do Sistema")

                                        val langList = listOf<Pair<SystemLanguage, String>>(

                                            SystemLanguage.BrazilianPortuguese to "Português (Brasil)",

                                            SystemLanguage.AmericanEnglish to "English (US)",

                                            SystemLanguage.LatinAmericanSpanish to "Español (LatAm)",

                                            SystemLanguage.Portuguese to "Português (Portugal)",

                                            SystemLanguage.BritishEnglish to "English (UK)",

                                            SystemLanguage.Spanish to "Español (Espanha)",

                                            SystemLanguage.Japanese to "Japonês",

                                            SystemLanguage.French to "Français",

                                            SystemLanguage.German to "Deutsch",

                                            SystemLanguage.Italian to "Italiano",

                                            SystemLanguage.Russian to "Russo",

                                            SystemLanguage.SimplifiedChinese to "Chinês (Simplificado)"

                                        )

                                        CompactSelect(

                                            selectedLabel = langList.first { it.first == systemLanguage.value }.second,

                                            options = langList,

                                            onSelected = { systemLanguage.value = it }

                                        )



                                        SectionTitle("Região do Console")

                                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {

                                            listOf<Pair<RegionCode, String>>(

                                                RegionCode.USA to "EUA",

                                                RegionCode.Europe to "Europa",

                                                RegionCode.Japan to "Japão",

                                                RegionCode.Australia to "Austrália"

                                            ).forEach { (reg, label) ->

                                                ChoiceCard(

                                                    label = label,

                                                    selected = regionCode.value == reg,

                                                    modifier = Modifier.weight(1f)

                                                ) { regionCode.value = reg }

                                            }

                                        }



                                        SectionTitle("Instalação de Chaves e Firmware")

                                        ActionButtonRow(

                                            title = "Instalar Chaves (prod.keys)",

                                            description = "Chaves decodificadoras necessárias para carregar os jogos",

                                            buttonText = "Instalar chaves"

                                        ) { showKeyDialog.value = true }



                                        ActionButtonRow(

                                            title = "Instalar Firmware",

                                            description = "Versão atual: ${firmwareVersion.value.ifBlank { "Nenhum instalado" }}",

                                            buttonText = "Instalar firmware"

                                        ) { showFirwmareDialog.value = true }

                                    }



                                    SettingsCategory.Jogos -> {
                                        SectionTitle("Conversão de arquivos")
                                        ActionButtonRow(
                                            title = "Descompactar NSZ ou NCZ",
                                            description = "Gerar NSP ou NCA e excluir o original após verificação",
                                            buttonText = "Descompactar"
                                        ) { showDecompressionDialog.value = true }

                                        SectionTitle("Diretórios de Jogos")

                                        ActionButtonRow(

                                            title = "Adicionar Pasta de Jogos",

                                            description = "Selecione o diretório onde suas ROMs estão armazenadas",

                                            buttonText = "Adicionar pasta"

                                        ) { settingsViewModel.openGameFolder() }



                                        ActionButtonRow(

                                            title = "Pasta de Atualizações e DLCs",

                                            description = "Diretório de patches e expansões instaladas",

                                            buttonText = "Selecionar pasta"

                                        ) { settingsViewModel.openUpdatesFolder() }



                                        SectionTitle("Gerenciamento de Dados")

                                        ActionButtonRow(

                                            title = "Abrir Pasta de Dados",

                                            description = "Acessar arquivos internos do emulador no gerenciador de arquivos",

                                            buttonText = "Abrir"

                                        ) {

                                            val intent = Intent(Intent.ACTION_VIEW).apply {

                                                setDataAndType(

                                                    DocumentsContract.buildRootUri(

                                                        "${mainViewModel.activity.packageName}.providers",

                                                        "root"

                                                    ),

                                                    "vnd.android.document/root"

                                                )

                                                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)

                                            }

                                            try {

                                                mainViewModel.activity.startActivity(intent)

                                            } catch (_: ActivityNotFoundException) {}

                                        }



                                        ActionButtonRow(

                                            title = "Importar Dados (ZIP)",

                                            description = "Restaurar saves, configurações e firmware de um backup",

                                            buttonText = "Importar"

                                        ) { showDataImportDialog.value = true }



                                        ActionButtonRow(

                                            title = "Redefinir Dados da Aplicação",

                                            description = "Limpar todos os dados e restaurar o emulador para os padrões de fábrica",

                                            buttonText = "Redefinir"

                                        ) { showDataResetDialog.value = true }

                                    }



                                    SettingsCategory.Avancado -> {

                                        HighContrastToggle(

                                            title = "Integridade do sistema de arquivos",

                                            description = "Valida a integridade dos arquivos de conteúdo durante o carregamento",

                                            checked = enableFsIntegrityChecks.value,

                                            onCheckedChange = { enableFsIntegrityChecks.value = it }

                                        )



                                        HighContrastToggle(

                                            title = "Ignorar Serviços Ausentes",

                                            description = "Permite continuar quando um jogo solicita serviços ainda não implementados; pode causar erros",

                                            checked = ignoreMissingServices.value,

                                            onCheckedChange = { ignoreMissingServices.value = it }

                                        )



                                        SectionTitle("Registros de Log e Diagnóstico")

                                        ActionButtonRow(

                                            title = "Compartilhar logs",

                                            description = "Compacta os registros disponíveis e abre o menu de compartilhamento do Android",

                                            buttonText = "Compartilhar"

                                        ) { mainViewModel.logging.requestExport() }



                                        HighContrastToggle(

                                            title = "Logs de Stub",

                                            description = "Registra chamadas a funções não implementadas",

                                            checked = enableStubLogs.value,

                                            onCheckedChange = { enableStubLogs.value = it }

                                        )

                                        HighContrastToggle(

                                            title = "Logs de Informações (Info)",

                                            description = "Registros gerais do processo de emulação",

                                            checked = enableInfoLogs.value,

                                            onCheckedChange = { enableInfoLogs.value = it }

                                        )

                                        HighContrastToggle(

                                            title = "Logs de Avisos (Warning)",

                                            description = "Alertas de operações inesperadas",

                                            checked = enableWarningLogs.value,

                                            onCheckedChange = { enableWarningLogs.value = it }

                                        )

                                        HighContrastToggle(

                                            title = "Logs de Erros (Error)",

                                            description = "Falhas críticas durante a execução",

                                            checked = enableErrorLogs.value,

                                            onCheckedChange = { enableErrorLogs.value = it }

                                        )

                                        HighContrastToggle(

                                            title = "Logs de Trace",

                                            description = "Registra cada chamada de sistema e de serviço. Muito pesado: use só para diagnóstico",

                                            checked = enableTraceLogs.value,

                                            onCheckedChange = { enableTraceLogs.value = it }

                                        )

                                        HighContrastToggle(

                                            title = "Logs de Gráficos (Graphics)",

                                            description = "Depuração avançada do pipeline Vulkan",

                                            checked = enableGraphicsLogs.value,

                                            onCheckedChange = { enableGraphicsLogs.value = it }

                                        )

                                        HighContrastToggle(
                                            title = "Logs do jogo (Guest)",
                                            description = "Mensagens que o próprio jogo escreve no log do sistema. Útil para descobrir por que um jogo falha",
                                            checked = enableGuestLogs.value,
                                            onCheckedChange = { enableGuestLogs.value = it }
                                        )

                                        HighContrastToggle(
                                            title = "Logs de Debug",
                                            description = "Detalhes internos do emulador. Pesado: use só para diagnóstico",
                                            checked = enableDebugLogs.value,
                                            onCheckedChange = { enableDebugLogs.value = it }
                                        )

                                    }



                                    SettingsCategory.Sobre -> {

                                        Surface(

                                            shape = RoundedCornerShape(14.dp),

                                            color = MaterialTheme.colorScheme.surface,

                                            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline),

                                            modifier = Modifier.fillMaxWidth()

                                        ) {

                                            Column(

                                                modifier = Modifier.padding(20.dp),

                                                verticalArrangement = Arrangement.spacedBy(12.dp)

                                            ) {

                                                Image(

                                                    painter = painterResource(

                                                        if (org.kenjinx.android.ui.theme.NeoThemeState.isDark) {

                                                            R.drawable.ryujinx_neo_logo_light

                                                        } else {

                                                            R.drawable.ryujinx_neo_logo

                                                        }

                                                    ),

                                                    contentDescription = "Ryujinx Neo",

                                                    contentScale = ContentScale.Fit,

                                                    modifier = Modifier.height(44.dp)

                                                )

                                                Text(

                                                    "Ryujinx Neo - Emulador de Nintendo Switch",

                                                    fontWeight = FontWeight.Bold,

                                                    fontSize = 16.sp,

                                                    color = MaterialTheme.colorScheme.onSurface

                                                )

                                                Text(

                                                    "Branch: neo-v2-nce2\nMotor NCE Armv8.2-A nativo com proteção de loop de shader e memória Slack.",

                                                    fontSize = 13.sp,

                                                    color = MaterialTheme.colorScheme.onSurfaceVariant

                                                )

                                                Text(

                                                    "Interface reformulada com caixinhas modulares de alto contraste e mockups de cartucho.",

                                                    fontSize = 12.sp,

                                                    color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.72f)

                                                )

                                            }

                                        }

                                    }

                                    }

                                    }

                                }

                            }

                        }

                    }

                }

            }



                            // Dialogs

                SimpleAlertDialog.Custom(

                    showDialog = isDriverSelectorOpen,

                    onDismissRequest = { isDriverSelectorOpen.value = false },

                    properties = DialogProperties(usePlatformDefaultWidth = false),

                    widthFraction = 0.82f

                ) {

                    VulkanDriverViews.Main(settingsViewModel.activity, isDriverSelectorOpen)

                }



                SimpleAlertDialog.Custom(

                        showDialog = showKeyDialog,

                        onDismissRequest = {

                            if (keyInstallState.value != KeyInstallState.Install) {

                                showKeyDialog.value = false

                                settingsViewModel.clearKeySelection(keyInstallState)

                                keyInstallState.value = KeyInstallState.File

                            }

                        }

                    ) {

                        Column(

                            modifier = Modifier

                                .padding(16.dp)

                                .fillMaxWidth(),

                            verticalArrangement = Arrangement.SpaceBetween,

                            horizontalAlignment = Alignment.CenterHorizontally

                        ) {

                            SettingsDialogHeader(

                                icon = Icons.Default.VpnKey,

                                title = "Instalação de Chaves",

                                subtitle = "Sistema • Arquivos prod.keys e title.keys",

                                onClose = {

                                    if (keyInstallState.value != KeyInstallState.Install) {

                                        showKeyDialog.value = false

                                        settingsViewModel.clearKeySelection(keyInstallState)

                                        keyInstallState.value = KeyInstallState.File

                                    }

                                }

                            )



                            when (keyInstallState.value) {

                                KeyInstallState.File -> {

                                    Text(

                                        text = "Selecione o arquivo de chaves que deseja instalar.",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    Row(

                                        horizontalArrangement = Arrangement.SpaceEvenly,

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    ) {

                                        Button(

                                            onClick = {

                                                settingsViewModel.selectKey(

                                                    keyInstallState

                                                )

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Selecionar arquivo")

                                        }

                                        Button(

                                            onClick = {

                                                showKeyDialog.value = false

                                                settingsViewModel.clearKeySelection(keyInstallState)

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Cancelar")

                                        }

                                    }

                                }

                                KeyInstallState.Query -> {

                                    Text(

                                        text = "O arquivo de chaves será instalado. Deseja continuar?",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    Row(

                                        horizontalArrangement = Arrangement.SpaceEvenly,

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    ) {

                                        Button(

                                            onClick = {

                                                settingsViewModel.installKey(

                                                    keyInstallState

                                                )



                                                if (keyInstallState.value == KeyInstallState.File) {

                                                    showKeyDialog.value = false

                                                    settingsViewModel.clearKeySelection(keyInstallState)

                                                }

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Sim")

                                        }

                                        Button(

                                            onClick = {

                                                showKeyDialog.value = false

                                                settingsViewModel.clearKeySelection(keyInstallState)

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Não")

                                        }

                                    }

                                }

                                KeyInstallState.Install -> {

                                    Text(

                                        text = "Instalando arquivo de chaves...",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    LinearProgressIndicator(

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    )

                                }

                                KeyInstallState.Done -> {

                                    Text(

                                        text = "Arquivo de chaves instalado.",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    Row(

                                        horizontalArrangement = Arrangement.SpaceEvenly,

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    ) {

                                        Button(

                                            onClick = {

                                                showKeyDialog.value = false

                                                settingsViewModel.clearKeySelection(keyInstallState)

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Fechar")

                                        }

                                    }

                                }

                                KeyInstallState.Cancelled -> {

                                    val file = settingsViewModel.selectedKeyFile

                                    if (file != null) {

                                        if (file.extension == "key") {

                                            Text(

                                                text = "Ocorreu um erro desconhecido. Consulte os logs.",

                                                modifier = Modifier

                                                    .fillMaxWidth()

                                                    .padding(start = 8.dp, bottom = 8.dp),

                                                textAlign = TextAlign.Start

                                            )

                                        } else {

                                            Text(

                                                text = "Este tipo de arquivo não é compatível.",

                                                modifier = Modifier

                                                    .fillMaxWidth()

                                                    .padding(start = 8.dp, bottom = 8.dp),

                                                textAlign = TextAlign.Start

                                            )

                                        }

                                    } else {

                                        Text(

                                            text = "Este tipo de arquivo não é compatível.",

                                            modifier = Modifier

                                                .fillMaxWidth()

                                                .padding(start = 8.dp, bottom = 8.dp),

                                            textAlign = TextAlign.Start

                                        )

                                    }



                                    Row(

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp),

                                        horizontalArrangement = Arrangement.Center

                                    ) {

                                        Button(

                                            onClick = {

                                                showKeyDialog.value = false

                                                settingsViewModel.clearKeySelection(keyInstallState)

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Fechar")

                                        }

                                    }

                                }

                            }

                        }

                    }



                SimpleAlertDialog.Custom(

                        showDialog = showFirwmareDialog,

                        onDismissRequest = {

                            if (firmwareInstallState.value != FirmwareInstallState.Install) {

                                showFirwmareDialog.value = false

                                settingsViewModel.clearFirmwareSelection(firmwareInstallState)

                                firmwareInstallState.value = FirmwareInstallState.File

                            }

                        }

                    ) {

                        Column(

                            modifier = Modifier

                                .padding(16.dp)

                                .fillMaxWidth(),

                            verticalArrangement = Arrangement.SpaceBetween,

                            horizontalAlignment = Alignment.CenterHorizontally

                        ) {

                            SettingsDialogHeader(

                                icon = Icons.Default.SystemUpdate,

                                title = "Instalação de Firmware",

                                subtitle = "Sistema • Pacote de firmware oficial (.zip ou .xci)",

                                onClose = {

                                    if (firmwareInstallState.value != FirmwareInstallState.Install) {

                                        showFirwmareDialog.value = false

                                        settingsViewModel.clearFirmwareSelection(firmwareInstallState)

                                        firmwareInstallState.value = FirmwareInstallState.File

                                    }

                                }

                            )



                            when (firmwareInstallState.value) {

                                FirmwareInstallState.File -> {

                                    Text(

                                        text = "Selecione um arquivo ZIP ou XCI que contenha o firmware.",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    Row(

                                        horizontalArrangement = Arrangement.SpaceEvenly,

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    ) {

                                        Button(

                                            onClick = {

                                                settingsViewModel.selectFirmware(firmwareInstallState)

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Selecionar arquivo")

                                        }

                                        Button(

                                            onClick = {

                                                showFirwmareDialog.value = false

                                                settingsViewModel.clearFirmwareSelection(firmwareInstallState)

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Cancelar")

                                        }

                                    }

                                }

                                FirmwareInstallState.Query -> {

                                    Text(

                                        text = "O firmware ${settingsViewModel.selectedFirmwareVersion} será instalado. Deseja continuar?",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    Row(

                                        horizontalArrangement = Arrangement.SpaceEvenly,

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    ) {

                                        Button(

                                            onClick = {

                                                settingsViewModel.installFirmware(firmwareInstallState)



                                                if (firmwareInstallState.value == FirmwareInstallState.File) {

                                                    showFirwmareDialog.value = false

                                                    settingsViewModel.clearFirmwareSelection(firmwareInstallState)

                                                }

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Sim")

                                        }

                                        Button(

                                            onClick = {

                                                showFirwmareDialog.value = false

                                                settingsViewModel.clearFirmwareSelection(firmwareInstallState)

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Não")

                                        }

                                    }

                                }

                                FirmwareInstallState.Verifying -> {

                                    Text(

                                        text = "Verificando o arquivo selecionado...",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    LinearProgressIndicator(

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    )

                                }

                                FirmwareInstallState.Install -> {

                                    Text(

                                        text = "Instalando o firmware ${settingsViewModel.selectedFirmwareVersion}...",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    LinearProgressIndicator(

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    )

                                }

                                FirmwareInstallState.Done -> {

                                    Text(

                                        text = "Firmware ${settingsViewModel.selectedFirmwareVersion}.",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    firmwareVersion.value = mainViewModel.firmwareVersion



                                    Row(

                                        horizontalArrangement = Arrangement.SpaceEvenly,

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    ) {

                                        Button(

                                            onClick = {

                                                showFirwmareDialog.value = false

                                                settingsViewModel.clearFirmwareSelection(firmwareInstallState)

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Fechar")

                                        }

                                    }

                                }

                                FirmwareInstallState.Cancelled -> {

                                    val file = settingsViewModel.selectedFirmwareFile

                                    if (file != null) {

                                        if (file.extension == "xci" || file.extension == "zip") {

                                            if (settingsViewModel.selectedFirmwareVersion.isEmpty()) {

                                                Text(

                                                    text = "Não foi possível identificar a versão no arquivo selecionado.",

                                                    modifier = Modifier

                                                        .fillMaxWidth()

                                                        .padding(start = 8.dp, bottom = 8.dp),

                                                    textAlign = TextAlign.Start

                                                )

                                            } else {

                                                Text(

                                                    text = "Ocorreu um erro desconhecido. Consulte os logs.",

                                                    modifier = Modifier

                                                        .fillMaxWidth()

                                                        .padding(start = 8.dp, bottom = 8.dp),

                                                    textAlign = TextAlign.Start

                                                )

                                            }

                                        } else {

                                            Text(

                                                text = "Este tipo de arquivo não é compatível.",

                                                modifier = Modifier

                                                    .fillMaxWidth()

                                                    .padding(start = 8.dp, bottom = 8.dp),

                                                textAlign = TextAlign.Start

                                            )

                                        }

                                    } else {

                                        Text(

                                            text = "Este tipo de arquivo não é compatível.",

                                            modifier = Modifier

                                                .fillMaxWidth()

                                                .padding(start = 8.dp, bottom = 8.dp),

                                            textAlign = TextAlign.Start

                                        )

                                    }



                                    Row(

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp),

                                        horizontalArrangement = Arrangement.Center

                                    ) {

                                        Button(

                                            onClick = {

                                                showFirwmareDialog.value = false

                                                settingsViewModel.clearFirmwareSelection(firmwareInstallState)

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Fechar")

                                        }

                                    }

                                }

                            }

                        }

                    }



                SimpleAlertDialog.Custom(

                        showDialog = showDataResetDialog,

                        onDismissRequest = {

                            if (dataResetState.value != DataResetState.Reset) {

                                showDataResetDialog.value = false

                                dataResetState.value = DataResetState.Query

                            }

                        }

                    ) {

                        Column(

                            modifier = Modifier

                                .padding(16.dp)

                                .fillMaxWidth(),

                            verticalArrangement = Arrangement.SpaceBetween,

                            horizontalAlignment = Alignment.CenterHorizontally

                        ) {

                            SettingsDialogHeader(

                                icon = Icons.Default.DeleteSweep,

                                title = "Redefinir Dados do Aplicativo",

                                subtitle = "Aviso • Redefinição para padrões de fábrica",

                                isError = true,

                                onClose = {

                                    if (dataResetState.value != DataResetState.Reset) {

                                        showDataResetDialog.value = false

                                        dataResetState.value = DataResetState.Query

                                    }

                                }

                            )



                            when (dataResetState.value) {

                                DataResetState.Query -> {

                                    Text(

                                        text = "As pastas BIS, jogos, perfis e sistema serão apagadas e recriadas. Deseja continuar?",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    Row(

                                        horizontalArrangement = Arrangement.spacedBy(10.dp),

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp),

                                        verticalAlignment = Alignment.CenterVertically

                                    ) {

                                        OutlinedButton(

                                            onClick = {

                                                showDataResetDialog.value = false

                                            },

                                            modifier = Modifier.weight(1f)

                                        ) {

                                            Icon(Icons.Default.Close, contentDescription = null, modifier = Modifier.size(18.dp))

                                            Spacer(Modifier.width(6.dp))

                                            Text("Cancelar")

                                        }

                                        Button(

                                            onClick = {

                                                thread {

                                                    settingsViewModel.resetAppData(dataResetState)

                                                }

                                            },

                                            colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),

                                            modifier = Modifier.weight(1f)

                                        ) {

                                            Icon(Icons.Default.Delete, contentDescription = null, modifier = Modifier.size(18.dp))

                                            Spacer(Modifier.width(6.dp))

                                            Text("Redefinir")

                                        }

                                    }

                                }

                                DataResetState.Reset -> {

                                    Text(

                                        text = "Redefinindo os dados do aplicativo...",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    LinearProgressIndicator(

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    )

                                }

                                DataResetState.Done -> {

                                    Text(

                                        text = "Os dados foram redefinidos com sucesso.",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )



                                    Row(

                                        horizontalArrangement = Arrangement.SpaceEvenly,

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    ) {

                                        Button(

                                            onClick = {

                                                showDataResetDialog.value = false

                                                firmwareVersion.value = mainViewModel.firmwareVersion

                                                dataResetState.value = DataResetState.Query

                                                mainViewModel.userViewModel.refreshUsers()

                                                mainViewModel.homeViewModel.requestReload()

                                                mainViewModel.activity.shutdownAndRestart()

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Fechar")

                                        }

                                    }

                                }

                            }

                        }

                    }



                SimpleAlertDialog.Custom(

                        showDialog = showDataImportDialog,

                        onDismissRequest = {

                            if (dataImportState.value != DataImportState.Import) {

                                showDataImportDialog.value = false

                                dataFile.value = null

                                dataImportState.value = DataImportState.File

                            }

                        }

                    ) {

                        Column(

                            modifier = Modifier

                                .padding(16.dp)

                                .fillMaxWidth(),

                            verticalArrangement = Arrangement.SpaceBetween,

                            horizontalAlignment = Alignment.CenterHorizontally

                        ) {

                            SettingsDialogHeader(

                                icon = Icons.Default.CloudDownload,

                                title = "Importar Dados do Aplicativo",

                                subtitle = "Backup • Restauração de dados (.zip)",

                                onClose = {

                                    if (dataImportState.value != DataImportState.Import) {

                                        showDataImportDialog.value = false

                                        dataFile.value = null

                                        dataImportState.value = DataImportState.File

                                    }

                                }

                            )



                            when (dataImportState.value) {

                                DataImportState.File -> {

                                    Text(

                                        text = "Selecione um arquivo ZIP com as pastas BIS, jogos, perfis e sistema de outra instalação Android.",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    Row(

                                        horizontalArrangement = Arrangement.SpaceEvenly,

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    ) {

                                        Button(

                                            onClick = {

                                                val storage = MainActivity.StorageHelper

                                                storage?.apply {

                                                    val callBack = this.onFileSelected

                                                    onFileSelected = { requestCode, files ->

                                                        run {

                                                            onFileSelected = callBack

                                                            if (requestCode == IMPORT_CODE) {

                                                                val file = files.firstOrNull()

                                                                file?.apply {

                                                                    if (this.extension == "zip") {

                                                                        dataFile.value = this

                                                                        dataImportState.value = DataImportState.Query

                                                                    }

                                                                }

                                                            }

                                                        }

                                                    }

                                                    openFilePicker(

                                                        IMPORT_CODE,

                                                        filterMimeTypes = arrayOf("application/zip")

                                                    )

                                                }

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Selecionar arquivo")

                                        }

                                        Button(

                                            onClick = {

                                                showDataImportDialog.value = false

                                                dataFile.value = null

                                                dataImportState.value = DataImportState.File

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Cancelar")

                                        }

                                    }

                                }

                                DataImportState.Query -> {

                                    Text(

                                        text = "Os dados atuais serão apagados e substituídos. Deseja continuar?",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    Row(

                                        horizontalArrangement = Arrangement.spacedBy(10.dp),

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp),

                                        verticalAlignment = Alignment.CenterVertically

                                    ) {

                                        OutlinedButton(

                                            onClick = {

                                                showDataImportDialog.value = false

                                                dataFile.value = null

                                            },

                                            modifier = Modifier.weight(1f)

                                        ) {

                                            Icon(Icons.Default.Close, contentDescription = null, modifier = Modifier.size(18.dp))

                                            Spacer(Modifier.width(6.dp))

                                            Text("Cancelar")

                                        }

                                        Button(

                                            onClick = {

                                                val file = dataFile.value

                                                dataFile.value = null

                                                file?.apply {

                                                    thread {

                                                        settingsViewModel.importAppData(this, dataImportState)

                                                    }

                                                }

                                            },

                                            colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error),

                                            modifier = Modifier.weight(1f)

                                        ) {

                                            Icon(Icons.Default.DeleteSweep, contentDescription = null, modifier = Modifier.size(18.dp))

                                            Spacer(Modifier.width(6.dp))

                                            Text("Substituir")

                                        }

                                    }

                                }

                                DataImportState.Import -> {

                                    Text(

                                        text = "Importando dados do aplicativo...",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )

                                    LinearProgressIndicator(

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    )

                                }



                                DataImportState.Done -> {

                                    Text(

                                        text = "Os dados foram importados com sucesso.",

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(start = 8.dp, bottom = 8.dp),

                                        textAlign = TextAlign.Start

                                    )



                                    Row(

                                        horizontalArrangement = Arrangement.SpaceEvenly,

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(top = 16.dp)

                                    ) {

                                        Button(

                                            onClick = {

                                                showDataImportDialog.value = false

                                                dataFile.value = null

                                                firmwareVersion.value = mainViewModel.firmwareVersion

                                                dataImportState.value = DataImportState.File

                                                mainViewModel.userViewModel.refreshUsers()

                                                mainViewModel.homeViewModel.requestReload()

                                                mainViewModel.activity.shutdownAndRestart()

                                            },

                                            modifier = Modifier

                                                .weight(1f)

                                                .padding(horizontal = 8.dp)

                                        ) {

                                            Text(text = "Fechar")

                                        }

                                    }

                                }

                            }

                        }

                    }

            }



        @Composable

        private fun <T> CompactSelect(

            selectedLabel: String,

            options: List<Pair<T, String>>,

            onSelected: (T) -> Unit

        ) {

            var expanded by remember { mutableStateOf(false) }

            Box(modifier = Modifier.widthIn(max = 360.dp).fillMaxWidth()) { // NEOV2-UI1: cabe no celular em pe

                Surface(

                    modifier = Modifier

                        .fillMaxWidth()

                        .clip(RoundedCornerShape(14.dp))

                        .clickable { expanded = true },

                    shape = RoundedCornerShape(14.dp),

                    color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f),

                    border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f))

                ) {

                    Row(

                        modifier = Modifier.padding(horizontal = 16.dp, vertical = 13.dp),

                        verticalAlignment = Alignment.CenterVertically

                    ) {

                        Text(

                            selectedLabel,

                            modifier = Modifier.weight(1f),

                            color = MaterialTheme.colorScheme.onSurface,

                            fontWeight = FontWeight.Medium

                        )

                        Icon(Icons.Default.ArrowDropDown, contentDescription = "Abrir opções", tint = MaterialTheme.colorScheme.onSurfaceVariant)

                    }

                }

                DropdownMenu(

                    expanded = expanded,

                    onDismissRequest = { expanded = false },

                    containerColor = MaterialTheme.colorScheme.surface

                ) {

                    options.forEach { (value, label) ->

                        DropdownMenuItem(

                            text = { Text(label, color = MaterialTheme.colorScheme.onSurface) },

                            onClick = {

                                onSelected(value)

                                expanded = false

                            }

                        )

                    }

                }

            }

        }



        @Composable

        private fun SettingsDialogHeader(

            icon: ImageVector,

            title: String,

            subtitle: String,

            isError: Boolean = false,

            onClose: () -> Unit

        ) {

            Row(

                modifier = Modifier

                    .fillMaxWidth()

                    .padding(bottom = 14.dp),

                verticalAlignment = Alignment.CenterVertically

            ) {

                Surface(

                    modifier = Modifier.size(54.dp),

                    shape = RoundedCornerShape(12.dp),

                    color = if (isError) MaterialTheme.colorScheme.errorContainer else MaterialTheme.colorScheme.primaryContainer

                ) {

                    Box(contentAlignment = Alignment.Center) {

                        Icon(

                            imageVector = icon,

                            contentDescription = null,

                            tint = if (isError) MaterialTheme.colorScheme.onErrorContainer else MaterialTheme.colorScheme.onPrimaryContainer,

                            modifier = Modifier.size(28.dp)

                        )

                    }

                }

                Spacer(Modifier.width(14.dp))

                Column(modifier = Modifier.weight(1f)) {

                    Text(

                        text = title,

                        style = MaterialTheme.typography.titleMedium,

                        fontWeight = FontWeight.Bold,

                        maxLines = 1,

                        overflow = TextOverflow.Ellipsis

                    )

                    Text(

                        text = subtitle,

                        style = MaterialTheme.typography.bodySmall,

                        color = MaterialTheme.colorScheme.onSurfaceVariant,

                        maxLines = 1,

                        overflow = TextOverflow.Ellipsis

                    )

                }

                IconButton(onClick = onClose) {

                    Icon(Icons.Default.Close, contentDescription = "Fechar")

                }

            }

        }



        @Composable

        private fun CategoryNavCard(

            category: SettingsCategory,

            onClick: () -> Unit

        ) {

            Surface(

                shape = RoundedCornerShape(16.dp),

                color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f),

                border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),

                modifier = Modifier

                    .fillMaxWidth()

                    .height(92.dp)

                    .clip(RoundedCornerShape(16.dp))

                    .clickable(onClick = onClick)

            ) {

                Row(

                    modifier = Modifier

                        .fillMaxSize()

                        .padding(horizontal = 16.dp, vertical = 12.dp),

                    verticalAlignment = Alignment.CenterVertically,

                    horizontalArrangement = Arrangement.SpaceBetween

                ) {

                    Row(

                        verticalAlignment = Alignment.CenterVertically,

                        horizontalArrangement = Arrangement.spacedBy(14.dp),

                        modifier = Modifier.weight(1f)

                    ) {

                        Surface(

                            shape = RoundedCornerShape(12.dp),

                            color = MaterialTheme.colorScheme.primaryContainer,

                            modifier = Modifier.size(54.dp)

                        ) {

                            Box(contentAlignment = Alignment.Center) {

                                if (category.iconRes != null) {

                                    Image(

                                        painter = painterResource(category.iconRes),

                                        contentDescription = null,

                                        modifier = Modifier.size(28.dp)

                                    )

                                } else {

                                    Icon(

                                        imageVector = category.icon,

                                        contentDescription = null,

                                        tint = MaterialTheme.colorScheme.onPrimaryContainer,

                                        modifier = Modifier.size(28.dp)

                                    )

                                }

                            }

                        }

                        Column(modifier = Modifier.weight(1f)) {

                            Text(

                                text = category.title,

                                fontWeight = FontWeight.Bold,

                                fontSize = 16.sp,

                                color = MaterialTheme.colorScheme.onSurface

                            )

                            Text(

                                text = category.description,

                                fontSize = 12.sp,

                                color = MaterialTheme.colorScheme.onSurfaceVariant,

                                maxLines = 2,

                                overflow = TextOverflow.Ellipsis

                            )

                        }

                    }

                    Icon(

                        imageVector = Icons.AutoMirrored.Filled.ArrowForward,

                        contentDescription = "Abrir",

                        tint = MaterialTheme.colorScheme.onSurfaceVariant,

                        modifier = Modifier.size(20.dp)

                    )

                }

            }

        }



        @Composable

        private fun SectionTitle(title: String) {

            Text(

                text = title,

                fontWeight = FontWeight.Bold,

                fontSize = 14.sp,

                color = MaterialTheme.colorScheme.onSurface,

                modifier = Modifier.padding(top = 8.dp, bottom = 2.dp)

            )

        }



        @Composable

        private fun HighContrastToggle(

            title: String,

            description: String,

            checked: Boolean,

            onCheckedChange: (Boolean) -> Unit

        ) {

            Surface(

                shape = RoundedCornerShape(14.dp),

                color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f),

                border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),

                modifier = Modifier

                    .fillMaxWidth()

                    .clip(RoundedCornerShape(14.dp))

                    .clickable { onCheckedChange(!checked) }

            ) {

                Row(

                    modifier = Modifier.padding(horizontal = 14.dp, vertical = 12.dp),

                    verticalAlignment = Alignment.CenterVertically,

                    horizontalArrangement = Arrangement.SpaceBetween

                ) {

                    Column(modifier = Modifier.weight(1f).padding(end = 12.dp)) {

                        Text(

                            text = title,

                            fontWeight = FontWeight.SemiBold,

                            fontSize = 14.sp,

                            color = MaterialTheme.colorScheme.onSurface

                        )

                        Text(

                            text = description,

                            fontSize = 12.sp,

                            color = MaterialTheme.colorScheme.onSurfaceVariant

                        )

                    }

                    Switch(

                        checked = checked,

                        onCheckedChange = null,

                        colors = SwitchDefaults.colors(

                            checkedThumbColor = MaterialTheme.colorScheme.onPrimary,

                            checkedTrackColor = MaterialTheme.colorScheme.primary,

                            uncheckedThumbColor = MaterialTheme.colorScheme.onSurfaceVariant,

                            uncheckedTrackColor = MaterialTheme.colorScheme.surfaceVariant

                        )

                    )

                }

            }

        }



        @Composable

        private fun ActionButtonRow(

            title: String,

            description: String,

            buttonText: String,

            onClick: () -> Unit

        ) {

            Surface(

                shape = RoundedCornerShape(14.dp),

                color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f),

                border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),

                modifier = Modifier.fillMaxWidth()

            ) {

                Row(

                    modifier = Modifier.padding(horizontal = 14.dp, vertical = 12.dp),

                    verticalAlignment = Alignment.CenterVertically,

                    horizontalArrangement = Arrangement.SpaceBetween

                ) {

                    Column(modifier = Modifier.weight(1f).padding(end = 12.dp)) {

                        Text(

                            text = title,

                            fontWeight = FontWeight.SemiBold,

                            fontSize = 14.sp,

                            color = MaterialTheme.colorScheme.onSurface

                        )

                        Text(

                            text = description,

                            fontSize = 12.sp,

                            color = MaterialTheme.colorScheme.onSurfaceVariant

                        )

                    }

                    Button(

                        onClick = onClick,

                        colors = ButtonDefaults.buttonColors(

                            containerColor = MaterialTheme.colorScheme.primary,

                            contentColor = MaterialTheme.colorScheme.onPrimary

                        ),

                        shape = RoundedCornerShape(8.dp)

                    ) {

                        Text(buttonText, fontWeight = FontWeight.Bold)

                    }

                }

            }

        }

    }

}

