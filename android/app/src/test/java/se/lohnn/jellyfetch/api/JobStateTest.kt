package se.lohnn.jellyfetch.api

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins [JobState.isTerminal]/[isCancellable]/[isRetryable] for every state —
 * these three predicates now directly drive which of Cancel/Retry/Remove
 * buttons the dashboard shows per row (JobsAdapter), so a regression here is
 * a regression in a destructive-action gate, not just cosmetics. isTerminal
 * in particular must mirror the server's own gate (DownloadJobManager.Delete
 * requires job.IsTerminal; DownloadJob.cs: Completed/Failed/Cancelled) — if
 * they drift apart, Remove would show for a job the server then 409s on.
 */
class JobStateTest {

    @Test
    fun `active states are NOT terminal, ARE cancellable, are NOT retryable`() {
        for (state in listOf(JobState.QUEUED, JobState.RESOLVING, JobState.DOWNLOADING, JobState.PROCESSING)) {
            assertEquals("$state.isTerminal", false, state.isTerminal)
            assertEquals("$state.isCancellable", true, state.isCancellable)
            assertEquals("$state.isRetryable", false, state.isRetryable)
        }
    }

    @Test
    fun `COMPLETED is terminal, not cancellable, not retryable`() {
        assertEquals(true, JobState.COMPLETED.isTerminal)
        assertEquals(false, JobState.COMPLETED.isCancellable)
        assertEquals(false, JobState.COMPLETED.isRetryable)
    }

    @Test
    fun `FAILED is terminal, not cancellable, IS retryable`() {
        assertEquals(true, JobState.FAILED.isTerminal)
        assertEquals(false, JobState.FAILED.isCancellable)
        assertEquals(true, JobState.FAILED.isRetryable)
    }

    @Test
    fun `CANCELLED is terminal, not cancellable, IS retryable`() {
        assertEquals(true, JobState.CANCELLED.isTerminal)
        assertEquals(false, JobState.CANCELLED.isCancellable)
        assertEquals(true, JobState.CANCELLED.isRetryable)
    }

    @Test
    fun `every state is either terminal or cancellable, never neither nor both`() {
        // isCancellable is literally defined as !isTerminal today, but this
        // pins the INVARIANT (not the implementation): Remove and Cancel
        // gating must always be exact opposites, with no state where both
        // buttons or neither button would show.
        for (state in JobState.entries) {
            assertEquals("$state: isCancellable must be !isTerminal", !state.isTerminal, state.isCancellable)
        }
    }
}
