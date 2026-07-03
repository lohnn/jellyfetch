package se.lohnn.jellyfetch.share

/**
 * MIME-type reality vs docs (dream residue candidate): file managers and
 * mobile browsers frequently deliver .torrent shares/opens as
 * application/octet-stream or omit a MIME type entirely, NOT the "correct"
 * application/x-bittorrent. The manifest already widens the intent filters
 * (MIME + filename-extension pathPattern fallback); this sniffer is the
 * last line of defense inside the app, confirming the bytes actually look
 * like a bencoded torrent dictionary before we let the user submit garbage.
 *
 * A .torrent file is a bencoded dictionary, which always starts with 'd'
 * (0x64) followed by bencoded key/value pairs. Real torrents contain the
 * ASCII substrings "announce" and/or "info" near the start. This is a
 * cheap heuristic, not a full bencode parser.
 */
object TorrentSniffer {

    fun looksLikeTorrent(bytes: ByteArray): Boolean {
        if (bytes.isEmpty() || bytes[0] != 'd'.code.toByte()) return false
        val headWindow = bytes.copyOfRange(0, minOf(bytes.size, 512))
        val text = String(headWindow, Charsets.ISO_8859_1)
        return text.contains("announce") || text.contains("4:info") || text.contains(":info")
    }
}
