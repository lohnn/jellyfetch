using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfetch.Plugin.Download;

/// <summary>
/// CONTRACT — owned by jellyfin-plugin, implemented by media-downloader (WebMedia/) and
/// torrent-engine (Torrents/). Changes to this interface are breaking; coordinate via HIVEmind.
///
/// Lifecycle driven by the core job queue (DownloadJobManager):
/// <list type="number">
///   <item>The manager picks the first registered handler whose <see cref="CanHandle"/> returns true.</item>
///   <item><see cref="ResolveAsync"/> is called with the job in state <c>Resolving</c>. It returns 1..N
///   <see cref="DownloadItem"/>s. Returning more than one (playlist / series expansion) makes the
///   submission job a group parent: the manager creates one independent child job per item, so one
///   failed episode never fails the whole series. Returning exactly one keeps the original job.</item>
///   <item><see cref="ExecuteAsync"/> is called once per item (job state <c>Downloading</c>), with a
///   per-job staging directory the handler must write all output into. Progress is streamed via
///   <paramref name="progress"/>; report as often as you like, the manager throttles persistence.</item>
///   <item>The returned <see cref="DownloadResult"/> hands produced files + best-known metadata back to
///   the core, which runs library placement (state <c>Processing</c>) and completion.</item>
/// </list>
///
/// Cancellation: the <see cref="CancellationToken"/> is cancelled when the user cancels the job or the
/// server shuts down. Handlers must abort promptly (kill subprocesses, stop torrents) and throw
/// <see cref="OperationCanceledException"/>. The manager deletes the staging directory afterwards —
/// handlers do not need to clean up partial files inside it.
///
/// Failures: throw any exception — the manager catches it, marks the job <c>Failed</c> with the
/// exception message, and never lets it reach the Jellyfin process. Prefer descriptive messages.
///
/// Retry/restart: <see cref="DownloadItem"/> is persisted verbatim with the job; after a retry or a
/// server restart the manager calls <see cref="ExecuteAsync"/> again with a fresh staging directory.
/// Anything the handler needs to re-execute must live inside the item (use <see cref="DownloadItem.HandlerPayload"/>).
/// </summary>
public interface IDownloadHandler
{
    /// <summary>
    /// Gets the stable handler kind identifier, e.g. "webMedia" or "torrent".
    /// Persisted with jobs and exposed in the REST API as <c>Kind</c>.
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Cheap, synchronous classification: can this handler process the request?
    /// (magnet:/.torrent → torrent-engine; http(s) → media-downloader). No network I/O here.
    /// </summary>
    /// <param name="request">The submitted download request.</param>
    /// <returns>true if this handler should own the request.</returns>
    bool CanHandle(DownloadRequest request);

    /// <summary>
    /// Deep classification and expansion. May do network I/O (probe the URL, expand a playlist or a
    /// series into episodes). Runs with the job in state <c>Resolving</c> and occupies a concurrency slot.
    /// </summary>
    /// <param name="request">The submitted download request.</param>
    /// <param name="cancellationToken">Cancelled on user cancel or shutdown.</param>
    /// <returns>The resolved item list (1..N) plus an optional group title.</returns>
    Task<ResolveResult> ResolveAsync(DownloadRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a single resolved item into <paramref name="stagingDirectory"/> (exists, is empty,
    /// and is exclusive to this job).
    /// </summary>
    /// <param name="item">The item to download (round-tripped through persistence — POCO only).</param>
    /// <param name="stagingDirectory">Absolute path all output files must be written under.</param>
    /// <param name="progress">Progress sink; feeds the job model and the REST API.</param>
    /// <param name="cancellationToken">Cancelled on user cancel or shutdown.</param>
    /// <returns>Produced files and best-known metadata.</returns>
    Task<DownloadResult> ExecuteAsync(DownloadItem item, string stagingDirectory, IProgress<JobProgress> progress, CancellationToken cancellationToken);
}
