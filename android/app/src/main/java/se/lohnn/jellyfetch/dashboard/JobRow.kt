package se.lohnn.jellyfetch.dashboard

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import se.lohnn.jellyfetch.Formatters
import se.lohnn.jellyfetch.R
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.api.JobCategory
import se.lohnn.jellyfetch.api.JobState
import se.lohnn.jellyfetch.ui.theme.JfTheme

/**
 * One dashboard row — the Compose port of item_job.xml + JobsAdapter.getView.
 * Preserves the exact per-state rendering rules of the classic adapter (which
 * progress bar / subtitle / which action buttons show) and the W-056 fix: the
 * action buttons are gated on state AND their failures surface (via the
 * ViewModel's message channel — see [DashboardViewModel]). Remove additionally
 * confirms first (history-removal only, never deletes files).
 */
@Composable
fun JobRow(
    job: Job,
    onOpenJob: (Job) -> Unit,
    onCancel: (Job) -> Unit,
    onRetry: (Job) -> Unit,
    onRemove: (Job) -> Unit,
) {
    var confirmRemove by remember(job.id) { mutableStateOf(false) }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clickable { onOpenJob(job) }
            .padding(horizontal = 16.dp, vertical = 12.dp),
    ) {
        // Title row + category badge + chevron.
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = job.title,
                style = MaterialTheme.typography.titleMedium,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            CategoryBadge(job.category)
            Spacer(Modifier.width(8.dp))
            Text(
                text = "\u203A", // ›
                style = MaterialTheme.typography.titleLarge,
                color = JfTheme.colors.chevron,
            )
        }

        Spacer(Modifier.height(2.dp))
        Text(
            text = stateLabel(job.state),
            style = MaterialTheme.typography.labelMedium,
            color = JfTheme.colors.stateAccent,
            fontWeight = FontWeight.Medium,
        )

        // Per-state progress + subtitle (mirrors JobsAdapter's when-block exactly).
        JobProgressAndSubtitle(job)

        // Actions — gated exactly as the server gates them.
        val showCancel = job.state.isCancellable
        val showRetry = job.state.isRetryable
        val showRemove = job.state.isTerminal
        if (showCancel || showRetry || showRemove) {
            Spacer(Modifier.height(8.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                if (showCancel) {
                    OutlinedButton(onClick = { onCancel(job) }) {
                        Text(stringResource(R.string.job_cancel))
                    }
                }
                if (showRetry) {
                    OutlinedButton(onClick = { onRetry(job) }) {
                        Text(stringResource(R.string.job_retry))
                    }
                }
                if (showRemove) {
                    OutlinedButton(onClick = { confirmRemove = true }) {
                        Text(stringResource(R.string.job_remove))
                    }
                }
            }
        }
    }

    if (confirmRemove) {
        AlertDialog(
            onDismissRequest = { confirmRemove = false },
            title = { Text(stringResource(R.string.job_remove_confirm_title)) },
            text = { Text(stringResource(R.string.job_remove_confirm_message)) },
            confirmButton = {
                TextButton(onClick = {
                    confirmRemove = false
                    onRemove(job)
                }) { Text(stringResource(R.string.job_remove)) }
            },
            dismissButton = {
                TextButton(onClick = { confirmRemove = false }) {
                    Text(stringResource(R.string.job_remove_confirm_cancel))
                }
            },
        )
    }
}

@Composable
private fun JobProgressAndSubtitle(job: Job) {
    when (job.state) {
        JobState.FAILED -> {
            Spacer(Modifier.height(4.dp))
            Subtitle(
                text = job.errorMessage ?: stringResource(R.string.job_error_unknown),
                color = MaterialTheme.colorScheme.error,
                maxLines = 2,
            )
        }

        JobState.QUEUED, JobState.RESOLVING, JobState.PROCESSING -> {
            Spacer(Modifier.height(6.dp))
            IndeterminateBar()
            job.statusText?.takeIf { it.isNotBlank() }?.let {
                Subtitle(it, maxLines = 1)
            }
        }

        JobState.DOWNLOADING -> {
            Spacer(Modifier.height(6.dp))
            val percent = job.progressPercent
            if (percent == null) {
                IndeterminateBar()
            } else {
                DeterminateBar(percent)
            }
            val parts = buildList {
                percent?.let { add("$it%") }
                Formatters.speed(job.speedBytesPerSec)?.let { add(it) }
                Formatters.eta(job.etaSeconds)?.let { add("ETA $it") }
                if (job.isGroup) job.statusText?.let { add(it) }
            }
            if (parts.isNotEmpty()) {
                Subtitle(parts.joinToString("  ·  "), maxLines = 1)
            }
        }

        JobState.COMPLETED -> {
            Spacer(Modifier.height(6.dp))
            DeterminateBar(100)
            if (job.isGroup && !job.statusText.isNullOrBlank()) {
                Subtitle(job.statusText!!, maxLines = 1)
            }
        }

        JobState.CANCELLED -> {
            // No progress bar, no subtitle — matches the classic adapter.
        }
    }
}

@Composable
private fun IndeterminateBar() {
    LinearProgressIndicator(
        modifier = Modifier
            .fillMaxWidth()
            .height(4.dp)
            .clip(RoundedCornerShape(2.dp)),
        color = MaterialTheme.colorScheme.primary,
        trackColor = JfTheme.colors.divider,
    )
}

@Composable
private fun DeterminateBar(percent: Int) {
    LinearProgressIndicator(
        progress = { percent.coerceIn(0, 100) / 100f },
        modifier = Modifier
            .fillMaxWidth()
            .height(4.dp)
            .clip(RoundedCornerShape(2.dp)),
        color = MaterialTheme.colorScheme.primary,
        trackColor = JfTheme.colors.divider,
    )
}

@Composable
private fun Subtitle(
    text: String,
    color: androidx.compose.ui.graphics.Color = MaterialTheme.colorScheme.onBackground.copy(alpha = 0.7f),
    maxLines: Int = 1,
) {
    Spacer(Modifier.height(4.dp))
    Text(
        text = text,
        style = MaterialTheme.typography.bodySmall,
        color = color,
        maxLines = maxLines,
        overflow = TextOverflow.Ellipsis,
    )
}

@Composable
private fun CategoryBadge(category: JobCategory?) {
    val label = when (category) {
        JobCategory.MOVIE -> stringResource(R.string.category_movie)
        JobCategory.SERIES -> stringResource(R.string.category_series)
        JobCategory.OTHER -> stringResource(R.string.category_other)
        null -> return
    }
    Text(
        text = label,
        style = MaterialTheme.typography.labelSmall,
        color = MaterialTheme.colorScheme.onPrimaryContainer,
        modifier = Modifier
            .clip(RoundedCornerShape(4.dp))
            .background(MaterialTheme.colorScheme.primaryContainer)
            .padding(horizontal = 6.dp, vertical = 2.dp),
    )
}

@Composable
private fun stateLabel(state: JobState): String = stringResource(
    when (state) {
        JobState.QUEUED -> R.string.state_queued
        JobState.RESOLVING -> R.string.state_resolving
        JobState.DOWNLOADING -> R.string.state_downloading
        JobState.PROCESSING -> R.string.state_processing
        JobState.COMPLETED -> R.string.state_completed
        JobState.FAILED -> R.string.state_failed
        JobState.CANCELLED -> R.string.state_cancelled
    },
)
