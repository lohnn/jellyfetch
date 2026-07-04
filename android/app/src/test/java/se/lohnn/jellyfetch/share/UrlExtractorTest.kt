package se.lohnn.jellyfetch.share

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class UrlExtractorTest {

    @Test
    fun `bare URL is returned verbatim`() {
        assertEquals(
            "https://www.svtplay.se/video/abc123",
            UrlExtractor.extractFirstUrl("https://www.svtplay.se/video/abc123"),
        )
    }

    @Test
    fun `URL embedded in surrounding prose is extracted`() {
        assertEquals(
            "https://example.com/article",
            UrlExtractor.extractFirstUrl("check this out https://example.com/article cool"),
        )
    }

    @Test
    fun `SVT Play style share text extracts the URL only`() {
        // Real-world payload documented in UrlExtractor's kdoc.
        val svtText = "Kolla in det här! https://www.svtplay.se/video/abc123 via SVT Play"
        assertEquals("https://www.svtplay.se/video/abc123", UrlExtractor.extractFirstUrl(svtText))
    }

    @Test
    fun `YouTube app share text extracts the short URL`() {
        val ytText = "Check out this video: https://youtu.be/dQw4w9WgXcQ"
        assertEquals("https://youtu.be/dQw4w9WgXcQ", UrlExtractor.extractFirstUrl(ytText))
    }

    @Test
    fun `title line then URL on its own line extracts just the URL`() {
        val text = "Page Title\nhttps://example.com/article"
        assertEquals("https://example.com/article", UrlExtractor.extractFirstUrl(text))
    }

    @Test
    fun `multiple URLs in text - first one wins`() {
        val text = "https://first.example.com/a and also https://second.example.com/b"
        assertEquals("https://first.example.com/a", UrlExtractor.extractFirstUrl(text))
    }

    @Test
    fun `magnet link is recognized as a URL`() {
        val magnet = "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&dn=example"
        assertEquals(magnet, UrlExtractor.extractFirstUrl(magnet))
    }

    @Test
    fun `magnet link embedded in prose is extracted`() {
        val text = "grab this magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a please"
        assertEquals(
            "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a",
            UrlExtractor.extractFirstUrl(text),
        )
    }

    @Test
    fun `no URL present returns null`() {
        assertNull(UrlExtractor.extractFirstUrl("just some regular text, nothing to see here"))
    }

    @Test
    fun `null input returns null`() {
        assertNull(UrlExtractor.extractFirstUrl(null))
    }

    @Test
    fun `blank input returns null`() {
        assertNull(UrlExtractor.extractFirstUrl("   "))
    }

    @Test
    fun `trailing sentence punctuation is stripped`() {
        assertEquals(
            "https://example.com/article",
            UrlExtractor.extractFirstUrl("Have a look: https://example.com/article."),
        )
    }

    @Test
    fun `trailing closing paren is stripped`() {
        assertEquals(
            "https://example.com/article",
            UrlExtractor.extractFirstUrl("(see https://example.com/article)"),
        )
    }

    @Test
    fun `multiple trailing punctuation characters are all stripped`() {
        assertEquals(
            "https://example.com/article",
            UrlExtractor.extractFirstUrl("Check this out: https://example.com/article)!\""),
        )
    }
}
