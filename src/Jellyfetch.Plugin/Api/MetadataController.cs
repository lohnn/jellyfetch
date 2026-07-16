using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Jobs;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfetch.Plugin.Api;

/// <summary>Request body for POST /Jellyfetch/Metadata/Search.</summary>
public class RemoteSearchRequest
{
    /// <summary>Gets or sets the free-text title to search for. Arbitrary — not tied to the item's current match.</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the search kind: "Movie" or "Series" (case-insensitive). Required.</summary>
    [Required]
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional production year to disambiguate.</summary>
    public int? Year { get; set; }
}

/// <summary>Request body for POST /Jellyfetch/Metadata/Items/{itemId}/Apply.</summary>
public class ApplyCorrectionRequest
{
    /// <summary>
    /// Gets or sets the explicit provider ids to apply, e.g. { "Tmdb": "603" }. Either this or
    /// <see cref="Candidate"/> (with its own ProviderIds) must be present. When both are set,
    /// <see cref="ProviderIds"/> wins for the ids and the candidate supplies name/year/image context.
    /// </summary>
    public Dictionary<string, string>? ProviderIds { get; set; }

    /// <summary>
    /// Gets or sets the full remote-search candidate the user tapped (echo the object returned by the
    /// Search endpoint). Its ProviderIds are used when <see cref="ProviderIds"/> is absent.
    /// </summary>
    public RemoteSearchCandidateDto? Candidate { get; set; }
}

/// <summary>Request body for POST /Jellyfetch/Metadata/Items/{itemId}/ConvertType.</summary>
public class ConvertTypeRequest
{
    /// <summary>
    /// Gets or sets the target type to convert the item to: "Movie", "Series", or "Other"
    /// (case-insensitive). Required. "Other" relocates the item into the fallback library root
    /// (Jellyfin has no literal Other item type — the fallback library decides its new type).
    /// </summary>
    [Required]
    public string TargetType { get; set; } = string.Empty;
}

/// <summary>Request body for POST /Jellyfetch/Metadata/Items/{itemId}/ChangeLibrary.</summary>
public class ChangeLibraryRequest
{
    /// <summary>
    /// Gets or sets the destination library id (the <c>Id</c> from <c>GET /Jellyfetch/Libraries</c> — a
    /// Jellyfin <c>VirtualFolderInfo.ItemId</c>). Required. The item's files are moved into that library's
    /// primary folder and re-ingested. Supports moving between two libraries of the same collection type.
    /// </summary>
    [Required]
    public string LibraryId { get; set; } = string.Empty;
}

/// <summary>
/// JellyFetch metadata-correction REST API. Wire contract documented in docs/api.md — keep in sync.
/// Auth: Jellyfin-native, requires an elevated (admin) token / API key — same scheme as DownloadsController.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Jellyfetch/Metadata")]
[Produces(MediaTypeNames.Application.Json)]
public class MetadataController : ControllerBase
{
    private readonly DownloadJobManager _manager;
    private readonly LibraryMetadataService _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataController"/> class.
    /// </summary>
    /// <param name="manager">The job manager (to resolve jobs).</param>
    /// <param name="library">The library/metadata bridge service.</param>
    public MetadataController(DownloadJobManager manager, LibraryMetadataService library)
    {
        _manager = manager;
        _library = library;
    }

    /// <summary>
    /// Resolves a completed job to its current Jellyfin library item + metadata ("what Jellyfin thinks
    /// this is"). Keyed by JellyFetch job id.
    /// </summary>
    /// <param name="jobId">The JellyFetch job id.</param>
    /// <returns>200 with the match (Matched=false when unscanned/removed); 404 when the job is unknown.</returns>
    [HttpGet("Jobs/{jobId}/LibraryMatch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<JobLibraryMatchDto> GetJobLibraryMatch([FromRoute] Guid jobId)
    {
        var job = _manager.GetJob(jobId);
        if (job is null)
        {
            return NotFound();
        }

        return Ok(_library.ResolveJob(job));
    }

    /// <summary>
    /// Lists the server's Jellyfin libraries (virtual folders) for the app's placement dropdown. Each
    /// entry carries its id (the token to send back on submit), display name, normalized collection type
    /// ("movies"/"tvshows"/…/null), and root location(s). Returned in Jellyfin's own ordering, so the
    /// first movies/tvshows library is the deterministic Auto target for that type.
    /// </summary>
    /// <returns>200 with the library list.</returns>
    [HttpGet("/Jellyfetch/Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LibraryListDto> ListLibraries() => Ok(_library.ListLibraries());

    /// <summary>Lists library Movies and/or Series, paged and searchable, for the browse-all correction page.</summary>
    /// <param name="type">Optional type filter: "Movie" or "Series". Omit for both.</param>
    /// <param name="searchTerm">Optional case-insensitive name search.</param>
    /// <param name="startIndex">Page start offset (default 0).</param>
    /// <param name="limit">Page size (default 50, clamped 1..200).</param>
    /// <returns>200 with the page; 400 on an unknown type.</returns>
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<LibraryItemsPageDto> ListItems(
        [FromQuery] string? type = null,
        [FromQuery] string? searchTerm = null,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50)
    {
        var kind = ParseKind(type, allowNull: true, out var kindError);
        if (kindError is not null)
        {
            return BadRequest(new { Error = kindError });
        }

        return Ok(_library.ListItems(kind, searchTerm, startIndex, limit));
    }

    /// <summary>
    /// Resolves the CURRENT library item at an absolute path — the deterministic post-conversion rebind.
    /// After a ConvertType moves files to a known destination, the client polls this with a
    /// <c>MovedPaths</c> entry (or the <c>ItemDirectory</c>) until it returns 200, robust where a
    /// title/type search drifts after the rescan re-fetches metadata.
    /// </summary>
    /// <param name="path">The absolute file-system path the item's files live at.</param>
    /// <returns>200 with the item; 400 on a missing path; 404 when nothing is indexed there yet (keep polling).</returns>
    [HttpGet("Items/ByPath")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<LibraryItemDto> GetItemByPath([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new { Error = "The 'path' query parameter is required." });
        }

        var item = _library.GetItemByPath(path);
        return item is null ? NotFound() : Ok(item);
    }

    /// <summary>Gets a single library item's current metadata by id.</summary>
    /// <param name="itemId">The library item id.</param>
    /// <returns>200 with the item; 404 when unknown.</returns>
    [HttpGet("Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<LibraryItemDto> GetItem([FromRoute] Guid itemId)
    {
        var item = _library.GetItem(itemId);
        return item is null ? NotFound() : Ok(item);
    }

    /// <summary>
    /// Remote-searches external metadata providers (TMDb/TVDb) for a free-text title, returning
    /// candidate matches for the correction picker. The Name is arbitrary — searching a completely
    /// unrelated title works; the item id only supplies provider context.
    /// </summary>
    /// <param name="itemId">The item being corrected (validated to exist).</param>
    /// <param name="request">The search request (Name, Type, optional Year).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with candidates; 400 on a bad type/empty name; 404 when the item is unknown.</returns>
    [HttpPost("Items/{itemId}/Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RemoteSearchCandidateDto>>> Search(
        [FromRoute] Guid itemId,
        [FromBody] RemoteSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { Error = "Name is required." });
        }

        var kind = ParseKind(request.Type, allowNull: false, out var kindError);
        if (kindError is not null)
        {
            return BadRequest(new { Error = kindError });
        }

        var candidates = await _library
            .RemoteSearchAsync(itemId, kind!.Value, request.Name.Trim(), request.Year, cancellationToken)
            .ConfigureAwait(false);

        return candidates is null ? NotFound() : Ok(candidates);
    }

    /// <summary>
    /// Applies a correction to a library item and awaits a full metadata + image refresh. Two modes,
    /// one endpoint: send a full <c>Candidate</c> (native pick) and/or explicit <c>ProviderIds</c>
    /// (browser-paste fallback). The refresh is awaited, so the response carries the corrected metadata.
    /// </summary>
    /// <param name="itemId">The library item to correct.</param>
    /// <param name="request">The correction (Candidate and/or ProviderIds).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>200 with the refreshed item; 400 when no provider ids resolvable; 404 when the item is unknown.</returns>
    [HttpPost("Items/{itemId}/Apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LibraryItemDto>> Apply(
        [FromRoute] Guid itemId,
        [FromBody] ApplyCorrectionRequest request,
        CancellationToken cancellationToken)
    {
        var providerIds = request.ProviderIds;
        RemoteSearchResult? candidate = null;

        if (request.Candidate is not null)
        {
            candidate = LibraryMetadataService.ToRemoteSearchResult(request.Candidate);
            if (providerIds is null or { Count: 0 } && candidate is not null)
            {
                providerIds = new Dictionary<string, string>(candidate.ProviderIds, StringComparer.OrdinalIgnoreCase);
            }
        }

        if (providerIds is null || providerIds.Count == 0)
        {
            return BadRequest(new { Error = "Provide ProviderIds (e.g. { \"Tmdb\": \"603\" }) or a Candidate carrying provider ids." });
        }

        var refreshed = await _library
            .ApplyAsync(itemId, providerIds, candidate, cancellationToken)
            .ConfigureAwait(false);

        return refreshed is null ? NotFound() : Ok(refreshed);
    }

    /// <summary>
    /// Converts a library item's type. Jellyfin has no in-place type change, so this re-ingests: the
    /// item's video files are moved into the target library root with the correct layout/NFO, the old
    /// mis-typed item is deleted (files kept), and a scoped rescan re-creates it as the correct type.
    /// The rescan is asynchronous — the response is 202 "RescanPending" and the client polls the Items
    /// list to find the new item once the scan finishes. TargetType accepts "Movie", "Series", or
    /// "Other" ("Other" relocates to the fallback library root — Jellyfin has no literal Other type).
    /// </summary>
    /// <param name="itemId">The library item to convert (must currently be a Movie or Series).</param>
    /// <param name="request">The conversion request (TargetType: "Movie" | "Series" | "Other").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>202 with the rescan-pending result; 400 on a rejected conversion; 403 on a permission problem; 404 when the item is unknown; 409 when the item is stale/already-moved.</returns>
    [HttpPost("Items/{itemId}/ConvertType")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ConvertTypeResultDto>> ConvertType(
        [FromRoute] Guid itemId,
        [FromBody] ConvertTypeRequest request,
        CancellationToken cancellationToken)
    {
        var targetCategory = ParseConvertTarget(request.TargetType, out var kindError);
        if (kindError is not null)
        {
            return BadRequest(new { Error = kindError });
        }

        var result = await _library
            .ConvertTypeAsync(itemId, targetCategory!.Value, cancellationToken)
            .ConfigureAwait(false);

        return MapConvertResult(result);
    }

    /// <summary>
    /// Moves a completed library item into a DIFFERENT Jellyfin library, chosen by explicit id — the
    /// "change destination library" operation. Same re-ingest as ConvertType (move files → delete old DB
    /// item → async rescan), but the target is an explicit library id's root and the item KEEPS its kind,
    /// so it supports moving between two libraries of the SAME collection type (e.g. Movies A → Movies B).
    /// The rescan is asynchronous — the response is 202 "RescanPending"; poll <c>Items/ByPath</c> with the
    /// returned <c>ItemDirectory</c> (or a <c>MovedPaths</c> entry) to rebind, never the deleted id.
    /// </summary>
    /// <param name="itemId">The library item to move (must currently be a Movie or Series).</param>
    /// <param name="request">The move request (LibraryId: the destination library id).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>202 with the rescan-pending result; 400 on a rejected move; 403 on a permission problem; 404 when the item is unknown; 409 when the item is stale/already-moved.</returns>
    [HttpPost("Items/{itemId}/ChangeLibrary")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ConvertTypeResultDto>> ChangeLibrary(
        [FromRoute] Guid itemId,
        [FromBody] ChangeLibraryRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.LibraryId))
        {
            return BadRequest(new { Error = "LibraryId is required (the Id from GET /Jellyfetch/Libraries)." });
        }

        var result = await _library
            .ChangeLibraryAsync(itemId, request.LibraryId.Trim(), cancellationToken)
            .ConfigureAwait(false);

        return MapConvertResult(result);
    }

    /// <summary>Maps a re-ingest outcome (shared by ConvertType and ChangeLibrary) to the HTTP result.</summary>
    /// <param name="result">The re-ingest result.</param>
    /// <returns>The mapped action result.</returns>
    private ActionResult<ConvertTypeResultDto> MapConvertResult(ConvertTypeResult result) =>
        result.Outcome switch
        {
            ConvertTypeResult.ConvertTypeOutcome.NotFoundOutcome => NotFound(),
            ConvertTypeResult.ConvertTypeOutcome.RejectedOutcome => BadRequest(new { Error = result.Error }),
            ConvertTypeResult.ConvertTypeOutcome.PermissionDeniedOutcome =>
                StatusCode(StatusCodes.Status403Forbidden, new { Error = result.Error }),
            ConvertTypeResult.ConvertTypeOutcome.SupersededOutcome =>
                Conflict(new { Error = result.Error }),
            _ => Accepted(result.Dto),
        };

    /// <summary>
    /// Parses a type string into a <see cref="BaseItemKind"/>. When <paramref name="allowNull"/> is true,
    /// an empty/absent value returns null with no error (both-types filter). Only Movie/Series accepted.
    /// </summary>
    private static BaseItemKind? ParseKind(string? type, bool allowNull, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(type))
        {
            if (allowNull)
            {
                return null;
            }

            error = "Type is required (Movie or Series).";
            return null;
        }

        if (string.Equals(type, "Movie", StringComparison.OrdinalIgnoreCase))
        {
            return BaseItemKind.Movie;
        }

        if (string.Equals(type, "Series", StringComparison.OrdinalIgnoreCase))
        {
            return BaseItemKind.Series;
        }

        error = $"Unknown type '{type}'. Use Movie or Series.";
        return null;
    }

    /// <summary>
    /// Parses a ConvertType target string into a <see cref="MediaCategory"/>. Unlike <see cref="ParseKind"/>
    /// (which lists/searches real Jellyfin item kinds), conversion also accepts "Other" — a placement
    /// category (fallback root), not a Jellyfin item type.
    /// </summary>
    private static MediaCategory? ParseConvertTarget(string? type, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(type))
        {
            error = "TargetType is required (Movie, Series or Other).";
            return null;
        }

        if (string.Equals(type, "Movie", StringComparison.OrdinalIgnoreCase))
        {
            return MediaCategory.Movie;
        }

        if (string.Equals(type, "Series", StringComparison.OrdinalIgnoreCase))
        {
            return MediaCategory.Series;
        }

        if (string.Equals(type, "Other", StringComparison.OrdinalIgnoreCase))
        {
            return MediaCategory.Other;
        }

        error = $"Unknown TargetType '{type}'. Use Movie, Series or Other.";
        return null;
    }
}
