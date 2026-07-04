package se.lohnn.jellyfetch.share

import android.content.ContentResolver
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.OpenableColumns
import java.util.concurrent.Executors

/**
 * Turns a caught Intent into a [CaughtInput], off the main thread — content
 * URI reads must never assume a filesystem path (many senders, especially
 * cloud file managers and browsers' "Downloads" surfaces, hand back a
 * content:// URI backed by no real file at all). Always go through
 * ContentResolver.openInputStream().
 */
object IntentResolver {

    private val executor = Executors.newSingleThreadExecutor()

    // Lazy (not eager): a plain JVM unit test that calls resolveBlocking()
    // directly (mocking the ContentResolver/Intent seam) never touches this
    // property, so it never needs a real Looper. Touched only from resolve(),
    // i.e. real app usage on a real main thread.
    private val mainHandler by lazy { Handler(Looper.getMainLooper()) }

    fun resolve(context: Context, intent: Intent, callback: (CaughtInput?) -> Unit) {
        val appContext = context.applicationContext
        executor.submit {
            val result = try {
                resolveBlocking(appContext.contentResolver, intent)
            } catch (t: Throwable) {
                null
            }
            mainHandler.post { callback(result) }
        }
    }

    // internal, not private: lets JVM unit tests exercise the pure decision
    // logic directly (mocking ContentResolver/Intent/Uri) without going
    // through the executor/Handler plumbing above — see share/ tests.
    internal fun resolveBlocking(resolver: ContentResolver, intent: Intent): CaughtInput? {
        return when (intent.action) {
            Intent.ACTION_SEND -> {
                if (intent.type == "text/plain") {
                    val text = intent.getStringExtra(Intent.EXTRA_TEXT)
                    UrlExtractor.extractFirstUrl(text)?.let { CaughtInput.UrlOrMagnet(it) }
                } else {
                    @Suppress("DEPRECATION")
                    val streamUri = intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)
                    streamUri?.let { readTorrent(resolver, it) }
                }
            }

            Intent.ACTION_VIEW -> {
                val data = intent.data ?: return null
                if (data.scheme.equals("magnet", ignoreCase = true)) {
                    CaughtInput.UrlOrMagnet(data.toString())
                } else {
                    readTorrent(resolver, data)
                }
            }

            else -> null
        }
    }

    private fun readTorrent(resolver: ContentResolver, uri: Uri): CaughtInput.Torrent? {
        val bytes = resolver.openInputStream(uri)?.use { it.readBytes() } ?: return null
        if (bytes.isEmpty()) return null

        val fileName = queryDisplayName(resolver, uri) ?: uri.lastPathSegment ?: "download.torrent"

        // Be lenient: accept if it either sniffs as bencode OR the filename
        // says .torrent (sniffing catches mislabeled MIME, filename catches
        // the rare torrent that doesn't sniff cleanly through a proxy/CDN).
        val looksRight = TorrentSniffer.looksLikeTorrent(bytes) ||
            fileName.endsWith(".torrent", ignoreCase = true)
        if (!looksRight) return null

        val safeName = if (fileName.endsWith(".torrent", ignoreCase = true)) fileName else "$fileName.torrent"
        return CaughtInput.Torrent(safeName, bytes)
    }

    private fun queryDisplayName(resolver: ContentResolver, uri: Uri): String? {
        if (uri.scheme != "content") return null
        return resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) {
                val idx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (idx >= 0) cursor.getString(idx) else null
            } else {
                null
            }
        }
    }
}
