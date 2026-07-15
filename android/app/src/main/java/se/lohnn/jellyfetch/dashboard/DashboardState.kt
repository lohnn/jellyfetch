package se.lohnn.jellyfetch.dashboard

import se.lohnn.jellyfetch.api.Job

/**
 * The dashboard's explicit UI state — a pure data model with NO android.*
 * dependency, so it (and the reducer logic in [DashboardViewModel]) is
 * exercisable in plain JVM unit tests (I-127: keep state-holder logic
 * android-free; any android.* singleton must be lazy-init, never touched at
 * class-load).
 *
 * The four content phases the spec calls for are modeled as distinct types so a
 * `when` over [content] is exhaustive and the composable cannot forget a state:
 *  - [Content.Loading]   first poll in flight, nothing shown yet
 *  - [Content.Empty]     server reachable, zero jobs
 *  - [Content.Error]     poll failed AND we have nothing to show
 *  - [Content.Jobs]      one or more jobs (the populated dashboard)
 *
 * [notConfigured] and [refreshing] are orthogonal banners/affordances that can
 * ride on top of any content phase (e.g. an unreachable error while a stale job
 * list is still visible → [Content.Jobs] + [transientError]).
 */
data class DashboardState(
    val content: Content,
    /** True when no server URL/api key is set — drives the "open Settings" banner. */
    val notConfigured: Boolean = false,
    /** True while a pull-to-refresh / poll is in flight over existing content. */
    val refreshing: Boolean = false,
    /**
     * A poll failed but we still have a populated list to show — surface the
     * error as a non-destructive banner rather than blanking the working view
     * (mirrors the classic MainActivity.renderUnreachable behavior).
     */
    val transientError: String? = null,
) {
    sealed interface Content {
        data object Loading : Content
        data object Empty : Content
        data class Error(val message: String) : Content
        data class Jobs(val jobs: List<Job>) : Content
    }

    companion object {
        val INITIAL = DashboardState(content = Content.Loading)
    }
}
