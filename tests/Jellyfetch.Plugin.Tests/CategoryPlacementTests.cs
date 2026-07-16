using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Download.WebMedia;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// End-to-end placement proof for the film-vs-series classification: that the resolved
/// <see cref="MediaCategory"/> drives BOTH axes of on-disk placement — the library ROOT (chosen by
/// the real <see cref="NaiveMediaPlacer"/> via <see cref="ILibraryRootResolver"/>) and the within-root
/// LAYOUT tree (built by the real <see cref="MediaOrganizer"/>). A film must physically land under the
/// MOVIES library root in a movie layout; a series episode under the TV SHOWS library root in a series
/// layout. This is the exact bug the user feared: a film could get the "Movie" label yet still be
/// dropped in the series tree. These tests exercise the real placer with real file moves (temp dirs),
/// which is the strongest proof short of a live Jellyfin server.
///
/// As of library-driven placement the root comes from the user's Jellyfin libraries (resolved by
/// collection type), NOT configured paths — so these tests inject a <see cref="FakeLibraryRootResolver"/>
/// mapping each category to a temp root, in place of the old config-path setup.
/// </summary>
public sealed class CategoryPlacementTests : IDisposable
{
    private readonly string _root;
    private readonly string _moviesRoot;
    private readonly string _seriesRoot;
    private readonly FakeLibraryRootResolver _resolver = new();
    private readonly MediaOrganizer _organizer = new();
    private readonly NaiveMediaPlacer _placer;

    public CategoryPlacementTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jf-place-" + Guid.NewGuid().ToString("N"));
        _moviesRoot = Path.Combine(_root, "Movies");
        _seriesRoot = Path.Combine(_root, "Shows");
        Directory.CreateDirectory(_moviesRoot);
        Directory.CreateDirectory(_seriesRoot);

        // Category → library root, as the resolver would derive from Jellyfin's libraries by collection
        // type: Series ⇒ the tvshows library, Movie AND Other/Auto ⇒ the movies library.
        _resolver.RootByCategory[MediaCategory.Series] = _seriesRoot;
        _resolver.RootByCategory[MediaCategory.Movie] = _moviesRoot;
        _resolver.RootByCategory[MediaCategory.Other] = _moviesRoot;
        _placer = new NaiveMediaPlacer(_resolver);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    /// <summary>
    /// Lay a single video out in a staging dir exactly as WebMediaDownloadHandler.LayOut does —
    /// via MediaOrganizer.Plan(meta) — then run the real placer over it (PreLaidOut = true).
    /// Returns the final on-disk paths.
    /// </summary>
    private async Task<System.Collections.Generic.IReadOnlyList<string>> LayoutAndPlaceAsync(MediaMetadata meta)
    {
        var staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        var plan = _organizer.Plan(meta);
        var videoRel = plan.VideoRelativePath(".mp4").Replace('/', Path.DirectorySeparatorChar);
        var videoPath = Path.Combine(staging, videoRel);
        Directory.CreateDirectory(Path.GetDirectoryName(videoPath)!);
        await File.WriteAllTextAsync(videoPath, "fake video bytes");

        var result = new DownloadResult
        {
            Files = new[] { videoPath },
            Metadata = meta,
            PreLaidOut = true,
        };

        var placement = await _placer.PlaceAsync(result, staging, null, CancellationToken.None);
        return placement.FinalPaths;
    }

    [Fact]
    public async Task Film_lands_under_movies_root_in_movie_layout()
    {
        // Exactly what ParseEpisodeNfo now produces for a standalone SVT film ("Son").
        var meta = new MediaMetadata
        {
            Category = MediaCategory.Movie,
            Title = "Son",
            SeriesName = null,
            Year = 2025,
        };

        var finalPaths = await LayoutAndPlaceAsync(meta);
        var video = finalPaths.Single(p => p.EndsWith(".mp4", StringComparison.Ordinal));

        // Physically under the MOVIES root, NOT the series root.
        Assert.StartsWith(_moviesRoot + Path.DirectorySeparatorChar, video);
        Assert.DoesNotContain(_seriesRoot, video);
        // Jellyfin movie layout: {Title (Year)}/{Title (Year)}.mp4
        Assert.Equal(
            Path.Combine(_moviesRoot, "Son (2025)", "Son (2025).mp4"),
            video);
    }

    [Fact]
    public async Task Series_episode_lands_under_series_root_in_series_layout()
    {
        // Exactly what ParseEpisodeNfo produces for a real SVT episode.
        var meta = new MediaMetadata
        {
            Category = MediaCategory.Series,
            Title = "Igenkännandet",
            SeriesName = "Pojken i grannhuset",
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Year = 2025,
        };

        var finalPaths = await LayoutAndPlaceAsync(meta);
        var video = finalPaths.Single(p => p.EndsWith(".mp4", StringComparison.Ordinal));

        // Physically under the SERIES root, NOT the movies root.
        Assert.StartsWith(_seriesRoot + Path.DirectorySeparatorChar, video);
        Assert.DoesNotContain(_moviesRoot, video);
        // Jellyfin series layout: {Series}/Season NN/{Series} SxxEyy.mp4 (åäö preserved).
        Assert.Equal(
            Path.Combine(_seriesRoot, "Pojken i grannhuset", "Season 01", "Pojken i grannhuset S01E01.mp4"),
            video);
    }

    [Fact]
    public async Task Other_web_video_lands_under_movies_root_not_series()
    {
        // Under library-driven placement there is no separate "fallback" library: unclassifiable
        // content (Category = Other) resolves to the MOVIES library — the historical "unclassifiable ⇒
        // movie root" rule, now expressed against the movies library.
        var meta = new MediaMetadata
        {
            Category = MediaCategory.Other,
            Title = "Me at the zoo",
            Year = 2005,
        };

        var finalPaths = await LayoutAndPlaceAsync(meta);
        var video = finalPaths.Single(p => p.EndsWith(".mp4", StringComparison.Ordinal));

        Assert.StartsWith(_moviesRoot + Path.DirectorySeparatorChar, video);
        Assert.DoesNotContain(_seriesRoot, video);
    }

    [Fact]
    public async Task Explicit_library_id_supersedes_category_root()
    {
        // An explicit library id wins over the category-derived root: a Movie-classified download with
        // an explicit "put this in my TV Shows library" id lands under the series root, not movies.
        _resolver.RootById["lib-tv"] = _seriesRoot;

        var staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var meta = new MediaMetadata { Category = MediaCategory.Movie, Title = "Explicitly Filed", Year = 2024 };
        var plan = _organizer.Plan(meta);
        var videoRel = plan.VideoRelativePath(".mp4").Replace('/', Path.DirectorySeparatorChar);
        var videoPath = Path.Combine(staging, videoRel);
        Directory.CreateDirectory(Path.GetDirectoryName(videoPath)!);
        await File.WriteAllTextAsync(videoPath, "fake video bytes");

        var result = new DownloadResult { Files = new[] { videoPath }, Metadata = meta, PreLaidOut = true };
        var placement = await _placer.PlaceAsync(result, staging, "lib-tv", CancellationToken.None);
        var video = placement.FinalPaths.Single(p => p.EndsWith(".mp4", StringComparison.Ordinal));

        Assert.StartsWith(_seriesRoot + Path.DirectorySeparatorChar, video);
        Assert.Equal(_seriesRoot, placement.LibraryRootUsed);
    }
}
