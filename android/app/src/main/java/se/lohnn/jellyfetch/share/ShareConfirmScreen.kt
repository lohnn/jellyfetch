package se.lohnn.jellyfetch.share

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MenuAnchorType
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import se.lohnn.jellyfetch.R
import se.lohnn.jellyfetch.api.LibraryInfo
import se.lohnn.jellyfetch.ui.theme.Dimens

/**
 * Everything the confirm screen needs to render the library dropdown, as a plain
 * value/lambda bundle so [ShareConfirmScreen] stays a STATELESS preview-friendly
 * surface (I-099 idiom: no ViewModel, ContentResolver, or intent in the
 * composable — the @Preview harness renders it deterministically). The host
 * (ShareActivity) maps [SharePickerState] into this and forwards the callbacks.
 *
 * [libraries] is empty until a load succeeds; [isLoading]/[loadError] drive the
 * dropdown's loading + graceful-failure states (Auto always remains selectable).
 * [onOpened] is the LAZY-load trigger — fired the first time the menu expands.
 */
data class LibraryPickerUi(
    val selectionLabel: String = "Auto",
    val libraries: List<LibraryInfo> = emptyList(),
    val isLoading: Boolean = false,
    val loadError: String? = null,
    val onOpened: () -> Unit = {},
    val onSelectAuto: () -> Unit = {},
    val onSelectLibrary: (LibraryInfo) -> Unit = {},
    val onRetry: () -> Unit = {},
)

/**
 * The share/confirm UI (PASS 2 — Compose port of the classic activity_share.xml,
 * plus the v2 library picker).
 *
 * ⚠ This is ONLY the confirm UI. The hard-won intent-resolution logic (I-099 —
 * SEND text/plain prose→URL regex extraction, SEND octet-stream torrent, VIEW
 * magnet scheme / .torrent by MIME+extension, content-URI byte reading) stays in
 * [IntentResolver] / [se.lohnn.jellyfetch.ShareActivity] and is carried over
 * intact, NOT rewritten. This composable just renders what was caught, offers a
 * library dropdown (Auto preselected), and one confirm tap. It is a stateless
 * value/lambda surface so the @Preview harness renders it deterministically (no
 * ContentResolver, no intent, no ViewModel).
 */
@Composable
fun ShareConfirmScreen(
    typeLabel: String,
    content: String,
    picker: LibraryPickerUi = LibraryPickerUi(),
    onCancel: () -> Unit,
    onSend: (dontAskAgain: Boolean) -> Unit,
) {
    var dontAsk by remember { mutableStateOf(false) }

    Surface(
        color = MaterialTheme.colorScheme.surface,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(Dimens.screenPadding)) {
            Text(
                text = stringResource(R.string.share_send),
                style = MaterialTheme.typography.titleLarge,
                color = MaterialTheme.colorScheme.primary,
            )
            Spacer(Modifier.height(Dimens.sectionGap))

            Text(
                text = typeLabel,
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(Modifier.height(Dimens.tightGap))
            Text(
                text = content,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurface,
                maxLines = 4,
                overflow = TextOverflow.Ellipsis,
            )

            Spacer(Modifier.height(Dimens.sectionGap))
            LibraryDropdown(picker)

            Spacer(Modifier.height(Dimens.sectionGap))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Checkbox(checked = dontAsk, onCheckedChange = { dontAsk = it })
                Text(
                    text = stringResource(R.string.share_dont_ask),
                    style = MaterialTheme.typography.bodyMedium,
                )
            }

            Spacer(Modifier.height(Dimens.blockGap))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.End,
            ) {
                TextButton(onClick = onCancel) {
                    Text(stringResource(R.string.share_cancel))
                }
                Spacer(Modifier.width(Dimens.blockGap))
                OutlinedButton(onClick = { onSend(dontAsk) }) {
                    Text(stringResource(R.string.share_send))
                }
            }
        }
    }
}

/**
 * The "place in library" dropdown. Auto is always the first row (send no
 * LibraryId — today's classify-and-place behavior). Opening the menu triggers
 * [LibraryPickerUi.onOpened] (the lazy fetch). While loading, a spinner row
 * shows; on failure, the error + a retry row show — but Auto stays selectable
 * and sending is never blocked (W-056). Non-placeable libraries render disabled.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun LibraryDropdown(picker: LibraryPickerUi) {
    var expanded by remember { mutableStateOf(false) }

    Text(
        text = stringResource(R.string.share_library_label),
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    Spacer(Modifier.height(Dimens.tightGap))

    ExposedDropdownMenuBox(
        expanded = expanded,
        onExpandedChange = { newExpanded ->
            expanded = newExpanded
            if (newExpanded) picker.onOpened() // LAZY: fetch only when opened.
        },
    ) {
        OutlinedTextField(
            value = picker.selectionLabel,
            onValueChange = {},
            readOnly = true,
            singleLine = true,
            label = null,
            trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = expanded) },
            modifier = Modifier
                .fillMaxWidth()
                .menuAnchor(MenuAnchorType.PrimaryNotEditable),
        )
        ExposedDropdownMenu(
            expanded = expanded,
            onDismissRequest = { expanded = false },
        ) {
            // Auto — always present, regardless of the loaded list.
            DropdownMenuItem(
                text = { Text(stringResource(R.string.share_library_auto)) },
                onClick = {
                    picker.onSelectAuto()
                    expanded = false
                },
            )

            when {
                picker.isLoading -> {
                    DropdownMenuItem(
                        enabled = false,
                        text = {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                CircularProgressIndicator(
                                    modifier = Modifier.size(16.dp),
                                    strokeWidth = 2.dp,
                                )
                                Spacer(Modifier.width(Dimens.blockGap))
                                Text(stringResource(R.string.share_library_loading))
                            }
                        },
                        onClick = {},
                    )
                }

                picker.loadError != null -> {
                    DropdownMenuItem(
                        text = {
                            Text(
                                text = stringResource(R.string.share_library_load_failed),
                                color = MaterialTheme.colorScheme.error,
                            )
                        },
                        onClick = {
                            picker.onRetry()
                            // keep the menu open so the loading/loaded state is visible
                        },
                    )
                }

                else -> {
                    for (library in picker.libraries) {
                        DropdownMenuItem(
                            enabled = library.isPlaceable,
                            text = {
                                Column {
                                    Text(
                                        text = library.name,
                                        style = MaterialTheme.typography.bodyMedium,
                                    )
                                    val subtitle = library.collectionType
                                        ?: if (!library.isPlaceable) {
                                            stringResource(R.string.share_library_unavailable)
                                        } else {
                                            null
                                        }
                                    if (subtitle != null) {
                                        Text(
                                            text = subtitle,
                                            style = MaterialTheme.typography.labelSmall,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        )
                                    }
                                }
                            },
                            onClick = {
                                picker.onSelectLibrary(library)
                                expanded = false
                            },
                        )
                    }
                }
            }
        }
    }
}
