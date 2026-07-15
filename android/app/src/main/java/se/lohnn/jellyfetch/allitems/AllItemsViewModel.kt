package se.lohnn.jellyfetch.allitems

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import se.lohnn.jellyfetch.api.JellyFetchApi
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemType

data class AllItemsState(
    val items: List<LibraryItem> = emptyList(),
    val totalCount: Int = 0,
    val query: String? = null,
    val typeFilter: LibraryItemType? = null,
    val loading: Boolean = false,
    /** A non-null banner when a load failed (kept items still shown). */
    val error: String? = null,
) {
    val isEmpty: Boolean get() = items.isEmpty()
    val canLoadMore: Boolean get() = items.size < totalCount
}

/**
 * State-holder for the "All library items" screen (PASS 2). Paged (append on
 * scroll-to-end), searchable, type-filterable — same contract as the classic
 * ListView Activity, but reduced to explicit immutable state. android.view-free
 * (I-127): the [JellyFetchApi] is injected.
 */
class AllItemsViewModel(
    private val apiProvider: () -> JellyFetchApi,
    private val pageSize: Int = DEFAULT_PAGE_SIZE,
) : ViewModel() {

    private val api get() = apiProvider()
    private val _state = MutableStateFlow(AllItemsState())
    val state: StateFlow<AllItemsState> = _state.asStateFlow()

    fun setQuery(raw: String) {
        _state.update { it.copy(query = raw.trim().ifBlank { null }) }
        reload()
    }

    fun setTypeFilter(type: LibraryItemType?) {
        if (type == _state.value.typeFilter) return
        _state.update { it.copy(typeFilter = type) }
        reload()
    }

    fun reload() {
        _state.update { it.copy(items = emptyList(), totalCount = 0) }
        loadPage(startIndex = 0, append = false)
    }

    fun loadMoreIfNeeded() {
        val s = _state.value
        if (s.loading || !s.canLoadMore) return
        loadPage(startIndex = s.items.size, append = true)
    }

    private fun loadPage(startIndex: Int, append: Boolean) {
        _state.update { it.copy(loading = true) }
        val s = _state.value
        api.listLibraryItems(s.query, s.typeFilter, startIndex, pageSize) { result ->
            result.onSuccess { page ->
                _state.update { cur ->
                    cur.copy(
                        items = if (append) cur.items + page.items else page.items,
                        totalCount = page.totalCount,
                        loading = false,
                        error = null,
                    )
                }
            }.onFailure { error ->
                // Keep whatever was loaded rather than blanking on one failure.
                _state.update { it.copy(loading = false, error = error.message ?: error.toString()) }
            }
        }
    }

    companion object {
        private const val DEFAULT_PAGE_SIZE = 30

        val Factory = viewModelFactory {
            initializer {
                AllItemsViewModel(apiProvider = { se.lohnn.jellyfetch.api.ApiClient.current })
            }
        }
    }
}
