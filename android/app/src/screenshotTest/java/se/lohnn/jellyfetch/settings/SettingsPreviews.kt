package se.lohnn.jellyfetch.settings

import androidx.compose.runtime.Composable
import androidx.compose.ui.tooling.preview.Preview
import com.android.tools.screenshot.PreviewTest
import se.lohnn.jellyfetch.ui.theme.JellyFetchTheme

/**
 * Screenshot-test previews for the Settings screen (PASS 2 visual oracle). Lives
 * in the screenshotTest source set (which is where @PreviewTest /
 * screenshot-validation-api are visible) and renders the stateless
 * [SettingsScreen] in its meaningful states, light + dark fuchsia.
 */
private val filledState = SettingsState(
    serverUrl = "http://192.168.1.10:8096",
    apiKey = "a1b2c3d4e5f6a7b8c9d0e1f2",
    sendWithoutConfirm = true,
)

@Composable
private fun PreviewSettings(state: SettingsState, dark: Boolean) {
    JellyFetchTheme(darkTheme = dark) {
        SettingsScreen(
            state = state,
            onBack = {},
            onServerUrlChange = {},
            onApiKeyChange = {},
            onSendWithoutConfirmChange = {},
            onTest = {},
            onSave = {},
        )
    }
}

@PreviewTest
@Preview(name = "Settings · filled · light", widthDp = 400, heightDp = 640)
@Composable
fun SettingsFilledLight() = PreviewSettings(filledState, dark = false)

@PreviewTest
@Preview(name = "Settings · filled · dark", widthDp = 400, heightDp = 640)
@Composable
fun SettingsFilledDark() = PreviewSettings(filledState, dark = true)

@PreviewTest
@Preview(name = "Settings · empty · light", widthDp = 400, heightDp = 640)
@Composable
fun SettingsEmptyLight() = PreviewSettings(SettingsState(), dark = false)

@PreviewTest
@Preview(name = "Settings · test OK · dark", widthDp = 400, heightDp = 640)
@Composable
fun SettingsTestOkDark() =
    PreviewSettings(filledState.copy(testStatus = TestStatus.Ok), dark = true)

@PreviewTest
@Preview(name = "Settings · test failed · light", widthDp = 400, heightDp = 640)
@Composable
fun SettingsTestFailedLight() =
    PreviewSettings(
        filledState.copy(testStatus = TestStatus.Failed("Connection refused (401 Unauthorized)")),
        dark = false,
    )
