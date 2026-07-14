package se.lohnn.jellyfetch

import android.graphics.BitmapFactory
import android.os.Handler
import android.os.Looper
import android.view.View
import android.widget.ImageView
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Executors

/**
 * Tiny hand-rolled async poster loader — no image library (I-082 minimal
 * footprint: no Glide/Coil). Loads a bitmap off-thread via HttpURLConnection +
 * BitmapFactory and sets it on the main thread, with a tag-based staleness guard
 * so a recycled row (ListView convertView) never shows the wrong poster.
 *
 * Deliberately cache-free and best-effort: a failed/absent load just leaves the
 * placeholder visible (the [ImageView]'s framed background), never crashes. This
 * is display polish, not load-bearing — the correction flow works without it.
 */
object PosterLoader {

    private val executor = Executors.newFixedThreadPool(3)
    private val main = Handler(Looper.getMainLooper())

    /**
     * Load [url] into [image]. [placeholder] (when given) is shown while loading
     * and restored on failure. Passing a null/blank [url] just shows the
     * placeholder and skips the network entirely.
     */
    fun load(url: String?, image: ImageView, placeholder: View? = null) {
        // Staleness guard: stamp the target url on the view; when the async result
        // returns, only apply it if the view still wants this url (ListView reuse).
        image.setTag(R.id.poster_url_tag, url)
        image.setImageDrawable(null)
        placeholder?.visibility = View.VISIBLE

        if (url.isNullOrBlank()) return

        executor.submit {
            val bitmap = try {
                val conn = URL(url).openConnection() as HttpURLConnection
                conn.connectTimeout = 8_000
                conn.readTimeout = 12_000
                try {
                    if (conn.responseCode in 200..299) {
                        conn.inputStream.use { BitmapFactory.decodeStream(it) }
                    } else {
                        null
                    }
                } finally {
                    conn.disconnect()
                }
            } catch (_: Exception) {
                null
            }

            main.post {
                // Only apply if this view is still expecting this exact url.
                if (image.getTag(R.id.poster_url_tag) != url) return@post
                if (bitmap != null) {
                    image.setImageBitmap(bitmap)
                    placeholder?.visibility = View.GONE
                } else {
                    placeholder?.visibility = View.VISIBLE
                }
            }
        }
    }
}
