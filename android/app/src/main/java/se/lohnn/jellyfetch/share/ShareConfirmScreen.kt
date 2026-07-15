package se.lohnn.jellyfetch.share

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
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
import se.lohnn.jellyfetch.ui.theme.Dimens

/**
 * The share/confirm UI (PASS 2 — Compose port of the classic activity_share.xml).
 *
 * ⚠ This is ONLY the confirm UI. The hard-won intent-resolution logic (I-099 —
 * SEND text/plain prose→URL regex extraction, SEND octet-stream torrent, VIEW
 * magnet scheme / .torrent by MIME+extension, content-URI byte reading) stays in
 * [IntentResolver] / [se.lohnn.jellyfetch.ShareActivity] and is carried over
 * intact, NOT rewritten. This composable just renders what was caught and offers
 * one confirm tap. It is a stateless value/lambda surface so the @Preview harness
 * renders it deterministically (no ContentResolver, no intent).
 */
@Composable
fun ShareConfirmScreen(
    typeLabel: String,
    content: String,
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
