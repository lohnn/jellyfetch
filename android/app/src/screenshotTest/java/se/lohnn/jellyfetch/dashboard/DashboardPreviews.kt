package se.lohnn.jellyfetch.dashboard

import androidx.compose.material3.SnackbarHostState
import androidx.compose.runtime.Composable
import androidx.compose.ui.tooling.preview.Preview
import com.android.tools.screenshot.PreviewTest
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.api.JobCategory
import se.lohnn.jellyfetch.api.JobState
import se.lohnn.jellyfetch.ui.theme.JellyFetchTheme

/**
 * Screenshot-test previews for the download dashboard (PASS 1 visual oracle).
 *
 * Each of the four explicit content states (loading / empty / error / populated)
 * is rendered in BOTH light and dark, plus a "not configured" and a "stale list
 * with a transient error banner" variant. The screenshot plugin (alpha15) renders
 * every @PreviewTest composable here to a reference PNG on the JVM (no emulator);
 * `debugUpdateScreenshotTest` writes them, `debugValidateScreenshotTest` diffs.
 *
 * These render the STATELESS [DashboardScreen] with hand-built [DashboardState]
 * values and no-op callbacks — no ViewModel, no ApiClient, no Android singletons —
 * so the render is deterministic.
 */
private val sampleDownloading = Job(
    id = "j-dl",
    title = "Sinners (2025)",
    state = JobState.DOWNLOADING,
    progressPercent = 42,
    speedBytesPerSec = 3_500_000,
    etaSeconds = 185,
    category = JobCategory.MOVIE,
)

private val sampleQueued = Job(
    id = "j-q",
    title = "Some very long series title that should ellipsize when it overflows the row width",
    state = JobState.QUEUED,
    statusText = "fetching metadata",
    category = JobCategory.SERIES,
)

private val sampleGroupDownloading = Job(
    id = "j-grp",
    title = "Skavlan (whole season)",
    state = JobState.DOWNLOADING,
    progressPercent = 61,
    speedBytesPerSec = 8_200_000,
    etaSeconds = 640,
    isGroup = true,
    childCount = 8,
    statusText = "3/8 episodes finished",
    category = JobCategory.SERIES,
)

private val sampleCompleted = Job(
    id = "j-done",
    title = "The Matrix (1999)",
    state = JobState.COMPLETED,
    progressPercent = 100,
    category = JobCategory.MOVIE,
)

private val sampleFailed = Job(
    id = "j-fail",
    title = "magnet:?xt=urn:btih:… (broken)",
    state = JobState.FAILED,
    errorMessage = "No peers found after 5m — the torrent may be dead.",
)

private val sampleCancelled = Job(
    id = "j-cancel",
    title = "Cancelled download",
    state = JobState.CANCELLED,
)

private val populatedJobs = listOf(
    sampleDownloading,
    sampleGroupDownloading,
    sampleQueued,
    sampleCompleted,
    sampleFailed,
    sampleCancelled,
)

@Composable
private fun Preview(state: DashboardState, dark: Boolean) {
    JellyFetchTheme(darkTheme = dark) {
        DashboardScreen(
            state = state,
            snackbarHostState = SnackbarHostState(),
            onRefresh = {},
            onOpenSettings = {},
            onOpenAllItems = {},
            onOpenJob = {},
            onCancel = {},
            onRetry = {},
            onRemove = {},
        )
    }
}

// --- Populated (the main dashboard) ---
@PreviewTest
@Preview(name = "Jobs · light", widthDp = 400, heightDp = 780)
@Composable
fun DashboardJobsLight() =
    Preview(DashboardState(DashboardState.Content.Jobs(populatedJobs)), dark = false)

@PreviewTest
@Preview(name = "Jobs · dark", widthDp = 400, heightDp = 780)
@Composable
fun DashboardJobsDark() =
    Preview(DashboardState(DashboardState.Content.Jobs(populatedJobs)), dark = true)

// --- Loading ---
@PreviewTest
@Preview(name = "Loading · light", widthDp = 400, heightDp = 300)
@Composable
fun DashboardLoadingLight() =
    Preview(DashboardState(DashboardState.Content.Loading), dark = false)

@PreviewTest
@Preview(name = "Loading · dark", widthDp = 400, heightDp = 300)
@Composable
fun DashboardLoadingDark() =
    Preview(DashboardState(DashboardState.Content.Loading), dark = true)

// --- Empty ---
@PreviewTest
@Preview(name = "Empty · light", widthDp = 400, heightDp = 300)
@Composable
fun DashboardEmptyLight() =
    Preview(DashboardState(DashboardState.Content.Empty), dark = false)

@PreviewTest
@Preview(name = "Empty · dark", widthDp = 400, heightDp = 300)
@Composable
fun DashboardEmptyDark() =
    Preview(DashboardState(DashboardState.Content.Empty), dark = true)

// --- Error (nothing to show) ---
@PreviewTest
@Preview(name = "Error · light", widthDp = 400, heightDp = 300)
@Composable
fun DashboardErrorLight() =
    Preview(DashboardState(DashboardState.Content.Error("Connection refused")), dark = false)

@PreviewTest
@Preview(name = "Error · dark", widthDp = 400, heightDp = 300)
@Composable
fun DashboardErrorDark() =
    Preview(DashboardState(DashboardState.Content.Error("Connection refused")), dark = true)

// --- Not configured banner over empty ---
@PreviewTest
@Preview(name = "Not configured · light", widthDp = 400, heightDp = 300)
@Composable
fun DashboardNotConfiguredLight() =
    Preview(
        DashboardState(DashboardState.Content.Empty, notConfigured = true),
        dark = false,
    )

// --- Stale list + transient error banner (non-destructive) ---
@PreviewTest
@Preview(name = "Stale + error banner · dark", widthDp = 400, heightDp = 500)
@Composable
fun DashboardStaleWithErrorDark() =
    Preview(
        DashboardState(
            DashboardState.Content.Jobs(listOf(sampleDownloading, sampleCompleted)),
            transientError = "Timed out after 10s",
        ),
        dark = true,
    )
