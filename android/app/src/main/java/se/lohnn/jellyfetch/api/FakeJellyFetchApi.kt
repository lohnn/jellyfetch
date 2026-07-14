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

    /**
     * Fake library items for the "All library items" screen + correction picker.
     * Keyed by item id. A completed job maps to one of these via [jobToItem].
     * Deliberately seeded with a WRONG match (see "The Matrix Reloaded" vs a job
     * titled "The Matrix") so the correction flow has something to fix.
     */
    private val libraryItems = linkedMapOf<String, LibraryItem>()
    private val jobToItem = mutableMapOf<String, String>()

    init {
        seedLibrary()
    }

    private fun seedLibrary() {
        fun add(item: LibraryItem) { libraryItems[item.id] = item }
        add(
            LibraryItem(
                id = "item-matrix",
                // Intentionally the WRONG match: the download was "The Matrix" (1999)
                // but Jellyfin matched it to the sequel — exactly the spot-and-correct
                // case the feature exists for.
                name = "The Matrix Reloaded",
                year = 2003,
                type = LibraryItemType.MOVIE,
                providerIds = mapOf("Tmdb" to "604", "Imdb" to "tt0234215"),
                posterUrl = null,
            ),
        )
        add(
            LibraryItem(
                id = "item-old-completed",
                name = "Old completed download",
                year = 2024,
                type = LibraryItemType.MOVIE,
                providerIds = mapOf("Tmdb" to "12345"),
            ),
        )
        add(
            LibraryItem(
                id = "item-abbor",
                name = "Abbormästarna",
                year = 2024,
                type = LibraryItemType.SERIES,
                providerIds = mapOf("Tmdb" to "998877"),
            ),
        )
        // A handful more so the all-items list + search has volume to page through.
        val extras = listOf(
            Triple("Rederiet", 1992, LibraryItemType.SERIES),
            Triple("Beck", 1997, LibraryItemType.SERIES),
            Triple("Blade Runner", 1982, LibraryItemType.MOVIE),
            Triple("Blade Runner 2049", 2017, LibraryItemType.MOVIE),
            Triple("Dune", 2021, LibraryItemType.MOVIE),
            Triple("Dune: Part Two", 2024, LibraryItemType.MOVIE),
            Triple("Fargo", 2014, LibraryItemType.SERIES),
        )
        for ((i, t) in extras.withIndex()) {
            add(
                LibraryItem(
                    id = "item-extra-$i",
                    name = t.first,
                    year = t.second,
                    type = t.third,
                    providerIds = mapOf("Tmdb" to "${1000 + i}"),
                ),
            )
        }
        // Map completed seed jobs to their (mis)matched library items.
        jobToItem["seed-4"] = "item-old-completed"
        jobToItem["seed-6"] = "item-abbor"
    }

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
                // Category is null until completion per the real contract (jellyfin-plugin,
                // confirmed 2026-07-05) — left unset here on purpose to exercise the
                // "in-progress, no badge yet" path alongside the completed seeds below.
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
                finalPaths = listOf("/media/movies/Old completed download (2024)/Old completed download.mkv"),
                sourceUrl = "https://www.svtplay.se/video/old-completed-download",
                kind = "webMedia",
                createdAt = "2026-06-28T09:00:00+00:00",
                updatedAt = "2026-06-28T09:41:00+00:00",
                completedAt = "2026-06-28T09:41:00+00:00",
                category = JobCategory.MOVIE,
            )
            jobs += Job(
                id = "seed-5",
                title = "Placement failed — permissions",
                state = JobState.FAILED,
                sourceUrl = "https://www.svtplay.se/video/placement-failed-demo",
                kind = "webMedia",
                errorMessage = "Failed to place downloaded file into the library: Access to the path " +
                    "'/media/series/Placement failed — permissions/S01' is denied. The jellyfin service " +
                    "user does not have write permission on the target directory. Fix on the server: " +
                    "sudo chown -R jellyfin:jellyfin /media/series && sudo chmod -R u+rwX /media/series, " +
                    "then retry this job.",
            )
            jobs += Job(
                id = "seed-6",
                title = "Abbormästarna",
                state = JobState.DOWNLOADING,
                progressPercent = 46,
                isGroup = true,
                childCount = 13,
                statusText = "6/13 items finished",
                sourceUrl = "https://www.svtplay.se/abbormastarna",
                kind = "webMedia",
                createdAt = "2026-07-03T18:00:00+00:00",
                updatedAt = "2026-07-04T01:00:00+00:00",
                category = JobCategory.SERIES,
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

    override fun getJobDetail(id: String, callback: (Result<Job>) -> Unit) {
        respond(callback) {
            val job = synchronized(lock) { jobs.firstOrNull { it.id == id } }
                ?: throw java.io.IOException("Job not found (HTTP 404).")
            if (job.isGroup && job.children == null) job.copy(children = fakeChildrenFor(job)) else job
        }
    }

    /**
     * Fake per-episode children exercising every rendering case: a Completed
     * episode with full SeriesName/SeasonNumber(=year)/EpisodeNumber/EpisodeTitle,
     * a Downloading one mid-progress, a Failed one with its own independent
     * error (siblings unaffected), and Queued ones still only labelled via
     * Title (matches media-downloader's pre-download "Avsnitt N" behavior —
     * the structured fields land at completion only).
     */
    private fun fakeChildrenFor(parent: Job): List<Job> = listOf(
        Job(
            id = "${parent.id}-c1",
            parentId = parent.id,
            title = "Avsnitt 1",
            state = JobState.COMPLETED,
            progressPercent = 100,
            sourceUrl = "https://www.svtplay.se/video/abbormastarna-avsnitt-1",
            finalPaths = listOf("/media/series/Abbormästarna/S2024/Abbormästarna S2024E01.mkv"),
            seriesName = "Abbormästarna",
            seasonNumber = 2024,
            episodeNumber = 1,
            episodeTitle = "Avsnitt 1",
            completedAt = "2026-07-03T19:00:00+00:00",
        ),
        Job(
            id = "${parent.id}-c2",
            parentId = parent.id,
            title = "Avsnitt 2",
            state = JobState.DOWNLOADING,
            progressPercent = 63,
            speedBytesPerSec = 2_100_000,
            etaSeconds = 40,
            sourceUrl = "https://www.svtplay.se/video/abbormastarna-avsnitt-2",
        ),
        Job(
            id = "${parent.id}-c3",
            parentId = parent.id,
            title = "Avsnitt 3",
            state = JobState.FAILED,
            sourceUrl = "https://www.svtplay.se/video/abbormastarna-avsnitt-3",
            errorMessage = "svtplay-dl exited with code 1: \"Can't find any videos at that URL — the " +
                "extractor may be out of date for this show.\" This episode failed independently; the " +
                "rest of the series is unaffected.",
        ),
    ) + (4..13).map { n ->
        Job(
            id = "${parent.id}-c$n",
            parentId = parent.id,
            title = "Avsnitt $n",
            state = JobState.QUEUED,
            sourceUrl = "https://www.svtplay.se/video/abbormastarna-avsnitt-$n",
        )
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

    // --- Metadata correction (fake) -----------------------------------------

    override fun getJobLibraryItem(jobId: String, callback: (Result<LibraryItem?>) -> Unit) {
        respond(callback) {
            val itemId = synchronized(lock) { jobToItem[jobId] }
            // A completed job with a title we recognise but no explicit mapping still
            // resolves to the deliberately-wrong Matrix item, so ANY freshly-completed
            // demo download surfaces the "spot the wrong match" case.
            val item = itemId?.let { synchronized(lock) { libraryItems[it] } }
                ?: synchronized(lock) { libraryItems["item-matrix"] }
            item
        }
    }

    override fun listLibraryItems(
        query: String?,
        type: LibraryItemType?,
        startIndex: Int,
        limit: Int,
        callback: (Result<LibraryItemPage>) -> Unit,
    ) {
        respond(callback) {
            val all = synchronized(lock) { libraryItems.values.toList() }
            val filtered = all.filter { item ->
                (type == null || item.type == type) &&
                    (query.isNullOrBlank() || item.name.contains(query.trim(), ignoreCase = true))
            }.sortedBy { it.name.lowercase() }
            val page = filtered.drop(startIndex).take(limit)
            LibraryItemPage(items = page, totalCount = filtered.size, startIndex = startIndex)
        }
    }

    override fun searchRemoteMetadata(
        itemId: String,
        searchType: LibraryItemType,
        name: String,
        year: Int?,
        callback: (Result<List<RemoteSearchCandidate>>) -> Unit,
    ) {
        respond(callback) {
            // Synthesize a few plausible candidates from the query so the picker
            // list, poster-placeholder, overview, and provider-id rows all render.
            val base = name.trim().ifBlank { "Unknown" }
            listOf(
                RemoteSearchCandidate(
                    name = base,
                    year = year ?: 1999,
                    overview = "A computer hacker learns the true nature of his reality. " +
                        "(fake remote-search result for \"$base\")",
                    providerIds = mapOf("Tmdb" to "603", "Imdb" to "tt0133093"),
                    imageUrl = null,
                    rawResult = "{\"fake\":true,\"name\":\"$base\",\"tmdb\":\"603\"}",
                ),
                RemoteSearchCandidate(
                    name = "$base Reloaded",
                    year = 2003,
                    overview = "The sequel. (fake candidate)",
                    providerIds = mapOf("Tmdb" to "604"),
                    imageUrl = null,
                    rawResult = "{\"fake\":true,\"tmdb\":\"604\"}",
                ),
                RemoteSearchCandidate(
                    name = "$base Revolutions",
                    year = 2003,
                    overview = "The third one. (fake candidate)",
                    providerIds = mapOf("Tmdb" to "605"),
                    imageUrl = null,
                    rawResult = "{\"fake\":true,\"tmdb\":\"605\"}",
                ),
            )
        }
    }

    override fun applyCorrectionByResult(
        itemId: String,
        candidate: RemoteSearchCandidate,
        callback: (Result<Unit>) -> Unit,
    ) {
        respond(callback) {
            applyCorrection(itemId, candidate.name, candidate.year, candidate.providerIds)
        }
    }

    override fun applyCorrectionByProvider(
        itemId: String,
        searchType: LibraryItemType,
        provider: String,
        providerId: String,
        callback: (Result<Unit>) -> Unit,
    ) {
        respond(callback) {
            // Mimic a server that rejects an obviously bad provider id, so the
            // failure-Toast path (W-056: don't silently discard) is exercisable.
            if (providerId.isBlank() || !providerId.any { it.isDigit() }) {
                throw IllegalStateException(
                    "Server rejected the provider id \"$providerId\" — expected a numeric id " +
                        "like 603 (from the TMDb URL themoviedb.org/movie/603).",
                )
            }
            applyCorrection(itemId, name = null, year = null, providerIds = mapOf(provider to providerId))
        }
    }

    /**
     * Fake "apply": mutate the stored library item in place so a subsequent
     * [getJobLibraryItem]/[listLibraryItems] re-fetch reflects the correction —
     * exercising the W-064 "re-fetch to confirm the new match landed" UI path.
     * Simulates async by only flipping the name if a real remote name was given.
     */
    private fun applyCorrection(
        itemId: String,
        name: String?,
        year: Int?,
        providerIds: Map<String, String>,
    ) {
        synchronized(lock) {
            val existing = libraryItems[itemId] ?: return
            libraryItems[itemId] = existing.copy(
                name = name ?: existing.name,
                year = year ?: existing.year,
                providerIds = existing.providerIds + providerIds,
            )
        }
    }

    override fun convertType(
        itemId: String,
        targetType: LibraryItemType,
        callback: (Result<ConvertTypeResult>) -> Unit,
    ) {
        respond(callback) {
            val existing = synchronized(lock) { libraryItems[itemId] }
                ?: throw java.io.IOException("Item not found (HTTP 404).")
            // Mirror the server's 400 guards (W-056: reject up front, don't no-op silently).
            if (existing.type == null) {
                throw IllegalStateException(
                    "Can't convert this item — it isn't a Movie or a Series (an Episode is " +
                        "corrected via its parent series).",
                )
            }
            if (existing.type == targetType) {
                throw IllegalStateException("This item is already a ${targetType.wireName}.")
            }

            val title = existing.name
            // Simulate the async re-ingest: after a short delay, DELETE the old
            // item and re-create it under a NEW id with the target type, so a
            // subsequent listLibraryItems poll surfaces the re-typed item (and the
            // old SourceItemId is genuinely gone). Exercises the real W-064 flow.
            val newId = "item-converted-${idCounter.getAndIncrement()}"
            executor.submit {
                Thread.sleep(1500)
                synchronized(lock) {
                    libraryItems.remove(itemId)
                    libraryItems[newId] = existing.copy(id = newId, type = targetType)
                }
            }

            ConvertTypeResult(
                sourceItemId = itemId,
                targetType = targetType,
                status = "RescanPending",
                newLibraryRoot = if (targetType == LibraryItemType.MOVIE) "/media/movies" else "/media/series",
                movedPaths = listOf(
                    if (targetType == LibraryItemType.MOVIE) {
                        "/media/movies/$title/$title.mkv"
                    } else {
                        "/media/series/$title/Season 01/$title - S01E01.mkv"
                    },
                ),
                title = title,
                message = "Files moved and a library rescan was triggered. The new " +
                    "${targetType.wireName} will appear once the scan finishes.",
            )
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
