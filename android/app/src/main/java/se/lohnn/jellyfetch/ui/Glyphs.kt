package se.lohnn.jellyfetch.ui

import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.res.stringResource
import se.lohnn.jellyfetch.R

/**
 * Plain-text "icons" — the app deliberately does NOT depend on
 * material-icons-core / -extended (I-082 minimal footprint; the Compose BOM's
 * material3 no longer pulls icons transitively, and the Pass-1 dashboard already
 * established the text-glyph convention). Accessibility is preserved via a
 * semantics contentDescription so TalkBack still announces the action.
 */
@Composable
fun GlyphText(glyph: String, contentDescription: String, modifier: Modifier = Modifier) {
    Text(
        text = glyph,
        style = MaterialTheme.typography.titleLarge,
        modifier = modifier.semantics { this.contentDescription = contentDescription },
    )
}

/** The standard up/back affordance for a screen's TopAppBar navigationIcon. */
@Composable
fun NavBackButton(onBack: () -> Unit) {
    IconButton(onClick = onBack) {
        GlyphText(glyph = "\u2190", contentDescription = stringResource(R.string.nav_back)) // ←
    }
}
