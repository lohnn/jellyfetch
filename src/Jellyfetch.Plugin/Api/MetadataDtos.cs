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
/// A Jellyfin virtual folder (library) as JellyFetch exposes it for placement targeting. Field names
/// here ARE the wire contract (serialized PascalCase by the server) — see docs/api.md. Treat renames
/// as breaking. Sourced from <c>ILibraryManager.GetVirtualFolders()</c> → <c>VirtualFolderInfo</c>
/// (verified against pinned Jellyfin 10.11.11): a library has a nullable <c>CollectionTypeOptions</c>
/// and one-or-more root <c>Locations</c>. JellyFetch normalizes the enum to a lowercase string and
/// picks the FIRST location as the placement root.
/// </summary>
public class LibraryInfoDto
{
    /// <summary>
    /// Gets or sets the Jellyfin library id — the <c>VirtualFolderInfo.ItemId</c> (GUID string). This
    /// is the opaque token the app sends back on submit (<c>SubmitDownloadRequest.LibraryId</c>) to
    /// target this library. May be null/empty for a library Jellyfin has not assigned an item id to yet
    /// (rare); such a library cannot be explicitly targeted and the app should treat it as display-only.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the library display name (e.g. "Movies", "TV Shows", "Home Videos").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the normalized collection type, lowercase: one of Jellyfin's
    /// <c>CollectionTypeOptions</c> names — <c>"movies"</c>, <c>"tvshows"</c>, <c>"music"</c>,
    /// <c>"musicvideos"</c>, <c>"homevideos"</c>, <c>"boxsets"</c>, <c>"books"</c>, <c>"mixed"</c> — or
    /// <c>null</c> when the library has no collection type set (a plain/undeclared library). The app
    /// renders every entry but should only let the user pick placement-sensible ones; JellyFetch
    /// classifies Auto submissions into <c>movies</c>/<c>tvshows</c> libraries only.
    /// </summary>
    public string? CollectionType { get; set; }

    /// <summary>
    /// Gets or sets the absolute root path JellyFetch will place into for this library — the FIRST of
    /// the library's <c>Locations</c>. Informational for the app (it need not send this back); the
    /// server resolves the root from <see cref="Id"/> at submit time. Null when the library declares no
    /// locations.
    /// </summary>
    public string? PrimaryLocation { get; set; }

    /// <summary>
    /// Gets or sets all root folders this library spans (<c>VirtualFolderInfo.Locations</c>). A Jellyfin
    /// library can map to several folders; JellyFetch places into <see cref="PrimaryLocation"/> (the
    /// first). Exposed so the app can show "spans N folders" if it wants. May be empty.
    /// </summary>
    public IReadOnlyList<string> Locations { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets a value indicating whether JellyFetch considers this library a valid explicit
    /// placement target: it has a non-empty <see cref="Id"/> AND at least one location. When false the
    /// app should show it disabled (or omit it from the picker). Auto is always available regardless.
    /// </summary>
    public bool IsPlaceable { get; set; }
}

/// <summary>
/// The library list served by GET /Jellyfetch/Libraries — the source of truth the app's placement
/// dropdown renders. See docs/api.md.
/// </summary>
public class LibraryListDto
{
    /// <summary>Gets or sets the server's libraries, in Jellyfin's own ordering (first = primary per type).</summary>
    public IReadOnlyList<LibraryInfoDto> Libraries { get; set; } = Array.Empty<LibraryInfoDto>();
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

/// <summary>
/// The result of a type-conversion request (Movie / Series / Other). Because Jellyfin has no in-place
/// reclassification (an item's type IS its CLR subclass + folder shape), conversion is a re-ingest:
/// the item's video files are moved into the target library root with the correct layout/NFO, the old
/// mis-typed item is deleted (files kept), and a scoped rescan re-creates it as the correct type. For
/// "Other" the target is the fallback library root (Jellyfin has no literal Other item type — the
/// fallback library decides the new type). That rescan is asynchronous, so the NEW item id is not known
/// synchronously. See docs/api.md.
/// </summary>
public class ConvertTypeResultDto
{
    /// <summary>Gets or sets the id of the item that was converted (the OLD item; it is deleted after the move).</summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the type the item was converted TO: "Movie", "Series", or "Other".</summary>
    public string TargetType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a stable status string describing the outcome. Currently always
    /// <c>"RescanPending"</c> on success: the files were moved and a rescan was triggered, but the new
    /// item has not been created yet. Additive vocabulary — treat unknown values as "in progress".
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the library root the files were moved INTO (the new type's root).</summary>
    public string? NewLibraryRoot { get; set; }

    /// <summary>Gets or sets the absolute paths the video files were moved to (under the new root).</summary>
    public IReadOnlyList<string> MovedPaths { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the absolute directory the moved item now lives in (the item folder under the new
    /// root — e.g. <c>{root}/{Title (Year)}</c>). This is the most stable rebind key: after the rescan,
    /// the new Movie/Series item's own Path is (at or under) this directory. Poll
    /// <c>GET /Jellyfetch/Metadata/Items/ByPath?path={ItemDirectory}</c> (or any <c>MovedPaths</c> entry)
    /// until it returns the freshly-scanned item.
    /// </summary>
    public string? ItemDirectory { get; set; }

    /// <summary>
    /// Gets or sets a human-readable note on what to do next — specifically that the client should poll
    /// <c>GET /Jellyfetch/Metadata/Items?type={TargetType}&amp;searchTerm={name}</c> (or the job's
    /// LibraryMatch) to find the newly-created item once the rescan finishes, then optionally apply a
    /// provider-id correction to it.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the best-known title of the moved item, so the client can pre-fill the poll search
    /// that locates the new item after the rescan.
    /// </summary>
    public string? Title { get; set; }
}
