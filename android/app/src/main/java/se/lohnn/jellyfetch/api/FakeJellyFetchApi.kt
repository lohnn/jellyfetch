package se.lohnn.jellyfetch.api

import android.os.Handler
import android.os.Looper
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicInteger

/**
 * In-memory stand-in for the real server. Lets the whole app (intent
 * handling, settings, dashboard) be built and exercised before the
 * jellyfin-plugin REST contract lands. Simulates job progress over time so
 * the dashboard's polling/progress-bar/state-transition UI has something
 * real to render.
 */
class FakeJellyFetchApi : JellyFetchApi {

    private val executor = Executors.newSingleThreadExecutor()
    private val mainHandler = Handler(Looper.getMainLooper())
    private val idCounter = AtomicInteger(1)

    private val lock = Object()
    private val jobs = mutableListOf<Job>()

    init {
        // Seed a few jobs spanning the state machine so the dashboard has
        // something interesting to show on first launch.
        synchronized(lock) {
            jobs += Job(
                id = "seed-1",
                title = "Big Buck Bunny (1080p)",
                state = JobState.DOWNLOADING,
                progressPercent = 42,
                speedBytesPerSec = 3_400_000,
                etaSeconds = 95,
            )
            jobs += Job(
                id = "seed-2",
                title = "SVT Play: Nyheter 2026-07-01",
                state = JobState.QUEUED,
            )
            jobs += Job(
                id = "seed-3",
                title = "Sample Season 1 Episode 4",
                state = JobState.FAILED,
                errorMessage = "Tracker unreachable after 3 retries",
            )
            jobs += Job(
                id = "seed-4",
                title = "Old completed download",
                state = JobState.COMPLETED,
                progressPercent = 100,
            )
        }
    }

    /** Simulated network delay so UI loading/empty states are exercised too. */
    private fun <T> respond(callback: (Result<T>) -> Unit, block: () -> T) {
        executor.submit {
            try {
                Thread.sleep(250)
                val value = block()
                mainHandler.post { callback(Result.success(value)) }
            } catch (t: Throwable) {
                mainHandler.post { callback(Result.failure(t)) }
            }
        }
    }

    override fun testConnection(callback: (Result<Unit>) -> Unit) {
        respond(callback) { Unit }
    }

    override fun submitUrl(url: String, callback: (Result<String>) -> Unit) {
        respond(callback) {
            val id = "job-${idCounter.getAndIncrement()}"
            synchronized(lock) {
                jobs.add(
                    0,
                    Job(
                        id = id,
                        title = url,
                        state = JobState.QUEUED,
                    ),
                )
            }
            advanceAsync(id)
            id
        }
    }

    override fun submitTorrent(fileName: String, bytes: ByteArray, callback: (Result<String>) -> Unit) {
        respond(callback) {
            val id = "job-${idCounter.getAndIncrement()}"
            synchronized(lock) {
                jobs.add(
                    0,
                    Job(
                        id = id,
                        title = fileName,
                        state = JobState.QUEUED,
                    ),
                )
            }
            advanceAsync(id)
            id
        }
    }

    override fun listJobs(callback: (Result<List<Job>>) -> Unit) {
        respond(callback) {
            synchronized(lock) { jobs.toList() }
        }
    }

    override fun cancelJob(id: String, callback: (Result<Unit>) -> Unit) {
        respond(callback) {
            mutate(id) { it.copy(state = JobState.CANCELLED, speedBytesPerSec = null, etaSeconds = null) }
        }
    }

    override fun retryJob(id: String, callback: (Result<Unit>) -> Unit) {
        respond(callback) {
            mutate(id) { it.copy(state = JobState.QUEUED, progressPercent = 0, errorMessage = null) }
            advanceAsync(id)
        }
    }

    override fun removeJob(id: String, callback: (Result<Unit>) -> Unit) {
        respond(callback) {
            synchronized(lock) { jobs.removeAll { it.id == id } }
        }
    }

    private fun mutate(id: String, transform: (Job) -> Job) {
        synchronized(lock) {
            val idx = jobs.indexOfFirst { it.id == id }
            if (idx >= 0) jobs[idx] = transform(jobs[idx])
        }
    }

    /** Walks a freshly submitted job through queued -> resolving -> downloading -> processing -> completed. */
    private fun advanceAsync(id: String) {
        executor.submit {
            val steps = listOf(
                JobState.RESOLVING to 1500L,
                JobState.DOWNLOADING to 4000L,
                JobState.PROCESSING to 1200L,
                JobState.COMPLETED to 0L,
            )
            for ((state, delayMs) in steps) {
                synchronized(lock) {
                    if (jobs.none { it.id == id && !it.state.isTerminal && it.state != JobState.CANCELLED }) {
                        return@synchronized
                    }
                }
                if (state == JobState.DOWNLOADING) {
                    var progress = 0
                    while (progress < 100) {
                        Thread.sleep(400)
                        progress = (progress + (5..15).random()).coerceAtMost(100)
                        val stillActive = synchronized(lock) {
                            val idx = jobs.indexOfFirst { it.id == id }
                            if (idx < 0 || jobs[idx].state == JobState.CANCELLED) {
                                false
                            } else {
                                jobs[idx] = jobs[idx].copy(
                                    state = JobState.DOWNLOADING,
                                    progressPercent = progress,
                                    speedBytesPerSec = (500_000L..5_000_000L).random(),
                                    etaSeconds = ((100 - progress) * 2).toLong(),
                                )
                                true
                            }
                        }
                        if (!stillActive) return@submit
                    }
                } else {
                    Thread.sleep(delayMs)
                    val stillActive = synchronized(lock) {
                        val idx = jobs.indexOfFirst { it.id == id }
                        if (idx < 0 || jobs[idx].state == JobState.CANCELLED) {
                            false
                        } else {
                            jobs[idx] = jobs[idx].copy(
                                state = state,
                                progressPercent = if (state == JobState.COMPLETED) 100 else jobs[idx].progressPercent,
                                speedBytesPerSec = if (state.isTerminal) null else jobs[idx].speedBytesPerSec,
                                etaSeconds = if (state.isTerminal) null else jobs[idx].etaSeconds,
                            )
                            true
                        }
                    }
                    if (!stillActive) return@submit
                }
            }
        }
    }
}

private fun ClosedRange<Long>.random(): Long =
    (start + (Math.random() * (endInclusive - start + 1)).toLong()).coerceIn(start, endInclusive)

private fun IntRange.random(): Int = kotlin.random.Random.nextInt(first, last + 1)
