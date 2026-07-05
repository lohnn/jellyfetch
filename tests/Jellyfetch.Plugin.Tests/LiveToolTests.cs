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

            // The reported bug: the title must be a real name, never the raw URL or "Untitled".
            Assert.NotEqual("Untitled", meta.Title);
            Assert.DoesNotContain("http", meta.Title, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// End-to-end proof of the film-vs-series fix against the real binary: a standalone SVT
    /// film (title-less <c>&lt;episodedetails&gt;</c> — <c>&lt;showtitle&gt;</c> present, no
    /// <c>&lt;title&gt;</c>/<c>&lt;episode&gt;</c>) must classify as <see cref="MediaCategory.Movie"/>
    /// with its program name as the title, NOT "Untitled" and NOT a series. Uses the SVT
    /// "Filmer" category to find a currently-valid film so it doesn't pin a volatile id.
    /// </summary>
    [SkippableFact]
    public async Task SvtPlayDl_standalone_film_classifies_as_movie_with_real_title()
    {
        Skip.IfNot(Live, "set JELLYFETCH_LIVE=1 to run live tool tests");
        var runner = new ProcessRunner();

        // Pull a real film URL from the Filmer category (avoids pinning a volatile video id).
        var listRes = await runner.RunAsync(
            SvtPlayDl,
            SvtPlayDlIntrospector.EpisodeListArgs("https://www.svtplay.se/kategori/filmer"),
            CancellationToken.None);
        var list = SvtPlayDlIntrospector.ClassifyProgram(listRes.StdErr);
        Skip.If(list.Failed || list.Entries.Count == 0, "svtplay-dl found no films in the Filmer category");
        var filmUrl = list.Entries[0].Url;

        var probeDir = Path.Combine(Path.GetTempPath(), "jf-film-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDir);
        try
        {
            await runner.RunAsync(
                SvtPlayDl,
                SvtPlayDlIntrospector.NfoProbeArgs(filmUrl, probeDir),
                CancellationToken.None);

            var nfo = Directory.EnumerateFiles(probeDir, "*.nfo", SearchOption.AllDirectories)
                .FirstOrDefault(f => !Path.GetFileName(f).Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase));
            Skip.If(nfo is null, "no episode NFO produced for the picked film");

            var meta = SvtPlayDlIntrospector.ParseEpisodeNfo(File.ReadAllText(nfo!));

            // A film category page can also surface series; only assert the movie contract when
            // svtplay-dl actually gave us a film-shaped NFO (no episode number).
            Skip.If(meta.EpisodeNumber is not null, "picked entry was an episode, not a standalone film");

            Assert.Equal(MediaCategory.Movie, meta.Category);
            Assert.False(string.IsNullOrWhiteSpace(meta.Title));
            Assert.NotEqual("Untitled", meta.Title);
            Assert.DoesNotContain("http", meta.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Null(meta.SeriesName);

            // Follow-up fix: the sidecar the handler emits for a film must be a <movie> NFO
            // (not svtplay-dl's <episodedetails>), carrying the probe's plot/aired verbatim.
            // This mirrors WebMediaDownloadHandler.LayOut's movie branch.
            var (plot, aired) = SvtPlayDlIntrospector.ReadNfoExtras(File.ReadAllText(nfo!));
            var movieNfo = new MediaOrganizer().BuildMovieNfo(meta, plot, aired);
            var movieRoot = System.Xml.Linq.XDocument.Parse(movieNfo).Root!;
            Assert.Equal("movie", movieRoot.Name.LocalName);
            Assert.Equal(meta.Title, movieRoot.Element("title")!.Value);
            // svtplay-dl gives films a plot; confirm it survived the re-root (when present).
            if (!string.IsNullOrWhiteSpace(plot))
            {
                Assert.Equal(plot, movieRoot.Element("plot")!.Value);
            }
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
