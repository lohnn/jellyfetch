package se.lohnn.jellyfetch

import android.app.Activity
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
            onRemove = { job -> ApiClient.current.removeJob(job.id) { pollNow() } },
        )
        listView.adapter = adapter

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

    override fun onCreateOptionsMenu(menu: Menu): Boolean {
        menuInflater.inflate(R.menu.main_menu, menu)
        return true
    }

    override fun onOptionsItemSelected(item: MenuItem): Boolean {
        if (item.itemId == R.id.action_settings) {
            startActivity(Intent(this, SettingsActivity::class.java))
            return true
        }
        return super.onOptionsItemSelected(item)
    }
}
