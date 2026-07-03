using System;
using System.Linq;

namespace Jellyfetch.Plugin.Download.WebMedia;

/// <summary>
/// The library-relative layout for one item: which category root it belongs under,
/// the relative directory, and the base file stem. All paths use '/' and are relative
/// to the (later-resolved) library root, matching the <c>PreLaidOut</c> placement mode
/// where the core placer moves the staging subtree verbatim under the category root.
/// </summary>
internal sealed class PlacementPlan
{
    public PlacementPlan(MediaCategory category, string relativeDirectory, string fileStem, string? tvShowNfoRelativePath)
    {
        Category = category;
        RelativeDirectory = relativeDirectory;
        FileStem = fileStem;
        TvShowNfoRelativePath = tvShowNfoRelativePath;
    }

    /// <summary>Category whose configured library root this item lands under.</summary>
    public MediaCategory Category { get; }

    /// <summary>Directory relative to the category root, e.g. "Series/Season 01".</summary>
    public string RelativeDirectory { get; }

    /// <summary>Base filename without extension, e.g. "Series S01E02".</summary>
    public string FileStem { get; }

    /// <summary>Series-level tvshow.nfo path when this is an episode, else null.</summary>
    public string? TvShowNfoRelativePath { get; }

    /// <summary>Relative path of the video file including extension.</summary>
    public string VideoRelativePath(string ext) => Combine(RelativeDirectory, FileStem + NormalizeExt(ext));

    /// <summary>Relative path of the .nfo sidecar next to the video.</summary>
    public string NfoRelativePath() => Combine(RelativeDirectory, FileStem + ".nfo");

    /// <summary>
    /// Relative subtitle path with a Jellyfin language suffix, e.g.
    /// "Series S01E02.sv.srt"; "sv-forced" → "Series S01E02.sv.forced.srt".
    /// </summary>
    public string SubtitleRelativePath(string languageCode, string ext)
        => Combine(RelativeDirectory, FileStem + NormalizeLanguage(languageCode) + NormalizeExt(ext));

    private static string NormalizeExt(string ext)
    {
        if (string.IsNullOrEmpty(ext))
        {
            return string.Empty;
        }

        return ext[0] == '.' ? ext : "." + ext;
    }

    private static string NormalizeLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var parts = code.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(p => p.Length > 0);
        return "." + string.Join('.', parts);
    }

    private static string Combine(string dir, string file)
        => string.IsNullOrEmpty(dir) ? file : dir + "/" + file;
}
