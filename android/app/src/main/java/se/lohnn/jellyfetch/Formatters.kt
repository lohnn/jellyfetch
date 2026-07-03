package se.lohnn.jellyfetch

object Formatters {

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
