using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Download.WebMedia;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// End-to-end placement proof for the film-vs-series classification change: that the resolved
/// <see cref="MediaCategory"/> drives BOTH axes of on-disk placement — the library ROOT (chosen by
/// the real <see cref="NaiveMediaPlacer"/>) and the within-root LAYOUT tree (built by the real
/// <see cref="MediaOrganizer"/>). A film must physically land under the configured MOVIES root in a
/// movie layout; a series episode under the configured SERIES root in a series layout. This is the
/// exact bug the user feared: a film could get the "Movie" label yet still be dropped in the series
/// tree. These tests exercise the real placer with real file moves (temp dirs), which is the
/// strongest proof short of a live Jellyfin server.
///
/// Serialized via the PluginState collection because <see cref="NaiveMediaPlacer"/> reads
/// <c>Plugin.Instance.Configuration</c> for the library roots.
/// </summary>
[Collection("PluginState")]
public sealed class CategoryPlacementTests : IDisposable
{
    private readonly string _root;
    private readonly string _moviesRoot;
    private readonly string _seriesRoot;
    private readonly string _fallbackRoot;
    private readonly MediaOrganizer _organizer = new();
    private readonly NaiveMediaPlacer _placer = new();

    public CategoryPlacementTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jf-place-" + Guid.NewGuid().ToString("N"));
        _moviesRoot = Path.Combine(_root, "Movies");
        _seriesRoot = Path.Combine(_root, "Shows");
        _fallbackRoot = Path.Combine(_root, "Web");
        Directory.CreateDirectory(_moviesRoot);
        Directory.CreateDirectory(_seriesRoot);
        Directory.CreateDirectory(_fallbackRoot);
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

        var placement = await _placer.PlaceAsync(result, staging, CancellationToken.None);
        return placement.FinalPaths;
    }

    [Fact]
    public async Task Film_lands_under_movies_root_in_movie_layout()
    {
        using var scope = new PluginConfigScope(_root);
        scope.Configuration.MovieLibraryPath = _moviesRoot;
        scope.Configuration.SeriesLibraryPath = _seriesRoot;
        scope.Configuration.FallbackLibraryPath = _fallbackRoot;

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
        using var scope = new PluginConfigScope(_root);
        scope.Configuration.MovieLibraryPath = _moviesRoot;
        scope.Configuration.SeriesLibraryPath = _seriesRoot;
        scope.Configuration.FallbackLibraryPath = _fallbackRoot;

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
    public async Task Other_web_video_lands_under_fallback_root_not_series_or_movie()
    {
        using var scope = new PluginConfigScope(_root);
        scope.Configuration.MovieLibraryPath = _moviesRoot;
        scope.Configuration.SeriesLibraryPath = _seriesRoot;
        scope.Configuration.FallbackLibraryPath = _fallbackRoot;

        // A plain YouTube clip / no-NFO SVT fallback: Category = Other.
        var meta = new MediaMetadata
        {
            Category = MediaCategory.Other,
            Title = "Me at the zoo",
            Year = 2005,
        };

        var finalPaths = await LayoutAndPlaceAsync(meta);
        var video = finalPaths.Single(p => p.EndsWith(".mp4", StringComparison.Ordinal));

        Assert.StartsWith(_fallbackRoot + Path.DirectorySeparatorChar, video);
        Assert.DoesNotContain(_seriesRoot, video);
    }

    [Fact]
    public async Task Other_falls_back_to_movie_root_when_fallback_unconfigured()
    {
        using var scope = new PluginConfigScope(_root);
        scope.Configuration.MovieLibraryPath = _moviesRoot;
        scope.Configuration.SeriesLibraryPath = _seriesRoot;
        scope.Configuration.FallbackLibraryPath = string.Empty; // empty => movie root, per config contract

        var meta = new MediaMetadata { Category = MediaCategory.Other, Title = "Some Clip" };

        var finalPaths = await LayoutAndPlaceAsync(meta);
        var video = finalPaths.Single(p => p.EndsWith(".mp4", StringComparison.Ordinal));

        Assert.StartsWith(_moviesRoot + Path.DirectorySeparatorChar, video);
        Assert.DoesNotContain(_seriesRoot, video);
    }
}
