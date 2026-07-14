using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Jobs;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfetch.Plugin.Api;

/// <summary>
/// Bridges JellyFetch to Jellyfin's library + metadata internals for the metadata-correction feature:
/// resolves a completed job to its library item, lists library movies/series, proxies Jellyfin's
/// native remote (provider) search, and applies a chosen match by triggering a full metadata refresh.
/// All Jellyfin-facing types (ILibraryManager, IProviderManager) are the real 10.11 typed surface —
/// verified against the pinned NuGet assemblies and the v10.11.11 ItemLookupController source.
/// </summary>
public sealed class LibraryMetadataService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<LibraryMetadataService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryMetadataService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="providerManager">Jellyfin provider (metadata) manager.</param>
    /// <param name="fileSystem">Jellyfin file-system abstraction (needed by MetadataRefreshOptions).</param>
    /// <param name="logger">Logger.</param>
    public LibraryMetadataService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<LibraryMetadataService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a completed job to its Jellyfin library item by matching its final paths, then its
    /// parent Movie/Series (files land as Episodes/Videos, but the user corrects the show/movie).
    /// </summary>
    /// <param name="job">The (ideally completed) job.</param>
    /// <returns>The library match; <see cref="JobLibraryMatchDto.Matched"/> false when nothing is found.</returns>
    public JobLibraryMatchDto ResolveJob(DownloadJob job)
    {
        var result = new JobLibraryMatchDto { JobId = job.Id, Matched = false };

        foreach (var path in job.FinalPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var item = _libraryManager.FindByPath(path, isFolder: false)
                ?? _libraryManager.FindByPath(path, isFolder: null);
            if (item is null)
            {
                continue;
            }

            // The physical file usually resolves to an Episode/Video; surface the correctable parent
            // (Series/Movie) when there is one, since that's what carries the provider ids the user fixes.
            var correctable = PromoteToCorrectable(item);
            result.Item = MapItem(correctable);
            result.Matched = true;
            return result;
        }

        return result;
    }

    /// <summary>
    /// Lists library Movies and/or Series, filtered and paged. Never loads the library unbounded.
    /// </summary>
    /// <param name="type">Optional single type filter: <see cref="BaseItemKind.Movie"/> or <see cref="BaseItemKind.Series"/>. Null = both.</param>
    /// <param name="searchTerm">Optional name search (case-insensitive contains).</param>
    /// <param name="startIndex">Page start offset (>= 0).</param>
    /// <param name="limit">Page size (clamped 1..200).</param>
    /// <returns>The page of items with a total count for paging.</returns>
    public LibraryItemsPageDto ListItems(BaseItemKind? type, string? searchTerm, int startIndex, int limit)
    {
        var kinds = type is { } t
            ? new[] { t }
            : new[] { BaseItemKind.Movie, BaseItemKind.Series };

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = kinds,
            Recursive = true,
            IsVirtualItem = false,
            StartIndex = Math.Max(0, startIndex),
            Limit = Math.Clamp(limit, 1, 200),
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
        };

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query.SearchTerm = searchTerm.Trim();
        }

        var result = _libraryManager.GetItemsResult(query);
        return new LibraryItemsPageDto
        {
            Items = result.Items.Select(MapItem).ToList(),
            TotalRecordCount = result.TotalRecordCount,
            StartIndex = query.StartIndex ?? 0,
        };
    }

    /// <summary>
    /// Gets a single library item's current metadata by id, or null when unknown.
    /// </summary>
    /// <param name="itemId">The library item id.</param>
    /// <returns>The item DTO or null.</returns>
    public LibraryItemDto? GetItem(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        return item is null ? null : MapItem(item);
    }

    /// <summary>
    /// Proxies Jellyfin's native remote (provider) search for a free-text title. The search Name is
    /// arbitrary — it is NOT constrained to the current item's match, so the user can search a totally
    /// different title. The item id only supplies provider context.
    /// </summary>
    /// <param name="itemId">The item being corrected (context; also validates the item exists).</param>
    /// <param name="kind">Search kind: Movie or Series.</param>
    /// <param name="name">Free-text title to search for.</param>
    /// <param name="year">Optional production year to disambiguate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidate matches, or null when the item does not exist.</returns>
    public async Task<IReadOnlyList<RemoteSearchCandidateDto>?> RemoteSearchAsync(
        Guid itemId,
        BaseItemKind kind,
        string name,
        int? year,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        IEnumerable<RemoteSearchResult> results;
        if (kind == BaseItemKind.Series)
        {
            var query = new RemoteSearchQuery<SeriesInfo>
            {
                ItemId = itemId,
                SearchInfo = new SeriesInfo { Name = name, Year = year },
            };
            results = await _providerManager
                .GetRemoteSearchResults<Series, SeriesInfo>(query, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var query = new RemoteSearchQuery<MovieInfo>
            {
                ItemId = itemId,
                SearchInfo = new MovieInfo { Name = name, Year = year },
            };
            results = await _providerManager
                .GetRemoteSearchResults<Movie, MovieInfo>(query, cancellationToken)
                .ConfigureAwait(false);
        }

        return results.Select(MapCandidate).ToList();
    }

    /// <summary>
    /// Applies a correction to a library item and awaits a full metadata + image refresh, mirroring
    /// Jellyfin's own ItemLookupController.ApplySearchCriteria. The provider ids are set explicitly
    /// first (a refresh does not erase them), then a FullRefresh with ReplaceAllMetadata rewrites the
    /// item from the chosen providers. This awaits completion, so the returned item is the refreshed one.
    /// </summary>
    /// <param name="itemId">The library item to correct.</param>
    /// <param name="providerIds">The provider ids to set (e.g. { "Tmdb": "603" }). Must be non-empty.</param>
    /// <param name="candidate">
    /// Optional full remote-search result the user picked. When supplied it is passed through as the
    /// refresh SearchResult (native path). When null, a minimal result carrying just the provider ids
    /// is synthesized (explicit-provider path). Both drive the identical refresh.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refreshed item DTO, or null when the item does not exist.</returns>
    public async Task<LibraryItemDto?> ApplyAsync(
        Guid itemId,
        IReadOnlyDictionary<string, string> providerIds,
        RemoteSearchResult? candidate,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        var searchResult = candidate ?? new RemoteSearchResult();
        searchResult.ProviderIds = new Dictionary<string, string>(providerIds, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "JellyFetch: applying metadata correction to {ItemId} ({ItemName}): {@ProviderIds}",
            item.Id,
            item.Name,
            searchResult.ProviderIds);

        // A refresh won't erase provider ids, so set them explicitly before refreshing.
        item.ProviderIds = new Dictionary<string, string>(searchResult.ProviderIds, StringComparer.OrdinalIgnoreCase);

        await _providerManager.RefreshFullItem(
            item,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ReplaceAllImages = true,
                SearchResult = searchResult,
                RemoveOldMetadata = true,
            },
            cancellationToken).ConfigureAwait(false);

        // Re-read so the returned metadata reflects the refreshed state.
        var refreshed = _libraryManager.GetItemById(itemId) ?? item;
        return MapItem(refreshed);
    }

    /// <summary>Builds a <see cref="RemoteSearchResult"/> from a client-supplied candidate DTO for the native apply path.</summary>
    /// <param name="dto">The candidate the client echoed back.</param>
    /// <returns>The native result, or null when the DTO has no provider ids.</returns>
    public static RemoteSearchResult? ToRemoteSearchResult(RemoteSearchCandidateDto dto)
    {
        if (dto.ProviderIds is null || dto.ProviderIds.Count == 0)
        {
            return null;
        }

        return new RemoteSearchResult
        {
            Name = dto.Name,
            ProductionYear = dto.ProductionYear,
            Overview = dto.Overview,
            ImageUrl = dto.ImageUrl,
            SearchProviderName = dto.SearchProviderName,
            ProviderIds = new Dictionary<string, string>(dto.ProviderIds, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// When a resolved item is an Episode/Season, returns its owning Series; otherwise the item itself.
    /// The user corrects the show/movie, not an individual episode file.
    /// </summary>
    private static BaseItem PromoteToCorrectable(BaseItem item) => item switch
    {
        Episode { Series: { } series } => series,
        Season { Series: { } series } => series,
        _ => item,
    };

    private static BaseItemKind MapKind(BaseItem item) => item switch
    {
        Series => BaseItemKind.Series,
        Movie => BaseItemKind.Movie,
        Episode => BaseItemKind.Episode,
        _ => item.GetBaseItemKind(),
    };

    private LibraryItemDto MapItem(BaseItem item)
    {
        var id = item.Id;
        var idN = id.ToString("N", CultureInfo.InvariantCulture);

        string? posterTag = null;
        string? posterUrl = null;
        var hasPrimary = item.HasImage(ImageType.Primary, 0);
        if (hasPrimary)
        {
            var image = item.GetImageInfo(ImageType.Primary, 0);
            if (image is not null)
            {
                posterTag = GetImageCacheTag(item, image);
            }

            posterUrl = posterTag is null
                ? $"/Items/{idN}/Images/Primary"
                : $"/Items/{idN}/Images/Primary?tag={posterTag}";
        }

        return new LibraryItemDto
        {
            ItemId = idN,
            Name = item.Name ?? string.Empty,
            ProductionYear = item.ProductionYear,
            Type = MapKind(item).ToString(),
            ProviderIds = item.ProviderIds is { Count: > 0 }
                ? new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(),
            HasPrimaryImage = hasPrimary,
            PosterUrl = posterUrl,
            PosterTag = posterTag,
        };
    }

    /// <summary>
    /// Derives a stable cache tag for an image without depending on IImageProcessor (which would add a
    /// heavier dependency). The image's last-modified ticks are a sufficient cache key for the client.
    /// </summary>
    private static string GetImageCacheTag(BaseItem item, ItemImageInfo image)
    {
        try
        {
            return image.DateModified.Ticks.ToString("x", CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return item.Id.ToString("N", CultureInfo.InvariantCulture);
        }
    }

    private static RemoteSearchCandidateDto MapCandidate(RemoteSearchResult result) => new()
    {
        Name = result.Name ?? string.Empty,
        ProductionYear = result.ProductionYear,
        Overview = result.Overview,
        ImageUrl = result.ImageUrl,
        SearchProviderName = result.SearchProviderName,
        ProviderIds = result.ProviderIds is { Count: > 0 }
            ? new Dictionary<string, string>(result.ProviderIds, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(),
    };
}
