using System;
using System.Collections.Generic;
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

    public static string[] EpisodeListArgs(string programUrl)
        => new[] { "--get-only-episode-url", "-A", programUrl };

    public static string[] NfoProbeArgs(string episodeUrl, string outDir)
        => new[] { "--nfo", "--force-nfo", "-o", outDir, episodeUrl };

    /// <summary>Extract episode URLs from svtplay-dl stderr, oldest-first with ordinals.</summary>
    public static UrlClassification ClassifyProgram(string stderrText)
    {
        var urls = new List<string>();
        foreach (Match m in UrlLine.Matches(stderrText ?? string.Empty))
        {
            urls.Add(m.Groups["url"].Value);
        }

        if (urls.Count == 0)
        {
            return new UrlClassification { Failed = true };
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var u in urls)
        {
            if (seen.Add(u))
            {
                ordered.Add(u);
            }
        }

        // svtplay-dl lists newest-first; reverse for ascending episode ordinals.
        ordered.Reverse();

        var entries = new List<ExpandedEntry>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            entries.Add(new ExpandedEntry(ordered[i], null, i + 1));
        }

        return new UrlClassification
        {
            IsMultiJob = entries.Count > 1,
            Entries = entries,
        };
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
