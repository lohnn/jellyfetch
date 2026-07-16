package se.lohnn.jellyfetch

import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.getValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import se.lohnn.jellyfetch.api.ApiClient
import se.lohnn.jellyfetch.share.CaughtInput
import se.lohnn.jellyfetch.share.IntentResolver
import se.lohnn.jellyfetch.share.LibraryPickerUi
import se.lohnn.jellyfetch.share.ShareConfirmScreen
import se.lohnn.jellyfetch.share.SharePickerViewModel
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
                // The picker VM owns the lazy library load + Auto-default selection.
                // We read its current selection at send time (Auto ⇒ null LibraryId).
                val picker: SharePickerViewModel = viewModel(factory = pickerFactory())
                val pickerState by picker.state.collectAsStateWithLifecycle()

                ShareConfirmScreen(
                    typeLabel = getString(typeLabelRes),
                    content = input.displayLabel,
                    picker = LibraryPickerUi(
                        selectionLabel = pickerState.selectionLabel,
                        libraries = pickerState.libraries,
                        isLoading = pickerState.isLoading,
                        loadError = pickerState.loadError,
                        onOpened = picker::onDropdownOpened,
                        onSelectAuto = picker::selectAuto,
                        onSelectLibrary = { picker.selectLibrary(it) },
                        onRetry = picker::retryLoad,
                    ),
                    onCancel = { finish() },
                    onSend = { dontAskAgain ->
                        if (dontAskAgain) prefs.sendWithoutConfirm = true
                        submit(input, picker.selectedLibraryId)
                    },
                )
            }
        }
    }

    private fun submit(input: CaughtInput, libraryId: String? = null) {
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
            is CaughtInput.UrlOrMagnet -> api.submitUrl(input.url, libraryId, onResult)
            is CaughtInput.Torrent -> api.submitTorrent(input.fileName, input.bytes, libraryId, onResult)
        }
    }

    private fun pickerFactory() = viewModelFactory {
        initializer {
            SharePickerViewModel(apiProvider = { ApiClient.current })
        }
    }
}
