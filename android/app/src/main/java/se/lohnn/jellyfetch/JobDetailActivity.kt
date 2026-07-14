package se.lohnn.jellyfetch

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.util.TypedValue
import android.view.LayoutInflater
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import se.lohnn.jellyfetch.api.ApiClient
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.api.JobState

/**
 * Tap-to-expand detail view for a single dashboard row (user-requested
 * follow-on to the flat-row dashboard). Renders immediately from the [Job]
 * the list already had (passed via [EXTRA_JOB], since [Job] is
 * [java.io.Serializable]) — full ErrorMessage and FinalPaths are already on
 * that object (the list endpoint carries them same as detail; only
 * [Job.children] is detail-only) — then refreshes via
 * [se.lohnn.jellyfetch.api.JellyFetchApi.getJobDetail] to pick up
 * [Job.children] for a group parent and any state change since the last poll.
 *
 * Per-child expand (error / final paths) is rendered INLINE from the same
 * detail response — jellyfin-plugin confirmed each child JobDto already
 * carries its own independent ErrorMessage/FinalPaths, so no extra
 * network round-trip per episode is needed.
 */
class JobDetailActivity : Activity() {

    private lateinit var statusBanner: TextView
    private lateinit var titleText: TextView
    private lateinit var categoryText: TextView
    private lateinit var stateText: TextView
    private lateinit var progressBar: ProgressBar
    private lateinit var progressSubtitle: TextView
    private lateinit var sourceUrl: TextView
    private lateinit var created: TextView
    private lateinit var updated: TextView
    private lateinit var completed: TextView
    private lateinit var errorSection: View
    private lateinit var errorText: TextView
    private lateinit var copyErrorButton: Button
    private lateinit var pathsSection: View
    private lateinit var pathsContainer: LinearLayout
    private lateinit var episodesSection: View
    private lateinit var episodesTitle: TextView
    private lateinit var episodesContainer: LinearLayout
    private lateinit var metadataSection: View
    private lateinit var metadataStatus: TextView
    private lateinit var metadataCard: View
    private lateinit var metadataName: TextView
    private lateinit var metadataYear: TextView
    private lateinit var metadataProviders: TextView
    private lateinit var metadataHint: TextView
    private lateinit var metadataFixButton: Button
    private lateinit var metadataPoster: android.widget.ImageView
    private lateinit var metadataPosterPlaceholder: TextView

    private var jobId: String = ""

    /** The current Jellyfin match for this job, once resolved — drives the picker. */
    private var libraryItem: se.lohnn.jellyfetch.api.LibraryItem? = null

    /**
     * Guards against a duplicate metadata fetch: [render] runs twice on open (once
     * from the intent-supplied job, once from the detail refresh), and both call
     * [renderMetadata]. Without this, two LibraryMatch requests could fire before
     * the first sets [libraryItem]. Reset to false only when we deliberately
     * re-load (after an apply, to confirm the new match — W-064).
     */
    private var metadataRequested: Boolean = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_job_detail)
        setTitle(R.string.job_detail_title)
        actionBar?.setDisplayHomeAsUpEnabled(true)

        statusBanner = findViewById(R.id.detail_status_banner)
        titleText = findViewById(R.id.detail_title)
        categoryText = findViewById(R.id.detail_category)
        stateText = findViewById(R.id.detail_state)
        progressBar = findViewById(R.id.detail_progress)
        progressSubtitle = findViewById(R.id.detail_progress_subtitle)
        sourceUrl = findViewById(R.id.detail_source_url)
        created = findViewById(R.id.detail_created)
        updated = findViewById(R.id.detail_updated)
        completed = findViewById(R.id.detail_completed)
        errorSection = findViewById(R.id.detail_error_section)
        errorText = findViewById(R.id.detail_error_text)
        copyErrorButton = findViewById(R.id.detail_copy_error_button)
        pathsSection = findViewById(R.id.detail_paths_section)
        pathsContainer = findViewById(R.id.detail_paths_container)
        episodesSection = findViewById(R.id.detail_episodes_section)
        episodesTitle = findViewById(R.id.detail_episodes_title)
        episodesContainer = findViewById(R.id.detail_episodes_container)
        metadataSection = findViewById(R.id.detail_metadata_section)
        metadataStatus = findViewById(R.id.detail_metadata_status)
        metadataCard = findViewById(R.id.detail_metadata_card)
        metadataName = findViewById(R.id.detail_metadata_name)
        metadataYear = findViewById(R.id.detail_metadata_year)
        metadataProviders = findViewById(R.id.detail_metadata_providers)
        metadataHint = findViewById(R.id.detail_metadata_hint)
        metadataFixButton = findViewById(R.id.detail_metadata_fix_button)
        metadataPoster = findViewById(R.id.detail_metadata_poster)
        metadataPosterPlaceholder = findViewById(R.id.detail_metadata_poster_placeholder)

        @Suppress("DEPRECATION")
        val initialJob = intent.getSerializableExtra(EXTRA_JOB) as? Job
        jobId = initialJob?.id ?: intent.getStringExtra(EXTRA_JOB_ID) ?: run {
            finish()
            return
        }

        if (initialJob != null) render(initialJob)
        refresh()
    }

    private fun refresh() {
        ApiClient.current.getJobDetail(jobId) { result ->
            result.onSuccess { job ->
                statusBanner.visibility = View.GONE
                render(job)
            }.onFailure { error ->
                statusBanner.visibility = View.VISIBLE
                statusBanner.text = getString(R.string.job_detail_load_failed, error.message ?: error.toString())
                // Keep whatever the initial intent-supplied job already rendered —
                // a failed refresh shouldn't blank a screen that had something to show.
            }
        }
    }

    private fun render(job: Job) {
        titleText.text = job.title
        categoryText.bindOrGone(Formatters.categoryLabel(this, job.category)) { it }
        stateText.text = buildString {
            append(Formatters.stateLabel(this@JobDetailActivity, job.state))
            if (!job.statusText.isNullOrBlank()) append(" — ${job.statusText}")
        }

        renderProgress(job)

        sourceUrl.bindOrGone(job.sourceUrl?.takeIf { it.isNotBlank() }) {
            getString(R.string.job_detail_source_url, it)
        }
        created.bindOrGone(Formatters.timestamp(job.createdAt)) { getString(R.string.job_detail_created, it) }
        updated.bindOrGone(Formatters.timestamp(job.updatedAt)) { getString(R.string.job_detail_updated, it) }
        completed.bindOrGone(Formatters.timestamp(job.completedAt)) { getString(R.string.job_detail_completed, it) }

        renderError(job)
        renderPaths(job)
        renderEpisodes(job)
        renderMetadata(job)
    }

    /**
     * The "Jellyfin match" block: only meaningful for a COMPLETED, non-group job
     * (a group parent isn't itself a library item — its children are). Fetches
     * the current Jellyfin match lazily and lets the user open the correction
     * picker. Tolerant-of-absence (I-134): shows a graceful "no match" or error
     * line rather than blanking, and never blocks the rest of the screen.
     */
    private fun renderMetadata(job: Job) {
        val eligible = job.state == JobState.COMPLETED && !job.isGroup
        metadataSection.visibility = if (eligible) View.VISIBLE else View.GONE
        if (!eligible) return

        // Only load once per screen unless we already have it (e.g. from a prior
        // refresh); the twice-called render (initial job + detail refresh) must not
        // fire two requests. If already loaded, just re-bind.
        val existing = libraryItem
        if (existing != null) {
            bindMetadata(existing)
            return
        }
        if (metadataRequested) return
        loadMetadata()
    }

    private fun loadMetadata() {
        metadataRequested = true
        metadataStatus.visibility = View.VISIBLE
        metadataStatus.text = getString(R.string.metadata_loading)
        metadataCard.visibility = View.GONE
        metadataHint.visibility = View.GONE
        metadataFixButton.visibility = View.GONE

        ApiClient.current.getJobLibraryItem(jobId) { result ->
            result.onSuccess { item ->
                libraryItem = item
                if (item == null) {
                    metadataStatus.visibility = View.VISIBLE
                    metadataStatus.text = getString(R.string.metadata_none)
                    metadataCard.visibility = View.GONE
                    metadataHint.visibility = View.GONE
                    metadataFixButton.visibility = View.GONE
                } else {
                    metadataStatus.visibility = View.GONE
                    bindMetadata(item)
                }
            }.onFailure { error ->
                metadataStatus.visibility = View.VISIBLE
                metadataStatus.text =
                    getString(R.string.metadata_load_failed, error.message ?: error.toString())
                metadataCard.visibility = View.GONE
            }
        }
    }

    private fun bindMetadata(item: se.lohnn.jellyfetch.api.LibraryItem) {
        metadataStatus.visibility = View.GONE
        metadataCard.visibility = View.VISIBLE
        metadataName.text = item.name

        val typeLabel = item.type?.let {
            when (it) {
                se.lohnn.jellyfetch.api.LibraryItemType.MOVIE -> getString(R.string.category_movie)
                se.lohnn.jellyfetch.api.LibraryItemType.SERIES -> getString(R.string.category_series)
            }
        }
        val yearTypeParts = listOfNotNull(item.year?.toString(), typeLabel)
        metadataYear.bindOrGone(yearTypeParts.joinToString(" · ").ifBlank { null }) { it }

        val providerLabel = item.providerIds.entries
            .joinToString(" · ") { "${it.key} ${it.value}" }
        metadataProviders.bindOrGone(providerLabel.ifBlank { null }) { it }

        // Best-effort poster (hand-rolled loader, no image lib — I-082). Placeholder
        // stays visible until/unless the bitmap loads.
        PosterLoader.load(item.posterUrl, metadataPoster, metadataPosterPlaceholder)

        metadataHint.visibility = View.VISIBLE
        metadataFixButton.visibility = View.VISIBLE
        metadataFixButton.setOnClickListener {
            CorrectionDialog(
                activity = this,
                item = item,
                onApplied = { refreshed ->
                    // ALWAYS discard the stale cached item first — the live bug was the
                    // app re-rendering the pre-convert item (still Series) after a
                    // convert already re-typed it (to Movie) server-side.
                    libraryItem = null
                    metadataRequested = false
                    if (refreshed != null) {
                        // CONVERT handed us the freshly-resolved item (by its new file
                        // path) — bind to it directly so the CORRECT new type shows
                        // immediately, deterministically, matching Jellyfin's own UI.
                        libraryItem = refreshed
                        metadataRequested = true
                        bindMetadata(refreshed)
                    } else {
                        // Provider-id apply, or a convert whose rescan hadn't indexed
                        // yet — re-resolve from the server so we never show stale data.
                        loadMetadata()
                    }
                },
            ).show()
        }
    }

    private fun renderProgress(job: Job) {
        when (job.state) {
            JobState.FAILED, JobState.CANCELLED -> {
                progressBar.visibility = View.GONE
                progressSubtitle.visibility = View.GONE
            }
            JobState.QUEUED, JobState.RESOLVING, JobState.PROCESSING -> {
                progressBar.visibility = View.VISIBLE
                progressBar.isIndeterminate = true
                progressSubtitle.visibility = View.GONE
            }
            JobState.DOWNLOADING -> {
                progressBar.visibility = View.VISIBLE
                progressBar.isIndeterminate = job.progressPercent == null
                progressBar.progress = job.progressPercent ?: 0
                val parts = listOfNotNull(
                    job.progressPercent?.let { "$it%" },
                    Formatters.speed(job.speedBytesPerSec),
                    Formatters.eta(job.etaSeconds)?.let { getString(R.string.job_eta_format, it) },
                )
                progressSubtitle.visibility = if (parts.isEmpty()) View.GONE else View.VISIBLE
                progressSubtitle.text = parts.joinToString(" · ")
            }
            JobState.COMPLETED -> {
                progressBar.visibility = View.VISIBLE
                progressBar.isIndeterminate = false
                progressBar.progress = 100
                progressSubtitle.visibility = View.GONE
            }
        }
    }

    private fun renderError(job: Job) {
        val message = job.errorMessage?.takeIf { it.isNotBlank() }
        errorSection.visibility = if (message != null) View.VISIBLE else View.GONE
        if (message != null) {
            // Rendered in FULL, wrappable, and selectable — this can be a multi-sentence
            // message that includes a shell command (e.g. a placement-permission fix);
            // never truncate it, the user needs to act on it.
            errorText.text = message
            copyErrorButton.setOnClickListener {
                val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                clipboard.setPrimaryClip(ClipData.newPlainText("JellyFetch error", message))
                Toast.makeText(this, R.string.job_detail_error_copied, Toast.LENGTH_SHORT).show()
            }
        }
    }

    private fun renderPaths(job: Job) {
        pathsSection.visibility = if (job.finalPaths.isNotEmpty()) View.VISIBLE else View.GONE
        pathsContainer.removeAllViews()
        for (path in job.finalPaths) {
            pathsContainer.addView(pathTextView(path))
        }
    }

    private fun pathTextView(path: String): TextView = TextView(this).apply {
        text = path
        textSize = 13f
        setTextIsSelectable(true)
        setPadding(0, 4, 0, 4)
    }

    private fun renderEpisodes(job: Job) {
        episodesSection.visibility = if (job.isGroup) View.VISIBLE else View.GONE
        if (!job.isGroup) return

        episodesTitle.text = getString(R.string.job_detail_episodes_title, job.childCount)
        episodesContainer.removeAllViews()

        val children = job.children
        if (children == null) {
            episodesContainer.addView(
                TextView(this).apply {
                    text = getString(R.string.job_detail_loading)
                    textSize = 13f
                    // Was a hardcoded 0xFF757575 (dark-mode-broken: same gray
                    // regardless of theme). Resolve the theme's own secondary
                    // text color at runtime instead — day/night correct for
                    // free, same as the XML-declared TextViews elsewhere in
                    // this screen that use ?android:attr/textColorSecondary.
                    setTextColor(resolveThemeColor(android.R.attr.textColorSecondary))
                },
            )
            return
        }

        val inflater = LayoutInflater.from(this)
        for (child in children) {
            episodesContainer.addView(buildChildRow(inflater, child))
        }
    }

    private fun buildChildRow(inflater: LayoutInflater, child: Job): View {
        val row = inflater.inflate(R.layout.item_job_child, episodesContainer, false)

        row.findViewById<TextView>(R.id.child_label).text = child.episodeLabel
        row.findViewById<TextView>(R.id.child_state).text = Formatters.stateLabel(this, child.state)

        val progress = row.findViewById<ProgressBar>(R.id.child_progress)
        val subtitle = row.findViewById<TextView>(R.id.child_subtitle)
        when (child.state) {
            JobState.DOWNLOADING -> {
                progress.visibility = View.VISIBLE
                progress.isIndeterminate = child.progressPercent == null
                progress.progress = child.progressPercent ?: 0
                val parts = listOfNotNull(
                    child.progressPercent?.let { "$it%" },
                    Formatters.speed(child.speedBytesPerSec),
                    Formatters.eta(child.etaSeconds)?.let { getString(R.string.job_eta_format, it) },
                )
                subtitle.visibility = if (parts.isEmpty()) View.GONE else View.VISIBLE
                subtitle.text = parts.joinToString(" · ")
            }
            JobState.QUEUED, JobState.RESOLVING, JobState.PROCESSING -> {
                progress.visibility = View.VISIBLE
                progress.isIndeterminate = true
                subtitle.visibility = View.GONE
            }
            JobState.COMPLETED -> {
                progress.visibility = View.VISIBLE
                progress.isIndeterminate = false
                progress.progress = 100
                subtitle.visibility = View.GONE
            }
            JobState.FAILED, JobState.CANCELLED -> {
                progress.visibility = View.GONE
                subtitle.visibility = View.GONE
            }
        }

        val childErrorText = row.findViewById<TextView>(R.id.child_error_text)
        val childPathsContainer = row.findViewById<LinearLayout>(R.id.child_paths_container)
        val childDetail = row.findViewById<View>(R.id.child_detail)
        val chevron = row.findViewById<TextView>(R.id.child_chevron)

        val message = child.errorMessage?.takeIf { it.isNotBlank() }
        if (message != null) {
            childErrorText.visibility = View.VISIBLE
            childErrorText.text = message
        } else {
            childErrorText.visibility = View.GONE
        }
        childPathsContainer.removeAllViews()
        for (path in child.finalPaths) {
            childPathsContainer.addView(pathTextView(path))
        }

        val hasDetail = message != null || child.finalPaths.isNotEmpty()
        chevron.visibility = if (hasDetail) View.VISIBLE else View.GONE
        val header = row.findViewById<View>(R.id.child_header)
        if (hasDetail) {
            header.setOnClickListener {
                childDetail.visibility = if (childDetail.visibility == View.VISIBLE) View.GONE else View.VISIBLE
            }
        } else {
            header.isClickable = false
        }

        return row
    }

    override fun onOptionsItemSelected(item: android.view.MenuItem): Boolean {
        if (item.itemId == android.R.id.home) {
            finish()
            return true
        }
        return super.onOptionsItemSelected(item)
    }

    /** Small helper: sets text via [format] and toggles visibility, or hides when [value] is null/blank. */
    private inline fun TextView.bindOrGone(value: String?, format: (String) -> String) {
        if (value.isNullOrBlank()) {
            visibility = View.GONE
        } else {
            visibility = View.VISIBLE
            text = format(value)
        }
    }

    /**
     * Resolves a theme attribute (e.g. [android.R.attr.textColorSecondary]) to
     * its current resolved color for the active theme/night-mode — the
     * programmatic equivalent of an XML `?android:attr/...` reference, for
     * the one TextView this screen builds in code rather than inflates.
     */
    private fun resolveThemeColor(attr: Int): Int {
        val typedValue = TypedValue()
        theme.resolveAttribute(attr, typedValue, true)
        return typedValue.data
    }

    companion object {
        const val EXTRA_JOB = "extra_job"
        const val EXTRA_JOB_ID = "extra_job_id"

        fun start(activity: Activity, job: Job) {
            activity.startActivity(
                Intent(activity, JobDetailActivity::class.java).putExtra(EXTRA_JOB, job),
            )
        }
    }
}
