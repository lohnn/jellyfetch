using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Configuration;
using Jellyfetch.Plugin.Download;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfetch.Plugin.Jobs;

/// <summary>
/// The central job queue: accepts submissions, routes them to registered <see cref="IDownloadHandler"/>s,
/// enforces the configured concurrency limit, streams progress into the persisted job model, and runs
/// library placement + scan on completion. Runs as a plugin-registered <see cref="IHostedService"/>.
/// Every handler call is exception-wrapped — a failing backend yields a Failed job, never a server crash.
/// </summary>
public sealed class DownloadJobManager : IHostedService, IDisposable
{
    private static readonly TimeSpan _persistInterval = TimeSpan.FromSeconds(3);

    private readonly IEnumerable<IDownloadHandler> _handlers;
    private readonly IMediaPlacer _placer;
    private readonly JobStore _store;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<DownloadJobManager> _logger;

    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly object _pumpLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private DateTimeOffset _lastPersist = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadJobManager"/> class.
    /// </summary>
    /// <param name="handlers">All registered download handlers (media-downloader, torrent-engine).</param>
    /// <param name="placer">The media placement service.</param>
    /// <param name="store">The persisted job store.</param>
    /// <param name="libraryMonitor">Jellyfin library monitor used to trigger scoped scans.</param>
    /// <param name="logger">Logger.</param>
    public DownloadJobManager(
        IEnumerable<IDownloadHandler> handlers,
        IMediaPlacer placer,
        JobStore store,
        ILibraryMonitor libraryMonitor,
        ILogger<DownloadJobManager> logger)
    {
        _handlers = handlers;
        _placer = placer;
        _store = store;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private string StagingRoot =>
        string.IsNullOrWhiteSpace(Config.StagingPath)
            ? Path.Combine(_store.DataDirectory, "staging")
            : Config.StagingPath;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var job in _store.Load())
        {
            // Restart recovery: anything that was mid-flight is failed-with-retry; queued stays queued.
            if (job.State is JobState.Resolving or JobState.Downloading or JobState.Processing)
            {
                job.State = JobState.Failed;
                job.ErrorMessage = "Interrupted by server restart. Retry to resume.";
                job.CompletedAt = DateTimeOffset.UtcNow;
            }

            _jobs[job.Id] = job;
        }

        _logger.LogInformation("JellyFetch job manager started with {Count} persisted jobs, {Handlers} handler(s)", _jobs.Count, _handlers.Count());
        Persist(force: true);
        PumpQueue();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdownCts.Cancel();
        foreach (var cts in _running.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        Persist(force: true);
        return Task.CompletedTask;
    }

    /// <summary>Gets all jobs (snapshot).</summary>
    /// <returns>All jobs.</returns>
    public IReadOnlyList<DownloadJob> GetJobs() => _jobs.Values.ToList();

    /// <summary>Gets a job by id.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The job or null.</returns>
    public DownloadJob? GetJob(Guid id) => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Gets the children of a group job.</summary>
    /// <param name="parentId">Parent job id.</param>
    /// <returns>Child jobs.</returns>
    public IReadOnlyList<DownloadJob> GetChildren(Guid parentId) =>
        _jobs.Values.Where(j => j.ParentId == parentId).OrderBy(j => j.CreatedAt).ToList();

    /// <summary>
    /// Accepts a new submission, returning the created job in state Queued.
    /// Throws <see cref="InvalidOperationException"/> when no handler accepts the input.
    /// </summary>
    /// <param name="request">The download request.</param>
    /// <returns>The created job.</returns>
    public DownloadJob Submit(DownloadRequest request)
    {
        var handler = _handlers.FirstOrDefault(h => SafeCanHandle(h, request));
        if (handler is null)
        {
            throw new InvalidOperationException(
                "No download backend accepts this input. Expected an http(s) URL, a magnet: URI, or a .torrent file.");
        }

        var job = new DownloadJob
        {
            Kind = handler.Kind,
            State = JobState.Queued,
            Title = request.SourceUrl ?? "torrent upload",
            SourceUrl = request.SourceUrl,
            Request = request,
        };

        _jobs[job.Id] = job;
        Persist(force: true);
        PumpQueue();
        return job;
    }

    /// <summary>Cancels a job (and its children when it is a group parent).</summary>
    /// <param name="id">Job id.</param>
    /// <returns>false when the job does not exist or is already terminal.</returns>
    public bool Cancel(Guid id)
    {
        var job = GetJob(id);
        if (job is null || job.IsTerminal)
        {
            return false;
        }

        if (job.IsGroup)
        {
            foreach (var child in GetChildren(id).Where(c => !c.IsTerminal))
            {
                CancelSingle(child);
            }

            RecomputeGroupState(job);
        }
        else
        {
            CancelSingle(job);
            if (job.ParentId is { } pid && GetJob(pid) is { } parent)
            {
                RecomputeGroupState(parent);
            }
        }

        Persist(force: true);
        PumpQueue();
        return true;
    }

    /// <summary>Retries a Failed or Cancelled job. Group parents retry all failed/cancelled children.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>false when the job does not exist or is not retryable.</returns>
    public bool Retry(Guid id)
    {
        var job = GetJob(id);
        if (job is null)
        {
            return false;
        }

        if (job.IsGroup)
        {
            var retried = false;
            foreach (var child in GetChildren(id).Where(c => c.State is JobState.Failed or JobState.Cancelled))
            {
                ResetForRetry(child);
                retried = true;
            }

            if (retried)
            {
                job.State = JobState.Downloading;
                job.ErrorMessage = null;
                job.CompletedAt = null;
                Touch(job);
            }

            Persist(force: true);
            PumpQueue();
            return retried;
        }

        if (job.State is not (JobState.Failed or JobState.Cancelled))
        {
            return false;
        }

        ResetForRetry(job);
        if (job.ParentId is { } pid && GetJob(pid) is { } parent)
        {
            parent.State = JobState.Downloading;
            parent.CompletedAt = null;
            Touch(parent);
        }

        Persist(force: true);
        PumpQueue();
        return true;
    }

    /// <summary>Deletes a terminal job from history (children included for group parents). Never deletes media files.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>false when the job does not exist or is still active.</returns>
    public bool Delete(Guid id)
    {
        var job = GetJob(id);
        if (job is null || !job.IsTerminal)
        {
            return false;
        }

        if (job.IsGroup && GetChildren(id).Any(c => !c.IsTerminal))
        {
            return false;
        }

        foreach (var child in GetChildren(id))
        {
            _jobs.TryRemove(child.Id, out _);
        }

        _jobs.TryRemove(id, out _);
        Persist(force: true);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdownCts.Dispose();
    }

    private static void Touch(DownloadJob job) => job.UpdatedAt = DateTimeOffset.UtcNow;

    /// <summary>Normalizes empty/whitespace and svtplay-dl's literal "NA" sentinel to null (I-098).</summary>
    private static string? NullIfNa(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "NA", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

    /// <summary>
    /// Normalizes a resolved <see cref="MediaCategory"/> for persistence on the job: the internal
    /// placeholder <see cref="MediaCategory.Auto"/> ("not yet classified") becomes null so the DTO
    /// exposes only the three meaningful, renderable values (Series/Movie/Other).
    /// </summary>
    private static MediaCategory? NormalizeCategory(MediaCategory category) =>
        category == MediaCategory.Auto ? null : category;

    private bool SafeCanHandle(IDownloadHandler handler, DownloadRequest request)
    {
        try
        {
            return handler.CanHandle(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyFetch: handler {Kind} threw in CanHandle", handler.Kind);
            return false;
        }
    }

    private void CancelSingle(DownloadJob job)
    {
        if (_running.TryGetValue(job.Id, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            // The running task will observe cancellation and finalize state itself.
        }
        else if (!job.IsTerminal)
        {
            job.State = JobState.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            Touch(job);
        }
    }

    private void ResetForRetry(DownloadJob job)
    {
        job.State = JobState.Queued;
        job.ErrorMessage = null;
        job.CompletedAt = null;
        job.Percent = null;
        job.SpeedBps = null;
        job.EtaSeconds = null;
        job.DownloadedBytes = null;
        job.StatusText = null;
        Touch(job);
    }

    /// <summary>Starts queued jobs while concurrency slots are available.</summary>
    private void PumpQueue()
    {
        if (_shutdownCts.IsCancellationRequested)
        {
            return;
        }

        lock (_pumpLock)
        {
            var maxConcurrent = Math.Max(1, Config.MaxConcurrentDownloads);
            var active = _jobs.Values.Count(j => !j.IsGroup && j.State is JobState.Resolving or JobState.Downloading or JobState.Processing);

            foreach (var job in _jobs.Values.Where(j => j.State == JobState.Queued && !j.IsGroup).OrderBy(j => j.CreatedAt))
            {
                if (active >= maxConcurrent)
                {
                    break;
                }

                StartJob(job);
                active++;
            }
        }
    }

    private void StartJob(DownloadJob job)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        if (!_running.TryAdd(job.Id, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => RunJobAsync(job, cts.Token)).ContinueWith(
            _ =>
            {
                if (_running.TryRemove(job.Id, out var removed))
                {
                    removed.Dispose();
                }

                Persist(force: true);
                PumpQueue();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunJobAsync(DownloadJob job, CancellationToken ct)
    {
        try
        {
            var handler = _handlers.FirstOrDefault(h => h.Kind == job.Kind)
                ?? throw new InvalidOperationException($"Download backend '{job.Kind}' is not registered.");

            // Phase 1: resolve (only submission jobs carry a Request; children arrive pre-resolved).
            if (job.Item is null)
            {
                var request = job.Request ?? throw new InvalidOperationException("Job has neither a resolved item nor a request.");
                job.State = JobState.Resolving;
                Touch(job);
                Persist(force: true);

                var resolved = await handler.ResolveAsync(request, ct).ConfigureAwait(false);
                if (resolved.Items.Count == 0)
                {
                    throw new InvalidOperationException("The backend resolved this input to zero downloadable items.");
                }

                if (resolved.Items.Count == 1)
                {
                    job.Item = resolved.Items[0];
                    if (!string.IsNullOrWhiteSpace(job.Item.Title))
                    {
                        job.Title = job.Item.Title;
                    }
                }
                else
                {
                    // Fan out: submission job becomes a group parent; each item becomes an independent job.
                    job.IsGroup = true;
                    job.Title = resolved.GroupTitle ?? job.Title;
                    job.State = JobState.Downloading;
                    job.StatusText = $"{resolved.Items.Count} items";
                    Touch(job);

                    foreach (var item in resolved.Items)
                    {
                        var child = new DownloadJob
                        {
                            ParentId = job.Id,
                            Kind = job.Kind,
                            State = JobState.Queued,
                            Title = string.IsNullOrWhiteSpace(item.Title) ? job.Title : item.Title,
                            SourceUrl = item.SourceUrl,
                            Item = item,
                        };
                        _jobs[child.Id] = child;
                    }

                    Persist(force: true);
                    return; // Parent's slot is released; children go through the queue individually.
                }
            }

            // Phase 2: download.
            job.State = JobState.Downloading;
            Touch(job);
            Persist(force: true);

            var staging = Path.Combine(StagingRoot, job.Id.ToString("N"));
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);

            try
            {
                var progress = new Progress<JobProgress>(p => ApplyProgress(job, p));
                var result = await handler.ExecuteAsync(job.Item!, staging, progress, ct).ConfigureAwait(false);

                // Phase 3: placement.
                job.State = JobState.Processing;
                job.StatusText = "placing files into library";
                Touch(job);
                Persist(force: true);

                if (!string.IsNullOrWhiteSpace(result.Metadata.Title))
                {
                    job.Title = result.Metadata.Title;
                }

                var placement = await _placer.PlaceAsync(result, staging, ct).ConfigureAwait(false);
                job.FinalPaths = placement.FinalPaths.ToList();

                foreach (var path in placement.FinalPaths)
                {
                    try
                    {
                        _libraryMonitor.ReportFileSystemChanged(path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "JellyFetch: failed to report library change for {Path}", path);
                    }
                }

                // Post-download fan-out: one physical download (e.g. a season-pack torrent) that
                // produced multiple logical episodes. Materialize born-Completed display children and
                // let the parent become an aggregating group. No child re-runs ExecuteAsync.
                if (result.Children is { Count: > 1 } childSpecs)
                {
                    MaterializeCompletedChildren(job, childSpecs, placement.LibraryRootUsed);
                }
                else
                {
                    // Single-job completion: carry structured per-episode metadata onto the job so it
                    // reaches JobDto/the app. media-downloader computes these via the svtplay-dl --nfo
                    // probe (I-098). SVT quirk: SeasonNumber carries the YEAR — intentional, do not
                    // "correct". Treat literal "NA" as null.
                    var meta = result.Metadata;
                    job.Category = NormalizeCategory(meta.Category);
                    job.SeriesName = NullIfNa(meta.SeriesName);
                    job.SeasonNumber = meta.SeasonNumber;
                    job.EpisodeNumber = meta.EpisodeNumber;
                    job.EpisodeTitle = NullIfNa(meta.Title);

                    job.State = JobState.Completed;
                    job.Percent = 100;
                    job.SpeedBps = null;
                    job.EtaSeconds = null;
                    job.StatusText = null;
                    job.CompletedAt = DateTimeOffset.UtcNow;
                    Touch(job);
                }

                _logger.LogInformation("JellyFetch: job {Id} completed: {Title} ({Files} file(s))", job.Id, job.Title, job.FinalPaths.Count);
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }
        catch (OperationCanceledException)
        {
            if (_shutdownCts.IsCancellationRequested)
            {
                // Server shutdown/restart, not a user cancel: keep the job retryable with an honest reason.
                job.State = JobState.Failed;
                job.ErrorMessage = "Interrupted by server restart. Retry to resume.";
                _logger.LogInformation("JellyFetch: job {Id} interrupted by shutdown", job.Id);
            }
            else
            {
                job.State = JobState.Cancelled;
                _logger.LogInformation("JellyFetch: job {Id} cancelled", job.Id);
            }

            job.CompletedAt = DateTimeOffset.UtcNow;
            job.StatusText = null;
            Touch(job);
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.StatusText = null;
            Touch(job);
            _logger.LogError(ex, "JellyFetch: job {Id} failed: {Title}", job.Id, job.Title);
        }
        finally
        {
            if (job.ParentId is { } pid && GetJob(pid) is { } parent)
            {
                RecomputeGroupState(parent);
            }
        }
    }

    private void ApplyProgress(DownloadJob job, JobProgress p)
    {
        if (p.Percent.HasValue)
        {
            job.Percent = Math.Clamp(p.Percent.Value, 0, 100);
        }

        job.SpeedBps = p.SpeedBps ?? job.SpeedBps;
        job.EtaSeconds = p.EtaSeconds ?? job.EtaSeconds;
        job.DownloadedBytes = p.DownloadedBytes ?? job.DownloadedBytes;
        job.TotalBytes = p.TotalBytes ?? job.TotalBytes;
        job.StatusText = p.StatusText ?? job.StatusText;
        if (!string.IsNullOrWhiteSpace(p.Title))
        {
            job.Title = p.Title!;
        }

        Touch(job);
        Persist(force: false);
    }

    /// <summary>
    /// Turns a completed single-download job into a group parent and materializes one born-Completed
    /// display child per <see cref="DownloadChild"/>. Children never run through the queue (they start
    /// terminal); the parent's aggregate state/percent is derived by <see cref="RecomputeGroupState"/>.
    /// Each child's library-relative <see cref="DownloadChild.RelativePath"/> is resolved against the
    /// placement's library root (the handler cannot know the root at ExecuteAsync time).
    /// </summary>
    private void MaterializeCompletedChildren(DownloadJob parent, IReadOnlyList<DownloadChild> specs, string? libraryRoot)
    {
        parent.IsGroup = true;
        parent.Item = null; // a group parent is an aggregator, not a downloadable item
        var now = DateTimeOffset.UtcNow;

        foreach (var spec in specs)
        {
            var meta = spec.Metadata ?? new MediaMetadata();
            var finalPath = string.IsNullOrWhiteSpace(libraryRoot)
                ? spec.RelativePath
                : Path.Combine(libraryRoot, spec.RelativePath);

            var child = new DownloadJob
            {
                ParentId = parent.Id,
                Kind = parent.Kind,
                State = JobState.Completed,
                Title = string.IsNullOrWhiteSpace(meta.Title) ? parent.Title : meta.Title,
                SourceUrl = parent.SourceUrl,
                Percent = 100,
                FinalPaths = new List<string> { finalPath },
                Category = NormalizeCategory(meta.Category),
                SeriesName = NullIfNa(meta.SeriesName),
                SeasonNumber = meta.SeasonNumber,
                EpisodeNumber = meta.EpisodeNumber,
                EpisodeTitle = NullIfNa(meta.Title),
                CompletedAt = now,
            };
            _jobs[child.Id] = child;

            try
            {
                _libraryMonitor.ReportFileSystemChanged(finalPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyFetch: failed to report library change for child {Path}", finalPath);
            }
        }

        RecomputeGroupState(parent);
    }

    private void RecomputeGroupState(DownloadJob parent)
    {
        var children = GetChildren(parent.Id);
        if (children.Count == 0)
        {
            return;
        }

        var percents = children.Select(c => c.Percent ?? (c.State == JobState.Completed ? 100.0 : 0.0)).ToList();
        parent.Percent = percents.Count > 0 ? Math.Round(percents.Average(), 1) : null;
        var done = children.Count(c => c.IsTerminal);
        parent.StatusText = $"{done}/{children.Count} items finished";

        if (children.All(c => c.IsTerminal))
        {
            parent.State = children.Any(c => c.State == JobState.Completed)
                ? JobState.Completed
                : children.All(c => c.State == JobState.Cancelled) ? JobState.Cancelled : JobState.Failed;
            parent.ErrorMessage = parent.State == JobState.Failed
                ? $"{children.Count(c => c.State == JobState.Failed)} of {children.Count} items failed"
                : null;
            parent.CompletedAt = DateTimeOffset.UtcNow;
        }

        Touch(parent);
    }

    private void Persist(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastPersist < _persistInterval)
        {
            return;
        }

        _lastPersist = now;
        _store.Save(_jobs.Values.ToList());
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyFetch: could not clean staging directory {Path}", path);
        }
    }
}
