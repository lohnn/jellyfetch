package se.lohnn.jellyfetch.api

import java.util.Locale

/**
 * The thin client seam (I-069 / SNG-024). The whole app is built against this
 * interface. [FakeJellyFetchApi] backs it when no server is configured;
 * [HttpJellyFetchApi] backs it against the real jellyfin-plugin REST API
 * once a server URL is set (see [ApiClient]).
 *
 * All methods are asynchronous: they run off the calling thread and invoke
 * [callback] on the main thread (I-082 — plain Executor + main-thread post,
 * no coroutine/retrofit dependency required for an app this small).
 */
interface JellyFetchApi {

    /** Cheap reachability + auth check against the configured server. */
    fun testConnection(callback: (Result<Unit>) -> Unit)

    /** Submit a URL or magnet URI. Returns the new job id. */
    fun submitUrl(url: String, callback: (Result<String>) -> Unit)

    /** Submit raw .torrent bytes read from a content URI. Returns the new job id. */
    fun submitTorrent(fileName: String, bytes: ByteArray, callback: (Result<String>) -> Unit)

    /** Poll the current job list. */
    fun listJobs(callback: (Result<List<Job>>) -> Unit)

    /**
     * Fetch a single job's full detail (GET /Downloads/{id}). Unlike [listJobs],
     * this populates [Job.children] for group parents (IsGroup=true) — the
     * flat list endpoint never does (server saves the payload for dashboards
     * that don't need per-episode rows).
     */
    fun getJobDetail(id: String, callback: (Result<Job>) -> Unit)

    fun cancelJob(id: String, callback: (Result<Unit>) -> Unit)
    fun retryJob(id: String, callback: (Result<Unit>) -> Unit)
    fun removeJob(id: String, callback: (Result<Unit>) -> Unit)

    // --- Metadata correction (jellyfin-plugin contract SETTLED 2026-07-14,
    // docs/api.md, base /Jellyfetch/Metadata). These four methods are the seam
    // the whole correction UI is built against; [FakeJellyFetchApi] backs them
    // for demo/offline, [HttpJellyFetchApi] binds the real endpoints. Apply is
    // SYNCHRONOUS server-side (the 200 carries the refreshed item), so callers
    // may re-fetch to display the new match but do not need to poll.

    /**
     * Resolve a completed JellyFetch job to the Jellyfin library item it maps to
     * (the current metadata match Jellyfin assigned). Returns null when the job
     * maps to nothing yet (unmatched, or files removed from the library). Only
     * meaningful for COMPLETED jobs — callers gate on that.
     */
    fun getJobLibraryItem(jobId: String, callback: (Result<LibraryItem?>) -> Unit)

    /**
     * List ALL library movies/series (not just app-originated downloads),
     * paged + searchable, for the "All library items" screen. [type] filters to
     * Movie or Series (null = both). [startIndex]/[limit] page; the result
     * carries a total count for paging.
     */
    fun listLibraryItems(
        query: String?,
        type: LibraryItemType?,
        startIndex: Int,
        limit: Int,
        callback: (Result<LibraryItemPage>) -> Unit,
    )

    /**
     * Free-text remote metadata search for correction candidates. [itemId] is the
     * library item being corrected (Jellyfin's RemoteSearch is keyed by the target
     * item + its kind), [searchType] its Movie/Series kind, [name]/[year] the query.
     * Returns external candidates (poster/title/year/overview/provider ids).
     */
    fun searchRemoteMetadata(
        itemId: String,
        searchType: LibraryItemType,
        name: String,
        year: Int?,
        callback: (Result<List<RemoteSearchCandidate>>) -> Unit,
    )

    /**
     * Apply a correction the user picked from [searchRemoteMetadata]'s native
     * candidate list. The whole opaque candidate is echoed back so the server can
     * re-hydrate the RemoteSearchResult it produced. See [applyCorrectionByProvider]
     * for the explicit-ProviderId (TMDb-paste-back) path.
     *
     * Apply is SYNCHRONOUS server-side (W-064 handled honestly by the plugin — it
     * awaits the full metadata+image refresh): success means "refreshed". Callers
     * re-fetch [getJobLibraryItem]/[listLibraryItems] to *display* the new match,
     * not because completion is in doubt.
     */
    fun applyCorrectionByResult(
        itemId: String,
        candidate: RemoteSearchCandidate,
        callback: (Result<Unit>) -> Unit,
    )

    /**
     * Apply a correction by an explicit provider id the user pasted (the TMDb
     * browser-fallback path): e.g. provider "Tmdb", id "603". Same synchronous
     * refresh semantics as [applyCorrectionByResult].
     */
    fun applyCorrectionByProvider(
        itemId: String,
        searchType: LibraryItemType,
        provider: String,
        providerId: String,
        callback: (Result<Unit>) -> Unit,
    )
}

/** Movie vs Series — the two library kinds the correction feature covers. */
enum class LibraryItemType {
    MOVIE, SERIES;

    /** Jellyfin's `BaseItemKind` / `RemoteSearch` type token (PascalCase). */
    val wireName: String get() = when (this) {
        MOVIE -> "Movie"
        SERIES -> "Series"
    }

    companion object {
        /** Tolerant parse (I-134): unknown/absent → null rather than throwing. */
        fun parse(raw: String?): LibraryItemType? = when (raw?.trim()?.lowercase(Locale.ROOT)) {
            "movie" -> MOVIE
            "series" -> SERIES
            else -> null
        }
    }
}

/**
 * A Jellyfin library item as seen for metadata display/correction. This is the
 * item Jellyfin *currently* thinks the download is — the thing the user inspects
 * to spot a wrong match. All fields except [id] are optional/tolerant (I-134):
 * an item mid-refresh, or from an older server, may have gaps.
 *
 * [java.io.Serializable] so it can ride an Intent extra (all-items list →
 * correction screen) verbatim, same pattern as [Job].
 */
data class LibraryItem(
    val id: String,
    val name: String,
    val year: Int? = null,
    val type: LibraryItemType? = null,
    /** e.g. {"Tmdb": "603", "Imdb": "tt0133093"} — provider → id. */
    val providerIds: Map<String, String> = emptyMap(),
    /**
     * Fully-resolved poster URL when the server hands one back directly. When the
     * server instead gives an image *tag*, [HttpJellyFetchApi] composes the
     * Jellyfin `/Items/{id}/Images/Primary?tag=...` URL into this same field, so
     * the UI only ever deals with a ready-to-load URL (or null = no poster).
     */
    val posterUrl: String? = null,
) : java.io.Serializable

/** One page of [listLibraryItems], with the server's total for paging. */
data class LibraryItemPage(
    val items: List<LibraryItem>,
    val totalCount: Int,
    val startIndex: Int,
) : java.io.Serializable

/**
 * An external metadata match candidate from a remote search — what the user taps
 * in the native picker to correct a wrong match. [rawResult] preserves the
 * server's opaque candidate payload verbatim so [applyCorrectionByResult] can
 * echo it back without the client needing to understand every provider field.
 */
data class RemoteSearchCandidate(
    val name: String,
    val year: Int? = null,
    val overview: String? = null,
    val providerIds: Map<String, String> = emptyMap(),
    val imageUrl: String? = null,
    /**
     * The server's original candidate JSON (as a string), echoed back on apply.
     * Null for the fake impl / when the server doesn't require round-tripping.
     */
    val rawResult: String? = null,
) : java.io.Serializable {
    /** Best single provider id for a compact display, e.g. "Tmdb 603". */
    val primaryProviderLabel: String?
        get() = providerIds.entries.firstOrNull()?.let { "${it.key} ${it.value}" }
}

enum class JobState {
    QUEUED, RESOLVING, DOWNLOADING, PROCESSING, COMPLETED, FAILED, CANCELLED;

    val isTerminal: Boolean
        get() = this == COMPLETED || this == FAILED || this == CANCELLED

    val isCancellable: Boolean
        get() = !isTerminal

    val isRetryable: Boolean
        get() = this == FAILED || this == CANCELLED
}

/**
 * Movie-vs-series classification (jellyfin-plugin, confirmed 2026-07-05).
 * Resolved server-side at completion — most jobs are `null` (unclassified)
 * until then: queued/resolving/downloading jobs, torrents that don't
 * classify, and any job from before this field existed. There is
 * deliberately NO "Auto" entry here: that's an internal request-only hint
 * placeholder on the *submit* endpoint and the resolved Job's Category is
 * documented to never emit it — [parseJobCategory] treats it (and any other
 * unrecognized value) the same as null rather than crashing or guessing.
 */
enum class JobCategory { MOVIE, SERIES, OTHER }

/**
 * Case-insensitively parses the server's raw `Category` string into
 * [JobCategory], tolerating null/blank/unrecognized values by returning null
 * (no badge) rather than throwing — the same tolerant-of-absence style as
 * every other optional field on [Job] (SeriesName, EpisodeNumber, and
 * [JobState]'s own `runCatching { valueOf(...) }` parse). `internal` so it's
 * directly unit-testable without standing up an HTTP/JSON round-trip.
 */
internal fun parseJobCategory(raw: String?): JobCategory? =
    raw?.trim()?.takeIf { it.isNotEmpty() }
        ?.let { runCatching { JobCategory.valueOf(it.uppercase(Locale.ROOT)) }.getOrNull() }

/**
 * [java.io.Serializable] so a [Job] can ride an [android.content.Intent] extra
 * verbatim (MainActivity -> JobDetailActivity) — lets the detail screen render
 * immediately from what the list already fetched, before its own
 * [JellyFetchApi.getJobDetail] refresh (which additionally populates
 * [children]) comes back.
 */
data class Job(
    val id: String,
    val title: String,
    val state: JobState,
    /** 0..100, or null if the server hasn't reported a percentage yet. */
    val progressPercent: Int? = null,
    val speedBytesPerSec: Long? = null,
    val etaSeconds: Long? = null,
    val errorMessage: String? = null,
    val parentId: String? = null,
    /** True when this submission fanned out into child jobs (playlist/series). */
    val isGroup: Boolean = false,
    /** Number of child jobs (0 for non-groups). Populated on both list and detail rows. */
    val childCount: Int = 0,
    /** Short human status line, e.g. "3/8 items finished" or "fetching metadata". */
    val statusText: String? = null,
    val sourceUrl: String? = null,
    /** Absolute library file paths, set once state is Completed. */
    val finalPaths: List<String> = emptyList(),
    val createdAt: String? = null,
    val updatedAt: String? = null,
    val completedAt: String? = null,
    /** Backend kind: "webMedia" or "torrent". */
    val kind: String? = null,
    /**
     * Only populated by [JellyFetchApi.getJobDetail] on a group parent (I-… per
     * jellyfin-plugin contract). The flat list endpoint never sets this even
     * when [childCount] > 0 — fetch detail to drill in.
     */
    val children: List<Job>? = null,
    /**
     * Movie-vs-series classification (jellyfin-plugin, confirmed 2026-07-05).
     * Null until the backend resolves it at completion — render no badge for
     * null rather than guessing, though [episodeLabel]'s existing
     * SeriesName-based inference already gives a reasonable pre-completion
     * hint if a caller wants one. Each entry in [children] carries its own
     * independently-parsed `category`.
     */
    val category: JobCategory? = null,
    // Per-episode metadata (jellyfin-plugin, confirmed 2026-07-04): nullable,
    // populated at COMPLETION only (svtplay-dl --nfo probe). Queued/downloading
    // children render their pre-download human label from [title] instead
    // (media-downloader sets child Title = "Avsnitt N" at expansion time).
    val seriesName: String? = null,
    /** SVT QUIRK: this is the YEAR (e.g. 2024), not a 1-based season — render verbatim. */
    val seasonNumber: Int? = null,
    val episodeNumber: Int? = null,
    val episodeTitle: String? = null,
) : java.io.Serializable {
    /**
     * Best-effort per-episode label. Suggested format from jellyfin-plugin:
     * "SeriesName · S{season}E{episode} · EpisodeTitle", degrading gracefully
     * to whatever's known — down to [title] when none of the structured
     * fields have landed yet (pre-completion, or a non-SVT source).
     */
    val episodeLabel: String
        get() {
            val parts = mutableListOf<String>()
            seriesName?.let { parts += it }
            if (seasonNumber != null && episodeNumber != null) {
                parts += "S${seasonNumber}E${episodeNumber.toString().padStart(2, '0')}"
            }
            episodeTitle?.let { parts += it }
            return if (parts.isNotEmpty()) parts.joinToString(" · ") else title
        }
}
