package se.lohnn.jellyfetch

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.runtime.LaunchedEffect
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import se.lohnn.jellyfetch.dashboard.DashboardRoute
import se.lohnn.jellyfetch.dashboard.DashboardViewModel
import se.lohnn.jellyfetch.ui.theme.JellyFetchTheme

/**
 * Download dashboard host (PASS 1 — migrated from classic Views to Compose).
 *
 * The screen itself is [DashboardRoute]; this Activity only owns the Compose
 * entry point + the foreground polling loop. Polling replaces the old
 * Handler.postDelayed with a lifecycle-scoped coroutine that ticks
 * [DashboardViewModel.refresh] every [POLL_INTERVAL_MS] while STARTED and stops
 * automatically when backgrounded (repeatOnLifecycle) — same "poll while
 * foregrounded, no push/websocket" contract as before, no ceremony.
 *
 * Settings / All-items / Job-detail are now ALSO Compose activities (Pass 2) —
 * the callbacks below launch them by Intent as before; each hosts its own
 * Compose content + ViewModel.
 */
class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            JellyFetchTheme {
                val vm: DashboardViewModel = viewModel(factory = DashboardViewModel.Factory)
                val lifecycleOwner = LocalLifecycleOwner.current

                // Foreground polling: refresh immediately on entering STARTED, then
                // every POLL_INTERVAL_MS until the lifecycle drops below STARTED.
                LaunchedEffect(lifecycleOwner, vm) {
                    lifecycleOwner.repeatOnLifecycle(Lifecycle.State.STARTED) {
                        while (isActive) {
                            vm.refresh()
                            delay(POLL_INTERVAL_MS)
                        }
                    }
                }

                DashboardRoute(
                    vm = vm,
                    onOpenSettings = {
                        startActivity(Intent(this, SettingsActivity::class.java))
                    },
                    onOpenAllItems = {
                        startActivity(Intent(this, AllItemsActivity::class.java))
                    },
                    onOpenJob = { job -> JobDetailActivity.start(this, job) },
                )
            }
        }
    }

    companion object {
        private const val POLL_INTERVAL_MS = 3000L
    }
}
