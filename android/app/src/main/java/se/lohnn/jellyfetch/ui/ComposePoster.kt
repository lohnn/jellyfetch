package se.lohnn.jellyfetch.ui

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import se.lohnn.jellyfetch.R
import se.lohnn.jellyfetch.ui.theme.Dimens
import java.net.HttpURLConnection
import java.net.URL

/**
 * Compose poster thumbnail — the Compose port of the hand-rolled [se.lohnn.jellyfetch.PosterLoader]
 * (I-082: still no Glide/Coil, just HttpURLConnection + BitmapFactory). Loads
 * [url] off the main thread inside a [LaunchedEffect] keyed on the url (so a
 * recomposition with a new url reloads and a scrolled-away row's load is
 * cancelled), and shows a themed "No poster" placeholder while loading / on
 * failure / when [url] is null.
 *
 * Best-effort and cache-free by design (display polish, never load-bearing): a
 * failed load simply keeps the placeholder. The @Preview renders never hit the
 * network — they pass url = null and show the placeholder deterministically.
 */
@Composable
fun Poster(
    url: String?,
    modifier: Modifier = Modifier,
) {
    var bitmap by remember(url) { mutableStateOf<Bitmap?>(null) }

    LaunchedEffect(url) {
        bitmap = null
        if (url.isNullOrBlank()) return@LaunchedEffect
        bitmap = withContext(Dispatchers.IO) { loadBitmap(url) }
    }

    Box(
        modifier = modifier
            .size(Dimens.posterWidth, Dimens.posterHeight)
            .clip(RoundedCornerShape(Dimens.cardCorner))
            .background(MaterialTheme.colorScheme.surfaceVariant),
        contentAlignment = Alignment.Center,
    ) {
        val bmp = bitmap
        if (bmp != null) {
            Image(
                bitmap = bmp.asImageBitmap(),
                contentDescription = null,
                modifier = Modifier.fillMaxSize(),
            )
        } else {
            Text(
                text = stringResource(R.string.metadata_no_poster),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
            )
        }
    }
}

private fun loadBitmap(url: String): Bitmap? = try {
    val conn = URL(url).openConnection() as HttpURLConnection
    conn.connectTimeout = 8_000
    conn.readTimeout = 12_000
    try {
        if (conn.responseCode in 200..299) {
            conn.inputStream.use { BitmapFactory.decodeStream(it) }
        } else {
            null
        }
    } finally {
        conn.disconnect()
    }
} catch (_: Exception) {
    null
}
