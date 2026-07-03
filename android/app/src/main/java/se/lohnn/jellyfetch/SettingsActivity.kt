package se.lohnn.jellyfetch

import android.app.Activity
import android.os.Bundle
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.TextView
import se.lohnn.jellyfetch.api.ApiClient

class SettingsActivity : Activity() {

    private lateinit var prefs: Prefs
    private lateinit var serverUrlField: EditText
    private lateinit var apiKeyField: EditText
    private lateinit var sendWithoutConfirmCheckbox: CheckBox
    private lateinit var testStatusLabel: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)
        prefs = Prefs(this)

        serverUrlField = findViewById(R.id.settings_server_url)
        apiKeyField = findViewById(R.id.settings_api_key)
        sendWithoutConfirmCheckbox = findViewById(R.id.settings_dont_ask_checkbox)
        testStatusLabel = findViewById(R.id.settings_test_status)

        serverUrlField.setText(prefs.serverUrl)
        apiKeyField.setText(prefs.apiKey)
        sendWithoutConfirmCheckbox.isChecked = prefs.sendWithoutConfirm

        findViewById<Button>(R.id.settings_save_button).setOnClickListener { save() }
        findViewById<Button>(R.id.settings_test_button).setOnClickListener { testConnection() }
    }

    override fun onPause() {
        super.onPause()
        save()
    }

    private fun save() {
        prefs.serverUrl = serverUrlField.text.toString()
        prefs.apiKey = apiKeyField.text.toString()
        prefs.sendWithoutConfirm = sendWithoutConfirmCheckbox.isChecked
    }

    private fun testConnection() {
        save()
        testStatusLabel.visibility = android.view.View.VISIBLE
        testStatusLabel.text = getString(R.string.settings_testing)

        if (!prefs.isConfigured) {
            testStatusLabel.text = getString(R.string.settings_test_need_url)
            return
        }

        // ApiClient returns a real HttpJellyFetchApi once a server URL is set,
        // so this hits GET /Jellyfetch/Ping for real.
        ApiClient.current.testConnection { result ->
            result.onSuccess {
                testStatusLabel.text = getString(R.string.settings_test_ok)
            }.onFailure { error ->
                testStatusLabel.text = getString(R.string.settings_test_failed, error.message ?: error.toString())
            }
        }
    }
}
