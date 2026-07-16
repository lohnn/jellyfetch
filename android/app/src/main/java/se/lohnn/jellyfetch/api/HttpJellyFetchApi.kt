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

    /**
     * List placement-target libraries (jellyfin-plugin v2, GET /Jellyfetch/Libraries).
     * Returns `{ "Libraries": [ LibraryInfoDto, ... ] }`. Goes through [parseJsonBody]
     * so the I-114 HTML-body guard applies (a 2xx-with-HTML from a proxy is caught,
     * not mis-parsed). Same MediaBrowser Token auth as every endpoint.
     */
    override fun listLibraries(callback: (Result<List<LibraryInfo>>) -> Unit) {
        run(callback) {
            val conn = openConnection("/Libraries", "GET")
            try {
                parseJsonBody(conn) { text ->
                    val arr = JSONObject(text).optJSONArray("Libraries") ?: JSONArray()
                    (0 until arr.length()).map { i -> parseLibraryInfo(arr.getJSONObject(i)) }
                }
            } finally {
                conn.disconnect()
            }
        }
    }

    override fun submitUrl(url: String, libraryId: String?, callback: (Result<String>) -> Unit) {
        run(callback) {
            val conn = openConnection("/Downloads", "POST")
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/json")
            val json = JSONObject().put("Url", url).put("Category", "Auto")
            // Auto = omit LibraryId entirely (v2 contract: "no LibraryId" ⇒ today's behavior).
            // Only send it when the user explicitly picked a specific library.
            if (!libraryId.isNullOrBlank()) json.put("LibraryId", libraryId)
            writeBody(conn, json.toString().toByteArray(Charsets.UTF_8))
            readJobId(conn)
        }
    }

    override fun submitTorrent(fileName: String, bytes: ByteArray, libraryId: String?, callback: (Result<String>) -> Unit) {
        run(callback) {
            // v2 contract: the torrent submit carries the explicit library as the
            // `?libraryId=` query param (mirrors the JSON LibraryId on /Downloads).
            // Auto = omit it.
            val path = if (!libraryId.isNullOrBlank()) {
                "/Downloads/Torrent?libraryId=" + urlEncode(libraryId)
            } else {
                "/Downloads/Torrent"
            }
            val conn = openConnection(path, "POST")
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

    // --- Metadata correction ------------------------------------------------
    //
    // CONTRACT SETTLED (jellyfin-plugin HIVEmind result 2026-07-14 + docs/api.md).
    // Verified against the real Jellyfin 10.11.11 typed surface. Base
    // /Jellyfetch/Metadata, PascalCase, same MediaBrowser Token auth. Every
    // parser goes through parseJsonBody (I-114 HTML-body guard). Apply is
    // SYNCHRONOUS: the 200 body carries the refreshed LibraryItem (no poll —
    // W-064 handled by the server genuinely awaiting the refresh).

    override fun getJobLibraryItem(jobId: String, callback: (Result<LibraryItem?>) -> Unit) {
        run(callback) {
            // GET /Metadata/Jobs/{jobId}/LibraryMatch → { JobId, Matched, Item }.
            // 404 = unknown jobId; Matched=false/Item=null = not-yet-scanned/removed.
            val conn = openConnection("/Metadata/Jobs/$jobId/LibraryMatch", "GET")
            try {
                if (conn.responseCode == 404) return@run null
                parseJsonBody(conn) { text ->
                    val o = JSONObject(text)
                    val itemJson = if (o.has("Item") && !o.isNull("Item")) o.getJSONObject("Item") else null
                    if (o.optBoolean("Matched", itemJson != null) && itemJson != null) {
                        parseLibraryItem(itemJson)
                    } else {
                        null
                    }
                }
            } finally {
                conn.disconnect()
            }
        }
    }

    override fun getItemByPath(path: String, callback: (Result<LibraryItem?>) -> Unit) {
        run(callback) {
            // GET /Metadata/Items/ByPath?path={absolute} (jellyfin-plugin, confirmed
            // 2026-07-14). 200 → LibraryItem (same shape as GET Items/{id}); 404 =
            // "nothing indexed at that path YET — rescan still running, keep polling"
            // (a not-yet, NOT an error → null). 400 = blank path (a real error, via
            // requireSuccess). Deterministic post-convert rebind (poll with
            // ConvertTypeResult.itemDirectory, else movedPaths[0]).
            val conn = openConnection("/Metadata/Items/ByPath?path=" + urlEncode(path), "GET")
            try {
                if (conn.responseCode == 404) return@run null
                parseJsonBody(conn) { text -> parseLibraryItem(JSONObject(text)) }
            } finally {
                conn.disconnect()
            }
        }
    }

    override fun listLibraryItems(
        query: String?,
        type: LibraryItemType?,
        startIndex: Int,
        limit: Int,
        callback: (Result<LibraryItemPage>) -> Unit,
    ) {
        run(callback) {
            // GET /Metadata/Items?type=&searchTerm=&startIndex=&limit=
            //   → { Items, TotalRecordCount, StartIndex }. limit clamped 1..200 server-side.
            val params = buildString {
                append("?startIndex=").append(startIndex)
                append("&limit=").append(limit)
                if (!query.isNullOrBlank()) append("&searchTerm=").append(urlEncode(query.trim()))
                if (type != null) append("&type=").append(type.wireName)
            }
            val conn = openConnection("/Metadata/Items$params", "GET")
            try {
                parseJsonBody(conn) { text -> parseLibraryItemPage(JSONObject(text), startIndex) }
            } finally {
                conn.disconnect()
            }
        }
    }

    override fun searchRemoteMetadata(
        itemId: String,
        searchType: LibraryItemType,
        name: String,
        year: Int?,
        callback: (Result<List<RemoteSearchCandidate>>) -> Unit,
    ) {
        run(callback) {
            // POST /Metadata/Items/{itemId}/Search  body {Name, Type, Year?}
            //   → BARE ARRAY of RemoteSearchCandidate. Empty [] = no candidates (not error).
            val conn = openConnection("/Metadata/Items/$itemId/Search", "POST")
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/json")
            val body = JSONObject()
                .put("Name", name)
                .put("Type", searchType.wireName)
            if (year != null) body.put("Year", year)
            conn.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            try {
                parseJsonBody(conn) { text ->
                    val arr = JSONArray(text)
                    (0 until arr.length()).map { i -> parseCandidate(arr.getJSONObject(i)) }
                }
            } finally {
                conn.disconnect()
            }
        }
    }

    override fun applyCorrectionByResult(
        itemId: String,
        candidate: RemoteSearchCandidate,
        callback: (Result<Unit>) -> Unit,
    ) {
        run(callback) {
            // POST /Metadata/Items/{itemId}/Apply  body { Candidate: <whole candidate> }
            //   → 200 with the REFRESHED LibraryItem (synchronous). We only need
            //   success/failure here; the caller re-GETs to display the new match.
            val candidateJson = candidate.rawResult?.let { runCatching { JSONObject(it) }.getOrNull() }
                ?: JSONObject().apply {
                    put("Name", candidate.name)
                    candidate.year?.let { put("ProductionYear", it) }
                    if (candidate.providerIds.isNotEmpty()) {
                        put("ProviderIds", JSONObject(candidate.providerIds as Map<*, *>))
                    }
                }
            val body = JSONObject().put("Candidate", candidateJson)
            applyCorrection(itemId, body)
        }
    }

    override fun applyCorrectionByProvider(
        itemId: String,
        searchType: LibraryItemType,
        provider: String,
        providerId: String,
        callback: (Result<Unit>) -> Unit,
    ) {
        run(callback) {
            // POST /Metadata/Items/{itemId}/Apply  body { ProviderIds: {"Tmdb":"603"} }
            //   — same endpoint, explicit-provider mode. 400 if it yields no provider ids.
            val body = JSONObject()
                .put("ProviderIds", JSONObject(mapOf(provider to providerId) as Map<*, *>))
            applyCorrection(itemId, body)
        }
    }

    /** Shared POST /Metadata/Items/{itemId}/Apply — both apply modes differ only in body. */
    private fun applyCorrection(itemId: String, body: JSONObject) {
        val conn = openConnection("/Metadata/Items/$itemId/Apply", "POST")
        conn.doOutput = true
        conn.setRequestProperty("Content-Type", "application/json")
        conn.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
        try {
            // 200 carries the refreshed item; we don't need its body, just the success.
            requireSuccess(conn)
        } finally {
            conn.disconnect()
        }
    }

    override fun convertType(
        itemId: String,
        target: ConvertTarget,
        callback: (Result<ConvertTypeResult>) -> Unit,
    ) {
        run(callback) {
            // POST /Metadata/Items/{itemId}/ConvertType  body {TargetType:"Movie"|"Series"|"Other"}
            //   → 202 Accepted with a rescan-pending ConvertTypeResult. The new
            //   re-typed item does NOT exist yet — caller polls the Items list.
            //   400 {Error} on bad/no-op/Episode/non-distinct-fallback (Other);
            //   403 {Error} on non-writable root (no files moved, safe to retry);
            //   404 unknown item — all handled by requireSuccess/parseJsonBody's
            //   cause-specific messages, surfaced to the user (W-056).
            val conn = openConnection("/Metadata/Items/$itemId/ConvertType", "POST")
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/json")
            val body = JSONObject().put("TargetType", target.wireName)
            conn.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            try {
                parseJsonBody(conn) { text -> parseConvertTypeResult(JSONObject(text), target) }
            } finally {
                conn.disconnect()
            }
        }
    }

    private fun parseConvertTypeResult(o: JSONObject, requested: ConvertTarget): ConvertTypeResult {
        val movedPaths = o.optJSONArray("MovedPaths")?.let { arr ->
            (0 until arr.length()).map { i -> arr.getString(i) }
        } ?: emptyList()
        return ConvertTypeResult(
            sourceItemId = o.optStringOrNull("SourceItemId") ?: "",
            // Echo back what we asked for if the server omits it (tolerant-of-absence).
            targetType = ConvertTarget.parse(o.optStringOrNull("TargetType")) ?: requested,
            status = o.optStringOrNull("Status") ?: "RescanPending",
            newLibraryRoot = o.optStringOrNull("NewLibraryRoot"),
            movedPaths = movedPaths,
            // The most stable rebind key (jellyfin-plugin 2026-07-14) — poll ByPath with it first.
            itemDirectory = o.optStringOrNull("ItemDirectory"),
            title = o.optStringOrNull("Title"),
            message = o.optStringOrNull("Message"),
        )
    }

    private fun parseLibraryItemPage(o: JSONObject, startIndex: Int): LibraryItemPage {
        val arr = o.optJSONArray("Items") ?: JSONArray()
        val items = (0 until arr.length()).map { i -> parseLibraryItem(arr.getJSONObject(i)) }
        val total = if (o.has("TotalRecordCount") && !o.isNull("TotalRecordCount")) {
            o.getInt("TotalRecordCount")
        } else {
            items.size
        }
        return LibraryItemPage(items = items, totalCount = total, startIndex = startIndex)
    }

    private fun parseLibraryInfo(o: JSONObject): LibraryInfo {
        val locations = o.optJSONArray("Locations")?.let { arr ->
            (0 until arr.length()).mapNotNull { i ->
                if (arr.isNull(i)) null else arr.getString(i).takeIf { it.isNotBlank() }
            }
        } ?: emptyList()
        val id = o.optStringOrNull("Id")
        // Trust the server's IsPlaceable, but defensively re-derive the same rule
        // (id non-null AND ≥1 location) so a partial/older server that omits the
        // flag still yields a sane placeability (I-134 tolerant-of-absence).
        val serverPlaceable = if (o.has("IsPlaceable") && !o.isNull("IsPlaceable")) {
            o.getBoolean("IsPlaceable")
        } else {
            id != null && locations.isNotEmpty()
        }
        return LibraryInfo(
            id = id,
            name = o.optStringOrNull("Name") ?: (id ?: "?"),
            collectionType = o.optStringOrNull("CollectionType"),
            primaryLocation = o.optStringOrNull("PrimaryLocation") ?: locations.firstOrNull(),
            locations = locations,
            isPlaceable = serverPlaceable && id != null && locations.isNotEmpty(),
        )
    }

    private fun parseLibraryItem(o: JSONObject): LibraryItem {
        // ItemId is the GUID "N" format (32 hex, no dashes) per the settled contract.
        val id = o.optStringOrNull("ItemId") ?: o.optStringOrNull("Id") ?: o.getString("ItemId")
        return LibraryItem(
            id = id,
            name = o.optStringOrNull("Name") ?: id,
            year = if (o.has("ProductionYear") && !o.isNull("ProductionYear")) o.getInt("ProductionYear") else null,
            // Type may be "Movie"|"Series"|"Episode"; parse() maps unknown/Episode → null
            // (only Movie/Series are correctable; episodes are promoted to Series server-side).
            type = LibraryItemType.parse(o.optStringOrNull("Type")),
            providerIds = parseProviderIds(o.optJSONObject("ProviderIds")),
            posterUrl = resolvePosterUrl(o, id),
        )
    }

    private fun parseCandidate(o: JSONObject): RemoteSearchCandidate {
        return RemoteSearchCandidate(
            name = o.optStringOrNull("Name") ?: "?",
            year = if (o.has("ProductionYear") && !o.isNull("ProductionYear")) o.getInt("ProductionYear") else null,
            overview = o.optStringOrNull("Overview"),
            providerIds = parseProviderIds(o.optJSONObject("ProviderIds")),
            // ImageUrl is an absolute external provider URL per contract.
            imageUrl = o.optStringOrNull("ImageUrl"),
            // Echo the whole candidate back verbatim on Apply (the server re-hydrates it).
            rawResult = o.toString(),
        )
    }

    private fun parseProviderIds(pj: JSONObject?): Map<String, String> =
        pj?.let {
            buildMap {
                for (key in it.keys()) {
                    val v = it.optString(key)
                    if (v.isNotBlank()) put(key, v)
                }
            }
        } ?: emptyMap()

    /**
     * PosterUrl (per contract) is a RELATIVE standard-Jellyfin route, e.g.
     * "/Items/{ItemId}/Images/Primary?tag=..." — NOT a /Jellyfetch route. Compose
     * it against the base URL and append our own token so the UI only deals with a
     * loadable absolute URL. Absolute URLs (defensive) pass through. Null = no poster.
     */
    private fun resolvePosterUrl(o: JSONObject, id: String): String? {
        val raw = o.optStringOrNull("PosterUrl")
        if (raw != null) {
            val abs = if (raw.startsWith("http://") || raw.startsWith("https://")) {
                raw
            } else {
                baseUrl.trimEnd('/') + "/" + raw.trimStart('/')
            }
            val sep = if (abs.contains('?')) "&" else "?"
            return abs + sep + "api_key=" + urlEncode(apiKey)
        }
        // Fallback: compose from a bare PosterTag if PosterUrl wasn't provided.
        val tag = o.optStringOrNull("PosterTag")
        return tag?.let {
            baseUrl.trimEnd('/') + "/Items/" + id + "/Images/Primary?tag=" + urlEncode(it) +
                "&api_key=" + urlEncode(apiKey)
        }
    }

    private fun urlEncode(s: String): String =
        java.net.URLEncoder.encode(s, "UTF-8").replace("+", "%20")

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
