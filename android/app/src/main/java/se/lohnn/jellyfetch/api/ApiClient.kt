package se.lohnn.jellyfetch.api

import android.content.Context
import se.lohnn.jellyfetch.Prefs

/**
 * Single access point for the active [JellyFetchApi] implementation.
 *
 * Contract confirmed (jellyfin-plugin HIVEmind result + docs/api.md) and
 * [HttpJellyFetchApi] is now wired in: whenever a server URL is configured
 * ([Prefs.isConfigured]), [current] returns a real HTTP-backed client;
 * otherwise it falls back to [FakeJellyFetchApi] so the app remains usable
 * (and demoable) before the user has entered settings.
 */
object ApiClient {

    private lateinit var appContext: Context
    private val fake: JellyFetchApi by lazy { FakeJellyFetchApi() }

    private var httpCache: HttpJellyFetchApi? = null
    private var httpCacheKey: Pair<String, String>? = null

    fun init(context: Context) {
        appContext = context.applicationContext
    }

    val current: JellyFetchApi
        get() {
            val prefs = Prefs(appContext)
            if (!prefs.isConfigured) return fake

            val key = prefs.serverUrl to prefs.apiKey
            if (httpCacheKey != key) {
                httpCache?.close()
                httpCache = HttpJellyFetchApi(prefs.serverUrl, prefs.apiKey)
                httpCacheKey = key
            }
            return httpCache!!
        }
}
