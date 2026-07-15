package se.lohnn.jellyfetch.correction

import se.lohnn.jellyfetch.api.ConvertTarget
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.RemoteSearchCandidate

/** Native-remote-search phase. */
sealed interface SearchPhase {
    data object Idle : SearchPhase
    data object Searching : SearchPhase
    data object NoResults : SearchPhase
    data class Failed(val message: String) : SearchPhase
}

/** Provider-apply phase (by-result or by-provider-id). */
sealed interface ApplyPhase {
    data object Idle : ApplyPhase
    data object Applying : ApplyPhase
    data class Failed(val message: String) : ApplyPhase
}

/** Type-convert phase. */
sealed interface ConvertPhase {
    data object Idle : ConvertPhase
    data class Converting(val target: ConvertTarget) : ConvertPhase
    data class Found(val target: ConvertTarget, val newItem: LibraryItem) : ConvertPhase
    data class Pending(val target: ConvertTarget) : ConvertPhase
    data class Failed(val message: String) : ConvertPhase
}

/**
 * The correction picker's explicit UI state — a pure value with no android.*
 * dependency, so the whole reducer in [CorrectionViewModel] is JVM-testable.
 *
 * The load-bearing guards are [convertFired] / [converting] / [lockedDown] (the
 * W-066 anti-double-convert latch + post-failure lockdown): once a convert fires
 * the destructive control disables and — on a stale/409 failure — every control
 * locks so nothing can act on the now-dead item id.
 */
data class CorrectionState(
    val item: LibraryItem,
    val searchPrefill: String,
    val typeConvertVisible: Boolean,
    /** The item's current type as a convert target — the "no-op" target. */
    val currentTarget: ConvertTarget,
    /** The radio selection; convert is meaningful only when it differs from [currentTarget]. */
    val selectedTarget: ConvertTarget = currentTarget,
    val candidates: List<RemoteSearchCandidate> = emptyList(),
    val searchPhase: SearchPhase = SearchPhase.Idle,
    val applyPhase: ApplyPhase = ApplyPhase.Idle,
    val convertPhase: ConvertPhase = ConvertPhase.Idle,
    /** True once a convert has FIRED — this item id is dead; never reset (W-066). */
    val convertFired: Boolean = false,
    /** True while a convert's rescan poll is in flight — guards against double-fire. */
    val converting: Boolean = false,
    /**
     * True after a convert failure/stale (409) — the whole picker is inert so no
     * control can act on the dead id. The user is directed to refresh.
     */
    val lockedDown: Boolean = false,
) {
    /**
     * Whether the Convert button should be enabled: a real type change, not
     * mid-convert, not already fired, not locked down. Mirrors the authority guard
     * in [CorrectionViewModel.convert] so the UI can't offer a tap the VM rejects.
     */
    val convertEnabled: Boolean
        get() = selectedTarget != currentTarget && !converting && !convertFired && !lockedDown
}

/**
 * One-shot outcomes the host surfaces (snackbar/toast) and acts on. [Dismiss]
 * carries the freshly-resolved item after a convert (or null → the caller
 * re-fetches), mirroring the classic `onApplied(refreshed)` contract exactly.
 */
sealed interface CorrectionEvent {
    data object Applied : CorrectionEvent
    data class ConvertFound(val target: ConvertTarget) : CorrectionEvent
    data class ConvertPending(val target: ConvertTarget) : CorrectionEvent
    data class ConvertFailed(val message: String) : CorrectionEvent
    /** Close the picker; [refreshed] is the new item to rebind to, or null to re-fetch. */
    data class Dismiss(val refreshed: LibraryItem?) : CorrectionEvent
    /** After a successful convert, offer to reopen the picker on the fresh item. */
    data class OfferPickerOnNewItem(val newItem: LibraryItem) : CorrectionEvent
}
