using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfetch.Plugin.Download.Torrents;

/// <summary>
/// One episode extracted from a season-pack torrent: the source file on disk, its library-relative
/// destination path, and the per-episode metadata. Pure data — no I/O.
/// </summary>
/// <param name="SourceFile">Absolute path of the episode's video file inside the staging directory.</param>
/// <param name="RelativePath">
/// The library-ROOT-relative destination path (e.g. <c>"The Wire/Season 01/The Wire - S01E01.mkv"</c>).
/// Because a season pack is <c>PreLaidOut = true</c> — the placer moves the staging subtree verbatim
/// under the library root — this is simultaneously the staging-relative layout target AND the
/// library-relative path the manager resolves against <c>PlacementResult.LibraryRootUsed</c> for the
/// post-download child fan-out. One value, two consumers, no drift.
/// </param>
/// <param name="Metadata">Per-episode metadata for the child job (SeriesName / Season / Episode / Title / Year).</param>
public readonly record struct SeasonPackEpisode(string SourceFile, string RelativePath, MediaMetadata Metadata);

/// <summary>
/// Pure, deterministic mapping from a parsed season pack + its video files to per-episode
/// <see cref="SeasonPackEpisode"/> specs. Extracted from the torrent handler so the SAME computation
/// drives BOTH (1) the physical file layout (<see cref="SeasonPackEpisode.SourceFile"/> →
/// <see cref="SeasonPackEpisode.RelativePath"/>) and (2) the post-download child fan-out
/// (<see cref="DownloadResult.Children"/>) — they can never disagree because they read one list.
///
/// No I/O, no clock: given the same inputs it always yields the same specs, so it is fully unit-testable
/// without touching a real torrent or filesystem.
/// </summary>
public static class SeasonPackChildBuilder
{
    /// <summary>
    /// Builds one <see cref="SeasonPackEpisode"/> per video file. Series name comes from the pack's
    /// parse; season/episode/title are refined per-file. A file whose episode number can't be pinned
    /// keeps its original file name under the series folder (still grouped, never dropped).
    /// </summary>
    /// <param name="pack">The parsed top-level (torrent-name) release — supplies series name, season, year.</param>
    /// <param name="videoFiles">Absolute paths of the pack's video files (junk already filtered).</param>
    /// <returns>One spec per input file, in input order.</returns>
    public static IReadOnlyList<SeasonPackEpisode> Build(ParsedRelease pack, IReadOnlyList<string> videoFiles)
    {
        if (videoFiles is null || videoFiles.Count == 0)
        {
            return Array.Empty<SeasonPackEpisode>();
        }

        var seriesName = string.IsNullOrWhiteSpace(pack.Title) ? "Unknown" : pack.Title;
        var seasonFolderName = Sanitize(seriesName);

        var episodes = new List<SeasonPackEpisode>(videoFiles.Count);
        foreach (var file in videoFiles)
        {
            var perFile = ReleaseNameParser.Parse(Path.GetFileNameWithoutExtension(file));
            var season = perFile.Season ?? pack.Season ?? 1;
            var episodeNumber = perFile.Episodes.Count > 0 ? perFile.Episodes[0] : (int?)null;

            // Prefer a per-file series name only when the file itself parsed confidently as an
            // episode; otherwise fall back to the pack's series name (more reliable single signal).
            var episodeSeries = perFile.Kind == ReleaseKind.Episode
                                && perFile.Confidence >= 0.8
                                && !string.IsNullOrWhiteSpace(perFile.Title)
                ? perFile.Title
                : seriesName;

            var ext = Path.GetExtension(file);
            string relativeTarget;
            if (episodeNumber.HasValue)
            {
                var fileName = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} - S{1:D2}E{2:D2}{3}",
                    Sanitize(episodeSeries),
                    season,
                    episodeNumber.Value,
                    ext);
                relativeTarget = Path.Combine(
                    seasonFolderName,
                    string.Format(CultureInfo.InvariantCulture, "Season {0:D2}", season),
                    fileName);
            }
            else
            {
                // Couldn't pin an episode number — keep the original name under the series folder.
                relativeTarget = Path.Combine(seasonFolderName, Path.GetFileName(file));
            }

            var metadata = new MediaMetadata
            {
                Category = MediaCategory.Series,
                SeriesName = seriesName,
                SeasonNumber = episodeNumber.HasValue ? season : pack.Season,
                EpisodeNumber = episodeNumber,
                Year = pack.Year,
                // Title carries the episode's display title. Without a real episode name from the
                // release, the best available label is the series name (the placer/DTO uses this as
                // the child row's Title). Kept consistent with the single-episode path.
                Title = episodeSeries,
            };

            episodes.Add(new SeasonPackEpisode(file, relativeTarget, metadata));
        }

        return episodes;
    }

    /// <summary>
    /// Maps episode specs to the <see cref="DownloadChild"/> contract the manager consumes for
    /// post-download fan-out: library-relative path + per-episode metadata, one child per episode.
    /// </summary>
    /// <param name="episodes">The per-episode specs from <see cref="Build"/>.</param>
    /// <returns>The child descriptors, in input order.</returns>
    public static IReadOnlyList<DownloadChild> ToChildren(IReadOnlyList<SeasonPackEpisode> episodes)
        => episodes
            .Select(e => new DownloadChild { RelativePath = e.RelativePath, Metadata = e.Metadata })
            .ToList();

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }
}
