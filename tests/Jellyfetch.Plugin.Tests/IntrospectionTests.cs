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

    // ---- Group/parent title for a series LANDING PAGE (the -A fan-out path) ----
    // These cover the two building blocks the group-title resolution composes:
    //   (a) PRIMARY: the first-episode --nfo probe's <showtitle> (åäö-correct series name).
    //   (b) FALLBACK: the landing-page URL slug label when the probe misses.
    // The full ResolveAsync group path spawns svtplay-dl (covered by the live tool test); these
    // assert the pure pieces that decide primary-vs-fallback and that neither is the raw URL.

    [Fact]
    public void Group_primary_series_name_comes_from_showtitle_with_diacritics()
    {
        // The <showtitle> in a real first-episode NFO is the series name with åäö intact — this is
        // exactly the string ResolveSvtSeriesNameAsync returns (via ParseEpisodeNfo.SeriesName).
        var nfo = "<?xml version='1.0' encoding='UTF-8'?>" +
                  "<episodedetails><showtitle>En oväntad förmögenhet</showtitle>" +
                  "<title>Avsnitt 1</title><season>01</season><episode>01</episode>" +
                  "<aired>2026-06-10T02:00:00</aired></episodedetails>";
        var m = SvtPlayDlIntrospector.ParseEpisodeNfo(nfo);
        Assert.Equal("En oväntad förmögenhet", m.SeriesName);
        // Diacritics must survive verbatim — the whole reason we probe instead of slug-folding.
        Assert.Contains("ö", m.SeriesName);
        Assert.Contains("ä", m.SeriesName);
    }

    [Fact]
    public void Group_fallback_uses_landing_page_slug_never_raw_url()
    {
        // When the probe misses, the fallback is the landing-page slug label — ASCII-folded
        // (knowingly lossy) but NEVER the raw URL.
        const string landingUrl = "https://www.svtplay.se/en-ovantad-formogenhet";
        var fallback = SvtPlayDlIntrospector.TitleFromEpisodeUrl(landingUrl);
        Assert.Equal("En Ovantad Formogenhet", fallback);
        Assert.NotEqual(landingUrl, fallback);
        Assert.DoesNotContain("http", fallback!, System.StringComparison.OrdinalIgnoreCase);
    }

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

    [Fact]
    public void Standalone_film_nfo_classifies_as_movie_with_showtitle_as_title()
    {
        // Real svtplay-dl 4.191 output for the film "Son" (/video/ja4ERxn/son), verified live:
        // <showtitle> present, NO <title>, NO <season>/<episode>. Was previously "Untitled" +
        // Series; must now be the film name + Movie.
        var nfo = "<?xml version='1.0' encoding='UTF-8'?>" +
                  "<episodedetails><showtitle>Son</showtitle>" +
                  "<plot>Bland betongväggar ... En film av Leona Cauklija från 2025.</plot>" +
                  "<aired>2025-10-26T02:00:00</aired></episodedetails>";
        var m = SvtPlayDlIntrospector.ParseEpisodeNfo(nfo);
        Assert.Equal(MediaCategory.Movie, m.Category);
        Assert.Equal("Son", m.Title);
        Assert.NotEqual("Untitled", m.Title);
        Assert.Null(m.SeriesName);
        Assert.Null(m.EpisodeNumber);
        Assert.Equal(2025, m.Year);
    }

    [Fact]
    public void Episode_without_title_falls_back_to_showtitle_not_untitled()
    {
        // An episode (has <episode>) whose episodename svtplay-dl omitted: the display title
        // must fall back to <showtitle>, never the literal "Untitled".
        var nfo = "<?xml version='1.0' encoding='UTF-8'?>" +
                  "<episodedetails><showtitle>Väder och vind</showtitle>" +
                  "<season>2024</season><episode>3</episode></episodedetails>";
        var m = SvtPlayDlIntrospector.ParseEpisodeNfo(nfo);
        Assert.Equal(MediaCategory.Series, m.Category);
        Assert.Equal("Väder och vind", m.Title);
        Assert.NotEqual("Untitled", m.Title);
        Assert.Equal("Väder och vind", m.SeriesName);
        Assert.Equal(3, m.EpisodeNumber);
        // SVT season carries the year here — preserved verbatim, not "corrected" (I-118).
        Assert.Equal(2024, m.SeasonNumber);
    }

    // Real svtplay-dl 4.191 -A output for "Pojken i grannhuset" (verified live 2026-07):
    // the "Episode N of M" marker counts EMIT position, and 4.191 emits LAST-first, so the
    // marker disagrees with the slug ordinal. The slug is authoritative.
    private const string ReverseEmitProgramStderr = """
    INFO: Episode 1 of 4
    INFO: Url: http://www.svtplay.se/video/jak523q/pojken-i-grannhuset/4-motet
    INFO: Episode 2 of 4
    INFO: Url: http://www.svtplay.se/video/j3dQvmW/pojken-i-grannhuset/3-en-bra-pojke
    INFO: Episode 3 of 4
    INFO: Url: http://www.svtplay.se/video/KR5ZLEb/pojken-i-grannhuset/2-hemligheten
    INFO: Episode 4 of 4
    INFO: Url: http://www.svtplay.se/video/KBM7rJy/pojken-i-grannhuset/1-igenkannandet
    """;

    [Fact]
    public void Slug_ordinal_beats_emit_marker_when_they_disagree()
    {
        // The marker would put "4-motet" first (Episode 1 of 4); the slug ordinal must win so
        // the child ordered first is episode 1 (1-igenkannandet), not episode 4 (4-motet).
        var c = SvtPlayDlIntrospector.ClassifyProgram(ReverseEmitProgramStderr);
        Assert.Equal(4, c.Entries.Count);
        Assert.EndsWith("1-igenkannandet", c.Entries[0].Url);
        Assert.Equal(1, c.Entries[0].Ordinal);
        Assert.EndsWith("4-motet", c.Entries[3].Url);
        Assert.Equal(4, c.Entries[3].Ordinal);
    }

    [Theory]
    [InlineData("http://www.svtplay.se/video/A/x/4-motet", 4)]
    [InlineData("http://www.svtplay.se/video/A/x/avsnitt-2", 2)]
    [InlineData("http://www.svtplay.se/video/A/x/del-1-av-6", 1)]
    [InlineData("http://www.svtplay.se/video/A/x/kaarina-kaikkonen", null)]
    [InlineData("http://www.svtplay.se/video/A/son", null)]
    public void SlugOrdinal_extracts_leading_episode_number(string url, int? expected)
        => Assert.Equal(expected, SvtPlayDlIntrospector.SlugOrdinal(url));

    [Fact]
    public void Ascending_marker_still_orders_when_slugs_lack_numbers()
    {
        // Named singles with no slug ordinal: fall back to the marker for ordering/numbering.
        var stderr = "INFO: Episode 1 of 2\n" +
                     "INFO: Url: http://www.svtplay.se/video/A/show/alpha\n" +
                     "INFO: Episode 2 of 2\n" +
                     "INFO: Url: http://www.svtplay.se/video/B/show/beta\n";
        var c = SvtPlayDlIntrospector.ClassifyProgram(stderr);
        Assert.Equal(2, c.Entries.Count);
        Assert.EndsWith("alpha", c.Entries[0].Url);
        Assert.Equal(1, c.Entries[0].Ordinal);
        Assert.EndsWith("beta", c.Entries[1].Url);
        Assert.Equal(2, c.Entries[1].Ordinal);
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

    // --- svtplay-dl live download progress (verified against svtplay-dl 4.191, real SVT download) ---

    [Theory]
    [InlineData("[06/47][==..................] ETA: 0:00:10 | 93 KB/s", 12.77)]
    [InlineData("[01/47][....................] ETA: 0:00:00", 2.13)]
    [InlineData("[47/47][====================] ETA: 0:00:00 | 379 KB/s", 100.0)]
    public void Parses_svtplaydl_segment_percent(string line, double expectedPct)
    {
        var p = ProgressParser.TryParseSvtPlayDlLine(line);
        Assert.NotNull(p);
        Assert.Equal(expectedPct, p!.Percent!.Value, 1);
    }

    [Fact]
    public void Svtplaydl_first_record_has_no_speed()
    {
        // The very first record carries no "| X KB/s" suffix — must parse, speed null, not crash.
        var p = ProgressParser.TryParseSvtPlayDlLine("[01/47][....................] ETA: 0:00:00");
        Assert.NotNull(p);
        Assert.Null(p!.SpeedBytesPerSecond);
        Assert.Equal(0, p.EtaSeconds);
    }

    [Fact]
    public void Svtplaydl_speed_converted_to_bytes_per_second()
    {
        var kb = ProgressParser.TryParseSvtPlayDlLine("[10/30][======..............] ETA: 0:00:05 | 170 KB/s");
        Assert.Equal(170L * 1024, kb!.SpeedBytesPerSecond);

        var mb = ProgressParser.TryParseSvtPlayDlLine("[10/30][======..............] ETA: 0:00:05 | 1.5 MB/s");
        Assert.Equal((long)(1.5 * 1024 * 1024), mb!.SpeedBytesPerSecond);
    }

    [Fact]
    public void Svtplaydl_eta_hms_converted_to_seconds()
    {
        var p = ProgressParser.TryParseSvtPlayDlLine("[04/30][==..................] ETA: 0:02:12 | 610 KB/s");
        Assert.Equal(132, p!.EtaSeconds); // 2*60 + 12
    }

    [Fact]
    public void Svtplaydl_progress_is_never_globally_finished()
    {
        // MM/MM ends a PHASE (video, then audio), not the whole download — Finished must stay false.
        var p = ProgressParser.TryParseSvtPlayDlLine("[30/30][====================] ETA: 0:00:00 | 379 KB/s");
        Assert.False(p!.Finished);
    }

    [Fact]
    public void Svtplaydl_ignores_info_and_ytdlp_lines()
    {
        Assert.Null(ProgressParser.TryParseSvtPlayDlLine("INFO: Selected to download hls, bitrate: 6131 format: h264"));
        Assert.Null(ProgressParser.TryParseSvtPlayDlLine("INFO: Outfile: video.ts"));
        Assert.Null(ProgressParser.TryParseSvtPlayDlLine("PROG|downloading|50|100|NA|NA|NA"));
        Assert.Null(ProgressParser.TryParseSvtPlayDlLine(""));
    }

    [Fact]
    public void Ytdlp_parser_rejects_svtplaydl_lines()
    {
        // Guards against the regression this bug WAS: routing svtplay-dl output through the yt-dlp parser.
        Assert.Null(ProgressParser.TryParseYtDlpLine("[06/47][==..................] ETA: 0:00:10 | 93 KB/s"));
    }
}
