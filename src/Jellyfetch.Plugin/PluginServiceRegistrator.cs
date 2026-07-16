using Jellyfetch.Plugin.Api;
using Jellyfetch.Plugin.Download;
using Jellyfetch.Plugin.Jobs;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfetch.Plugin;

/// <summary>
/// Registers JellyFetch services into the Jellyfin server DI container.
/// Backend capabilities: add exactly ONE registration line each in the marked section below —
/// that is the only edit you make outside your own directory.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<JobStore>();
        serviceCollection.AddSingleton<ILibraryRootResolver, LibraryRootResolver>();
        serviceCollection.AddSingleton<IMediaPlacer, NaiveMediaPlacer>();
        serviceCollection.AddSingleton<DownloadJobManager>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DownloadJobManager>());
        serviceCollection.AddSingleton<LibraryMetadataService>();

        // ── Download backends (one line per capability) ─────────────────────────────
        // media-downloader:
        serviceCollection.AddSingleton<IDownloadHandler, Download.WebMedia.WebMediaDownloadHandler>();
        // torrent-engine:
        serviceCollection.AddSingleton<IDownloadHandler, Download.Torrents.TorrentDownloadHandler>();
        // ────────────────────────────────────────────────────────────────────────────
    }
}
