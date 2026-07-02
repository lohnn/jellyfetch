using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfetch.Plugin.Api;

/// <summary>Request body for POST /Jellyfetch/Downloads.</summary>
public class SubmitDownloadRequest
{
    /// <summary>Gets or sets the http(s) URL or magnet: URI to download.</summary>
    [Required]
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional category hint: Auto|Series|Movie|Other (case-insensitive). Default Auto.</summary>
    public string? Category { get; set; }
}

/// <summary>
/// JellyFetch REST API. The wire contract is documented in docs/api.md — keep them in sync.
/// Auth: Jellyfin-native. Requires an elevated (admin) token / API key.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Jellyfetch")]
[Produces(MediaTypeNames.Application.Json)]
public class DownloadsController : ControllerBase
{
    private const long MaxTorrentFileBytes = 10 * 1024 * 1024;

    private readonly DownloadJobManager _manager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadsController"/> class.
    /// </summary>
    /// <param name="manager">The job manager.</param>
    public DownloadsController(DownloadJobManager manager)
    {
        _manager = manager;
    }

    /// <summary>Cheap authenticated ping for "test connection" in clients.</summary>
    /// <returns>Plugin name and version.</returns>
    [HttpGet("Ping")]
    public ActionResult<object> Ping() => Ok(new
    {
        Name = "JellyFetch",
        Version = Plugin.Instance?.Version?.ToString() ?? "unknown",
    });

    /// <summary>Submits an http(s) URL or magnet: URI for download.</summary>
    /// <param name="request">The submission.</param>
    /// <returns>201 with the created job.</returns>
    [HttpPost("Downloads")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<JobDto> Submit([FromBody] SubmitDownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest(new { Error = "Url is required." });
        }

        var category = ParseCategory(request.Category);
        if (category is null)
        {
            return BadRequest(new { Error = $"Unknown category '{request.Category}'. Use Auto, Series, Movie or Other." });
        }

        try
        {
            var job = _manager.Submit(new DownloadRequest
            {
                SourceUrl = request.Url.Trim(),
                CategoryHint = category.Value,
            });
            return Created($"/Jellyfetch/Downloads/{job.Id}", JobDto.FromJob(job));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Submits a .torrent file. Send the raw file bytes as the request body with
    /// Content-Type: application/x-bittorrent (no multipart).
    /// </summary>
    /// <param name="category">Optional category hint: Auto|Series|Movie|Other.</param>
    /// <returns>201 with the created job.</returns>
    [HttpPost("Downloads/Torrent")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<JobDto>> SubmitTorrent([FromQuery] string? category = null)
    {
        var parsedCategory = ParseCategory(category);
        if (parsedCategory is null)
        {
            return BadRequest(new { Error = $"Unknown category '{category}'. Use Auto, Series, Movie or Other." });
        }

        if (Request.ContentLength is > MaxTorrentFileBytes)
        {
            return BadRequest(new { Error = ".torrent file too large (max 10 MiB)." });
        }

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await Request.Body.CopyToAsync(ms, HttpContext.RequestAborted).ConfigureAwait(false);
            bytes = ms.ToArray();
        }

        if (bytes.Length == 0)
        {
            return BadRequest(new { Error = "Empty request body; send the raw .torrent file bytes." });
        }

        if (bytes.Length > MaxTorrentFileBytes)
        {
            return BadRequest(new { Error = ".torrent file too large (max 10 MiB)." });
        }

        try
        {
            var job = _manager.Submit(new DownloadRequest
            {
                TorrentFileBase64 = Convert.ToBase64String(bytes),
                CategoryHint = parsedCategory.Value,
            });
            return Created($"/Jellyfetch/Downloads/{job.Id}", JobDto.FromJob(job));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    /// <summary>Lists jobs, newest first.</summary>
    /// <param name="state">Optional state filter (case-insensitive), e.g. "Downloading".</param>
    /// <param name="includeChildren">When false (default) children of group jobs are omitted from the flat list.</param>
    /// <returns>The jobs.</returns>
    [HttpGet("Downloads")]
    public ActionResult<IReadOnlyList<JobDto>> List([FromQuery] string? state = null, [FromQuery] bool includeChildren = false)
    {
        JobState? stateFilter = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!Enum.TryParse<JobState>(state, ignoreCase: true, out var parsed))
            {
                return BadRequest(new { Error = $"Unknown state '{state}'." });
            }

            stateFilter = parsed;
        }

        var jobs = _manager.GetJobs()
            .Where(j => includeChildren || j.ParentId is null)
            .Where(j => stateFilter is null || j.State == stateFilter)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => JobDto.FromJob(j, _manager.GetChildren(j.Id).Count))
            .ToList();

        return Ok(jobs);
    }

    /// <summary>Gets a single job; group parents include their children.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>The job, or 404.</returns>
    [HttpGet("Downloads/{id}")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<JobDto> Detail([FromRoute] Guid id)
    {
        var job = _manager.GetJob(id);
        if (job is null)
        {
            return NotFound();
        }

        var children = _manager.GetChildren(id);
        return Ok(JobDto.FromJob(job, children.Count, children.Select(c => JobDto.FromJob(c))));
    }

    /// <summary>Cancels a job (cascades to children for group parents).</summary>
    /// <param name="id">Job id.</param>
    /// <returns>204, 404, or 409 when already terminal.</returns>
    [HttpPost("Downloads/{id}/Cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult CancelJob([FromRoute] Guid id)
    {
        var job = _manager.GetJob(id);
        if (job is null)
        {
            return NotFound();
        }

        return _manager.Cancel(id) ? NoContent() : Conflict(new { Error = "Job is already in a terminal state." });
    }

    /// <summary>Retries a Failed or Cancelled job (group parents retry all failed/cancelled children).</summary>
    /// <param name="id">Job id.</param>
    /// <returns>200 with the updated job, 404, or 409 when not retryable.</returns>
    [HttpPost("Downloads/{id}/Retry")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<JobDto> RetryJob([FromRoute] Guid id)
    {
        var job = _manager.GetJob(id);
        if (job is null)
        {
            return NotFound();
        }

        if (!_manager.Retry(id))
        {
            return Conflict(new { Error = "Only Failed or Cancelled jobs can be retried." });
        }

        return Ok(JobDto.FromJob(_manager.GetJob(id)!, _manager.GetChildren(id).Count));
    }

    /// <summary>Deletes a terminal job from history (children included). Never deletes downloaded media files.</summary>
    /// <param name="id">Job id.</param>
    /// <returns>204, 404, or 409 when the job is still active.</returns>
    [HttpDelete("Downloads/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult DeleteJob([FromRoute] Guid id)
    {
        var job = _manager.GetJob(id);
        if (job is null)
        {
            return NotFound();
        }

        return _manager.Delete(id) ? NoContent() : Conflict(new { Error = "Only finished jobs can be deleted. Cancel it first." });
    }

    private static MediaCategory? ParseCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return MediaCategory.Auto;
        }

        return Enum.TryParse<MediaCategory>(category, ignoreCase: true, out var parsed) ? parsed : null;
    }
}
