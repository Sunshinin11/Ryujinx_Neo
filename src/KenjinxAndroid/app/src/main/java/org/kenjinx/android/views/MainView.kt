package org.kenjinx.android.views

import androidx.compose.runtime.Composable
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import androidx.navigation.NavType
import androidx.navigation.navArgument
import org.kenjinx.android.viewmodels.MainViewModel
import org.kenjinx.android.viewmodels.SettingsViewModel

class MainView {
    companion object {
        @Composable
        fun Main(mainViewModel: MainViewModel) {
            val navController = rememberNavController()
            mainViewModel.navController = navController

            NavHost(navController = navController, startDestination = "home") {
                composable("home") { HomeViews.Home(mainViewModel.homeViewModel, navController) }
                composable("user") { UserViews.Main(mainViewModel) }
                composable("game") { GameViews.Main() }
                // NEOV2-UI1: configuracoes do jogo em pagina propria (antes era popup).
                composable("gameSettings") {
                    val game = mainViewModel.selected
                    if (game != null) {
                        GameSettingsPage(
                            activity = mainViewModel.activity,
                            titleId = game.titleId.orEmpty(),
                            titleName = game.titleName.orEmpty(),
                            iconBase64 = game.icon,
                            onBack = { navController.popBackStack() }
                        )
                    } else {
                        navController.popBackStack()
                    }
                }
                composable(
                    route = "settings?category={category}",
                    arguments = listOf(navArgument("category") {
                        type = NavType.StringType
                        nullable = true
                        defaultValue = null
                    })
                ) { entry ->
                    SettingViews.Main(
                        SettingsViewModel(mainViewModel.activity),
                        mainViewModel,
                        entry.arguments?.getString("category")
                    )
                }
            }
        }
    }
}
