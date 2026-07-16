package se.lohnn.jellyfetch.share

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import se.lohnn.jellyfetch.api.ConvertTarget
import se.lohnn.jellyfetch.api.ConvertTypeResult
import se.lohnn.jellyfetch.api.JellyFetchApi
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.api.LibraryInfo
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemPage
import se.lohnn.jellyfetch.api.LibraryItemType
import se.lohnn.jellyfetch.api.RemoteSearchCandidate

/**
 * Pure-JVM tests for the share-popup library picker's decision logic (I-159): the
 * VM is android.view-free with its [JellyFetchApi] injected as a closure, so a
 * synchronous fake exercises every guard deterministically — no device.
 *
 * Covers the four load-bearing behaviors from the dispatch:
 *   1. Auto (default) ⇒ NO LibraryId sent.
 *   2. Explicit pick ⇒ correct LibraryId sent.
 *   3. Lazy load fires ONLY on dropdown open (never on construction).
 *   4. Load failure falls back to Auto-only and never blocks sending.
 */
class SharePickerViewModelTest {

    private val movies = LibraryInfo(id = "lib-movies", name = "Movies", collectionType = "movies", locations = listOf("/m"), isPlaceable = true)
    private val tv = LibraryInfo(id = "lib-tv", name = "TV Shows", collectionType = "tvshows", locations = listOf("/t"), isPlaceable = true)
    private val photos = LibraryInfo(id = null, name = "Photos", collectionType = null, locations = emptyList(), isPlaceable = false)

    /**
     * Synchronous fake: [listLibraries] is scriptable (result + a call counter) so
     * a test can assert lazy-load timing and the failure fallback. Everything else
     * is an unused no-op.
     */
    private class FakeApi(
        val librariesResult: () -> Result<List<LibraryInfo>>,
    ) : JellyFetchApi {
        var listLibrariesCalls = 0

        override fun listLibraries(callback: (Result<List<LibraryInfo>>) -> Unit) {
            listLibrariesCalls++
            callback(librariesResult())
        }

        override fun testConnection(callback: (Result<Unit>) -> Unit) = callback(Result.success(Unit))
        override fun submitUrl(url: String, libraryId: String?, callback: (Result<String>) -> Unit) = callback(Result.success("j"))
        override fun submitTorrent(fileName: String, bytes: ByteArray, libraryId: String?, callback: (Result<String>) -> Unit) = callback(Result.success("j"))
        override fun listJobs(callback: (Result<List<Job>>) -> Unit) = callback(Result.success(emptyList()))
        override fun getJobDetail(id: String, callback: (Result<Job>) -> Unit) = Unit
        override fun cancelJob(id: String, callback: (Result<Unit>) -> Unit) = callback(Result.success(Unit))
        override fun retryJob(id: String, callback: (Result<Unit>) -> Unit) = callback(Result.success(Unit))
        override fun removeJob(id: String, callback: (Result<Unit>) -> Unit) = callback(Result.success(Unit))
        override fun getJobLibraryItem(jobId: String, callback: (Result<LibraryItem?>) -> Unit) = callback(Result.success(null))
        override fun getItemByPath(path: String, callback: (Result<LibraryItem?>) -> Unit) = callback(Result.success(null))
        override fun listLibraryItems(query: String?, type: LibraryItemType?, startIndex: Int, limit: Int, callback: (Result<LibraryItemPage>) -> Unit) =
            callback(Result.success(LibraryItemPage(emptyList(), 0, 0)))
        override fun searchRemoteMetadata(itemId: String, searchType: LibraryItemType, name: String, year: Int?, callback: (Result<List<RemoteSearchCandidate>>) -> Unit) =
            callback(Result.success(emptyList()))
        override fun applyCorrectionByResult(itemId: String, candidate: RemoteSearchCandidate, callback: (Result<Unit>) -> Unit) = callback(Result.success(Unit))
        override fun applyCorrectionByProvider(itemId: String, searchType: LibraryItemType, provider: String, providerId: String, callback: (Result<Unit>) -> Unit) = callback(Result.success(Unit))
        override fun convertType(itemId: String, target: ConvertTarget, callback: (Result<ConvertTypeResult>) -> Unit) = Unit
    }

    private fun vm(api: FakeApi) = SharePickerViewModel(apiProvider = { api })

    // --- 1. Auto is the default ⇒ no LibraryId --------------------------------

    @Test
    fun `default selection is Auto and sends no LibraryId`() {
        val api = FakeApi { Result.success(listOf(movies, tv)) }
        val sut = vm(api)

        assertTrue(sut.state.value.selection is LibrarySelection.Auto)
        assertEquals("Auto", sut.state.value.selectionLabel)
        // Auto ⇒ null LibraryId (server classifies + places as today).
        assertNull(sut.selectedLibraryId)
    }

    @Test
    fun `explicitly selecting Auto after a pick clears the LibraryId`() {
        val api = FakeApi { Result.success(listOf(movies, tv)) }
        val sut = vm(api)

        sut.onDropdownOpened()
        sut.selectLibrary(tv)
        assertEquals("lib-tv", sut.selectedLibraryId)

        sut.selectAuto()
        assertTrue(sut.state.value.selection is LibrarySelection.Auto)
        assertNull(sut.selectedLibraryId)
    }

    // --- 2. Explicit pick ⇒ correct LibraryId ---------------------------------

    @Test
    fun `explicit pick sends that library's id as LibraryId`() {
        val api = FakeApi { Result.success(listOf(movies, tv)) }
        val sut = vm(api)

        sut.onDropdownOpened()
        val accepted = sut.selectLibrary(movies)

        assertTrue(accepted)
        assertEquals("lib-movies", sut.selectedLibraryId)
        assertEquals("Movies", sut.state.value.selectionLabel)
    }

    @Test
    fun `a non-placeable library cannot be selected and stays Auto`() {
        val api = FakeApi { Result.success(listOf(movies, photos)) }
        val sut = vm(api)

        sut.onDropdownOpened()
        val accepted = sut.selectLibrary(photos)

        assertFalse(accepted)
        assertTrue(sut.state.value.selection is LibrarySelection.Auto)
        assertNull(sut.selectedLibraryId)
    }

    // --- 3. Lazy load only fires on dropdown open -----------------------------

    @Test
    fun `library list is NOT fetched on construction`() {
        val api = FakeApi { Result.success(listOf(movies, tv)) }
        vm(api)

        // Nothing fetched until the user opens the dropdown (lazy).
        assertEquals(0, api.listLibrariesCalls)
    }

    @Test
    fun `opening the dropdown triggers exactly one fetch and populates the list`() {
        val api = FakeApi { Result.success(listOf(movies, tv)) }
        val sut = vm(api)

        sut.onDropdownOpened()

        assertEquals(1, api.listLibrariesCalls)
        assertEquals(listOf(movies, tv), sut.state.value.libraries)
    }

    @Test
    fun `re-opening the dropdown does not re-fetch after a successful load`() {
        val api = FakeApi { Result.success(listOf(movies, tv)) }
        val sut = vm(api)

        sut.onDropdownOpened()
        sut.onDropdownOpened()
        sut.onDropdownOpened()

        assertEquals(1, api.listLibrariesCalls)
    }

    // --- 4. Load failure falls back to Auto-only, never blocks sending --------

    @Test
    fun `load failure surfaces the error, keeps Auto, and still sends with no LibraryId`() {
        val api = FakeApi { Result.failure(RuntimeException("boom: no route to host")) }
        val sut = vm(api)

        sut.onDropdownOpened()

        // W-056: surfaced, not swallowed.
        assertEquals("boom: no route to host", sut.state.value.loadError)
        // Fallback: no libraries, Auto still selected, submit works (null LibraryId).
        assertTrue(sut.state.value.libraries.isEmpty())
        assertTrue(sut.state.value.selection is LibrarySelection.Auto)
        assertNull(sut.selectedLibraryId)
    }

    @Test
    fun `retry after a failed load re-fetches and can succeed`() {
        var fail = true
        val api = FakeApi {
            if (fail) Result.failure(RuntimeException("temporary")) else Result.success(listOf(movies))
        }
        val sut = vm(api)

        sut.onDropdownOpened()
        assertEquals("temporary", sut.state.value.loadError)
        assertEquals(1, api.listLibrariesCalls)

        fail = false
        sut.retryLoad()

        assertEquals(2, api.listLibrariesCalls)
        assertNull(sut.state.value.loadError)
        assertEquals(listOf(movies), sut.state.value.libraries)
    }
}
