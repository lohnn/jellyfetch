using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Configuration;
using Jellyfetch.Plugin.Download;
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
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<LibraryMetadataService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryMetadataService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="providerManager">Jellyfin provider (metadata) manager.</param>
    /// <param name="libraryMonitor">Jellyfin library monitor, used to trigger scoped rescans after a conversion.</param>
    /// <param name="fileSystem">Jellyfin file-system abstraction (needed by MetadataRefreshOptions).</param>
    /// <param name="logger">Logger.</param>
    public LibraryMetadataService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ILibraryMonitor libraryMonitor,
        IFileSystem fileSystem,
        ILogger<LibraryMetadataService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _libraryMonitor = libraryMonitor;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

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

    /// <summary>
    /// Converts a library item's type by re-ingesting it. Jellyfin has NO in-place type change — an
    /// item's type is its CLR subclass + folder shape (verified: BaseItem kind is derived from
    /// GetType().Name; there is no reclassify API). So this: (1) collects the item's video file(s),
    /// (2) moves them into the TARGET library root with the correct layout + a seed NFO, (3) deletes the
    /// old mis-typed library item WITHOUT deleting the (already-moved) files, and (4) triggers a scoped
    /// rescan so Jellyfin re-creates the item as the correct type. The rescan is async — the new item id
    /// is not known synchronously, hence the "RescanPending" result.
    ///
    /// <para>The target is a <see cref="MediaCategory"/>: <c>Movie</c>/<c>Series</c> select the movie/
    /// series library roots; <c>Other</c> selects the fallback root (falling back to the movie root when
    /// unset — mirroring <see cref="NaiveMediaPlacer"/>'s <see cref="MediaCategory.Other"/> precedence).
    /// Jellyfin has no literal "Other" item type — what it re-types the item as depends on which library
    /// the fallback root belongs to (the user's Jellyfin config); JellyFetch only relocates it there.</para>
    /// </summary>
    /// <param name="itemId">The library item to convert (must currently be a Movie or Series).</param>
    /// <param name="targetCategory">The desired category: Movie, Series, or Other.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The conversion outcome.</returns>
    public Task<ConvertTypeResult> ConvertTypeAsync(Guid itemId, MediaCategory targetCategory, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return Task.FromResult(ConvertTypeResult.NotFound());
        }

        var currentKind = MapKind(item);
        if (currentKind != BaseItemKind.Movie && currentKind != BaseItemKind.Series)
        {
            return Task.FromResult(ConvertTypeResult.Rejected(
                $"Only Movie and Series items can be converted. This item is a {currentKind}. "
                + "Correct the parent show/movie instead of an individual episode."));
        }

        // "Already that type" for Movie/Series is a straight kind match. (Other has no item kind, so it
        // is never a straight match here — its no-op case is the same-root check below.)
        if ((targetCategory == MediaCategory.Movie && currentKind == BaseItemKind.Movie)
            || (targetCategory == MediaCategory.Series && currentKind == BaseItemKind.Series))
        {
            return Task.FromResult(ConvertTypeResult.Rejected($"Item is already a {currentKind}."));
        }

        // Resolve the destination root using the placer's exact precedence (Other ⇒ fallback, or movie
        // when fallback is unset). Returns null with a message when the required root isn't configured.
        var (targetRoot, rootError) = ResolveTargetRoot(targetCategory);
        if (rootError is not null)
        {
            return Task.FromResult(ConvertTypeResult.Rejected(rootError));
        }

        // HONEST no-op guard: if the resolved destination root is the SAME root the item already lives
        // under, the move+rescan would just re-file it as the same category (misleading the user). This
        // is the Other-with-empty-fallback-on-a-movie case, and any target-root == current-root case.
        var currentRoot = ResolveCurrentRoot(item);
        if (currentRoot is not null && TypeConversionLayout.SameRoot(currentRoot, targetRoot!))
        {
            return Task.FromResult(ConvertTypeResult.Rejected(
                targetCategory == MediaCategory.Other
                    ? "Converting to Other would re-file this item into the same library it's already in, "
                      + "because no separate fallback library is configured. Set the JellyFetch \"fallback "
                      + "library path\" to a DISTINCT library root (e.g. a Home Videos library) first, then retry."
                    : "The target library root is the same directory this item already lives in, so the "
                      + "conversion would be a no-op. Configure a distinct library root for the target type first."));
        }

        // Gather the physical video files backing this item BEFORE we touch anything.
        var videoFiles = CollectVideoFiles(item);
        if (videoFiles.Count == 0)
        {
            return Task.FromResult(ConvertTypeResult.Rejected(
                "Could not locate any video files on disk for this item, so it cannot be re-ingested. "
                + "The files may have been moved or removed outside JellyFetch."));
        }

        // W-049: the move crosses library roots — fail fast (before any move) if the target root isn't
        // writable by the Jellyfin service user, so we never half-move.
        try
        {
            PlacementPermissions.EnsureWritable(targetRoot!);
        }
        catch (PlacementPermissionException ex)
        {
            return Task.FromResult(ConvertTypeResult.PermissionDenied(ex.Message));
        }

        var title = string.IsNullOrWhiteSpace(item.Name) ? "Untitled" : item.Name;
        var year = item.ProductionYear;

        List<string> movedPaths;
        string newItemDir;
        try
        {
            // Series ⇒ episode layout; Movie AND Other ⇒ the titled {Title (Year)}/… layout with a
            // <movie> NFO (matching MediaOrganizer, whose Other layout is identical to Movie — only the
            // ROOT differs). The fallback library decides what it becomes on rescan.
            (movedPaths, newItemDir) = targetCategory == MediaCategory.Series
                ? LayOutAsSeries(targetRoot!, title, year, videoFiles)
                : LayOutAsMovie(targetRoot!, title, year, videoFiles);
        }
        catch (PlacementPermissionException ex)
        {
            return Task.FromResult(ConvertTypeResult.PermissionDenied(ex.Message));
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "JellyFetch: convert-type move failed for {ItemId}", itemId);
            return Task.FromResult(ConvertTypeResult.Rejected($"Failed to move files: {ex.Message}"));
        }

        // Delete the OLD mis-typed library item, keeping the (already-moved) files on disk.
        // Its old on-disk location is now empty of the video we moved; Jellyfin drops the stale item.
        try
        {
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
        }
        catch (Exception ex)
        {
            // The move already succeeded; a delete failure is non-fatal (the rescan + a stale duplicate
            // is recoverable), but report it honestly so the user knows to clean up the old entry.
            _logger.LogWarning(ex, "JellyFetch: convert-type could not delete old item {ItemId}", itemId);
        }

        // Trigger a scoped rescan on the new location so Jellyfin ingests it as the correct type.
        try
        {
            _libraryMonitor.ReportFileSystemChanged(newItemDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyFetch: convert-type could not report change for {Path}", newItemDir);
        }

        _logger.LogInformation(
            "JellyFetch: converted item {ItemId} ({Title}) to {TargetType}; moved {Count} file(s) into {Dir}, rescan pending",
            itemId,
            title,
            targetCategory,
            movedPaths.Count,
            newItemDir);

        return Task.FromResult(ConvertTypeResult.RescanPending(itemId, targetCategory.ToString(), targetRoot!, movedPaths, title));
    }

    /// <summary>
    /// Resolves the destination library root for a convert target from the live plugin config, mirroring
    /// <see cref="NaiveMediaPlacer"/>'s per-category precedence (Other ⇒ fallback, else movie). Returns an
    /// error message (and null root) when the required root isn't configured. Delegates the pure
    /// precedence rule to <see cref="TypeConversionLayout.ResolveTargetRoot"/> so it is unit-testable.
    /// </summary>
    private static (string? Root, string? Error) ResolveTargetRoot(MediaCategory targetCategory)
    {
        var config = Config;
        return TypeConversionLayout.ResolveTargetRoot(
            targetCategory,
            config.SeriesLibraryPath,
            config.MovieLibraryPath,
            config.FallbackLibraryPath);
    }

    /// <summary>
    /// Determines which configured library root an item currently lives under (used by the no-op guard),
    /// or null when its path is outside all configured roots (then no no-op is possible).
    /// </summary>
    private static string? ResolveCurrentRoot(BaseItem item)
    {
        var path = item.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var config = Config;
        foreach (var root in new[] { config.SeriesLibraryPath, config.MovieLibraryPath, config.FallbackLibraryPath })
        {
            if (!string.IsNullOrWhiteSpace(root) && TypeConversionLayout.PathIsUnder(path, root))
            {
                return root;
            }
        }

        return null;
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

    /// <summary>Video file extensions we treat as the item's payload when re-ingesting.</summary>
    private static readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg", ".vob",
    };

    /// <summary>
    /// Collects the physical video files backing an item: for a Movie (a Video), its own path; for a
    /// Series (a Folder), the paths of its recursive Episode/Video children. Only real, existing files
    /// with a known video extension are returned.
    /// </summary>
    private static List<string> CollectVideoFiles(BaseItem item)
    {
        var paths = new List<string>();

        void Consider(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)
                && _videoExtensions.Contains(Path.GetExtension(path))
                && File.Exists(path))
            {
                paths.Add(path);
            }
        }

        if (item is Folder folder)
        {
            foreach (var child in folder.GetRecursiveChildren())
            {
                if (child is Video)
                {
                    Consider(child.Path);
                }
            }
        }
        else
        {
            Consider(item.Path);
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Lays the video file(s) out as a Movie and delegates the file moves to <see cref="TypeConversionLayout"/>
    /// (extracted so the pure layout/NFO/move logic is unit-testable without a live Jellyfin server).
    /// </summary>
    private static (List<string> Moved, string ItemDir) LayOutAsMovie(string root, string title, int? year, List<string> videoFiles)
        => TypeConversionLayout.LayOutAsMovie(root, title, year, videoFiles);

    /// <summary>
    /// Lays the video file(s) out as a Series and delegates the file moves to <see cref="TypeConversionLayout"/>.
    /// </summary>
    private static (List<string> Moved, string ItemDir) LayOutAsSeries(string root, string title, int? year, List<string> videoFiles)
        => TypeConversionLayout.LayOutAsSeries(root, title, year, videoFiles);
}

/// <summary>
/// The pure, filesystem-only layout logic for type conversion (Movie ↔ Series): folder/file naming,
/// NFO generation, and the collision-safe cross-device move. Extracted from
/// <see cref="LibraryMetadataService"/> so it can be unit-tested in isolation with real temp-dir moves
/// — the strongest proof short of a live Jellyfin server (dream W-057: the service itself needs
/// ILibraryManager/IProviderManager which aren't injectable in a unit test, but this layer isn't).
/// Movie: {root}/{Title (Year)}/{Title (Year)}{ext} + &lt;movie&gt; NFO.
/// Series: {root}/{Title}/Season 01/{Title} - S01Eyy{ext} + tvshow.nfo.
/// </summary>
internal static class TypeConversionLayout
{
    /// <summary>Lays the video file(s) out as a Movie. Returns the moved paths and the new item directory.</summary>
    /// <param name="root">The target (movie) library root.</param>
    /// <param name="title">The item title.</param>
    /// <param name="year">The production year, when known.</param>
    /// <param name="videoFiles">The source video files to move.</param>
    /// <returns>The moved absolute paths and the new item directory (rescan target).</returns>
    public static (List<string> Moved, string ItemDir) LayOutAsMovie(string root, string title, int? year, List<string> videoFiles)
    {
        var folderName = TitleWithYear(title, year);
        var itemDir = Path.Combine(root, folderName);
        var moved = new List<string>();

        for (var i = 0; i < videoFiles.Count; i++)
        {
            var ext = Path.GetExtension(videoFiles[i]);
            var stem = videoFiles.Count == 1 || i == 0
                ? folderName
                : string.Format(CultureInfo.InvariantCulture, "{0} - part{1}", folderName, i + 1);
            moved.Add(MoveInto(itemDir, videoFiles[i], stem + ext));
        }

        WriteText(Path.Combine(itemDir, folderName + ".nfo"), BuildMovieNfo(title, year));
        return (moved, itemDir);
    }

    /// <summary>Lays the video file(s) out as a Series (one-episode series for a single file). Returns the moved paths and the new item directory.</summary>
    /// <param name="root">The target (series) library root.</param>
    /// <param name="title">The item title (becomes the series name).</param>
    /// <param name="year">The production year, when known.</param>
    /// <param name="videoFiles">The source video files to move (mapped to sequential S01 episodes).</param>
    /// <returns>The moved absolute paths and the new item directory (rescan target).</returns>
    public static (List<string> Moved, string ItemDir) LayOutAsSeries(string root, string title, int? year, List<string> videoFiles)
    {
        var seriesName = Sanitize(title);
        var itemDir = Path.Combine(root, seriesName);
        var seasonDir = Path.Combine(itemDir, "Season 01");
        var moved = new List<string>();

        for (var i = 0; i < videoFiles.Count; i++)
        {
            var ext = Path.GetExtension(videoFiles[i]);
            var fileName = string.Format(CultureInfo.InvariantCulture, "{0} - S01E{1:D2}{2}", seriesName, i + 1, ext);
            moved.Add(MoveInto(seasonDir, videoFiles[i], fileName));
        }

        WriteText(Path.Combine(itemDir, "tvshow.nfo"), BuildTvShowNfo(title, year));
        return (moved, itemDir);
    }

    /// <summary>Builds a minimal Jellyfin-readable &lt;movie&gt; NFO carrying title + year (providers fill the rest on rescan).</summary>
    /// <param name="title">The movie title.</param>
    /// <param name="year">The year, when known.</param>
    /// <returns>The NFO XML body.</returns>
    public static string BuildMovieNfo(string title, int? year)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.AppendLine("<movie>");
        sb.AppendLine("  <title>" + Xml(title) + "</title>");
        if (year is > 0)
        {
            sb.AppendLine("  <year>" + year.Value.ToString(CultureInfo.InvariantCulture) + "</year>");
        }

        sb.AppendLine("</movie>");
        return sb.ToString();
    }

    /// <summary>Builds a minimal series-level tvshow.nfo (title + optional year); providers fill the rest on rescan.</summary>
    /// <param name="title">The series title.</param>
    /// <param name="year">The year, when known.</param>
    /// <returns>The NFO XML body.</returns>
    public static string BuildTvShowNfo(string title, int? year)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.AppendLine("<tvshow>");
        sb.AppendLine("  <title>" + Xml(title) + "</title>");
        if (year is > 0)
        {
            sb.AppendLine("  <year>" + year.Value.ToString(CultureInfo.InvariantCulture) + "</year>");
        }

        sb.AppendLine("</tvshow>");
        return sb.ToString();
    }

    /// <summary>Produces the "Title (Year)" movie folder/file stem (year omitted when unknown), sanitized.</summary>
    /// <param name="title">The title.</param>
    /// <param name="year">The year, when known.</param>
    /// <returns>The sanitized stem.</returns>
    public static string TitleWithYear(string title, int? year)
    {
        var t = Sanitize(title);
        return year is > 0 ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", t, year.Value) : t;
    }

    /// <summary>Replaces invalid filename characters with spaces; empty/blank → "Untitled".</summary>
    /// <param name="name">The raw name.</param>
    /// <returns>A filesystem-safe name.</returns>
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned;
    }

    /// <summary>
    /// Pure per-category destination-root precedence for type conversion, mirroring
    /// <see cref="NaiveMediaPlacer"/>: Series ⇒ series root, Movie ⇒ movie root, Other ⇒ fallback root
    /// (or movie root when the fallback is empty). Returns an error message (and null root) when the
    /// required root isn't configured. Kept pure (roots passed in) so it is unit-testable without plugin state.
    /// </summary>
    /// <param name="targetCategory">The convert target category.</param>
    /// <param name="seriesRoot">The configured series library root.</param>
    /// <param name="movieRoot">The configured movie library root.</param>
    /// <param name="fallbackRoot">The configured fallback (Other) library root.</param>
    /// <returns>The resolved root and a null error, or a null root and an error message.</returns>
    public static (string? Root, string? Error) ResolveTargetRoot(MediaCategory targetCategory, string? seriesRoot, string? movieRoot, string? fallbackRoot)
    {
        switch (targetCategory)
        {
            case MediaCategory.Series:
                return string.IsNullOrWhiteSpace(seriesRoot)
                    ? (null, "No Series library path is configured. Set it in the JellyFetch plugin settings first.")
                    : (seriesRoot, null);
            case MediaCategory.Movie:
                return string.IsNullOrWhiteSpace(movieRoot)
                    ? (null, "No Movie library path is configured. Set it in the JellyFetch plugin settings first.")
                    : (movieRoot, null);
            case MediaCategory.Other:
                // Mirror the placer exactly: fallback, else movie.
                var root = string.IsNullOrWhiteSpace(fallbackRoot) ? movieRoot : fallbackRoot;
                return string.IsNullOrWhiteSpace(root)
                    ? (null, "No fallback (Other) library path and no movie library path are configured. "
                        + "Set the JellyFetch \"fallback library path\" to a distinct library first.")
                    : (root, null);
            default:
                return (null, $"Unsupported target type '{targetCategory}'. Use Movie, Series or Other.");
        }
    }

    /// <summary>
    /// True when two library roots are the same directory (normalized: full path, trailing separators
    /// trimmed, case-insensitive). Used by the convert no-op guard so relocating to the same root is
    /// rejected rather than silently re-filing the item as the same category.
    /// </summary>
    /// <param name="a">First root.</param>
    /// <param name="b">Second root.</param>
    /// <returns>True when they resolve to the same directory.</returns>
    public static bool SameRoot(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="path"/> is the same as, or nested under, <paramref name="root"/>
    /// (normalized). Used to determine which configured root an item currently lives in.
    /// </summary>
    /// <param name="path">The item path to test.</param>
    /// <param name="root">The candidate library root.</param>
    /// <returns>True when path is at or under root.</returns>
    public static bool PathIsUnder(string path, string root)
    {
        var np = Normalize(path);
        var nr = Normalize(root);
        if (string.Equals(np, nr, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return np.StartsWith(nr + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string p)
    {
        try
        {
            var full = Path.GetFullPath(p);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    /// <summary>Moves <paramref name="source"/> into <paramref name="targetDir"/> as <paramref name="fileName"/>, creating dirs and handling cross-device moves. Returns the final path.</summary>
    private static string MoveInto(string targetDir, string source, string fileName)
    {
        try
        {
            Directory.CreateDirectory(targetDir);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw PlacementPermissions.Denied(targetDir, ex);
        }

        var target = Path.Combine(targetDir, fileName);
        try
        {
            File.Move(source, target, overwrite: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw PlacementPermissions.Denied(targetDir, ex);
        }
        catch (IOException)
        {
            // Cross-device move: File.Move can't rename across filesystems, so copy+delete.
            File.Copy(source, target, overwrite: true);
            try
            {
                File.Delete(source);
            }
            catch (IOException)
            {
                // best effort — the copy succeeded, a lingering source is harmless.
            }
        }

        return target;
    }

    private static void WriteText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw PlacementPermissions.Denied(dir, ex);
        }
    }

    private static string Xml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}

/// <summary>
/// Outcome of a <see cref="LibraryMetadataService.ConvertTypeAsync"/> call. Distinguishes not-found,
/// rejected (validation), permission-denied, and success (rescan pending) so the controller can map
/// each to the right HTTP status without leaking exceptions.
/// </summary>
public sealed class ConvertTypeResult
{
    private ConvertTypeResult(ConvertTypeOutcome outcome, string? error, ConvertTypeResultDto? dto)
    {
        Outcome = outcome;
        Error = error;
        Dto = dto;
    }

    /// <summary>The outcome category.</summary>
    public enum ConvertTypeOutcome
    {
        /// <summary>The item id did not resolve to a library item.</summary>
        NotFoundOutcome,

        /// <summary>The request was valid JSON but not a permitted conversion (bad state/type/config).</summary>
        RejectedOutcome,

        /// <summary>The move target is not writable by the Jellyfin service user.</summary>
        PermissionDeniedOutcome,

        /// <summary>Files were moved and a rescan was triggered; the new item id is not yet known.</summary>
        RescanPendingOutcome,
    }

    /// <summary>Gets the outcome category.</summary>
    public ConvertTypeOutcome Outcome { get; }

    /// <summary>Gets the error message for non-success outcomes, or null.</summary>
    public string? Error { get; }

    /// <summary>Gets the success payload for the rescan-pending outcome, or null.</summary>
    public ConvertTypeResultDto? Dto { get; }

    /// <summary>Creates a not-found result.</summary>
    /// <returns>The result.</returns>
    public static ConvertTypeResult NotFound() => new(ConvertTypeOutcome.NotFoundOutcome, null, null);

    /// <summary>Creates a rejected (validation) result.</summary>
    /// <param name="error">The user-facing reason.</param>
    /// <returns>The result.</returns>
    public static ConvertTypeResult Rejected(string error) => new(ConvertTypeOutcome.RejectedOutcome, error, null);

    /// <summary>Creates a permission-denied result.</summary>
    /// <param name="error">The actionable, fix-carrying message.</param>
    /// <returns>The result.</returns>
    public static ConvertTypeResult PermissionDenied(string error) => new(ConvertTypeOutcome.PermissionDeniedOutcome, error, null);

    /// <summary>Creates a rescan-pending success result.</summary>
    /// <param name="sourceItemId">The converted (now-deleted) item id.</param>
    /// <param name="targetType">The type converted to ("Movie", "Series", or "Other").</param>
    /// <param name="newRoot">The library root the files were moved into.</param>
    /// <param name="movedPaths">The absolute moved file paths.</param>
    /// <param name="title">The item title (to seed the poll search).</param>
    /// <returns>The result.</returns>
    public static ConvertTypeResult RescanPending(Guid sourceItemId, string targetType, string newRoot, IReadOnlyList<string> movedPaths, string title)
    {
        // "Other" has no Jellyfin item type to poll by type — its new kind depends on the fallback
        // library's own type — so the poll hint drops the type filter and searches by title only.
        var pollQuery = string.Equals(targetType, "Other", StringComparison.OrdinalIgnoreCase)
            ? $"GET /Jellyfetch/Metadata/Items?searchTerm={Uri.EscapeDataString(title)} (the fallback library "
                + "decides its new type)"
            : $"GET /Jellyfetch/Metadata/Items?type={targetType}&searchTerm={Uri.EscapeDataString(title)}";

        return new ConvertTypeResult(
            ConvertTypeOutcome.RescanPendingOutcome,
            null,
            new ConvertTypeResultDto
            {
                SourceItemId = sourceItemId.ToString("N", CultureInfo.InvariantCulture),
                TargetType = targetType,
                Status = "RescanPending",
                NewLibraryRoot = newRoot,
                MovedPaths = movedPaths,
                Title = title,
                Message = $"Files moved and a library rescan was triggered. The re-typed item will appear "
                    + $"once the scan finishes — poll {pollQuery} to find it, then optionally apply a "
                    + "provider-id correction.",
            });
    }
}
