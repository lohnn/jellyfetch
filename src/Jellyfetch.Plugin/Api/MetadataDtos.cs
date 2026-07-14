using System;
using System.Collections.Generic;

namespace Jellyfetch.Plugin.Api;

/// <summary>
/// A Jellyfin library item as served by the metadata-correction endpoints. Field names here ARE the
/// wire contract (serialized PascalCase by the server) — see docs/api.md. Treat renames as breaking.
/// </summary>
public class LibraryItemDto
{
    /// <summary>Gets or sets the Jellyfin library item id (GUID, "N" format — 32 hex, no dashes).</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the item name / title as Jellyfin currently has it matched.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the production year, when known. Null otherwise.</summary>
    public int? ProductionYear { get; set; }

    /// <summary>Gets or sets the item type: "Movie", "Series", or "Episode". Stable PascalCase string.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider-id map Jellyfin currently has for this item, e.g.
    /// { "Tmdb": "603", "Imdb": "tt0133093" }. Keys are provider names, values are ids. May be empty.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets or sets a value indicating whether the item currently has a primary (poster) image.
    /// When true, fetch it from the standard Jellyfin route in <see cref="PosterUrl"/>.
    /// </summary>
    public bool HasPrimaryImage { get; set; }

    /// <summary>
    /// Gets or sets the relative Jellyfin image route for the primary/poster image, e.g.
    /// "/Items/{ItemId}/Images/Primary?tag={tag}". Null when the item has no primary image.
    /// Fetch it against the same Jellyfin server with the caller's token — it is a standard
    /// Jellyfin image endpoint, not a JellyFetch one. The <c>tag</c> query param is a cache key.
    /// </summary>
    public string? PosterUrl { get; set; }

    /// <summary>Gets or sets the primary image cache tag, when present. Cache-busts <see cref="PosterUrl"/>.</summary>
    public string? PosterTag { get; set; }
}

/// <summary>
/// The result of resolving a completed JellyFetch job to its Jellyfin library item.
/// See docs/api.md. Additive/optional fields — clients must tolerate <see cref="Item"/> being null.
/// </summary>
public class JobLibraryMatchDto
{
    /// <summary>Gets or sets the JellyFetch job id this resolution is for.</summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a library item was found for this job. False when the
    /// job is not terminal, has no final paths, or the files are not (yet) scanned into the library.
    /// </summary>
    public bool Matched { get; set; }

    /// <summary>
    /// Gets or sets the matched library item and its current metadata. Null when
    /// <see cref="Matched"/> is false — clients render an "unmatched / not scanned yet" state.
    /// </summary>
    public LibraryItemDto? Item { get; set; }
}

/// <summary>
/// A paged slice of library items. See docs/api.md.
/// </summary>
public class LibraryItemsPageDto
{
    /// <summary>Gets or sets the items in this page.</summary>
    public IReadOnlyList<LibraryItemDto> Items { get; set; } = Array.Empty<LibraryItemDto>();

    /// <summary>Gets or sets the total number of items matching the query (across all pages), for paging.</summary>
    public int TotalRecordCount { get; set; }

    /// <summary>Gets or sets the start index this page was served from (echoes the request).</summary>
    public int StartIndex { get; set; }
}

/// <summary>
/// A single remote-search candidate proxied from Jellyfin's native provider search (TMDb/TVDb/…).
/// See docs/api.md.
/// </summary>
public class RemoteSearchCandidateDto
{
    /// <summary>Gets or sets the candidate title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the candidate production year, when known.</summary>
    public int? ProductionYear { get; set; }

    /// <summary>Gets or sets the candidate overview / synopsis, when provided.</summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the provider-id map for this candidate, e.g. { "Tmdb": "603" }. This is the payload
    /// the client echoes back to the Apply endpoint to select this candidate.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();

    /// <summary>Gets or sets a poster/thumbnail image URL for the candidate, when the provider supplies one. Absolute external URL.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Gets or sets the name of the provider that produced this candidate (e.g. "TheMovieDb"). Informational.</summary>
    public string? SearchProviderName { get; set; }
}
