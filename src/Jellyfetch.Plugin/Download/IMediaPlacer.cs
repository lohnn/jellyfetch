using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfetch.Plugin.Download;

/// <summary>
/// Places completed downloads into the Jellyfin library roots with names metadata providers can match.
/// The core registers <c>NaiveMediaPlacer</c>, which is the production placer in practice: by design
/// each download backend lays out its own naming/subtree in staging and returns
/// <see cref="DownloadResult.PreLaidOut"/> = true, and the placer moves that tree verbatim under the
/// correct library root (with a write-permission pre-flight). Its own Series/Movie/Other scheme is a
/// correct fallback applied only to non-pre-laid-out inputs. This is the settled architecture — see
/// AGENTS.md "Naming &amp; placement" — not a placeholder awaiting replacement.
/// </summary>
public interface IMediaPlacer
{
    /// <summary>
    /// Moves the files in <paramref name="result"/> from staging into the appropriate library root.
    /// </summary>
    /// <param name="result">The completed download result (files still in staging).</param>
    /// <param name="stagingDirectory">The staging directory the files live under.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final absolute paths of the placed files.</returns>
    Task<PlacementResult> PlaceAsync(DownloadResult result, string stagingDirectory, CancellationToken cancellationToken);
}

/// <summary>Result of a placement operation.</summary>
public class PlacementResult
{
    /// <summary>Gets or sets the final absolute paths of all placed files.</summary>
    public IReadOnlyList<string> FinalPaths { get; set; } = new List<string>();

    /// <summary>Gets or sets the library root the files were placed under (for scoped library scan).</summary>
    public string? LibraryRootUsed { get; set; }
}
