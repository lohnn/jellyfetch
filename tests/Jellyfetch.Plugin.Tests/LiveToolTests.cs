using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Download.WebMedia;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// Integration tests that shell out to the real yt-dlp / svtplay-dl binaries.
/// Skipped unless JELLYFETCH_LIVE=1 (they need network + installed tools), so the
/// normal unit run stays hermetic. Run with:
///   JELLYFETCH_LIVE=1 dotnet test
/// These validate that the pinned arg vectors and parsers still match real tool
/// output — the thing unit tests with canned fixtures cannot catch when a tool
/// updates its format.
/// </summary>
public class LiveToolTests
{
    private static bool Live => Environment.GetEnvironmentVariable("JELLYFETCH_LIVE") == "1";

    private static string YtDlp => Environment.GetEnvironmentVariable("YTDLP_PATH") ?? "yt-dlp";

    private static string SvtPlayDl => Environment.GetEnvironmentVariable("SVTPLAYDL_PATH") ?? "svtplay-dl";

    [SkippableFact]
    public async Task YtDlp_classifies_a_real_single_video()
    {
        Skip.IfNot(Live, "set JELLYFETCH_LIVE=1 to run live tool tests");
        var runner = new ProcessRunner();
        var url = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
        var res = await runner.RunAsync(YtDlp, YtDlpIntrospector.ClassifyArgs(url), CancellationToken.None);
        var c = YtDlpIntrospector.Classify(res.StdOut);
        Assert.False(c.Failed);
        Assert.False(c.IsMultiJob);
    }

    [SkippableFact]
    public async Task YtDlp_expands_a_real_playlist()
    {
        Skip.IfNot(Live, "set JELLYFETCH_LIVE=1 to run live tool tests");
        var runner = new ProcessRunner();
        var url = "https://www.youtube.com/playlist?list=PLBCF2DAC6FFB574DE";
        var res = await runner.RunAsync(YtDlp, YtDlpIntrospector.ClassifyArgs(url), CancellationToken.None);
        var c = YtDlpIntrospector.Classify(res.StdOut);
        Assert.False(c.Failed);
        Assert.True(c.IsMultiJob);
        Assert.True(c.Entries.Count > 1);
    }

    [SkippableFact]
    public async Task SvtPlayDl_lists_program_episodes()
    {
        Skip.IfNot(Live, "set JELLYFETCH_LIVE=1 to run live tool tests");
        var runner = new ProcessRunner();
        var url = "https://www.svtplay.se/bortom-bilden";
        var res = await runner.RunAsync(SvtPlayDl, SvtPlayDlIntrospector.EpisodeListArgs(url), CancellationToken.None);
        var c = SvtPlayDlIntrospector.ClassifyProgram(res.StdErr);
        Assert.False(c.Failed);
        Assert.True(c.Entries.Count >= 1);
    }
}
