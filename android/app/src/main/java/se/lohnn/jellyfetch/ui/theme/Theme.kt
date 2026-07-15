package se.lohnn.jellyfetch.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color

/**
 * JellyFetch-specific semantic colors that Material3's [androidx.compose.material3.ColorScheme]
 * has no slot for: the error/warning CALLOUT backgrounds, the "tap to expand"
 * chevron tint, list dividers, and the job-state accent text. Ported from the
 * classic-Views colors.xml so the two implementations stay visually identical
 * during the phased migration.
 *
 * Exposed via [LocalJfColors] so any composable can read the correct light/dark
 * value without threading it through every call.
 */
@Immutable
data class JfExtendedColors(
    val warningBg: Color,
    val warningText: Color,
    val errorCalloutBg: Color,
    val errorCalloutText: Color,
    val chevron: Color,
    val divider: Color,
    val stateAccent: Color,
)

private val LightExtended = JfExtendedColors(
    warningBg = JfColors.LightWarningBg,
    warningText = JfColors.LightWarningText,
    errorCalloutBg = JfColors.LightErrorBg,
    errorCalloutText = JfColors.LightError,
    chevron = JfColors.LightChevron,
    divider = JfColors.LightDivider,
    stateAccent = JfColors.LightStateAccent,
)

private val DarkExtended = JfExtendedColors(
    warningBg = JfColors.DarkWarningBg,
    warningText = JfColors.DarkWarningText,
    errorCalloutBg = JfColors.DarkErrorBg,
    errorCalloutText = JfColors.DarkError,
    chevron = JfColors.DarkChevron,
    divider = JfColors.DarkDivider,
    stateAccent = JfColors.DarkStateAccent,
)

val LocalJfColors = staticCompositionLocalOf { LightExtended }

private val LightScheme = lightColorScheme(
    primary = JfColors.LightPrimary,
    onPrimary = JfColors.LightOnPrimary,
    primaryContainer = JfColors.LightPrimaryContainer,
    onPrimaryContainer = JfColors.LightOnPrimaryContainer,
    secondary = JfColors.LightSecondary,
    onSecondary = JfColors.LightOnSecondary,
    tertiary = JfColors.LightTertiary,
    onTertiary = JfColors.LightOnTertiary,
    background = JfColors.LightBackground,
    onBackground = JfColors.LightOnBackground,
    surface = JfColors.LightSurface,
    onSurface = JfColors.LightOnSurface,
    error = JfColors.LightError,
)

private val DarkScheme = darkColorScheme(
    primary = JfColors.DarkPrimary,
    onPrimary = JfColors.DarkOnPrimary,
    primaryContainer = JfColors.DarkPrimaryContainer,
    onPrimaryContainer = JfColors.DarkOnPrimaryContainer,
    secondary = JfColors.DarkSecondary,
    onSecondary = JfColors.DarkOnSecondary,
    tertiary = JfColors.DarkTertiary,
    onTertiary = JfColors.DarkOnTertiary,
    background = JfColors.DarkBackground,
    onBackground = JfColors.DarkOnBackground,
    surface = JfColors.DarkSurface,
    onSurface = JfColors.DarkOnSurface,
    error = JfColors.DarkError,
)

/**
 * The app theme. Follows the SYSTEM dark-mode setting only (no in-app toggle),
 * matching the classic-Views behavior (values-night resource qualifiers). The
 * [darkTheme] param is overridable so @Preview functions can force each scheme
 * for the screenshot harness.
 */
@Composable
fun JellyFetchTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit,
) {
    val scheme = if (darkTheme) DarkScheme else LightScheme
    val extended = if (darkTheme) DarkExtended else LightExtended
    CompositionLocalProvider(LocalJfColors provides extended) {
        MaterialTheme(
            colorScheme = scheme,
            content = content,
        )
    }
}

/** Convenience accessor: `JfTheme.colors.chevron`, mirroring MaterialTheme.colorScheme. */
object JfTheme {
    val colors: JfExtendedColors
        @Composable
        @ReadOnlyComposable
        get() = LocalJfColors.current
}
