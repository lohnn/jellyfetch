package se.lohnn.jellyfetch

import android.app.Activity
import android.os.Bundle
import android.view.View
import android.view.inputmethod.EditorInfo
import android.widget.AbsListView
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.EditText
import android.widget.ListView
import android.widget.Spinner
import android.widget.TextView
import androidx.core.widget.ListViewCompat
import androidx.swiperefreshlayout.widget.SwipeRefreshLayout
import se.lohnn.jellyfetch.api.ApiClient
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemType

/**
 * The "All library items" screen: every movie/series on the server (not just
 * app-originated downloads), searchable + type-filterable, each tappable into the
 * SAME [CorrectionDialog] used from the job detail. Lets the user fix metadata for
 * items that came from elsewhere or whose download was removed from the app.
 *
 * Classic Views idiom (ListView + [LibraryItemsAdapter] + framework widgets), paged
 * (append on scroll-to-end), with pull-to-refresh and graceful empty/unreachable
 * states — mirroring the dashboard's conventions.
 */
class AllItemsActivity : Activity() {

    private lateinit var searchField: EditText
    private lateinit var filterSpinner: Spinner
    private lateinit var swipeRefresh: SwipeRefreshLayout
    private lateinit var listView: ListView
    private lateinit var emptyView: TextView
    private lateinit var statusBanner: TextView
    private lateinit var countFooter: TextView
    private lateinit var adapter: LibraryItemsAdapter

    private val pageSize = 30
    private val loaded = mutableListOf<LibraryItem>()
    private var totalCount = 0
    private var loading = false
    private var currentQuery: String? = null
    private var currentType: LibraryItemType? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_all_items)
        setTitle(R.string.all_items_title)
        actionBar?.setDisplayHomeAsUpEnabled(true)

        searchField = findViewById(R.id.all_items_search)
        filterSpinner = findViewById(R.id.all_items_filter)
        swipeRefresh = findViewById(R.id.all_items_swipe_refresh)
        listView = findViewById(R.id.all_items_list)
        emptyView = findViewById(R.id.all_items_empty)
        statusBanner = findViewById(R.id.all_items_status_banner)
        countFooter = findViewById(R.id.all_items_count)

        adapter = LibraryItemsAdapter(this)
        listView.adapter = adapter

        swipeRefresh.setOnChildScrollUpCallback { _, _ -> ListViewCompat.canScrollList(listView, -1) }
        swipeRefresh.setProgressBackgroundColorSchemeResource(R.color.jf_background)
        swipeRefresh.setColorSchemeResources(R.color.jf_primary)
        swipeRefresh.setOnRefreshListener { reload() }

        setupFilter()
        setupSearch()
        setupPaging()

        listView.setOnItemClickListener { _, _, position, _ ->
            openCorrection(adapter.getItem(position))
        }

        reload()
    }

    private fun setupFilter() {
        // Order matters: index 0 = All (null), 1 = Movies, 2 = Series.
        val labels = listOf(
            getString(R.string.all_items_filter_all),
            getString(R.string.all_items_filter_movies),
            getString(R.string.all_items_filter_series),
        )
        val spinnerAdapter = ArrayAdapter(this, android.R.layout.simple_spinner_item, labels)
        spinnerAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item)
        filterSpinner.adapter = spinnerAdapter
        filterSpinner.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                val newType = when (position) {
                    1 -> LibraryItemType.MOVIE
                    2 -> LibraryItemType.SERIES
                    else -> null
                }
                if (newType != currentType) {
                    currentType = newType
                    reload()
                }
            }

            override fun onNothingSelected(parent: AdapterView<*>?) {}
        }
    }

    private fun setupSearch() {
        searchField.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_SEARCH) {
                currentQuery = searchField.text.toString().trim().ifBlank { null }
                reload()
                true
            } else {
                false
            }
        }
    }

    private fun setupPaging() {
        listView.setOnScrollListener(object : AbsListView.OnScrollListener {
            override fun onScrollStateChanged(view: AbsListView?, scrollState: Int) {}
            override fun onScroll(
                view: AbsListView?,
                firstVisibleItem: Int,
                visibleItemCount: Int,
                totalItemCount: Int,
            ) {
                if (totalItemCount == 0 || loading) return
                val reachedEnd = firstVisibleItem + visibleItemCount >= totalItemCount
                if (reachedEnd && loaded.size < totalCount) {
                    loadPage(startIndex = loaded.size, append = true)
                }
            }
        })
    }

    private fun reload() {
        loaded.clear()
        totalCount = 0
        adapter.submitList(emptyList())
        loadPage(startIndex = 0, append = false)
    }

    private fun loadPage(startIndex: Int, append: Boolean) {
        loading = true
        if (!append) swipeRefresh.isRefreshing = true

        ApiClient.current.listLibraryItems(currentQuery, currentType, startIndex, pageSize) { result ->
            loading = false
            swipeRefresh.isRefreshing = false
            result.onSuccess { page ->
                statusBanner.visibility = View.GONE
                totalCount = page.totalCount
                if (append) loaded.addAll(page.items) else loaded.replaceAll(page.items)
                adapter.submitList(loaded.toList())
                renderStates()
            }.onFailure { error ->
                statusBanner.visibility = View.VISIBLE
                statusBanner.text =
                    getString(R.string.all_items_unreachable, error.message ?: error.toString())
                // Keep whatever was already loaded rather than blanking on one failure.
                renderStates()
            }
        }
    }

    private fun renderStates() {
        val isEmpty = loaded.isEmpty()
        emptyView.visibility = if (isEmpty && statusBanner.visibility != View.VISIBLE) View.VISIBLE else View.GONE
        listView.visibility = if (isEmpty) View.GONE else View.VISIBLE
        countFooter.visibility = if (isEmpty) View.GONE else View.VISIBLE
        countFooter.text = getString(R.string.all_items_count, loaded.size, totalCount)
    }

    private fun openCorrection(item: LibraryItem) {
        CorrectionDialog(
            activity = this,
            item = item,
            onApplied = { _ ->
                // Full reload discards the stale (possibly converted-away) item and
                // re-resolves the whole list from the server — so a converted item
                // shows its new type and the old deleted id is gone from view. We
                // ignore the handed-back item here (a list re-fetch is simplest and
                // guarantees no stale row lingers).
                reload()
            },
        ).show()
    }

    override fun onOptionsItemSelected(item: android.view.MenuItem): Boolean {
        if (item.itemId == android.R.id.home) {
            finish()
            return true
        }
        return super.onOptionsItemSelected(item)
    }
}

/** MutableList.replaceAll-with-contents helper (clear + addAll) for min-SDK safety. */
private fun <T> MutableList<T>.replaceAll(newItems: List<T>) {
    clear()
    addAll(newItems)
}
