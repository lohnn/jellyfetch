package se.lohnn.jellyfetch.share

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TorrentSnifferTest {

    @Test
    fun `real bencode torrent header bytes are recognized`() {
        // Minimal-but-real bencode dictionary shape: starts with 'd', carries
        // an "announce" key and an "info" dict, like an actual .torrent file.
        val header = "d8:announce35:http://tracker.example.com/announce4:infod6:lengthi1024e4:name8:foo.file12:piece lengthi16384eee"
        assertTrue(TorrentSniffer.looksLikeTorrent(header.toByteArray(Charsets.ISO_8859_1)))
    }

    @Test
    fun `bencode dict without announce or info is rejected`() {
        val notATorrent = "d4:spam4:eggse"
        assertFalse(TorrentSniffer.looksLikeTorrent(notATorrent.toByteArray(Charsets.ISO_8859_1)))
    }

    @Test
    fun `HTML bytes are rejected`() {
        val html = "<!DOCTYPE html><html><head><title>Not a torrent</title></head></html>"
        assertFalse(TorrentSniffer.looksLikeTorrent(html.toByteArray()))
    }

    @Test
    fun `arbitrary binary bytes are rejected`() {
        val garbage = byteArrayOf(0x00, 0x01, 0x02, 0xFF.toByte(), 0x7F)
        assertFalse(TorrentSniffer.looksLikeTorrent(garbage))
    }

    @Test
    fun `empty byte array is rejected`() {
        assertFalse(TorrentSniffer.looksLikeTorrent(ByteArray(0)))
    }

    @Test
    fun `announce keyword beyond the 512-byte head window is not found`() {
        val padding = "0".repeat(600)
        val text = "d$padding" + "8:announce20:http://example.com/e"
        assertFalse(TorrentSniffer.looksLikeTorrent(text.toByteArray(Charsets.ISO_8859_1)))
    }

    @Test
    fun `4-colon info form is recognized`() {
        val text = "d4:infod4:name3:foo6:lengthi1e6:piecei0eee"
        assertTrue(TorrentSniffer.looksLikeTorrent(text.toByteArray(Charsets.ISO_8859_1)))
    }
}
