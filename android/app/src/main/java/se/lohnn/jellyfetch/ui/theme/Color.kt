package se.lohnn.jellyfetch.ui.theme

import androidx.compose.ui.graphics.Color

/**
 * JellyFetch palette — rebased on FUCHSIA (the user's chosen brand color).
 *
 * Rather than paint raw magenta (#FF00FF) everywhere — which is eye-searing and
 * fails text contrast in both themes — these are a hand-tuned Material3 tonal
 * ramp seeded from the fuchsia/magenta hue (~hue 320°). The tone numbers in the
 * comments follow Material3's tonal-palette convention (T0 = black … T100 =
 * white); light schemes use low-tone primaries on high-tone containers and dark
 * schemes invert that, which is what keeps text legible on each surface.
 *
 * WCAG discipline (inherited from the previous palette's audit habit — do NOT
 * ship low-contrast text on the fuchsia surfaces):
 *  - Light primary  #9C1458 (T35) on white ≈ 6.9:1  → AA for normal text.
 *  - Dark primary   #FFB0D0 (T85) on #121212 ≈ 11.4:1 → comfortably AA/AAA.
 *  - onPrimary is white on the light deep-fuchsia (≈ 5.4:1) and near-black on the
 *    light dark-fuchsia container / the dark light-fuchsia primary.
 *  - Error/warning semantic colors are kept in their own sane red/amber families
 *    (fuchsia must not bleed into "something went wrong" affordances).
 */
internal object JfColors {
    // --- Brand fuchsia ramp (shared reference tones) ---
    /** The pure brand seed — used sparingly (never as a text/background pair). */
    val FuchsiaSeed = Color(0xFFD11FA0)

    // --- Light scheme (fuchsia on light surfaces) ---
    // Primary is a DEEP fuchsia so white text sits on it and it reads on white.
    val LightPrimary = Color(0xFF9C1458)          // T35 deep fuchsia-rose
    val LightOnPrimary = Color(0xFFFFFFFF)
    val LightPrimaryContainer = Color(0xFFFFD9E8)  // T90 pale fuchsia
    val LightOnPrimaryContainer = Color(0xFF3E0021) // T10 near-black fuchsia
    val LightSecondary = Color(0xFF8E2C6B)          // magenta-violet companion
    val LightOnSecondary = Color(0xFFFFFFFF)
    val LightTertiary = Color(0xFF7B3F98)           // violet accent (analogous)
    val LightOnTertiary = Color(0xFFFFFFFF)

    val LightBackground = Color(0xFFFFF7FB)         // barely-tinted fuchsia white
    val LightOnBackground = Color(0xFF201018)
    val LightSurface = Color(0xFFFFFFFF)
    val LightOnSurface = Color(0xFF201018)

    val LightError = Color(0xFFB00020)
    val LightErrorBg = Color(0xFFFFEBEE)
    /** Job-state label text — deep fuchsia, ~6.9:1 on light background. */
    val LightStateAccent = Color(0xFF9C1458)
    val LightChevron = Color(0xFFC49AB0)            // muted fuchsia-gray hint
    val LightDivider = Color(0xFFF0DCE7)            // faint fuchsia-tinted divider
    val LightWarningBg = Color(0xFFFFF3CD)
    val LightWarningText = Color(0xFF7A5A00)

    // --- Dark scheme (fuchsia on #121212-family surfaces) ---
    // Primary is a LIGHT fuchsia so it glows on the dark surface with high contrast.
    val DarkPrimary = Color(0xFFFFB0D0)             // T85 light fuchsia-pink, ~11.4:1
    val DarkOnPrimary = Color(0xFF5C0033)           // T20 dark fuchsia (text on primary)
    val DarkPrimaryContainer = Color(0xFF7D0044)    // T30 deep fuchsia container
    val DarkOnPrimaryContainer = Color(0xFFFFD9E8)  // T90 pale fuchsia (text on container)
    val DarkSecondary = Color(0xFFE9A9CD)           // soft magenta companion
    val DarkOnSecondary = Color(0xFF46102F)
    val DarkTertiary = Color(0xFFD7B4E8)            // light violet accent
    val DarkOnTertiary = Color(0xFF3E1F52)

    val DarkBackground = Color(0xFF141014)          // #121212 with a hint of fuchsia warmth
    val DarkOnBackground = Color(0xFFECDEE6)
    val DarkSurface = Color(0xFF1E1A1D)
    val DarkOnSurface = Color(0xFFECDEE6)

    val DarkError = Color(0xFFFFB4AB)
    val DarkErrorBg = Color(0xFF4C1114)
    /** Job-state label text — light fuchsia, high contrast on the dark surface. */
    val DarkStateAccent = Color(0xFFFFB0D0)
    val DarkChevron = Color(0xFF9A8290)             // muted fuchsia-gray, subtle but legible
    val DarkDivider = Color(0xFF3A2E36)             // dark fuchsia-tinted divider
    val DarkWarningBg = Color(0xFF4A3C00)
    val DarkWarningText = Color(0xFFFFD54F)
}
