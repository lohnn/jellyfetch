package se.lohnn.jellyfetch.allitems

import androidx.compose.runtime.Composable
import androidx.compose.ui.tooling.preview.Preview
import com.android.tools.screenshot.PreviewTest
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemType
import se.lohnn.jellyfetch.ui.theme.JellyFetchTheme

/**
 * Screenshot-test previews for the "All library items" screen — populated (with
 * the search + filter chips and count footer), empty, and unreachable, light +
 * dark.
 *
 * A MIX of items carry a non-null [LibraryItem.posterUrl] and some leave it null,
 * to exercise the leading-poster row layout both ways. NOTE: a @Preview render
 * never hits the network, so EVERY poster slot shows the themed "No poster"
 * placeholder box regardless of url — that's expected and still proves the row
 * layout WITH the poster slot present. Real thumbnails only load on-device against
 * a live Jellyfin server.
 */
private val sampleItems = listOf(
    LibraryItem(id = "1", name = "The Matrix", year = 1999, type = LibraryItemType.MOVIE, posterUrl = "https://example/poster1.jpg"),
    LibraryItem(id = "2", name = "Game of Thrones", year = 2011, type = LibraryItemType.SERIES, posterUrl = "https://example/poster2.jpg"),
    LibraryItem(id = "3", name = "Blade Runner 2049", year = 2017, type = LibraryItemType.MOVIE),
    LibraryItem(id = "4", name = "Skavlan", year = 2009, type = LibraryItemType.SERIES, posterUrl = "https://example/poster4.jpg"),
)

@Composable
private fun PreviewAllItems(state: AllItemsState, dark: Boolean) {
    JellyFetchTheme(darkTheme = dark) {
        AllItemsScreen(
            state = state,
            onBack = {},
            onSearch = {},
            onTypeFilter = {},
            onLoadMore = {},
            onOpenItem = {},
        )
    }
}

@PreviewTest
@Preview(name = "AllItems · populated · light", widthDp = 400, heightDp = 700)
@Composable
fun AllItemsPopulatedLight() =
    PreviewAllItems(AllItemsState(items = sampleItems, totalCount = 42), dark = false)

@PreviewTest
@Preview(name = "AllItems · populated · dark", widthDp = 400, heightDp = 700)
@Composable
fun AllItemsPopulatedDark() =
    PreviewAllItems(AllItemsState(items = sampleItems, totalCount = 42), dark = true)

@PreviewTest
@Preview(name = "AllItems · empty · light", widthDp = 400, heightDp = 500)
@Composable
fun AllItemsEmptyLight() = PreviewAllItems(AllItemsState(), dark = false)

@PreviewTest
@Preview(name = "AllItems · unreachable · dark", widthDp = 400, heightDp = 500)
@Composable
fun AllItemsUnreachableDark() =
    PreviewAllItems(AllItemsState(error = "Connection refused"), dark = true)
