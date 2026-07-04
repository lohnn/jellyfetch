using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Jellyfetch.Plugin.Download.WebMedia;

/// <summary>
/// svtplay-dl has no JSON introspection. Parses the two text surfaces used:
///   1. <c>svtplay-dl --get-only-episode-url -A &lt;program&gt;</c> → "INFO: Url: &lt;url&gt;"
///      lines on STDERR, newest-first — reversed here to ascending ordinals.
///   2. <c>svtplay-dl --nfo --force-nfo -o &lt;dir&gt; &lt;episode&gt;</c> → an
///      episodedetails NFO (no video downloaded) parsed for metadata.
///
/// Ground truth (svtplay-dl 4.191): episodedetails carries showtitle/title/season/
/// episode/plot/aired(ISO datetime).
/// </summary>
internal static class SvtPlayDlIntrospector
{
    private static readonly Regex UrlLine =
        new(@"^\s*INFO:\s*Url:\s*(?<url>\S+)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    // "INFO: Episode 3 of 12" — svtplay-dl prints this immediately before each Url line.
    // We use it to (a) order episodes ascending without guessing emit direction and
    // (b) seed a provisional ordinal, so children are never mis-numbered by a blind reverse.
    private static readonly Regex EpisodeLine =
        new(@"^\s*INFO:\s*Episode\s+(?<n>\d+)\s+of\s+\d+\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    public static string[] EpisodeListArgs(string programUrl)
        => new[] { "--get-only-episode-url", "-A", programUrl };

    public static string[] NfoProbeArgs(string episodeUrl, string outDir)
        => new[] { "--nfo", "--force-nfo", "-o", outDir, episodeUrl };

    /// <summary>
    /// Extract episode URLs from svtplay-dl stderr, ordered ascending with ordinals and a
    /// slug-derived provisional title so children render as labelled episodes at expansion
    /// time (before the per-episode <c>--nfo</c> probe supplies real metadata at download).
    /// </summary>
    /// <remarks>
    /// Ground truth (svtplay-dl 4.191, verified against real SVT Play): each episode is
    /// printed as an <c>INFO: Episode N of M</c> line followed by an <c>INFO: Url: &lt;page&gt;</c>
    /// line. Emit order was newest-first in older svtplay-dl but is ascending in 4.191, so we
    /// order by the reported episode number rather than blindly reversing.
    /// </remarks>
    public static UrlClassification ClassifyProgram(string stderrText)
    {
        var text = stderrText ?? string.Empty;

        // Pair each "Url:" with the nearest preceding "Episode N of M" (if any), by position.
        var episodeMarks = new List<(int Pos, int Number)>();
        foreach (Match e in EpisodeLine.Matches(text))
        {
            if (int.TryParse(e.Groups["n"].Value, out var n))
            {
                episodeMarks.Add((e.Index, n));
            }
        }

        var raw = new List<(string Url, int? Reported)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in UrlLine.Matches(text))
        {
            var url = m.Groups["url"].Value;
            if (!seen.Add(url))
            {
                continue;
            }

            int? reported = null;
            for (var i = episodeMarks.Count - 1; i >= 0; i--)
            {
                if (episodeMarks[i].Pos < m.Index)
                {
                    reported = episodeMarks[i].Number;
                    break;
                }
            }

            raw.Add((url, reported));
        }

        if (raw.Count == 0)
        {
            return new UrlClassification { Failed = true };
        }

        // Prefer svtplay-dl's own "Episode N of M" numbering when every entry has one;
        // otherwise fall back to emit order (do NOT blind-reverse — 4.191 is ascending).
        var ordered = raw.All(r => r.Reported.HasValue)
            ? raw.OrderBy(r => r.Reported!.Value).ToList()
            : raw;

        var entries = new List<ExpandedEntry>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var ordinal = ordered[i].Reported ?? i + 1;
            entries.Add(new ExpandedEntry(ordered[i].Url, TitleFromEpisodeUrl(ordered[i].Url), ordinal));
        }

        return new UrlClassification
        {
            IsMultiJob = entries.Count > 1,
            Entries = entries,
        };
    }

    /// <summary>
    /// Derive a human episode label from an SVT Play episode-page URL so a queued child job
    /// shows "Avsnitt 2" instead of a raw URL before its metadata probe runs. SVT episode URLs
    /// are <c>.../video/{id}/{show-slug}/{episode-slug}</c>; the last segment is the label.
    /// This is a provisional display title only — the authoritative title/season/episode come
    /// from the per-episode <c>--nfo</c> probe at download time.
    /// </summary>
    internal static string? TitleFromEpisodeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var slug = Uri.UnescapeDataString(segments[^1]);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        // "avsnitt-2" → "Avsnitt 2"; keep Swedish characters intact.
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 1
                ? w.ToUpperInvariant()
                : char.ToUpper(w[0], CultureInfo.CurrentCulture) + w.Substring(1));
        var title = string.Join(' ', words).Trim();
        return title.Length == 0 ? null : title;
    }

    /// <summary>Parse a svtplay-dl episodedetails NFO into contract metadata.</summary>
    public static MediaMetadata ParseEpisodeNfo(string nfoXml)
    {
        var doc = XDocument.Parse(nfoXml);
        var ep = doc.Root ?? throw new FormatException("empty NFO");

        string? El(string name) => ep.Element(name)?.Value?.Trim();
        int? Int(string name) => int.TryParse(El(name), out var v) ? v : (int?)null;

        int? year = null;
        var airedRaw = El("aired");
        if (!string.IsNullOrWhiteSpace(airedRaw) && DateTime.TryParse(airedRaw, out var dt))
        {
            year = dt.Year;
        }

        return new MediaMetadata
        {
            Category = MediaCategory.Series,
            Title = El("title") ?? "Untitled",
            SeriesName = El("showtitle"),
            SeasonNumber = Int("season"),
            EpisodeNumber = Int("episode"),
            Year = year,
        };
    }
}
