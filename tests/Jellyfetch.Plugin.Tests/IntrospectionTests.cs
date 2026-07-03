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
    private const string ProgramStderr = """
    INFO: Episode 1 of 3
    INFO: Url: http://www.svtplay.se/video/C/bortom-bilden/3-c
    INFO: Episode 2 of 3
    INFO: Url: http://www.svtplay.se/video/B/bortom-bilden/2-b
    INFO: Episode 3 of 3
    INFO: Url: http://www.svtplay.se/video/A/bortom-bilden/1-a
    """;

    [Fact]
    public void Program_expands_to_ascending_order()
    {
        var c = SvtPlayDlIntrospector.ClassifyProgram(ProgramStderr);
        Assert.True(c.IsMultiJob);
        Assert.Equal(3, c.Entries.Count);
        Assert.EndsWith("1-a", c.Entries[0].Url); // reversed from newest-first
        Assert.Equal(1, c.Entries[0].Ordinal);
        Assert.EndsWith("3-c", c.Entries[2].Url);
    }

    [Fact]
    public void Single_episode_is_not_multijob()
    {
        var c = SvtPlayDlIntrospector.ClassifyProgram("INFO: Url: http://x/video/A/x/1-a\n");
        Assert.False(c.IsMultiJob);
        Assert.Single(c.Entries);
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
