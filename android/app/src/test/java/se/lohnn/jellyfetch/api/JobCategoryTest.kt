package se.lohnn.jellyfetch.api

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Pins [parseJobCategory]'s tolerant-of-absence parsing (jellyfin-plugin's
 * `Category` field, confirmed 2026-07-05) — the same defensive style as
 * every other optional field on [Job]. Plain JVM, no Android/Robolectric
 * needed: the function is pure string-in/enum-out.
 */
class JobCategoryTest {

    @Test
    fun `null resolves to null (unclassified — the common case)`() {
        assertNull(parseJobCategory(null))
    }

    @Test
    fun `blank or whitespace-only resolves to null`() {
        assertNull(parseJobCategory(""))
        assertNull(parseJobCategory("   "))
    }

    @Test
    fun `exact PascalCase values from the server parse correctly`() {
        assertEquals(JobCategory.MOVIE, parseJobCategory("Movie"))
        assertEquals(JobCategory.SERIES, parseJobCategory("Series"))
        assertEquals(JobCategory.OTHER, parseJobCategory("Other"))
    }

    @Test
    fun `parsing is case-insensitive, per the published contract`() {
        assertEquals(JobCategory.MOVIE, parseJobCategory("movie"))
        assertEquals(JobCategory.MOVIE, parseJobCategory("MOVIE"))
        assertEquals(JobCategory.SERIES, parseJobCategory("sErIeS"))
        assertEquals(JobCategory.OTHER, parseJobCategory("OTHER"))
    }

    @Test
    fun `surrounding whitespace is tolerated`() {
        assertEquals(JobCategory.SERIES, parseJobCategory("  Series  "))
    }

    @Test
    fun `the request-only Auto placeholder must never resolve to a real category`() {
        // "Auto" is documented as a submit-endpoint request HINT only; the
        // resolved Job's Category is documented to never emit it. If a future
        // server bug ever leaked it through anyway, this must still degrade
        // to "no badge" rather than crash or invent a fake category.
        assertNull(parseJobCategory("Auto"))
        assertNull(parseJobCategory("auto"))
    }

    @Test
    fun `unrecognized or future values resolve to null rather than throwing`() {
        assertNull(parseJobCategory("Documentary"))
        assertNull(parseJobCategory("garbage-value"))
    }
}
