package se.lohnn.jellyfetch.dashboard

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import se.lohnn.jellyfetch.R
import se.lohnn.jellyfetch.api.Job
import se.lohnn.jellyfetch.ui.theme.JfTheme

/**
 * The download dashboard — the first screen migrated to Compose (PASS 1). Driven
 * entirely by [DashboardViewModel.state]; polling is the caller's job (the host
 * Activity restarts [DashboardViewModel.refresh] on resume, matching the classic
 * Handler-polling lifecycle). Action callbacks route through the ViewModel, which
 * surfaces success/failure via [DashboardViewModel.messages] (the W-056 fix).
 */
@Composable
fun DashboardRoute(
    vm: DashboardViewModel,
    onOpenSettings: () -> Unit,
    onOpenAllItems: () -> Unit,
    onOpenJob: (Job) -> Unit,
) {
    val state by vm.state.collectAsStateWithLifecycle()
    val snackbarHostState = remember { SnackbarHostState() }

    // Collect the one-shot action messages and show them as snackbars.
    androidx.compose.runtime.LaunchedEffect(vm) {
        vm.messages.collect { message ->
            snackbarHostState.showSnackbar(message)
        }
    }

    DashboardScreen(
        state = state,
        snackbarHostState = snackbarHostState,
        onRefresh = { vm.refresh(userInitiated = true) },
        onOpenSettings = onOpenSettings,
        onOpenAllItems = onOpenAllItems,
        onOpenJob = onOpenJob,
        onCancel = vm::cancel,
        onRetry = vm::retry,
        onRemove = vm::remove,
    )
}

/**
 * The stateless dashboard. Every input is a plain value or lambda, so this is
 * what the @Preview functions render (no ViewModel, no Android singletons) — the
 * screenshot harness exercises this exact composable per state.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DashboardScreen(
    state: DashboardState,
    snackbarHostState: SnackbarHostState,
    onRefresh: () -> Unit,
    onOpenSettings: () -> Unit,
    onOpenAllItems: () -> Unit,
    onOpenJob: (Job) -> Unit,
    onCancel: (Job) -> Unit,
    onRetry: (Job) -> Unit,
    onRemove: (Job) -> Unit,
) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.app_name)) },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.primary,
                    titleContentColor = MaterialTheme.colorScheme.onPrimary,
                    actionIconContentColor = MaterialTheme.colorScheme.onPrimary,
                ),
                actions = {
                    IconButton(onClick = onOpenAllItems) {
                        SimpleGlyph("≡", stringResource(R.string.all_items_menu))
                    }
                    IconButton(onClick = onOpenSettings) {
                        SimpleGlyph("⚙", stringResource(R.string.settings_title))
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
    ) { innerPadding ->
        PullToRefreshBox(
            isRefreshing = state.refreshing,
            onRefresh = onRefresh,
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding),
        ) {
            Column(Modifier.fillMaxSize()) {
                if (state.notConfigured) {
                    Banner(
                        text = stringResource(R.string.dashboard_not_configured),
                        bg = JfTheme.colors.warningBg,
                        fg = JfTheme.colors.warningText,
                        onClick = onOpenSettings,
                    )
                }
                state.transientError?.let { message ->
                    Banner(
                        text = stringResource(R.string.dashboard_unreachable, message),
                        bg = JfTheme.colors.errorCalloutBg,
                        fg = JfTheme.colors.errorCalloutText,
                    )
                }

                when (val content = state.content) {
                    DashboardState.Content.Loading -> LoadingState()
                    DashboardState.Content.Empty -> EmptyState()
                    is DashboardState.Content.Error -> ErrorState(content.message, onRefresh)
                    is DashboardState.Content.Jobs -> JobList(
                        jobs = content.jobs,
                        onOpenJob = onOpenJob,
                        onCancel = onCancel,
                        onRetry = onRetry,
                        onRemove = onRemove,
                    )
                }
            }
        }
    }
}

@Composable
private fun LoadingState() {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        CircularProgressIndicator(color = MaterialTheme.colorScheme.primary)
    }
}

@Composable
private fun EmptyState() {
    Box(
        Modifier
            .fillMaxSize()
            .padding(32.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = stringResource(R.string.dashboard_empty),
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onBackground,
            textAlign = TextAlign.Center,
        )
    }
}

@Composable
private fun ErrorState(message: String, onRetry: () -> Unit) {
    Box(
        Modifier
            .fillMaxSize()
            .padding(32.dp),
        contentAlignment = Alignment.Center,
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(
                text = stringResource(R.string.dashboard_unreachable, message),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.error,
                textAlign = TextAlign.Center,
            )
            Spacer(Modifier.height(16.dp))
            androidx.compose.material3.OutlinedButton(onClick = onRetry) {
                Text(stringResource(R.string.job_retry))
            }
        }
    }
}

@Composable
private fun JobList(
    jobs: List<Job>,
    onOpenJob: (Job) -> Unit,
    onCancel: (Job) -> Unit,
    onRetry: (Job) -> Unit,
    onRemove: (Job) -> Unit,
) {
    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        contentPadding = PaddingValues(vertical = 4.dp),
    ) {
        items(jobs, key = { it.id }) { job ->
            JobRow(
                job = job,
                onOpenJob = onOpenJob,
                onCancel = onCancel,
                onRetry = onRetry,
                onRemove = onRemove,
            )
            HorizontalDivider(color = JfTheme.colors.divider)
        }
    }
}

@Composable
private fun Banner(
    text: String,
    bg: androidx.compose.ui.graphics.Color,
    fg: androidx.compose.ui.graphics.Color,
    onClick: (() -> Unit)? = null,
) {
    val clickModifier = if (onClick != null) {
        Modifier.clickable(onClick = onClick)
    } else {
        Modifier
    }
    Surface(color = bg, modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .then(clickModifier)
                .padding(horizontal = 16.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.Center,
        ) {
            Text(
                text = text,
                color = fg,
                style = MaterialTheme.typography.bodyMedium,
                textAlign = TextAlign.Center,
            )
        }
    }
}

/**
 * A plain text "icon" (no material-icons-extended dependency — keeps the
 * footprint minimal per I-082). [contentDescription] is wired for accessibility
 * via a semantics modifier so TalkBack still announces the action.
 */
@Composable
private fun SimpleGlyph(glyph: String, contentDescription: String) {
    Text(
        text = glyph,
        style = MaterialTheme.typography.titleLarge,
        modifier = Modifier.semantics { this.contentDescription = contentDescription },
    )
}
