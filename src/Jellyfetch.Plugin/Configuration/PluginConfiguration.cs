using MediaBrowser.Model.Plugins;

namespace Jellyfetch.Plugin.Configuration;

/// <summary>
/// Plugin configuration for JellyFetch.
/// Config keys are part of the contract shared with backend capabilities — treat renames as breaking.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // Library placement roots are NO LONGER configured here. As of the library-driven-placement phase
    // (docs/api.md "Library-driven placement (v2 contract)"), JellyFetch reads the libraries the user
    // already defined in Jellyfin (ILibraryManager.GetVirtualFolders) and resolves placement roots from
    // their collection type / chosen id via ILibraryRootResolver. The removed keys —
    // SeriesLibraryPath / MovieLibraryPath / FallbackLibraryPath — are simply ignored on existing
    // installs (unknown keys are dropped by the config deserializer); no migration is needed.

    /// <summary>Gets or sets the path or command name of the yt-dlp binary.</summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>Gets or sets the path or command name of the svtplay-dl binary.</summary>
    public string SvtPlayDlPath { get; set; } = "svtplay-dl";

    /// <summary>Gets or sets the maximum number of downloads executing concurrently.</summary>
    public int MaxConcurrentDownloads { get; set; } = 2;

    /// <summary>Gets or sets the TCP listen port used by the embedded torrent engine.</summary>
    public int TorrentListenPort { get; set; } = 6881;

    /// <summary>
    /// Gets or sets the staging directory downloads are written to before library placement.
    /// Empty means &lt;jellyfin data path&gt;/jellyfetch/staging.
    /// </summary>
    public string StagingPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets domain→tool routing overrides for the web-media downloader, one entry per
    /// line in the form <c>domain=tool</c> (e.g. <c>vimeo.com=yt-dlp</c>). Tool is one of
    /// <c>yt-dlp</c> / <c>svtplay-dl</c> (case-insensitive; <c>ytdlp</c> / <c>svtplay</c> also
    /// accepted). A host match on this map wins over the built-in defaults
    /// (svtplay.se / svt.se → svtplay-dl, everything else → yt-dlp). Empty ⇒ built-in defaults only.
    /// Read live per-use, so edits apply without a server restart.
    /// </summary>
    public string[] ToolRoutingOverrides { get; set; } = System.Array.Empty<string>();
}
