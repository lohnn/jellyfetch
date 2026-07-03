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
        view.findViewById<TextView>(R.id.job_state).text = stateLabel(job.state)

        val progressBar = view.findViewById<ProgressBar>(R.id.job_progress)
        val subtitle = view.findViewById<TextView>(R.id.job_subtitle)

        when (job.state) {
            JobState.FAILED -> {
                progressBar.visibility = View.GONE
                subtitle.visibility = View.VISIBLE
                subtitle.text = job.errorMessage ?: context.getString(R.string.job_error_unknown)
            }
            JobState.QUEUED, JobState.RESOLVING, JobState.PROCESSING -> {
                progressBar.visibility = View.VISIBLE
                progressBar.isIndeterminate = true
                subtitle.visibility = View.GONE
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
                )
                subtitle.visibility = if (parts.isEmpty()) View.GONE else View.VISIBLE
                subtitle.text = parts.joinToString(" · ")
            }
            JobState.COMPLETED -> {
                progressBar.visibility = View.VISIBLE
                progressBar.isIndeterminate = false
                progressBar.progress = 100
                subtitle.visibility = View.GONE
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
        removeButton.visibility = View.VISIBLE

        cancelButton.setOnClickListener { onCancel(job) }
        retryButton.setOnClickListener { onRetry(job) }
        removeButton.setOnClickListener { onRemove(job) }

        return view
    }

    private fun stateLabel(state: JobState): String = when (state) {
        JobState.QUEUED -> context.getString(R.string.state_queued)
        JobState.RESOLVING -> context.getString(R.string.state_resolving)
        JobState.DOWNLOADING -> context.getString(R.string.state_downloading)
        JobState.PROCESSING -> context.getString(R.string.state_processing)
        JobState.COMPLETED -> context.getString(R.string.state_completed)
        JobState.FAILED -> context.getString(R.string.state_failed)
        JobState.CANCELLED -> context.getString(R.string.state_cancelled)
    }
}
