package se.lohnn.jellyfetch.api

import android.os.Handler
import android.os.Looper
import org.json.JSONArray
import org.json.JSONException
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

    /**
     * Field bug (confirmed on a real device): a user entered the Jellyfin base URL
     * WITHOUT the port (http://192.168.86.29 instead of http://192.168.86.29:8096).
     * That port-less URL reached *something* that returned 200 — the Jellyfin web
     * client, or a proxy in front of it — and the old status-code-only check reported
     * "success". It was a false positive: "reached a server" is not "reached the
     * JellyFetch API". Fix: success requires 2xx AND a body that actually matches the
     * shape GET /Jellyfetch/Ping documents (docs/api.md: {"Name":"JellyFetch",
     * "Version":"..."}). Only "Name" is checked (not "Version", which will change
     * across plugin releases and must never break connectivity testing).
     *
     * Four distinguishable outcomes reach the UI via [Result]:
     *  (a) can't reach the host at all — ConnectException/SocketTimeoutException/
     *      UnknownHostException propagate from the network layer untouched; their
     *      messages already read as "no route to host" / "timed out", distinct from
     *      the messages below.
     *  (b) reached A server, not the JellyFetch API (HTML/non-JSON, OR valid JSON
     *      that isn't Ping's shape) — [PING_WRONG_SERVICE_MESSAGE], which names the
     *      missing-port cause directly (the concrete thing that bit this user).
     *  (c) reached the API, wrong key — the existing 401/403 message from
     *      [requireSuccess] ("Authentication failed ... check your API key"),
     *      reached via [parseJsonBody] -> [requireSuccess].
     *  (d) success — Ping's body parses and Name == "JellyFetch".
     */
    override fun testConnection(callback: (Result<Unit>) -> Unit) {
        run(callback) {
            val conn = openConnection("/Ping", "GET")
            try {
                val json = parseJsonBody(conn, nonJsonContext = PING_WRONG_SERVICE_MESSAGE) { text ->
                    JSONObject(text)
                }
                val name = json.optString("Name")
                if (name != "JellyFetch") {
                    throw IOException(
                        "$PING_WRONG_SERVICE_MESSAGE Got a JSON response, but not from the JellyFetch " +
                            "API (Name=\"${name.ifBlank { "?" }}\").",
                    )
                }
                Unit
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
                parseJsonBody(conn) { text ->
                    val arr = JSONArray(text)
                    (0 until arr.length()).map { i -> parseJob(arr.getJSONObject(i)) }
                }
            } finally {
                conn.disconnect()
            }
        }
    }

    override fun getJobDetail(id: String, callback: (Result<Job>) -> Unit) {
        run(callback) {
            val conn = openConnection("/Downloads/$id", "GET")
            try {
                parseJsonBody(conn) { text -> parseJob(JSONObject(text)) }
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

    /**
     * Checks the HTTP status code BEFORE any body is parsed as JSON. This is the first
     * of two guards against the field bug where a misconfigured/wrong server URL (e.g.
     * pointing at the Jellyfin web UI root, or a reverse proxy) produces a response the
     * raw JSON parser chokes on with a cryptic "Value <!DOCTYPE ... cannot be converted
     * to JSONArray" message. Every non-2xx path here throws with a message that names
     * the likely cause, not the parser's confusion. 201/200/204 are all "success"
     * depending on endpoint.
     */
    private fun requireSuccess(conn: HttpURLConnection) {
        val code = conn.responseCode
        when {
            code in 200..299 -> return
            code == 401 || code == 403 ->
                throw SecurityException("Authentication failed (HTTP $code) — check your API key.")
            code == 404 ->
                throw IOException(
                    "Endpoint not found (HTTP 404) — check the server URL (is it the Jellyfin base " +
                        "URL, e.g. http://host:8096, with no extra path segments?).",
                )
            code == 409 -> throw IllegalStateException(errorMessageOf(conn) ?: "Conflict (HTTP 409).")
            code == 400 -> throw IOException(errorMessageOf(conn) ?: "Bad request (HTTP 400).")
            code in 500..599 ->
                throw IOException(
                    "Server error (HTTP $code) — the JellyFetch plugin or Jellyfin itself hit a " +
                        "problem; check the server logs.",
                )
            else -> throw IOException("Server responded HTTP $code.")
        }
    }

    private fun errorMessageOf(conn: HttpURLConnection): String? = try {
        val stream = conn.errorStream ?: return null
        val text = stream.bufferedReader().use { it.readText() }
        JSONObject(text).optString("Error").ifBlank { null }
    } catch (_: Exception) {
        null
    }

    /**
     * Second guard: even on a 2xx status, the body might not actually be JSON (the
     * exact shape of the field bug — a 200 OK carrying an HTML page). Reads the body,
     * sniffs its shape before handing it to [parse], and on ANY failure (non-JSON shape
     * OR a JSONException from a malformed-but-JSON-ish body) throws a message aimed at
     * a human debugging their server URL/proxy, never the raw org.json exception text.
     * Used by every endpoint this class parses a response body from, so the message
     * quality and guarding logic stay in exactly one place.
     */
    private fun <T> parseJsonBody(
        conn: HttpURLConnection,
        nonJsonContext: String = DEFAULT_NON_JSON_CONTEXT,
        parse: (String) -> T,
    ): T {
        requireSuccess(conn)
        val contentType = conn.contentType
        val text = conn.inputStream.bufferedReader().use { it.readText() }
        val trimmed = text.trim()
        val looksLikeJson = trimmed.startsWith("{") || trimmed.startsWith("[")
        if (!looksLikeJson) {
            throw IOException(nonJsonMessage(nonJsonContext, contentType, trimmed))
        }
        return try {
            parse(trimmed)
        } catch (_: JSONException) {
            throw IOException(nonJsonMessage(nonJsonContext, contentType, trimmed))
        }
    }

    private fun nonJsonMessage(context: String, contentType: String?, body: String): String {
        val snippet = body.take(120).replace("\n", " ").replace("\r", " ").trim()
        val typeInfo = if (!contentType.isNullOrBlank()) " (content-type: $contentType)" else ""
        val snippetInfo = if (snippet.isNotBlank()) " Response started with: \"$snippet\"" else ""
        return "$context$typeInfo.$snippetInfo"
    }

    private fun readJobId(conn: HttpURLConnection): String {
        try {
            return parseJsonBody(conn) { text -> JSONObject(text).getString("Id") }
        } finally {
            conn.disconnect()
        }
    }

    private companion object {
        const val DEFAULT_NON_JSON_CONTEXT =
            "Server returned an unexpected (non-JSON) response — this usually means the URL is " +
                "reaching a web page or proxy, not the JellyFetch API"

        // Names the concrete, common cause (missing Jellyfin port) rather than a
        // generic "check your URL" — this exact confusion (port-less URL silently
        // reaching the Jellyfin web client instead of the plugin API) was confirmed
        // against a real device.
        const val PING_WRONG_SERVICE_MESSAGE =
            "Reached a server, but not the JellyFetch API. Check the URL includes the Jellyfin port " +
                "(usually :8096), e.g. http://192.168.1.10:8096."
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
            parentId = o.optStringOrNull("ParentId"),
            isGroup = o.optBoolean("IsGroup", false),
            childCount = o.optInt("ChildCount", 0),
            statusText = o.optStringOrNull("StatusText"),
            sourceUrl = o.optStringOrNull("SourceUrl"),
            finalPaths = o.optJSONArray("FinalPaths")?.let { arr ->
                (0 until arr.length()).map { i -> arr.getString(i) }
            } ?: emptyList(),
            createdAt = o.optStringOrNull("CreatedAt"),
            updatedAt = o.optStringOrNull("UpdatedAt"),
            completedAt = o.optStringOrNull("CompletedAt"),
            kind = o.optStringOrNull("Kind"),
            // Additive per-episode fields (jellyfin-plugin, confirmed 2026-07-04). Read
            // defensively with optX so an old server without them still parses fine.
            seriesName = o.optStringOrNull("SeriesName"),
            seasonNumber = if (o.has("SeasonNumber") && !o.isNull("SeasonNumber")) o.getInt("SeasonNumber") else null,
            episodeNumber = if (o.has("EpisodeNumber") && !o.isNull("EpisodeNumber")) o.getInt("EpisodeNumber") else null,
            episodeTitle = o.optStringOrNull("EpisodeTitle"),
            // Only the detail endpoint populates this; the list endpoint omits/nulls it.
            children = o.optJSONArray("Children")?.let { arr ->
                (0 until arr.length()).map { i -> parseJob(arr.getJSONObject(i)) }
            },
            // New field (jellyfin-plugin, confirmed 2026-07-05): additive/optional,
            // same level as SeriesName. parseJobCategory tolerates null/absent/
            // unrecognized (incl. the request-only "Auto" placeholder, which must
            // never appear here) by returning null rather than throwing — an old
            // server without this field parses exactly as before.
            category = parseJobCategory(o.optStringOrNull("Category")),
        )
    }

    /** `optString` returns `""` for both absent and JSON-null; this distinguishes null/absent from a real empty string. */
    private fun JSONObject.optStringOrNull(name: String): String? =
        if (has(name) && !isNull(name)) getString(name) else null
}
