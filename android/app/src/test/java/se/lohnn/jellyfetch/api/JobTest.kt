package se.lohnn.jellyfetch.api

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins [Job.episodeLabel]'s current formatting behavior, including the SVT
 * quirk noted on [Job.seasonNumber]: for SVT-sourced episodes, seasonNumber
 * carries the calendar YEAR (e.g. 2024), not a 1-based season — the label
 * renders it verbatim (no re-interpretation), which is exactly the behavior
 * these tests lock in.
 */
class JobTest {

    private fun baseJob(
        title: String = "fallback title",
        seriesName: String? = null,
        seasonNumber: Int? = null,
        episodeNumber: Int? = null,
        episodeTitle: String? = null,
    ) = Job(
        id = "job-1",
        title = title,
        state = JobState.COMPLETED,
        seriesName = seriesName,
        seasonNumber = seasonNumber,
        episodeNumber = episodeNumber,
        episodeTitle = episodeTitle,
    )

    @Test
    fun `all fields present renders series, SVT year-as-season, episode and title`() {
        val job = baseJob(
            seriesName = "Vår tid är nu",
            seasonNumber = 2024, // SVT quirk: this is a YEAR, not season 1
            episodeNumber = 3,
            episodeTitle = "Avsnitt 3",
        )
        assertEquals("Vår tid är nu · S2024E03 · Avsnitt 3", job.episodeLabel)
    }

    @Test
    fun `SVT year is rendered verbatim, not normalized to a small season number`() {
        val job = baseJob(seriesName = "Serie", seasonNumber = 2019, episodeNumber = 1)
        // Must NOT collapse 2019 down to "S1E01" or any reinterpretation —
        // the raw value from the server is rendered as-is.
        assertEquals("Serie · S2019E01", job.episodeLabel)
    }

    @Test
    fun `episode number is zero-padded to two digits`() {
        val job = baseJob(seriesName = "Serie", seasonNumber = 2024, episodeNumber = 7)
        assertEquals("Serie · S2024E07", job.episodeLabel)
    }

    @Test
    fun `episode number greater than 9 is not truncated by padding`() {
        val job = baseJob(seriesName = "Serie", seasonNumber = 2024, episodeNumber = 12)
        assertEquals("Serie · S2024E12", job.episodeLabel)
    }

    @Test
    fun `no structured fields falls back to title`() {
        val job = baseJob(title = "raw title before completion")
        assertEquals("raw title before completion", job.episodeLabel)
    }

    @Test
    fun `series name only, no season-episode pair, renders just the series name`() {
        val job = baseJob(title = "fallback", seriesName = "Serie utan avsnittsdata")
        assertEquals("Serie utan avsnittsdata", job.episodeLabel)
    }

    @Test
    fun `season without episode number does not render the S-E segment`() {
        // Both season AND episode are required together per the getter logic.
        val job = baseJob(title = "fallback", seriesName = "Serie", seasonNumber = 2024, episodeNumber = null)
        assertEquals("Serie", job.episodeLabel)
    }

    @Test
    fun `episode title only, no series or season-episode, still renders`() {
        val job = baseJob(title = "fallback", episodeTitle = "Just a title")
        assertEquals("Just a title", job.episodeLabel)
    }

    @Test
    fun `non-SVT source with only title populated (pre-completion) uses title`() {
        val job = baseJob(title = "Some.Movie.2024.1080p")
        assertEquals("Some.Movie.2024.1080p", job.episodeLabel)
    }
}
