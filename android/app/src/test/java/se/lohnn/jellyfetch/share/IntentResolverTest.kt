package se.lohnn.jellyfetch.share

import android.content.ContentResolver
import android.content.Intent
import android.database.Cursor
import android.net.Uri
import android.provider.OpenableColumns
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.mockito.kotlin.any
import org.mockito.kotlin.anyOrNull
import org.mockito.kotlin.doReturn
import org.mockito.kotlin.eq
import org.mockito.kotlin.mock
import org.mockito.kotlin.whenever
import java.io.ByteArrayInputStream

/**
 * Exercises IntentResolver.resolveBlocking directly (internal visibility —
 * see IntentResolver kdoc) so these are plain JVM unit tests: the
 * ContentResolver/Intent/Uri seam is mocked with Mockito rather than
 * standing up a real (or shadow) Android runtime. No emulator/Robolectric
 * required — Handler/Looper are never touched because resolveBlocking sits
 * below IntentResolver.resolve()'s executor/Handler plumbing.
 */
class IntentResolverTest {

    private val realTorrentBytes =
        "d8:announce35:http://tracker.example.com/announce4:infod6:lengthi1024eee"
            .toByteArray(Charsets.ISO_8859_1)

    private fun mockResolver(): ContentResolver = mock()

    // --- ACTION_SEND / text/plain ------------------------------------------

    @Test
    fun `ACTION_SEND text-plain with a plain URL resolves to UrlOrMagnet`() {
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_SEND
            on { type } doReturn "text/plain"
            on { getStringExtra(Intent.EXTRA_TEXT) } doReturn "https://www.svtplay.se/video/abc123"
        }

        val result = IntentResolver.resolveBlocking(mockResolver(), intent)

        assertEquals(CaughtInput.UrlOrMagnet("https://www.svtplay.se/video/abc123"), result)
    }

    @Test
    fun `ACTION_SEND text-plain with prose-wrapped URL extracts just the URL`() {
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_SEND
            on { type } doReturn "text/plain"
            on { getStringExtra(Intent.EXTRA_TEXT) } doReturn "Check out this video: https://youtu.be/dQw4w9WgXcQ"
        }

        val result = IntentResolver.resolveBlocking(mockResolver(), intent)

        assertEquals(CaughtInput.UrlOrMagnet("https://youtu.be/dQw4w9WgXcQ"), result)
    }

    @Test
    fun `ACTION_SEND text-plain with no URL resolves to null`() {
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_SEND
            on { type } doReturn "text/plain"
            on { getStringExtra(Intent.EXTRA_TEXT) } doReturn "no links here, just words"
        }

        assertNull(IntentResolver.resolveBlocking(mockResolver(), intent))
    }

    // --- ACTION_SEND / file share (torrent) ---------------------------------

    @Test
    fun `ACTION_SEND with torrent stream extra resolves to Torrent`() {
        val uri = mock<Uri> {
            on { scheme } doReturn "content"
            on { lastPathSegment } doReturn "movie.torrent"
        }
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_SEND
            on { type } doReturn "application/x-bittorrent"
            @Suppress("DEPRECATION")
            on { getParcelableExtra<Uri>(Intent.EXTRA_STREAM) } doReturn uri
        }
        val resolver = mock<ContentResolver> {
            on { openInputStream(uri) } doReturn ByteArrayInputStream(realTorrentBytes)
        }
        stubNoDisplayName(resolver, uri)

        val result = IntentResolver.resolveBlocking(resolver, intent)

        assertEquals(CaughtInput.Torrent("movie.torrent", realTorrentBytes), result)
    }

    @Test
    fun `ACTION_SEND with no stream extra resolves to null`() {
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_SEND
            on { type } doReturn "application/x-bittorrent"
            @Suppress("DEPRECATION")
            on { getParcelableExtra<Uri>(Intent.EXTRA_STREAM) } doReturn null
        }

        assertNull(IntentResolver.resolveBlocking(mockResolver(), intent))
    }

    // --- ACTION_VIEW / magnet ------------------------------------------------

    @Test
    fun `ACTION_VIEW magnet scheme resolves to UrlOrMagnet with the raw uri string`() {
        val magnetString = "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a"
        val uri = mock<Uri> {
            on { scheme } doReturn "magnet"
            on { toString() } doReturn magnetString
        }
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_VIEW
            on { data } doReturn uri
        }

        val result = IntentResolver.resolveBlocking(mockResolver(), intent)

        assertEquals(CaughtInput.UrlOrMagnet(magnetString), result)
    }

    @Test
    fun `ACTION_VIEW magnet scheme match is case-insensitive`() {
        val uri = mock<Uri> {
            on { scheme } doReturn "MAGNET"
            on { toString() } doReturn "MAGNET:?xt=urn:btih:abc"
        }
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_VIEW
            on { data } doReturn uri
        }

        val result = IntentResolver.resolveBlocking(mockResolver(), intent)

        assertTrue(result is CaughtInput.UrlOrMagnet)
    }

    @Test
    fun `ACTION_VIEW with null data resolves to null`() {
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_VIEW
            on { data } doReturn null
        }

        assertNull(IntentResolver.resolveBlocking(mockResolver(), intent))
    }

    // --- ACTION_VIEW / .torrent content uri -----------------------------------

    @Test
    fun `ACTION_VIEW content uri that sniffs as bencode resolves to Torrent`() {
        val uri = mock<Uri> {
            on { scheme } doReturn "content"
            on { lastPathSegment } doReturn "somefile"
        }
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_VIEW
            on { data } doReturn uri
        }
        val resolver = mock<ContentResolver> {
            on { openInputStream(uri) } doReturn ByteArrayInputStream(realTorrentBytes)
        }
        stubNoDisplayName(resolver, uri)

        val result = IntentResolver.resolveBlocking(resolver, intent)

        // No .torrent extension on the display name/last path segment, so the
        // resolver appends one (see IntentResolver.readTorrent). CaughtInput.Torrent
        // has a byte-content equals() override, so this also pins the payload bytes
        // without relying on ByteArray reference equality.
        assertEquals(CaughtInput.Torrent("somefile.torrent", realTorrentBytes), result)
    }

    @Test
    fun `ACTION_VIEW content uri that neither sniffs nor has a torrent filename resolves to null`() {
        val uri = mock<Uri> {
            on { scheme } doReturn "content"
            on { lastPathSegment } doReturn "randomfile.bin"
        }
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_VIEW
            on { data } doReturn uri
        }
        val notATorrent = "<html>not a torrent</html>".toByteArray()
        val resolver = mock<ContentResolver> {
            on { openInputStream(uri) } doReturn ByteArrayInputStream(notATorrent)
        }
        stubNoDisplayName(resolver, uri)

        assertNull(IntentResolver.resolveBlocking(resolver, intent))
    }

    @Test
    fun `ACTION_VIEW content uri with torrent filename but non-sniffing bytes is accepted leniently`() {
        // Filename says .torrent even though the bytes don't cleanly sniff
        // (e.g. proxied/CDN-mangled) -- the resolver is lenient on an OR.
        val uri = mock<Uri> {
            on { scheme } doReturn "content"
            on { lastPathSegment } doReturn "weird.torrent"
        }
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_VIEW
            on { data } doReturn uri
        }
        val nonSniffingBytes = "not bencode but still a torrent by name".toByteArray()
        val resolver = mock<ContentResolver> {
            on { openInputStream(uri) } doReturn ByteArrayInputStream(nonSniffingBytes)
        }
        stubNoDisplayName(resolver, uri)

        val result = IntentResolver.resolveBlocking(resolver, intent) as? CaughtInput.Torrent

        assertEquals("weird.torrent", result?.fileName)
    }

    @Test
    fun `ACTION_VIEW with empty byte stream resolves to null`() {
        val uri = mock<Uri> {
            on { scheme } doReturn "content"
            on { lastPathSegment } doReturn "empty.torrent"
        }
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_VIEW
            on { data } doReturn uri
        }
        val resolver = mock<ContentResolver> {
            on { openInputStream(uri) } doReturn ByteArrayInputStream(ByteArray(0))
        }
        stubNoDisplayName(resolver, uri)

        assertNull(IntentResolver.resolveBlocking(resolver, intent))
    }

    @Test
    fun `display name from content resolver query takes priority over last path segment`() {
        val uri = mock<Uri> {
            on { scheme } doReturn "content"
            on { lastPathSegment } doReturn "opaque-id-123"
        }
        val intent = mock<Intent> {
            on { action } doReturn Intent.ACTION_VIEW
            on { data } doReturn uri
        }
        val resolver = mock<ContentResolver> {
            on { openInputStream(uri) } doReturn ByteArrayInputStream(realTorrentBytes)
        }
        val cursor = mock<Cursor> {
            on { moveToFirst() } doReturn true
            on { getColumnIndex(OpenableColumns.DISPLAY_NAME) } doReturn 0
            on { getString(0) } doReturn "MyShow.S01E02.torrent"
        }
        whenever(resolver.query(eq(uri), any(), anyOrNull(), anyOrNull(), anyOrNull())) doReturn cursor

        val result = IntentResolver.resolveBlocking(resolver, intent) as? CaughtInput.Torrent

        assertEquals("MyShow.S01E02.torrent", result?.fileName)
    }

    // --- Unknown / unsupported actions ---------------------------------------

    @Test
    fun `unsupported action resolves to null`() {
        val intent = mock<Intent> {
            on { action } doReturn "android.intent.action.EDIT"
        }

        assertNull(IntentResolver.resolveBlocking(mockResolver(), intent))
    }

    private fun stubNoDisplayName(resolver: ContentResolver, uri: Uri) {
        whenever(resolver.query(eq(uri), any(), anyOrNull(), anyOrNull(), anyOrNull())) doReturn null
    }
}
