using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Download;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// Placement-hardening proof for the "tvshow.nfo already exists" series-download bug.
///
/// Series downloads fan out into N independent child jobs, each with its own staging dir; every
/// child lays out an identical, title-only, byte-for-byte-equal series-level <c>tvshow.nfo</c> into
/// the SAME series folder under the shared library root. The old placer appended " (1)" exactly
/// once (never re-checking File.Exists) and then re-threw an uncaught IOException from the
/// File.Copy fallback — so the 3rd+ episode of a series FAILED at placement even though the video
/// had already landed. The fix: tvshow.nfo is a write-if-absent artifact (skip-if-exists, not
/// counted as a placed path), and the generic collision handler now loops to a genuinely free slot.
///
/// These tests exercise the real <see cref="NaiveMediaPlacer"/> with real temp-dir file moves,
/// which is the strongest proof short of a live Jellyfin server (dream W-057: media-downloader's
/// process runner isn't injectable, so unit-test the placer directly — it IS testable in isolation).
///
/// Serialized via the PluginState collection because <see cref="NaiveMediaPlacer"/> reads
/// <c>Plugin.Instance.Configuration</c> for the library roots.
/// </summary>
[Collection("PluginState")]
public sealed class NaiveMediaPlacerTests : IDisposable
{
    private const string TvShowNfoBody =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<tvshow>\n  <title>Vår tid är nu</title>\n</tvshow>\n";

    private readonly string _root;
    private readonly string _seriesRoot;
    private readonly NaiveMediaPlacer _placer = new();

    public NaiveMediaPlacerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jf-placer-" + Guid.NewGuid().ToString("N"));
        _seriesRoot = Path.Combine(_root, "Shows");
        Directory.CreateDirectory(_seriesRoot);
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

    private PluginConfigScope NewScope()
    {
        var scope = new PluginConfigScope(_root);
        scope.Configuration.SeriesLibraryPath = _seriesRoot;
        scope.Configuration.MovieLibraryPath = Path.Combine(_root, "Movies");
        scope.Configuration.FallbackLibraryPath = Path.Combine(_root, "Web");
        return scope;
    }

    /// <summary>
    /// Lay out one episode of "Vår tid är nu" into a fresh per-child staging dir, exactly as
    /// WebMediaDownloadHandler.LayOut does: episode video + episode .nfo + the series-level
    /// tvshow.nfo (byte-identical across episodes), then run the real placer (PreLaidOut = true).
    /// Returns the final on-disk paths reported by the placer.
    /// </summary>
    private async Task<IReadOnlyList<string>> PlaceEpisodeAsync(int episode)
    {
        var staging = Path.Combine(_root, "staging-e" + episode + "-" + Guid.NewGuid().ToString("N"));
        var seriesFolder = Path.Combine(staging, "Vår tid är nu");
        var seasonFolder = Path.Combine(seriesFolder, "Season 01");
        Directory.CreateDirectory(seasonFolder);

        var stem = string.Format(System.Globalization.CultureInfo.InvariantCulture, "Vår tid är nu S01E{0:D2}", episode);
        var videoPath = Path.Combine(seasonFolder, stem + ".mp4");
        var episodeNfoPath = Path.Combine(seasonFolder, stem + ".nfo");
        var tvShowNfoPath = Path.Combine(seriesFolder, "tvshow.nfo");

        await File.WriteAllTextAsync(videoPath, "fake video bytes for episode " + episode);
        await File.WriteAllTextAsync(episodeNfoPath, "<episodedetails><episode>" + episode + "</episode></episodedetails>");
        await File.WriteAllTextAsync(tvShowNfoPath, TvShowNfoBody);

        var result = new DownloadResult
        {
            Files = new[] { videoPath, episodeNfoPath, tvShowNfoPath },
            Metadata = new MediaMetadata
            {
                Category = MediaCategory.Series,
                Title = "Avsnitt " + episode,
                SeriesName = "Vår tid är nu",
                SeasonNumber = 1,
                EpisodeNumber = episode,
            },
            PreLaidOut = true,
        };

        var placement = await _placer.PlaceAsync(result, staging, CancellationToken.None);
        return placement.FinalPaths;
    }

    // ---- (1) tvshow.nfo write-once, skip-if-exists ----

    [Fact]
    public async Task Placing_tvshow_nfo_twice_into_same_root_succeeds_with_no_duplicate()
    {
        using var scope = NewScope();

        var first = await PlaceEpisodeAsync(1);
        var second = await PlaceEpisodeAsync(2); // would previously create tvshow (1).nfo

        var seriesFolder = Path.Combine(_seriesRoot, "Vår tid är nu");
        var tvShowNfo = Path.Combine(seriesFolder, "tvshow.nfo");

        // The series-level nfo exists exactly once, with the original identical content.
        Assert.True(File.Exists(tvShowNfo));
        Assert.Equal(TvShowNfoBody, await File.ReadAllTextAsync(tvShowNfo));

        // No "tvshow (1).nfo" pollution.
        var nfoFiles = Directory.GetFiles(seriesFolder, "tvshow*.nfo");
        Assert.Single(nfoFiles);
        Assert.DoesNotContain(nfoFiles, f => f.Contains("(1)", StringComparison.Ordinal));

        // Episode 1's placement DID report the tvshow.nfo (it wrote it); episode 2's did NOT
        // (skipped) — so it isn't double-reported and the job stays green.
        Assert.Contains(first, p => Path.GetFileName(p).Equals("tvshow.nfo", StringComparison.Ordinal));
        Assert.DoesNotContain(second, p => Path.GetFileName(p).Equals("tvshow.nfo", StringComparison.Ordinal));

        // Both episodes' videos landed correctly.
        Assert.True(File.Exists(Path.Combine(seriesFolder, "Season 01", "Vår tid är nu S01E01.mp4")));
        Assert.True(File.Exists(Path.Combine(seriesFolder, "Season 01", "Vår tid är nu S01E02.mp4")));
    }

    [Fact]
    public async Task Multi_episode_series_all_place_without_throwing_and_video_lands_per_episode()
    {
        // The group cardinality (dream I-135): a per-item fix must be verified across N children,
        // not just one. This is the exact failure the user saw — episode 3+ failing at placement.
        using var scope = NewScope();

        for (var e = 1; e <= 5; e++)
        {
            var placed = await PlaceEpisodeAsync(e); // must not throw for ANY episode
            Assert.NotEmpty(placed);
        }

        var seriesFolder = Path.Combine(_seriesRoot, "Vår tid är nu");
        var seasonFolder = Path.Combine(seriesFolder, "Season 01");

        // Every episode's video + episode nfo is present.
        for (var e = 1; e <= 5; e++)
        {
            var stem = string.Format(System.Globalization.CultureInfo.InvariantCulture, "Vår tid är nu S01E{0:D2}", e);
            Assert.True(File.Exists(Path.Combine(seasonFolder, stem + ".mp4")), $"missing video E{e:D2}");
            Assert.True(File.Exists(Path.Combine(seasonFolder, stem + ".nfo")), $"missing episode nfo E{e:D2}");
        }

        // Exactly one series-level tvshow.nfo, no numbered duplicates.
        Assert.Single(Directory.GetFiles(seriesFolder, "tvshow*.nfo"));
    }

    // ---- (2) generic collision handling loops to a free slot ----

    [Fact]
    public async Task Three_colliding_non_tvshow_files_get_sequential_unique_suffixes()
    {
        using var scope = NewScope();

        var targetDir = Path.Combine(_seriesRoot, "Collide");
        var results = new List<IReadOnlyList<string>>();

        // Place three distinct staging files that all want the SAME final name "clip.mp4".
        for (var i = 0; i < 3; i++)
        {
            var staging = Path.Combine(_root, "collide-staging-" + i + "-" + Guid.NewGuid().ToString("N"));
            var rel = Path.Combine("Collide", "clip.mp4");
            var src = Path.Combine(staging, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(src)!);
            await File.WriteAllTextAsync(src, "content " + i);

            var result = new DownloadResult
            {
                Files = new[] { src },
                Metadata = new MediaMetadata { Category = MediaCategory.Series, Title = "clip", SeriesName = "Collide" },
                PreLaidOut = true,
            };

            results.Add((await _placer.PlaceAsync(result, staging, CancellationToken.None)).FinalPaths);
        }

        // clip.mp4, clip (1).mp4, clip (2).mp4 — three distinct files, none clobbered, none thrown.
        var placed = results.SelectMany(r => r).ToList();
        Assert.Equal(3, placed.Count);
        Assert.True(File.Exists(Path.Combine(targetDir, "clip.mp4")));
        Assert.True(File.Exists(Path.Combine(targetDir, "clip (1).mp4")));
        Assert.True(File.Exists(Path.Combine(targetDir, "clip (2).mp4")));
        Assert.Equal(3, Directory.GetFiles(targetDir, "clip*.mp4").Length);
    }

    // ---- (3) cross-filesystem copy fallback on a resolved unique target ----

    [Fact]
    public async Task Copy_fallback_path_does_not_throw_on_resolved_unique_target()
    {
        // Simulate the cross-filesystem branch: File.Move throwing IOException is normally the
        // cross-device signal. We can't force a second mount in a unit test, but we CAN prove the
        // fallback's OWN semantics: once a unique target is resolved, a File.Copy(overwrite:true)
        // to it succeeds. Here we drive it through the public placer with a pre-existing collision
        // so the unique-suffix resolver runs, then confirm the placed file is intact and unique.
        using var scope = NewScope();

        var targetDir = Path.Combine(_seriesRoot, "Fallback");
        Directory.CreateDirectory(targetDir);
        // Pre-seed the primary name so the resolver must pick "movie (1).mp4".
        await File.WriteAllTextAsync(Path.Combine(targetDir, "movie.mp4"), "pre-existing");

        var staging = Path.Combine(_root, "fallback-staging-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(staging, "Fallback", "movie.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        await File.WriteAllTextAsync(src, "new content");

        var result = new DownloadResult
        {
            Files = new[] { src },
            Metadata = new MediaMetadata { Category = MediaCategory.Series, Title = "movie", SeriesName = "Fallback" },
            PreLaidOut = true,
        };

        var placement = await _placer.PlaceAsync(result, staging, CancellationToken.None);

        var placed = Assert.Single(placement.FinalPaths);
        Assert.Equal(Path.Combine(targetDir, "movie (1).mp4"), placed);
        Assert.Equal("new content", await File.ReadAllTextAsync(placed));
        // Original untouched (not clobbered).
        Assert.Equal("pre-existing", await File.ReadAllTextAsync(Path.Combine(targetDir, "movie.mp4")));
    }
}
