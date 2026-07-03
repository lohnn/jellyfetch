package se.lohnn.jellyfetch

import android.content.Context
import android.content.SharedPreferences

/**
 * Thin wrapper over SharedPreferences.
 *
 * I-080 (0.9): getStringSet() returns a LIVE reference into the preference's
 * internal storage — mutating it in place silently corrupts persistence
 * until the next process restart re-reads from disk. This app doesn't
 * currently store a Set<String> preference, but if one is added later
 * (e.g. a locally-dismissed-job-ids cache), follow the pattern already
 * established here: read with HashSet(prefs.getStringSet(key, emptySet())!!)
 * to defensively copy, and write back a brand-new collection via
 * putStringSet(key, newSet) — never mutate the returned set directly.
 */
class Prefs(context: Context) {

    private val sp: SharedPreferences =
        context.getSharedPreferences("jellyfetch_prefs", Context.MODE_PRIVATE)

    var serverUrl: String
        get() = sp.getString(KEY_SERVER_URL, "") ?: ""
        set(value) = sp.edit().putString(KEY_SERVER_URL, value.trim()).apply()

    var apiKey: String
        get() = sp.getString(KEY_API_KEY, "") ?: ""
        set(value) = sp.edit().putString(KEY_API_KEY, value.trim()).apply()

    /** "Don't ask, just send" — skip the ShareActivity confirm tap. */
    var sendWithoutConfirm: Boolean
        get() = sp.getBoolean(KEY_SEND_WITHOUT_CONFIRM, false)
        set(value) = sp.edit().putBoolean(KEY_SEND_WITHOUT_CONFIRM, value).apply()

    val isConfigured: Boolean
        get() = serverUrl.isNotBlank()

    companion object {
        private const val KEY_SERVER_URL = "server_url"
        private const val KEY_API_KEY = "api_key"
        private const val KEY_SEND_WITHOUT_CONFIRM = "send_without_confirm"
    }
}
