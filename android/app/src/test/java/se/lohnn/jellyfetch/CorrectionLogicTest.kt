package se.lohnn.jellyfetch

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import se.lohnn.jellyfetch.api.ConvertTarget
import se.lohnn.jellyfetch.api.ConvertTypeResult
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

    @Test
    fun libraryItemType_other_flipsMovieAndSeries() {
        // The convert target is always the opposite type — a Movie converts to a
        // Series and vice versa. This is the whole basis of the type-convert control.
        assertEquals(LibraryItemType.SERIES, LibraryItemType.MOVIE.other())
        assertEquals(LibraryItemType.MOVIE, LibraryItemType.SERIES.other())
    }

    @Test
    fun libraryItemType_other_isInvolution() {
        // other().other() must round-trip back to the original.
        for (t in LibraryItemType.values()) {
            assertEquals(t, t.other().other())
        }
    }

    @Test
    fun convertTarget_wireName_isPascalCase() {
        assertEquals("Movie", ConvertTarget.MOVIE.wireName)
        assertEquals("Series", ConvertTarget.SERIES.wireName)
        assertEquals("Other", ConvertTarget.OTHER.wireName)
    }

    @Test
    fun convertTarget_parse_isTolerant() {
        assertEquals(ConvertTarget.MOVIE, ConvertTarget.parse("Movie"))
        assertEquals(ConvertTarget.SERIES, ConvertTarget.parse("series"))
        assertEquals(ConvertTarget.OTHER, ConvertTarget.parse("OTHER"))
        assertNull(ConvertTarget.parse(null))
        assertNull(ConvertTarget.parse(""))
        assertNull(ConvertTarget.parse("Episode"))
    }

    @Test
    fun convertTarget_pollType_dropsFilterForOther() {
        // The whole point of the Other branch: no type filter when polling, because
        // the fallback library decides the re-typed item's kind.
        assertEquals(LibraryItemType.MOVIE, ConvertTarget.MOVIE.pollType)
        assertEquals(LibraryItemType.SERIES, ConvertTarget.SERIES.pollType)
        assertNull(ConvertTarget.OTHER.pollType)
    }

    @Test
    fun convertTarget_of_mapsCurrentType() {
        assertEquals(ConvertTarget.MOVIE, ConvertTarget.of(LibraryItemType.MOVIE))
        assertEquals(ConvertTarget.SERIES, ConvertTarget.of(LibraryItemType.SERIES))
    }

    // --- Post-convert rebind path (the live stale-display fix) ---------------

    @Test
    fun rebindPath_prefersItemDirectory() {
        // ItemDirectory is the most stable rebind key (the new item's Path is under
        // it); it wins over MovedPaths when both are present.
        val result = ConvertTypeResult(
            sourceItemId = "old",
            targetType = ConvertTarget.MOVIE,
            status = "RescanPending",
            movedPaths = listOf("/media/movies/Film (2021)/Film (2021).mkv"),
            itemDirectory = "/media/movies/Film (2021)",
        )
        assertEquals("/media/movies/Film (2021)", result.rebindPath)
    }

    @Test
    fun rebindPath_fallsBackToFirstMovedPath() {
        // No ItemDirectory (older/edge server) → first moved file path.
        val result = ConvertTypeResult(
            sourceItemId = "old",
            targetType = ConvertTarget.SERIES,
            status = "RescanPending",
            movedPaths = listOf("/media/series/Show/Season 01/Show - S01E01.mkv"),
            itemDirectory = null,
        )
        assertEquals("/media/series/Show/Season 01/Show - S01E01.mkv", result.rebindPath)
    }

    @Test
    fun rebindPath_ignoresBlankAndIsNullWhenNothingUsable() {
        // Blank ItemDirectory falls through to MovedPaths; blank/empty everything → null
        // (the tolerant-of-absence branch that fires onApplied(null) → caller re-fetches).
        val blankDir = ConvertTypeResult(
            sourceItemId = "old",
            targetType = ConvertTarget.MOVIE,
            status = "RescanPending",
            movedPaths = listOf("/media/movies/X/X.mkv"),
            itemDirectory = "   ",
        )
        assertEquals("/media/movies/X/X.mkv", blankDir.rebindPath)

        val nothing = ConvertTypeResult(
            sourceItemId = "old",
            targetType = ConvertTarget.MOVIE,
            status = "RescanPending",
            movedPaths = emptyList(),
            itemDirectory = null,
        )
        assertNull(nothing.rebindPath)
    }
}
