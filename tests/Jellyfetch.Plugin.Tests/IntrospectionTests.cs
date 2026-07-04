using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Download.WebMedia;

namespace Jellyfetch.Plugin.Tests;

public class ToolRouterTests
{
    private readonly ToolRouter _router = new();

    [Theory]
    [InlineData("https://www.svtplay.se/video/abc/slug", true)]
    [InlineData("https://svtplay.se/skattjakt", true)]
    [InlineData("https://www.svt.se/nyheter/x", true)]
    [InlineData("https://www.youtube.com/watch?v=x", false)]
    [InlineData("https://vimeo.com/12345", false)]
    [InlineData("not a url", false)]
    public void Routes_by_host(string url, bool expectSvt)
    {
        var expected = expectSvt ? DownloadTool.SvtPlayDl : DownloadTool.YtDlp;
        Assert.Equal(expected, _router.Route(url));
    }

    [Fact]
    public void Empty_overrides_reproduce_defaults()
    {
        // Null and empty override lists must behave exactly like the no-arg defaults.
        Assert.Equal(DownloadTool.SvtPlayDl, _router.Route("https://www.svtplay.se/video/x", null));
        Assert.Equal(DownloadTool.SvtPlayDl, _router.Route("https://www.svtplay.se/video/x", System.Array.Empty<string>()));
        Assert.Equal(DownloadTool.YtDlp, _router.Route("https://vimeo.com/12345", System.Array.Empty<string>()));
    }

    [Fact]
    public void Override_takes_precedence_over_default()
    {
        // Force an SVT host to yt-dlp — override wins over the built-in svtplay-dl default.
        var overrides = new[] { "svtplay.se=yt-dlp" };
        Assert.Equal(DownloadTool.YtDlp, _router.Route("https://www.svtplay.se/video/x", overrides));
    }

    [Fact]
    public void Override_adds_new_domain_routing()
    {
        // A domain with no built-in default can be pinned to svtplay-dl via config.
        var overrides = new[] { "vimeo.com=svtplay-dl" };
        Assert.Equal(DownloadTool.SvtPlayDl, _router.Route("https://vimeo.com/12345", overrides));
    }

    [Fact]
    public void Unknown_domain_still_falls_back_to_default_tool()
    {
        // An override map that doesn't mention the host leaves the defaults intact.
        var overrides = new[] { "example.com=svtplay-dl" };
        Assert.Equal(DownloadTool.YtDlp, _router.Route("https://vimeo.com/12345", overrides));
        Assert.Equal(DownloadTool.SvtPlayDl, _router.Route("https://www.svtplay.se/video/x", overrides));
    }

    [Fact]
    public void Override_matches_subdomains_like_defaults()
    {
        var overrides = new[] { "example.com=svtplay-dl" };
        Assert.Equal(DownloadTool.SvtPlayDl, _router.Route("https://cdn.example.com/v/1", overrides));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-equals-sign")]
    [InlineData("vimeo.com=")]
    [InlineData("=yt-dlp")]
    [InlineData("vimeo.com=bogustool")]
    public void Malformed_override_lines_are_ignored(string line)
    {
        // Defensive parsing: junk config lines are skipped, defaults survive.
        var overrides = new[] { line };
        Assert.Equal(DownloadTool.YtDlp, _router.Route("https://vimeo.com/12345", overrides));
        Assert.Equal(DownloadTool.SvtPlayDl, _router.Route("https://www.svtplay.se/video/x", overrides));
    }

    [Fact]
    public void Tool_tokens_are_lenient_and_trimmed()
    {
        Assert.Equal(DownloadTool.YtDlp, _router.Route("https://vimeo.com/12345", new[] { "vimeo.com=ytdlp" }));
        Assert.Equal(DownloadTool.SvtPlayDl, _router.Route("https://vimeo.com/12345", new[] { "vimeo.com = svtplay " }));
        Assert.Equal(DownloadTool.SvtPlayDl, _router.Route("https://vimeo.com/12345", new[] { "vimeo.com=SVTPlay-DL" }));
    }
}

public class YtDlpIntrospectorTests
{
    [Fact]
    public void Classifies_single_video()
    {
        var json = """{"_type":"video","id":"x","title":"Me at the zoo"}""";
        var c = YtDlpIntrospector.Classify(json);
        Assert.False(c.IsMultiJob);
        Assert.False(c.Failed);
        Assert.Equal("Me at the zoo", c.ContainerTitle);
    }

    [Fact]
    public void Classifies_playlist_and_expands_entries_in_order()
    {
        var json = """
        {"_type":"playlist","title":"Google Search Stories",
         "entries":[
           {"_type":"url","url":"https://youtu.be/a","title":"First"},
           {"_type":"url","url":"https://youtu.be/b","title":"Second"}
         ]}
        """;
        var c = YtDlpIntrospector.Classify(json);
        Assert.True(c.IsMultiJob);
        Assert.Equal(2, c.Entries.Count);
        Assert.Equal("https://youtu.be/a", c.Entries[0].Url);
        Assert.Equal(1, c.Entries[0].Ordinal);
        Assert.Equal("Second", c.Entries[1].Title);
    }

    [Fact]
    public void Malformed_json_flags_failed()
    {
        var c = YtDlpIntrospector.Classify("not json");
        Assert.True(c.Failed);
    }

    [Fact]
    public void Youtube_single_without_series_fields_is_other()
    {
        var json = """{"id":"x","title":"Me at the zoo","upload_date":"20050424"}""";
        var m = YtDlpIntrospector.ParseMetadata(json, MediaCategory.Other);
        Assert.Equal(MediaCategory.Other, m.Category);
        Assert.Equal("Me at the zoo", m.Title);
        Assert.Equal(2005, m.Year);
    }

    [Fact]
    public void Content_with_series_and_episode_becomes_series()
    {
        var json = """{"id":"x","title":"Pilot","series":"Some Show","season_number":2,"episode_number":5}""";
        var m = YtDlpIntrospector.ParseMetadata(json, MediaCategory.Other);
        Assert.Equal(MediaCategory.Series, m.Category);
        Assert.Equal("Some Show", m.SeriesName);
        Assert.Equal(2, m.SeasonNumber);
        Assert.Equal(5, m.EpisodeNumber);
    }
}

public class SvtPlayDlIntrospectorTests
{
    // Ground truth: svtplay-dl 4.191 emits episodes ASCENDING, each "Episode N of M" line
    // immediately preceding its "Url:" line (verified against real SVT Play, 2026-07).
    private const string ProgramStderr = """
    INFO: Episode 1 of 3
    INFO: Url: http://www.svtplay.se/video/A/bortom-bilden/avsnitt-1
    INFO: Episode 2 of 3
    INFO: Url: http://www.svtplay.se/video/B/bortom-bilden/avsnitt-2
    INFO: Episode 3 of 3
    INFO: Url: http://www.svtplay.se/video/C/bortom-bilden/avsnitt-3
    """;

    // Older svtplay-dl listed newest-first (descending emit order). The "Episode N of M"
    // marker still labels each url, so ordering by the marker must recover ascending order
    // regardless of emit direction.
    private const string ProgramStderrNewestFirst = """
    INFO: Episode 3 of 3
    INFO: Url: http://www.svtplay.se/video/C/bortom-bilden/avsnitt-3
    INFO: Episode 2 of 3
    INFO: Url: http://www.svtplay.se/video/B/bortom-bilden/avsnitt-2
    INFO: Episode 1 of 3
    INFO: Url: http://www.svtplay.se/video/A/bortom-bilden/avsnitt-1
    """;

    [Fact]
    public void Program_expands_to_ascending_order()
    {
        var c = SvtPlayDlIntrospector.ClassifyProgram(ProgramStderr);
        Assert.True(c.IsMultiJob);
        Assert.Equal(3, c.Entries.Count);
        Assert.EndsWith("avsnitt-1", c.Entries[0].Url);
        Assert.Equal(1, c.Entries[0].Ordinal);
        Assert.EndsWith("avsnitt-3", c.Entries[2].Url);
        Assert.Equal(3, c.Entries[2].Ordinal);
    }

    [Fact]
    public void Episode_marker_orders_regardless_of_emit_direction()
    {
        // Newest-first emit order must still yield ascending ordinals via the marker.
        var c = SvtPlayDlIntrospector.ClassifyProgram(ProgramStderrNewestFirst);
        Assert.Equal(3, c.Entries.Count);
        Assert.EndsWith("avsnitt-1", c.Entries[0].Url);
        Assert.Equal(1, c.Entries[0].Ordinal);
        Assert.EndsWith("avsnitt-3", c.Entries[2].Url);
        Assert.Equal(3, c.Entries[2].Ordinal);
    }

    [Fact]
    public void Child_titles_are_human_readable_not_raw_urls()
    {
        // Case-B fix: queued episodes must render as labels, not URL blobs, before download.
        var c = SvtPlayDlIntrospector.ClassifyProgram(ProgramStderr);
        Assert.Equal("Avsnitt 1", c.Entries[0].Title);
        Assert.Equal("Avsnitt 3", c.Entries[2].Title);
    }

    [Theory]
    [InlineData("http://www.svtplay.se/video/eEq2yVZ/abborrmastarna/avsnitt-2", "Avsnitt 2")]
    [InlineData("https://www.svtplay.se/video/A/bortom-bilden/kaarina-kaikkonen", "Kaarina Kaikkonen")]
    [InlineData("https://www.svtplay.se/video/A/x/del-1-av-6", "Del 1 Av 6")]
    public void TitleFromEpisodeUrl_slug_becomes_label(string url, string expected)
        => Assert.Equal(expected, SvtPlayDlIntrospector.TitleFromEpisodeUrl(url));

    [Fact]
    public void Single_episode_is_not_multijob()
    {
        var c = SvtPlayDlIntrospector.ClassifyProgram("INFO: Url: http://x/video/A/x/avsnitt-1\n");
        Assert.False(c.IsMultiJob);
        Assert.Single(c.Entries);
        Assert.Equal("Avsnitt 1", c.Entries[0].Title);
    }

    [Fact]
    public void No_urls_flags_failed()
        => Assert.True(SvtPlayDlIntrospector.ClassifyProgram("INFO: nothing").Failed);

    [Fact]
    public void Parses_real_svtplaydl_episode_nfo()
    {
        var nfo = "<?xml version='1.0' encoding='UTF-8'?>\n" +
                  "<episodedetails><showtitle>Bortom bilden</showtitle>" +
                  "<title>Kaarina Kaikkonen - Sorgen är mitt hem</title>" +
                  "<season>03</season><episode>06</episode>" +
                  "<plot>Del 6 av 6,</plot>" +
                  "<aired>2026-05-29T02:00:00</aired></episodedetails>";
        var m = SvtPlayDlIntrospector.ParseEpisodeNfo(nfo);
        Assert.Equal(MediaCategory.Series, m.Category);
        Assert.Equal("Bortom bilden", m.SeriesName);
        Assert.Equal("Kaarina Kaikkonen - Sorgen är mitt hem", m.Title);
        Assert.Equal(3, m.SeasonNumber);
        Assert.Equal(6, m.EpisodeNumber);
        Assert.Equal(2026, m.Year);
    }
}

public class ProgressParserTests
{
    [Theory]
    [InlineData("PROG|downloading|261120|629172|NA|2223790.17|0", 41.5, false)]
    [InlineData("PROG|finished|629172|629172|NA|1050838.2|NA", 100.0, true)]
    public void Parses_progress_template_lines(string line, double pct, bool finished)
    {
        var p = ProgressParser.TryParseYtDlpLine(line);
        Assert.NotNull(p);
        Assert.Equal(pct, p!.Percent!.Value, 1);
        Assert.Equal(finished, p.Finished);
    }

    [Fact]
    public void Falls_back_to_estimate_when_total_is_na()
    {
        var p = ProgressParser.TryParseYtDlpLine("PROG|downloading|50|NA|100|NA|NA");
        Assert.Equal(100, p!.TotalBytes);
        Assert.Equal(50, p.Percent!.Value, 1);
    }

    [Fact]
    public void Na_speed_and_eta_are_null()
    {
        var p = ProgressParser.TryParseYtDlpLine("PROG|downloading|1|100|NA|NA|NA");
        Assert.Null(p!.SpeedBytesPerSecond);
        Assert.Null(p.EtaSeconds);
    }

    [Fact]
    public void Non_progress_lines_return_null()
    {
        Assert.Null(ProgressParser.TryParseYtDlpLine("[youtube] Extracting URL"));
        Assert.Null(ProgressParser.TryParseYtDlpLine("WARNING: no js runtime"));
        Assert.Null(ProgressParser.TryParseYtDlpLine(""));
    }
}
