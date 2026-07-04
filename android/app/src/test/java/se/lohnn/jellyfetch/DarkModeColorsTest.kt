package se.lohnn.jellyfetch

import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.ConscryptMode

/**
 * Sanity check that the values-night resource qualifier is actually being
 * picked up — not a contrast/design check (that needs an eyeball on a real
 * device/emulator, which this sandbox doesn't have), just a guard against
 * the class of bug where a values-night file/color name is typo'd (e.g.
 * "value-night" instead of "values-night", or a mismatched color name) and
 * silently, invisibly falls back to the day color with no build error.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
@ConscryptMode(ConscryptMode.Mode.OFF) // see DashboardScrollTest kdoc: no linux-aarch64 Conscrypt build
class DarkModeColorsTest {

    @Test
    fun `day mode resolves the light background color`() {
        val context = ApplicationProvider.getApplicationContext<JellyFetchApp>()
        assertEquals(0xFFFAFAFA.toInt(), context.getColor(R.color.jf_background))
    }

    @Config(qualifiers = "night")
    @Test
    fun `night qualifier resolves the dark background override, not the day color`() {
        val context = ApplicationProvider.getApplicationContext<JellyFetchApp>()
        assertEquals(0xFF121212.toInt(), context.getColor(R.color.jf_background))
    }

    @Config(qualifiers = "night")
    @Test
    fun `night qualifier resolves the lifted-contrast state-accent color, not the brand purple verbatim`() {
        val context = ApplicationProvider.getApplicationContext<JellyFetchApp>()
        // #6A5ACD (brand purple, reused verbatim) only hits ~3.5:1 contrast
        // against the dark background — this pins that the lighter #B39DDB
        // dark-mode override is what actually resolves, not a silent
        // fallback to the light value.
        assertEquals(0xFFB39DDB.toInt(), context.getColor(R.color.jf_state_accent))
    }
}
