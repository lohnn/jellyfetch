package se.lohnn.jellyfetch.detail

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.widget.Toast
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import se.lohnn.jellyfetch.Formatters
import se.lohnn.jellyfetch.R
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.api.JobState
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemType
import se.lohnn.jellyfetch.ui.NavBackButton
import se.lohnn.jellyfetch.ui.Poster
import se.lohnn.jellyfetch.ui.theme.Dimens
import se.lohnn.jellyfetch.ui.theme.JfTheme

/**
 * Tap-to-expand detail view for a single job (PASS 2 — Compose port of
 * activity_job_detail.xml + JobDetailActivity). Renders every block the classic
 * screen did: status banner, title/category/state, progress, source/timestamps,
 * full selectable error + copy, file paths, per-episode child rows (expandable),
 * and the Jellyfin-match metadata card with the "Fix metadata" affordance that
 * opens the shared [se.lohnn.jellyfetch.correction.CorrectionSheet].
 *
 * Stateless over [JobDetailState] (value/lambda surface) so the @Preview harness
 * renders each meaningful state. [onCopyError] is threaded so the host can put the
 * message on the clipboard (Compose has no clipboard-copy without a Context seam).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun JobDetailScreen(
    state: JobDetailState,
    onBack: () -> Unit,
    onCopyError: (String) -> Unit,
    onFixMetadata: (LibraryItem) -> Unit,
) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.job_detail_title)) },
                navigationIcon = { NavBackButton(onBack) },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.primary,
                    titleContentColor = MaterialTheme.colorScheme.onPrimary,
                    navigationIconContentColor = MaterialTheme.colorScheme.onPrimary,
                ),
            )
        },
    ) { innerPadding ->
        val job = state.job
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding)
                .verticalScroll(rememberScrollState())
                .padding(Dimens.screenPadding),
        ) {
            state.loadError?.let { err ->
                Banner(stringResource(R.string.job_detail_load_failed, err))
                Spacer(Modifier.height(Dimens.blockGap))
            }

            Text(job.title, style = MaterialTheme.typography.headlineSmall)
            Formatters.categoryLabel(LocalContext.current, job.category)?.let {
                Spacer(Modifier.height(Dimens.tightGap))
                Text(it, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }

            Spacer(Modifier.height(Dimens.tightGap))
            Text(
                text = buildString {
                    append(Formatters.stateLabel(LocalContext.current, job.state))
                    if (!job.statusText.isNullOrBlank()) append(" — ${job.statusText}")
                },
                style = MaterialTheme.typography.labelLarge,
                color = JfTheme.colors.stateAccent,
            )

            ProgressBlock(job)

            job.sourceUrl?.takeIf { it.isNotBlank() }?.let {
                DetailLine(stringResource(R.string.job_detail_source_url, it))
            }
            Formatters.timestamp(job.createdAt)?.let { DetailLine(stringResource(R.string.job_detail_created, it)) }
            Formatters.timestamp(job.updatedAt)?.let { DetailLine(stringResource(R.string.job_detail_updated, it)) }
            Formatters.timestamp(job.completedAt)?.let { DetailLine(stringResource(R.string.job_detail_completed, it)) }

            job.errorMessage?.takeIf { it.isNotBlank() }?.let { ErrorBlock(it, onCopyError) }

            if (job.finalPaths.isNotEmpty()) {
                SectionTitle(stringResource(R.string.job_detail_final_paths_title))
                for (path in job.finalPaths) PathLine(path)
            }

            if (job.isGroup) EpisodesBlock(job)

            MetadataBlock(state.metadata, onFixMetadata)
        }
    }
}

@Composable
private fun ProgressBlock(job: Job) {
    when (job.state) {
        JobState.FAILED, JobState.CANCELLED -> Unit
        JobState.QUEUED, JobState.RESOLVING, JobState.PROCESSING -> {
            Spacer(Modifier.height(Dimens.blockGap))
            LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
        }
        JobState.DOWNLOADING -> {
            Spacer(Modifier.height(Dimens.blockGap))
            val percent = job.progressPercent
            if (percent == null) {
                LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
            } else {
                LinearProgressIndicator(progress = { percent.coerceIn(0, 100) / 100f }, modifier = Modifier.fillMaxWidth())
            }
            val parts = listOfNotNull(
                percent?.let { "$it%" },
                Formatters.speed(job.speedBytesPerSec),
                Formatters.eta(job.etaSeconds)?.let { "ETA $it" },
            )
            if (parts.isNotEmpty()) DetailLine(parts.joinToString("  ·  "))
        }
        JobState.COMPLETED -> {
            Spacer(Modifier.height(Dimens.blockGap))
            LinearProgressIndicator(progress = { 1f }, modifier = Modifier.fillMaxWidth())
        }
    }
}

@Composable
private fun ErrorBlock(message: String, onCopyError: (String) -> Unit) {
    SectionTitle(stringResource(R.string.job_detail_error_title))
    Surface(color = JfTheme.colors.errorCalloutBg, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(Dimens.blockGap)) {
            Text(message, color = JfTheme.colors.errorCalloutText, style = MaterialTheme.typography.bodyMedium)
            Spacer(Modifier.height(Dimens.blockGap))
            OutlinedButton(onClick = { onCopyError(message) }) {
                Text(stringResource(R.string.job_detail_copy_error))
            }
        }
    }
}

@Composable
private fun EpisodesBlock(job: Job) {
    SectionTitle(stringResource(R.string.job_detail_episodes_title, job.childCount))
    val children = job.children
    if (children == null) {
        DetailLine(stringResource(R.string.job_detail_loading))
        return
    }
    for (child in children) ChildRow(child)
}

@Composable
private fun ChildRow(child: Job) {
    var expanded by remember(child.id) { mutableStateOf(false) }
    val hasDetail = !child.errorMessage.isNullOrBlank() || child.finalPaths.isNotEmpty()

    Spacer(Modifier.height(Dimens.blockGap))
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(enabled = hasDetail) { expanded = !expanded },
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(child.episodeLabel, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
            Text(
                Formatters.stateLabel(LocalContext.current, child.state),
                style = MaterialTheme.typography.labelSmall,
                color = JfTheme.colors.stateAccent,
            )
            if (hasDetail) {
                Spacer(Modifier.width(Dimens.blockGap))
                Text(if (expanded) "\u2304" else "\u203A", color = JfTheme.colors.chevron)
            }
        }
        when (child.state) {
            JobState.DOWNLOADING ->
                LinearProgressIndicator(
                    progress = { (child.progressPercent ?: 0).coerceIn(0, 100) / 100f },
                    modifier = Modifier.fillMaxWidth().padding(top = Dimens.tightGap),
                )
            JobState.QUEUED, JobState.RESOLVING, JobState.PROCESSING ->
                LinearProgressIndicator(modifier = Modifier.fillMaxWidth().padding(top = Dimens.tightGap))
            JobState.COMPLETED ->
                LinearProgressIndicator(progress = { 1f }, modifier = Modifier.fillMaxWidth().padding(top = Dimens.tightGap))
            JobState.FAILED, JobState.CANCELLED -> Unit
        }
        if (expanded) {
            child.errorMessage?.takeIf { it.isNotBlank() }?.let {
                Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
            }
            for (path in child.finalPaths) PathLine(path)
        }
    }
}

@Composable
private fun MetadataBlock(metadata: MetadataState, onFixMetadata: (LibraryItem) -> Unit) {
    when (metadata) {
        MetadataState.Hidden -> return
        MetadataState.Loading -> {
            SectionTitle(stringResource(R.string.metadata_section_title))
            DetailLine(stringResource(R.string.metadata_loading))
        }
        MetadataState.None -> {
            SectionTitle(stringResource(R.string.metadata_section_title))
            DetailLine(stringResource(R.string.metadata_none))
        }
        is MetadataState.Failed -> {
            SectionTitle(stringResource(R.string.metadata_section_title))
            Text(
                stringResource(R.string.metadata_load_failed, metadata.message),
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodyMedium,
            )
        }
        is MetadataState.Loaded -> MetadataCard(metadata.item, onFixMetadata)
    }
}

@Composable
private fun MetadataCard(item: LibraryItem, onFixMetadata: (LibraryItem) -> Unit) {
    SectionTitle(stringResource(R.string.metadata_section_title))
    Card(Modifier.fillMaxWidth()) {
        Row(Modifier.padding(Dimens.blockGap)) {
            Poster(url = item.posterUrl)
            Spacer(Modifier.width(Dimens.blockGap))
            Column(Modifier.weight(1f)) {
                Text(item.name, style = MaterialTheme.typography.titleMedium)
                val typeLabel = item.type?.let {
                    when (it) {
                        LibraryItemType.MOVIE -> stringResource(R.string.category_movie)
                        LibraryItemType.SERIES -> stringResource(R.string.category_series)
                    }
                }
                listOfNotNull(item.year?.toString(), typeLabel).joinToString(" · ").takeIf { it.isNotBlank() }?.let {
                    Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                item.providerIds.entries.joinToString(" · ") { "${it.key} ${it.value}" }.takeIf { it.isNotBlank() }?.let {
                    Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
    }
    Spacer(Modifier.height(Dimens.tightGap))
    Text(
        stringResource(R.string.metadata_wrong_match_hint),
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    Spacer(Modifier.height(Dimens.blockGap))
    OutlinedButton(onClick = { onFixMetadata(item) }) {
        Text(stringResource(R.string.metadata_fix_button))
    }
}

// --- Small shared pieces -----------------------------------------------------

@Composable
private fun SectionTitle(text: String) {
    Spacer(Modifier.height(Dimens.sectionGap))
    Text(text, style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
    Spacer(Modifier.height(Dimens.tightGap))
}

@Composable
private fun DetailLine(text: String) {
    Spacer(Modifier.height(Dimens.tightGap))
    Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

@Composable
private fun PathLine(path: String) {
    Text(path, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(vertical = 2.dp))
}

@Composable
private fun Banner(text: String) {
    Surface(color = JfTheme.colors.errorCalloutBg, modifier = Modifier.fillMaxWidth()) {
        Text(
            text,
            color = JfTheme.colors.errorCalloutText,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(Dimens.blockGap),
        )
    }
}

/** Clipboard copy for the host Activity to pass as onCopyError. */
fun copyErrorToClipboard(context: Context, message: String) {
    val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    clipboard.setPrimaryClip(ClipData.newPlainText("JellyFetch error", message))
    Toast.makeText(context, R.string.job_detail_error_copied, Toast.LENGTH_SHORT).show()
}

