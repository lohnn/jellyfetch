using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.Client;

namespace Jellyfetch.Plugin.Download.Torrents;

/// <summary>
/// Owns the single <see cref="ClientEngine"/> shared by all torrent jobs for the lifetime of the
/// plugin. MonoTorrent is designed around one engine hosting many <see cref="MonoTorrent.Client.TorrentManager"/>s;
/// creating an engine per job would fight over the listen port and DHT table.
///
/// The engine is created lazily on first use (so an idle server pays nothing) and disposed when the
/// plugin's DI scope is torn down. Access is guarded so concurrent jobs share one instance safely.
/// </summary>
public sealed class TorrentEngineHost : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClientEngine? _engine;
    private bool _disposed;

    /// <summary>
    /// Gets the shared engine, creating it on first call. The engine's cache directory is placed
    /// under <paramref name="cacheRoot"/>; the configured listen port is applied at creation time.
    /// </summary>
    /// <param name="cacheRoot">Root directory for the engine's fast-resume / metadata / DHT cache.</param>
    /// <param name="listenPort">TCP/UDP port for peer + DHT traffic.</param>
    /// <param name="cancellationToken">Cancellation for the (fast) creation critical section.</param>
    public async Task<ClientEngine> GetEngineAsync(string cacheRoot, int listenPort, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Fast path: already created.
        var existing = Volatile.Read(ref _engine);
        if (existing is not null)
            return existing;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_engine is not null)
                return _engine;

            Directory.CreateDirectory(cacheRoot);

            var builder = new EngineSettingsBuilder
            {
                CacheDirectory = cacheRoot,
                // Persist partial-download state so a job resumes after a restart instead of
                // re-hashing / re-downloading from zero.
                AutoSaveLoadFastResume = true,
                FastResumeMode = FastResumeMode.Accurate,
                // Persist magnet metadata so a resumed magnet job doesn't re-fetch it.
                AutoSaveLoadMagnetLinkMetadata = true,
                // Persist the DHT routing table for faster bootstrap on later runs.
                AutoSaveLoadDhtCache = true,
                // We are a client that downloads-and-done; keep the footprint modest.
                AllowPortForwarding = true,
                AllowLocalPeerDiscovery = true,
                MaximumConnections = 200,
            };

            // v3: listen endpoints are a dictionary (was a single ListenPort in v2).
            builder.ListenEndPoints["ipv4"] = new IPEndPoint(IPAddress.Any, listenPort);
            builder.ListenEndPoints["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, listenPort);
            builder.DhtEndPoint = new IPEndPoint(IPAddress.Any, listenPort);

            _engine = new ClientEngine(builder.ToSettings());
            return _engine;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _engine?.Dispose();
        }
        catch
        {
            // Disposal must never throw out of the plugin teardown path.
        }
        finally
        {
            _engine = null;
            _gate.Dispose();
        }
    }
}
