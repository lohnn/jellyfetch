package se.lohnn.jellyfetch.correction

import androidx.compose.runtime.Composable
import androidx.compose.ui.tooling.preview.Preview
import com.android.tools.screenshot.PreviewTest
import se.lohnn.jellyfetch.api.ConvertTarget
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemType
import se.lohnn.jellyfetch.api.RemoteSearchCandidate
import se.lohnn.jellyfetch.ui.theme.JellyFetchTheme

/**
 * Screenshot-test previews for the correction picker. Renders the stateless
 * [CorrectionSheet] over the states that matter visually AND encode the
 * load-bearing guards: a candidate list, a mid-convert (destructive control
 * disabled, W-066 latch), and the locked-down 409-stale case.
 */
private val sampleItem = LibraryItem(
    id = "42", name = "The Matrix", year = 1999, type = LibraryItemType.MOVIE,
    providerIds = mapOf("Tmdb" to "603"),
)

private val sampleCandidates = listOf(
    RemoteSearchCandidate(
        name = "The Matrix", year = 1999,
        overview = "A computer hacker learns the true nature of his reality.",
        providerIds = mapOf("Tmdb" to "603"),
    ),
    RemoteSearchCandidate(
        name = "The Matrix Reloaded", year = 2003,
        overview = "Neo and the rebel leaders estimate they have 72 hours.",
        providerIds = mapOf("Tmdb" to "604"),
    ),
)

private fun baseState() = CorrectionState(
    item = sampleItem,
    searchPrefill = sampleItem.name,
    typeConvertVisible = true,
    currentTarget = ConvertTarget.MOVIE,
)

@Composable
private fun PreviewCorrection(state: CorrectionState, dark: Boolean) {
    JellyFetchTheme(darkTheme = dark) {
        CorrectionSheet(
            state = state,
            onDismiss = {},
            onSearch = {},
            onApplyCandidate = {},
            onOpenTmdb = {},
            onApplyPastedId = {},
            onSelectTarget = {},
            onConvert = {},
        )
    }
}

@PreviewTest
@Preview(name = "Correction · candidates · light", widthDp = 400, heightDp = 780)
@Composable
fun CorrectionCandidatesLight() =
    PreviewCorrection(baseState().copy(candidates = sampleCandidates), dark = false)

@PreviewTest
@Preview(name = "Correction · candidates · dark", widthDp = 400, heightDp = 780)
@Composable
fun CorrectionCandidatesDark() =
    PreviewCorrection(baseState().copy(candidates = sampleCandidates), dark = true)

@PreviewTest
@Preview(name = "Correction · converting (Series) · light", widthDp = 400, heightDp = 780)
@Composable
fun CorrectionConvertingLight() =
    PreviewCorrection(
        baseState().copy(
            selectedTarget = ConvertTarget.SERIES,
            converting = true,
            convertFired = true,
            convertPhase = ConvertPhase.Converting(ConvertTarget.SERIES),
        ),
        dark = false,
    )

@PreviewTest
@Preview(name = "Correction · locked down (409 stale) · dark", widthDp = 400, heightDp = 780)
@Composable
fun CorrectionLockedDownDark() =
    PreviewCorrection(
        baseState().copy(
            convertFired = true,
            lockedDown = true,
            convertPhase = ConvertPhase.Failed("This item was already converted (409 Superseded). Pull to refresh."),
        ),
        dark = true,
    )
