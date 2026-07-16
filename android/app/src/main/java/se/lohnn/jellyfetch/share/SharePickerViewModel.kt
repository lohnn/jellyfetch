package se.lohnn.jellyfetch.share

import androidx.lifecycle.ViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import se.lohnn.jellyfetch.api.JellyFetchApi
import se.lohnn.jellyfetch.api.LibraryInfo

/**
 * The share-popup library picker's decision logic, extracted android.view-free
 * (I-159) so the load/select/confirm contract is JVM-unit-testable without a
 * device. The [JellyFetchApi] is injected as a closure ([apiProvider]) — same
 * seam as [se.lohnn.jellyfetch.correction.CorrectionViewModel].
 *
 * Load-bearing invariants (all covered by SharePickerViewModelTest):
 *
 *  • **Auto is the default** and is always available regardless of the library
 *    list. Auto ⇒ NO `LibraryId` is sent on submit (server classifies as today).
 *    Leaving the picker untouched preserves the exact one-tap flow.
 *  • **Lazy load**: the library list is fetched ONLY when [onDropdownOpened] is
 *    first called (the user opens the dropdown) — never on construction, never
 *    on every share. Subsequent opens do not re-fetch (unless a prior load
 *    failed and the user retries via [retryLoad]).
 *  • **Failure falls back to Auto-only** and never blocks sending (W-056: the
 *    failure is surfaced in state, not swallowed; Auto remains selectable and
 *    submit still works with no LibraryId).
 *  • **Explicit pick ⇒ that library's id is the LibraryId** on submit. Only
 *    placeable libraries can be selected; a non-placeable row can't be picked.
 *
 * The picker itself does NOT submit — it computes [selectedLibraryId] (null for
 * Auto) and the host ([se.lohnn.jellyfetch.ShareActivity]) passes it to
 * [JellyFetchApi.submitUrl] / [JellyFetchApi.submitTorrent].
 */
class SharePickerViewModel(
    private val apiProvider: () -> JellyFetchApi,
) : ViewModel() {

    private val api get() = apiProvider()

    private val _state = MutableStateFlow(SharePickerState())
    val state: StateFlow<SharePickerState> = _state.asStateFlow()

    /**
     * The value to send as `LibraryId` on submit: the selected library's id, or
     * `null` for Auto (or when the current selection is somehow not placeable —
     * defensive: Auto semantics rather than sending a bad id).
     */
    val selectedLibraryId: String?
        get() {
            val sel = _state.value.selection
            return if (sel is LibrarySelection.Explicit && sel.library.isPlaceable) sel.library.id else null
        }

    /**
     * Called the first time the user opens the dropdown. Triggers the LAZY
     * `GET /Jellyfetch/Libraries` exactly once; a no-op if a load already
     * succeeded or is in flight. (A prior FAILED load is retried via [retryLoad],
     * not by re-opening, so a broken server doesn't hammer on every open.)
     */
    fun onDropdownOpened() {
        val phase = _state.value.loadPhase
        if (phase == LoadPhase.Loading || phase is LoadPhase.Loaded) return
        load()
    }

    /** Explicit retry after a failed load (e.g. a "retry" affordance in the dropdown). */
    fun retryLoad() {
        if (_state.value.loadPhase == LoadPhase.Loading) return
        load()
    }

    private fun load() {
        _state.update { it.copy(loadPhase = LoadPhase.Loading) }
        api.listLibraries { result ->
            result.onSuccess { libraries ->
                _state.update { it.copy(loadPhase = LoadPhase.Loaded(libraries)) }
            }.onFailure { error ->
                // W-056: surface, never swallow. Auto stays selectable + submit works.
                _state.update { it.copy(loadPhase = LoadPhase.Failed(error.message ?: error.toString())) }
            }
        }
    }

    /** Reselect Auto (send no LibraryId). Always available. */
    fun selectAuto() {
        _state.update { it.copy(selection = LibrarySelection.Auto) }
    }

    /**
     * Pick a specific library. Ignored for non-placeable libraries (their rows
     * are disabled in the UI, but guard here too so the authority is the VM, not
     * the view). Returns true iff the selection was accepted.
     */
    fun selectLibrary(library: LibraryInfo): Boolean {
        if (!library.isPlaceable) return false
        _state.update { it.copy(selection = LibrarySelection.Explicit(library)) }
        return true
    }
}

/** Immutable UI state for the library picker. */
data class SharePickerState(
    val loadPhase: LoadPhase = LoadPhase.Idle,
    val selection: LibrarySelection = LibrarySelection.Auto,
) {
    /** Libraries to render as pickable rows (empty until a successful load). */
    val libraries: List<LibraryInfo>
        get() = (loadPhase as? LoadPhase.Loaded)?.libraries ?: emptyList()

    /** Human label for the currently-selected row (drives the collapsed dropdown text). */
    val selectionLabel: String
        get() = when (val s = selection) {
            is LibrarySelection.Auto -> "Auto"
            is LibrarySelection.Explicit -> s.library.name
        }

    val isLoading: Boolean get() = loadPhase == LoadPhase.Loading
    val loadError: String? get() = (loadPhase as? LoadPhase.Failed)?.message
}

/** Lazy-load lifecycle of the library list. */
sealed interface LoadPhase {
    /** Not fetched yet — the pre-open state (lazy: no request has fired). */
    data object Idle : LoadPhase
    data object Loading : LoadPhase
    data class Loaded(val libraries: List<LibraryInfo>) : LoadPhase
    /** Load failed; UI falls back to Auto-only and offers a retry. */
    data class Failed(val message: String) : LoadPhase
}

/** What the user has chosen. Auto ⇒ no LibraryId; Explicit ⇒ that library's id. */
sealed interface LibrarySelection {
    data object Auto : LibrarySelection
    data class Explicit(val library: LibraryInfo) : LibrarySelection
}
