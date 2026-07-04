using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Configuration;
using MonoTorrent;
using MonoTorrent.Client;

namespace Jellyfetch.Plugin.Download.Torrents;

/// <summary>
/// <see cref="IDownloadHandler"/> for BitTorrent sources (magnet URIs and uploaded .torrent files),
/// built on MonoTorrent. Downloads-and-done: it stops as soon as the payload is complete and never
/// seeds. All output is written into the per-job staging directory; the core placer files it into
/// the library.
///
/// One <see cref="ClientEngine"/> is shared across all jobs (via <see cref="TorrentEngineHost"/>);
/// each job adds its own <see cref="TorrentManager"/>, downloads, then removes it.
/// </summary>
public sealed class TorrentDownloadHandler : IDownloadHandler, IDisposable
{
    /// <summary>Stable handler identifier persisted with jobs and surfaced in the REST API.</summary>
    public const string KindId = "torrent";

    // Extensions that are payload junk, never real media — filtered from the completion result.
    private static readonly HashSet<string> JunkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".txt", ".sfv", ".srr", ".srs", ".md5", ".exe", ".bat", ".lnk",
        ".url", ".website", ".jpg", ".jpeg", ".png", ".gif", ".db", ".ds_store",
    };

    // Filenames that are junk regardless of extension.
    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sample", "rarbg.txt", "downloaded from.txt", ".pad",
    };

    // Video containers we treat as the real payload for classification.
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".ts", ".m4v", ".wmv", ".flv", ".webm", ".mpg", ".mpeg",
    };

    private readonly TorrentEngineHost _engineHost = new();

    /// <inheritdoc />
    public string Kind => KindId;

    /// <inheritdoc />
    public bool CanHandle(DownloadRequest request)
    {
        if (request is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.TorrentFileBase64))
        {
            return true;
        }

        var url = request.SourceUrl;
        return !string.IsNullOrWhiteSpace(url)
            && url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<ResolveResult> ResolveAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        // A single torrent is one job. Metadata (and therefore the true file/episode list) is only
        // known after peers deliver it, which we defer to ExecuteAsync — so we do NOT fan out here.
        // A season-pack torrent is downloaded as one job and self-laid-out per episode at completion.
        var item = new DownloadItem
        {
            Title = DeriveInitialTitle(request),
            SourceUrl = request.SourceUrl,
            TorrentFileBase64 = request.TorrentFileBase64,
            Category = request.CategoryHint,
        };

        var result = new ResolveResult { Items = new[] { item } };
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public async Task<DownloadResult> ExecuteAsync(
        DownloadItem item,
        string stagingDirectory,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var cacheRoot = Path.Combine(ResolveCacheRoot(config), "torrent-engine-cache");

        var engine = await _engineHost
            .GetEngineAsync(cacheRoot, config.TorrentListenPort, cancellationToken)
            .ConfigureAwait(false);

        TorrentManager? manager = null;
        try
        {
            manager = await AddTorrentAsync(engine, item, stagingDirectory, cancellationToken)
                .ConfigureAwait(false);

            await manager.StartAsync().ConfigureAwait(false);

            await DriveToCompletionAsync(manager, progress, cancellationToken).ConfigureAwait(false);

            // Payload complete. Stop cleanly before we touch the files.
            await manager.StopAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            return BuildResult(manager, item, stagingDirectory);
        }
        catch (OperationCanceledException)
        {
            // User cancelled or shutdown: stop + remove + purge partial data. The core also deletes
            // the staging directory, but we release engine resources promptly.
            await SafeTeardownAsync(engine, manager, RemoveMode.CacheDataAndDownloadedData).ConfigureAwait(false);
            throw;
        }
        catch
        {
            // Failure: keep partial data out of the way, let the manager mark the job Failed.
            await SafeTeardownAsync(engine, manager, RemoveMode.CacheDataAndDownloadedData).ConfigureAwait(false);
            throw;
        }
        finally
        {
            // On success the manager is stopped; remove it from the engine (keep the downloaded data
            // in staging for placement). Idempotent with the catch-path teardown.
            if (manager is not null && engine.Torrents.Contains(manager))
            {
                try
                {
                    await engine.RemoveAsync(manager, RemoveMode.CacheDataOnly).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
    }

    private static async Task<TorrentManager> AddTorrentAsync(
        ClientEngine engine,
        DownloadItem item,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);

        if (!string.IsNullOrWhiteSpace(item.TorrentFileBase64))
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(item.TorrentFileBase64);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("The uploaded .torrent data was not valid base64.", ex);
            }

            Torrent torrent;
            try
            {
                torrent = Torrent.Load(bytes);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("The uploaded file is not a valid .torrent.", ex);
            }

            return await engine.AddAsync(torrent, stagingDirectory).ConfigureAwait(false);
        }

        var url = item.SourceUrl;
        if (string.IsNullOrWhiteSpace(url) || !MagnetLink.TryParse(url, out var magnet) || magnet is null)
        {
            throw new InvalidOperationException($"Not a valid magnet URI: '{url}'.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await engine.AddAsync(magnet, stagingDirectory).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls the manager until the payload is complete, streaming progress. Handles the magnet
    /// "resolving metadata" phase (no size/name yet) distinctly from the downloading phase.
    /// </summary>
    private static async Task DriveToCompletionAsync(
        TorrentManager manager,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken)
    {
        var lastReportedTitle = manager.Torrent?.Name;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (manager.Complete)
            {
                return;
            }

            if (manager.State == TorrentState.Error)
            {
                var reason = manager.Error?.Exception?.Message ?? "unknown torrent error";
                throw new InvalidOperationException($"Torrent entered error state: {reason}");
            }

            progress.Report(BuildProgress(manager, ref lastReportedTitle));

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static JobProgress BuildProgress(TorrentManager manager, ref string? lastReportedTitle)
    {
        var hasMetadata = manager.HasMetadata && manager.Torrent is not null;
        var totalBytes = hasMetadata ? manager.Torrent!.Size : (long?)null;
        var rate = manager.Monitor.DownloadRate;
        var downloaded = manager.Monitor.DataBytesReceived;

        long? eta = null;
        if (rate > 0 && totalBytes.HasValue)
        {
            var remaining = totalBytes.Value - (long)(totalBytes.Value * (manager.Progress / 100.0));
            eta = remaining > 0 ? remaining / rate : 0;
        }

        var status = manager.State switch
        {
            TorrentState.Metadata => "Resolving metadata",
            TorrentState.Hashing => "Verifying downloaded pieces",
            TorrentState.Starting => "Starting",
            TorrentState.Downloading => "Downloading",
            TorrentState.Seeding => "Finalizing",
            TorrentState.Stopping => "Stopping",
            _ => manager.State.ToString(),
        };

        string? improvedTitle = null;
        var currentName = manager.Torrent?.Name;
        if (!string.IsNullOrEmpty(currentName) && currentName != lastReportedTitle)
        {
            improvedTitle = currentName;
            lastReportedTitle = currentName;
        }

        return new JobProgress
        {
            Percent = hasMetadata ? manager.Progress : null,
            SpeedBps = rate,
            EtaSeconds = eta,
            DownloadedBytes = downloaded,
            TotalBytes = totalBytes,
            StatusText = status,
            Title = improvedTitle,
        };
    }

    /// <summary>
    /// Assembles the completion result: filters junk, parses the release name, and — for season
    /// packs (multiple episodes in one torrent) — self-lays-out per episode.
    /// </summary>
    private static DownloadResult BuildResult(TorrentManager manager, DownloadItem item, string stagingDirectory)
    {
        var torrentName = manager.Torrent?.Name ?? item.Title;

        // All real payload files on disk (absolute paths inside staging), junk removed.
        var payloadFiles = manager.Files
            .Where(f => !IsJunk(f.Path, f.Length))
            .Select(f => f.FullPath)
            .Where(File.Exists)
            .ToList();

        if (payloadFiles.Count == 0)
        {
            // Nothing but junk (or filter too aggressive) — fall back to everything that exists.
            payloadFiles = manager.Files.Select(f => f.FullPath).Where(File.Exists).ToList();
        }

        // Classify the torrent as a whole using its name (most reliable single signal),
        // then refine per-file for season packs.
        var top = ReleaseNameParser.Parse(torrentName);

        var videoFiles = payloadFiles
            .Where(p => VideoExtensions.Contains(Path.GetExtension(p)))
            .ToList();

        // Season pack: the torrent parses as a season pack OR there are multiple distinct
        // episodes among the video files. Lay each episode out itself.
        if (videoFiles.Count > 1 && LooksEpisodic(top, videoFiles))
        {
            return BuildSeasonPackResult(top, videoFiles, stagingDirectory);
        }

        return BuildSingleResult(top, payloadFiles, torrentName);
    }

    private static bool LooksEpisodic(ParsedRelease top, IReadOnlyList<string> videoFiles)
    {
        if (top.Kind is ReleaseKind.SeasonPack or ReleaseKind.Episode)
        {
            return true;
        }

        // If most video files individually parse as episodes, treat as a pack.
        var episodic = videoFiles.Count(f =>
            ReleaseNameParser.Parse(Path.GetFileNameWithoutExtension(f)).Kind == ReleaseKind.Episode);
        return episodic >= 2;
    }

    private static DownloadResult BuildSeasonPackResult(
        ParsedRelease top,
        IReadOnlyList<string> videoFiles,
        string stagingDirectory)
    {
        var seriesName = string.IsNullOrWhiteSpace(top.Title) ? "Unknown" : top.Title;

        // One pure computation drives BOTH the physical layout and the child fan-out, so they can
        // never disagree: each episode's RelativePath is simultaneously the staging-relative move
        // target (PreLaidOut) and the library-relative path the manager resolves for its child rows.
        var episodes = SeasonPackChildBuilder.Build(top, videoFiles);

        var finalFiles = new List<string>(episodes.Count);
        foreach (var episode in episodes)
        {
            var absoluteTarget = Path.Combine(stagingDirectory, episode.RelativePath);
            MoveWithinStaging(episode.SourceFile, absoluteTarget);
            finalFiles.Add(absoluteTarget);
        }

        // Post-download fan-out: give the dashboard one row per episode (parity with the web series
        // path). The manager only fans out when Children.Count > 1, so a pack that resolved to a
        // single episode stays a single job — no spurious group. Children carry library-relative
        // paths; the manager resolves them against PlacementResult.LibraryRootUsed after placement.
        var children = SeasonPackChildBuilder.ToChildren(episodes);

        return new DownloadResult
        {
            Files = finalFiles,
            PreLaidOut = true, // we produced the {Series}/Season NN/... tree ourselves
            Children = children,
            Metadata = new MediaMetadata
            {
                Title = seriesName,
                Category = MediaCategory.Series,
                SeriesName = seriesName,
                SeasonNumber = top.Season,
                Year = top.Year,
            },
        };
    }

    private static DownloadResult BuildSingleResult(ParsedRelease parsed, IReadOnlyList<string> files, string torrentName)
    {
        // Low confidence → route to the fallback (Other) root rather than mis-file.
        var trustworthy = parsed.Confidence >= 0.5;

        var metadata = new MediaMetadata { Title = FallbackTitle(parsed, torrentName) };

        if (trustworthy && parsed.Kind == ReleaseKind.Episode)
        {
            metadata.Category = MediaCategory.Series;
            metadata.SeriesName = parsed.Title;
            metadata.SeasonNumber = parsed.Season;
            metadata.EpisodeNumber = parsed.Episodes.Count > 0 ? parsed.Episodes[0] : null;
            metadata.Title = parsed.Title; // placer builds "Series - SxxEyy - Title"
            metadata.Year = parsed.Year;
        }
        else if (trustworthy && parsed.Kind == ReleaseKind.Movie)
        {
            metadata.Category = MediaCategory.Movie;
            metadata.Title = parsed.Title;
            metadata.Year = parsed.Year;
        }
        else
        {
            // Unknown / season-pack-single / low confidence → Other (fallback root).
            metadata.Category = MediaCategory.Other;
        }

        return new DownloadResult
        {
            Files = files,
            PreLaidOut = false, // let the core placer name it
            Metadata = metadata,
        };
    }

    private static string FallbackTitle(ParsedRelease parsed, string torrentName)
        => !string.IsNullOrWhiteSpace(parsed.Title) ? parsed.Title : torrentName;

    private static bool IsJunk(string relativePath, long length)
    {
        var name = Path.GetFileName(relativePath);
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        if (JunkNames.Contains(name))
        {
            return true;
        }

        var ext = Path.GetExtension(name);
        if (JunkExtensions.Contains(ext))
        {
            return true;
        }

        // A "sample" file is junk (common release convention): small and named sample.
        if (name.Contains("sample", StringComparison.OrdinalIgnoreCase) && length < 200L * 1024 * 1024)
        {
            return true;
        }

        return false;
    }

    private static void MoveWithinStaging(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            return;
        }

        if (File.Exists(target))
        {
            File.Delete(target);
        }

        try
        {
            File.Move(source, target);
        }
        catch (IOException)
        {
            File.Copy(source, target, overwrite: true);
            File.Delete(source);
        }
    }

    private static async Task SafeTeardownAsync(ClientEngine engine, TorrentManager? manager, RemoveMode mode)
    {
        if (manager is null)
        {
            return;
        }

        try
        {
            if (manager.State != TorrentState.Stopped)
            {
                await manager.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
        catch
        {
            // best-effort
        }

        try
        {
            if (engine.Torrents.Contains(manager))
            {
                await engine.RemoveAsync(manager, mode).ConfigureAwait(false);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static string ResolveCacheRoot(PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.StagingPath))
        {
            return config.StagingPath;
        }

        var dataPath = Plugin.Instance?.DataFolderPath;
        return string.IsNullOrWhiteSpace(dataPath) ? Path.GetTempPath() : dataPath;
    }

    private static string DeriveInitialTitle(DownloadRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SourceUrl)
            && MagnetLink.TryParse(request.SourceUrl, out var magnet)
            && magnet is not null
            && !string.IsNullOrWhiteSpace(magnet.Name))
        {
            return magnet.Name!;
        }

        return "Torrent download";
    }

    /// <inheritdoc />
    public void Dispose() => _engineHost.Dispose();
}
