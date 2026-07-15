package se.lohnn.jellyfetch.detail

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewmodel.initializer
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.api.JellyFetchApi
import se.lohnn.jellyfetch.api.JobState
import se.lohnn.jellyfetch.api.LibraryItem

/** Metadata-match resolution state for a completed, non-group job. */
sealed interface MetadataState {
    data object Hidden : MetadataState
    data object Loading : MetadataState
    data object None : MetadataState
    data class Loaded(val item: LibraryItem) : MetadataState
    data class Failed(val message: String) : MetadataState
}

data class JobDetailState(
    val job: Job,
    /** A non-null banner shown when a detail refresh failed (stale content kept). */
    val loadError: String? = null,
    val metadata: MetadataState = MetadataState.Hidden,
)

/**
 * State-holder for the job-detail screen (PASS 2). Renders immediately from the
 * intent-supplied [Job], then refreshes via [JellyFetchApi.getJobDetail] to pick
 * up [Job.children] and any state change — mirroring the classic Activity's
 * two-phase render. Metadata match is resolved lazily (once) for a completed,
 * non-group job.
 *
 * android.view-free (I-127): the API is injected; the metadata one-shot guard is
 * a plain flag. Cancel/Retry/Remove feedback (W-056) is surfaced via [events].
 */
class JobDetailViewModel(
    initialJob: Job,
    private val apiProvider: () -> JellyFetchApi,
) : ViewModel() {

    private val api get() = apiProvider()
    private val jobId = initialJob.id

    private val _state = MutableStateFlow(JobDetailState(job = initialJob))
    val state: StateFlow<JobDetailState> = _state.asStateFlow()

    private val _events = MutableSharedFlow<String>(
        replay = 0, extraBufferCapacity = 8, onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    val events: SharedFlow<String> = _events.asSharedFlow()

    /** Guards the metadata fetch against the twice-called render (I-… classic guard). */
    private var metadataRequested = false

    fun refresh() {
        api.getJobDetail(jobId) { result ->
            result.onSuccess { job ->
                _state.update { it.copy(job = job, loadError = null) }
                maybeLoadMetadata(job)
            }.onFailure { error ->
                // Keep whatever's shown; surface the failure as a banner.
                _state.update { it.copy(loadError = error.message ?: error.toString()) }
            }
        }
    }

    private fun maybeLoadMetadata(job: Job) {
        val eligible = job.state == JobState.COMPLETED && !job.isGroup
        if (!eligible) {
            _state.update { it.copy(metadata = MetadataState.Hidden) }
            return
        }
        // Already have it, or a request is in flight — don't refetch.
        if (_state.value.metadata is MetadataState.Loaded) return
        if (metadataRequested) return
        loadMetadata()
    }

    fun loadMetadata() {
        metadataRequested = true
        _state.update { it.copy(metadata = MetadataState.Loading) }
        api.getJobLibraryItem(jobId) { result ->
            result.onSuccess { item ->
                _state.update {
                    it.copy(metadata = if (item == null) MetadataState.None else MetadataState.Loaded(item))
                }
            }.onFailure { error ->
                _state.update { it.copy(metadata = MetadataState.Failed(error.message ?: error.toString())) }
            }
        }
    }

    /**
     * Rebind after a correction/convert (mirrors JobDetailActivity.onApplied): a
     * non-null [refreshed] is the freshly-resolved item (by path) — bind it
     * directly (correct new type, deterministic, W-065). Null → re-resolve from
     * the server so we never show stale data.
     */
    fun onCorrectionApplied(refreshed: LibraryItem?) {
        metadataRequested = false
        if (refreshed != null) {
            metadataRequested = true
            _state.update { it.copy(metadata = MetadataState.Loaded(refreshed)) }
        } else {
            loadMetadata()
        }
    }

    companion object {
        fun factory(initialJob: Job) =
            androidx.lifecycle.viewmodel.viewModelFactory {
                initializer {
                    JobDetailViewModel(
                        initialJob = initialJob,
                        apiProvider = { se.lohnn.jellyfetch.api.ApiClient.current },
                    )
                }
            }
    }
}
