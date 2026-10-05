package org.kenjinx.android.ui.theme

import android.app.Activity
import android.content.Context
import android.os.Build
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.unit.dp
import androidx.core.view.WindowCompat

object NeoThemeState {
    private const val PreferencesName = "neo_interface"
    private const val DarkModeKey = "darkMode"

    var isDark by mutableStateOf(false)
        private set

    fun initialize(context: Context) {
        isDark = context.getSharedPreferences(PreferencesName, Context.MODE_PRIVATE)
            .getBoolean(DarkModeKey, false)
    }

    fun toggle(context: Context) {
        isDark = !isDark
        context.getSharedPreferences(PreferencesName, Context.MODE_PRIVATE)
            .edit()
            .putBoolean(DarkModeKey, isDark)
            .apply()
    }
}

private val DarkColorScheme = darkColorScheme(
    primary = Gray90,
    secondary = NeoAccent,
    tertiary = Gray60,
    background = Gray05,
    surface = Gray10,
    surfaceVariant = Gray20,
    primaryContainer = Gray30,
    secondaryContainer = Gray20,
    onPrimary = Gray10,
    onSecondary = Gray90,
    onTertiary = Gray10,
    onBackground = Gray90,
    onSurface = Gray90,
    onSurfaceVariant = Gray80,
    outline = Gray40,
    error = NeoDanger
)

private val LightColorScheme = lightColorScheme(
    primary = Color(0xFF20242B),
    secondary = Color(0xFF59616D),
    tertiary = Color(0xFF747B86),
    background = Color(0xFFF3F4F6),
    surface = Color(0xFFFFFFFF),
    surfaceVariant = Color(0xFFEAECF0),
    primaryContainer = Color(0xFFD8DCE3),
    secondaryContainer = Color(0xFFE3E6EB),
    onPrimary = Color.White,
    onPrimaryContainer = Color(0xFF171A20),
    onSecondary = Color.White,
    onSecondaryContainer = Color(0xFF252930),
    onTertiary = Color.White,
    onBackground = Color(0xFF17191D),
    onSurface = Color(0xFF17191D),
    onSurfaceVariant = Color(0xFF555B65),
    outline = Color(0xFFC5C9D0),
    error = Color(0xFFC5333E)
)

@Suppress("DEPRECATION")
@Composable
fun KenjinxAndroidTheme(
    darkTheme: Boolean = false,
    // Dynamic color is available on Android 12+
    dynamicColor: Boolean = false,  // Changed from true to false to use our custom colors
    content: @Composable () -> Unit
) {
    val colorScheme = when {
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> {
            val context = LocalContext.current
            if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        }

        darkTheme -> DarkColorScheme
        else -> LightColorScheme
    }
    val shapes = Shapes(
        extraSmall = RoundedCornerShape(4.dp),
        small = RoundedCornerShape(8.dp),
        medium = RoundedCornerShape(12.dp),
        large = RoundedCornerShape(16.dp),
        extraLarge = RoundedCornerShape(24.dp)
    )
    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = (view.context as Activity).window
            window.statusBarColor = colorScheme.background.toArgb()
            window.navigationBarColor = colorScheme.background.toArgb()
            WindowCompat.getInsetsController(window, view).isAppearanceLightStatusBars = !darkTheme
            WindowCompat.getInsetsController(window, view).isAppearanceLightNavigationBars = !darkTheme
        }
    }

    MaterialTheme(
        colorScheme = colorScheme,
        typography = Typography,
        content = content,
        shapes = shapes
    )
}
