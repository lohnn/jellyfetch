package se.lohnn.jellyfetch

import android.app.Activity
import android.app.AlertDialog
import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.view.LayoutInflater
import android.view.View
import android.view.inputmethod.EditorInfo
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.RadioGroup
import android.widget.TextView
import android.widget.Toast
import se.lohnn.jellyfetch.api.ApiClient
import se.lohnn.jellyfetch.api.ConvertTarget
import se.lohnn.jellyfetch.api.ConvertTypeResult
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
 * Plus a third, distinct operation: TYPE CONVERT (Movie / Series / Other). Unlike
 * the two provider-id paths (synchronous), convert is ASYNC (W-064): the server
 * re-ingests (move files → delete old item → rescan) and the re-typed item gets a
 * NEW id that doesn't exist at return time. So convert shows a "converting… rescan
 * pending" state and POLLS the Items list to surface the new item. "Other"
 * relocates the item into the fallback library (e.g. a home video that shouldn't
 * count as a movie); its poll drops the type filter (the fallback library decides
 * the new kind), and a non-distinct-fallback 400 is surfaced verbatim (W-056).
 *
 * The provider-apply may be async server-side (W-064): on success we fire
 * [onApplied] so the caller re-fetches to confirm the new match actually landed.
 */
class CorrectionDialog(
    private val activity: Activity,
    private val item: LibraryItem,
    /**
     * Invoked after a successful apply/convert so the caller can refresh its
     * display (W-064). [refreshed] carries the freshly-resolved item when we have
     * it — after a CONVERT this is the re-typed item resolved by its new file path
     * ([JellyFetchApi.getItemByPath]), so the caller can rebind to the CORRECT new
     * type deterministically rather than re-resolving a stale/dead id. It is null
     * when we don't have a concrete item to hand back (a provider-id apply, or a
     * convert whose rescan hadn't indexed yet) — the caller should then re-fetch.
     */
    private val onApplied: (refreshed: LibraryItem?) -> Unit,
) {

    private val api get() = ApiClient.current
    private val searchType: LibraryItemType = item.type ?: LibraryItemType.MOVIE
    private val pollHandler = Handler(Looper.getMainLooper())

    /**
     * True once a convert has been FIRED (files move synchronously server-side, so
     * this id is immediately stale/dead). Latches the anti-double-convert guard:
     * the user must not re-convert this now-superseded id (that hits the server's
     * "no video files"/409 stale error). Never reset within this dialog instance —
     * the whole point is this item is done.
     */
    private var convertFired = false

    /** True while a convert's rescan poll is running — guards against double-fire. */
    private var converting = false

    private lateinit var dialog: AlertDialog
    private lateinit var searchField: EditText
    private lateinit var searchStatus: TextView
    private lateinit var resultsContainer: LinearLayout
    private lateinit var pasteField: EditText
    private lateinit var applyStatus: TextView
    private lateinit var typeSection: View
    private lateinit var typeGroup: RadioGroup
    private lateinit var convertButton: Button
    private lateinit var convertStatus: TextView

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
        typeSection = view.findViewById(R.id.correction_type_section)
        typeGroup = view.findViewById(R.id.correction_type_group)
        convertButton = view.findViewById(R.id.correction_convert_button)
        convertStatus = view.findViewById(R.id.correction_convert_status)

        setupTypeConvert(view)

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
        // Stop any in-flight rescan poll if the dialog goes away (I-137: Handler-posted
        // work must not outlive the view it targets).
        dialog.setOnDismissListener { pollHandler.removeCallbacksAndMessages(null) }
        dialog.show()
    }

    /**
     * Wire the Movie / Series / Other type-convert control. Only shown when the
     * item has a resolved Movie/Series type (the server rejects Episodes and
     * unknown types with 400 — mirror that guard by hiding the control entirely).
     * The RadioGroup starts on the current type; the Convert button enables only
     * when a DIFFERENT target is selected and labels itself "Convert to <target>".
     * "Other" (relocate to the fallback library — e.g. a home video) is always a
     * change from Movie/Series.
     */
    private fun setupTypeConvert(view: View) {
        val current = item.type
        if (current != LibraryItemType.MOVIE && current != LibraryItemType.SERIES) {
            // Not a convertible item (Episode / unknown): hide the whole section.
            typeSection.visibility = View.GONE
            return
        }
        typeSection.visibility = View.VISIBLE
        val currentTarget = ConvertTarget.of(current)

        // Check the current type; converting is only meaningful to a different one.
        val movieButton = view.findViewById<android.widget.RadioButton>(R.id.correction_type_movie)
        val seriesButton = view.findViewById<android.widget.RadioButton>(R.id.correction_type_series)
        when (currentTarget) {
            ConvertTarget.SERIES -> seriesButton.isChecked = true
            else -> movieButton.isChecked = true
        }

        fun selectedTarget(): ConvertTarget = when (typeGroup.checkedRadioButtonId) {
            R.id.correction_type_series -> ConvertTarget.SERIES
            R.id.correction_type_other -> ConvertTarget.OTHER
            else -> ConvertTarget.MOVIE
        }

        fun refreshConvertButton() {
            val target = selectedTarget()
            val isChange = target != currentTarget
            // Anti-double-convert (W-056): once a convert is fired this id is stale —
            // never re-enable, so the user can't act on the soon-deleted item.
            convertButton.isEnabled = isChange && !converting && !convertFired
            convertButton.text = activity.getString(R.string.correction_convert_button, target.wireName)
        }
        refreshConvertButton()

        typeGroup.setOnCheckedChangeListener { _, _ -> refreshConvertButton() }
        convertButton.setOnClickListener { confirmConvert(selectedTarget()) }
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
                // Provider-id apply keeps the SAME (still-valid) item id — hand back
                // null so the caller re-fetches to display the refreshed match.
                onApplied(null)
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
            onApplied(null)
        }.onFailure { error ->
            // W-056: surface the failure, don't swallow it.
            setApplyStatus(
                activity.getString(R.string.correction_apply_failed, error.message ?: error.toString()),
            )
        }
    }

    // --- Type convert (Movie / Series / Other) ------------------------------

    private fun confirmConvert(target: ConvertTarget) {
        // Destructive-ish (moves files, deletes+recreates the item) — confirm first.
        AlertDialog.Builder(activity)
            .setTitle(activity.getString(R.string.correction_convert_confirm_title, target.wireName))
            .setMessage(activity.getString(R.string.correction_convert_confirm_message, target.wireName))
            .setPositiveButton(R.string.correction_convert_confirm_ok) { _, _ -> doConvert(target) }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun doConvert(target: ConvertTarget) {
        converting = true
        // The moment we fire, the server moves the files synchronously — item.id is
        // now stale. Latch the anti-double-convert guard so the user can NEVER
        // re-convert this dead id from this dialog (root cause of the live "no video
        // files" bug), regardless of how the rescan poll turns out.
        convertFired = true
        convertButton.isEnabled = false
        setConvertStatus(activity.getString(R.string.correction_converting, target.wireName))

        api.convertType(item.id, target) { result ->
            result.onSuccess { convertResult ->
                // 202 rescan-pending: the new re-typed item doesn't exist yet. Resolve
                // it deterministically by its NEW file path (MovedPaths) — robust where
                // a title search drifts after Jellyfin re-fetches metadata (W-064).
                pollForConvertedItem(convertResult, attemptsLeft = POLL_MAX_ATTEMPTS)
            }.onFailure { error ->
                // W-056: surface the failure verbatim. This is where the "Other"
                // fallback-not-distinct 400 AND the 409 stale/superseded guard land —
                // both actionable, both shown. We do NOT re-enable convert: the id is
                // spent; the user must refresh to act on the current item.
                converting = false
                setConvertStatus(
                    activity.getString(R.string.correction_convert_failed, error.message ?: error.toString()),
                )
                toast(activity.getString(R.string.correction_convert_failed, error.message ?: error.toString()))
                lockDownAfterConvert()
            }
        }
    }

    /**
     * Resolves the newly re-typed item by its NEW FILE PATH (the deterministic
     * rebind): [ConvertTypeResult.movedPaths] are where the server put the files,
     * and [JellyFetchApi.getItemByPath] returns the freshly-rescanned item once the
     * scan indexes it — returning null until then. This replaces the old
     * title-search poll, which drifted (year suffix / provider rename) and left the
     * app showing the stale type — the live bug.
     *
     * On found: hand the FRESH item to [onApplied] so the caller rebinds to the
     * correct new type, Toast, dismiss. On timeout (I-134 tolerant-of-absence): the
     * rescan may still be running — Toast "pull to refresh", fire [onApplied]`(null)`
     * so the caller re-fetches, dismiss. Either way the stale item leaves the view.
     */
    private fun pollForConvertedItem(convertResult: ConvertTypeResult, attemptsLeft: Int) {
        if (!dialog.isShowing) return
        val target = convertResult.targetType
        // Most stable rebind key: ItemDirectory (the destination folder), else
        // MovedPaths[0] — NOT the title (rescan may rename it). See rebindPath.
        val rebindPath = convertResult.rebindPath

        // Fallback for a server that didn't return a path (shouldn't happen, but
        // tolerant-of-absence): fire onApplied(null) so the caller re-resolves.
        if (rebindPath == null) {
            converting = false
            toast(activity.getString(R.string.correction_convert_pending, target.wireName))
            onApplied(null)
            dialog.dismiss()
            return
        }

        api.getItemByPath(rebindPath) { result ->
            if (!dialog.isShowing) return@getItemByPath
            // getItemByPath: success+null = "not indexed yet, poll again"; failure =
            // a real transport error (surface, then let the caller re-fetch).
            val newItem = result.getOrNull()
            when {
                newItem != null -> {
                    converting = false
                    toast(activity.getString(R.string.correction_convert_found, target.wireName))
                    // Hand the caller the FRESH item so it displays the correct type.
                    onApplied(newItem)
                    dialog.dismiss()
                    offerPickerOnNewItem(newItem)
                }
                attemptsLeft <= 1 -> {
                    // Timed out waiting for the rescan — honest about it (don't fake
                    // completion). The caller re-fetches; the new item shows up later.
                    converting = false
                    toast(activity.getString(R.string.correction_convert_pending, target.wireName))
                    onApplied(null)
                    dialog.dismiss()
                }
                else -> {
                    // Not indexed yet (or a transient failure) — keep "converting…"
                    // and retry the by-path resolve.
                    pollHandler.postDelayed(
                        { pollForConvertedItem(convertResult, attemptsLeft - 1) },
                        POLL_INTERVAL_MS,
                    )
                }
            }
        }
    }

    /**
     * After a convert that couldn't be followed to the new item (error, or the
     * dialog stayed open on a stale id), lock the whole dialog down so the user
     * can't act on the now-dead item: disable convert permanently and hide the
     * provider-id correction controls (they'd hit the same dead id). The user is
     * directed to refresh (the Toast/status already said so).
     */
    private fun lockDownAfterConvert() {
        convertButton.isEnabled = false
        // Also disable the provider-id apply paths — same dead id.
        dialog.findViewById<Button>(R.id.correction_search_button)?.isEnabled = false
        dialog.findViewById<Button>(R.id.correction_paste_apply_button)?.isEnabled = false
        resultsContainer.removeAllViews()
    }

    /**
     * After a successful convert, the user may also want a provider-id fix on the
     * freshly re-typed item (it now searches the CORRECT type's database). Offer to
     * reopen the picker on the new item — the whole point of "converting re-points
     * the remote search to the new type". The new dialog operates on the FRESH
     * item id (valid), never the old dead one.
     */
    private fun offerPickerOnNewItem(newItem: LibraryItem) {
        AlertDialog.Builder(activity)
            .setMessage(activity.getString(R.string.correction_open_new_item, newItem.type?.wireName ?: newItem.name))
            .setPositiveButton(R.string.metadata_fix_button) { _, _ ->
                CorrectionDialog(activity, newItem, onApplied).show()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun setConvertStatus(text: String) {
        convertStatus.visibility = View.VISIBLE
        convertStatus.text = text
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

        /** Rescan poll cadence: ~2s × 8 ≈ 16s before falling back to "pull to refresh". */
        private const val POLL_INTERVAL_MS = 2000L
        private const val POLL_MAX_ATTEMPTS = 8

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
