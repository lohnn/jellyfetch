package se.lohnn.jellyfetch

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.lifecycle.viewmodel.compose.viewModel
import se.lohnn.jellyfetch.settings.SettingsRoute
import se.lohnn.jellyfetch.settings.SettingsViewModel
import se.lohnn.jellyfetch.ui.theme.JellyFetchTheme

/**
 * Server URL + API key settings (PASS 2 — migrated from classic Views to
 * Compose). The screen is [SettingsRoute]; this Activity only owns the Compose
 * entry point. Persistence + the test-connection probe live in
 * [SettingsViewModel] (JVM-testable, I-127). onPause still saves so a
 * back-out-without-Save keeps the entered values, matching the old behavior.
 */
class SettingsActivity : ComponentActivity() {

    private var vm: SettingsViewModel? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            JellyFetchTheme {
                val settingsVm: SettingsViewModel = viewModel(factory = SettingsViewModel.Factory)
                vm = settingsVm
                SettingsRoute(vm = settingsVm, onBack = { finish() })
            }
        }
    }

    override fun onPause() {
        super.onPause()
        // Persist on leave (parity with the classic Activity's onPause save), so
        // values typed but not explicitly Saved aren't lost.
        vm?.save()
    }
}
