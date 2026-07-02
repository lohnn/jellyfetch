using System;
using System.Collections.Generic;

namespace Jellyfetch.Plugin.Download;

/// <summary>
/// CONTRACT models shared between the core job queue and download backends.
/// All types here are persisted as JSON — keep them POCO and backwards-compatible.
/// </summary>
public enum MediaCategory
{
    /// <summary>Not yet classified / let the backend decide.</summary>
    Auto,

    /// <summary>Episodic content — placed under the series library root.</summary>
    Series,

    /// <summary>A movie — placed under the movie library root.</summary>
    Movie,

    /// <summary>Unclassifiable content (e.g. one-off YouTube video) — placed under the fallback root.</summary>
    Other
}

/// <summary>A user submission as it enters the system. Exactly one of SourceUrl / TorrentFileBase64 is set.</summary>
public class DownloadRequest
{
    /// <summary>Gets or sets the submitted URL: http(s) or magnet: URI. Null for .torrent file uploads.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Gets or sets the raw .torrent file content, base64-encoded. Null for URL submissions.</summary>
    public string? TorrentFileBase64 { get; set; }

    /// <summary>Gets or sets the user's category hint. Backends may override during resolve.</summary>
    public MediaCategory CategoryHint { get; set; } = MediaCategory.Auto;
}

/// <summary>Result of <see cref="IDownloadHandler.ResolveAsync"/>.</summary>
public class ResolveResult
{
    /// <summary>Gets or sets the resolved downloadable items. Must contain at least one.</summary>
    public IReadOnlyList<DownloadItem> Items { get; set; } = Array.Empty<DownloadItem>();

    /// <summary>Gets or sets the group title (series/playlist name) when Items.Count &gt; 1.</summary>
    public string? GroupTitle { get; set; }
}

/// <summary>
/// One concrete downloadable unit (a single video / episode / torrent).
/// Persisted verbatim with the job; must survive JSON round-trips.
/// </summary>
public class DownloadItem
{
    /// <summary>Gets or sets the best-known human title at resolve time.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the concrete URL for this item (episode page URL, magnet URI), if any.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Gets or sets the raw .torrent content, base64-encoded, if this item came from a file upload.</summary>
    public string? TorrentFileBase64 { get; set; }

    /// <summary>Gets or sets the category as classified during resolve.</summary>
    public MediaCategory Category { get; set; } = MediaCategory.Auto;

    /// <summary>
    /// Gets or sets an opaque, handler-owned payload (serialized JSON string) carrying whatever the
    /// handler needs to execute this item later (format selection, program id, ...). The core never
    /// inspects it.
    /// </summary>
    public string? HandlerPayload { get; set; }
}

/// <summary>Progress report streamed from a handler during <see cref="IDownloadHandler.ExecuteAsync"/>.</summary>
public class JobProgress
{
    /// <summary>Gets or sets completion percent 0..100, or null when indeterminate.</summary>
    public double? Percent { get; set; }

    /// <summary>Gets or sets the current transfer speed in bytes/second, or null if unknown.</summary>
    public long? SpeedBps { get; set; }

    /// <summary>Gets or sets the estimated seconds remaining, or null if unknown.</summary>
    public long? EtaSeconds { get; set; }

    /// <summary>Gets or sets bytes downloaded so far, or null if unknown.</summary>
    public long? DownloadedBytes { get; set; }

    /// <summary>Gets or sets total expected bytes, or null if unknown.</summary>
    public long? TotalBytes { get; set; }

    /// <summary>Gets or sets a short human status line (e.g. "fetching metadata", "muxing").</summary>
    public string? StatusText { get; set; }

    /// <summary>Gets or sets an improved title, if the handler learned one mid-download.</summary>
    public string? Title { get; set; }
}

/// <summary>Best-known metadata for produced media, used by library placement.</summary>
public class MediaMetadata
{
    /// <summary>Gets or sets the display title (episode title or movie title).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the final category. Should not be Auto by completion time; Auto is treated as Other.</summary>
    public MediaCategory Category { get; set; } = MediaCategory.Other;

    /// <summary>Gets or sets the series name, when Category is Series.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the season number, when known.</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode number, when known.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the release year, when known.</summary>
    public int? Year { get; set; }
}

/// <summary>Completion result of <see cref="IDownloadHandler.ExecuteAsync"/>.</summary>
public class DownloadResult
{
    /// <summary>Gets or sets the absolute paths of all produced files (inside the staging directory).</summary>
    public IReadOnlyList<string> Files { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the best-known metadata for placement and display.</summary>
    public MediaMetadata Metadata { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether files are already laid out relative to staging in their
    /// final library-relative structure (handler did its own naming). When true the placer moves the
    /// tree verbatim under the category root instead of applying its own naming scheme.
    /// </summary>
    public bool PreLaidOut { get; set; }
}
