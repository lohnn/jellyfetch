using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfetch.Plugin.Jobs;

namespace Jellyfetch.Plugin.Api;

/// <summary>
/// The job object as served by the REST API. Field names here ARE the wire contract
/// (serialized PascalCase by the Jellyfin server) — see docs/api.md. Treat renames as breaking.
/// </summary>
public class JobDto
{
    /// <summary>Gets or sets the job id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the parent job id for children of an expanded playlist/series submission.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Gets or sets a value indicating whether this job is a group parent.</summary>
    public bool IsGroup { get; set; }

    /// <summary>Gets or sets the backend kind: "webMedia" or "torrent".</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the state: Queued|Resolving|Downloading|Processing|Completed|Failed|Cancelled.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets the best-known display title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the originally submitted URL, if any.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Gets or sets completion percent 0..100, null when indeterminate.</summary>
    public double? Percent { get; set; }

    /// <summary>Gets or sets the current speed in bytes/second.</summary>
    public long? SpeedBps { get; set; }

    /// <summary>Gets or sets the estimated seconds remaining.</summary>
    public long? EtaSeconds { get; set; }

    /// <summary>Gets or sets bytes downloaded so far.</summary>
    public long? DownloadedBytes { get; set; }

    /// <summary>Gets or sets total expected bytes.</summary>
    public long? TotalBytes { get; set; }

    /// <summary>Gets or sets a short human status line.</summary>
    public string? StatusText { get; set; }

    /// <summary>Gets or sets the error message when State is Failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets the final library file paths (set once Completed).</summary>
    public IReadOnlyList<string> FinalPaths { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the resolved media category as a stable string: "Movie", "Series", or "Other".
    /// Null when unknown/unclassified (old jobs, torrents, still-in-flight, or the internal "Auto"
    /// placeholder). Additive and optional — clients must tolerate null/absent and may fall back to
    /// inferring from SeriesName/EpisodeNumber. See docs/api.md.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the series name, for episode jobs. Null when not a known episode.</summary>
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets the season number, when known. NOTE: for SVT this intentionally carries the YEAR
    /// (e.g. 2024) — that's how SVT dates its shows; clients should render it verbatim.
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode number, when known.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the episode title, when known (e.g. "Avsnitt 2").</summary>
    public string? EpisodeTitle { get; set; }

    /// <summary>Gets or sets the creation timestamp (UTC, ISO 8601).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the last-update timestamp (UTC, ISO 8601).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the terminal-state timestamp (UTC, ISO 8601).</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Gets or sets the number of child jobs (group parents only).</summary>
    public int ChildCount { get; set; }

    /// <summary>Gets or sets the child jobs. Populated only by the job-detail endpoint.</summary>
    public IReadOnlyList<JobDto>? Children { get; set; }

    /// <summary>Maps a persisted job to its API representation.</summary>
    /// <param name="job">The job.</param>
    /// <param name="childCount">Number of children (group parents).</param>
    /// <param name="children">Child DTOs for the detail endpoint, or null.</param>
    /// <returns>The DTO.</returns>
    public static JobDto FromJob(DownloadJob job, int childCount = 0, IEnumerable<JobDto>? children = null) => new()
    {
        Id = job.Id,
        ParentId = job.ParentId,
        IsGroup = job.IsGroup,
        Kind = job.Kind,
        State = job.State.ToString(),
        Title = job.Title,
        SourceUrl = job.SourceUrl,
        Percent = job.Percent,
        SpeedBps = job.SpeedBps,
        EtaSeconds = job.EtaSeconds,
        DownloadedBytes = job.DownloadedBytes,
        TotalBytes = job.TotalBytes,
        StatusText = job.StatusText,
        ErrorMessage = job.ErrorMessage,
        FinalPaths = job.FinalPaths,
        Category = job.Category?.ToString(),
        SeriesName = job.SeriesName,
        SeasonNumber = job.SeasonNumber,
        EpisodeNumber = job.EpisodeNumber,
        EpisodeTitle = job.EpisodeTitle,
        CreatedAt = job.CreatedAt,
        UpdatedAt = job.UpdatedAt,
        CompletedAt = job.CompletedAt,
        ChildCount = childCount,
        Children = children?.ToList(),
    };
}
