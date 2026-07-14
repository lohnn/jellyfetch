package se.lohnn.jellyfetch

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import se.lohnn.jellyfetch.api.LibraryItemType

/**
 * Pure JVM unit tests for the correction feature's decision logic — no Android
 * runtime (lazy, not eager, android.* init per the spec). Covers the two bits of
 * logic that don't need a device: TMDb-id extraction from user paste, and the
 * tolerant library-type parse.
 */
class CorrectionLogicTest {

    @Test
    fun extractTmdbId_bareNumber_passesThrough() {
        assertEquals("603", CorrectionDialog.extractTmdbId("603"))
        assertEquals("603", CorrectionDialog.extractTmdbId("  603 "))
    }

    @Test
    fun extractTmdbId_fromMovieUrl_pullsIdSegment() {
        assertEquals(
            "603",
            CorrectionDialog.extractTmdbId("https://www.themoviedb.org/movie/603-the-matrix"),
        )
    }

    @Test
    fun extractTmdbId_fromTvUrl_pullsIdSegment() {
        assertEquals(
            "1399",
            CorrectionDialog.extractTmdbId("https://www.themoviedb.org/tv/1399-game-of-thrones"),
        )
    }

    @Test
    fun extractTmdbId_urlSegmentBeatsOtherDigits() {
        // A trailing slug with digits must not win over the /movie/<id> segment.
        assertEquals(
            "603",
            CorrectionDialog.extractTmdbId("https://www.themoviedb.org/movie/603-matrix-1999"),
        )
    }

    @Test
    fun extractTmdbId_noDigits_returnsInputForServerToReject() {
        // We don't guess — a garbage paste is returned trimmed so the server can
        // reject it with a clear message (W-056: surface, don't silently swallow).
        assertEquals("notanid", CorrectionDialog.extractTmdbId("  notanid  "))
    }

    @Test
    fun libraryItemType_parse_isTolerant() {
        assertEquals(LibraryItemType.MOVIE, LibraryItemType.parse("Movie"))
        assertEquals(LibraryItemType.MOVIE, LibraryItemType.parse("movie"))
        assertEquals(LibraryItemType.SERIES, LibraryItemType.parse("SERIES"))
        assertNull(LibraryItemType.parse(null))
        assertNull(LibraryItemType.parse(""))
        assertNull(LibraryItemType.parse("Episode"))
    }

    @Test
    fun libraryItemType_wireName_isPascalCase() {
        assertEquals("Movie", LibraryItemType.MOVIE.wireName)
        assertEquals("Series", LibraryItemType.SERIES.wireName)
    }
}
