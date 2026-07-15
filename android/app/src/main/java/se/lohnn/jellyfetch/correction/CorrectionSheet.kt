package se.lohnn.jellyfetch.correction

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import se.lohnn.jellyfetch.R
import se.lohnn.jellyfetch.api.ConvertTarget
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.RemoteSearchCandidate
import se.lohnn.jellyfetch.ui.Poster
import se.lohnn.jellyfetch.ui.theme.Dimens
import se.lohnn.jellyfetch.ui.theme.JfTheme

/**
 * The metadata correction picker (PASS 2 — Compose port of the classic
 * [se.lohnn.jellyfetch.CorrectionDialog] + dialog_correction.xml). Rendered in a
 * [Dialog]; all the load-bearing behavior lives in [CorrectionViewModel] /
 * [CorrectionState] (see those files for the W-066 latch, rebind-by-path, 409
 * lockdown contract). This composable is the stateless view over that state.
 *
 * A convert confirmation is an in-dialog two-step (an inline confirm row), keeping
 * the destructive action one deliberate extra tap away — matching the classic
 * AlertDialog.confirmConvert.
 */
@Composable
fun CorrectionSheet(
    state: CorrectionState,
    onDismiss: () -> Unit,
    onSearch: (String) -> Unit,
    onApplyCandidate: (RemoteSearchCandidate) -> Unit,
    onOpenTmdb: (query: String) -> Unit,
    onApplyPastedId: (String) -> Unit,
    onSelectTarget: (ConvertTarget) -> Unit,
    onConvert: (ConvertTarget) -> Unit,
) {
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Surface(
            color = MaterialTheme.colorScheme.surface,
            modifier = Modifier
                .fillMaxWidth(0.94f)
                .fillMaxHeight(0.9f),
        ) {
            Column(
                Modifier
                    .verticalScroll(rememberScrollState())
                    .padding(Dimens.screenPadding),
            ) {
                Text(
                    text = stringResource(R.string.correction_title),
                    style = MaterialTheme.typography.titleLarge,
                    color = MaterialTheme.colorScheme.primary,
                )
                Spacer(Modifier.height(Dimens.tightGap))
                Text(
                    text = stringResource(R.string.correction_current_prefix) + " " + displayName(state.item),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )

                if (state.lockedDown) {
                    Spacer(Modifier.height(Dimens.sectionGap))
                    Text(
                        text = (state.convertPhase as? ConvertPhase.Failed)?.message
                            ?: stringResource(R.string.correction_convert_pending, "…"),
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }

                Spacer(Modifier.height(Dimens.sectionGap))
                SearchSection(state, onSearch, onApplyCandidate, enabled = !state.lockedDown)

                Spacer(Modifier.height(Dimens.sectionGap))
                HorizontalDivider(color = JfTheme.colors.divider)
                Spacer(Modifier.height(Dimens.sectionGap))

                TmdbFallbackSection(state, onOpenTmdb, onApplyPastedId, enabled = !state.lockedDown)

                if (state.typeConvertVisible) {
                    Spacer(Modifier.height(Dimens.sectionGap))
                    HorizontalDivider(color = JfTheme.colors.divider)
                    Spacer(Modifier.height(Dimens.sectionGap))
                    TypeConvertSection(state, onSelectTarget, onConvert)
                }

                Spacer(Modifier.height(Dimens.sectionGap))
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                    TextButton(onClick = onDismiss) {
                        Text(stringResource(R.string.share_cancel))
                    }
                }
            }
        }
    }
}

@Composable
private fun SearchSection(
    state: CorrectionState,
    onSearch: (String) -> Unit,
    onApplyCandidate: (RemoteSearchCandidate) -> Unit,
    enabled: Boolean,
) {
    var query by rememberSaveable(state.item.id) { mutableStateOf(state.searchPrefill) }
    OutlinedTextField(
        value = query,
        onValueChange = { query = it },
        label = { Text(stringResource(R.string.correction_search_hint)) },
        singleLine = true,
        enabled = enabled,
        modifier = Modifier.fillMaxWidth(),
    )
    Spacer(Modifier.height(Dimens.blockGap))
    OutlinedButton(onClick = { onSearch(query) }, enabled = enabled) {
        Text(stringResource(R.string.correction_search_button))
    }

    when (val phase = state.searchPhase) {
        SearchPhase.Idle -> Unit
        SearchPhase.Searching -> StatusLine(stringResource(R.string.correction_searching))
        SearchPhase.NoResults -> StatusLine(stringResource(R.string.correction_no_results))
        is SearchPhase.Failed -> StatusLine(
            stringResource(R.string.correction_search_failed, phase.message),
            color = MaterialTheme.colorScheme.error,
        )
    }

    if (state.applyPhase is ApplyPhase.Applying) {
        StatusLine(stringResource(R.string.correction_applying))
    } else if (state.applyPhase is ApplyPhase.Failed) {
        StatusLine(
            stringResource(R.string.correction_apply_failed, (state.applyPhase as ApplyPhase.Failed).message),
            color = MaterialTheme.colorScheme.error,
        )
    }

    for (candidate in state.candidates) {
        CandidateRow(candidate, onClick = { onApplyCandidate(candidate) }, enabled = enabled)
    }
}

@Composable
private fun CandidateRow(candidate: RemoteSearchCandidate, onClick: () -> Unit, enabled: Boolean) {
    Spacer(Modifier.height(Dimens.blockGap))
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(enabled = enabled, onClick = onClick),
    ) {
        Poster(url = candidate.imageUrl)
        Spacer(Modifier.width(Dimens.blockGap))
        Column(Modifier.weight(1f)) {
            Text(
                text = buildString {
                    append(candidate.name)
                    candidate.year?.let { append(" ($it)") }
                },
                style = MaterialTheme.typography.titleSmall,
            )
            candidate.primaryProviderLabel?.let {
                Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            candidate.overview?.takeIf { it.isNotBlank() }?.let {
                Text(
                    text = it,
                    style = MaterialTheme.typography.bodySmall,
                    maxLines = 3,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

@Composable
private fun TmdbFallbackSection(
    state: CorrectionState,
    onOpenTmdb: (String) -> Unit,
    onApplyPastedId: (String) -> Unit,
    enabled: Boolean,
) {
    var pasted by rememberSaveable(state.item.id) { mutableStateOf("") }
    Text(
        text = stringResource(R.string.correction_fallback_title),
        style = MaterialTheme.typography.titleSmall,
    )
    Spacer(Modifier.height(Dimens.tightGap))
    Text(
        text = stringResource(R.string.correction_fallback_help),
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    Spacer(Modifier.height(Dimens.blockGap))
    OutlinedButton(onClick = { onOpenTmdb(state.searchPrefill) }, enabled = enabled) {
        Text(stringResource(R.string.correction_fallback_tmdb_button))
    }
    Spacer(Modifier.height(Dimens.blockGap))
    OutlinedTextField(
        value = pasted,
        onValueChange = { pasted = it },
        label = { Text(stringResource(R.string.correction_fallback_paste_hint)) },
        singleLine = true,
        enabled = enabled,
        modifier = Modifier.fillMaxWidth(),
    )
    Spacer(Modifier.height(Dimens.blockGap))
    Button(onClick = { onApplyPastedId(pasted) }, enabled = enabled) {
        Text(stringResource(R.string.correction_fallback_apply_button))
    }
}

@Composable
private fun TypeConvertSection(
    state: CorrectionState,
    onSelectTarget: (ConvertTarget) -> Unit,
    onConvert: (ConvertTarget) -> Unit,
) {
    var confirming by remember(state.item.id) { mutableStateOf(false) }

    Text(stringResource(R.string.correction_type_title), style = MaterialTheme.typography.titleSmall)
    Spacer(Modifier.height(Dimens.tightGap))
    Text(
        stringResource(R.string.correction_type_help),
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    Spacer(Modifier.height(Dimens.blockGap))

    val options = listOf(
        ConvertTarget.MOVIE to stringResource(R.string.category_movie),
        ConvertTarget.SERIES to stringResource(R.string.category_series),
        ConvertTarget.OTHER to stringResource(R.string.correction_type_other_label),
    )
    for ((target, label) in options) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .selectable(
                    selected = state.selectedTarget == target,
                    enabled = !state.converting && !state.convertFired && !state.lockedDown,
                    onClick = { onSelectTarget(target) },
                )
                .padding(vertical = 2.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            RadioButton(
                selected = state.selectedTarget == target,
                onClick = { onSelectTarget(target) },
                enabled = !state.converting && !state.convertFired && !state.lockedDown,
            )
            Text(label, style = MaterialTheme.typography.bodyMedium)
        }
    }

    Spacer(Modifier.height(Dimens.blockGap))
    if (!confirming) {
        Button(
            onClick = { confirming = true },
            enabled = state.convertEnabled,
        ) {
            Text(stringResource(R.string.correction_convert_button, state.selectedTarget.wireName))
        }
    } else {
        // Inline confirm — the destructive step, one deliberate extra tap.
        Text(
            stringResource(R.string.correction_convert_confirm_message, state.selectedTarget.wireName),
            style = MaterialTheme.typography.bodySmall,
        )
        Spacer(Modifier.height(Dimens.blockGap))
        Row(horizontalArrangement = Arrangement.spacedBy(Dimens.blockGap)) {
            TextButton(onClick = { confirming = false }) {
                Text(stringResource(R.string.share_cancel))
            }
            Button(
                onClick = {
                    confirming = false
                    onConvert(state.selectedTarget)
                },
                enabled = state.convertEnabled,
            ) {
                Text(stringResource(R.string.correction_convert_confirm_ok))
            }
        }
    }

    when (val phase = state.convertPhase) {
        is ConvertPhase.Converting -> StatusLine(stringResource(R.string.correction_converting, phase.target.wireName))
        is ConvertPhase.Found -> StatusLine(stringResource(R.string.correction_convert_found, phase.target.wireName))
        is ConvertPhase.Pending -> StatusLine(stringResource(R.string.correction_convert_pending, phase.target.wireName))
        is ConvertPhase.Failed -> StatusLine(
            stringResource(R.string.correction_convert_failed, phase.message),
            color = MaterialTheme.colorScheme.error,
        )
        ConvertPhase.Idle -> Unit
    }
}

@Composable
private fun StatusLine(text: String, color: androidx.compose.ui.graphics.Color = MaterialTheme.colorScheme.onSurface) {
    Spacer(Modifier.height(Dimens.blockGap))
    Text(text = text, color = color, style = MaterialTheme.typography.bodyMedium)
}

private fun displayName(item: LibraryItem): String = buildString {
    append(item.name)
    item.year?.let { append(" ($it)") }
}

