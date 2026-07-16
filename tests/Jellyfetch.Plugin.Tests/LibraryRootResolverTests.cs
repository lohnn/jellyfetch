using System;
using Jellyfetch.Plugin.Download;
using MediaBrowser.Model.Entities;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// Unit coverage for library-driven root resolution — the heart of the library-driven-placement
/// contract. Exercises the pure <see cref="LibraryRootResolution"/> rules against plain
/// <see cref="VirtualFolderInfo"/> lists (W-057: the live <c>ILibraryManager</c> isn't injectable in a
/// unit test, but this pure layer is; the resolver is a thin adapter that just queries the manager and
/// forwards to these rules). Proves: explicit-id → the library's first Location; a library spanning
/// multiple folders → the FIRST (primary); Auto + category → the first matching-collection-type library
/// in list order (deterministic when several of the same type exist); and the correct specific error on
/// missing / location-less / no-matching-type (I-119: the error string flows to the job's ErrorMessage
/// via the existing generic catch — no new error field).
/// </summary>
public sealed class LibraryRootResolverTests
{
    private static VirtualFolderInfo Lib(string name, string id, CollectionTypeOptions? type, params string[] locations) =>
        new()
        {
            Name = name,
            ItemId = id,
            CollectionType = type,
            Locations = locations,
        };

    // ── Explicit library id ──

    [Fact]
    public void Explicit_id_resolves_to_that_librarys_first_location()
    {
        var folders = new[]
        {
            Lib("Movies", "id-movies", CollectionTypeOptions.movies, "/media/movies"),
            Lib("TV Shows", "id-tv", CollectionTypeOptions.tvshows, "/media/tv"),
        };

        var (root, error) = LibraryRootResolution.ResolveById(folders, "id-tv");

        Assert.Null(error);
        Assert.Equal("/media/tv", root);
    }

    [Fact]
    public void Explicit_id_is_case_insensitive()
    {
        var folders = new[] { Lib("Movies", "ID-Movies", CollectionTypeOptions.movies, "/media/movies") };

        var (root, error) = LibraryRootResolution.ResolveById(folders, "id-movies");

        Assert.Null(error);
        Assert.Equal("/media/movies", root);
    }

    [Fact]
    public void Multi_location_library_resolves_to_the_first_location()
    {
        // A Jellyfin library can span several folders (VirtualFolderInfo.Locations[]). We deterministically
        // place into the FIRST (its primary root).
        var folders = new[]
        {
            Lib("TV Shows", "id-tv", CollectionTypeOptions.tvshows, "/media/tv", "/media/tv-archive", "/mnt/old-tv"),
        };

        var (root, error) = LibraryRootResolution.ResolveById(folders, "id-tv");

        Assert.Null(error);
        Assert.Equal("/media/tv", root);
    }

    [Fact]
    public void Multi_location_skips_leading_blank_and_takes_first_real_folder()
    {
        var folders = new[] { Lib("TV", "id-tv", CollectionTypeOptions.tvshows, "  ", "/media/tv") };

        var (root, error) = LibraryRootResolution.ResolveById(folders, "id-tv");

        Assert.Null(error);
        Assert.Equal("/media/tv", root);
    }

    [Fact]
    public void Explicit_id_that_matches_no_library_errors_specifically()
    {
        var folders = new[] { Lib("Movies", "id-movies", CollectionTypeOptions.movies, "/media/movies") };

        var (root, error) = LibraryRootResolution.ResolveById(folders, "id-ghost");

        Assert.Null(root);
        Assert.NotNull(error);
        Assert.Contains("id-ghost", error!, StringComparison.Ordinal);
        Assert.Contains("GET /Jellyfetch/Libraries", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_id_of_a_library_with_no_folder_errors_specifically()
    {
        var folders = new[] { Lib("Empty", "id-empty", CollectionTypeOptions.movies /* no locations */) };

        var (root, error) = LibraryRootResolution.ResolveById(folders, "id-empty");

        Assert.Null(root);
        Assert.NotNull(error);
        Assert.Contains("Empty", error!, StringComparison.Ordinal);
        Assert.Contains("no folder", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Auto (category-driven) ──

    [Fact]
    public void Auto_series_resolves_to_the_first_tvshows_library()
    {
        var folders = new[]
        {
            Lib("Movies", "id-movies", CollectionTypeOptions.movies, "/media/movies"),
            Lib("TV Shows", "id-tv", CollectionTypeOptions.tvshows, "/media/tv"),
        };

        var (root, error) = LibraryRootResolution.Resolve(folders, libraryId: null, MediaCategory.Series);

        Assert.Null(error);
        Assert.Equal("/media/tv", root);
    }

    [Fact]
    public void Auto_movie_resolves_to_the_first_movies_library()
    {
        var folders = new[]
        {
            Lib("TV Shows", "id-tv", CollectionTypeOptions.tvshows, "/media/tv"),
            Lib("Movies", "id-movies", CollectionTypeOptions.movies, "/media/movies"),
        };

        var (root, error) = LibraryRootResolution.Resolve(folders, libraryId: null, MediaCategory.Movie);

        Assert.Null(error);
        Assert.Equal("/media/movies", root);
    }

    [Fact]
    public void Auto_with_multiple_same_type_libraries_picks_the_first_in_list_order()
    {
        // Deterministic primary: Jellyfin's own ordering. The FIRST movies library wins; there is no
        // per-type default config setting.
        var folders = new[]
        {
            Lib("Movies (Primary)", "id-m1", CollectionTypeOptions.movies, "/media/movies"),
            Lib("Movies (Second)", "id-m2", CollectionTypeOptions.movies, "/media/movies2"),
        };

        var (root, error) = LibraryRootResolution.Resolve(folders, libraryId: null, MediaCategory.Movie);

        Assert.Null(error);
        Assert.Equal("/media/movies", root);
    }

    [Fact]
    public void Auto_other_resolves_to_the_movies_library()
    {
        // Unclassifiable content resolves to the movies library — the historical "Other ⇒ movie root".
        var folders = new[]
        {
            Lib("TV Shows", "id-tv", CollectionTypeOptions.tvshows, "/media/tv"),
            Lib("Movies", "id-movies", CollectionTypeOptions.movies, "/media/movies"),
        };

        var (root, error) = LibraryRootResolution.Resolve(folders, libraryId: null, MediaCategory.Other);

        Assert.Null(error);
        Assert.Equal("/media/movies", root);
    }

    [Fact]
    public void Auto_skips_a_matching_type_library_that_has_no_folder()
    {
        // A tvshows library with no location can't be a placement target; fall through to the next one.
        var folders = new[]
        {
            Lib("Broken TV", "id-broken", CollectionTypeOptions.tvshows /* no locations */),
            Lib("Real TV", "id-tv", CollectionTypeOptions.tvshows, "/media/tv"),
        };

        var (root, error) = LibraryRootResolution.Resolve(folders, libraryId: null, MediaCategory.Series);

        Assert.Null(error);
        Assert.Equal("/media/tv", root);
    }

    [Fact]
    public void Auto_series_with_no_tvshows_library_errors_specifically()
    {
        var folders = new[] { Lib("Movies", "id-movies", CollectionTypeOptions.movies, "/media/movies") };

        var (root, error) = LibraryRootResolution.Resolve(folders, libraryId: null, MediaCategory.Series);

        Assert.Null(root);
        Assert.NotNull(error);
        Assert.Contains("TV Shows", error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LibraryId", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_id_supersedes_category_in_the_combined_resolve()
    {
        // With BOTH a libraryId and a category, the explicit id wins (its root, regardless of category).
        var folders = new[]
        {
            Lib("Movies", "id-movies", CollectionTypeOptions.movies, "/media/movies"),
            Lib("TV Shows", "id-tv", CollectionTypeOptions.tvshows, "/media/tv"),
        };

        var (root, error) = LibraryRootResolution.Resolve(folders, libraryId: "id-tv", MediaCategory.Movie);

        Assert.Null(error);
        Assert.Equal("/media/tv", root); // the id's root, not the movies root the category would pick
    }

    [Fact]
    public void A_null_collection_type_library_never_matches_auto()
    {
        // A plain/undeclared library (CollectionType == null) is not a valid Auto target for any category.
        var folders = new[]
        {
            Lib("Photos", "id-photos", type: null, "/media/photos"),
            Lib("Movies", "id-movies", CollectionTypeOptions.movies, "/media/movies"),
        };

        var (root, error) = LibraryRootResolution.Resolve(folders, libraryId: null, MediaCategory.Movie);

        Assert.Null(error);
        Assert.Equal("/media/movies", root);
    }
}
