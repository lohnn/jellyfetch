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
    /// Ground truth (svtplay-dl 4.191, verified LIVE against real svtplay.se, 2026-07): each
    /// episode is printed as an <c>INFO: Episode N of M</c> line followed by an
    /// <c>INFO: Url: &lt;page&gt;</c> line. IMPORTANT correction to earlier notes: the
    /// <c>Episode N of M</c> marker is an EMIT-POSITION counter, NOT the true episode ordinal,
    /// and 4.191 emits episodes REVERSE (last-first) — "Episode 1 of 4" was observed paired
    /// with the slug ".../4-motet" (episode 4), and "Episode 4 of 4" with ".../1-igenkannandet"
    /// (episode 1). The AUTHORITATIVE ordinal is the leading number in the episode-page SLUG
    /// (<c>4-motet</c> → 4, <c>avsnitt-2</c> → 2). So we order by, and number from, the slug
    /// ordinal when every entry has one; only when slugs lack a leading number do we fall back
    /// to the emitted marker / emit order. This makes ordering robust to svtplay-dl's emit
    /// direction (which has flipped between versions). The per-episode <c>--nfo</c> probe still
    /// supplies the authoritative <c>&lt;episode&gt;</c> at download time; this ordinal is a
    /// provisional display/expansion order only.
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

        // Preserve emit order in EmitIndex so we can fall back to it when neither the slug nor
        // the marker gives a usable ordinal.
        var raw = new List<(string Url, int? SlugOrdinal, int? Marker, int EmitIndex)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var emitIndex = 0;
        foreach (Match m in UrlLine.Matches(text))
        {
            var url = m.Groups["url"].Value;
            if (!seen.Add(url))
            {
                continue;
            }

            int? marker = null;
            for (var i = episodeMarks.Count - 1; i >= 0; i--)
            {
                if (episodeMarks[i].Pos < m.Index)
                {
                    marker = episodeMarks[i].Number;
                    break;
                }
            }

            raw.Add((url, SlugOrdinal(url), marker, emitIndex++));
        }

        if (raw.Count == 0)
        {
            return new UrlClassification { Failed = true };
        }

        // Ordering priority (live-verified): slug ordinal (authoritative) when every entry
        // has one; else the emitted marker; else raw emit order. Slug wins because the marker
        // is only an emit counter and svtplay-dl's emit direction is version-dependent.
        List<(string Url, int? SlugOrdinal, int? Marker, int EmitIndex)> ordered;
        if (raw.All(r => r.SlugOrdinal.HasValue))
        {
            ordered = raw.OrderBy(r => r.SlugOrdinal!.Value).ToList();
        }
        else if (raw.All(r => r.Marker.HasValue))
        {
            ordered = raw.OrderBy(r => r.Marker!.Value).ToList();
        }
        else
        {
            ordered = raw.OrderBy(r => r.EmitIndex).ToList();
        }

        var entries = new List<ExpandedEntry>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            // Number from the slug ordinal (authoritative); fall back to marker, then position.
            var ordinal = ordered[i].SlugOrdinal ?? ordered[i].Marker ?? i + 1;
            entries.Add(new ExpandedEntry(ordered[i].Url, TitleFromEpisodeUrl(ordered[i].Url), ordinal));
        }

        return new UrlClassification
        {
            IsMultiJob = entries.Count > 1,
            Entries = entries,
        };
    }

    /// <summary>
    /// Extract the leading episode ordinal from an SVT episode-page slug, e.g.
    /// <c>.../4-motet</c> → 4, <c>.../avsnitt-2</c> → 2, <c>.../del-1-av-6</c> → 1. Returns
    /// null when the slug has no leading/embedded number (e.g. a named single like
    /// <c>.../kaarina-kaikkonen</c>). Live-verified as the authoritative episode ordinal for
    /// svtplay-dl 4.191, which numbers the "Episode N of M" marker by emit position only.
    /// </summary>
    internal static int? SlugOrdinal(string url)
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

        // Leading "N-" (e.g. "4-motet").
        var lead = Regex.Match(slug, @"^(?<n>\d+)-");
        if (lead.Success && int.TryParse(lead.Groups["n"].Value, out var n))
        {
            return n;
        }

        // "avsnitt-N" / "del-N" anywhere in the slug.
        var named = Regex.Match(slug, @"(?:avsnitt|del)-(?<n>\d+)", RegexOptions.IgnoreCase);
        if (named.Success && int.TryParse(named.Groups["n"].Value, out var n2))
        {
            return n2;
        }

        return null;
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

    /// <summary>
    /// Parse a svtplay-dl NFO into contract metadata, classifying film vs. series.
    /// </summary>
    /// <remarks>
    /// Ground truth (svtplay-dl 4.191, verified live against real svtplay.se, 2026-07):
    /// svtplay-dl ALWAYS writes an <c>&lt;episodedetails&gt;</c> root — it never emits a
    /// <c>&lt;movie&gt;</c> NFO, even for standalone films. It distinguishes them by which
    /// child elements it fills:
    ///   • Series episode → <c>&lt;showtitle&gt;</c> (series name) + <c>&lt;title&gt;</c>
    ///     (episode name) + <c>&lt;season&gt;</c> + <c>&lt;episode&gt;</c>.
    ///   • Standalone film → <c>&lt;showtitle&gt;</c> (the film's own name) only; svtplay-dl
    ///     OMITS <c>&lt;title&gt;</c> (its <c>episodename</c> is empty when the parent name
    ///     equals the details heading) and emits NO <c>&lt;season&gt;</c>/<c>&lt;episode&gt;</c>.
    /// So the real name for BOTH lives in <c>&lt;showtitle&gt;</c>; a title-less film with no
    /// <c>&lt;episode&gt;</c> is classified <see cref="MediaCategory.Movie"/> and its display
    /// title taken from <c>&lt;showtitle&gt;</c> (was incorrectly "Untitled" + Series before).
    /// SVT <c>&lt;season&gt;</c> is preserved verbatim — it is sometimes the real season index
    /// and sometimes the production year (I-118); never "corrected" here.
    /// </remarks>
    public static MediaMetadata ParseEpisodeNfo(string nfoXml)
    {
        var doc = XDocument.Parse(nfoXml);
        var ep = doc.Root ?? throw new FormatException("empty NFO");

        string? El(string name) => Trimmed(ep.Element(name)?.Value);
        int? Int(string name) => int.TryParse(El(name), out var v) ? v : (int?)null;

        int? year = null;
        var airedRaw = El("aired");
        if (!string.IsNullOrWhiteSpace(airedRaw) && DateTime.TryParse(airedRaw, out var dt))
        {
            year = dt.Year;
        }

        var episodeTitle = El("title");
        var showTitle = El("showtitle");
        var season = Int("season");
        var episode = Int("episode");

        // Film-vs-series signal (validated live, svtplay-dl 4.191): svtplay-dl fills
        // <episode> only for genuine series episodes. A title-less, episode-less NFO is a
        // standalone film. <title> presence is a corroborating signal but <episode> is the
        // structural one (a film NFO carries neither <title> nor <episode>).
        var isFilm = episode == null && string.IsNullOrWhiteSpace(episodeTitle);
        if (isFilm)
        {
            return new MediaMetadata
            {
                Category = MediaCategory.Movie,

                // The film's own name lives in <showtitle> (episodename is empty → no <title>).
                Title = showTitle ?? "Untitled",

                // A movie is not part of a series; leave SeriesName null so the movie layout
                // ({Title (Year)}/...) is used rather than the series layout.
                SeriesName = null,
                SeasonNumber = null,
                EpisodeNumber = null,
                Year = year,
            };
        }

        return new MediaMetadata
        {
            Category = MediaCategory.Series,

            // Prefer the per-episode <title>; fall back to <showtitle> so an episode whose
            // episodename svtplay-dl omitted still shows a real name, never "Untitled".
            Title = episodeTitle ?? showTitle ?? "Untitled",
            SeriesName = showTitle,
            SeasonNumber = season,   // SVT: real index OR year — preserved verbatim (I-118).
            EpisodeNumber = episode,
            Year = year,
        };
    }

    /// <summary>
    /// Extract the rich free-text fields (<c>plot</c>, <c>aired</c>) from a svtplay-dl NFO so they
    /// can be carried VERBATIM into a re-rooted <c>&lt;movie&gt;</c> NFO for standalone films —
    /// without which the switch from svtplay-dl's <c>&lt;episodedetails&gt;</c> probe NFO would lose
    /// the plot/air-date svtplay-dl already provided. Returns nulls (never throws) if the XML can't
    /// be parsed or the fields are absent. Values are returned unmodified (trimmed only).
    /// </summary>
    public static (string? Plot, string? Aired) ReadNfoExtras(string nfoXml)
    {
        try
        {
            var doc = XDocument.Parse(nfoXml);
            var root = doc.Root;
            if (root == null)
            {
                return (null, null);
            }

            return (Trimmed(root.Element("plot")?.Value), Trimmed(root.Element("aired")?.Value));
        }
        catch (System.Xml.XmlException)
        {
            return (null, null);
        }
    }

    private static string? Trimmed(string? value)
    {
        if (value == null)
        {
            return null;
        }

        var t = value.Trim();
        return t.Length == 0 ? null : t;
    }
}
