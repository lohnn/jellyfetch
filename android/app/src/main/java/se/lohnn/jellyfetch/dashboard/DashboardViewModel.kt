package se.lohnn.jellyfetch.dashboard

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.api.JellyFetchApi
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/**
 * State-holder for the download dashboard. Deliberately android.view-free and
 * driven by a plain [JellyFetchApi] handle + a [() -> Boolean] configured-check,
 * so the whole reducer is exercisable in JVM unit tests (I-127) — nothing here
 * touches an android.* singleton at construction; the [api]/[isConfigured]
 * providers are injected, and the real [ApiClient] lookup is deferred to the
 * factory in [DashboardViewModel.Companion].
 *
 * The API itself is still the tiny callback-based seam (I-082 — no coroutine/
 * retrofit dependency in the transport). We only use coroutines Flow here as the
 * UI-state observable surface Compose consumes; the API callbacks post results
 * into the flows.
 */
class DashboardViewModel(
    private val apiProvider: () -> JellyFetchApi,
    private val isConfigured: () -> Boolean,
) : ViewModel() {

    private val _state = MutableStateFlow(DashboardState.INITIAL)
    val state: StateFlow<DashboardState> = _state.asStateFlow()

    /**
     * One-shot user-facing messages (W-056 fix): a failed Cancel/Retry/Remove,
     * or a success confirmation, is emitted here and shown as a snackbar. Uses a
     * buffered SharedFlow so an event fired before the collector attaches (e.g.
     * during a config change) isn't lost.
     */
    private val _messages = MutableSharedFlow<String>(
        replay = 0,
        extraBufferCapacity = 8,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    val messages: SharedFlow<String> = _messages.asSharedFlow()

    /** Poll the job list. [userInitiated] flags a pull-to-refresh (shows the spinner). */
    fun refresh(userInitiated: Boolean = false) {
        if (userInitiated) {
            _state.update { it.copy(refreshing = true) }
        }
        val configured = isConfigured()
        apiProvider().listJobs { result ->
            result
                .onSuccess { jobs -> onJobs(jobs, configured) }
                .onFailure { error -> onPollFailure(error) }
        }
    }

    private fun onJobs(jobs: List<Job>, configured: Boolean) {
        val content = when {
            jobs.isEmpty() -> DashboardState.Content.Empty
            else -> DashboardState.Content.Jobs(jobs)
        }
        _state.update {
            it.copy(
                content = content,
                notConfigured = !configured,
                refreshing = false,
                transientError = null,
            )
        }
    }

    private fun onPollFailure(error: Throwable) {
        val message = error.message ?: error.toString()
        _state.update { current ->
            // If we already have a populated list, keep it and show the error as
            // a non-destructive banner — never blank a working view over one bad
            // poll (mirrors classic MainActivity.renderUnreachable). Only when we
            // have nothing to show does the error BECOME the content.
            val newContent = when (current.content) {
                is DashboardState.Content.Jobs -> current.content
                else -> DashboardState.Content.Error(message)
            }
            current.copy(
                content = newContent,
                refreshing = false,
                transientError = if (newContent is DashboardState.Content.Jobs) message else null,
            )
        }
    }

    // --- Job actions (W-056: ALL THREE surface success/failure + gate on state) ---
    //
    // The classic JobsAdapter/MainActivity gated the BUTTON VISIBILITY on job
    // state, but only Remove surfaced a failure toast; Cancel/Retry silently
    // discarded their Result. Here every action (a) re-checks the state guard
    // server-side-mirroring so a stale tap can't fire, and (b) emits a message on
    // BOTH success and failure so a server rejection is never invisible.

    fun cancel(job: Job) {
        if (!job.state.isCancellable) {
            emit(Msg.notCancellable(job))
            return
        }
        apiProvider().cancelJob(job.id) { result ->
            result
                .onSuccess { emit(Msg.cancelled(job)); refresh() }
                .onFailure { emit(Msg.cancelFailed(job, it)) }
        }
    }

    fun retry(job: Job) {
        if (!job.state.isRetryable) {
            emit(Msg.notRetryable(job))
            return
        }
        apiProvider().retryJob(job.id) { result ->
            result
                .onSuccess { emit(Msg.retried(job)); refresh() }
                .onFailure { emit(Msg.retryFailed(job, it)) }
        }
    }

    /**
     * Remove is HISTORY-removal only — it never deletes downloaded files (the
     * server's DELETE /Downloads/{id} just drops the job row). The server 409s on
     * a non-terminal job, so we gate on [job.state.isTerminal] to match.
     */
    fun remove(job: Job) {
        if (!job.state.isTerminal) {
            emit(Msg.notRemovable(job))
            return
        }
        apiProvider().removeJob(job.id) { result ->
            result
                .onSuccess { emit(Msg.removed(job)); refresh() }
                .onFailure { emit(Msg.removeFailed(job, it)) }
        }
    }

    private fun emit(message: String) {
        _messages.tryEmit(message)
    }

    /**
     * Human-readable action messages. Kept as a plain string table (no
     * android.content.Context) so the ViewModel stays JVM-testable; the UI layer
     * could localize these later, but honest feedback beats silent discard now.
     */
    private object Msg {
        fun cancelled(j: Job) = "Cancelled “${j.title}”."
        fun cancelFailed(j: Job, e: Throwable) = "Couldn't cancel “${j.title}”: ${reason(e)}"
        fun notCancellable(j: Job) = "“${j.title}” can't be cancelled — it already finished."
        fun retried(j: Job) = "Retrying “${j.title}”."
        fun retryFailed(j: Job, e: Throwable) = "Couldn't retry “${j.title}”: ${reason(e)}"
        fun notRetryable(j: Job) = "“${j.title}” can't be retried in its current state."
        fun removed(j: Job) = "Removed “${j.title}” from history."
        fun removeFailed(j: Job, e: Throwable) = "Couldn't remove “${j.title}”: ${reason(e)}"
        fun notRemovable(j: Job) = "“${j.title}” is still active — cancel it before removing."
        private fun reason(e: Throwable) = e.message ?: e.toString()
    }

    companion object {
        /**
         * Factory that binds the real [se.lohnn.jellyfetch.api.ApiClient] +
         * [se.lohnn.jellyfetch.Prefs] — but LAZILY (I-127): the providers are
         * `() -> ...` closures, so nothing touches the ApiClient singleton or
         * SharedPreferences until an action actually runs. This keeps the class
         * itself instantiable in a plain JVM test with fakes (see the primary
         * constructor), while production callers use `viewModel(factory = Factory)`.
         *
         * Imported inside the closure to keep this file's top-level imports
         * android-free (the constructor + reducer above compile against nothing
         * but the JellyFetchApi interface and coroutines Flow).
         */
        val Factory = viewModelFactory {
            initializer {
                val app = this[androidx.lifecycle.ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]!!
                DashboardViewModel(
                    apiProvider = { se.lohnn.jellyfetch.api.ApiClient.current },
                    isConfigured = { se.lohnn.jellyfetch.Prefs(app).isConfigured },
                )
            }
        }
    }
}
