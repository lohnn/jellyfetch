package se.lohnn.jellyfetch

import android.content.Context
import java.text.ParseException
import java.text.SimpleDateFormat
import java.util.Locale
import se.lohnn.jellyfetch.api.JobState

object Formatters {

    /** Shared human label for a [JobState] — used by the flat dashboard row and the detail/episode rows alike. */
    fun stateLabel(context: Context, state: JobState): String = when (state) {
        JobState.QUEUED -> context.getString(R.string.state_queued)
        JobState.RESOLVING -> context.getString(R.string.state_resolving)
        JobState.DOWNLOADING -> context.getString(R.string.state_downloading)
        JobState.PROCESSING -> context.getString(R.string.state_processing)
        JobState.COMPLETED -> context.getString(R.string.state_completed)
        JobState.FAILED -> context.getString(R.string.state_failed)
        JobState.CANCELLED -> context.getString(R.string.state_cancelled)
    }

    /**
     * Formats a server ISO 8601 timestamp (e.g. "2026-07-02T16:31:12+00:00")
     * into a short local-time display string. Falls back to the raw value on
     * any parse failure — a display glitch, never a crash, and still shows
     * *something* useful (min SDK 24 predates java.time without desugaring,
     * hence SimpleDateFormat here rather than Instant/DateTimeFormatter).
     */
    fun timestamp(iso: String?): String? {
        if (iso.isNullOrBlank()) return null
        val patterns = listOf(
            "yyyy-MM-dd'T'HH:mm:ssXXX",
            "yyyy-MM-dd'T'HH:mm:ss.SSSXXX",
        )
        for (pattern in patterns) {
            try {
                val parser = SimpleDateFormat(pattern, Locale.US)
                val date = parser.parse(iso) ?: continue
                val out = SimpleDateFormat("MMM d, HH:mm", Locale.getDefault())
                return out.format(date)
            } catch (_: ParseException) {
                // try next pattern
            }
        }
        return iso
    }

    fun speed(bytesPerSec: Long?): String? {
        if (bytesPerSec == null) return null
        val kb = bytesPerSec / 1024.0
        return when {
            kb < 1 -> "$bytesPerSec B/s"
            kb < 1024 -> "%.0f KB/s".format(kb)
            else -> "%.1f MB/s".format(kb / 1024.0)
        }
    }

    fun eta(seconds: Long?): String? {
        if (seconds == null) return null
        if (seconds < 60) return "${seconds}s"
        val minutes = seconds / 60
        val remSeconds = seconds % 60
        if (minutes < 60) return "${minutes}m ${remSeconds}s"
        val hours = minutes / 60
        val remMinutes = minutes % 60
        return "${hours}h ${remMinutes}m"
    }
}
