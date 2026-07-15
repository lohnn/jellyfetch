package se.lohnn.jellyfetch

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import se.lohnn.jellyfetch.allitems.AllItemsScreen
import se.lohnn.jellyfetch.allitems.AllItemsViewModel
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.correction.CorrectionHost
import se.lohnn.jellyfetch.ui.theme.JellyFetchTheme

/**
 * The "All library items" host (PASS 2 — migrated from classic ListView Views to
 * Compose). Renders [AllItemsScreen] from [AllItemsViewModel]; tapping an item
 * opens the shared correction picker. onApplied does a full [AllItemsViewModel.reload]
 * so a converted item shows its new type and the old id is gone — mirroring the
 * classic AllItemsActivity.openCorrection behavior exactly.
 */
class AllItemsActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            JellyFetchTheme {
                val vm: AllItemsViewModel = viewModel(factory = AllItemsViewModel.Factory)
                val state by vm.state.collectAsStateWithLifecycle()
                var correcting by remember { mutableStateOf<LibraryItem?>(null) }

                LaunchedEffect(Unit) { vm.reload() }

                AllItemsScreen(
                    state = state,
                    onBack = { finish() },
                    onSearch = vm::setQuery,
                    onTypeFilter = vm::setTypeFilter,
                    onLoadMore = vm::loadMoreIfNeeded,
                    onOpenItem = { item -> correcting = item },
                )

                correcting?.let { item ->
                    CorrectionHost(
                        item = item,
                        onDismiss = { correcting = null },
                        onApplied = {
                            // Full reload discards any stale/converted-away row and
                            // re-resolves from the server (ignore the handed-back item).
                            vm.reload()
                        },
                    )
                }
            }
        }
    }
}
