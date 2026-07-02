using System;
using System.Collections.Generic;
using Jellyfetch.Plugin.Download;

namespace Jellyfetch.Plugin.Jobs;

/// <summary>Job lifecycle states. Serialized as strings in the REST API and the job store.</summary>
public enum JobState
{
    /// <summary>Accepted, waiting for a concurrency slot.</summary>
    Queued,

    /// <summary>Handler is classifying/expanding the input (may fan out into child jobs).</summary>
    Resolving,

    /// <summary>Bytes are being transferred.</summary>
    Downloading,

    /// <summary>Download finished; files are being placed into the library.</summary>
    Processing,

    /// <summary>Terminal: media placed, library scan triggered.</summary>
    Completed,

    /// <summary>Terminal: something went wrong; see ErrorMessage. Retryable.</summary>
    Failed,

    /// <summary>Terminal: cancelled by the user. Retryable.</summary>
    Cancelled
}

/// <summary>
/// The persisted job record. Everything here round-trips through the JSON job store —
/// keep it POCO and additive-only.
/// </summary>
public class DownloadJob
{
    /// <summary>Gets or sets the job id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the parent job id, for children of an expanded submission (playlist/series).</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Gets or sets a value indicating whether this job expanded into child jobs (group parent).</summary>
    public bool IsGroup { get; set; }

    /// <summary>Gets or sets the handler kind ("webMedia" / "torrent"). Null until a handler matched.</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the state.</summary>
    public JobState State { get; set; } = JobState.Queued;

    /// <summary>Gets or sets the best-known display title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the originally submitted URL (http(s)/magnet), if any.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Gets or sets the original request (only on submission jobs, null on children).</summary>
    public DownloadRequest? Request { get; set; }

    /// <summary>Gets or sets the resolved item (set on children and on single-item submissions after resolve).</summary>
    public DownloadItem? Item { get; set; }

    /// <summary>Gets or sets completion percent 0..100, null when indeterminate.</summary>
    public double? Percent { get; set; }

    /// <summary>Gets or sets current speed in bytes/second.</summary>
    public long? SpeedBps { get; set; }

    /// <summary>Gets or sets estimated seconds remaining.</summary>
    public long? EtaSeconds { get; set; }

    /// <summary>Gets or sets bytes downloaded so far.</summary>
    public long? DownloadedBytes { get; set; }

    /// <summary>Gets or sets total expected bytes.</summary>
    public long? TotalBytes { get; set; }

    /// <summary>Gets or sets a short human status line.</summary>
    public string? StatusText { get; set; }

    /// <summary>Gets or sets the error message when State is Failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets the final library paths after successful placement.</summary>
    public List<string> FinalPaths { get; set; } = new();

    /// <summary>Gets or sets the creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the last-update timestamp (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the completion timestamp (UTC), set on any terminal state.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Gets a value indicating whether the job is in a terminal state.</summary>
    public bool IsTerminal => State is JobState.Completed or JobState.Failed or JobState.Cancelled;
}
