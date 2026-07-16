package se.lohnn.jellyfetch.dashboard

import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import se.lohnn.jellyfetch.api.ConvertTarget
import se.lohnn.jellyfetch.api.ConvertTypeResult
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.api.JellyFetchApi
import se.lohnn.jellyfetch.api.JobState
import se.lohnn.jellyfetch.api.LibraryInfo
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemPage
import se.lohnn.jellyfetch.api.LibraryItemType
import se.lohnn.jellyfetch.api.RemoteSearchCandidate

/**
 * Pure-JVM unit tests for [DashboardViewModel] — no Robolectric, no Android
 * runtime (I-127: the state-holder is android-free by construction, and the
 * ApiClient/Prefs lookup is deferred behind () -> providers). Proves both the
 * loading/empty/error/populated reducer AND the W-056 fix: Cancel/Retry/Remove
 * surface success/failure and gate on job state.
 *
 * The fake API here invokes its callbacks SYNCHRONOUSLY on the calling thread
 * (the production Http/Fake impls post to the main thread, but for a reducer
 * test synchronous is exactly what we want — no scheduler needed).
 */
class DashboardViewModelTest {

    /** Minimal synchronous JellyFetchApi test double, scriptable per-method. */
    private class FakeApi(
        var jobsResult: Result<List<Job>> = Result.success(emptyList()),
        var cancelResult: Result<Unit> = Result.success(Unit),
        var retryResult: Result<Unit> = Result.success(Unit),
        var removeResult: Result<Unit> = Result.success(Unit),
    ) : JellyFetchApi {
        var listJobsCalls = 0
        override fun listJobs(callback: (Result<List<Job>>) -> Unit) {
            listJobsCalls++
            callback(jobsResult)
        }
        override fun cancelJob(id: String, callback: (Result<Unit>) -> Unit) = callback(cancelResult)
        override fun retryJob(id: String, callback: (Result<Unit>) -> Unit) = callback(retryResult)
        override fun removeJob(id: String, callback: (Result<Unit>) -> Unit) = callback(removeResult)

        // Unused by these tests — satisfy the interface with no-ops.
        override fun testConnection(callback: (Result<Unit>) -> Unit) = callback(Result.success(Unit))
        override fun listLibraries(callback: (Result<List<LibraryInfo>>) -> Unit) = callback(Result.success(emptyList()))
        override fun submitUrl(url: String, libraryId: String?, callback: (Result<String>) -> Unit) = callback(Result.success("x"))
        override fun submitTorrent(fileName: String, bytes: ByteArray, libraryId: String?, callback: (Result<String>) -> Unit) = callback(Result.success("x"))
        override fun getJobDetail(id: String, callback: (Result<Job>) -> Unit) = Unit
        override fun getJobLibraryItem(jobId: String, callback: (Result<LibraryItem?>) -> Unit) = Unit
        override fun getItemByPath(path: String, callback: (Result<LibraryItem?>) -> Unit) = Unit
        override fun listLibraryItems(query: String?, type: LibraryItemType?, startIndex: Int, limit: Int, callback: (Result<LibraryItemPage>) -> Unit) = Unit
        override fun searchRemoteMetadata(itemId: String, searchType: LibraryItemType, name: String, year: Int?, callback: (Result<List<RemoteSearchCandidate>>) -> Unit) = Unit
        override fun applyCorrectionByResult(itemId: String, candidate: RemoteSearchCandidate, callback: (Result<Unit>) -> Unit) = Unit
        override fun applyCorrectionByProvider(itemId: String, searchType: LibraryItemType, provider: String, providerId: String, callback: (Result<Unit>) -> Unit) = Unit
        override fun convertType(itemId: String, target: ConvertTarget, callback: (Result<ConvertTypeResult>) -> Unit) = Unit
    }

    private fun vm(api: FakeApi, configured: Boolean = true) =
        DashboardViewModel(apiProvider = { api }, isConfigured = { configured })

    private fun job(id: String, state: JobState) = Job(id = id, title = "Job $id", state = state)

    // --- Reducer: content states ---

    @Test
    fun `empty job list yields Empty content`() {
        val vm = vm(FakeApi(jobsResult = Result.success(emptyList())))
        vm.refresh()
        assertEquals(DashboardState.Content.Empty, vm.state.value.content)
        assertEquals(false, vm.state.value.refreshing)
    }

    @Test
    fun `populated list yields Jobs content`() {
        val jobs = listOf(job("1", JobState.DOWNLOADING))
        val vm = vm(FakeApi(jobsResult = Result.success(jobs)))
        vm.refresh()
        val content = vm.state.value.content
        assertTrue(content is DashboardState.Content.Jobs)
        assertEquals(jobs, (content as DashboardState.Content.Jobs).jobs)
    }

    @Test
    fun `poll failure with no prior list becomes Error content`() {
        val vm = vm(FakeApi(jobsResult = Result.failure(RuntimeException("refused"))))
        vm.refresh()
        val content = vm.state.value.content
        assertTrue(content is DashboardState.Content.Error)
        assertEquals("refused", (content as DashboardState.Content.Error).message)
    }

    @Test
    fun `poll failure with an existing list keeps list and shows transient error`() {
        val api = FakeApi(jobsResult = Result.success(listOf(job("1", JobState.COMPLETED))))
        val vm = vm(api)
        vm.refresh() // populate
        api.jobsResult = Result.failure(RuntimeException("timeout"))
        vm.refresh() // now fails
        assertTrue("list preserved", vm.state.value.content is DashboardState.Content.Jobs)
        assertEquals("timeout", vm.state.value.transientError)
    }

    @Test
    fun `not configured flag propagates into state`() {
        val vm = vm(FakeApi(jobsResult = Result.success(emptyList())), configured = false)
        vm.refresh()
        assertTrue(vm.state.value.notConfigured)
    }

    // --- W-056: action feedback + state gating ---

    @Test
    fun `cancel on an active job emits a success message`() = runBlocking {
        val api = FakeApi(cancelResult = Result.success(Unit))
        val vm = vm(api)
        val msg = collectOneMessage(vm) { vm.cancel(job("1", JobState.DOWNLOADING)) }
        assertTrue("got a cancel message", msg.contains("Cancelled"))
    }

    @Test
    fun `cancel failure surfaces the error (never silently discarded)`() = runBlocking {
        val api = FakeApi(cancelResult = Result.failure(RuntimeException("server said no")))
        val vm = vm(api)
        val msg = collectOneMessage(vm) { vm.cancel(job("1", JobState.DOWNLOADING)) }
        assertTrue(msg.contains("Couldn't cancel"))
        assertTrue(msg.contains("server said no"))
    }

    @Test
    fun `retry failure surfaces the error`() = runBlocking {
        val api = FakeApi(retryResult = Result.failure(RuntimeException("nope")))
        val vm = vm(api)
        val msg = collectOneMessage(vm) { vm.retry(job("1", JobState.FAILED)) }
        assertTrue(msg.contains("Couldn't retry"))
    }

    @Test
    fun `remove failure surfaces the error`() = runBlocking {
        val api = FakeApi(removeResult = Result.failure(RuntimeException("gone")))
        val vm = vm(api)
        val msg = collectOneMessage(vm) { vm.remove(job("1", JobState.COMPLETED)) }
        assertTrue(msg.contains("Couldn't remove"))
    }

    @Test
    fun `cancel on a terminal job is gated and never calls the API`() = runBlocking {
        val api = FakeApi()
        val vm = vm(api)
        val before = api.listJobsCalls
        val msg = collectOneMessage(vm) { vm.cancel(job("1", JobState.COMPLETED)) }
        assertTrue("gated message", msg.contains("can't be cancelled"))
        // No refresh() fired (that only happens on a successful action).
        assertEquals(before, api.listJobsCalls)
    }

    @Test
    fun `remove on a still-active job is gated`() = runBlocking {
        val vm = vm(FakeApi())
        val msg = collectOneMessage(vm) { vm.remove(job("1", JobState.DOWNLOADING)) }
        assertTrue(msg.contains("still active"))
    }

    /**
     * Collects the first message the [action] causes the VM to emit. The fake
     * API is synchronous, so we subscribe, run the action, and read the buffered
     * emission (the SharedFlow has extraBufferCapacity so the emit isn't lost
     * even though no collector is suspended at emit time).
     */
    private fun collectOneMessage(vm: DashboardViewModel, action: () -> Unit): String = runBlocking {
        val received = mutableListOf<String>()
        val collector = launch { vm.messages.collect { received += it } }
        // Let the collector subscribe before we trigger the (synchronous) emit.
        yield()
        action()
        // Give the emission a tick to be delivered.
        repeat(20) { if (received.isEmpty()) delay(5) }
        collector.cancel()
        received.firstOrNull() ?: error("no message emitted")
    }
}
