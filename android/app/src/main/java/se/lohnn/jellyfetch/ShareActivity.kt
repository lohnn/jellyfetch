package se.lohnn.jellyfetch

import android.app.Activity
import android.os.Bundle
import android.widget.CheckBox
import android.widget.TextView
import android.widget.Toast
import se.lohnn.jellyfetch.api.ApiClient
import se.lohnn.jellyfetch.share.CaughtInput
import se.lohnn.jellyfetch.share.IntentResolver

/**
 * The app's front door (fast path over ceremony, per spec): catch whatever
 * the OS handed us, show a one-line summary, one confirm tap, POST, toast,
 * finish. Never claim success without a server round trip.
 */
class ShareActivity : Activity() {

    private lateinit var prefs: Prefs
    private var caught: CaughtInput? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = Prefs(this)

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
        setContentView(R.layout.activity_share)

        val typeLabel = findViewById<TextView>(R.id.share_type_label)
        val contentLabel = findViewById<TextView>(R.id.share_content_label)
        val dontAskCheckbox = findViewById<CheckBox>(R.id.share_dont_ask_checkbox)

        typeLabel.text = when (input) {
            is CaughtInput.UrlOrMagnet -> getString(R.string.share_type_url)
            is CaughtInput.Torrent -> getString(R.string.share_type_torrent)
        }
        contentLabel.text = input.displayLabel

        findViewById<android.view.View>(R.id.share_cancel_button).setOnClickListener {
            finish()
        }
        findViewById<android.view.View>(R.id.share_send_button).setOnClickListener {
            if (dontAskCheckbox.isChecked) {
                prefs.sendWithoutConfirm = true
            }
            submit(input)
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
