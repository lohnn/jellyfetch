package se.lohnn.jellyfetch.settings

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import se.lohnn.jellyfetch.api.JellyFetchApi

/**
 * The connection-test result surfaced under the "Test connection" button.
 * Modeled as explicit states so the composable renders each deterministically
 * (and the @Preview harness can force each one) rather than juggling a mutable
 * TextView + visibility flag as the classic Activity did.
 */
sealed interface TestStatus {
    data object Idle : TestStatus
    data object Testing : TestStatus
    data object NeedUrl : TestStatus
    data object Ok : TestStatus
    data class Failed(val message: String) : TestStatus
}

/** The Settings form's live UI state — a pure value, no android.* dependency. */
data class SettingsState(
    val serverUrl: String = "",
    val apiKey: String = "",
    val sendWithoutConfirm: Boolean = false,
    val testStatus: TestStatus = TestStatus.Idle,
)

/**
 * State-holder for the Settings screen. Deliberately android.view-free (I-127):
 * persistence is injected as plain load/save closures over [se.lohnn.jellyfetch.Prefs],
 * and the reachability probe as a [JellyFetchApi] provider — so the whole reducer
 * (form edits, the test-connection decision, the not-configured guard) is
 * exercisable in a JVM unit test with fakes.
 *
 * ⚠ I-080 (string-set persistence): Settings does NOT currently persist a
 * Set<String> preference. IF one is ever added here (e.g. a
 * dismissed-server-warnings set), the load closure must hand this ViewModel a
 * DEFENSIVE COPY (`prefs.getStringSet(k, emptySet())!!.toSet()`) and the save
 * closure must write a BRAND-NEW collection (`putStringSet(k, HashSet(value))`) —
 * never the live reference SharedPreferences returns, which corrupts persistence
 * when mutated in place. See [se.lohnn.jellyfetch.Prefs] for the established
 * pattern. This ViewModel holds its own immutable [SettingsState] copies, so as
 * long as the Prefs seam obeys that rule the corruption class can't reach here.
 */
class SettingsViewModel(
    initial: SettingsState,
    private val persist: (SettingsState) -> Unit,
    private val apiProvider: () -> JellyFetchApi,
    private val isConfigured: (SettingsState) -> Boolean,
) : ViewModel() {

    private val _state = MutableStateFlow(initial)
    val state: StateFlow<SettingsState> = _state.asStateFlow()

    fun onServerUrlChange(value: String) {
        _state.update { it.copy(serverUrl = value, testStatus = TestStatus.Idle) }
    }

    fun onApiKeyChange(value: String) {
        _state.update { it.copy(apiKey = value, testStatus = TestStatus.Idle) }
    }

    fun onSendWithoutConfirmChange(value: Boolean) {
        _state.update { it.copy(sendWithoutConfirm = value) }
        // Toggles persist immediately (a checkbox has no "save" affordance).
        save()
    }

    /** Persist the current form. Called on toggle, on test, and on screen leave. */
    fun save() {
        persist(_state.value)
    }

    /**
     * Persist first (so the probe hits the just-entered URL/key), then test.
     * Mirrors the classic SettingsActivity.testConnection ordering exactly.
     */
    fun testConnection() {
        save()
        val current = _state.value
        if (!isConfigured(current)) {
            _state.update { it.copy(testStatus = TestStatus.NeedUrl) }
            return
        }
        _state.update { it.copy(testStatus = TestStatus.Testing) }
        apiProvider().testConnection { result ->
            result.onSuccess {
                _state.update { it.copy(testStatus = TestStatus.Ok) }
            }.onFailure { error ->
                _state.update {
                    it.copy(testStatus = TestStatus.Failed(error.message ?: error.toString()))
                }
            }
        }
    }

    companion object {
        /**
         * Binds the real [se.lohnn.jellyfetch.Prefs] + [se.lohnn.jellyfetch.api.ApiClient],
         * but LAZILY (I-127): the initial state is read from Prefs at factory time,
         * the persist closure writes back, and the api provider is a `() -> ...`
         * closure so nothing touches the ApiClient singleton until Test is tapped.
         */
        val Factory = viewModelFactory {
            initializer {
                val app = this[androidx.lifecycle.ViewModelProvider.AndroidViewModelFactory.APPLICATION_KEY]!!
                val prefs = se.lohnn.jellyfetch.Prefs(app)
                SettingsViewModel(
                    initial = SettingsState(
                        serverUrl = prefs.serverUrl,
                        apiKey = prefs.apiKey,
                        sendWithoutConfirm = prefs.sendWithoutConfirm,
                    ),
                    persist = { s ->
                        prefs.serverUrl = s.serverUrl
                        prefs.apiKey = s.apiKey
                        prefs.sendWithoutConfirm = s.sendWithoutConfirm
                    },
                    apiProvider = { se.lohnn.jellyfetch.api.ApiClient.current },
                    // Mirror Prefs.isConfigured: a non-blank server URL.
                    isConfigured = { it.serverUrl.isNotBlank() },
                )
            }
        }
    }
}
