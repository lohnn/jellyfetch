using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Download.Torrents;
using Xunit;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// Unit tests for the pure season-pack → per-episode child mapping. No torrent, no filesystem:
/// the builder is deterministic over (parsed pack, video file paths).
/// </summary>
public class SeasonPackChildBuilderTests
{
    private static string Rel(params string[] parts) => Path.Combine(parts);

    private static ParsedRelease Pack(string title, int? season = null, int? year = null) => new()
    {
        Kind = ReleaseKind.SeasonPack,
        Title = title,
        Season = season,
        Year = year,
        Episodes = System.Array.Empty<int>(),
        Confidence = 0.8,
        Source = title,
    };

    // ---- Core: a real season pack fans out into one spec per episode --------

    [Fact]
    public void SeasonPack_produces_one_child_per_episode_with_correct_metadata()
    {
        var pack = Pack("The Wire", season: 1, year: 2002);
        var files = new[]
        {
            "/staging/The.Wire.S01E01.1080p.mkv",
            "/staging/The.Wire.S01E02.1080p.mkv",
            "/staging/The.Wire.S01E03.1080p.mkv",
        };

        var episodes = SeasonPackChildBuilder.Build(pack, files);

        Assert.Equal(3, episodes.Count);

        for (var i = 0; i < 3; i++)
        {
            var e = episodes[i];
            Assert.Equal(files[i], e.SourceFile);
            Assert.Equal(MediaCategory.Series, e.Metadata.Category);
            Assert.Equal("The Wire", e.Metadata.SeriesName);
            Assert.Equal(1, e.Metadata.SeasonNumber);
            Assert.Equal(i + 1, e.Metadata.EpisodeNumber);
            Assert.Equal(2002, e.Metadata.Year);
        }

        // Library-relative layout: {Series}/Season NN/{Series} - SxxEyy.ext
        Assert.Equal(Rel("The Wire", "Season 01", "The Wire - S01E01.mkv"), episodes[0].RelativePath);
        Assert.Equal(Rel("The Wire", "Season 01", "The Wire - S01E02.mkv"), episodes[1].RelativePath);
        Assert.Equal(Rel("The Wire", "Season 01", "The Wire - S01E03.mkv"), episodes[2].RelativePath);
    }

    // ---- Single file: builder still returns one spec (no fan-out is the
    //      manager's job — it only groups when Children.Count > 1) -----------

    [Fact]
    public void Single_episode_file_yields_a_single_spec()
    {
        var pack = Pack("Severance", season: 1);
        var files = new[] { "/staging/Severance.S01E05.2160p.mkv" };

        var episodes = SeasonPackChildBuilder.Build(pack, files);

        Assert.Single(episodes);
        Assert.Equal(5, episodes[0].Metadata.EpisodeNumber);
        Assert.Equal(Rel("Severance", "Season 01", "Severance - S01E05.mkv"), episodes[0].RelativePath);
    }

    [Fact]
    public void Empty_input_yields_no_specs()
    {
        Assert.Empty(SeasonPackChildBuilder.Build(Pack("Whatever"), System.Array.Empty<string>()));
        Assert.Empty(SeasonPackChildBuilder.Build(Pack("Whatever"), null!));
    }

    // ---- Season number: per-file wins, else pack, else default 1 -----------

    [Fact]
    public void Per_file_season_overrides_pack_season()
    {
        // Pack parsed as season 1, but the files are actually season 2 episodes.
        var pack = Pack("Show Name", season: 1);
        var files = new[]
        {
            "/staging/Show.Name.S02E01.mkv",
            "/staging/Show.Name.S02E02.mkv",
        };

        var episodes = SeasonPackChildBuilder.Build(pack, files);

        Assert.All(episodes, e => Assert.Equal(2, e.Metadata.SeasonNumber));
        Assert.Equal(Rel("Show Name", "Season 02", "Show Name - S02E01.mkv"), episodes[0].RelativePath);
    }

    [Fact]
    public void Missing_season_everywhere_defaults_to_one()
    {
        // A file that parses to an episode with no season is unusual; use 1x-form to force season.
        var pack = Pack("Show Name"); // no pack season
        var files = new[] { "/staging/Show.Name.1x04.mkv" };

        var episodes = SeasonPackChildBuilder.Build(pack, files);

        Assert.Single(episodes);
        Assert.Equal(1, episodes[0].Metadata.SeasonNumber);
        Assert.Equal(4, episodes[0].Metadata.EpisodeNumber);
    }

    // ---- Episode number that can't be pinned: keep original name, group it -

    [Fact]
    public void File_without_episode_number_keeps_original_name_under_series_folder()
    {
        var pack = Pack("Planet Earth", season: 1);
        var files = new[]
        {
            "/staging/Planet.Earth.S01E01.mkv",
            "/staging/extras-behind-the-scenes.mkv", // no SxxEyy → can't pin an episode
        };

        var episodes = SeasonPackChildBuilder.Build(pack, files);

        Assert.Equal(2, episodes.Count);
        // The un-pinnable file keeps its own filename directly under the series folder.
        Assert.Equal(Rel("Planet Earth", "extras-behind-the-scenes.mkv"), episodes[1].RelativePath);
        Assert.Null(episodes[1].Metadata.EpisodeNumber);
        // But it still carries the series so it groups with the pack.
        Assert.Equal("Planet Earth", episodes[1].Metadata.SeriesName);
        // Pack season carried onto the unpinned child's metadata (season-folder placement).
        Assert.Equal(1, episodes[1].Metadata.SeasonNumber);
    }

    // ---- Series name comes from the pack even if per-file title is weak ----

    [Fact]
    public void Series_name_defaults_to_pack_title_when_per_file_title_is_low_confidence()
    {
        var pack = Pack("The Office", season: 3);
        // Bare "S03E01.mkv" parses as an episode with an EMPTY title (low series confidence),
        // so the pack's series name must be used.
        var files = new[] { "/staging/S03E01.mkv" };

        var episodes = SeasonPackChildBuilder.Build(pack, files);

        Assert.Single(episodes);
        Assert.Equal("The Office", episodes[0].Metadata.SeriesName);
        Assert.Equal(Rel("The Office", "Season 03", "The Office - S03E01.mkv"), episodes[0].RelativePath);
    }

    [Fact]
    public void Blank_pack_title_falls_back_to_Unknown()
    {
        var pack = Pack("   ", season: 1);
        var files = new[] { "/staging/S01E01.mkv" };

        var episodes = SeasonPackChildBuilder.Build(pack, files);

        Assert.Equal("Unknown", episodes[0].Metadata.SeriesName);
        Assert.Equal(Rel("Unknown", "Season 01", "Unknown - S01E01.mkv"), episodes[0].RelativePath);
    }

    // ---- ToChildren: 1:1 mapping onto the manager's DownloadChild contract --

    [Fact]
    public void ToChildren_maps_relative_path_and_metadata_verbatim()
    {
        var pack = Pack("Fringe", season: 2, year: 2009);
        var files = new[]
        {
            "/staging/Fringe.S02E01.mkv",
            "/staging/Fringe.S02E02.mkv",
        };

        var episodes = SeasonPackChildBuilder.Build(pack, files);
        var children = SeasonPackChildBuilder.ToChildren(episodes);

        Assert.Equal(episodes.Count, children.Count);
        for (var i = 0; i < children.Count; i++)
        {
            Assert.Equal(episodes[i].RelativePath, children[i].RelativePath);
            Assert.Same(episodes[i].Metadata, children[i].Metadata);
            Assert.Equal("Fringe", children[i].Metadata.SeriesName);
            Assert.Equal(2, children[i].Metadata.SeasonNumber);
            Assert.Equal(i + 1, children[i].Metadata.EpisodeNumber);
        }
    }

    [Fact]
    public void ToChildren_on_empty_is_empty()
    {
        Assert.Empty(SeasonPackChildBuilder.ToChildren(System.Array.Empty<SeasonPackEpisode>()));
    }

    // ---- Contract guard: a >1 pack yields >1 children (so the manager fans
    //      out), a 1-file pack yields exactly 1 (so it does NOT). ------------

    [Fact]
    public void Child_count_matches_episode_count_so_manager_grouping_threshold_is_honored()
    {
        var multi = SeasonPackChildBuilder.ToChildren(
            SeasonPackChildBuilder.Build(Pack("Show", 1), new[]
            {
                "/s/Show.S01E01.mkv", "/s/Show.S01E02.mkv",
            }));
        Assert.True(multi.Count > 1, "a real pack must produce >1 children so the manager groups it");

        var single = SeasonPackChildBuilder.ToChildren(
            SeasonPackChildBuilder.Build(Pack("Show", 1), new[] { "/s/Show.S01E01.mkv" }));
        Assert.Single(single); // manager will NOT fan out — stays a single job
    }
}
