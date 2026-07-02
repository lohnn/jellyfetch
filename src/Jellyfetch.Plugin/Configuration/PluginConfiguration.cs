using MediaBrowser.Model.Plugins;

namespace Jellyfetch.Plugin.Configuration;

/// <summary>
/// Plugin configuration for JellyFetch.
/// Config keys are part of the contract shared with backend capabilities — treat renames as breaking.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the library root where series/episode content is placed.</summary>
    public string SeriesLibraryPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the library root where movie content is placed.</summary>
    public string MovieLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library root for content that cannot be classified as series or movie
    /// (e.g. one-off YouTube videos). Falls back to <see cref="MovieLibraryPath"/> when empty.
    /// </summary>
    public string FallbackLibraryPath { get; set; } = string.Empty;

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
}
