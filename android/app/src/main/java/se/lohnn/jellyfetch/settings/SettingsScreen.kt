package se.lohnn.jellyfetch.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import se.lohnn.jellyfetch.R
import se.lohnn.jellyfetch.ui.NavBackButton
import se.lohnn.jellyfetch.ui.theme.Dimens
import se.lohnn.jellyfetch.ui.theme.JfTheme

/** Route wrapper: binds the ViewModel state to the stateless [SettingsScreen]. */
@Composable
fun SettingsRoute(
    vm: SettingsViewModel,
    onBack: () -> Unit,
) {
    val state by vm.state.collectAsStateWithLifecycle()
    SettingsScreen(
        state = state,
        onBack = onBack,
        onServerUrlChange = vm::onServerUrlChange,
        onApiKeyChange = vm::onApiKeyChange,
        onSendWithoutConfirmChange = vm::onSendWithoutConfirmChange,
        onTest = vm::testConnection,
        onSave = vm::save,
    )
}

/**
 * Stateless Settings form. Every input is a value/lambda, so the @Preview
 * functions render it per test-status state without a ViewModel. Fuchsia
 * TopAppBar (the ONLY app bar now — the platform ActionBar is removed by the
 * NoActionBar theme fix). Respects the system font scale (no fontScale handling
 * here — SNG-026).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(
    state: SettingsState,
    onBack: () -> Unit,
    onServerUrlChange: (String) -> Unit,
    onApiKeyChange: (String) -> Unit,
    onSendWithoutConfirmChange: (Boolean) -> Unit,
    onTest: () -> Unit,
    onSave: () -> Unit,
) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.settings_title)) },
                navigationIcon = { NavBackButton(onBack) },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.primary,
                    titleContentColor = MaterialTheme.colorScheme.onPrimary,
                    navigationIconContentColor = MaterialTheme.colorScheme.onPrimary,
                ),
            )
        },
    ) { innerPadding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding)
                .verticalScroll(rememberScrollState())
                .padding(Dimens.screenPadding),
        ) {
            OutlinedTextField(
                value = state.serverUrl,
                onValueChange = onServerUrlChange,
                label = { Text(stringResource(R.string.settings_server_url_hint)) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(Dimens.sectionGap))

            OutlinedTextField(
                value = state.apiKey,
                onValueChange = onApiKeyChange,
                label = { Text(stringResource(R.string.settings_api_key_hint)) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(Dimens.sectionGap))

            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text(
                    text = stringResource(R.string.settings_dont_ask),
                    style = MaterialTheme.typography.bodyLarge,
                    modifier = Modifier.weight(1f),
                )
                Switch(
                    checked = state.sendWithoutConfirm,
                    onCheckedChange = onSendWithoutConfirmChange,
                )
            }
            Spacer(Modifier.height(Dimens.sectionGap))

            Row(horizontalArrangement = Arrangement.spacedBy(Dimens.blockGap)) {
                Button(onClick = onSave) {
                    Text(stringResource(R.string.settings_save))
                }
                OutlinedButton(onClick = onTest) {
                    Text(stringResource(R.string.settings_test_connection))
                }
            }

            TestStatusLine(state.testStatus)
        }
    }
}

@Composable
private fun TestStatusLine(status: TestStatus) {
    val (text, color) = when (status) {
        TestStatus.Idle -> return
        TestStatus.Testing -> stringResource(R.string.settings_testing) to MaterialTheme.colorScheme.onBackground
        TestStatus.NeedUrl -> stringResource(R.string.settings_test_need_url) to JfTheme.colors.warningText
        TestStatus.Ok -> stringResource(R.string.settings_test_ok) to JfTheme.colors.stateAccent
        is TestStatus.Failed ->
            stringResource(R.string.settings_test_failed, status.message) to MaterialTheme.colorScheme.error
    }
    Spacer(Modifier.height(Dimens.sectionGap))
    Text(text = text, color = color, style = MaterialTheme.typography.bodyMedium)
}
