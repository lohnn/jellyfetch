package se.lohnn.jellyfetch.share

import androidx.compose.runtime.Composable
import androidx.compose.ui.tooling.preview.Preview
import com.android.tools.screenshot.PreviewTest
import se.lohnn.jellyfetch.ui.theme.JellyFetchTheme

/**
 * Screenshot-test previews for the share/confirm UI. Renders the stateless
 * [ShareConfirmScreen] (the intent-resolution logic is NOT here — I-099, see
 * ShareActivity) for a URL, a magnet, and a torrent file, light + dark.
 */
@Composable
private fun PreviewShare(typeLabel: String, content: String, dark: Boolean) {
    JellyFetchTheme(darkTheme = dark) {
        ShareConfirmScreen(
            typeLabel = typeLabel,
            content = content,
            onCancel = {},
            onSend = {},
        )
    }
}

@PreviewTest
@Preview(name = "Share · URL · light", widthDp = 360, heightDp = 320)
@Composable
fun ShareUrlLight() = PreviewShare(
    typeLabel = "Link",
    content = "https://www.svtplay.se/video/abc123/some-program/episode-4",
    dark = false,
)

@PreviewTest
@Preview(name = "Share · magnet · dark", widthDp = 360, heightDp = 320)
@Composable
fun ShareMagnetDark() = PreviewShare(
    typeLabel = "Link",
    content = "magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&dn=Example",
    dark = true,
)

@PreviewTest
@Preview(name = "Share · torrent · light", widthDp = 360, heightDp = 320)
@Composable
fun ShareTorrentLight() = PreviewShare(
    typeLabel = "Torrent file",
    content = "ubuntu-24.04-desktop-amd64.iso.torrent",
    dark = false,
)
