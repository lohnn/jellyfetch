package se.lohnn.jellyfetch.correction

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import se.lohnn.jellyfetch.api.ConvertTarget
import se.lohnn.jellyfetch.api.ConvertTypeResult
import se.lohnn.jellyfetch.api.JellyFetchApi
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemPage
import se.lohnn.jellyfetch.api.LibraryItemType
import se.lohnn.jellyfetch.api.RemoteSearchCandidate

/**
 * Pure-JVM tests for the correction state machine — the load-bearing business
 * contract (I-152/I-153/W-066/SNG-040) extracted from the classic dialog so it's
 * testable without a device (I-127). No Robolectric: the ViewModel is
 * android.view-free and its API + poll scheduler are injected, so a synchronous
 * fake exercises every guard deterministically.
 *
 * These replace the deleted DashboardScrollTest (which tested the classic
 * ListView/SwipeRefreshLayout scroll bug — code now gone, replaced by Compose
 * PullToRefreshBox which resolves that class of bug natively).
 */
class CorrectionViewModelTest {

    private val movieItem = LibraryItem(id = "42", name = "The Matrix", year = 1999, type = LibraryItemType.MOVIE)

    /**
     * Synchronous fake: every call invokes the callback inline. Convert responses
     * and the by-path resolve are scriptable so a test can model "not indexed
     * yet → then indexed", the 409 stale case, etc.
     */
    private class FakeApi(
        val convertResponse: () -> Result<ConvertTypeResult> = { Result.failure(NotStubbed()) },
        val byPathResponses: MutableList<Result<LibraryItem?>> = mutableListOf(),
    ) : JellyFetchApi {
        var convertCalls = 0
        var byPathCalls = 0

        override fun convertType(itemId: String, target: ConvertTarget, callback: (Result<ConvertTypeResult>) -> Unit) {
            convertCalls++
            callback(convertResponse())
        }

        override fun getItemByPath(path: String, callback: (Result<LibraryItem?>) -> Unit) {
            byPathCalls++
            val next = if (byPathResponses.isNotEmpty()) byPathResponses.removeAt(0) else Result.success(null)
            callback(next)
        }

        override fun searchRemoteMetadata(
            itemId: String, searchType: LibraryItemType, name: String, year: Int?,
            callback: (Result<List<RemoteSearchCandidate>>) -> Unit,
        ) = callback(Result.success(listOf(RemoteSearchCandidate(name = name, year = year))))

        override fun applyCorrectionByResult(itemId: String, candidate: RemoteSearchCandidate, callback: (Result<Unit>) -> Unit) =
            callback(Result.success(Unit))

        override fun applyCorrectionByProvider(
            itemId: String, searchType: LibraryItemType, provider: String, providerId: String,
            callback: (Result<Unit>) -> Unit,
        ) = callback(Result.success(Unit))

        // Unused by these tests.
        override fun testConnection(callback: (Result<Unit>) -> Unit) = callback(Result.success(Unit))
        override fun submitUrl(url: String, callback: (Result<String>) -> Unit) = callback(Result.success("j"))
        override fun submitTorrent(fileName: String, bytes: ByteArray, callback: (Result<String>) -> Unit) = callback(Result.success("j"))
        override fun listJobs(callback: (Result<List<Job>>) -> Unit) = callback(Result.success(emptyList()))
        override fun getJobDetail(id: String, callback: (Result<Job>) -> Unit) = callback(Result.failure(NotStubbed()))
        override fun cancelJob(id: String, callback: (Result<Unit>) -> Unit) = callback(Result.success(Unit))
        override fun retryJob(id: String, callback: (Result<Unit>) -> Unit) = callback(Result.success(Unit))
        override fun removeJob(id: String, callback: (Result<Unit>) -> Unit) = callback(Result.success(Unit))
        override fun getJobLibraryItem(jobId: String, callback: (Result<LibraryItem?>) -> Unit) = callback(Result.success(null))
        override fun listLibraryItems(
            query: String?, type: LibraryItemType?, startIndex: Int, limit: Int,
            callback: (Result<LibraryItemPage>) -> Unit,
        ) = callback(Result.success(LibraryItemPage(emptyList(), 0, 0)))

        class NotStubbed : RuntimeException("not stubbed")
    }

    /** Immediate scheduler: run the poll retry inline so the loop is synchronous. */
    private fun immediateSchedule(): (Long, () -> Unit) -> Unit = { _, block -> block() }

    private fun vm(api: FakeApi) = CorrectionViewModel(
        item = movieItem,
        apiProvider = { api },
        schedule = immediateSchedule(),
        pollIntervalMs = 0L,
        pollMaxAttempts = 3,
    )

    @Test
    fun `convert fires the latch synchronously and disables further convert (W-066)`() {
        // rebindPath resolves immediately to the new item.
        val newItem = movieItem.copy(id = "99", type = LibraryItemType.SERIES)
        val api = FakeApi(
            convertResponse = {
                Result.success(
                    ConvertTypeResult(
                        sourceItemId = "42",
                        targetType = ConvertTarget.SERIES,
                        status = "RescanPending",
                        itemDirectory = "/media/series/The Matrix",
                    )
                )
            },
            byPathResponses = mutableListOf(Result.success(newItem)),
        )
        val vm = vm(api)
        vm.selectTarget(ConvertTarget.SERIES)
        assertTrue("convert must be enabled for a real type change", vm.state.value.convertEnabled)

        vm.convert(ConvertTarget.SERIES)

        // The latch fired synchronously; convert can never be offered again.
        assertTrue(vm.state.value.convertFired)
        assertFalse("once fired, convert must be disabled (the dead id guard)", vm.state.value.convertEnabled)
        assertEquals(1, api.convertCalls)
        // Resolved by PATH (never title): the by-path resolve was used.
        assertEquals(1, api.byPathCalls)
    }

    @Test
    fun `a second convert on the same fired instance is a no-op (double-convert guard)`() {
        val api = FakeApi(
            convertResponse = {
                Result.success(
                    ConvertTypeResult(sourceItemId = "42", targetType = ConvertTarget.SERIES, status = "RescanPending", itemDirectory = "/x")
                )
            },
            byPathResponses = mutableListOf(Result.success(movieItem.copy(id = "99"))),
        )
        val vm = vm(api)
        vm.selectTarget(ConvertTarget.SERIES)
        vm.convert(ConvertTarget.SERIES)
        val callsAfterFirst = api.convertCalls

        // Try to fire again — must be refused by the latch, no second server call.
        vm.convert(ConvertTarget.SERIES)
        assertEquals("the fired latch must reject a second convert", callsAfterFirst, api.convertCalls)
    }

    @Test
    fun `a convert failure (409 stale) locks the picker down (W-056)`() {
        val api = FakeApi(
            convertResponse = {
                Result.failure(IllegalStateException("This item was already converted (409 Superseded)."))
            },
        )
        val vm = vm(api)
        vm.selectTarget(ConvertTarget.SERIES)
        vm.convert(ConvertTarget.SERIES)

        val s = vm.state.value
        assertTrue("a failed convert must lock the whole picker down", s.lockedDown)
        assertTrue(s.convertFired)
        assertFalse(s.convertEnabled)
        assertTrue(s.convertPhase is ConvertPhase.Failed)
    }

    @Test
    fun `by-path poll retries until the rescan indexes the new item, never by title`() {
        val newItem = movieItem.copy(id = "99", type = LibraryItemType.SERIES)
        val api = FakeApi(
            convertResponse = {
                Result.success(
                    ConvertTypeResult(sourceItemId = "42", targetType = ConvertTarget.SERIES, status = "RescanPending", itemDirectory = "/media/series/The Matrix")
                )
            },
            // Not indexed yet, not indexed yet, THEN found.
            byPathResponses = mutableListOf(Result.success(null), Result.success(null), Result.success(newItem)),
        )
        val vm = vm(api)
        vm.selectTarget(ConvertTarget.SERIES)
        vm.convert(ConvertTarget.SERIES)

        // Immediate scheduler ran the retries inline: 3 by-path calls (2 misses + 1 hit).
        assertEquals(3, api.byPathCalls)
        assertTrue(vm.state.value.convertPhase is ConvertPhase.Found)
    }

    @Test
    fun `convert with no rebind path falls back to pending (tolerant of absence)`() {
        val api = FakeApi(
            convertResponse = {
                // No itemDirectory, no movedPaths → rebindPath == null.
                Result.success(ConvertTypeResult(sourceItemId = "42", targetType = ConvertTarget.SERIES, status = "RescanPending"))
            },
        )
        val vm = vm(api)
        vm.selectTarget(ConvertTarget.SERIES)
        vm.convert(ConvertTarget.SERIES)

        assertEquals("no path to resolve → must not call getItemByPath", 0, api.byPathCalls)
        assertTrue(vm.state.value.convertPhase is ConvertPhase.Pending)
    }

    @Test
    fun `selecting the same type as current never enables convert`() {
        val api = FakeApi()
        val vm = vm(api)
        vm.selectTarget(ConvertTarget.MOVIE) // same as current
        assertFalse(vm.state.value.convertEnabled)
        vm.convert(ConvertTarget.MOVIE)
        assertEquals("a same-type convert must not hit the server", 0, api.convertCalls)
    }
}
