package se.lohnn.jellyfetch

import android.content.Context
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.BaseAdapter
import android.widget.Button
import android.widget.ProgressBar
import android.widget.TextView
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.api.JobState

class JobsAdapter(
    private val context: Context,
    private val onOpenDetail: (Job) -> Unit,
    private val onCancel: (Job) -> Unit,
    private val onRetry: (Job) -> Unit,
    private val onRemove: (Job) -> Unit,
) : BaseAdapter() {

    private var jobs: List<Job> = emptyList()

    fun submitList(newJobs: List<Job>) {
        jobs = newJobs
        notifyDataSetChanged()
    }

    override fun getCount(): Int = jobs.size
    override fun getItem(position: Int): Job = jobs[position]
    override fun getItemId(position: Int): Long = jobs[position].id.hashCode().toLong()

    override fun getView(position: Int, convertView: View?, parent: ViewGroup): View {
        val view = convertView ?: LayoutInflater.from(context).inflate(R.layout.item_job, parent, false)
        val job = jobs[position]

        view.findViewById<TextView>(R.id.job_title).text = job.title
        view.findViewById<TextView>(R.id.job_state).text = Formatters.stateLabel(context, job.state)
        // Every row is tap-to-expand now (detail view: full error, final paths,
        // and — for a group — the per-episode list). The chevron just hints at it.
        view.findViewById<TextView>(R.id.job_chevron).visibility = View.VISIBLE
        view.setOnClickListener { onOpenDetail(job) }

        val progressBar = view.findViewById<ProgressBar>(R.id.job_progress)
        val subtitle = view.findViewById<TextView>(R.id.job_subtitle)

        when (job.state) {
            JobState.FAILED -> {
                progressBar.visibility = View.GONE
                subtitle.visibility = View.VISIBLE
                subtitle.maxLines = 2
                subtitle.text = job.errorMessage ?: context.getString(R.string.job_error_unknown)
            }
            JobState.QUEUED, JobState.RESOLVING, JobState.PROCESSING -> {
                progressBar.visibility = View.VISIBLE
                progressBar.isIndeterminate = true
                val status = job.statusText
                subtitle.visibility = if (status.isNullOrBlank()) View.GONE else View.VISIBLE
                subtitle.maxLines = 1
                subtitle.text = status
            }
            JobState.DOWNLOADING -> {
                progressBar.visibility = View.VISIBLE
                progressBar.isIndeterminate = job.progressPercent == null
                progressBar.progress = job.progressPercent ?: 0
                val speed = Formatters.speed(job.speedBytesPerSec)
                val eta = Formatters.eta(job.etaSeconds)
                val parts = listOfNotNull(
                    job.progressPercent?.let { "$it%" },
                    speed,
                    eta?.let { context.getString(R.string.job_eta_format, it) },
                    job.statusText?.takeIf { job.isGroup },
                )
                subtitle.visibility = if (parts.isEmpty()) View.GONE else View.VISIBLE
                subtitle.maxLines = 1
                subtitle.text = parts.joinToString(" · ")
            }
            JobState.COMPLETED -> {
                progressBar.visibility = View.VISIBLE
                progressBar.isIndeterminate = false
                progressBar.progress = 100
                subtitle.visibility = if (job.isGroup && !job.statusText.isNullOrBlank()) View.VISIBLE else View.GONE
                subtitle.maxLines = 1
                subtitle.text = job.statusText
            }
            JobState.CANCELLED -> {
                progressBar.visibility = View.GONE
                subtitle.visibility = View.GONE
            }
        }

        val cancelButton = view.findViewById<Button>(R.id.job_cancel_button)
        val retryButton = view.findViewById<Button>(R.id.job_retry_button)
        val removeButton = view.findViewById<Button>(R.id.job_remove_button)

        cancelButton.visibility = if (job.state.isCancellable) View.VISIBLE else View.GONE
        retryButton.visibility = if (job.state.isRetryable) View.VISIBLE else View.GONE
        // Server-side, DELETE /Downloads/{id} 409s on anything not terminal
        // (DownloadJobManager.Delete requires job.IsTerminal) — mirror that
        // constraint here the same way Cancel/Retry already mirror theirs,
        // instead of letting the user tap Remove on an active job only to
        // have it silently rejected.
        removeButton.visibility = if (job.state.isTerminal) View.VISIBLE else View.GONE

        cancelButton.setOnClickListener { onCancel(job) }
        retryButton.setOnClickListener { onRetry(job) }
        removeButton.setOnClickListener { onRemove(job) }

        return view
    }
}
