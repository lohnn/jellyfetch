using System;
using System.Collections.Generic;
using Jellyfetch.Plugin.Api;

namespace Jellyfetch.Plugin.Tests;

/// <summary>
/// Contract proof for the <see cref="ConvertTypeResult"/> outcomes the app rebinds against after the
/// live convert-rebind bug (the re-typed item reappeared but the app kept showing a stale item, and a
/// second convert hit the dead id → misleading "no video files").
///
/// The server-side conversion itself is correct (Jellyfin re-types + displays the item properly); the
/// fix is (1) an honest 409 "superseded" outcome for a stale re-convert instead of the generic
/// no-files message, and (2) a deterministic by-PATH rebind — the result now carries the destination
/// <see cref="ConvertTypeResultDto.ItemDirectory"/> + <see cref="ConvertTypeResultDto.MovedPaths"/> and
/// points the client at GET /Metadata/Items/ByPath (robust where a post-rescan title search drifts).
/// These factories are pure, so they are unit-testable without a live Jellyfin server.
/// </summary>
public sealed class ConvertTypeResultTests
{
    [Fact]
    public void RescanPending_carries_the_by_path_rebind_keys()
    {
        var id = Guid.NewGuid();
        var moved = new List<string> { "/media/movies/Some Film (2021)/Some Film (2021).mkv" };
        var itemDir = "/media/movies/Some Film (2021)";

        var result = ConvertTypeResult.RescanPending(id, "Movie", "/media/movies", moved, itemDir, "Some Film");

        Assert.Equal(ConvertTypeResult.ConvertTypeOutcome.RescanPendingOutcome, result.Outcome);
        Assert.Null(result.Error);
        var dto = Assert.IsType<ConvertTypeResultDto>(result.Dto);

        Assert.Equal(id.ToString("N"), dto.SourceItemId);
        Assert.Equal("Movie", dto.TargetType);
        Assert.Equal("RescanPending", dto.Status);
        Assert.Equal("/media/movies", dto.NewLibraryRoot);
        Assert.Equal(itemDir, dto.ItemDirectory);
        Assert.Equal(moved, dto.MovedPaths);
        Assert.Equal("Some Film", dto.Title);
    }

    [Fact]
    public void RescanPending_message_points_at_the_by_path_endpoint_not_a_title_search()
    {
        var result = ConvertTypeResult.RescanPending(
            Guid.NewGuid(), "Movie", "/media/movies",
            new List<string> { "/media/movies/X/X.mkv" }, "/media/movies/X", "X");

        var message = result.Dto!.Message!;
        // The reliable rebind is by path — the message must steer the client there, and warn off the
        // deleted SourceItemId. (Pre-fix it pointed at Items?searchTerm= which drifts after rescan.)
        Assert.Contains("/Jellyfetch/Metadata/Items/ByPath", message, StringComparison.Ordinal);
        Assert.Contains("SourceItemId", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RescanPending_url_encodes_the_item_directory_in_the_hint()
    {
        var result = ConvertTypeResult.RescanPending(
            Guid.NewGuid(), "Movie", "/media/movies",
            new List<string> { "/media/movies/A B/A B.mkv" }, "/media/movies/A B", "A B");

        // A space in the path must be percent-encoded in the poll URL hint (not a raw space).
        Assert.Contains("A%20B", result.Dto!.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Superseded_is_its_own_outcome_with_an_actionable_message()
    {
        var result = ConvertTypeResult.Superseded("already moved/converted — refresh to see the current item");

        Assert.Equal(ConvertTypeResult.ConvertTypeOutcome.SupersededOutcome, result.Outcome);
        Assert.Null(result.Dto);
        Assert.NotNull(result.Error);
        Assert.Contains("refresh", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Superseded_is_distinct_from_Rejected_so_the_controller_can_return_409_not_400()
    {
        // The stale-item case must map to 409 (Superseded), NOT the generic 400 (Rejected) that other
        // validation failures use — the app maps 409 → "re-resolve by path" rather than a hard error.
        Assert.NotEqual(
            ConvertTypeResult.Rejected("x").Outcome,
            ConvertTypeResult.Superseded("y").Outcome);
    }
}
