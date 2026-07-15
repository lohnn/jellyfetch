package se.lohnn.jellyfetch.detail

import androidx.compose.runtime.Composable
import androidx.compose.ui.tooling.preview.Preview
import com.android.tools.screenshot.PreviewTest
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.api.JobCategory
import se.lohnn.jellyfetch.api.JobState
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemType
import se.lohnn.jellyfetch.ui.theme.JellyFetchTheme

/**
 * Screenshot-test previews for the job-detail screen. Renders the stateless
 * [JobDetailScreen] over the meaningful [JobDetailState] shapes: a downloading
 * job, a failed job with a full copyable error, and a completed job with a
 * resolved Jellyfin-match metadata card (the "Fix metadata" entry point).
 */
private val downloadingJob = Job(
    id = "j1", title = "Sinners (2025)", state = JobState.DOWNLOADING,
    progressPercent = 42, speedBytesPerSec = 3_500_000, etaSeconds = 185,
    category = JobCategory.MOVIE, sourceUrl = "https://www.svtplay.se/video/abc",
    createdAt = "2026-07-15T10:00:00+00:00",
)

private val failedJob = Job(
    id = "j2", title = "Broken torrent", state = JobState.FAILED,
    errorMessage = "No peers found after 5m — the torrent may be dead. Try:\n  sudo chown -R jellyfin /media/downloads",
    createdAt = "2026-07-15T10:00:00+00:00",
)

private val completedJob = Job(
    id = "j3", title = "The Matrix (1999)", state = JobState.COMPLETED,
    progressPercent = 100, category = JobCategory.MOVIE,
    finalPaths = listOf("/media/movies/The Matrix (1999)/The Matrix (1999).mkv"),
    completedAt = "2026-07-15T11:00:00+00:00",
)

private val sampleMatched = LibraryItem(
    id = "42", name = "The Matrix", year = 1999, type = LibraryItemType.MOVIE,
    providerIds = mapOf("Tmdb" to "603", "Imdb" to "tt0133093"),
)

@Composable
private fun PreviewDetail(state: JobDetailState, dark: Boolean) {
    JellyFetchTheme(darkTheme = dark) {
        JobDetailScreen(state = state, onBack = {}, onCopyError = {}, onFixMetadata = {})
    }
}

@PreviewTest
@Preview(name = "Detail · downloading · light", widthDp = 400, heightDp = 700)
@Composable
fun DetailDownloadingLight() = PreviewDetail(JobDetailState(downloadingJob), dark = false)

@PreviewTest
@Preview(name = "Detail · failed + error · dark", widthDp = 400, heightDp = 700)
@Composable
fun DetailFailedDark() = PreviewDetail(JobDetailState(failedJob), dark = true)

@PreviewTest
@Preview(name = "Detail · completed + metadata · light", widthDp = 400, heightDp = 700)
@Composable
fun DetailCompletedMetadataLight() =
    PreviewDetail(JobDetailState(completedJob, metadata = MetadataState.Loaded(sampleMatched)), dark = false)

@PreviewTest
@Preview(name = "Detail · completed + metadata · dark", widthDp = 400, heightDp = 700)
@Composable
fun DetailCompletedMetadataDark() =
    PreviewDetail(JobDetailState(completedJob, metadata = MetadataState.Loaded(sampleMatched)), dark = true)
