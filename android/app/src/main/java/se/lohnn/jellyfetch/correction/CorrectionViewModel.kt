package se.lohnn.jellyfetch.correction

import androidx.lifecycle.ViewModel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.update
import se.lohnn.jellyfetch.api.ConvertTarget
import se.lohnn.jellyfetch.api.ConvertTypeResult
import se.lohnn.jellyfetch.api.JellyFetchApi
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemType
import se.lohnn.jellyfetch.api.RemoteSearchCandidate

/**
 * State-holder for the metadata correction picker (PASS 2 — extracted from the
 * classic [se.lohnn.jellyfetch.CorrectionDialog] so the BUSINESS CONTRACT is
 * exercisable in JVM unit tests, not buried in dialog callbacks).
 *
 * ⚠ This is NOT just a UI shell (I-152/I-153/W-066/SNG-040). It drives Jellyfin
 * RemoteSearch / Apply / convert-rebind, and the following invariants are LOAD-
 * BEARING and preserved verbatim from the classic dialog:
 *
 *  • Apply (by-result / by-provider) is SYNCHRONOUS server-side — success means
 *    "refreshed"; we fire [onApplied] so the CALLER re-fetches (no fake poll here).
 *  • Convert (type change) is ASYNC. The instant it fires, the server moves files
 *    and the old id is DEAD. We latch [convertFired] immediately and NEVER reset
 *    it within this instance — the destructive control is disabled the moment it
 *    fires (the client-side latch, W-066 / the live "no video files" fix).
 *  • After a convert we rebind by a STABLE key: the file PATH
 *    ([ConvertTypeResult.rebindPath] → getItemByPath), NEVER the title (drifts on
 *    re-fetch). 404/not-indexed-yet (success+null) = "poll again", not an error.
 *  • A convert leaves a dangling old id + emptied source container — the 409
 *    Superseded / stale case surfaces verbatim (W-056) and locks the dialog down
 *    ([lockedDown]) so no control can act on the dead id.
 *  • W-065: we do NOT trust the dialog's own state as source of truth — a
 *    successful convert hands the CALLER the freshly-resolved item (or null →
 *    re-fetch), so the display rebinds to server truth.
 *
 * SHADOW-010: this whole arc has NEVER been verified against a live Jellyfin
 * server; on-device convert/picker rendering is UNPROVEN. A green build is not
 * conclusive here.
 *
 * android.view-free (I-127): the [JellyFetchApi] is injected and a [schedule]
 * closure abstracts the poll delay, so the poll loop is testable without a real
 * Handler/Looper. The production Activity passes a main-thread postDelayed.
 */
class CorrectionViewModel(
    private val item: LibraryItem,
    private val apiProvider: () -> JellyFetchApi,
    /**
     * Schedule [block] to run after [delayMs]. Production passes a main-thread
     * Handler.postDelayed; tests pass an immediate/queued executor so the poll
     * loop runs deterministically.
     */
    private val schedule: (delayMs: Long, block: () -> Unit) -> Unit,
    private val pollIntervalMs: Long = DEFAULT_POLL_INTERVAL_MS,
    private val pollMaxAttempts: Int = DEFAULT_POLL_MAX_ATTEMPTS,
) : ViewModel() {

    private val api get() = apiProvider()

    /** RemoteSearch is keyed by the target item's Movie/Series kind. */
    private val searchType: LibraryItemType = item.type ?: LibraryItemType.MOVIE

    private val _state = MutableStateFlow(
        CorrectionState(
            item = item,
            searchPrefill = item.name,
            typeConvertVisible = item.type == LibraryItemType.MOVIE || item.type == LibraryItemType.SERIES,
            currentTarget = item.type?.let { ConvertTarget.of(it) } ?: ConvertTarget.MOVIE,
        ),
    )
    val state: StateFlow<CorrectionState> = _state.asStateFlow()

    /**
     * One-shot outcomes for the host to surface (snackbar/toast) — never silently
     * discard an apply/convert Result (W-056). Buffered so an event fired before a
     * collector attaches isn't lost.
     */
    private val _events = MutableSharedFlow<CorrectionEvent>(
        replay = 0,
        extraBufferCapacity = 8,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    val events: SharedFlow<CorrectionEvent> = _events.asSharedFlow()

    // --- Native remote search -------------------------------------------------

    fun search(rawName: String) {
        if (_state.value.lockedDown) return
        val name = rawName.trim()
        if (name.isEmpty()) return
        _state.update { it.copy(searchPhase = SearchPhase.Searching, candidates = emptyList()) }
        api.searchRemoteMetadata(item.id, searchType, name, item.year) { result ->
            result.onSuccess { candidates ->
                _state.update {
                    it.copy(
                        searchPhase = if (candidates.isEmpty()) SearchPhase.NoResults else SearchPhase.Idle,
                        candidates = candidates,
                    )
                }
            }.onFailure { error ->
                _state.update { it.copy(searchPhase = SearchPhase.Failed(reason(error)), candidates = emptyList()) }
            }
        }
    }

    fun applyCandidate(candidate: RemoteSearchCandidate) {
        if (_state.value.lockedDown) return
        _state.update { it.copy(applyPhase = ApplyPhase.Applying) }
        api.applyCorrectionByResult(item.id, candidate) { result -> handleApply(result) }
    }

    /**
     * The TMDb browser-fallback path: accept a bare id or a full URL and let the
     * server reject garbage with a clear message (W-056). ID extraction is the
     * pure/testable [extractTmdbId].
     */
    fun applyPastedProviderId(pasted: String): Boolean {
        if (_state.value.lockedDown) return false
        if (pasted.trim().isEmpty()) return false
        val providerId = extractTmdbId(pasted)
        _state.update { it.copy(applyPhase = ApplyPhase.Applying) }
        api.applyCorrectionByProvider(item.id, searchType, PROVIDER_TMDB, providerId) { result ->
            handleApply(result)
        }
        return true
    }

    private fun handleApply(result: Result<Unit>) {
        result.onSuccess {
            _state.update { it.copy(applyPhase = ApplyPhase.Idle) }
            _events.tryEmit(CorrectionEvent.Applied)
            // Apply is synchronous server-side; the caller re-fetches to DISPLAY the
            // new match (W-064) — we hand back null (same still-valid item id).
            _events.tryEmit(CorrectionEvent.Dismiss(refreshed = null))
        }.onFailure { error ->
            // Surface, don't swallow (W-056).
            _state.update { it.copy(applyPhase = ApplyPhase.Failed(reason(error))) }
        }
    }

    // --- Type convert (Movie / Series / Other) --------------------------------

    fun selectTarget(target: ConvertTarget) {
        _state.update { it.copy(selectedTarget = target) }
    }

    /**
     * Fire a convert. The convert-enabled guard (different target AND not already
     * converting AND not already fired) is enforced here, not just in the UI —
     * [CorrectionState.convertEnabled] mirrors it for the button, but this method
     * is the authority so a stale tap can't slip through (W-066).
     */
    fun convert(target: ConvertTarget) {
        val s = _state.value
        if (!s.convertEnabled || target == s.currentTarget) return

        // Latch the anti-double-convert guard SYNCHRONOUSLY, before the request:
        // the moment we fire, the server moves the files and item.id is dead. This
        // latch is never reset within this instance (W-066 — the live fix).
        _state.update {
            it.copy(
                convertFired = true,
                converting = true,
                convertPhase = ConvertPhase.Converting(target),
            )
        }

        api.convertType(item.id, target) { result ->
            result.onSuccess { convertResult ->
                pollForConvertedItem(convertResult, attemptsLeft = pollMaxAttempts)
            }.onFailure { error ->
                // W-056: surface verbatim (this is where the OTHER not-distinct 400
                // AND the 409 stale/superseded guard land). Do NOT re-enable convert —
                // the id is spent; lock the whole dialog down so no control touches the
                // dead id, and direct the user to refresh.
                _state.update {
                    it.copy(
                        converting = false,
                        convertPhase = ConvertPhase.Failed(reason(error)),
                        lockedDown = true,
                    )
                }
                _events.tryEmit(CorrectionEvent.ConvertFailed(reason(error)))
            }
        }
    }

    /**
     * Resolve the newly re-typed item by its NEW FILE PATH — the deterministic
     * rebind (never by title, which drifts). [ConvertTypeResult.rebindPath] is
     * ItemDirectory-preferred, MovedPaths-fallback. getItemByPath: success+null =
     * "not indexed yet, poll again"; failure = a real transport error.
     */
    private fun pollForConvertedItem(convertResult: ConvertTypeResult, attemptsLeft: Int) {
        val target = convertResult.targetType
        val rebindPath = convertResult.rebindPath

        // Tolerant-of-absence: no path to rebind by → tell the caller to re-fetch.
        if (rebindPath == null) {
            _state.update { it.copy(converting = false, convertPhase = ConvertPhase.Pending(target)) }
            _events.tryEmit(CorrectionEvent.ConvertPending(target))
            _events.tryEmit(CorrectionEvent.Dismiss(refreshed = null))
            return
        }

        api.getItemByPath(rebindPath) { result ->
            val newItem = result.getOrNull()
            when {
                newItem != null -> {
                    _state.update { it.copy(converting = false, convertPhase = ConvertPhase.Found(target, newItem)) }
                    _events.tryEmit(CorrectionEvent.ConvertFound(target))
                    // Hand the CALLER the fresh item so it rebinds to the correct new
                    // type (W-065 — server truth, not our stale display).
                    _events.tryEmit(CorrectionEvent.Dismiss(refreshed = newItem))
                    _events.tryEmit(CorrectionEvent.OfferPickerOnNewItem(newItem))
                }
                attemptsLeft <= 1 -> {
                    // Timed out waiting for the rescan — honest, don't fake completion.
                    _state.update { it.copy(converting = false, convertPhase = ConvertPhase.Pending(target)) }
                    _events.tryEmit(CorrectionEvent.ConvertPending(target))
                    _events.tryEmit(CorrectionEvent.Dismiss(refreshed = null))
                }
                else -> {
                    // Not indexed yet (or transient) — keep "converting…" and retry.
                    schedule(pollIntervalMs) {
                        pollForConvertedItem(convertResult, attemptsLeft - 1)
                    }
                }
            }
        }
    }

    /** The TMDb search URL for the browser-fallback (opened by the host Activity). */
    fun tmdbSearchUrl(rawQuery: String): String {
        val query = rawQuery.trim().ifEmpty { item.name }
        val typePath = if (searchType == LibraryItemType.SERIES) "tv" else "movie"
        return "https://www.themoviedb.org/search/$typePath?query=" + android.net.Uri.encode(query)
    }

    private fun reason(e: Throwable) = e.message ?: e.toString()

    companion object {
        const val PROVIDER_TMDB = "Tmdb"
        private const val DEFAULT_POLL_INTERVAL_MS = 2000L
        private const val DEFAULT_POLL_MAX_ATTEMPTS = 8

        /**
         * Extract a TMDb numeric id from either a bare id ("603") or a pasted URL
         * (`themoviedb.org/movie/603-the-matrix`, `/tv/1399`). Pure/testable:
         * prefers the /movie/ or /tv/ id segment, else the first run of digits,
         * else the trimmed input unchanged (letting the server reject it clearly).
         *
         * Preserved verbatim from the classic CorrectionDialog so CorrectionLogicTest
         * keeps passing against the same contract.
         */
        fun extractTmdbId(pasted: String): String {
            val trimmed = pasted.trim()
            val urlMatch = Regex("/(?:movie|tv)/(\\d+)").find(trimmed)
            if (urlMatch != null) return urlMatch.groupValues[1]
            val digits = Regex("\\d+").find(trimmed)
            return digits?.value ?: trimmed
        }
    }
}
