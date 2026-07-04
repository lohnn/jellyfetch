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
/// The production placer. Despite the "Naive" name it is not a stopgap: by design the download
/// backends own their own layout logic and hand this placer a subtree they already laid out in
/// staging (<see cref="DownloadResult.PreLaidOut"/> = true), which it moves verbatim under the
/// correct library root — after a write-permission pre-flight (W-049 / SNG-032). Its own
/// Series/Movie/Other scheme below is a correct fallback used only for non-pre-laid-out inputs
/// (e.g. a backend that returns bare files with metadata but no tree). See AGENTS.md
/// "Naming &amp; placement" for why layout stays in the backends rather than being consolidated here.
/// Fallback layout:
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

        // Pre-flight: fail fast with an actionable message if the library root isn't writable by the
        // Jellyfin service user, rather than only after files are already downloaded (W-049, SNG-032).
        PlacementPermissions.EnsureWritable(root);

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
        var targetDir = Path.GetDirectoryName(target)!;
        try
        {
            Directory.CreateDirectory(targetDir);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw PlacementPermissions.Denied(targetDir, ex);
        }

        if (File.Exists(target))
        {
            target = Path.Combine(
                targetDir,
                Path.GetFileNameWithoutExtension(target) + " (1)" + Path.GetExtension(target));
        }

        // Move works across filesystems on .NET when overwrite=false may throw; fall back to copy+delete.
        try
        {
            File.Move(source, target);
        }
        catch (UnauthorizedAccessException ex)
        {
            // The target directory exists but the Jellyfin service user cannot write into it.
            throw PlacementPermissions.Denied(targetDir, ex);
        }
        catch (IOException)
        {
            try
            {
                File.Copy(source, target, overwrite: false);
                File.Delete(source);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw PlacementPermissions.Denied(targetDir, ex);
            }
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }
}

/// <summary>
/// Thrown when library placement fails because the Jellyfin service user lacks write access to a
/// target directory. Carries a message actionable enough to surface in the app's job-failure field
/// (it names the directory and the exact fix), so the user isn't left with a raw stack trace.
///
/// Honors W-049 / SNG-032: a correctly-produced download handed to a placement step that lacks
/// permission on the target dir is as broken as a failed download — the failure is at the
/// ownership/access boundary, not in the code, and only reproduces on a real self-hosted box.
/// </summary>
public sealed class PlacementPermissionException : UnauthorizedAccessException
{
    /// <summary>Initializes a new instance of the <see cref="PlacementPermissionException"/> class.</summary>
    /// <param name="message">The actionable, user-facing message.</param>
    /// <param name="targetDirectory">The directory that could not be written.</param>
    /// <param name="inner">The underlying <see cref="UnauthorizedAccessException"/>.</param>
    public PlacementPermissionException(string message, string targetDirectory, Exception inner)
        : base(message, inner)
    {
        TargetDirectory = targetDirectory;
    }

    /// <summary>Gets the directory the Jellyfin service user could not write into.</summary>
    public string TargetDirectory { get; }
}

/// <summary>
/// Permission detection + actionable messaging for library placement. Kept together with the placer
/// so any placement path (this naive one, a future production placer, or a backend laying out files)
/// surfaces the identical, fix-carrying message instead of a raw <see cref="UnauthorizedAccessException"/>.
/// Detection only — never chmod/chown from the plugin (never silently escalate perms).
/// </summary>
public static class PlacementPermissions
{
    /// <summary>
    /// Builds a <see cref="PlacementPermissionException"/> naming the failing directory and the exact
    /// remediation (chown/chmod for the jellyfin service user). Wrap a caught
    /// <see cref="UnauthorizedAccessException"/> from a create/move/write into the library.
    /// </summary>
    /// <param name="targetDirectory">The directory that could not be written.</param>
    /// <param name="inner">The underlying access exception.</param>
    /// <returns>An exception whose message is safe and useful to show in the app.</returns>
    public static PlacementPermissionException Denied(string targetDirectory, Exception inner)
        => new(BuildMessage(targetDirectory), targetDirectory, inner);

    /// <summary>
    /// Pre-flight check: probe whether the Jellyfin service user can actually write under
    /// <paramref name="root"/> by creating and deleting a throwaway file. On denial, throws the same
    /// actionable <see cref="PlacementPermissionException"/> — so a long download isn't wasted before
    /// the permission problem is reported. Silently succeeds when the root doesn't exist yet (the move
    /// step creates it) or when the probe hits a non-permission I/O error (don't block on transient
    /// conditions; the real move will surface anything that matters).
    /// </summary>
    /// <param name="root">The resolved library root to probe.</param>
    public static void EnsureWritable(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        // Probe the nearest existing ancestor — if the root itself doesn't exist yet, writability of
        // the parent is what determines whether we can create it.
        var probeDir = NearestExistingAncestor(root);
        if (probeDir is null)
        {
            return; // Nothing exists to probe; let the move step attempt creation and report.
        }

        var probeFile = Path.Combine(probeDir, ".jellyfetch-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (File.Create(probeFile))
            {
            }

            File.Delete(probeFile);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Denied(root, ex);
        }
        catch (IOException)
        {
            // Non-permission I/O hiccup (disk full, path length, transient). Don't pre-emptively fail;
            // the actual placement will surface a real error if one exists.
            TryDeleteProbe(probeFile);
        }
    }

    private static string BuildMessage(string targetDirectory)
        => $"Access denied writing to the library directory '{targetDirectory}'. "
           + "The Jellyfin service user must own and be able to write this directory. "
           + $"On a self-hosted server, fix it with: sudo chown -R jellyfin:jellyfin \"{targetDirectory}\" "
           + $"&& sudo chmod -R u+rwX \"{targetDirectory}\" (then retry the download). "
           + "JellyFetch does not change permissions itself.";

    private static string? NearestExistingAncestor(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

    private static void TryDeleteProbe(string probeFile)
    {
        try
        {
            if (File.Exists(probeFile))
            {
                File.Delete(probeFile);
            }
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
