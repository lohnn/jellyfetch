package se.lohnn.jellyfetch.api

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

    fun cancelJob(id: String, callback: (Result<Unit>) -> Unit)
    fun retryJob(id: String, callback: (Result<Unit>) -> Unit)
    fun removeJob(id: String, callback: (Result<Unit>) -> Unit)
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

data class Job(
    val id: String,
    val title: String,
    val state: JobState,
    /** 0..100, or null if the server hasn't reported a percentage yet. */
    val progressPercent: Int? = null,
    val speedBytesPerSec: Long? = null,
    val etaSeconds: Long? = null,
    val errorMessage: String? = null,
)
