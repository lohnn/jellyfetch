package se.lohnn.jellyfetch.share

/**
 * Extracts the first usable URL or magnet URI from prose-wrapped shared
 * text. Real-world ACTION_SEND text/plain payloads observed in the wild
 * (documented for future reference — see dream residue):
 *
 *  - SVT Play app: "Kolla in det här! https://www.svtplay.se/video/abc123 via SVT Play"
 *  - YouTube app:   "Check out this video: https://youtu.be/dQw4w9WgXcQ"
 *                    (sometimes the plain https://www.youtube.com/watch?v=... form)
 *  - Chrome "Share... > copy link" from the address bar sends the bare URL
 *    with no surrounding prose, but "Share" from a page's own share button
 *    frequently prepends a title line before the URL, e.g.:
 *    "Page Title\nhttps://example.com/article"
 *
 * So: never assume the whole EXTRA_TEXT is the URL. Find the first
 * substring starting at a recognized scheme and ending at the first
 * whitespace, then strip common trailing punctuation that prose wrapping
 * tends to leave attached (closing parens, sentence-ending periods, etc).
 */
object UrlExtractor {

    private val URL_PATTERN = Regex("""(https?://|magnet:)\S+""")
    private val TRAILING_PUNCTUATION = setOf('.', ',', ')', ']', '}', '!', '?', '"', '\'', '>')

    fun extractFirstUrl(text: String?): String? {
        if (text.isNullOrBlank()) return null
        val match = URL_PATTERN.find(text) ?: return null
        var raw = match.value
        while (raw.isNotEmpty() && raw.last() in TRAILING_PUNCTUATION) {
            raw = raw.dropLast(1)
        }
        return raw.ifBlank { null }
    }
}
