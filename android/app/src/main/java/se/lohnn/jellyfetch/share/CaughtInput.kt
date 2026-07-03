package se.lohnn.jellyfetch.share

/** What ShareActivity caught, normalized regardless of which intent-filter matched. */
sealed class CaughtInput {
    abstract val displayLabel: String

    data class UrlOrMagnet(val url: String) : CaughtInput() {
        override val displayLabel: String get() = url
    }

    data class Torrent(val fileName: String, val bytes: ByteArray) : CaughtInput() {
        override val displayLabel: String get() = fileName

        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is Torrent) return false
            return fileName == other.fileName && bytes.contentEquals(other.bytes)
        }

        override fun hashCode(): Int = fileName.hashCode() * 31 + bytes.contentHashCode()
    }
}
