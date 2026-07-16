using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfetch.Plugin.Download;

/// <summary>
/// Resolves a placement library ROOT from the Jellyfin libraries the user already defined, instead of
/// from configured paths. This is the single source of truth for "which folder does this land in" —
/// used by both <see cref="NaiveMediaPlacer"/> (download placement) and the metadata ChangeLibrary /
/// ConvertType re-ingest. Two modes:
/// <list type="bullet">
///   <item>By explicit library id (the <c>Id</c> the app sends as <c>LibraryId</c>) ⇒ that library's
///   first <c>Location</c>.</item>
///   <item>By category (Auto placement) ⇒ the FIRST library whose collection type matches
///   (Series→tvshows, Movie→movies), in Jellyfin's own ordering — deterministic primary.</item>
/// </list>
/// Every lookup reads <see cref="ILibraryManager.GetVirtualFolders()"/> LIVE per call (never a cached
/// snapshot) — the W-065 discriminator: only the live virtual-folder list says what a library
/// genuinely is. A library may span several <c>Locations</c>; we always pick the first (its primary
/// root). Resolution failures return a null root + a specific, per-library message (W-049 / I-119:
/// that message flows to the job's ErrorMessage via the generic catch — no new error field).
/// </summary>
public interface ILibraryRootResolver
{
    /// <summary>
    /// Resolves the placement root for a job: an explicit <paramref name="libraryId"/> wins; otherwise
    /// the category picks the first matching-type library. Reads libraries live.
    /// </summary>
    /// <param name="libraryId">The explicit library id (VirtualFolderInfo.ItemId), or null for Auto.</param>
    /// <param name="category">The job's resolved category, used only when <paramref name="libraryId"/> is null.</param>
    /// <returns>The resolved root and a null error, or a null root and a specific error message.</returns>
    (string? Root, string? Error) Resolve(string? libraryId, MediaCategory category);

    /// <summary>
    /// Resolves the placement root for an explicit library id only (the ChangeLibrary case). Reads
    /// libraries live.
    /// </summary>
    /// <param name="libraryId">The target library id (VirtualFolderInfo.ItemId).</param>
    /// <returns>The resolved root and a null error, or a null root and a specific error message.</returns>
    (string? Root, string? Error) ResolveById(string libraryId);
}

/// <summary>
/// Production <see cref="ILibraryRootResolver"/> backed by Jellyfin's <see cref="ILibraryManager"/>.
/// </summary>
public sealed class LibraryRootResolver : ILibraryRootResolver
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="LibraryRootResolver"/> class.</summary>
    /// <param name="libraryManager">The Jellyfin library manager (source of virtual folders).</param>
    public LibraryRootResolver(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public (string? Root, string? Error) Resolve(string? libraryId, MediaCategory category) =>
        LibraryRootResolution.Resolve(_libraryManager.GetVirtualFolders(), libraryId, category);

    /// <inheritdoc />
    public (string? Root, string? Error) ResolveById(string libraryId) =>
        LibraryRootResolution.ResolveById(_libraryManager.GetVirtualFolders(), libraryId);
}

/// <summary>
/// The pure resolution rules behind <see cref="LibraryRootResolver"/>, operating on a plain list of
/// <see cref="VirtualFolderInfo"/> so they are unit-testable WITHOUT a live
/// <see cref="ILibraryManager"/> (W-057: the manager isn't injectable in a unit test, but this layer is).
/// The live-read discipline (W-065) lives in the resolver, which passes a freshly-queried list here.
/// </summary>
public static class LibraryRootResolution
{
    /// <summary>
    /// Resolves the placement root: an explicit <paramref name="libraryId"/> wins; otherwise the
    /// category picks the first matching-collection-type library, in list order.
    /// </summary>
    /// <param name="folders">The current virtual folders (queried live by the caller).</param>
    /// <param name="libraryId">The explicit library id, or null for category-driven Auto placement.</param>
    /// <param name="category">The job's resolved category (used only when <paramref name="libraryId"/> is null).</param>
    /// <returns>The resolved root and a null error, or a null root and a specific error message.</returns>
    public static (string? Root, string? Error) Resolve(IReadOnlyList<VirtualFolderInfo> folders, string? libraryId, MediaCategory category)
    {
        if (!string.IsNullOrWhiteSpace(libraryId))
        {
            return ResolveById(folders, libraryId!);
        }

        return ResolveByCategory(folders, category);
    }

    /// <summary>Resolves an explicit library id to its primary root, or a specific error.</summary>
    /// <param name="folders">The current virtual folders.</param>
    /// <param name="libraryId">The target library id (VirtualFolderInfo.ItemId).</param>
    /// <returns>The resolved root and a null error, or a null root and a specific error message.</returns>
    public static (string? Root, string? Error) ResolveById(IReadOnlyList<VirtualFolderInfo> folders, string libraryId)
    {
        var match = folders.FirstOrDefault(f =>
            !string.IsNullOrWhiteSpace(f.ItemId)
            && string.Equals(f.ItemId, libraryId, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return (null, $"No Jellyfin library matches the requested library id '{libraryId}'. "
                + "It may have been removed or renamed — refresh the library list (GET /Jellyfetch/Libraries) and retry.");
        }

        var root = FirstLocation(match);
        if (root is null)
        {
            return (null, $"The Jellyfin library '{match.Name}' has no folder configured, so JellyFetch "
                + "cannot place files into it. Add a folder to that library in Jellyfin, then retry.");
        }

        return (root, null);
    }

    /// <summary>
    /// Resolves the first library whose collection type matches the category, in list order
    /// (Series→tvshows, Movie/Other/Auto→movies). Returns a specific error when none exists.
    /// </summary>
    /// <param name="folders">The current virtual folders.</param>
    /// <param name="category">The category to match.</param>
    /// <returns>The resolved root and a null error, or a null root and a specific error message.</returns>
    public static (string? Root, string? Error) ResolveByCategory(IReadOnlyList<VirtualFolderInfo> folders, MediaCategory category)
    {
        var wanted = category switch
        {
            MediaCategory.Series => CollectionTypeOptions.tvshows,
            MediaCategory.Movie => CollectionTypeOptions.movies,
            // Auto should have been classified by completion; Other has no canonical Jellyfin type.
            // Both resolve to the movies library — the historical "unclassifiable ⇒ movie root" rule,
            // now expressed against the movies library rather than a configured fallback path.
            _ => CollectionTypeOptions.movies,
        };

        var match = folders.FirstOrDefault(f => f.CollectionType == wanted && FirstLocation(f) is not null);

        if (match is null)
        {
            var typeName = wanted == CollectionTypeOptions.tvshows ? "TV Shows (tvshows)" : "Movies (movies)";
            return (null, $"No Jellyfin library of type {typeName} with a folder is configured, so JellyFetch "
                + $"cannot auto-place this {category} content. Create such a library in Jellyfin (or submit with an "
                + "explicit LibraryId), then retry.");
        }

        return (FirstLocation(match), null);
    }

    /// <summary>Returns a library's primary root — the first non-blank of its <c>Locations</c> — or null when it has none.</summary>
    /// <param name="folder">The virtual folder.</param>
    /// <returns>The first location, or null.</returns>
    public static string? FirstLocation(VirtualFolderInfo folder)
    {
        if (folder.Locations is { Length: > 0 } locations)
        {
            foreach (var loc in locations)
            {
                if (!string.IsNullOrWhiteSpace(loc))
                {
                    return loc;
                }
            }
        }

        return null;
    }
}
