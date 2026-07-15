package se.lohnn.jellyfetch

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.correction.CorrectionHost
import se.lohnn.jellyfetch.detail.JobDetailScreen
import se.lohnn.jellyfetch.detail.JobDetailViewModel
import se.lohnn.jellyfetch.detail.copyErrorToClipboard
import se.lohnn.jellyfetch.ui.theme.JellyFetchTheme

/**
 * Job-detail host (PASS 2 — migrated from classic Views to Compose). Renders
 * [JobDetailScreen] from [JobDetailViewModel], polls the detail while foregrounded
 * (same 3s cadence as the dashboard), and hosts the shared correction picker when
 * "Fix metadata" is tapped — routing its onApplied back into the VM's rebind logic
 * (by-path fresh item, or re-fetch), preserving the W-065 stale-display fix.
 */
class JobDetailActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        @Suppress("DEPRECATION")
        val initialJob = intent.getSerializableExtra(EXTRA_JOB) as? Job
        if (initialJob == null) {
            finish()
            return
        }

        setContent {
            JellyFetchTheme {
                val vm: JobDetailViewModel = viewModel(factory = JobDetailViewModel.factory(initialJob))
                val state by vm.state.collectAsStateWithLifecycle()
                val context = LocalContext.current
                val lifecycleOwner = LocalLifecycleOwner.current

                var correcting by remember { mutableStateOf<LibraryItem?>(null) }

                LaunchedEffect(lifecycleOwner, vm) {
                    lifecycleOwner.repeatOnLifecycle(Lifecycle.State.STARTED) {
                        while (isActive) {
                            vm.refresh()
                            delay(POLL_INTERVAL_MS)
                        }
                    }
                }

                JobDetailScreen(
                    state = state,
                    onBack = { finish() },
                    onCopyError = { message -> copyErrorToClipboard(context, message) },
                    onFixMetadata = { item -> correcting = item },
                )

                correcting?.let { item ->
                    CorrectionHost(
                        item = item,
                        onDismiss = { correcting = null },
                        onApplied = { refreshed -> vm.onCorrectionApplied(refreshed) },
                    )
                }
            }
        }
    }

    companion object {
        const val EXTRA_JOB = "extra_job"
        const val EXTRA_JOB_ID = "extra_job_id"
        private const val POLL_INTERVAL_MS = 3000L

        fun start(activity: Activity, job: Job) {
            activity.startActivity(
                Intent(activity, JobDetailActivity::class.java).putExtra(EXTRA_JOB, job),
            )
        }
    }
}
