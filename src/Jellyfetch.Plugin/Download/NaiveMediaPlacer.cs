using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfetch.Plugin.Configuration;

namespace Jellyfetch.Plugin.Download;

/// <summary>
/// Default placement: dumb but correct. media-downloader owns the production naming conventions
/// and is expected to supersede this with a smarter implementation.
/// Layout:
///   Series → {SeriesRoot}/{SeriesName}/Season {NN}/{SeriesName} - SxxEyy - {Title}{ext}
///   Movie  → {MovieRoot}/{Title (Year)}/{Title (Year)}{ext}
///   Other  → {FallbackRoot or MovieRoot}/{Title}/{original file name}
/// </summary>
public class NaiveMediaPlacer : IMediaPlacer
{
    /// <inheritdoc />
    public Task<PlacementResult> PlaceAsync(DownloadResult result, string stagingDirectory, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var metadata = result.Metadata;
        var category = metadata.Category == MediaCategory.Auto ? MediaCategory.Other : metadata.Category;

        var root = category switch
        {
            MediaCategory.Series => config.SeriesLibraryPath,
            MediaCategory.Movie => config.MovieLibraryPath,
            _ => string.IsNullOrWhiteSpace(config.FallbackLibraryPath) ? config.MovieLibraryPath : config.FallbackLibraryPath,
        };

        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException($"No library path configured for category '{category}'. Set it in the JellyFetch plugin settings.");
        }

        var finalPaths = new List<string>();

        if (result.PreLaidOut)
        {
            foreach (var file in result.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(stagingDirectory, file);
                var target = Path.Combine(root, relative);
                MoveFile(file, target);
                finalPaths.Add(target);
            }
        }
        else
        {
            var targetDir = category switch
            {
                MediaCategory.Series => Path.Combine(
                    root,
                    Sanitize(metadata.SeriesName ?? metadata.Title),
                    string.Format(CultureInfo.InvariantCulture, "Season {0:D2}", metadata.SeasonNumber ?? 1)),
                MediaCategory.Movie => Path.Combine(root, Sanitize(MovieFolderName(metadata))),
                _ => Path.Combine(root, Sanitize(metadata.Title)),
            };

            foreach (var file in result.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = category == MediaCategory.Series && metadata.SeasonNumber.HasValue && metadata.EpisodeNumber.HasValue
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} - S{1:D2}E{2:D2} - {3}{4}",
                        Sanitize(metadata.SeriesName ?? metadata.Title),
                        metadata.SeasonNumber.Value,
                        metadata.EpisodeNumber.Value,
                        Sanitize(metadata.Title),
                        Path.GetExtension(file))
                    : Path.GetFileName(file);

                var target = Path.Combine(targetDir, fileName);
                MoveFile(file, target);
                finalPaths.Add(target);
            }
        }

        return Task.FromResult(new PlacementResult { FinalPaths = finalPaths, LibraryRootUsed = root });
    }

    private static string MovieFolderName(MediaMetadata metadata)
        => metadata.Year.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", metadata.Title, metadata.Year.Value)
            : metadata.Title;

    private static void MoveFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            target = Path.Combine(
                Path.GetDirectoryName(target)!,
                Path.GetFileNameWithoutExtension(target) + " (1)" + Path.GetExtension(target));
        }

        // Move works across filesystems on .NET when overwrite=false may throw; fall back to copy+delete.
        try
        {
            File.Move(source, target);
        }
        catch (IOException)
        {
            File.Copy(source, target, overwrite: false);
            File.Delete(source);
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }
}
