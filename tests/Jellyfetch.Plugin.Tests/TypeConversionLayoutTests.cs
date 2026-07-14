using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfetch.Plugin.Api;
using Jellyfetch.Plugin.Download;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// Proof for the type-conversion re-ingest layout (Movie ↔ Series). Jellyfin has NO in-place type
/// change — an item's type is its CLR subclass + folder shape — so converting a mis-typed item means
/// physically re-laying its video files into the OTHER library root with the correct layout + NFO,
/// then letting a rescan re-create it as the right type.
///
/// <see cref="TypeConversionLayout"/> is the pure filesystem half of that operation, extracted from
/// <see cref="LibraryMetadataService"/> precisely so it can be exercised with real temp-dir moves —
/// the strongest proof short of a live Jellyfin server (W-057). These tests assert the resulting
/// tree, filenames, NFO shapes, and that the source files are actually moved (not copied/left behind).
/// </summary>
public sealed class TypeConversionLayoutTests : IDisposable
{
    private readonly string _work;
    private readonly string _targetRoot;

    public TypeConversionLayoutTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "jf-convert-" + Guid.NewGuid().ToString("N"));
        _targetRoot = Path.Combine(_work, "target");
        Directory.CreateDirectory(_targetRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work))
            {
                Directory.Delete(_work, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private string MakeSourceFile(string name, string content = "video-bytes")
    {
        var dir = Path.Combine(_work, "source");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Series_to_Movie_lays_out_titled_folder_with_movie_nfo()
    {
        var src = MakeSourceFile("episode.mkv");

        var (moved, itemDir) = TypeConversionLayout.LayOutAsMovie(_targetRoot, "Some Film", 2021, new List<string> { src });

        var expectedDir = Path.Combine(_targetRoot, "Some Film (2021)");
        Assert.Equal(expectedDir, itemDir);

        var expectedFile = Path.Combine(expectedDir, "Some Film (2021).mkv");
        Assert.Single(moved);
        Assert.Equal(expectedFile, moved[0]);
        Assert.True(File.Exists(expectedFile));

        // The source must be MOVED, not copied.
        Assert.False(File.Exists(src));

        var nfoPath = Path.Combine(expectedDir, "Some Film (2021).nfo");
        Assert.True(File.Exists(nfoPath));
        var nfo = File.ReadAllText(nfoPath);
        Assert.Contains("<movie>", nfo, StringComparison.Ordinal);
        Assert.Contains("<title>Some Film</title>", nfo, StringComparison.Ordinal);
        Assert.Contains("<year>2021</year>", nfo, StringComparison.Ordinal);
    }

    [Fact]
    public void Movie_to_Series_lays_out_season01_episode_with_tvshow_nfo()
    {
        var src = MakeSourceFile("the movie.mp4");

        var (moved, itemDir) = TypeConversionLayout.LayOutAsSeries(_targetRoot, "My Show", 2019, new List<string> { src });

        var expectedDir = Path.Combine(_targetRoot, "My Show");
        Assert.Equal(expectedDir, itemDir);

        var expectedFile = Path.Combine(expectedDir, "Season 01", "My Show - S01E01.mp4");
        Assert.Single(moved);
        Assert.Equal(expectedFile, moved[0]);
        Assert.True(File.Exists(expectedFile));
        Assert.False(File.Exists(src));

        var nfoPath = Path.Combine(expectedDir, "tvshow.nfo");
        Assert.True(File.Exists(nfoPath));
        var nfo = File.ReadAllText(nfoPath);
        Assert.Contains("<tvshow>", nfo, StringComparison.Ordinal);
        Assert.Contains("<title>My Show</title>", nfo, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_file_series_to_movie_keeps_first_as_movie_and_parts_alongside()
    {
        var a = MakeSourceFile("a.mkv", "a");
        var b = MakeSourceFile("b.mkv", "b");

        var (moved, itemDir) = TypeConversionLayout.LayOutAsMovie(_targetRoot, "Two Parter", null, new List<string> { a, b });

        Assert.Equal(2, moved.Count);
        Assert.Equal(Path.Combine(itemDir, "Two Parter.mkv"), moved[0]);
        Assert.Equal(Path.Combine(itemDir, "Two Parter - part2.mkv"), moved[1]);
        Assert.All(moved, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void Multi_file_movie_to_series_maps_to_sequential_episodes()
    {
        var a = MakeSourceFile("one.mkv", "1");
        var b = MakeSourceFile("two.mkv", "2");
        var c = MakeSourceFile("three.mkv", "3");

        var (moved, itemDir) = TypeConversionLayout.LayOutAsSeries(_targetRoot, "Trilogy", null, new List<string> { a, b, c });

        var seasonDir = Path.Combine(itemDir, "Season 01");
        Assert.Equal(Path.Combine(seasonDir, "Trilogy - S01E01.mkv"), moved[0]);
        Assert.Equal(Path.Combine(seasonDir, "Trilogy - S01E02.mkv"), moved[1]);
        Assert.Equal(Path.Combine(seasonDir, "Trilogy - S01E03.mkv"), moved[2]);
    }

    [Fact]
    public void Title_with_path_separators_is_sanitized_into_a_single_folder()
    {
        var src = MakeSourceFile("x.mkv");

        var (moved, itemDir) = TypeConversionLayout.LayOutAsMovie(_targetRoot, "A/B\\C", 2000, new List<string> { src });

        // The title must not create nested directories: the item dir is a DIRECT child of the root, and
        // its name carries no OS path separator (Sanitize replaces every invalid filename char, which
        // includes the directory separators on every platform).
        Assert.Equal(_targetRoot, Path.GetDirectoryName(itemDir));
        var folderName = Path.GetFileName(itemDir);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, folderName);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, folderName);
        Assert.True(File.Exists(moved[0]));
    }

    [Fact]
    public void Movie_nfo_without_year_omits_year_element()
    {
        var nfo = TypeConversionLayout.BuildMovieNfo("No Year Film", null);
        Assert.Contains("<title>No Year Film</title>", nfo, StringComparison.Ordinal);
        Assert.DoesNotContain("<year>", nfo, StringComparison.Ordinal);
    }

    [Fact]
    public void Nfo_escapes_xml_special_characters_in_title()
    {
        var nfo = TypeConversionLayout.BuildMovieNfo("Tom & Jerry <best>", 1999);
        Assert.Contains("Tom &amp; Jerry &lt;best&gt;", nfo, StringComparison.Ordinal);
        Assert.DoesNotContain("<best>", nfo, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleWithYear_omits_year_when_absent()
    {
        Assert.Equal("Plain", TypeConversionLayout.TitleWithYear("Plain", null));
        Assert.Equal("Dated (2020)", TypeConversionLayout.TitleWithYear("Dated", 2020));
    }

    // ── Convert-to-Other: destination-root precedence (mirrors NaiveMediaPlacer's MediaCategory.Other) ──

    [Fact]
    public void Other_destination_prefers_fallback_root_when_set()
    {
        var (root, error) = TypeConversionLayout.ResolveTargetRoot(
            MediaCategory.Other, seriesRoot: "/tv", movieRoot: "/movies", fallbackRoot: "/other");
        Assert.Null(error);
        Assert.Equal("/other", root);
    }

    [Fact]
    public void Other_destination_falls_back_to_movie_root_when_fallback_empty()
    {
        // This is the placer's exact precedence: Other with no fallback ⇒ movie root.
        var (root, error) = TypeConversionLayout.ResolveTargetRoot(
            MediaCategory.Other, seriesRoot: "/tv", movieRoot: "/movies", fallbackRoot: "");
        Assert.Null(error);
        Assert.Equal("/movies", root);
    }

    [Fact]
    public void Other_destination_errors_when_neither_fallback_nor_movie_configured()
    {
        var (root, error) = TypeConversionLayout.ResolveTargetRoot(
            MediaCategory.Other, seriesRoot: "/tv", movieRoot: "", fallbackRoot: "");
        Assert.Null(root);
        Assert.NotNull(error);
        Assert.Contains("fallback", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Movie_and_Series_destinations_error_when_their_root_unset()
    {
        Assert.NotNull(TypeConversionLayout.ResolveTargetRoot(MediaCategory.Movie, "/tv", "", "/other").Error);
        Assert.NotNull(TypeConversionLayout.ResolveTargetRoot(MediaCategory.Series, "", "/movies", "/other").Error);
    }

    // ── Convert-to-Other: the same-root no-op guard core (SameRoot / PathIsUnder) ──

    [Fact]
    public void SameRoot_is_true_for_identical_and_trailing_slash_variants()
    {
        Assert.True(TypeConversionLayout.SameRoot("/media/movies", "/media/movies/"));
        Assert.True(TypeConversionLayout.SameRoot("/media/movies/", "/media/movies"));
    }

    [Fact]
    public void SameRoot_is_false_for_distinct_roots()
    {
        Assert.False(TypeConversionLayout.SameRoot("/media/movies", "/media/other"));
    }

    [Fact]
    public void Other_with_empty_fallback_on_a_movie_is_detected_as_a_no_op()
    {
        // The exact misleading case: item already under the movie root, Other resolves (empty fallback)
        // back to the movie root ⇒ SameRoot ⇒ the service rejects with a 400 rather than move+rescan.
        var (otherRoot, _) = TypeConversionLayout.ResolveTargetRoot(
            MediaCategory.Other, seriesRoot: "/tv", movieRoot: "/movies", fallbackRoot: "");
        var itemPath = "/movies/Some Film (2021)/Some Film (2021).mkv";
        var currentRootIsMovie = TypeConversionLayout.PathIsUnder(itemPath, "/movies");

        Assert.True(currentRootIsMovie);
        Assert.True(TypeConversionLayout.SameRoot("/movies", otherRoot!));
    }

    [Fact]
    public void Other_with_distinct_fallback_is_not_a_no_op_for_a_movie()
    {
        var (otherRoot, _) = TypeConversionLayout.ResolveTargetRoot(
            MediaCategory.Other, seriesRoot: "/tv", movieRoot: "/movies", fallbackRoot: "/home-videos");
        Assert.False(TypeConversionLayout.SameRoot("/movies", otherRoot!));
    }

    [Fact]
    public void PathIsUnder_matches_nested_paths_but_not_siblings()
    {
        Assert.True(TypeConversionLayout.PathIsUnder("/media/movies/Film/Film.mkv", "/media/movies"));
        Assert.True(TypeConversionLayout.PathIsUnder("/media/movies", "/media/movies"));
        Assert.False(TypeConversionLayout.PathIsUnder("/media/movies-extra/x.mkv", "/media/movies"));
        Assert.False(TypeConversionLayout.PathIsUnder("/media/tv/Show/x.mkv", "/media/movies"));
    }

    [Fact]
    public void Convert_to_Other_uses_the_titled_movie_layout_only_the_root_differs()
    {
        // MediaOrganizer's Other layout == Movie layout ({Title (Year)}/… + <movie> NFO); the service
        // routes Other through LayOutAsMovie into the fallback root. Prove the layout shape here.
        var src = MakeSourceFile("clip.mp4");
        var fallbackRoot = Path.Combine(_work, "home-videos");
        Directory.CreateDirectory(fallbackRoot);

        var (moved, itemDir) = TypeConversionLayout.LayOutAsMovie(fallbackRoot, "My Clip", 2024, new List<string> { src });

        Assert.Equal(Path.Combine(fallbackRoot, "My Clip (2024)"), itemDir);
        Assert.Equal(Path.Combine(itemDir, "My Clip (2024).mp4"), moved[0]);
        Assert.True(File.Exists(Path.Combine(itemDir, "My Clip (2024).nfo")));
    }
}
