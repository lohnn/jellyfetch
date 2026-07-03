using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfetch.Plugin.Download.Torrents;

/// <summary>
/// The kind of media a release name most plausibly describes.
/// </summary>
public enum ReleaseKind
{
    /// <summary>Could not be determined — caller should fall back to unsorted placement.</summary>
    Unknown,

    /// <summary>Episodic content (has a season/episode designation).</summary>
    Episode,

    /// <summary>A whole season / batch of episodes with no single episode number.</summary>
    SeasonPack,

    /// <summary>A movie (year present, no episode markers).</summary>
    Movie
}

/// <summary>
/// A structured, best-effort interpretation of a scene/community release name.
/// All fields are guesses; <see cref="Confidence"/> communicates how much to trust them.
/// Low confidence should route to an unsorted/ fallback rather than a wrong library slot.
/// </summary>
public readonly record struct ParsedRelease
{
    /// <summary>Gets the detected kind of media.</summary>
    public ReleaseKind Kind { get; init; }

    /// <summary>Gets the cleaned human title (series name for episodes, movie title for movies).</summary>
    public string Title { get; init; }

    /// <summary>Gets the release year, if one was found.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the season number, for episodic content.</summary>
    public int? Season { get; init; }

    /// <summary>
    /// Gets the episode number(s). A single-episode release has one entry; a multi-episode
    /// file (S01E01E02) has several. Empty for movies and season packs.
    /// </summary>
    public IReadOnlyList<int> Episodes { get; init; }

    /// <summary>Gets a 0..1 confidence score. Below ~0.5 should be treated as unsorted.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets the original name this was parsed from.</summary>
    public string Source { get; init; }
}

/// <summary>
/// Parses scene/community torrent release names into structured <see cref="ParsedRelease"/> guesses.
///
/// Design principles:
/// <list type="bullet">
///   <item>Never throw. An unparseable name returns <see cref="ReleaseKind.Unknown"/> with low confidence.</item>
///   <item>Prefer under-claiming to mis-claiming: ambiguous input yields lower confidence so the
///   caller routes it to an unsorted fallback rather than the wrong library folder.</item>
///   <item>Pure and deterministic — no I/O, no clock, no culture surprises (invariant parsing).</item>
/// </list>
/// </summary>
public static class ReleaseNameParser
{
    // Episode patterns, most-specific first. All case-insensitive.
    // S01E02, S01E02E03, S01E02-E03. Extra episodes MUST carry an explicit E (or -E)
    // so we don't swallow the quality tag that follows (e.g. ".1080p").
    private static readonly Regex SxxExx = new(
        @"[._ \-]?S(?<s>\d{1,2})[._ \-]?E(?<e>\d{1,3})(?<extra>(?:[._ \-]?E\d{1,3})*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 1x02, 01x02, 1x02-1x03. Extra episodes must be full NxNN groups.
    private static readonly Regex NxNN = new(
        @"[._ \-](?<s>\d{1,2})x(?<e>\d{1,3})(?<extra>(?:[._ \-]\d{1,2}x\d{1,3})*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Season pack: "S01" / "Season 1" / "Series 1" NOT immediately followed by an episode
    // marker (E\d or x\d). A quality tag or end-of-string after the season number is fine.
    private static readonly Regex SeasonOnly = new(
        @"[._ \-](?:S(?<s>\d{1,2})|Season[._ \-]?(?<s2>\d{1,2})|Series[._ \-]?(?<s3>\d{1,2}))(?![Ee]\d|x\d|\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Complete" season-pack hint.
    private static readonly Regex CompleteHint = new(
        @"[._ \-]COMPLETE[._ \-]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A 4-digit year 1900..2099, as a delimited token.
    private static readonly Regex Year = new(
        @"(?<![\d])(?<y>(?:19|20)\d{2})(?![\d])", RegexOptions.Compiled);

    // Tokens that mark the end of a title and the start of quality/tags. Everything from the
    // first of these onward is stripped when deriving a clean title.
    private static readonly string[] TagTokens =
    {
        "1080p", "2160p", "720p", "480p", "4k", "uhd", "hdr", "hdr10", "dv", "dolby",
        "web-dl", "webdl", "webrip", "web", "bluray", "blu-ray", "bdrip", "brrip", "dvdrip",
        "hdtv", "pdtv", "hdrip", "remux", "x264", "x265", "h264", "h265", "hevc", "avc",
        "xvid", "divx", "aac", "ac3", "dts", "dd5", "ddp5", "atmos", "flac", "mp3",
        "10bit", "8bit", "proper", "repack", "internal", "limited", "extended", "unrated",
        "multi", "dual", "dubbed", "subbed", "hardsub", "amzn", "nf", "dsnp", "hmax", "hulu",
    };

    private static readonly HashSet<string> TagSet =
        new(TagTokens, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parse a release name (a directory or file name; extension is ignored if present).
    /// </summary>
    /// <param name="rawName">The release name. Null/empty yields an Unknown low-confidence result.</param>
    /// <returns>A best-effort structured interpretation. Never throws.</returns>
    public static ParsedRelease Parse(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return Unknown(rawName ?? string.Empty);

        var name = StripExtension(rawName.Trim());

        // 1) Try episodic S01E02 / S01E02E03 form.
        var m = SxxExx.Match(name);
        if (m.Success)
            return BuildEpisode(name, m, delimiterIsX: false);

        // 2) Try 1x02 form.
        m = NxNN.Match(name);
        if (m.Success)
            return BuildEpisode(name, m, delimiterIsX: true);

        // 3) Season pack (S01 / Season 1 / Complete) with no episode.
        var sp = SeasonOnly.Match(name);
        bool complete = CompleteHint.IsMatch("." + name + ".");
        if (sp.Success || complete)
        {
            int? season = null;
            if (sp.Success)
            {
                var sVal = sp.Groups["s"].Success ? sp.Groups["s"].Value
                    : sp.Groups["s2"].Success ? sp.Groups["s2"].Value
                    : sp.Groups["s3"].Value;
                if (int.TryParse(sVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sn))
                    season = sn;
            }

            int cutIndex = sp.Success ? sp.Index : FirstTagIndex(name);
            var title = CleanTitle(name, cutIndex);
            var (year, _) = ExtractYear(name, title);

            double conf = 0.6;
            if (season.HasValue) conf += 0.15;
            if (complete) conf += 0.1;
            if (!string.IsNullOrEmpty(title)) conf += 0.05;

            return new ParsedRelease
            {
                Kind = ReleaseKind.SeasonPack,
                Title = title,
                Season = season,
                Year = year,
                Episodes = Array.Empty<int>(),
                Confidence = Math.Min(conf, 0.9),
                Source = rawName,
            };
        }

        // 4) Movie: a year token with no episode markers.
        var ym = Year.Match(name);
        if (ym.Success)
        {
            var year = int.Parse(ym.Groups["y"].Value, CultureInfo.InvariantCulture);
            var title = CleanTitle(name, ym.Index);
            double conf = 0.7;
            if (string.IsNullOrEmpty(title))
            {
                // Year with no leading title text — weak.
                title = CleanTitle(name, FirstTagIndex(name));
                conf = 0.4;
            }

            return new ParsedRelease
            {
                Kind = ReleaseKind.Movie,
                Title = title,
                Year = year,
                Season = null,
                Episodes = Array.Empty<int>(),
                Confidence = Math.Min(conf, 0.9),
                Source = rawName,
            };
        }

        // 5) Nothing recognizable. Return a cleaned title with low confidence so the
        //    caller can still show *something* but routes to unsorted.
        var fallbackTitle = CleanTitle(name, FirstTagIndex(name));
        return new ParsedRelease
        {
            Kind = ReleaseKind.Unknown,
            Title = fallbackTitle,
            Year = null,
            Season = null,
            Episodes = Array.Empty<int>(),
            Confidence = string.IsNullOrEmpty(fallbackTitle) ? 0.0 : 0.25,
            Source = rawName,
        };
    }

    private static ParsedRelease BuildEpisode(string name, Match m, bool delimiterIsX)
    {
        int season = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        var episodes = new List<int> { int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture) };

        // Multi-episode extras: E03, -E03, x03, 03.
        var extra = m.Groups["extra"].Value;
        if (!string.IsNullOrEmpty(extra))
        {
            foreach (Match em in Regex.Matches(extra, @"\d{1,3}"))
            {
                if (int.TryParse(em.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ev))
                    episodes.Add(ev);
            }
        }

        var title = CleanTitle(name, m.Index);
        var (year, _) = ExtractYear(name, title);

        double conf = 0.85;
        if (!string.IsNullOrEmpty(title)) conf += 0.1;
        if (episodes.Count > 1) conf -= 0.0; // multi-ep is still confident
        conf = Math.Min(conf, 0.98);

        // A title that is empty is suspicious for an episode.
        if (string.IsNullOrEmpty(title)) conf = 0.5;

        return new ParsedRelease
        {
            Kind = ReleaseKind.Episode,
            Title = title,
            Season = season,
            Episodes = episodes,
            Year = year,
            Confidence = conf,
            Source = name,
        };
    }

    // ---- helpers -----------------------------------------------------------

    private static ParsedRelease Unknown(string source) => new()
    {
        Kind = ReleaseKind.Unknown,
        Title = string.Empty,
        Episodes = Array.Empty<int>(),
        Confidence = 0.0,
        Source = source,
    };

    private static string StripExtension(string name)
    {
        // Only strip known media/container extensions so "S01.2160p" isn't mangled.
        var ext = new[] { ".mkv", ".mp4", ".avi", ".mov", ".ts", ".m4v", ".wmv", ".flv" };
        foreach (var e in ext)
        {
            if (name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                return name[..^e.Length];
        }
        return name;
    }

    /// <summary>Index of the first quality/tag token, or name length if none.</summary>
    private static int FirstTagIndex(string name)
    {
        int best = name.Length;
        foreach (var token in Tokenize(name))
        {
            if (TagSet.Contains(token.value))
            {
                best = Math.Min(best, token.index);
            }
        }
        return best;
    }

    private static (int? year, int index) ExtractYear(string name, string title)
    {
        foreach (Match ym in Year.Matches(name))
        {
            // Ignore a year that is actually part of the title we already kept
            // (e.g. "Blade Runner 2049" — the 2049 is > 2038 but still plausibly a title number).
            var y = int.Parse(ym.Groups["y"].Value, CultureInfo.InvariantCulture);
            // Prefer years that appear after the title text (typical release layout).
            return (y, ym.Index);
        }
        return (null, -1);
    }

    /// <summary>
    /// Build a clean, human-readable title from the portion of the name before <paramref name="cutIndex"/>.
    /// Replaces dots/underscores with spaces, collapses whitespace, trims trailing separators.
    /// </summary>
    private static string CleanTitle(string name, int cutIndex)
    {
        if (cutIndex < 0 || cutIndex > name.Length) cutIndex = name.Length;
        var head = name[..cutIndex];

        // Normalize separators.
        var spaced = head.Replace('.', ' ').Replace('_', ' ');
        // Release group / bracket noise at the end.
        spaced = Regex.Replace(spaced, @"[\[\(\{].*$", " ");
        // Strip a trailing "COMPLETE" season-pack marker that precedes the tag block.
        spaced = Regex.Replace(spaced, @"\bCOMPLETE\b", " ", RegexOptions.IgnoreCase);
        // Collapse dashes used as separators, but keep intra-word hyphens rare — simplest: to space.
        spaced = spaced.Replace(" - ", " ");
        // Collapse whitespace.
        spaced = Regex.Replace(spaced, @"\s+", " ").Trim(' ', '-', '.', '_');

        return spaced;
    }

    private static IEnumerable<(string value, int index)> Tokenize(string name)
    {
        foreach (Match tok in Regex.Matches(name, @"[A-Za-z0-9]+"))
            yield return (tok.Value, tok.Index);
    }
}
