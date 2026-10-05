package org.kenjinx.android.views

import androidx.compose.foundation.layout.PaddingValues

import androidx.compose.foundation.text.BasicTextField

import androidx.compose.ui.text.TextStyle

import androidx.compose.ui.text.style.TextAlign

import androidx.compose.ui.unit.sp

import androidx.compose.foundation.shape.CircleShape

import androidx.compose.foundation.layout.BoxWithConstraints

import androidx.compose.foundation.layout.offset



import android.app.Activity

import android.content.Intent

import android.content.res.Resources

import android.graphics.Bitmap

import android.graphics.BitmapFactory

import android.net.Uri

import androidx.compose.animation.AnimatedVisibility

import androidx.compose.animation.Crossfade

import androidx.compose.animation.animateColorAsState

import androidx.compose.animation.core.animateFloatAsState

import androidx.compose.foundation.ExperimentalFoundationApi

import androidx.compose.foundation.BorderStroke

import androidx.compose.foundation.Image

import androidx.compose.foundation.background

import androidx.compose.foundation.basicMarquee

import androidx.compose.foundation.border

import androidx.compose.foundation.clickable

import androidx.compose.foundation.combinedClickable

import androidx.compose.foundation.interaction.MutableInteractionSource

import androidx.compose.foundation.interaction.collectIsPressedAsState

import androidx.compose.foundation.layout.Arrangement

import androidx.compose.foundation.layout.Box

import androidx.compose.foundation.layout.Column

import androidx.compose.foundation.layout.Row

import androidx.compose.foundation.layout.Spacer

import androidx.compose.foundation.layout.fillMaxSize

import androidx.compose.foundation.layout.fillMaxWidth

import androidx.compose.foundation.layout.height

import androidx.compose.foundation.layout.fillMaxHeight

import androidx.compose.foundation.layout.padding

import androidx.compose.foundation.layout.size

import androidx.compose.foundation.layout.width

import androidx.compose.foundation.layout.aspectRatio

import androidx.compose.foundation.lazy.LazyColumn

import androidx.compose.foundation.lazy.LazyRow

import androidx.compose.foundation.lazy.grid.GridCells

import androidx.compose.foundation.lazy.grid.LazyVerticalGrid

import androidx.compose.foundation.lazy.grid.items

import androidx.compose.foundation.lazy.items

import androidx.compose.foundation.shape.RoundedCornerShape

import androidx.compose.material.icons.Icons

import androidx.compose.material.icons.filled.Add

import androidx.compose.material.icons.filled.Menu

import androidx.compose.material.icons.filled.Person

import androidx.compose.material.icons.filled.Refresh

import androidx.compose.material.icons.filled.Search

import androidx.compose.material.icons.filled.Settings

import androidx.compose.material.icons.filled.ViewCarousel

import androidx.compose.material.icons.filled.ViewModule

import androidx.compose.material.icons.filled.Memory

import androidx.compose.material.icons.filled.Palette

import androidx.compose.material.icons.filled.SportsEsports

import androidx.compose.material.icons.filled.Folder

import androidx.compose.material.icons.filled.DarkMode

import androidx.compose.material.icons.filled.Devices

import androidx.compose.material.icons.filled.LightMode

import androidx.compose.material3.CircularProgressIndicator

import androidx.compose.material3.ExperimentalMaterial3Api

import androidx.compose.material3.FabPosition

import androidx.compose.material3.FloatingActionButton

import androidx.compose.material3.Icon

import androidx.compose.material3.IconButton

import androidx.compose.material3.MaterialTheme

import androidx.compose.material3.BasicAlertDialog

import androidx.compose.material3.OutlinedTextField

import androidx.compose.material3.OutlinedTextFieldDefaults

import androidx.compose.material3.Scaffold

import androidx.compose.material3.Surface

import androidx.compose.material3.Text

import androidx.compose.runtime.Composable

import androidx.compose.runtime.LaunchedEffect

import androidx.compose.runtime.MutableState

import androidx.compose.runtime.getValue

import androidx.compose.runtime.mutableStateOf

import androidx.compose.runtime.remember

import androidx.compose.runtime.setValue

import androidx.compose.ui.Alignment

import androidx.compose.ui.Modifier

import androidx.compose.ui.draw.clip

import androidx.compose.ui.draw.shadow

import androidx.compose.foundation.focusable

import androidx.compose.ui.focus.onFocusChanged

import androidx.compose.ui.geometry.Offset

import androidx.compose.ui.graphics.Color

import androidx.compose.ui.graphics.Brush

import androidx.compose.ui.graphics.graphicsLayer

import androidx.compose.ui.window.DialogProperties

import androidx.compose.ui.graphics.asImageBitmap

import androidx.compose.ui.input.nestedscroll.NestedScrollConnection

import androidx.compose.ui.input.nestedscroll.NestedScrollSource

import androidx.compose.ui.input.nestedscroll.nestedScroll

import androidx.compose.ui.layout.ContentScale

import androidx.compose.ui.platform.LocalContext

import androidx.compose.ui.res.painterResource

import androidx.compose.ui.text.style.TextOverflow

import androidx.compose.ui.tooling.preview.Preview

import androidx.compose.ui.unit.dp

import androidx.compose.animation.core.tween

import androidx.preference.PreferenceManager

import androidx.navigation.NavHostController

import com.anggrayudi.storage.extension.launchOnUiThread

import java.util.Base64

import java.util.Locale

import java.util.Date

import java.text.SimpleDateFormat

import kotlin.concurrent.thread

import kotlin.math.roundToInt

import kotlinx.coroutines.CoroutineScope

import kotlinx.coroutines.Dispatchers

import kotlinx.coroutines.delay

import kotlinx.coroutines.launch

import org.kenjinx.android.R

import org.kenjinx.android.ui.theme.NeoThemeState

import org.kenjinx.android.viewmodels.FileType

import org.kenjinx.android.viewmodels.GameModel

import org.kenjinx.android.viewmodels.HomeViewModel

import org.kenjinx.android.viewmodels.QuickSettings

import org.kenjinx.android.widgets.SimpleAlertDialog

import org.kenjinx.android.ShortcutUtils

import android.widget.Toast

import androidx.activity.compose.rememberLauncherForActivityResult

import androidx.activity.result.contract.ActivityResultContracts

import androidx.compose.material3.AlertDialog

import androidx.compose.material3.TextButton

import androidx.compose.material3.Button

import androidx.compose.material3.ButtonDefaults

import androidx.compose.material3.OutlinedButton

import androidx.compose.material.icons.filled.AddBox

import androidx.compose.material.icons.filled.Check

import androidx.compose.material.icons.filled.Close

import androidx.compose.material.icons.filled.Code

import androidx.compose.material.icons.filled.Contactless

import androidx.compose.material.icons.filled.Delete

import androidx.compose.material.icons.filled.Save

import androidx.compose.material3.LinearProgressIndicator

import androidx.compose.material3.Switch

import androidx.compose.ui.text.font.FontWeight

import androidx.compose.foundation.layout.heightIn

import androidx.compose.foundation.layout.widthIn

import androidx.compose.runtime.mutableFloatStateOf

import androidx.documentfile.provider.DocumentFile



// Cheats

import org.kenjinx.android.cheats.CheatPrefs

import org.kenjinx.android.cheats.CheatItem

import org.kenjinx.android.cheats.loadCheatsFromDisk

import org.kenjinx.android.cheats.applyCheatSelectionOnDisk

import org.kenjinx.android.cheats.importCheatTxt



// Mods

import org.kenjinx.android.cheats.listMods

import org.kenjinx.android.cheats.deleteMod

import org.kenjinx.android.cheats.importModsZip
import org.kenjinx.android.cheats.importModsFolder
import androidx.compose.material.icons.filled.FolderOpen



// Saves

import org.kenjinx.android.saves.*



class HomeViews {

    companion object {

        private const val HOME_LAYOUT_KEY = "neoHomeLayout"

        private enum class HomeLayout { Neo, Switch }

        const val ListImageSize = 150

        const val GridImageSize = 300



        // --- small version badge bottom left

        @Composable

        private fun VersionBadge(modifier: Modifier = Modifier) {

            Text(

                text = "v" + org.kenjinx.android.BuildConfig.VERSION_NAME,

                style = MaterialTheme.typography.labelSmall,

                color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.6f),

                modifier = modifier.padding(8.dp)

            )

        }

        // -- Helper for Shortcut Flow

        private fun resolveGameUri(gm: GameModel): Uri? = gm.file.uri



        private fun decodeGameIcon(gm: GameModel): Bitmap? {

            return try {

                val b64 = gm.icon ?: return null

                val bytes = Base64.getDecoder().decode(b64)

                BitmapFactory.decodeByteArray(bytes, 0, bytes.size)

            } catch (_: Throwable) {

                null

            }

        }

        @OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)

        @Composable

        fun Home(

            viewModel: HomeViewModel = HomeViewModel(),

            navController: NavHostController? = null,

            isPreview: Boolean = false

        ) {

            viewModel.ensureReloadIfNecessary()

            val showAppActions = remember { mutableStateOf(false) }

            val showLoading = remember { mutableStateOf(false) }

            val openTitleUpdateDialog = remember { mutableStateOf(false) }

            val canClose = remember { mutableStateOf(true) }

            val openDlcDialog = remember { mutableStateOf(false) }

            val openGameSettingsDialog = remember { mutableStateOf(false) }

            val showError = remember { mutableStateOf("") }

            val selectedModel = remember { mutableStateOf(viewModel.mainViewModel?.selected) }

            val query = remember { mutableStateOf("") }

            var refreshUser by remember { mutableStateOf(true) }

            var isFabVisible by remember { mutableStateOf(true) }

            val isNavigating = remember { mutableStateOf(false) }



            // Amiibo slot picker state

            val showAmiiboSlotDialog = remember { mutableStateOf(false) }

            val pendingSlot = remember { mutableStateOf(1) }



            // Cheats UI state

            val openCheatsDialog = remember { mutableStateOf(false) }

            val cheatsForSelected = remember { mutableStateOf(listOf<CheatItem>()) }

            val enabledCheatKeys = remember { mutableStateOf(mutableSetOf<String>()) }



            // Mods UI state

            val openModsDialog = remember { mutableStateOf(false) }

            val modsForSelected = remember { mutableStateOf(listOf<String>()) }

            val modsImportProgress = remember { mutableFloatStateOf(0f) }

            val modsImportBusy = remember { mutableStateOf(false) }

            val modsImportStatusText = remember { mutableStateOf("") }



            // Save Manager State

            val openSavesDialog = remember { mutableStateOf(false) }

            val saveImportBusy = remember { mutableStateOf(false) }

            val saveExportBusy = remember { mutableStateOf(false) }

            val saveImportProgress = remember { mutableFloatStateOf(0f) }

            val saveExportProgress = remember { mutableFloatStateOf(0f) }

            val saveImportStatus = remember { mutableStateOf("") }

            val saveExportStatus = remember { mutableStateOf("") }



            val activity = LocalContext.current as? Activity



            // Import: OpenDocument (ZIP)

            val importZipLauncher = rememberLauncherForActivityResult(

                contract = ActivityResultContracts.OpenDocument()

            ) { uri: Uri? ->

                val act = activity

                // Guard on selected game – optional

                val tIdNow = viewModel.mainViewModel?.selected?.titleId.orEmpty()

                if (uri != null && act != null && tIdNow.isNotEmpty()) {

                    saveImportBusy.value = true

                    saveImportProgress.floatValue = 0f

                    saveImportStatus.value = "Starting…"



                    thread {

                        val res = importSaveFromZip(act, uri) { prog ->

                            val frac = if (prog.total > 0) prog.bytes.toFloat() / prog.total else 0f

                            saveImportProgress.floatValue = frac.coerceIn(0f, 1f)

                            saveImportStatus.value = "Importing: ${prog.currentEntry}"

                        }

                        saveImportBusy.value = false

                        launchOnUiThread {

                            Toast.makeText(act, res.message, Toast.LENGTH_SHORT).show()

                        }

                    }

                }

            }



            // Export: CreateDocument (ZIP)

            val exportZipLauncher = rememberLauncherForActivityResult(

                contract = ActivityResultContracts.CreateDocument("application/zip")

            ) { uri: Uri? ->

                val act = activity

                val tIdNow = viewModel.mainViewModel?.selected?.titleId.orEmpty()

                if (uri != null && act != null && tIdNow.isNotEmpty()) {

                    saveExportBusy.value = true

                    saveExportProgress.floatValue = 0f

                    saveExportStatus.value = "Starting…"



                    thread {

                        val res = exportSaveToZip(act, tIdNow, uri) { prog ->

                            val frac = if (prog.total > 0) prog.bytes.toFloat() / prog.total else 0f

                            saveExportProgress.floatValue = frac.coerceIn(0f, 1f)

                            saveExportStatus.value = "Exporting: ${prog.currentPath}"

                        }

                        saveExportBusy.value = false

                        launchOnUiThread {

                            Toast.makeText(

                                act,

                                if (res.ok) "save exported" else (res.error ?: "export failed"),

                                Toast.LENGTH_SHORT

                            ).show()

                        }

                    }

                }

            }



            // Shortcut Dialog State

            val showShortcutDialog = remember { mutableStateOf(false) }

            val shortcutName = remember { mutableStateOf("") }



            val context = LocalContext.current

            val homePreferences = remember(context) { PreferenceManager.getDefaultSharedPreferences(context) }

            var homeLayout by remember {

                mutableStateOf(

                    if (homePreferences.getString(HOME_LAYOUT_KEY, HomeLayout.Neo.name) == HomeLayout.Switch.name) {

                        HomeLayout.Switch

                    } else {

                        HomeLayout.Neo

                    }

                )

            }



            // Amiibo Launcher (OpenDocument)

            val pickAmiiboLauncher = rememberLauncherForActivityResult(

                contract = ActivityResultContracts.OpenDocument()

            ) { uri: Uri? ->

                if (uri != null && activity != null) {

                    try {

                        activity.contentResolver.takePersistableUriPermission(

                            uri,

                            Intent.FLAG_GRANT_READ_URI_PERMISSION

                        )

                    } catch (_: Exception) {}

                    val name = DocumentFile.fromSingleUri(activity, uri)?.name ?: "amiibo.bin"

                    val qs = QuickSettings(activity)

                    when (pendingSlot.value) {

                        1 -> { qs.amiibo1Uri = uri.toString(); qs.amiibo1Name = name }

                        2 -> { qs.amiibo2Uri = uri.toString(); qs.amiibo2Name = name }

                        3 -> { qs.amiibo3Uri = uri.toString(); qs.amiibo3Name = name }

                        4 -> { qs.amiibo4Uri = uri.toString(); qs.amiibo4Name = name }

                        5 -> { qs.amiibo5Uri = uri.toString(); qs.amiibo5Name = name }

                    }

                    qs.save()

                    Toast.makeText(activity, "Amiibo saved to slot ${pendingSlot.value}", Toast.LENGTH_SHORT).show()

                }

            }



            // Cheats Import (.txt)

            val importCheatLauncher = rememberLauncherForActivityResult(

                contract = ActivityResultContracts.OpenDocument()

            ) { uri: Uri? ->

                val gm = viewModel.mainViewModel?.selected

                val act = viewModel.activity

                val titleId = gm?.titleId ?: ""

                if (uri != null && act != null && titleId.isNotEmpty()) {

                    // Only accept .txt files

                    val okExt = runCatching {

                        DocumentFile.fromSingleUri(act, uri)?.name?.lowercase()?.endsWith(".txt") == true

                    }.getOrElse { false }

                    if (!okExt) {

                        Toast.makeText(act, "Please select a .txt file", Toast.LENGTH_SHORT).show()

                        return@rememberLauncherForActivityResult

                    }



                    val res = importCheatTxt(act, titleId, uri)

                    if (res.isSuccess) {

                        Toast.makeText(act, "Imported: ${res.getOrNull()?.name}", Toast.LENGTH_SHORT).show()

                        // Then update the list

                        cheatsForSelected.value = loadCheatsFromDisk(act, titleId)

                    } else {

                        Toast.makeText(act, "Import failed: ${res.exceptionOrNull()?.message}", Toast.LENGTH_LONG).show()

                    }

                }

            }

            val pickModFolderLauncher = rememberLauncherForActivityResult(
                contract = ActivityResultContracts.OpenDocumentTree()
            ) { uri: Uri? ->
                val act = viewModel.activity
                val titleId = viewModel.mainViewModel?.selected?.titleId.orEmpty()
                if (uri != null && act != null && titleId.isNotEmpty() && !modsImportBusy.value) {
                    modsImportBusy.value = true
                    modsImportProgress.floatValue = -1f
                    modsImportStatusText.value = "Preparando a pasta…"
                    thread(name = "ModFolderImport") {
                        val result = importModsFolder(act, titleId, uri) { progress ->
                            launchOnUiThread {
                                modsImportProgress.floatValue = if (progress.totalBytes > 0) progress.fraction else -1f
                                modsImportStatusText.value = "Copiando: ${progress.currentEntry}"
                            }
                        }
                        val installed = listMods(act, titleId)
                        launchOnUiThread {
                            modsForSelected.value = installed
                            modsImportBusy.value = false
                            Toast.makeText(act, if (result.ok) "Importado: ${result.imported.joinToString(", ")}"
                                else result.error ?: "Falha ao importar a pasta", Toast.LENGTH_LONG).show()
                        }
                    }
                }
            }

            // Launcher for Mods

            val pickModZipLauncher = rememberLauncherForActivityResult(

                contract = ActivityResultContracts.OpenDocument()

            ) { uri: Uri? ->

                val gm = viewModel.mainViewModel?.selected

                val act = viewModel.activity

                val titleId = gm?.titleId ?: ""

                if (uri != null && act != null && titleId.isNotEmpty()) {

                    // Persist permission (read)

                    try {

                        act.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION)

                    } catch (_: Exception) {}



                    modsImportBusy.value = true

                    modsImportProgress.floatValue = 0f

                    modsImportStatusText.value = "Starting…"



                    thread {

                        val res = importModsZip(

                            act,

                            titleId,

                            uri

                        ) { prog ->

                            modsImportProgress.floatValue = prog.fraction

                            modsImportStatusText.value = if (prog.currentEntry.isNotEmpty())

                                "Copying: ${prog.currentEntry}"

                            else

                                "Copying… ${(prog.fraction * 100).toInt()}%"

                        }



                        // Update list

                        modsForSelected.value = listMods(act, titleId)

                        modsImportBusy.value = false



                        launchOnUiThread {

                            val msg = if (res.ok)

                                "Imported: ${res.imported.joinToString(", ")}"

                            else

                                "Import failed"

                            Toast.makeText(act, msg, Toast.LENGTH_SHORT).show()

                        }

                    }



                }

            }





            // Launcher for "Custom icon" (OpenDocument)

            val pickImageLauncher = rememberLauncherForActivityResult(

                contract = ActivityResultContracts.OpenDocument()

            ) { uri: Uri? ->

                val gm = viewModel.mainViewModel?.selected

                if (uri != null && gm != null && activity != null) {

                    val bmp = runCatching {

                        context.contentResolver.openInputStream(uri).use { BitmapFactory.decodeStream(it) }

                    }.getOrNull()



                    val label = shortcutName.value.ifBlank { gm.titleName ?: "Start Game" }

                    val gameUri = resolveGameUri(gm)

                    if (gameUri != null) {

                        ShortcutUtils.persistReadWrite(activity, gameUri)



                        ShortcutUtils.pinShortcutForGame(

                            activity = activity,

                            gameUri = gameUri,

                            label = label,

                            iconBitmap = bmp

                        ) {



                        }

                    } else {

                        showError.value = "Shortcut failed (no game URI found)."

                    }

                }

            }



            val nestedScrollConnection = remember {

                object : NestedScrollConnection {

                    override fun onPreScroll(available: Offset, source: NestedScrollSource): Offset {

                        if (available.y < -1) isFabVisible = false

                        if (available.y > 1) isFabVisible = true

                        return Offset.Zero

                    }

                }

            }



            val homeBackground by animateColorAsState(

                targetValue = MaterialTheme.colorScheme.background,

                animationSpec = tween(420),

                label = "homeBackground"

            )

            val homeBackgroundEnd by animateColorAsState(

                targetValue = MaterialTheme.colorScheme.surfaceVariant,

                animationSpec = tween(420),

                label = "homeBackgroundEnd"

            )



            Box(

                modifier = Modifier

                    .fillMaxSize()

                    .background(

                        Brush.verticalGradient(

                            listOf(

                                homeBackground,

                                homeBackgroundEnd

                            )

                        )

                    )

            ) {

                Crossfade(targetState = homeLayout, animationSpec = tween(360), label = "homeLayout") { layout ->

                    if (layout == HomeLayout.Switch) {

                        SwitchHome(

                            viewModel = viewModel,

                            query = query,

                            showAppActions = showAppActions,

                            showLoading = showLoading,

                            selectedModel = selectedModel,

                            showError = showError,

                            onChangeLayout = {

                                homeLayout = HomeLayout.Neo

                                homePreferences.edit().putString(HOME_LAYOUT_KEY, homeLayout.name).apply()

                            },

                            onOpenSettings = {

                                category ->

                                val route = category?.let { "settings?category=$it" } ?: "settings"

                                navController?.navigate(route) { launchSingleTop = true }

                            },

                            isPreview = isPreview

                        )

                    } else {

                Box(modifier = Modifier.fillMaxSize()) {

                    Column(

                        modifier = Modifier

                            .fillMaxSize()

                            .padding(horizontal = if (neoCompactWidth()) 12.dp else 30.dp, vertical = 12.dp),

                        verticalArrangement = Arrangement.spacedBy(10.dp)

                    ) {

                    SharedHomeHeader(query)



                    // Caixinha Principal: Library Panel (Grid de 7 Colunas com Mockup do Cartucho)

                    Surface(

                        modifier = Modifier

                            .fillMaxWidth()

                            .weight(1f),

                        shape = RoundedCornerShape(0.dp),

                        color = Color.Transparent,

                        shadowElevation = 0.dp

                    ) {

                        val list = remember { viewModel.gameList }

                        val isLoading = remember { viewModel.isLoading }



                        viewModel.filter(query.value)



                        if (!isPreview) {

                            if (isLoading.value) {

                                Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {

                                    CircularProgressIndicator(

                                        modifier = Modifier.size(54.dp),

                                        color = MaterialTheme.colorScheme.onBackground,

                                        trackColor = MaterialTheme.colorScheme.surfaceVariant

                                    )

                                }

                            } else {

                                val filteredList = list.filter { game ->

                                    val title = game.titleName.orEmpty()

                                    title.isNotEmpty() && (query.value.trim().isEmpty() || title.lowercase(Locale.getDefault()).contains(query.value.lowercase(Locale.getDefault())))

                                }



                                if (filteredList.isEmpty()) {

                                    EmptyLibraryState()

                                } else {

                                    LazyVerticalGrid(

                                        columns = GridCells.Fixed(neoGridColumns()),

                                        contentPadding = PaddingValues(16.dp),

                                        horizontalArrangement = Arrangement.spacedBy(10.dp),

                                        verticalArrangement = Arrangement.spacedBy(14.dp),

                                        modifier = Modifier.fillMaxSize()

                                    ) {

                                        items(filteredList) { game ->

                                            GridGameItem(game, viewModel, showAppActions, showLoading, selectedModel, showError)

                                        }

                                    }

                                }

                            }

                        }

                    }

                    HomeBottomControls(

                        layoutIcon = Icons.Default.ViewCarousel,

                        layoutDescription = "Usar visual Switch",

                        onChangeLayout = {

                            homeLayout = HomeLayout.Switch

                            homePreferences.edit().putString(HOME_LAYOUT_KEY, homeLayout.name).apply()

                        },

                        onOpenSettings = { category ->

                            val route = category?.let { "settings?category=$it" } ?: "settings"

                            navController?.navigate(route) { launchSingleTop = true }

                        }

                    )

                    }

                }

                    }

                }



                    SimpleAlertDialog.Loading(showDialog = showLoading)

                    SimpleAlertDialog.Custom(

                        showDialog = openTitleUpdateDialog,

                        onDismissRequest = { openTitleUpdateDialog.value = false },

                        properties = DialogProperties(usePlatformDefaultWidth = false),

                        widthFraction = 0.82f

                    ) {

                        val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""

                        val name = viewModel.mainViewModel?.selected?.titleName ?: ""

                        TitleUpdateViews.Main(titleId, name, openTitleUpdateDialog, canClose)

                    }

                    SimpleAlertDialog.Custom(

                        showDialog = openDlcDialog,

                        onDismissRequest = { openDlcDialog.value = false },

                        properties = DialogProperties(usePlatformDefaultWidth = false),

                        widthFraction = 0.82f

                    ) {

                        val titleId = viewModel.mainViewModel?.selected?.titleId ?: ""

                        val name = viewModel.mainViewModel?.selected?.titleName ?: ""

                        DlcViews.Main(titleId, name, openDlcDialog, canClose)

                    }



                    // Amiibo slot chooser dialog (outside of game)

                    if (showAmiiboSlotDialog.value) {

                        BasicAlertDialog(

                            onDismissRequest = { showAmiiboSlotDialog.value = false }

                        ) {

                            Surface(

                                shape = RoundedCornerShape(18.dp),

                                color = MaterialTheme.colorScheme.surface,

                                border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)),

                                modifier = Modifier

                                    .widthIn(max = 520.dp)

                                    .fillMaxWidth(0.92f)

                            ) {

                                Column(Modifier.padding(18.dp)) {

                                    // Header: Switch style 54dp

                                    Row(

                                        modifier = Modifier.fillMaxWidth().padding(bottom = 14.dp),

                                        verticalAlignment = Alignment.CenterVertically

                                    ) {

                                        Surface(

                                            shape = RoundedCornerShape(12.dp),

                                            color = MaterialTheme.colorScheme.primaryContainer,

                                            modifier = Modifier.size(54.dp)

                                        ) {

                                            Box(contentAlignment = Alignment.Center) {

                                                Icon(

                                                    imageVector = Icons.Default.Contactless,

                                                    contentDescription = null,

                                                    tint = MaterialTheme.colorScheme.onPrimaryContainer,

                                                    modifier = Modifier.size(28.dp)

                                                )

                                            }

                                        }

                                        Spacer(Modifier.width(14.dp))

                                        Column(modifier = Modifier.weight(1f)) {

                                            Text(

                                                text = "Importar Amiibo",

                                                style = MaterialTheme.typography.titleMedium,

                                                fontWeight = FontWeight.Bold,

                                                maxLines = 1,

                                                overflow = TextOverflow.Ellipsis

                                            )

                                            Text(

                                                text = "Selecione o slot de armazenamento",

                                                style = MaterialTheme.typography.bodySmall,

                                                color = MaterialTheme.colorScheme.onSurfaceVariant,

                                                maxLines = 1,

                                                overflow = TextOverflow.Ellipsis

                                            )

                                        }

                                        IconButton(onClick = { showAmiiboSlotDialog.value = false }) {

                                            Icon(Icons.Default.Close, contentDescription = "Fechar")

                                        }

                                    }



                                    Text(

                                        text = "Escolha um slot (1 a 5) para armazenar este Amiibo:",

                                        style = MaterialTheme.typography.bodySmall,

                                        color = MaterialTheme.colorScheme.onSurfaceVariant,

                                        modifier = Modifier.padding(bottom = 12.dp)

                                    )



                                    Row(

                                        modifier = Modifier.fillMaxWidth(),

                                        horizontalArrangement = Arrangement.spacedBy(8.dp)

                                    ) {

                                        (1..5).forEach { slot ->

                                            Surface(

                                                shape = RoundedCornerShape(12.dp),

                                                color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f),

                                                border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),

                                                modifier = Modifier

                                                    .weight(1f)

                                                    .height(58.dp)

                                                    .clip(RoundedCornerShape(12.dp))

                                                    .clickable {

                                                        pendingSlot.value = slot

                                                        pickAmiiboLauncher.launch(arrayOf("application/octet-stream", "*/*"))

                                                        showAmiiboSlotDialog.value = false

                                                    }

                                            ) {

                                                Column(

                                                    modifier = Modifier.fillMaxSize(),

                                                    horizontalAlignment = Alignment.CenterHorizontally,

                                                    verticalArrangement = Arrangement.Center

                                                ) {

                                                    Text(

                                                        text = "Slot $slot",

                                                        fontWeight = FontWeight.Bold,

                                                        style = MaterialTheme.typography.bodyMedium,

                                                        color = MaterialTheme.colorScheme.onSurface

                                                    )

                                                }

                                            }

                                        }

                                    }



                                    Spacer(Modifier.height(16.dp))



                                    Row(

                                        modifier = Modifier.fillMaxWidth(),

                                        horizontalArrangement = Arrangement.End

                                    ) {

                                        OutlinedButton(onClick = { showAmiiboSlotDialog.value = false }) {

                                            Text("Fechar")

                                        }

                                    }

                                }

                            }

                        }

                    }



                if (viewModel.mainViewModel?.loadGameModel?.value != null)

                    LaunchedEffect(viewModel.mainViewModel.loadGameModel.value) {

                        if (viewModel.mainViewModel.bootPath.value ==

                            "gameItem_${viewModel.mainViewModel.loadGameModel.value!!.titleName}"

                        ) {

                            viewModel.mainViewModel.bootPath.value = null



                            thread {

                                showLoading.value = true



                                // Push cheats before starting (Auto-start path)

                                val gm = viewModel.mainViewModel.loadGameModel.value!!

                                val tId = gm.titleId ?: ""

                                val act = viewModel.activity



                                val success = viewModel.mainViewModel.loadGame(gm)

                                if (success == 1) {

                                    launchOnUiThread {

                                        viewModel.mainViewModel.navigateToGame()

                                    }

                                } else {

                                    if (success == -2)

                                        showError.value = "Error loading update. Please re-add update file"

                                    viewModel.mainViewModel.loadGameModel.value!!.close()

                                }

                                showLoading.value = false

                            }

                        }

                    }



                if (showAppActions.value) {

                    val gm = viewModel.mainViewModel?.selected

                    if (gm != null) {

                        GameActionsDialog(

                            game = gm,

                            onDismiss = {

                                showAppActions.value = false

                                viewModel.mainViewModel?.apply { selected = null }

                                selectedModel.value = null

                            },

                            onPlay = {

                                showAppActions.value = false

                                val act = viewModel.activity

                                thread {

                                    showLoading.value = true

                                    val success = viewModel.mainViewModel?.loadGame(gm) ?: 0

                                    if (success == 1) {

                                        launchOnUiThread {

                                            viewModel.mainViewModel?.navigateToGame()

                                        }

                                    } else {

                                        if (success == -2)

                                            showError.value = "Error loading update. Please re-add update file"

                                        gm.close()

                                    }

                                    showLoading.value = false

                                }

                            },

                            onConfigure = {

                                showAppActions.value = false

                                navController?.navigate("gameSettings") // NEOV2-UI1: pagina propria em vez de popup

                            },

                            onUpdates = {

                                showAppActions.value = false

                                openTitleUpdateDialog.value = true

                            },

                            onDlc = {

                                showAppActions.value = false

                                openDlcDialog.value = true

                            },

                            onMods = {

                                showAppActions.value = false

                                val act = viewModel.activity

                                if (!gm.titleId.isNullOrEmpty() && act != null) {

                                    val titleId = gm.titleId!!

                                    modsForSelected.value = listMods(act, titleId)

                                    openModsDialog.value = true

                                } else {

                                    showError.value = "No title selected."

                                }

                            },

                            onCheats = {

                                showAppActions.value = false

                                val act = viewModel.activity

                                if (!gm.titleId.isNullOrEmpty() && act != null) {

                                    val titleId = gm.titleId!!

                                    cheatsForSelected.value = loadCheatsFromDisk(act, titleId)

                                    enabledCheatKeys.value = CheatPrefs(act).getEnabled(titleId)

                                    openCheatsDialog.value = true

                                } else {

                                    showError.value = "No title selected."

                                }

                            },

                            onSaves = {

                                showAppActions.value = false

                                openSavesDialog.value = true

                            },

                            onAmiibo = {

                                showAppActions.value = false

                                showAmiiboSlotDialog.value = true

                            },

                            onCreateShortcut = {

                                showAppActions.value = false

                                shortcutName.value = gm.titleName ?: ""

                                showShortcutDialog.value = true

                            },

                            onClearPptc = {

                                viewModel.mainViewModel?.clearPptcCache(gm.titleId.orEmpty())

                                Toast.makeText(viewModel.activity, "Cache PPTC limpo", Toast.LENGTH_SHORT).show()

                            },

                            onPurgeShader = {

                                viewModel.mainViewModel?.purgeShaderCache(gm.titleId.orEmpty())

                                Toast.makeText(viewModel.activity, "Cache de shaders limpo", Toast.LENGTH_SHORT).show()

                            },

                            onDeleteCache = {

                                viewModel.mainViewModel?.deleteCache(gm.titleId.orEmpty())

                                Toast.makeText(viewModel.activity, "Todos os caches excluídos", Toast.LENGTH_SHORT).show()

                            },

                            // NEOV2-NCZ2: fim da conversao para NSZ.
                            onCompressed = { success, message ->

                                Toast.makeText(viewModel.activity, message, Toast.LENGTH_LONG).show()

                                if (success) {

                                    showAppActions.value = false

                                    viewModel.mainViewModel?.apply { selected = null }

                                    selectedModel.value = null

                                    viewModel.requestReload()

                                    viewModel.ensureReloadIfNecessary()

                                }

                            }

                        )

                    } else {

                        showAppActions.value = false

                    }

                }



                // NEOV2-UI1: GameSettingsDialog virou a pagina "gameSettings" (MainView).



                // Management windows (Standardized Switch Style)

                // --- Cheats Dialog ---

                if (openCheatsDialog.value) {

                    BasicAlertDialog(

                        onDismissRequest = { openCheatsDialog.value = false }

                    ) {

                        val gm = viewModel.mainViewModel?.selected

                        val act = viewModel.activity

                        val titleId = gm?.titleId ?: ""

                        val cheats = cheatsForSelected.value

                        val enabledCount = cheats.count { enabledCheatKeys.value.contains(it.key) }



                        Surface(

                            shape = RoundedCornerShape(18.dp),

                            color = MaterialTheme.colorScheme.surface,

                            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)),

                            modifier = Modifier

                                .widthIn(max = 560.dp)

                                .fillMaxWidth(0.92f)

                                .heightIn(max = 620.dp)

                        ) {

                            Column(Modifier.padding(18.dp)) {

                                // Header: 54dp Switch style

                                Row(

                                    modifier = Modifier.fillMaxWidth().padding(bottom = 14.dp),

                                    verticalAlignment = Alignment.CenterVertically

                                ) {

                                    Surface(

                                        shape = RoundedCornerShape(12.dp),

                                        color = MaterialTheme.colorScheme.primaryContainer,

                                        modifier = Modifier.size(54.dp)

                                    ) {

                                        Box(contentAlignment = Alignment.Center) {

                                            Icon(

                                                imageVector = Icons.Default.Code,

                                                contentDescription = null,

                                                tint = MaterialTheme.colorScheme.onPrimaryContainer,

                                                modifier = Modifier.size(28.dp)

                                            )

                                        }

                                    }

                                    Spacer(Modifier.width(14.dp))

                                    Column(modifier = Modifier.weight(1f)) {

                                        Text(

                                            text = gm?.titleName ?: "Gerenciar Cheats",

                                            style = MaterialTheme.typography.titleMedium,

                                            fontWeight = FontWeight.Bold,

                                            maxLines = 1,

                                            overflow = TextOverflow.Ellipsis

                                        )

                                        val sub = buildList {
                                            if (titleId.isNotBlank()) add(titleId.uppercase())
                                            if (!gm?.version.isNullOrBlank()) add("v${gm?.version}")
                                            add("Cheats")
                                            add("$enabledCount de ${cheats.size} ativados")
                                        }.joinToString(" • ")
                                        Text(
                                            text = sub,
                                            style = MaterialTheme.typography.bodySmall,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                                            maxLines = 2
                                        )

                                    }

                                    IconButton(onClick = { openCheatsDialog.value = false }) {

                                        Icon(Icons.Default.Close, contentDescription = "Fechar")

                                    }

                                }



                                // Content Area

                                if (cheats.isEmpty()) {

                                    Surface(

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .height(200.dp),

                                        shape = RoundedCornerShape(16.dp),

                                        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.4f),

                                        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.3f))

                                    ) {

                                        Column(

                                            modifier = Modifier.fillMaxSize().padding(20.dp),

                                            horizontalAlignment = Alignment.CenterHorizontally,

                                            verticalArrangement = Arrangement.Center

                                        ) {

                                            Icon(

                                                imageVector = Icons.Default.Code,

                                                contentDescription = null,

                                                tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.7f),

                                                modifier = Modifier.size(44.dp)

                                            )

                                            Spacer(Modifier.height(10.dp))

                                            Text(

                                                text = "Nenhum cheat encontrado",

                                                style = MaterialTheme.typography.titleSmall,

                                                fontWeight = FontWeight.SemiBold,

                                                color = MaterialTheme.colorScheme.onSurface

                                            )

                                            Spacer(Modifier.height(4.dp))

                                            Text(

                                                text = "Importe arquivos .txt contendo códigos de trapaça para este jogo.",

                                                style = MaterialTheme.typography.bodySmall,

                                                color = MaterialTheme.colorScheme.onSurfaceVariant,

                                                textAlign = TextAlign.Center

                                            )

                                        }

                                    }

                                } else {

                                    LazyColumn(

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .weight(1f, fill = false),

                                        verticalArrangement = Arrangement.spacedBy(8.dp)

                                    ) {

                                        items(cheats) { cheat ->

                                            val checked = enabledCheatKeys.value.contains(cheat.key)

                                            Surface(

                                                shape = RoundedCornerShape(12.dp),

                                                color = if (checked) MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.3f)

                                                       else MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f),

                                                border = BorderStroke(

                                                    1.dp,

                                                    if (checked) MaterialTheme.colorScheme.primary.copy(alpha = 0.4f)

                                                    else MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)

                                                ),

                                                modifier = Modifier

                                                    .fillMaxWidth()

                                                    .clip(RoundedCornerShape(12.dp))

                                                    .clickable {

                                                        enabledCheatKeys.value =

                                                            enabledCheatKeys.value.toMutableSet().apply {

                                                                if (!checked) add(cheat.key) else remove(cheat.key)

                                                            }

                                                    }

                                            ) {

                                                Row(

                                                    modifier = Modifier.padding(horizontal = 14.dp, vertical = 12.dp),

                                                    verticalAlignment = Alignment.CenterVertically,

                                                    horizontalArrangement = Arrangement.SpaceBetween

                                                ) {

                                                    Column(

                                                        modifier = Modifier.weight(1f).padding(end = 12.dp)

                                                    ) {

                                                        Text(

                                                            text = cheat.name,

                                                            style = MaterialTheme.typography.bodyMedium,

                                                            fontWeight = FontWeight.SemiBold,

                                                            maxLines = 2,

                                                            overflow = TextOverflow.Ellipsis

                                                        )

                                                        Text(

                                                            text = "Build ID: ${cheat.buildId}",

                                                            style = MaterialTheme.typography.labelSmall,

                                                            color = MaterialTheme.colorScheme.onSurfaceVariant

                                                        )

                                                    }

                                                    Switch(

                                                        checked = checked,

                                                        onCheckedChange = { isOn ->

                                                            enabledCheatKeys.value =

                                                                enabledCheatKeys.value.toMutableSet().apply {

                                                                    if (isOn) add(cheat.key) else remove(cheat.key)

                                                                }

                                                        }

                                                    )

                                                }

                                            }

                                        }

                                    }

                                }



                                Spacer(Modifier.height(16.dp))



                                // Bottom Action Buttons matching DlcViews

                                Row(

                                    modifier = Modifier.fillMaxWidth(),

                                    horizontalArrangement = Arrangement.spacedBy(10.dp),

                                    verticalAlignment = Alignment.CenterVertically

                                ) {

                                    OutlinedButton(onClick = {

                                        importCheatLauncher.launch(arrayOf("text/plain", "text/*", "*/*"))

                                    }) {

                                        Icon(Icons.Default.Add, contentDescription = null, modifier = Modifier.size(18.dp))

                                        Spacer(Modifier.width(6.dp))

                                        Text("Importar .txt")

                                    }



                                    Spacer(modifier = Modifier.weight(1f))



                                    OutlinedButton(
                                        onClick = { openCheatsDialog.value = false },
                                        contentPadding = PaddingValues(horizontal = 14.dp, vertical = 8.dp)
                                    ) {
                                        Text("Cancelar", maxLines = 1, softWrap = false)
                                    }



                                    Button(

                                        onClick = {

                                            val act2 = act

                                            if (act2 != null && titleId.isNotEmpty()) {

                                                CheatPrefs(act2).setEnabled(titleId, enabledCheatKeys.value)

                                                applyCheatSelectionOnDisk(act2, titleId, enabledCheatKeys.value)

                                                cheatsForSelected.value = loadCheatsFromDisk(act2, titleId)

                                            }

                                            openCheatsDialog.value = false

                                        },

                                        colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.primary)

                                    ) {

                                        Icon(Icons.Default.Check, contentDescription = null, modifier = Modifier.size(18.dp))

                                        Spacer(Modifier.width(6.dp))

                                        Text("Salvar", fontWeight = FontWeight.Bold, maxLines = 1, softWrap = false)

                                    }

                                }

                            }

                        }

                    }

                }



                // --- Mods Dialog ---

                if (openModsDialog.value) {

                    BasicAlertDialog(

                        onDismissRequest = { if (!modsImportBusy.value) openModsDialog.value = false }

                    ) {

                        val gm = viewModel.mainViewModel?.selected

                        val act = viewModel.activity

                        val titleId = gm?.titleId ?: ""

                        val mods = modsForSelected.value



                        Surface(

                            shape = RoundedCornerShape(18.dp),

                            color = MaterialTheme.colorScheme.surface,

                            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)),

                            modifier = Modifier

                                .widthIn(max = 560.dp)

                                .fillMaxWidth(0.92f)

                                .heightIn(max = 620.dp)

                        ) {

                            Column(Modifier.padding(18.dp)) {

                                // Header: 54dp Switch style

                                Row(

                                    modifier = Modifier.fillMaxWidth().padding(bottom = 14.dp),

                                    verticalAlignment = Alignment.CenterVertically

                                ) {

                                    Surface(

                                        shape = RoundedCornerShape(12.dp),

                                        color = MaterialTheme.colorScheme.primaryContainer,

                                        modifier = Modifier.size(54.dp)

                                    ) {

                                        Box(contentAlignment = Alignment.Center) {

                                            Icon(

                                                imageVector = Icons.Default.ViewModule,

                                                contentDescription = null,

                                                tint = MaterialTheme.colorScheme.onPrimaryContainer,

                                                modifier = Modifier.size(28.dp)

                                            )

                                        }

                                    }

                                    Spacer(Modifier.width(14.dp))

                                    Column(modifier = Modifier.weight(1f)) {

                                        Text(

                                            text = gm?.titleName ?: "Gerenciar Mods",

                                            style = MaterialTheme.typography.titleMedium,

                                            fontWeight = FontWeight.Bold,

                                            maxLines = 1,

                                            overflow = TextOverflow.Ellipsis

                                        )

                                        val sub = buildList {

                                            if (titleId.isNotBlank()) add(titleId.uppercase())

                                            add("Mods")

                                            add("${mods.size} instalados")

                                        }.joinToString(" • ")

                                        Text(

                                            text = sub,

                                            style = MaterialTheme.typography.bodySmall,

                                            color = MaterialTheme.colorScheme.onSurfaceVariant,

                                            maxLines = 1,

                                            overflow = TextOverflow.Ellipsis

                                        )

                                    }

                                    IconButton(enabled = !modsImportBusy.value, onClick = { openModsDialog.value = false }) {

                                        Icon(Icons.Default.Close, contentDescription = "Fechar")

                                    }

                                }



                                if (modsImportBusy.value) {

                                    if (modsImportProgress.value < 0f) LinearProgressIndicator(
                                        modifier = Modifier.fillMaxWidth().padding(bottom = 8.dp)
                                    ) else

                                    LinearProgressIndicator(

                                        progress = { modsImportProgress.value },

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .padding(bottom = 8.dp)

                                    )

                                    Text(

                                        modsImportStatusText.value,

                                        style = MaterialTheme.typography.labelSmall,

                                        color = MaterialTheme.colorScheme.onSurfaceVariant,

                                        modifier = Modifier.padding(bottom = 8.dp)

                                    )

                                }



                                // Content Area

                                if (mods.isEmpty()) {

                                    Surface(

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .weight(1f, fill = false).heightIn(max = 200.dp),

                                        shape = RoundedCornerShape(16.dp),

                                        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.4f),

                                        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.3f))

                                    ) {

                                        Column(

                                            modifier = Modifier.fillMaxSize().padding(20.dp),

                                            horizontalAlignment = Alignment.CenterHorizontally,

                                            verticalArrangement = Arrangement.Center

                                        ) {

                                            Icon(

                                                imageVector = Icons.Default.ViewModule,

                                                contentDescription = null,

                                                tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.7f),

                                                modifier = Modifier.size(44.dp)

                                            )

                                            Spacer(Modifier.height(10.dp))

                                            Text(

                                                text = "Nenhum mod instalado",

                                                style = MaterialTheme.typography.titleSmall,

                                                fontWeight = FontWeight.SemiBold,

                                                color = MaterialTheme.colorScheme.onSurface

                                            )

                                            Spacer(Modifier.height(4.dp))

                                            Text(

                                                text = "Selecione a pasta do mod que contém romfs, exefs ou cheats, ou importe um arquivo .zip.",

                                                style = MaterialTheme.typography.bodySmall,

                                                color = MaterialTheme.colorScheme.onSurfaceVariant,

                                                textAlign = TextAlign.Center

                                            )

                                        }

                                    }

                                } else {

                                    LazyColumn(

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .weight(1f, fill = false),

                                        verticalArrangement = Arrangement.spacedBy(8.dp)

                                    ) {

                                        items(mods) { modName ->

                                            Surface(

                                                shape = RoundedCornerShape(12.dp),

                                                color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f),

                                                border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)),

                                                modifier = Modifier.fillMaxWidth()

                                            ) {

                                                Row(

                                                    modifier = Modifier.padding(horizontal = 14.dp, vertical = 10.dp),

                                                    verticalAlignment = Alignment.CenterVertically,

                                                    horizontalArrangement = Arrangement.SpaceBetween

                                                ) {

                                                    Row(

                                                        verticalAlignment = Alignment.CenterVertically,

                                                        modifier = Modifier.weight(1f)

                                                    ) {

                                                        Icon(

                                                            imageVector = Icons.Default.ViewModule,

                                                            contentDescription = null,

                                                            tint = MaterialTheme.colorScheme.primary,

                                                            modifier = Modifier.size(22.dp)

                                                        )

                                                        Spacer(Modifier.width(12.dp))

                                                        Text(

                                                            text = modName,

                                                            style = MaterialTheme.typography.bodyMedium,

                                                            fontWeight = FontWeight.SemiBold,

                                                            maxLines = 2,

                                                            overflow = TextOverflow.Ellipsis

                                                        )

                                                    }

                                                    IconButton(

                                                        onClick = {

                                                            val a = act

                                                            if (a != null && titleId.isNotEmpty()) {

                                                                thread {

                                                                    val ok = deleteMod(a, titleId, modName)

                                                                    if (ok) {

                                                                        modsForSelected.value = listMods(a, titleId)

                                                                    }

                                                                }

                                                            }

                                                        }

                                                    ) {

                                                        Icon(

                                                            imageVector = Icons.Default.Delete,

                                                            contentDescription = "Excluir",

                                                            tint = MaterialTheme.colorScheme.error,

                                                            modifier = Modifier.size(20.dp)

                                                        )

                                                    }

                                                }

                                            }

                                        }

                                    }

                                }



                                Spacer(Modifier.height(16.dp))



                                // Bottom Action Buttons

                                Column(

                                    modifier = Modifier.fillMaxWidth(),

                                    verticalArrangement = Arrangement.spacedBy(10.dp),

                                    horizontalAlignment = Alignment.CenterHorizontally

                                ) {

                                    OutlinedButton(
                                        modifier = Modifier.fillMaxWidth(),
                                        enabled = !modsImportBusy.value,
                                        onClick = { pickModFolderLauncher.launch(null) }
                                    ) {
                                        Icon(Icons.Default.FolderOpen, contentDescription = null, modifier = Modifier.size(18.dp))
                                        Spacer(Modifier.width(6.dp))
                                        Text("Importar pasta")
                                    }
                                    OutlinedButton(

                                        enabled = !modsImportBusy.value,

                                        modifier = Modifier.fillMaxWidth(),
                                        onClick = { pickModZipLauncher.launch(arrayOf("application/zip", "application/octet-stream", "*/*")) }

                                    ) {

                                        Icon(Icons.Default.Add, contentDescription = null, modifier = Modifier.size(18.dp))

                                        Spacer(Modifier.width(6.dp))

                                        Text("Importar .zip")

                                    }







                                    OutlinedButton(modifier = Modifier.fillMaxWidth(), enabled = !modsImportBusy.value, onClick = { openModsDialog.value = false }) {

                                        Text("Fechar")

                                    }

                                }

                            }

                        }

                    }

                }



                // --- Saves Dialog ---

                if (openSavesDialog.value) {

                    BasicAlertDialog(

                        onDismissRequest = { openSavesDialog.value = false }

                    ) {

                        val gm = viewModel.mainViewModel?.selected

                        val titleId = gm?.titleId.orEmpty()



                        Surface(

                            shape = RoundedCornerShape(18.dp),

                            color = MaterialTheme.colorScheme.surface,

                            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)),

                            modifier = Modifier

                                .widthIn(max = 560.dp)

                                .fillMaxWidth(0.92f)

                                .heightIn(max = 620.dp)

                        ) {

                            Column(Modifier.padding(18.dp)) {

                                // Header: 54dp Switch style

                                Row(

                                    modifier = Modifier.fillMaxWidth().padding(bottom = 14.dp),

                                    verticalAlignment = Alignment.CenterVertically

                                ) {

                                    Surface(

                                        shape = RoundedCornerShape(12.dp),

                                        color = MaterialTheme.colorScheme.primaryContainer,

                                        modifier = Modifier.size(54.dp)

                                    ) {

                                        Box(contentAlignment = Alignment.Center) {

                                            Icon(

                                                imageVector = Icons.Default.Save,

                                                contentDescription = null,

                                                tint = MaterialTheme.colorScheme.onPrimaryContainer,

                                                modifier = Modifier.size(28.dp)

                                            )

                                        }

                                    }

                                    Spacer(Modifier.width(14.dp))

                                    Column(modifier = Modifier.weight(1f)) {

                                        Text(

                                            text = gm?.titleName ?: "Gerenciador de Saves",

                                            style = MaterialTheme.typography.titleMedium,

                                            fontWeight = FontWeight.Bold,

                                            maxLines = 1,

                                            overflow = TextOverflow.Ellipsis

                                        )

                                        val sub = buildList {

                                            if (titleId.isNotBlank()) add(titleId.uppercase())

                                            add("Dados salvos")

                                            add("Backup e Restauração")

                                        }.joinToString(" • ")

                                        Text(

                                            text = sub,

                                            style = MaterialTheme.typography.bodySmall,

                                            color = MaterialTheme.colorScheme.onSurfaceVariant,

                                            maxLines = 1,

                                            overflow = TextOverflow.Ellipsis

                                        )

                                    }

                                    IconButton(onClick = { openSavesDialog.value = false }) {

                                        Icon(Icons.Default.Close, contentDescription = "Fechar")

                                    }

                                }



                                // Two Action Cards: Import & Export

                                Column(

                                    modifier = Modifier.fillMaxWidth(),

                                    verticalArrangement = Arrangement.spacedBy(10.dp)

                                ) {

                                    val canAction = !saveImportBusy.value && !saveExportBusy.value && titleId.isNotEmpty()



                                    // Import Card

                                    Surface(

                                        shape = RoundedCornerShape(14.dp),

                                        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f),

                                        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .clip(RoundedCornerShape(14.dp))

                                            .clickable(enabled = canAction) {

                                                saveImportProgress.value = 0f

                                                saveImportStatus.value = ""

                                                importZipLauncher.launch(arrayOf("application/zip", "application/octet-stream", "*/*"))

                                            }

                                    ) {

                                        Row(

                                            modifier = Modifier.padding(horizontal = 14.dp, vertical = 14.dp),

                                            verticalAlignment = Alignment.CenterVertically

                                        ) {

                                            Icon(

                                                imageVector = Icons.Default.Add,

                                                contentDescription = null,

                                                tint = MaterialTheme.colorScheme.primary,

                                                modifier = Modifier.size(24.dp)

                                            )

                                            Spacer(Modifier.width(14.dp))

                                            Column(modifier = Modifier.weight(1f)) {

                                                Text(

                                                    text = "Importar Saves (.zip)",

                                                    fontWeight = FontWeight.SemiBold,

                                                    style = MaterialTheme.typography.bodyMedium,

                                                    color = MaterialTheme.colorScheme.onSurface

                                                )

                                                Text(

                                                    text = "Restaura arquivos de salvamento a partir de um arquivo ZIP",

                                                    style = MaterialTheme.typography.bodySmall,

                                                    color = MaterialTheme.colorScheme.onSurfaceVariant

                                                )

                                            }

                                        }

                                    }



                                    // Export Card

                                    Surface(

                                        shape = RoundedCornerShape(14.dp),

                                        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.45f),

                                        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),

                                        modifier = Modifier

                                            .fillMaxWidth()

                                            .clip(RoundedCornerShape(14.dp))

                                            .clickable(enabled = canAction) {

                                                val actLocal = activity

                                                if (actLocal != null && titleId.isNotEmpty()) {

                                                    val fname = suggestedCreateDocNameForExport(actLocal, titleId)

                                                    saveExportProgress.value = 0f

                                                    saveExportStatus.value = ""

                                                    exportZipLauncher.launch(fname)

                                                }

                                            }

                                    ) {

                                        Row(

                                            modifier = Modifier.padding(horizontal = 14.dp, vertical = 14.dp),

                                            verticalAlignment = Alignment.CenterVertically

                                        ) {

                                            Icon(

                                                imageVector = Icons.Default.Save,

                                                contentDescription = null,

                                                tint = MaterialTheme.colorScheme.primary,

                                                modifier = Modifier.size(24.dp)

                                            )

                                            Spacer(Modifier.width(14.dp))

                                            Column(modifier = Modifier.weight(1f)) {

                                                Text(

                                                    text = "Exportar Saves (.zip)",

                                                    fontWeight = FontWeight.SemiBold,

                                                    style = MaterialTheme.typography.bodyMedium,

                                                    color = MaterialTheme.colorScheme.onSurface

                                                )

                                                Text(

                                                    text = "Cria um arquivo ZIP com o backup completo dos saves",

                                                    style = MaterialTheme.typography.bodySmall,

                                                    color = MaterialTheme.colorScheme.onSurfaceVariant

                                                )

                                            }

                                        }

                                    }

                                }



                                if (saveImportBusy.value) {

                                    Column(Modifier.padding(top = 12.dp)) {

                                        LinearProgressIndicator(progress = { saveImportProgress.value }, modifier = Modifier.fillMaxWidth())

                                        Text(saveImportStatus.value, modifier = Modifier.padding(top = 6.dp), style = MaterialTheme.typography.labelSmall)

                                    }

                                }

                                if (saveExportBusy.value) {

                                    Column(Modifier.padding(top = 12.dp)) {

                                        LinearProgressIndicator(progress = { saveExportProgress.value }, modifier = Modifier.fillMaxWidth())

                                        Text(saveExportStatus.value, modifier = Modifier.padding(top = 6.dp), style = MaterialTheme.typography.labelSmall)

                                    }

                                }



                                Spacer(Modifier.height(16.dp))



                                Row(

                                    modifier = Modifier.fillMaxWidth(),

                                    horizontalArrangement = Arrangement.End

                                ) {

                                    OutlinedButton(onClick = { openSavesDialog.value = false }) {

                                        Text("Fechar")

                                    }

                                }

                            }

                        }

                    }

                }



                // --- Shortcut Dialog ---

                if (showShortcutDialog.value) {

                    val gm = viewModel.mainViewModel?.selected

                    BasicAlertDialog(

                        onDismissRequest = { showShortcutDialog.value = false }

                    ) {

                        Surface(

                            shape = RoundedCornerShape(18.dp),

                            color = MaterialTheme.colorScheme.surface,

                            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.2f)),

                            modifier = Modifier

                                .widthIn(max = 520.dp)

                                .fillMaxWidth(0.92f)

                        ) {

                            Column(Modifier.padding(18.dp)) {

                                // Header: 54dp Switch style

                                Row(

                                    modifier = Modifier.fillMaxWidth().padding(bottom = 14.dp),

                                    verticalAlignment = Alignment.CenterVertically

                                ) {

                                    Surface(

                                        shape = RoundedCornerShape(12.dp),

                                        color = MaterialTheme.colorScheme.primaryContainer,

                                        modifier = Modifier.size(54.dp)

                                    ) {

                                        Box(contentAlignment = Alignment.Center) {

                                            Icon(

                                                imageVector = Icons.Default.AddBox,

                                                contentDescription = null,

                                                tint = MaterialTheme.colorScheme.onPrimaryContainer,

                                                modifier = Modifier.size(28.dp)

                                            )

                                        }

                                    }

                                    Spacer(Modifier.width(14.dp))

                                    Column(modifier = Modifier.weight(1f)) {

                                        Text(

                                            text = "Criar Atalho",

                                            style = MaterialTheme.typography.titleMedium,

                                            fontWeight = FontWeight.Bold,

                                            maxLines = 1,

                                            overflow = TextOverflow.Ellipsis

                                        )

                                        Text(

                                            text = "Adicionar jogo à tela inicial",

                                            style = MaterialTheme.typography.bodySmall,

                                            color = MaterialTheme.colorScheme.onSurfaceVariant,

                                            maxLines = 1,

                                            overflow = TextOverflow.Ellipsis

                                        )

                                    }

                                    IconButton(onClick = { showShortcutDialog.value = false }) {

                                        Icon(Icons.Default.Close, contentDescription = "Fechar")

                                    }

                                }



                                OutlinedTextField(

                                    value = shortcutName.value,

                                    onValueChange = { shortcutName.value = it },

                                    label = { Text("Nome do atalho") },

                                    singleLine = true,

                                    modifier = Modifier.fillMaxWidth()

                                )



                                Spacer(Modifier.height(14.dp))



                                Text(

                                    text = "Escolha o ícone do atalho:",

                                    style = MaterialTheme.typography.bodySmall,

                                    color = MaterialTheme.colorScheme.onSurfaceVariant,

                                    modifier = Modifier.padding(bottom = 10.dp)

                                )



                                Row(
                                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                                    modifier = Modifier.fillMaxWidth()
                                ) {
                                    // Game Icon Card
                                    Surface(
                                        shape = RoundedCornerShape(12.dp),
                                        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f),
                                        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),
                                        modifier = Modifier
                                            .weight(1f)
                                            .height(58.dp)
                                            .clip(RoundedCornerShape(12.dp))
                                            .clickable {
                                                if (gm != null && activity != null) {
                                                    val gameUri = resolveGameUri(gm)
                                                    if (gameUri != null) {
                                                        ShortcutUtils.persistReadWrite(activity, gameUri)
                                                        val bmp = decodeGameIcon(gm)
                                                        val label = shortcutName.value.ifBlank { gm.titleName ?: "Iniciar Jogo" }
                                                        ShortcutUtils.pinShortcutForGame(
                                                            activity = activity,
                                                            gameUri = gameUri,
                                                            label = label,
                                                            iconBitmap = bmp
                                                        ) { }
                                                    }
                                                }
                                                showShortcutDialog.value = false
                                            }
                                    ) {
                                        Row(
                                            modifier = Modifier.fillMaxSize().padding(horizontal = 8.dp),
                                            verticalAlignment = Alignment.CenterVertically,
                                            horizontalArrangement = Arrangement.Center
                                        ) {
                                            Icon(Icons.Default.SportsEsports, contentDescription = null, modifier = Modifier.size(18.dp), tint = MaterialTheme.colorScheme.primary)
                                            Spacer(Modifier.width(6.dp))
                                            Text(
                                                text = "Ícone do jogo",
                                                fontWeight = FontWeight.SemiBold,
                                                style = MaterialTheme.typography.bodySmall,
                                                maxLines = 1,
                                                softWrap = false
                                            )
                                        }
                                    }

                                    // Custom Icon Card
                                    Surface(
                                        shape = RoundedCornerShape(12.dp),
                                        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f),
                                        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.25f)),
                                        modifier = Modifier
                                            .weight(1f)
                                            .height(58.dp)
                                            .clip(RoundedCornerShape(12.dp))
                                            .clickable {
                                                pickImageLauncher.launch(arrayOf("image/*"))
                                                showShortcutDialog.value = false
                                            }
                                    ) {
                                        Row(
                                            modifier = Modifier.fillMaxSize().padding(horizontal = 8.dp),
                                            verticalAlignment = Alignment.CenterVertically,
                                            horizontalArrangement = Arrangement.Center
                                        ) {
                                            Icon(Icons.Default.Palette, contentDescription = null, modifier = Modifier.size(18.dp), tint = MaterialTheme.colorScheme.primary)
                                            Spacer(Modifier.width(6.dp))
                                            Text(
                                                text = "Personalizado",
                                                fontWeight = FontWeight.SemiBold,
                                                style = MaterialTheme.typography.bodySmall,
                                                maxLines = 1,
                                                softWrap = false
                                            )
                                        }
                                    }
                                }



                                Spacer(Modifier.height(16.dp))



                                Row(

                                    modifier = Modifier.fillMaxWidth(),

                                    horizontalArrangement = Arrangement.End

                                ) {

                                    OutlinedButton(
                                        onClick = { showShortcutDialog.value = false },
                                        contentPadding = PaddingValues(horizontal = 14.dp, vertical = 8.dp)
                                    ) {
                                        Text("Cancelar", maxLines = 1, softWrap = false)
                                    }

                                }

                            }

                        }

                    }

                }



            } // End of box

        }



        @Composable

        private fun EmptyLibraryState() {

            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {

                Column(

                    horizontalAlignment = Alignment.CenterHorizontally,

                    verticalArrangement = Arrangement.spacedBy(8.dp)

                ) {

                    Text(

                        "Nenhum jogo encontrado",

                        style = MaterialTheme.typography.titleMedium,

                        color = MaterialTheme.colorScheme.onBackground,

                        fontWeight = FontWeight.Bold,

                        textAlign = TextAlign.Center

                    )

                    Text(

                        "Adicione pastas de jogos no menu de configurações.",

                        style = MaterialTheme.typography.bodySmall,

                        color = MaterialTheme.colorScheme.onSurfaceVariant,

                        textAlign = TextAlign.Center

                    )

                }

            }

        }



        // NEOV2-UI1: layout responsivo. Tela estreita (celular em pe) usa cabecalho, barra e margens compactos;
        // a biblioteca em grade usa menos colunas em modo retrato.
        @Composable
        private fun neoCompactWidth(): Boolean = androidx.compose.ui.platform.LocalConfiguration.current.screenWidthDp < 600

        @Composable
        private fun neoGridColumns(): Int {
            val config = androidx.compose.ui.platform.LocalConfiguration.current
            return if (config.screenHeightDp > config.screenWidthDp) {
                if (config.screenWidthDp < 480) 3 else 4
            } else {
                7
            }
        }

        @Composable
        private fun SharedHomeHeader(query: MutableState<String>) {
            val currentTime = remember { SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date()) }
            val neoCompact = neoCompactWidth()

            Row(

                modifier = Modifier

                    .fillMaxWidth()

                    .height(54.dp)

                    .padding(horizontal = 4.dp),

                verticalAlignment = Alignment.CenterVertically

            ) {

                Box(
                    modifier = Modifier.width(if (neoCompact) 104.dp else 188.dp),
                    contentAlignment = Alignment.CenterStart
                ) {

                    Crossfade(

                        targetState = NeoThemeState.isDark,

                        animationSpec = tween(320),

                        label = "neoLogoTheme"

                    ) { dark ->

                        Image(

                            painter = painterResource(

                                if (dark) R.drawable.ryujinx_neo_logo_light else R.drawable.ryujinx_neo_logo

                            ),

                            contentDescription = "Ryujinx Neo",

                            contentScale = ContentScale.Fit,

                            // The source artwork has transparent padding on its left edge.

                            // Keep its rendered size and compensate that padding visually.

                            modifier = Modifier

                                .width(if (neoCompact) 108.dp else 136.dp)
                                .height(if (neoCompact) 40.dp else 50.dp)

                                .offset(x = (-16).dp)

                        )

                    }

                }



                Spacer(if (neoCompact) Modifier.width(6.dp) else Modifier.weight(1f))
                Surface(
                    shape = CircleShape,
                    color = MaterialTheme.colorScheme.surface,
                    shadowElevation = 3.dp,
                    modifier = (if (neoCompact) Modifier.weight(1f) else Modifier.width(330.dp)).height(38.dp)
                ) {

                    Row(

                        modifier = Modifier.fillMaxSize().padding(horizontal = 14.dp),

                        verticalAlignment = Alignment.CenterVertically

                    ) {

                        BasicTextField(

                            value = query.value,

                            onValueChange = { query.value = it },

                            singleLine = true,

                            textStyle = TextStyle(

                                color = MaterialTheme.colorScheme.onSurface,

                                fontSize = 13.sp,

                                fontWeight = FontWeight.Medium

                            ),

                            modifier = Modifier.weight(1f),

                            decorationBox = { field ->

                                if (query.value.isEmpty()) {

                                    Text(

                                        "Pesquisar jogos...",

                                        color = MaterialTheme.colorScheme.onSurfaceVariant,

                                        fontSize = 13.sp

                                    )

                                }

                                field()

                            }

                        )

                        Icon(

                            Icons.Default.Search,

                            contentDescription = "Pesquisar",

                            tint = MaterialTheme.colorScheme.onSurfaceVariant,

                            modifier = Modifier.size(19.dp)

                        )

                    }

                }

                Spacer(if (neoCompact) Modifier.width(8.dp) else Modifier.weight(1f))
                Box(modifier = if (neoCompact) Modifier else Modifier.width(188.dp), contentAlignment = Alignment.CenterEnd) {

                    Text(

                        currentTime,

                        color = MaterialTheme.colorScheme.onBackground,

                        fontWeight = FontWeight.Bold,

                        fontSize = 15.sp

                    )

                }

            }

        }



        @Composable

        private fun HomeBottomControls(

            layoutIcon: androidx.compose.ui.graphics.vector.ImageVector,

            layoutDescription: String,

            onChangeLayout: () -> Unit,

            onOpenSettings: (String?) -> Unit,

            modifier: Modifier = Modifier

        ) {

            val context = LocalContext.current

            Box(modifier = modifier.fillMaxWidth()) {

                Surface(

                    modifier = Modifier.align(Alignment.Center),

                    shape = CircleShape,

                    color = MaterialTheme.colorScheme.surface,

                    shadowElevation = 5.dp

                ) {

                    Row(
                        modifier = Modifier.padding(if (neoCompactWidth()) 5.dp else 7.dp),
                        horizontalArrangement = Arrangement.spacedBy(if (neoCompactWidth()) 4.dp else 14.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        SwitchRailButton(Icons.Default.Memory, "CPU", Color(0xFFE3A33B)) { onOpenSettings("Emulacao") }

                        SwitchRailButton(Icons.Default.Palette, "Gráficos", Color(0xFF5DAA67)) { onOpenSettings("Graficos") }

                        SwitchRailButton(Icons.Default.SportsEsports, "Controles", Color(0xFF4B91D1)) { onOpenSettings("Controles") }

                        SwitchRailButton(Icons.Default.Folder, "Dados", Color(0xFF54A9A4)) { onOpenSettings("Jogos") }

                        SwitchRailButton(Icons.Default.Devices, "Sistema", Color(0xFF787F8A)) { onOpenSettings("Sistema") }

                        SwitchRailButton(Icons.Default.Settings, "Configurações", Color(0xFF626975)) { onOpenSettings(null) }

                    }

                }



                HomeCornerButton(

                    icon = if (NeoThemeState.isDark) Icons.Default.LightMode else Icons.Default.DarkMode,

                    description = if (NeoThemeState.isDark) "Ativar tema claro" else "Ativar tema escuro",

                    onClick = { NeoThemeState.toggle(context) },

                    modifier = Modifier.align(Alignment.CenterStart)

                )

                HomeCornerButton(

                    icon = layoutIcon,

                    description = layoutDescription,

                    onClick = onChangeLayout,

                    modifier = Modifier.align(Alignment.CenterEnd)

                )

            }

        }



        @Composable

        private fun HomeCornerButton(

            icon: androidx.compose.ui.graphics.vector.ImageVector,

            description: String,

            onClick: () -> Unit,

            modifier: Modifier = Modifier

        ) {

            Surface(

                modifier = modifier.size(if (neoCompactWidth()) 40.dp else 46.dp).clip(CircleShape).clickable(onClick = onClick),

                shape = CircleShape,

                color = MaterialTheme.colorScheme.surface,

                border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline),

                shadowElevation = 5.dp

            ) {

                Box(contentAlignment = Alignment.Center) {

                    Icon(

                        icon,

                        contentDescription = description,

                        tint = MaterialTheme.colorScheme.onSurface,

                        modifier = Modifier.size(21.dp)

                    )

                }

            }

        }



        @OptIn(ExperimentalFoundationApi::class)

        @Composable

        private fun SwitchHome(

            viewModel: HomeViewModel,

            query: MutableState<String>,

            showAppActions: MutableState<Boolean>,

            showLoading: MutableState<Boolean>,

            selectedModel: MutableState<GameModel?>,

            showError: MutableState<String>,

            onChangeLayout: () -> Unit,

            onOpenSettings: (String?) -> Unit,

            isPreview: Boolean

        ) {

            val list = remember { viewModel.gameList }

            val isLoading = remember { viewModel.isLoading }

            viewModel.filter(query.value)

            val filteredList = list.filter { game ->

                val title = game.titleName.orEmpty()

                title.isNotEmpty() && (query.value.isBlank() || title.contains(query.value.trim(), ignoreCase = true))

            }



            Column(

                modifier = Modifier

                    .fillMaxSize()

                    .padding(horizontal = if (neoCompactWidth()) 12.dp else 30.dp, vertical = 12.dp)

            ) {

                SharedHomeHeader(query)



                Box(

                    modifier = Modifier.weight(1f).fillMaxWidth(),

                    contentAlignment = Alignment.Center

                ) {

                    when {

                        isLoading.value -> CircularProgressIndicator(color = MaterialTheme.colorScheme.onBackground)

                        !isPreview && filteredList.isEmpty() -> EmptyLibraryState()

                        else -> LazyRow(

                            contentPadding = PaddingValues(horizontal = 8.dp, vertical = 24.dp),

                            horizontalArrangement = Arrangement.spacedBy(18.dp),

                            verticalAlignment = Alignment.CenterVertically,

                            modifier = Modifier.fillMaxWidth()

                        ) {

                            items(filteredList) { game ->

                                SwitchGameItem(game, viewModel, showAppActions, showLoading, selectedModel, showError)

                            }

                        }

                    }

                }



                HomeBottomControls(

                    layoutIcon = Icons.Default.ViewModule,

                    layoutDescription = "Usar visual NEO",

                    onChangeLayout = onChangeLayout,

                    onOpenSettings = onOpenSettings

                )

            }

        }



        @Composable

        private fun SwitchRailButton(

            icon: androidx.compose.ui.graphics.vector.ImageVector,

            label: String,

            color: Color,

            onClick: () -> Unit

        ) {

            Surface(

                shape = CircleShape,

                color = if (NeoThemeState.isDark) MaterialTheme.colorScheme.surfaceVariant else color,

                shadowElevation = 2.dp,

                modifier = Modifier
                    .size(if (neoCompactWidth()) 34.dp else 40.dp)
                    .clip(CircleShape)
                    .clickable(onClick = onClick)

            ) {

                Box(contentAlignment = Alignment.Center) {

                    Icon(

                        icon,

                        contentDescription = label,

                        tint = if (NeoThemeState.isDark) MaterialTheme.colorScheme.onSurfaceVariant else Color.White,

                        modifier = Modifier.size(21.dp)

                    )

                }

            }

        }



        @Composable

        private fun SwitchGameItem(

            gameModel: GameModel,

            viewModel: HomeViewModel,

            showAppActions: MutableState<Boolean>,

            showLoading: MutableState<Boolean>,

            selectedModel: MutableState<GameModel?>,

            showError: MutableState<String>

        ) {

            val context = LocalContext.current

            val interactionSource = remember { MutableInteractionSource() }

            val isPressed by interactionSource.collectIsPressedAsState()

            var isFocused by remember { mutableStateOf(false) }

            val emphasis = isPressed || isFocused

            val scale by animateFloatAsState(

                targetValue = if (isPressed) 0.965f else if (isFocused) 1.035f else 1f,

                animationSpec = tween(140),

                label = "switchGameScale"

            )

            val elevation by animateFloatAsState(

                targetValue = if (emphasis) 16f else 8f,

                animationSpec = tween(160),

                label = "switchGameElevation"

            )

            val coverBitmap = remember(gameModel.titleId) {

                try {

                    val iconFile = java.io.File(context.filesDir, "game_icons/${gameModel.titleId}.jpg")

                    if (iconFile.exists()) BitmapFactory.decodeFile(iconFile.absolutePath)

                    else if (gameModel.icon?.isNotEmpty() == true) {

                        val bytes = Base64.getDecoder().decode(gameModel.icon)

                        BitmapFactory.decodeByteArray(bytes, 0, bytes.size)

                    } else null

                } catch (_: Throwable) { null }

            }



            Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.width(168.dp)) {

                Surface(

                    modifier = Modifier

                        .size(168.dp)

                        .graphicsLayer {

                            scaleX = scale

                            scaleY = scale

                        }

                        .shadow(elevation.dp, RoundedCornerShape(10.dp))

                        .border(

                            width = if (emphasis) 3.dp else 1.dp,

                            color = if (emphasis) MaterialTheme.colorScheme.onBackground else MaterialTheme.colorScheme.outline,

                            shape = RoundedCornerShape(10.dp)

                        )

                        .onFocusChanged { isFocused = it.isFocused }

                        .combinedClickable(

                            interactionSource = interactionSource,

                            indication = null,

                            onClick = {

                                thread {

                                    showLoading.value = true

                                    val success = viewModel.mainViewModel?.loadGame(gameModel) ?: 0

                                    if (success == 1) launchOnUiThread { viewModel.mainViewModel?.navigateToGame() }

                                    else {

                                        if (success == -2) showError.value = "Erro ao carregar atualização."

                                        gameModel.close()

                                    }

                                    showLoading.value = false

                                }

                            },

                            onLongClick = {

                                viewModel.mainViewModel?.selected = gameModel

                                selectedModel.value = gameModel

                                showAppActions.value = true

                            }

                        ),

                    shape = RoundedCornerShape(8.dp),

                    color = MaterialTheme.colorScheme.surfaceVariant

                ) {

                    if (coverBitmap != null) Image(

                        bitmap = coverBitmap.asImageBitmap(),

                        contentDescription = gameModel.titleName,

                        contentScale = ContentScale.Crop,

                        modifier = Modifier.fillMaxSize()

                    ) else Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {

                        Text("NEO", color = MaterialTheme.colorScheme.onSurfaceVariant, fontWeight = FontWeight.Bold)

                    }

                }

                Text(

                    gameModel.titleName ?: "Jogo",

                    color = MaterialTheme.colorScheme.onBackground,

                    fontWeight = FontWeight.SemiBold,

                    fontSize = 13.sp,

                    maxLines = 1,

                    overflow = TextOverflow.Ellipsis,

                    modifier = Modifier.padding(top = 10.dp).fillMaxWidth(),

                    textAlign = TextAlign.Center

                )

            }

        }



        @Composable

        fun GridGameItem(

            gameModel: GameModel,

            viewModel: HomeViewModel,

            showAppActions: MutableState<Boolean>,

            showLoading: MutableState<Boolean>,

            selectedModel: MutableState<GameModel?>,

            showError: MutableState<String>

        ) {

            val context = LocalContext.current

            val isSelected = selectedModel.value == gameModel

            val interactionSource = remember { MutableInteractionSource() }

            val isPressed by interactionSource.collectIsPressedAsState()

            var isFocused by remember { mutableStateOf(false) }

            val emphasis = isPressed || isFocused || isSelected

            val scale by animateFloatAsState(

                targetValue = if (isPressed) 0.965f else if (emphasis) 1.035f else 1f,

                animationSpec = tween(140),

                label = "cartridgeScale"

            )

            val elevation by animateFloatAsState(

                targetValue = if (emphasis) 13f else 2f,

                animationSpec = tween(160),

                label = "cartridgeElevation"

            )



            // Load game cover bitmap (from game_icons file or base64 icon)

            val coverBitmap = remember(gameModel.titleId) {

                try {

                    val iconFile = java.io.File(context.filesDir, "game_icons/${gameModel.titleId}.jpg")

                    if (iconFile.exists()) {

                        BitmapFactory.decodeFile(iconFile.absolutePath)

                    } else if (gameModel.icon?.isNotEmpty() == true) {

                        val pic = Base64.getDecoder().decode(gameModel.icon)

                        BitmapFactory.decodeByteArray(pic, 0, pic.size)

                    } else null

                } catch (_: Throwable) {

                    null

                }

            }



            BoxWithConstraints(

                modifier = Modifier

                    .fillMaxWidth()

                    .aspectRatio(214f / 308f)

                    .graphicsLayer {

                        scaleX = scale

                        scaleY = scale

                    }

                    .shadow(elevation.dp, RoundedCornerShape(10.dp))

                    .clip(RoundedCornerShape(8.dp))

                    .border(

                        width = if (emphasis) 2.dp else 0.dp,

                        color = if (emphasis) Color(0xFF22252B) else Color.Transparent,

                        shape = RoundedCornerShape(8.dp)

                    )

                    .onFocusChanged { isFocused = it.isFocused }

                    .focusable()

                    .combinedClickable(

                        interactionSource = interactionSource,

                        indication = null,

                        onClick = {

                            if (viewModel.mainViewModel?.selected != null) {

                                showAppActions.value = false

                                viewModel.mainViewModel.apply { selected = null }

                                selectedModel.value = null

                            } else if (gameModel.titleId.isNullOrEmpty()

                                || gameModel.titleId != "0000000000000000"

                                || gameModel.type == FileType.Nro

                            ) {

                                thread {

                                    showLoading.value = true

                                    val success = viewModel.mainViewModel?.loadGame(gameModel) ?: 0

                                    if (success == 1) {

                                        launchOnUiThread { viewModel.mainViewModel?.navigateToGame() }

                                    } else {

                                        if (success == -2)

                                            showError.value = "Erro ao carregar atualização. Adicione o arquivo de atualização novamente."

                                        gameModel.close()

                                    }

                                    showLoading.value = false

                                }

                            }

                        },

                        onLongClick = {

                            viewModel.mainViewModel?.selected = gameModel

                            showAppActions.value = true

                            selectedModel.value = gameModel

                        }

                    )

            ) {

                val w = maxWidth

                val h = maxHeight



                // Layer 1: Game artwork strictly aligned with the cartridge cutout (18, 83) to (197, 240) in 214x308

                Box(

                    modifier = Modifier

                        .offset(

                            x = w * (18f / 214f),

                            y = h * (83f / 308f)

                        )

                        .size(

                            width = w * (179f / 214f),

                            height = h * (157f / 308f)

                        )

                        .background(Color(0xFF1B1B1D)),

                    contentAlignment = Alignment.Center

                ) {

                    if (coverBitmap != null) {

                        Image(

                            bitmap = coverBitmap.asImageBitmap(),

                            contentDescription = gameModel.titleName,

                            contentScale = ContentScale.Crop,

                            modifier = Modifier.fillMaxSize()

                        )

                    } else if (gameModel.type == FileType.Nro) {

                        Image(

                            painter = painterResource(id = R.drawable.icon_nro),

                            contentDescription = "NRO",

                            contentScale = ContentScale.Fit,

                            modifier = Modifier.size(48.dp)

                        )

                    } else {

                        Text(

                            text = gameModel.titleName ?: "Jogo",

                            color = Color.White,

                            fontSize = 10.sp,

                            fontWeight = FontWeight.Bold,

                            textAlign = TextAlign.Center,

                            maxLines = 3,

                            overflow = TextOverflow.Ellipsis,

                            modifier = Modifier.padding(4.dp)

                        )

                    }

                }



                // Layer 2: Nintendo Switch Cartridge Mockup frame from /imagens (transparent cutout allows artwork to show)

                Image(

                    painter = painterResource(R.drawable.mockup_cartucho_switch),

                    contentDescription = gameModel.titleName,

                    contentScale = ContentScale.FillBounds,

                    modifier = Modifier.fillMaxSize()

                )



                // The selected/focused cartridge keeps a visible inner highlight.

                if (emphasis) {

                    Box(

                        modifier = Modifier

                            .fillMaxSize()

                            .border(2.dp, Color.White.copy(alpha = 0.88f), RoundedCornerShape(8.dp))

                    )

                }

            }

        }



        @Composable

        fun NotAvailableIcon() {

            val size = ListImageSize / Resources.getSystem().displayMetrics.density

            Icon(

                Icons.Filled.Add,

                contentDescription = "N/A",

                modifier = Modifier

                    .padding(end = 8.dp)

                    .width(size.roundToInt().dp)

                    .height(size.roundToInt().dp)

            )

        }



        @Composable

        fun NROIcon() {

            val size = ListImageSize / Resources.getSystem().displayMetrics.density

            Image(

                painter = painterResource(id = R.drawable.icon_nro),

                contentDescription = "NRO",

                modifier = Modifier

                    .padding(end = 8.dp)

                    .width(size.roundToInt().dp)

                    .height(size.roundToInt().dp)

            )

        }

    }



    @Preview

    @Composable

    fun HomePreview() {

        Home(isPreview = true)

    }

}

