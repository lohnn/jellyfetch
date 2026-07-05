using System.Xml.Linq;
using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Download.WebMedia;

namespace Jellyfetch.Plugin.Tests;

public class MediaOrganizerTests
{
    private static readonly MediaOrganizer Organizer = new();

    [Fact]
    public void Episode_uses_series_season_layout()
    {
        var plan = Organizer.Plan(new MediaMetadata
        {
            Category = MediaCategory.Series,
            Title = "The One With Ross",
            SeriesName = "Friends",
            SeasonNumber = 1,
            EpisodeNumber = 2,
        });

        Assert.Equal(MediaCategory.Series, plan.Category);
        Assert.Equal("Friends/Season 01", plan.RelativeDirectory);
        Assert.Equal("Friends S01E02", plan.FileStem);
        Assert.Equal("Friends/Season 01/Friends S01E02.mkv", plan.VideoRelativePath(".mkv"));
        Assert.Equal("Friends/tvshow.nfo", plan.TvShowNfoRelativePath);
    }

    [Fact]
    public void Episode_pads_double_digits()
    {
        var plan = Organizer.Plan(new MediaMetadata
        {
            Category = MediaCategory.Series, Title = "x", SeriesName = "Show",
            SeasonNumber = 12, EpisodeNumber = 34,
        });
        Assert.Equal("Show/Season 12", plan.RelativeDirectory);
        Assert.Equal("Show S12E34", plan.FileStem);
    }

    [Fact]
    public void Episode_defaults_missing_season_to_1_and_episode_to_0()
    {
        var plan = Organizer.Plan(new MediaMetadata
        {
            Category = MediaCategory.Series, Title = "Loose", SeriesName = "Bortom bilden",
        });
        Assert.Equal("Bortom bilden/Season 01", plan.RelativeDirectory);
        Assert.Equal("Bortom bilden S01E00", plan.FileStem);
    }

    [Fact]
    public void Movie_uses_title_year_folder()
    {
        var plan = Organizer.Plan(new MediaMetadata
        {
            Category = MediaCategory.Movie, Title = "Blade Runner", Year = 1982,
        });
        Assert.Equal(MediaCategory.Movie, plan.Category);
        Assert.Equal("Blade Runner (1982)", plan.RelativeDirectory);
        Assert.Equal("Blade Runner (1982)/Blade Runner (1982).mp4", plan.VideoRelativePath("mp4"));
    }

    [Fact]
    public void Movie_without_year_drops_the_parenthetical()
    {
        var plan = Organizer.Plan(new MediaMetadata { Category = MediaCategory.Movie, Title = "Some Home Movie" });
        Assert.Equal("Some Home Movie", plan.RelativeDirectory);
    }

    [Fact]
    public void Svt_film_nfo_flows_end_to_end_to_movie_layout()
    {
        // Integration of the fix: a real title-less SVT film NFO must classify as a movie and
        // land in the movie layout with its program name (åäö preserved), NOT a series folder
        // and NOT "Untitled".
        var nfo = "<?xml version='1.0' encoding='UTF-8'?>" +
                  "<episodedetails><showtitle>Så mycket bättre</showtitle>" +
                  "<aired>2025-10-26T02:00:00</aired></episodedetails>";
        var meta = SvtPlayDlIntrospector.ParseEpisodeNfo(nfo);
        var plan = Organizer.Plan(meta);

        Assert.Equal(MediaCategory.Movie, plan.Category);
        Assert.Equal("Så mycket bättre (2025)", plan.RelativeDirectory);
        Assert.Null(plan.TvShowNfoRelativePath);
        Assert.Equal("movie", XDocument.Parse(Organizer.BuildNfo(meta)).Root!.Name.LocalName);
    }

    [Fact]
    public void Other_uses_title_year_folder()
    {
        var plan = Organizer.Plan(new MediaMetadata { Category = MediaCategory.Other, Title = "Me at the zoo", Year = 2005 });
        Assert.Equal(MediaCategory.Other, plan.Category);
        Assert.Equal("Me at the zoo (2005)", plan.RelativeDirectory);
    }

    [Fact]
    public void Auto_category_is_treated_as_other()
    {
        var plan = Organizer.Plan(new MediaMetadata { Category = MediaCategory.Auto, Title = "clip" });
        Assert.Equal(MediaCategory.Other, plan.Category);
    }

    // ---- Sanitization edge cases ----

    [Fact]
    public void Swedish_characters_survive_intact()
    {
        var plan = Organizer.Plan(new MediaMetadata
        {
            Category = MediaCategory.Series, Title = "x", SeriesName = "Så mycket bättre",
            SeasonNumber = 3, EpisodeNumber = 6,
        });
        Assert.Equal("Så mycket bättre/Season 03", plan.RelativeDirectory);
        Assert.Equal("Så mycket bättre S03E06", plan.FileStem);
    }

    [Fact]
    public void Colon_in_title_is_removed()
    {
        var plan = Organizer.Plan(new MediaMetadata { Category = MediaCategory.Movie, Title = "Mission: Impossible", Year = 1996 });
        Assert.DoesNotContain(":", plan.RelativeDirectory);
        Assert.Equal("Mission Impossible (1996)", plan.RelativeDirectory);
    }

    [Theory]
    [InlineData("a/b\\c", "a b c")]
    [InlineData("what? really*", "what really")]
    [InlineData("pipe|name", "pipe name")]
    [InlineData("quote\"name", "quote name")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("   spaced   out   ", "spaced out")]
    public void Sanitizer_strips_hostile_chars(string input, string expected)
        => Assert.Equal(expected, FilenameSanitizer.Sanitize(input));

    [Fact]
    public void Sanitizer_keeps_swedish_letters()
    {
        var s = FilenameSanitizer.Sanitize("Åäö ÅÄÖ é ñ");
        Assert.Equal("Åäö ÅÄÖ é ñ", s);
        Assert.True(FilenameSanitizer.IsSafe(s));
    }

    [Fact]
    public void Sanitizer_falls_back_for_empty_input()
    {
        Assert.Equal("Unknown", FilenameSanitizer.Sanitize("   "));
        Assert.Equal("Untitled", FilenameSanitizer.Sanitize("///", "Untitled"));
    }

    [Fact]
    public void Sanitizer_escapes_reserved_device_names()
    {
        Assert.Equal("_CON", FilenameSanitizer.Sanitize("CON"));
        Assert.Equal("_nul.txt", FilenameSanitizer.Sanitize("nul.txt"));
    }

    // ---- Subtitle / NFO placement ----

    [Fact]
    public void Subtitle_gets_language_suffix()
    {
        var plan = Organizer.Plan(new MediaMetadata
        {
            Category = MediaCategory.Series, Title = "x", SeriesName = "Show",
            SeasonNumber = 1, EpisodeNumber = 1,
        });
        Assert.Equal("Show/Season 01/Show S01E01.sv.srt", plan.SubtitleRelativePath("sv", ".srt"));
        Assert.Equal("Show/Season 01/Show S01E01.sv.forced.srt", plan.SubtitleRelativePath("sv-forced", ".srt"));
        Assert.Equal("Show/Season 01/Show S01E01.nfo", plan.NfoRelativePath());
    }

    [Fact]
    public void Episode_nfo_is_wellformed_with_swedish_content()
    {
        var nfo = Organizer.BuildNfo(new MediaMetadata
        {
            Category = MediaCategory.Series,
            Title = "Kaarina Kaikkonen - Sorgen är mitt hem",
            SeriesName = "Bortom bilden",
            SeasonNumber = 3,
            EpisodeNumber = 6,
        });
        Assert.Contains("<showtitle>Bortom bilden</showtitle>", nfo);
        Assert.Contains("<season>3</season>", nfo);
        Assert.Contains("<episode>6</episode>", nfo);
        Assert.Contains("Sorgen är mitt hem", nfo);
        Assert.Equal("episodedetails", XDocument.Parse(nfo).Root!.Name.LocalName);
    }

    [Fact]
    public void Movie_nfo_escapes_ampersand_and_is_valid_xml()
    {
        var nfo = Organizer.BuildNfo(new MediaMetadata { Category = MediaCategory.Movie, Title = "Tom & Jerry", Year = 2021 });
        Assert.Contains("Tom &amp; Jerry", nfo);
        Assert.Equal("movie", XDocument.Parse(nfo).Root!.Name.LocalName);
    }

    [Fact]
    public void Movie_nfo_carries_over_plot_and_aired_verbatim()
    {
        // The follow-up fix: a standalone SVT film re-rooted from <episodedetails> to <movie> must
        // NOT lose the plot/aired svtplay-dl provided. Simulate the real "Son" probe NFO fields.
        var plot = "Bland betongväggar ... En film av Leona Cauklija från 2025.";
        var aired = "2025-10-26T02:00:00";
        var nfo = Organizer.BuildMovieNfo(
            new MediaMetadata { Category = MediaCategory.Movie, Title = "Son", Year = 2025 },
            plot,
            aired);

        var root = XDocument.Parse(nfo).Root!;
        Assert.Equal("movie", root.Name.LocalName);
        Assert.Equal("Son", root.Element("title")!.Value);
        Assert.Equal("2025", root.Element("year")!.Value);
        // Plot carried verbatim (åäö intact).
        Assert.Equal(plot, root.Element("plot")!.Value);
        Assert.Contains("från 2025", root.Element("plot")!.Value);
        // Aired carried verbatim, emitted as <premiered> (movie tag) + <aired> alias.
        Assert.Equal(aired, root.Element("premiered")!.Value);
        Assert.Equal(aired, root.Element("aired")!.Value);
    }

    [Fact]
    public void Movie_nfo_without_extras_is_barebones_but_valid()
    {
        var nfo = Organizer.BuildMovieNfo(
            new MediaMetadata { Category = MediaCategory.Movie, Title = "Home Video" },
            plot: null,
            aired: null);
        var root = XDocument.Parse(nfo).Root!;
        Assert.Equal("movie", root.Name.LocalName);
        Assert.Equal("Home Video", root.Element("title")!.Value);
        Assert.Null(root.Element("plot"));
        Assert.Null(root.Element("premiered"));
    }

    [Fact]
    public void Series_episode_nfo_stays_episodedetails_regression_guard()
    {
        // Regression guard for the follow-up: the movie-NFO change must NOT touch the episode path.
        var nfo = Organizer.BuildNfo(new MediaMetadata
        {
            Category = MediaCategory.Series,
            Title = "Igenkännandet",
            SeriesName = "Pojken i grannhuset",
            SeasonNumber = 1,
            EpisodeNumber = 1,
        });
        Assert.Equal("episodedetails", XDocument.Parse(nfo).Root!.Name.LocalName);
    }

    [Fact]
    public void ReadNfoExtras_pulls_plot_and_aired_from_episodedetails()
    {
        var nfo = "<?xml version='1.0' encoding='UTF-8'?>" +
                  "<episodedetails><showtitle>Son</showtitle>" +
                  "<plot>Bland betongväggar från 2025.</plot>" +
                  "<aired>2025-10-26T02:00:00</aired></episodedetails>";
        var (plot, aired) = SvtPlayDlIntrospector.ReadNfoExtras(nfo);
        Assert.Equal("Bland betongväggar från 2025.", plot);
        Assert.Equal("2025-10-26T02:00:00", aired);
    }

    [Fact]
    public void ReadNfoExtras_is_null_safe_on_missing_fields_and_bad_xml()
    {
        var (p1, a1) = SvtPlayDlIntrospector.ReadNfoExtras("<episodedetails><title>x</title></episodedetails>");
        Assert.Null(p1);
        Assert.Null(a1);

        var (p2, a2) = SvtPlayDlIntrospector.ReadNfoExtras("not xml at all");
        Assert.Null(p2);
        Assert.Null(a2);
    }

    [Theory]
    [InlineData("video.sv", "sv")]
    [InlineData("video.sv-forced", "sv-forced")]
    [InlineData("video.en", "en")]
    [InlineData("video", "und")]
    [InlineData("video.12345", "und")]
    public void Subtitle_language_extracted_from_filename(string stem, string expected)
        => Assert.Equal(expected, WebMediaDownloadHandler.SubtitleLanguageFromFileName(stem));
}
