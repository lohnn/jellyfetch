using System;
using System.Globalization;
using System.Text;

namespace Jellyfetch.Plugin.Download.WebMedia;

/// <summary>
/// Turns best-known <see cref="MediaMetadata"/> into Jellyfin-conventional
/// library-relative paths, and generates .nfo sidecar bodies. Pure and deterministic
/// (no I/O) so it is unit-testable in isolation and reusable — the torrent engine can
/// feed the same <see cref="MediaMetadata"/> in to get identical placement.
///
/// Conventions (Jellyfin naming docs):
///   Series → {SeriesName}/Season 01/{SeriesName} S01E02.ext          (under series root)
///   Movie  → {Title (Year)}/{Title (Year)}.ext                        (under movie root)
///   Other  → {Title (Year)}/{Title (Year)}.ext                        (under fallback root)
///
/// Used in <c>PreLaidOut</c> mode: the handler materializes files at these relative
/// paths inside the staging dir, and the core placer moves the tree verbatim under the
/// category's configured library root.
/// </summary>
internal sealed class MediaOrganizer
{
    /// <summary>Compute the library-relative layout for an item.</summary>
    public PlacementPlan Plan(MediaMetadata meta)
    {
        if (meta == null)
        {
            throw new ArgumentNullException(nameof(meta));
        }

        var category = meta.Category == MediaCategory.Auto ? MediaCategory.Other : meta.Category;
        return category switch
        {
            MediaCategory.Series => PlanEpisode(meta),
            MediaCategory.Movie => PlanTitled(meta, MediaCategory.Movie),
            _ => PlanTitled(meta, MediaCategory.Other),
        };
    }

    private static PlacementPlan PlanEpisode(MediaMetadata meta)
    {
        var series = FilenameSanitizer.Sanitize(meta.SeriesName ?? meta.Title, "Unknown Series");
        var season = meta.SeasonNumber ?? 1;
        var episode = meta.EpisodeNumber ?? 0;
        var seasonFolder = string.Format(CultureInfo.InvariantCulture, "Season {0:D2}", season);
        var stem = string.Format(CultureInfo.InvariantCulture, "{0} S{1:D2}E{2:D2}", series, season, episode);

        return new PlacementPlan(
            category: MediaCategory.Series,
            relativeDirectory: series + "/" + seasonFolder,
            fileStem: stem,
            tvShowNfoRelativePath: series + "/tvshow.nfo");
    }

    private static PlacementPlan PlanTitled(MediaMetadata meta, MediaCategory category)
    {
        var titled = TitleWithYear(meta.Title, meta.Year);
        return new PlacementPlan(category, relativeDirectory: titled, fileStem: titled, tvShowNfoRelativePath: null);
    }

    private static string TitleWithYear(string? title, int? year)
    {
        var t = FilenameSanitizer.Sanitize(title, "Untitled");
        return year is > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", t, year.Value)
            : t;
    }

    /// <summary>Jellyfin-readable NFO sidecar body: episodedetails for episodes, movie otherwise.</summary>
    public string BuildNfo(MediaMetadata meta)
    {
        if (meta == null)
        {
            throw new ArgumentNullException(nameof(meta));
        }

        return meta.Category == MediaCategory.Series ? BuildEpisodeNfo(meta) : BuildMovieNfo(meta);
    }

    /// <summary>
    /// Build a Jellyfin <c>&lt;movie&gt;</c> NFO body, carrying over any rich fields svtplay-dl
    /// already provided (plot, aired date) so switching a standalone SVT film from svtplay-dl's
    /// <c>&lt;episodedetails&gt;</c> probe NFO to a correctly-rooted <c>&lt;movie&gt;</c> NFO does not
    /// DROP that metadata. Title/year come from the resolved <see cref="MediaMetadata"/>; plot/aired
    /// are passed through VERBATIM from the source NFO (never fabricated, never massaged — I-118).
    /// The aired date is emitted as <c>&lt;premiered&gt;</c> (Jellyfin's movie-release date tag)
    /// with an <c>&lt;aired&gt;</c> alias for readers that prefer it.
    /// </summary>
    /// <param name="meta">Resolved metadata (title, year).</param>
    /// <param name="plot">Plot/description carried from the source NFO, or null.</param>
    /// <param name="aired">Aired/premiere date string carried verbatim from the source NFO, or null.</param>
    /// <returns>The <c>&lt;movie&gt;</c> NFO XML body.</returns>
    public string BuildMovieNfo(MediaMetadata meta, string? plot, string? aired)
    {
        if (meta == null)
        {
            throw new ArgumentNullException(nameof(meta));
        }

        return BuildMovieNfoInternal(meta, plot, aired);
    }

    /// <summary>Series-level tvshow.nfo body (title only — providers fill the rest).</summary>
    public string BuildTvShowNfo(string seriesName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.AppendLine("<tvshow>");
        sb.AppendLine("  <title>" + Xml(seriesName) + "</title>");
        sb.AppendLine("</tvshow>");
        return sb.ToString();
    }

    private static string BuildEpisodeNfo(MediaMetadata meta)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.AppendLine("<episodedetails>");
        sb.AppendLine("  <title>" + Xml(meta.Title) + "</title>");
        if (!string.IsNullOrWhiteSpace(meta.SeriesName))
        {
            sb.AppendLine("  <showtitle>" + Xml(meta.SeriesName!) + "</showtitle>");
        }

        sb.AppendLine("  <season>" + (meta.SeasonNumber ?? 1).ToString(CultureInfo.InvariantCulture) + "</season>");
        sb.AppendLine("  <episode>" + (meta.EpisodeNumber ?? 0).ToString(CultureInfo.InvariantCulture) + "</episode>");
        sb.AppendLine("</episodedetails>");
        return sb.ToString();
    }

    private static string BuildMovieNfo(MediaMetadata meta) => BuildMovieNfoInternal(meta, plot: null, aired: null);

    private static string BuildMovieNfoInternal(MediaMetadata meta, string? plot, string? aired)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.AppendLine("<movie>");
        sb.AppendLine("  <title>" + Xml(meta.Title) + "</title>");
        if (meta.Year is > 0)
        {
            sb.AppendLine("  <year>" + meta.Year!.Value.ToString(CultureInfo.InvariantCulture) + "</year>");
        }

        // Carried over verbatim from svtplay-dl's probe NFO when present (do not fabricate).
        if (!string.IsNullOrWhiteSpace(plot))
        {
            sb.AppendLine("  <plot>" + Xml(plot!.Trim()) + "</plot>");
        }

        if (!string.IsNullOrWhiteSpace(aired))
        {
            var a = Xml(aired!.Trim());
            sb.AppendLine("  <premiered>" + a + "</premiered>");
            sb.AppendLine("  <aired>" + a + "</aired>");
        }

        sb.AppendLine("</movie>");
        return sb.ToString();
    }

    private static string Xml(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal)
         .Replace("<", "&lt;", StringComparison.Ordinal)
         .Replace(">", "&gt;", StringComparison.Ordinal)
         .Replace("\"", "&quot;", StringComparison.Ordinal);
}
