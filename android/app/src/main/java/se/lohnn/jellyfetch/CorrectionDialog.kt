package se.lohnn.jellyfetch

import android.app.Activity
import android.app.AlertDialog
import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import android.view.LayoutInflater
import android.view.View
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import se.lohnn.jellyfetch.api.ApiClient
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemType
import se.lohnn.jellyfetch.api.RemoteSearchCandidate

/**
 * The metadata correction picker, shared by [JobDetailActivity] and
 * [AllItemsActivity]. Classic Views idiom — a plain framework [AlertDialog]
 * hosting [R.layout.dialog_correction] (no Compose / AppCompat), inheriting the
 * Activity theme like the rest of the app.
 *
 * Two correction paths, both wired to Toast feedback (W-056 — never silently
 * discard an apply Result) and both gated on a correctable item (an item with an
 * id whose type we can resolve):
 *
 *  1. Native remote-search: type a title → tap a candidate → apply-by-result.
 *  2. Browser fallback: open TMDb via ACTION_VIEW (I-099 — verify on device) →
 *     paste the provider id back → apply-by-provider.
 *
 * The apply may be async server-side (W-064): on success we fire [onApplied] so
 * the caller re-fetches to confirm the new match actually landed, rather than
 * assuming the correction is instantaneous.
 */
class CorrectionDialog(
    private val activity: Activity,
    private val item: LibraryItem,
    /** Invoked after a successful apply so the caller can re-fetch/confirm (W-064). */
    private val onApplied: () -> Unit,
) {

    private val api get() = ApiClient.current
    private val searchType: LibraryItemType = item.type ?: LibraryItemType.MOVIE

    private lateinit var dialog: AlertDialog
    private lateinit var searchField: EditText
    private lateinit var searchStatus: TextView
    private lateinit var resultsContainer: LinearLayout
    private lateinit var pasteField: EditText
    private lateinit var applyStatus: TextView

    fun show() {
        val view = LayoutInflater.from(activity).inflate(R.layout.dialog_correction, null)

        view.findViewById<TextView>(R.id.correction_current).text =
            activity.getString(
                R.string.correction_current_prefix,
            ) + " " + displayName(item)

        searchField = view.findViewById(R.id.correction_search_field)
        searchStatus = view.findViewById(R.id.correction_search_status)
        resultsContainer = view.findViewById(R.id.correction_results_container)
        pasteField = view.findViewById(R.id.correction_paste_field)
        applyStatus = view.findViewById(R.id.correction_apply_status)

        // Prefill the search with the current (probably-wrong) name so a search is
        // one tap away.
        searchField.setText(item.name)

        view.findViewById<Button>(R.id.correction_search_button).setOnClickListener { runSearch() }
        searchField.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_SEARCH) {
                runSearch(); true
            } else {
                false
            }
        }

        view.findViewById<Button>(R.id.correction_tmdb_button).setOnClickListener { openTmdb() }
        view.findViewById<Button>(R.id.correction_paste_apply_button).setOnClickListener { applyPasted() }

        dialog = AlertDialog.Builder(activity)
            .setTitle(R.string.correction_title)
            .setView(view)
            .setNegativeButton(android.R.string.cancel, null)
            .create()
        dialog.show()
    }

    private fun runSearch() {
        val name = searchField.text.toString().trim()
        if (name.isEmpty()) return
        setSearchStatus(activity.getString(R.string.correction_searching))
        resultsContainer.removeAllViews()

        api.searchRemoteMetadata(item.id, searchType, name, item.year) { result ->
            if (!dialog.isShowing) return@searchRemoteMetadata
            result.onSuccess { candidates ->
                if (candidates.isEmpty()) {
                    setSearchStatus(activity.getString(R.string.correction_no_results))
                } else {
                    searchStatus.visibility = View.GONE
                    renderCandidates(candidates)
                }
            }.onFailure { error ->
                setSearchStatus(
                    activity.getString(R.string.correction_search_failed, error.message ?: error.toString()),
                )
            }
        }
    }

    private fun renderCandidates(candidates: List<RemoteSearchCandidate>) {
        resultsContainer.removeAllViews()
        val inflater = LayoutInflater.from(activity)
        for (candidate in candidates) {
            val row = inflater.inflate(R.layout.item_correction_candidate, resultsContainer, false)
            row.findViewById<TextView>(R.id.candidate_title).text = buildString {
                append(candidate.name)
                candidate.year?.let { append(" ($it)") }
            }
            row.findViewById<TextView>(R.id.candidate_providers).apply {
                val label = candidate.providerIds.entries.joinToString(" · ") { "${it.key} ${it.value}" }
                visibility = if (label.isBlank()) View.GONE else View.VISIBLE
                text = label
            }
            row.findViewById<TextView>(R.id.candidate_overview).apply {
                visibility = if (candidate.overview.isNullOrBlank()) View.GONE else View.VISIBLE
                text = candidate.overview
            }
            // Best-effort poster from the candidate's external image URL (hand-rolled
            // loader, no image lib — I-082). Frame background stands in until it loads.
            PosterLoader.load(candidate.imageUrl, row.findViewById(R.id.candidate_poster))
            row.findViewById<Button>(R.id.candidate_apply_button).setOnClickListener {
                applyByResult(candidate)
            }
            // Whole-row tap also applies, matching dashboard tap-to-act ergonomics.
            row.setOnClickListener { applyByResult(candidate) }
            resultsContainer.addView(row)
        }
    }

    private fun applyByResult(candidate: RemoteSearchCandidate) {
        setApplyStatus(activity.getString(R.string.correction_applying))
        api.applyCorrectionByResult(item.id, candidate) { result ->
            handleApplyResult(result)
        }
    }

    private fun applyPasted() {
        val pasted = pasteField.text.toString().trim()
        if (pasted.isEmpty()) {
            Toast.makeText(activity, R.string.correction_paste_empty, Toast.LENGTH_SHORT).show()
            return
        }
        // Accept either a bare id ("603") or a full TMDb URL the user copied — pull
        // the numeric id out of themoviedb.org/movie/603 or /tv/1399.
        val providerId = extractTmdbId(pasted)
        setApplyStatus(activity.getString(R.string.correction_applying))
        api.applyCorrectionByProvider(item.id, searchType, PROVIDER_TMDB, providerId) { result ->
            handleApplyResult(result)
        }
    }

    private fun handleApplyResult(result: Result<Unit>) {
        if (!dialog.isShowing) {
            // Dialog already dismissed; still surface the outcome so it's never silent.
            result.onSuccess {
                toast(activity.getString(R.string.correction_applied))
                onApplied()
            }.onFailure { error ->
                toast(activity.getString(R.string.correction_apply_failed, error.message ?: error.toString()))
            }
            return
        }
        result.onSuccess {
            applyStatus.visibility = View.GONE
            toast(activity.getString(R.string.correction_applied))
            dialog.dismiss()
            // W-064: apply may be async — the caller re-fetches to confirm the new
            // match, rather than us claiming it's done.
            onApplied()
        }.onFailure { error ->
            // W-056: surface the failure, don't swallow it.
            setApplyStatus(
                activity.getString(R.string.correction_apply_failed, error.message ?: error.toString()),
            )
        }
    }

    private fun openTmdb() {
        val query = searchField.text.toString().trim().ifEmpty { item.name }
        // TMDb's public search URL; the type token narrows movie vs tv.
        val typePath = if (searchType == LibraryItemType.SERIES) "tv" else "movie"
        val url = "https://www.themoviedb.org/search/$typePath?query=" +
            Uri.encode(query)
        val intent = Intent(Intent.ACTION_VIEW, Uri.parse(url))
        try {
            activity.startActivity(intent)
        } catch (_: ActivityNotFoundException) {
            Toast.makeText(activity, R.string.correction_no_browser, Toast.LENGTH_LONG).show()
        }
    }

    private fun setSearchStatus(text: String) {
        searchStatus.visibility = View.VISIBLE
        searchStatus.text = text
    }

    private fun setApplyStatus(text: String) {
        applyStatus.visibility = View.VISIBLE
        applyStatus.text = text
    }

    private fun toast(text: String) {
        Toast.makeText(activity, text, Toast.LENGTH_LONG).show()
    }

    private fun displayName(item: LibraryItem): String = buildString {
        append(item.name)
        item.year?.let { append(" ($it)") }
    }

    companion object {
        const val PROVIDER_TMDB = "Tmdb"

        /**
         * Extract a TMDb numeric id from either a bare id ("603") or a URL the user
         * pasted (`https://www.themoviedb.org/movie/603-the-matrix`, `/tv/1399`).
         * Pure/testable: returns the first run of digits, or the trimmed input
         * unchanged when there are none (letting the server reject it with a clear
         * message rather than us guessing).
         */
        fun extractTmdbId(pasted: String): String {
            val trimmed = pasted.trim()
            // Prefer the id segment after /movie/ or /tv/ if this looks like a URL.
            val urlMatch = Regex("/(?:movie|tv)/(\\d+)").find(trimmed)
            if (urlMatch != null) return urlMatch.groupValues[1]
            val digits = Regex("\\d+").find(trimmed)
            return digits?.value ?: trimmed
        }
    }
}
