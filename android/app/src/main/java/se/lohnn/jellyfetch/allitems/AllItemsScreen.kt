package se.lohnn.jellyfetch.allitems

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import se.lohnn.jellyfetch.R
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemType
import se.lohnn.jellyfetch.ui.NavBackButton
import se.lohnn.jellyfetch.ui.Poster
import se.lohnn.jellyfetch.ui.theme.Dimens
import se.lohnn.jellyfetch.ui.theme.JfTheme

/**
 * The "All library items" screen (PASS 2 — Compose port of activity_all_items.xml
 * + LibraryItemsAdapter). Search + type-filter chips, paged LazyColumn (append on
 * scroll-to-end), graceful empty/unreachable states, tap-into the shared
 * correction picker. Stateless over [AllItemsState] for the @Preview harness.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AllItemsScreen(
    state: AllItemsState,
    onBack: () -> Unit,
    onSearch: (String) -> Unit,
    onTypeFilter: (LibraryItemType?) -> Unit,
    onLoadMore: () -> Unit,
    onOpenItem: (LibraryItem) -> Unit,
) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.all_items_title)) },
                navigationIcon = { NavBackButton(onBack) },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.primary,
                    titleContentColor = MaterialTheme.colorScheme.onPrimary,
                    navigationIconContentColor = MaterialTheme.colorScheme.onPrimary,
                ),
            )
        },
    ) { innerPadding ->
        Column(
            Modifier
                .fillMaxSize()
                .padding(innerPadding),
        ) {
            SearchAndFilter(state, onSearch, onTypeFilter)

            state.error?.let {
                Surface(color = JfTheme.colors.errorCalloutBg, modifier = Modifier.fillMaxWidth()) {
                    Text(
                        stringResource(R.string.all_items_unreachable, it),
                        color = JfTheme.colors.errorCalloutText,
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.padding(Dimens.blockGap),
                    )
                }
            }

            when {
                state.isEmpty && state.error == null ->
                    Centered(stringResource(R.string.all_items_empty))
                state.isEmpty -> Unit // error banner already shown above
                else -> ItemList(state, onLoadMore, onOpenItem)
            }
        }
    }
}

@Composable
private fun SearchAndFilter(
    state: AllItemsState,
    onSearch: (String) -> Unit,
    onTypeFilter: (LibraryItemType?) -> Unit,
) {
    var query by remember { mutableStateOf(state.query.orEmpty()) }
    Column(Modifier.padding(Dimens.screenPadding)) {
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            label = { Text(stringResource(R.string.all_items_search_hint)) },
            singleLine = true,
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(Dimens.blockGap))
        Row(horizontalArrangement = Arrangement.spacedBy(Dimens.blockGap)) {
            FilterChip(
                selected = state.typeFilter == null,
                onClick = { onSearch(query); onTypeFilter(null) },
                label = { Text(stringResource(R.string.all_items_filter_all)) },
            )
            FilterChip(
                selected = state.typeFilter == LibraryItemType.MOVIE,
                onClick = { onSearch(query); onTypeFilter(LibraryItemType.MOVIE) },
                label = { Text(stringResource(R.string.all_items_filter_movies)) },
            )
            FilterChip(
                selected = state.typeFilter == LibraryItemType.SERIES,
                onClick = { onSearch(query); onTypeFilter(LibraryItemType.SERIES) },
                label = { Text(stringResource(R.string.all_items_filter_series)) },
            )
        }
    }
}

@Composable
private fun ItemList(
    state: AllItemsState,
    onLoadMore: () -> Unit,
    onOpenItem: (LibraryItem) -> Unit,
) {
    val listState = rememberLazyListState()
    // Fire load-more when the last visible item nears the end of the buffer.
    val shouldLoadMore by remember {
        derivedStateOf {
            val last = listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
            last >= state.items.size - 3 && state.canLoadMore
        }
    }
    LaunchedEffect(shouldLoadMore) {
        if (shouldLoadMore) onLoadMore()
    }

    LazyColumn(state = listState, modifier = Modifier.fillMaxSize()) {
        items(state.items, key = { it.id }) { item ->
            LibraryItemRow(item, onClick = { onOpenItem(item) })
            HorizontalDivider(color = JfTheme.colors.divider)
        }
        item {
            Text(
                text = stringResource(R.string.all_items_count, state.items.size, state.totalCount),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(Dimens.blockGap),
                textAlign = TextAlign.Center,
            )
        }
    }
}

@Composable
private fun LibraryItemRow(item: LibraryItem, onClick: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = Dimens.screenPadding, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        // Leading poster/album-art thumbnail, mirroring the CorrectionSheet
        // candidate rows for visual consistency. Uses the compact list-thumb size
        // (Dimens.listThumb*) so the scrolling list stays tidy. Poster shows a
        // themed "No poster" placeholder for null/failed URLs — and always in a
        // @Preview render (no network), which still proves the row LAYOUT.
        Poster(
            url = item.posterUrl,
            width = Dimens.listThumbWidth,
            height = Dimens.listThumbHeight,
        )
        Spacer(Modifier.width(Dimens.blockGap))
        Column(Modifier.weight(1f)) {
            Text(item.name, style = MaterialTheme.typography.titleMedium)
            val typeLabel = item.type?.let {
                when (it) {
                    LibraryItemType.MOVIE -> stringResource(R.string.category_movie)
                    LibraryItemType.SERIES -> stringResource(R.string.category_series)
                }
            }
            listOfNotNull(item.year?.toString(), typeLabel).joinToString(" · ").takeIf { it.isNotBlank() }?.let {
                Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        Text("\u203A", style = MaterialTheme.typography.titleLarge, color = JfTheme.colors.chevron)
    }
}

@Composable
private fun Centered(text: String) {
    Box(Modifier.fillMaxSize().padding(32.dp), contentAlignment = Alignment.Center) {
        Text(text, style = MaterialTheme.typography.bodyLarge, textAlign = TextAlign.Center)
    }
}

