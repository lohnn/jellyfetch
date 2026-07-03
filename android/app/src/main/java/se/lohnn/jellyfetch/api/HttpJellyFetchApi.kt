package se.lohnn.jellyfetch.api

import android.os.Handler
import android.os.Looper
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.io.OutputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Executors

/**
 * Real HTTP implementation of [JellyFetchApi] against the jellyfin-plugin
 * REST API. Contract confirmed by jellyfin-plugin (HIVEmind result +
 * projects/jellyfetch/docs/api.md) — see that doc for the source of truth;
 * this file mirrors it as of the message received during this session.
 *
 * Base path: /Jellyfetch on the Jellyfin server (e.g. http://host:8096/Jellyfetch).
 * Auth: `Authorization: MediaBrowser Token="<key>"` (quoted form, canonical).
 * JSON: PascalCase fields; request bodies parsed case-insensitively.
 */
class HttpJellyFetchApi(
    private val baseUrl: String,
    private val apiKey: String,
) : JellyFetchApi {

    private val executor = Executors.newSingleThreadExecutor()
    private val mainHandler = Handler(Looper.getMainLooper())

    private fun <T> run(callback: (Result<T>) -> Unit, block: () -> T) {
        executor.submit {
            try {
                val value = block()
                mainHandler.post { callback(Result.success(value)) }
            } catch (t: Throwable) {
                mainHandler.post { callback(Result.failure(t)) }
            }
        }
    }

    private fun openConnection(path: String, method: String): HttpURLConnection {
        val url = URL(baseUrl.trimEnd('/') + "/Jellyfetch" + path)
        val conn = url.openConnection() as HttpURLConnection
        conn.requestMethod = method
        conn.setRequestProperty("Authorization", "MediaBrowser Token=\"$apiKey\"")
        conn.connectTimeout = 10_000
        conn.readTimeout = 15_000
        return conn
    }

    override fun testConnection(callback: (Result<Unit>) -> Unit) {
        run(callback) {
            val conn = openConnection("/Ping", "GET")
            try {
                requireSuccess(conn)
            } finally {
                conn.disconnect()
            }
        }
    }

    override fun submitUrl(url: String, callback: (Result<String>) -> Unit) {
        run(callback) {
            val conn = openConnection("/Downloads", "POST")
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/json")
            val body = JSONObject().put("Url", url).put("Category", "Auto").toString()
            writeBody(conn, body.toByteArray(Charsets.UTF_8))
            readJobId(conn)
        }
    }

    override fun submitTorrent(fileName: String, bytes: ByteArray, callback: (Result<String>) -> Unit) {
        run(callback) {
            val conn = openConnection("/Downloads/Torrent", "POST")
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/x-bittorrent")
            writeBody(conn, bytes)
            readJobId(conn)
        }
    }

    override fun listJobs(callback: (Result<List<Job>>) -> Unit) {
        run(callback) {
            val conn = openConnection("/Downloads?includeChildren=false", "GET")
            try {
                requireSuccess(conn)
                val text = conn.inputStream.bufferedReader().use { it.readText() }
                val arr = JSONArray(text)
                (0 until arr.length()).map { i -> parseJob(arr.getJSONObject(i)) }
            } finally {
                conn.disconnect()
            }
        }
    }

    override fun cancelJob(id: String, callback: (Result<Unit>) -> Unit) {
        run(callback) { postAction(id, "Cancel") }
    }

    override fun retryJob(id: String, callback: (Result<Unit>) -> Unit) {
        run(callback) { postAction(id, "Retry") }
    }

    override fun removeJob(id: String, callback: (Result<Unit>) -> Unit) {
        run(callback) {
            val conn = openConnection("/Downloads/$id", "DELETE")
            try {
                requireSuccess(conn)
            } finally {
                conn.disconnect()
            }
        }
    }

    /** Called by ApiClient when settings change and this instance is replaced. */
    fun close() {
        executor.shutdown()
    }

    private fun postAction(id: String, action: String) {
        val conn = openConnection("/Downloads/$id/$action", "POST")
        try {
            requireSuccess(conn)
        } finally {
            conn.disconnect()
        }
    }

    private fun writeBody(conn: HttpURLConnection, bytes: ByteArray) {
        val out: OutputStream = conn.outputStream
        out.write(bytes)
        out.flush()
        out.close()
        requireSuccess(conn)
    }

    /** 201/200/204 are all "success" depending on endpoint; anything else is an error. */
    private fun requireSuccess(conn: HttpURLConnection) {
        val code = conn.responseCode
        if (code == 401 || code == 403) throw SecurityException("Authentication rejected (HTTP $code)")
        if (code == 404) throw IOException("Not found (HTTP 404)")
        if (code == 409) throw IllegalStateException(errorMessageOf(conn) ?: "Conflict (HTTP 409)")
        if (code == 400) throw IOException(errorMessageOf(conn) ?: "Bad request (HTTP 400)")
        if (code !in 200..299) throw IOException("Server responded HTTP $code")
    }

    private fun errorMessageOf(conn: HttpURLConnection): String? = try {
        val stream = conn.errorStream ?: return null
        val text = stream.bufferedReader().use { it.readText() }
        JSONObject(text).optString("Error").ifBlank { null }
    } catch (_: Exception) {
        null
    }

    private fun readJobId(conn: HttpURLConnection): String {
        try {
            requireSuccess(conn)
            val text = conn.inputStream.bufferedReader().use { it.readText() }
            return JSONObject(text).getString("Id")
        } finally {
            conn.disconnect()
        }
    }

    private fun parseJob(o: JSONObject): Job {
        val stateStr = o.optString("State", "Queued").uppercase()
        val state = runCatching { JobState.valueOf(stateStr) }.getOrDefault(JobState.QUEUED)
        return Job(
            id = o.getString("Id"),
            title = o.optString("Title", o.optString("Id")),
            state = state,
            progressPercent = if (o.has("Percent") && !o.isNull("Percent")) o.getDouble("Percent").toInt() else null,
            speedBytesPerSec = if (o.has("SpeedBps") && !o.isNull("SpeedBps")) o.getLong("SpeedBps") else null,
            etaSeconds = if (o.has("EtaSeconds") && !o.isNull("EtaSeconds")) o.getLong("EtaSeconds") else null,
            errorMessage = if (o.has("ErrorMessage") && !o.isNull("ErrorMessage")) o.getString("ErrorMessage") else null,
        )
    }
}
