package se.lohnn.jellyfetch

import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import se.lohnn.jellyfetch.api.ApiClient
import se.lohnn.jellyfetch.share.CaughtInput
import se.lohnn.jellyfetch.share.IntentResolver
import se.lohnn.jellyfetch.share.ShareConfirmScreen
import se.lohnn.jellyfetch.ui.theme.JellyFetchTheme

/**
 * The app's front door (fast path over ceremony, per spec): catch whatever the OS
 * handed us, show a one-line summary, one confirm tap, POST, toast, finish. Never
 * claim success without a server round trip.
 *
 * ⚠ PASS 2 migrated ONLY the confirm UI to Compose ([ShareConfirmScreen]). The
 * intent-resolution logic (I-099) — which intent-filter matched, prose→URL
 * extraction, content-URI byte reading, torrent sniffing — is UNCHANGED and still
 * lives in [IntentResolver] + [CaughtInput]. Intent resolution is NOT
 * build-verifiable; it needs on-device sender-app testing (share from SVT Play /
 * YouTube / a browser, tap a magnet link, open a .torrent).
 */
class ShareActivity : ComponentActivity() {

    private lateinit var prefs: Prefs
    private var caught: CaughtInput? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = Prefs(this)

        // Intent logic carried over intact — IntentResolver reads the content URI
        // bytes / extracts the URL from prose off the main thread and calls back.
        IntentResolver.resolve(this, intent) { input ->
            caught = input
            if (input == null) {
                Toast.makeText(this, R.string.share_nothing_found, Toast.LENGTH_LONG).show()
                finish()
                return@resolve
            }
            if (prefs.sendWithoutConfirm) {
                submit(input)
            } else {
                showConfirmUi(input)
            }
        }
    }

    private fun showConfirmUi(input: CaughtInput) {
        val typeLabelRes = when (input) {
            is CaughtInput.UrlOrMagnet -> R.string.share_type_url
            is CaughtInput.Torrent -> R.string.share_type_torrent
        }
        setContent {
            JellyFetchTheme {
                ShareConfirmScreen(
                    typeLabel = getString(typeLabelRes),
                    content = input.displayLabel,
                    onCancel = { finish() },
                    onSend = { dontAskAgain ->
                        if (dontAskAgain) prefs.sendWithoutConfirm = true
                        submit(input)
                    },
                )
            }
        }
    }

    private fun submit(input: CaughtInput) {
        if (!prefs.isConfigured) {
            Toast.makeText(this, R.string.share_not_configured, Toast.LENGTH_LONG).show()
            finish()
            return
        }

        val api = ApiClient.current
        val onResult: (Result<String>) -> Unit = { result ->
            result.onSuccess {
                Toast.makeText(this, R.string.share_sent, Toast.LENGTH_SHORT).show()
            }.onFailure { error ->
                Toast.makeText(
                    this,
                    getString(R.string.share_failed, error.message ?: error.toString()),
                    Toast.LENGTH_LONG,
                ).show()
            }
            finish()
        }

        when (input) {
            is CaughtInput.UrlOrMagnet -> api.submitUrl(input.url, onResult)
            is CaughtInput.Torrent -> api.submitTorrent(input.fileName, input.bytes, onResult)
        }
    }
}
