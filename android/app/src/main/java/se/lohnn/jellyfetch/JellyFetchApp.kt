package se.lohnn.jellyfetch

import android.app.Application
import se.lohnn.jellyfetch.api.ApiClient

class JellyFetchApp : Application() {
    override fun onCreate() {
        super.onCreate()
        ApiClient.init(this)
    }
}
