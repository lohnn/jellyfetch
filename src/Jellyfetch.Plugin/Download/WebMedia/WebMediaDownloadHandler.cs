using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfetch.Plugin.Download.WebMedia;

/// <summary>
/// The web-media <see cref="IDownloadHandler"/> (<c>Kind == "webMedia"</c>): yt-dlp +
/// svtplay-dl orchestration for http(s) URLs.
///
/// Design:
///   • <see cref="ResolveAsync"/> classifies + expands (playlist/series → 1 item per
///     episode). The core turns N&gt;1 into independent child jobs, so one failed
///     episode never fails the series.
///   • <see cref="ExecuteAsync"/> downloads one item into the supplied staging dir,
///     then lays the produced files out in library-relative structure inside staging
///     and returns <c>PreLaidOut = true</c>. The core placer moves that subtree
///     verbatim under the category root — this backend owns the naming conventions.
///   • Config (paths, roots, staging) is read live from <c>Plugin.Instance.Configuration</c>.
///   • Failures throw; cancellation kills the subprocess tree and throws
///     <see cref="OperationCanceledException"/> — per the contract the core cleans staging.
///
/// The classification, naming, and progress-parsing logic is pure and unit-tested;
/// this class is the only process-spawning, I/O-doing piece.
/// </summary>
public sealed class WebMediaDownloadHandler : IDownloadHandler
{
    private static readonly string[] VideoExtensions =
        { ".mp4", ".mkv", ".webm", ".ts", ".m4v", ".mov", ".flv", ".avi" };

    private static readonly string[] SubtitleExtensions = { ".srt", ".vtt", ".ass", ".ssa" };

    private readonly ILogger<WebMediaDownloadHandler> _logger;
    private readonly ToolRouter _router = new();
    private readonly MediaOrganizer _organizer = new();
    private readonly ProcessRunner _runner = new();

    /// <summary>Initializes a new instance of the <see cref="WebMediaDownloadHandler"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public WebMediaDownloadHandler(ILogger<WebMediaDownloadHandler> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Kind => "webMedia";

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public bool CanHandle(DownloadRequest request)
    {
        if (request?.SourceUrl is not { Length: > 0 } url)
        {
            return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <inheritdoc />
    public async Task<ResolveResult> ResolveAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        var url = request.SourceUrl
                  ?? throw new InvalidOperationException("WebMedia handler requires a SourceUrl.");
        var tool = _router.Route(url);

        var classification = tool == DownloadTool.SvtPlayDl
            ? await ClassifySvtAsync(url, cancellationToken).ConfigureAwait(false)
            : await ClassifyYtDlpAsync(url, cancellationToken).ConfigureAwait(false);

        if (classification.Failed)
        {
            throw new InvalidOperationException(
                $"Could not resolve '{url}'. The site may be unsupported or geo-restricted.");
        }

        if (!classification.IsMultiJob)
        {
            var single = new DownloadItem
            {
                Title = classification.ContainerTitle ?? url,
                SourceUrl = url,
                Category = request.CategoryHint,
                HandlerPayload = SerializePayload(new HandlerPayload(tool, null)),
            };
            return new ResolveResult { Items = new[] { single } };
        }

        var items = new List<DownloadItem>(classification.Entries.Count);
        foreach (var entry in classification.Entries)
        {
            items.Add(new DownloadItem
            {
                Title = entry.Title ?? entry.Url,
                SourceUrl = entry.Url,
                Category = request.CategoryHint,
                HandlerPayload = SerializePayload(new HandlerPayload(tool, entry.Ordinal)),
            });
        }

        return new ResolveResult { Items = items, GroupTitle = classification.ContainerTitle };
    }

    /// <inheritdoc />
    public async Task<DownloadResult> ExecuteAsync(
        DownloadItem item,
        string stagingDirectory,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken)
    {
        var url = item.SourceUrl
                  ?? throw new InvalidOperationException("WebMedia item is missing SourceUrl.");
        var payload = DeserializePayload(item.HandlerPayload);
        var tool = payload.Tool;

        progress.Report(new JobProgress { Percent = null, StatusText = "Fetching metadata" });

        // Download everything into a private sub-dir first, then lay out under staging root.
        var workDir = Path.Combine(stagingDirectory, "_work");
        Directory.CreateDirectory(workDir);

        var meta = await ResolveMetadataAsync(tool, url, item, workDir, cancellationToken).ConfigureAwait(false);
        if (meta.Category == MediaCategory.Series && meta.EpisodeNumber == null && payload.Ordinal is int ord)
        {
            meta.EpisodeNumber = ord;
        }

        progress.Report(new JobProgress { Percent = 0, StatusText = "Downloading", Title = meta.Title });

        var stderrTail = new StderrTail();
        var exit = await RunDownloadAsync(tool, url, workDir, meta.Title, progress, stderrTail, cancellationToken)
            .ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"{ToolName(tool)} exited with code {exit}. {stderrTail.Text}".Trim());
        }

        progress.Report(new JobProgress { Percent = 100, StatusText = "Organizing" });
        var files = LayOut(workDir, stagingDirectory, meta);

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"{ToolName(tool)} reported success but produced no media file.");
        }

        return new DownloadResult
        {
            Files = files,
            Metadata = meta,
            PreLaidOut = true,
        };
    }

    // ---- Resolution / classification ----

    private async Task<UrlClassification> ClassifyYtDlpAsync(string url, CancellationToken ct)
    {
        var res = await _runner.RunAsync(Config.YtDlpPath, YtDlpIntrospector.ClassifyArgs(url), ct)
            .ConfigureAwait(false);
        return YtDlpIntrospector.Classify(res.StdOut); // JSON on stdout only
    }

    private async Task<UrlClassification> ClassifySvtAsync(string url, CancellationToken ct)
    {
        var res = await _runner.RunAsync(Config.SvtPlayDlPath, SvtPlayDlIntrospector.EpisodeListArgs(url), ct)
            .ConfigureAwait(false);
        return SvtPlayDlIntrospector.ClassifyProgram(res.StdErr); // episode URLs on stderr
    }

    private async Task<MediaMetadata> ResolveMetadataAsync(
        DownloadTool tool, string url, DownloadItem item, string workDir, CancellationToken ct)
    {
        if (tool == DownloadTool.SvtPlayDl)
        {
            var probeDir = Path.Combine(workDir, "_probe");
            Directory.CreateDirectory(probeDir);
            await _runner.RunAsync(Config.SvtPlayDlPath, SvtPlayDlIntrospector.NfoProbeArgs(url, probeDir), ct)
                .ConfigureAwait(false);

            var nfo = FirstNfo(probeDir);
            if (nfo != null)
            {
                var m = SvtPlayDlIntrospector.ParseEpisodeNfo(File.ReadAllText(nfo));
                Directory.Delete(probeDir, recursive: true);
                return m;
            }

            return new MediaMetadata
            {
                Category = MediaCategory.Series,
                Title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled" : item.Title,
                SeriesName = "Unknown Series",
            };
        }

        var res = await _runner.RunAsync(Config.YtDlpPath, YtDlpIntrospector.MetadataArgs(url), ct)
            .ConfigureAwait(false);
        var webDefault = string.IsNullOrWhiteSpace(Config.FallbackLibraryPath)
            ? MediaCategory.Other // still "Other"; placer falls back to movie root
            : MediaCategory.Other;
        return YtDlpIntrospector.ParseMetadata(res.StdOut, webDefault);
    }

    // ---- Download ----

    private async Task<int> RunDownloadAsync(
        DownloadTool tool,
        string url,
        string workDir,
        string? knownTitle,
        IProgress<JobProgress> progress,
        StderrTail stderrTail,
        CancellationToken ct)
    {
        var stall = TimeSpan.FromMinutes(5);
        if (tool == DownloadTool.SvtPlayDl)
        {
            var args = new[] { "-S", "--nfo", "-o", workDir, "--filename", "video.{ext}", url };
            return await _runner.StreamAsync(
                Config.SvtPlayDlPath,
                args,
                onStdout: _ => { },
                onStderr: line =>
                {
                    stderrTail.Add(line);
                    var p = ProgressParser.TryParseYtDlpLine(line);
                    if (p != null)
                    {
                        progress.Report(ToJobProgress(p, knownTitle));
                    }
                },
                stall,
                ct).ConfigureAwait(false);
        }

        var ytArgs = new List<string>();
        ytArgs.AddRange(ProgressParser.ProgressArgs());
        ytArgs.AddRange(new[] { "--write-subs", "--sub-langs", "all", "--no-warnings" });
        ytArgs.AddRange(new[] { "-o", Path.Combine(workDir, "video.%(ext)s"), url });

        return await _runner.StreamAsync(
            Config.YtDlpPath,
            ytArgs,
            onStdout: line =>
            {
                var p = ProgressParser.TryParseYtDlpLine(line);
                if (p != null)
                {
                    progress.Report(ToJobProgress(p, knownTitle));
                }
            },
            onStderr: stderrTail.Add,
            stall,
            ct).ConfigureAwait(false);
    }

    private static JobProgress ToJobProgress(ProgressSnapshot p, string? title) => new()
    {
        Percent = p.Percent,
        SpeedBps = p.SpeedBytesPerSecond,
        EtaSeconds = p.EtaSeconds,
        DownloadedBytes = p.DownloadedBytes,
        TotalBytes = p.TotalBytes,
        StatusText = p.Finished ? "Finalizing" : "Downloading",
        Title = title,
    };

    // ---- Layout (PreLaidOut) ----

    private IReadOnlyList<string> LayOut(string workDir, string stagingRoot, MediaMetadata meta)
    {
        var plan = _organizer.Plan(meta);
        var placed = new List<string>();

        var video = FindLargestVideo(workDir);
        if (video == null)
        {
            return placed;
        }

        var videoExt = Path.GetExtension(video);
        var videoDest = Path.Combine(stagingRoot, ToOsPath(plan.VideoRelativePath(videoExt)));
        MoveInto(video, videoDest);
        placed.Add(videoDest);

        foreach (var sub in FindSubtitles(workDir))
        {
            var lang = SubtitleLanguageFromFileName(Path.GetFileNameWithoutExtension(sub));
            var subDest = Path.Combine(stagingRoot, ToOsPath(plan.SubtitleRelativePath(lang, Path.GetExtension(sub))));
            MoveInto(sub, subDest);
            placed.Add(subDest);
        }

        // NFO sidecar: reuse svtplay-dl's episode NFO if present, else generate.
        var existingNfo = FirstNfo(workDir);
        var nfoDest = Path.Combine(stagingRoot, ToOsPath(plan.NfoRelativePath()));
        if (existingNfo != null)
        {
            MoveInto(existingNfo, nfoDest);
        }
        else
        {
            WriteText(nfoDest, _organizer.BuildNfo(meta));
        }

        placed.Add(nfoDest);

        // Series-level tvshow.nfo.
        if (plan.TvShowNfoRelativePath is { } tvshowRel && meta.SeriesName is { Length: > 0 } series)
        {
            var tvshowDest = Path.Combine(stagingRoot, ToOsPath(tvshowRel));
            WriteText(tvshowDest, _organizer.BuildTvShowNfo(series));
            placed.Add(tvshowDest);
        }

        // Drop the now-empty work dir so the placer only sees laid-out files.
        TryDeleteDirectory(workDir);
        return placed;
    }

    // ---- File helpers ----

    private static string? FindLargestVideo(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();

    private static IEnumerable<string> FindSubtitles(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => SubtitleExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

    private static string? FirstNfo(string dir)
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.nfo", SearchOption.AllDirectories)
                .FirstOrDefault(f => !Path.GetFileName(f).Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase))
            : null;

    internal static string SubtitleLanguageFromFileName(string stemWithoutExt)
    {
        var dot = stemWithoutExt.LastIndexOf('.');
        if (dot <= 0)
        {
            return "und";
        }

        var candidate = stemWithoutExt.Substring(dot + 1);
        if (candidate.Length is >= 2 and <= 12 && candidate.All(c => char.IsLetter(c) || c == '-'))
        {
            return candidate;
        }

        return "und";
    }

    private static void MoveInto(string source, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (File.Exists(dest))
        {
            File.Delete(dest);
        }

        File.Move(source, dest);
    }

    private static void WriteText(string dest, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, content);
    }

    private static string ToOsPath(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort; core cleans the whole staging dir on completion/failure.
        }
    }

    private static string ToolName(DownloadTool tool) => tool == DownloadTool.SvtPlayDl ? "svtplay-dl" : "yt-dlp";

    // ---- Payload ----

    private sealed record HandlerPayload(DownloadTool Tool, int? Ordinal);

    private static string SerializePayload(HandlerPayload payload) => JsonSerializer.Serialize(payload);

    private static HandlerPayload DeserializePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new HandlerPayload(DownloadTool.YtDlp, null);
        }

        try
        {
            return JsonSerializer.Deserialize<HandlerPayload>(json) ?? new HandlerPayload(DownloadTool.YtDlp, null);
        }
        catch (JsonException)
        {
            return new HandlerPayload(DownloadTool.YtDlp, null);
        }
    }

    private sealed class StderrTail
    {
        private readonly Queue<string> _lines = new();

        public void Add(string line)
        {
            _lines.Enqueue(line);
            while (_lines.Count > 8)
            {
                _lines.Dequeue();
            }
        }

        public string Text => string.Join(" | ", _lines);
    }
}
