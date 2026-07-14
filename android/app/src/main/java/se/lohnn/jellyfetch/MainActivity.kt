package se.lohnn.jellyfetch

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Menu
import android.view.MenuItem
import android.view.View
import android.widget.ListView
import android.widget.TextView
import android.widget.Toast
import androidx.core.widget.ListViewCompat
import androidx.swiperefreshlayout.widget.SwipeRefreshLayout
import se.lohnn.jellyfetch.api.ApiClient
import se.lohnn.jellyfetch.api.Job

/**
 * Download dashboard: polls the job list while foregrounded (no
 * push/websocket ceremony — spec explicitly calls this sufficient).
 */
class MainActivity : Activity() {

    private val pollHandler = Handler(Looper.getMainLooper())
    private val pollIntervalMs = 3000L
    private var pollRunnable: Runnable? = null

    private lateinit var prefs: Prefs
    private lateinit var swipeRefresh: SwipeRefreshLayout
    private lateinit var listView: ListView
    private lateinit var emptyView: TextView
    private lateinit var statusBanner: TextView
    private lateinit var adapter: JobsAdapter

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        prefs = Prefs(this)

        swipeRefresh = findViewById(R.id.swipe_refresh)
        listView = findViewById(R.id.job_list)
        emptyView = findViewById(R.id.empty_view)
        statusBanner = findViewById(R.id.status_banner)

        adapter = JobsAdapter(
            context = this,
            onOpenDetail = { job -> JobDetailActivity.start(this, job) },
            onCancel = { job -> ApiClient.current.cancelJob(job.id) { pollNow() } },
            onRetry = { job -> ApiClient.current.retryJob(job.id) { pollNow() } },
            onRemove = { job -> confirmRemove(job) },
        )
        listView.adapter = adapter

        // SwipeRefreshLayout decides whether to intercept a downward drag (i.e. whether
        // the user is "at the top, pulling to refresh" vs. "scrolling back up through a
        // scrolled-down list") by inspecting its *direct child's* scroll state. Our direct
        // child is the FrameLayout wrapper (ListView + empty-state overlay), not the
        // ListView itself — a FrameLayout is never "scrolled", so the default check always
        // reports canChildScrollUp() == false and SRL swallows every downward drag as a
        // refresh gesture, no matter how far down the list is scrolled. That's exactly why
        // scrolling down worked but scrolling back up did not. Point SRL at the real
        // scrollable target explicitly.
        swipeRefresh.setOnChildScrollUpCallback { _, _ ->
            ListViewCompat.canScrollList(listView, -1)
        }

        // SwipeRefreshLayout's spinner circle defaults to a hardcoded WHITE
        // background (setProgressBackgroundColorSchemeColor's default),
        // completely untouched by any theme attribute or day/night resource
        // qualifier — left alone, it renders as a jarring white blob floating
        // over the dark background in night mode. Point it at our own
        // (day/night-qualified) surface color instead, and tint the spinner
        // ring with the brand accent so it stays visually consistent with
        // the rest of the app in both themes.
        swipeRefresh.setProgressBackgroundColorSchemeResource(R.color.jf_background)
        swipeRefresh.setColorSchemeResources(R.color.jf_primary)

        swipeRefresh.setOnRefreshListener { pollNow() }
    }

    override fun onResume() {
        super.onResume()
        if (!prefs.isConfigured) {
            statusBanner.visibility = View.VISIBLE
            statusBanner.text = getString(R.string.dashboard_not_configured)
        }
        startPolling()
    }

    override fun onPause() {
        super.onPause()
        stopPolling()
    }

    private fun startPolling() {
        stopPolling()
        val runnable = object : Runnable {
            override fun run() {
                pollNow()
                pollHandler.postDelayed(this, pollIntervalMs)
            }
        }
        pollRunnable = runnable
        pollHandler.post(runnable)
    }

    private fun stopPolling() {
        pollRunnable?.let { pollHandler.removeCallbacks(it) }
        pollRunnable = null
    }

    private fun pollNow() {
        ApiClient.current.listJobs { result ->
            swipeRefresh.isRefreshing = false
            result.onSuccess { jobs -> renderJobs(jobs) }
                .onFailure { error -> renderUnreachable(error) }
        }
    }

    private fun renderJobs(jobs: List<Job>) {
        statusBanner.visibility = if (prefs.isConfigured) View.GONE else View.VISIBLE
        if (!prefs.isConfigured) statusBanner.text = getString(R.string.dashboard_not_configured)

        adapter.submitList(jobs)
        emptyView.visibility = if (jobs.isEmpty()) View.VISIBLE else View.GONE
        listView.visibility = if (jobs.isEmpty()) View.GONE else View.VISIBLE
    }

    private fun renderUnreachable(error: Throwable) {
        statusBanner.visibility = View.VISIBLE
        statusBanner.text = getString(R.string.dashboard_unreachable, error.message ?: error.toString())
        // Keep whatever the list already showed — don't blank a working view
        // just because one poll failed.
    }

    /**
     * Confirms before removing (destructive, and previously fired with zero
     * confirmation on tap). Plain platform android.app.AlertDialog.Builder(this)
     * — no AppCompat/Material dependency (I-081/I-082) — inherits its style
     * from the Activity's own theme, which is already day/night-aware
     * (Theme.JellyFetch / values-night/themes.xml), so the dialog reads
     * correctly in both light and dark automatically; nothing dialog-specific
     * to theme by hand.
     */
    private fun confirmRemove(job: Job) {
        AlertDialog.Builder(this)
            .setTitle(R.string.job_remove_confirm_title)
            .setMessage(R.string.job_remove_confirm_message)
            .setPositiveButton(R.string.job_remove) { _, _ -> removeJob(job) }
            .setNegativeButton(R.string.job_remove_confirm_cancel, null)
            .show()
    }

    private fun removeJob(job: Job) {
        ApiClient.current.removeJob(job.id) { result ->
            result.onSuccess { pollNow() }
                .onFailure { error ->
                    // Gating removeButton on job.state.isTerminal (JobsAdapter)
                    // means the server's 409-on-active-job case shouldn't reach
                    // here via the UI anymore, but a real failure (network,
                    // race with another client, etc.) was previously discarded
                    // silently — surface it instead, matching the Toast pattern
                    // ShareActivity already uses for its own send-failure case.
                    Toast.makeText(
                        this,
                        getString(R.string.job_remove_failed, error.message ?: error.toString()),
                        Toast.LENGTH_LONG,
                    ).show()
                }
        }
    }

    override fun onCreateOptionsMenu(menu: Menu): Boolean {
        menuInflater.inflate(R.menu.main_menu, menu)
        return true
    }

    override fun onOptionsItemSelected(item: MenuItem): Boolean {
        return when (item.itemId) {
            R.id.action_settings -> {
                startActivity(Intent(this, SettingsActivity::class.java))
                true
            }
            R.id.action_all_items -> {
                startActivity(Intent(this, AllItemsActivity::class.java))
                true
            }
            else -> super.onOptionsItemSelected(item)
        }
    }
}
