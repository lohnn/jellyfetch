using System;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// End-to-end proof that the per-episode metadata the app renders (SeriesName / Season /
    /// Episode / Title) actually flows out of the real svtplay-dl <c>--nfo</c> probe. Mirrors
    /// the exact production path in <c>WebMediaDownloadHandler.ResolveMetadataAsync</c>:
    /// <c>NfoProbeArgs</c> → real binary → pick the non-tvshow .nfo → <c>ParseEpisodeNfo</c>.
    /// These are the fields jellyfin-plugin copies onto DownloadJob/JobDto at completion, so if
    /// any were dropped the SVT episode-labelling feature would silently break.
    /// </summary>
    [SkippableFact]
    public async Task SvtPlayDl_episode_probe_fills_structured_metadata()
    {
        Skip.IfNot(Live, "set JELLYFETCH_LIVE=1 to run live tool tests");
        var runner = new ProcessRunner();

        // Resolve a concrete episode URL from the program (avoids pinning a volatile id).
        var listRes = await runner.RunAsync(
            SvtPlayDl,
            SvtPlayDlIntrospector.EpisodeListArgs("https://www.svtplay.se/bortom-bilden"),
            CancellationToken.None);
        var list = SvtPlayDlIntrospector.ClassifyProgram(listRes.StdErr);
        Skip.If(list.Failed || list.Entries.Count == 0, "svtplay-dl found no episodes for this show");
        var episodeUrl = list.Entries[0].Url;

        var probeDir = Path.Combine(Path.GetTempPath(), "jf-nfo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDir);
        try
        {
            await runner.RunAsync(
                SvtPlayDl,
                SvtPlayDlIntrospector.NfoProbeArgs(episodeUrl, probeDir),
                CancellationToken.None);

            // Same selection rule as WebMediaDownloadHandler.FirstNfo: the episodedetails NFO,
            // not the series-level tvshow.nfo.
            var nfo = Directory.EnumerateFiles(probeDir, "*.nfo", SearchOption.AllDirectories)
                .FirstOrDefault(f => !Path.GetFileName(f).Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(nfo);

            var meta = SvtPlayDlIntrospector.ParseEpisodeNfo(File.ReadAllText(nfo!));

            // These four are exactly what jellyfin-plugin maps to Series/Season/Episode/EpisodeTitle.
            Assert.Equal(MediaCategory.Series, meta.Category);
            Assert.False(string.IsNullOrWhiteSpace(meta.SeriesName));   // -> SeriesName
            Assert.NotNull(meta.SeasonNumber);                          // -> SeasonNumber (SVT: season index or year, preserved verbatim)
            Assert.NotNull(meta.EpisodeNumber);                         // -> EpisodeNumber (real ordinal, not the slug-derived provisional)
            Assert.False(string.IsNullOrWhiteSpace(meta.Title));        // -> EpisodeTitle
        }
        finally
        {
            try
            {
                Directory.Delete(probeDir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
