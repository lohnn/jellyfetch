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
    /// THE regression test for "SVT Play shows no download progress". This exercises the exact
    /// production I/O path — real <see cref="ProcessRunner.StreamAsync"/> streaming a REAL svtplay-dl
    /// download, its stderr lines fed through the REAL <see cref="ProgressParser.TryParseSvtPlayDlLine"/>
    /// — and asserts that MULTIPLE, INCREASING progress snapshots arrive WHILE the download runs.
    ///
    /// Two independent bugs this guards against (both verified against svtplay-dl 4.191):
    ///   (1) svtplay-dl progress was parsed with the yt-dlp parser (never matched → zero progress).
    ///   (2) svtplay-dl emits CR-terminated (\r) progress records; the old ReadLineAsync pump only
    ///       split on \n, so the whole progress stream collapsed into ONE line delivered at process
    ///       exit — no LIVE progress. If the pump ever regresses to ReadLineAsync, the "&gt;= 2 distinct
    ///       snapshots mid-download" assertion below fails even with a correct parser.
    ///
    /// W-057: prior to this the real-download progress path had NO hermetic-ish coverage — only the
    /// pure parser was unit-tested, never the pump+parser+real-binary chain.
    /// </summary>
    [SkippableFact]
    public async Task SvtPlayDl_reports_live_progress_during_a_real_download()
    {
        Skip.IfNot(Live, "set JELLYFETCH_LIVE=1 to run live tool tests");
        var runner = new ProcessRunner();

        // Resolve a currently-valid episode URL rather than pinning a volatile video id.
        var listRes = await runner.RunAsync(
            SvtPlayDl,
            SvtPlayDlIntrospector.EpisodeListArgs("https://www.svtplay.se/rapport"),
            CancellationToken.None);
        var list = SvtPlayDlIntrospector.ClassifyProgram(listRes.StdErr);
        Skip.If(list.Failed || list.Entries.Count == 0, "svtplay-dl found no episodes to download");
        var episodeUrl = list.Entries[0].Url;

        var workDir = Path.Combine(Path.GetTempPath(), "jf-prog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        // Mirror the handler's svtplay-dl download arg vector.
        var args = new[] { "-S", "--nfo", "-o", workDir, "--filename", "video.{ext}", episodeUrl };

        var snapshots = new System.Collections.Concurrent.ConcurrentQueue<double>();

        // Cancel once we've SEEN enough live updates — we don't need the whole file, only proof
        // that progress streams incrementally. A hard cap keeps the test bounded if it never does.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await runner.StreamAsync(
                SvtPlayDl,
                args,
                onStdout: _ => { },
                onStderr: line =>
                {
                    var p = ProgressParser.TryParseSvtPlayDlLine(line);
                    if (p?.Percent is double pct)
                    {
                        snapshots.Enqueue(pct);

                        // Two distinct live updates is all the proof we need; stop the download.
                        if (snapshots.Distinct().Count() >= 3)
                        {
                            cts.Cancel();
                        }
                    }
                },
                stallTimeout: TimeSpan.FromMinutes(2),
                cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: we cancel once enough live progress has been observed (or on the time cap).
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }

        // The core assertion: progress arrived LIVE and INCREMENTALLY (not one lump at exit).
        var seen = snapshots.ToArray();
        Assert.True(
            seen.Distinct().Count() >= 2,
            $"expected >= 2 distinct live progress snapshots during the download, saw {seen.Length} " +
            $"total / {seen.Distinct().Count()} distinct: [{string.Join(", ", seen)}]");
        Assert.All(seen, pct => Assert.InRange(pct, 0, 100));
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

/// <summary>
/// Live integration for the series LANDING-PAGE group-title fix: submitting a program URL (no
/// /video/ segment) through the REAL <see cref="WebMediaDownloadHandler.ResolveAsync"/> must yield a
/// group whose <see cref="ResolveResult.GroupTitle"/> is the real series name (åäö intact), NOT the
/// raw URL and NOT the ASCII-folded slug. Exercises the actual handler (config → installed
/// svtplay-dl), so it's in the PluginState collection (needs Plugin.Instance for the tool path).
/// </summary>
[Collection("PluginState")]
public class LiveGroupTitleTests
{
    private static bool Live => Environment.GetEnvironmentVariable("JELLYFETCH_LIVE") == "1";

    private static string SvtPlayDl => Environment.GetEnvironmentVariable("SVTPLAYDL_PATH") ?? "svtplay-dl";

    [SkippableFact]
    public async Task Series_landing_page_group_title_is_real_series_name_not_url()
    {
        Skip.IfNot(Live, "set JELLYFETCH_LIVE=1 to run live tool tests");

        var tempRoot = Path.Combine(Path.GetTempPath(), "jf-grp-live-" + Guid.NewGuid().ToString("N"));
        using var scope = new PluginConfigScope(tempRoot);
        scope.Configuration.SvtPlayDlPath = SvtPlayDl;

        var handler = new WebMediaDownloadHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WebMediaDownloadHandler>.Instance);

        const string landingUrl = "https://www.svtplay.se/en-ovantad-formogenhet";
        var result = await handler.ResolveAsync(
            new DownloadRequest { SourceUrl = landingUrl },
            CancellationToken.None);

        // It must have fanned out into per-episode items (the group path).
        Skip.If(result.Items.Count <= 1, "landing page did not expand to multiple episodes (show may have changed)");

        // The parent group title must be the real series name, with diacritics — from the
        // first-episode --nfo <showtitle> probe, NOT the raw URL, NOT the ASCII slug.
        Assert.False(string.IsNullOrWhiteSpace(result.GroupTitle));
        Assert.NotEqual(landingUrl, result.GroupTitle);
        Assert.DoesNotContain("http", result.GroupTitle!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("En oväntad förmögenhet", result.GroupTitle);
        Assert.Contains("ö", result.GroupTitle!); // åäö survived (not slug-folded to "Formogenhet")

        try
        {
            Directory.Delete(tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
