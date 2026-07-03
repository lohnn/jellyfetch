# Torrents backend — owned by `torrent-engine`

BitTorrent transfer backend for JellyFetch, built on **MonoTorrent 3.0.2**. Implements
`Jellyfetch.Plugin.Download.IDownloadHandler` with `Kind == "torrent"`: magnet URIs and uploaded
`.torrent` files, metadata resolution, download-and-done (no seeding), progress into `JobProgress`.

## Files

| File | Responsibility |
|---|---|
| `TorrentDownloadHandler.cs` | The `IDownloadHandler`. Ingestion (magnet / `.torrent` bytes), the download loop, progress mapping, junk filtering, completion handoff. |
| `TorrentEngineHost.cs` | Owns the single shared `ClientEngine` for the plugin lifetime (lazy-created, disposed on teardown). One engine hosts many `TorrentManager`s — one per job. |
| `ReleaseNameParser.cs` | Pure, standalone scene/community release-name parser → `ParsedRelease` (title/year/season/episode/confidence). Never throws. |

## Behaviour

- **CanHandle**: `magnet:` in `DownloadRequest.SourceUrl`, or `TorrentFileBase64` present.
- **ResolveAsync**: returns **one** item. The true file/episode list is only known after peers
  deliver metadata (post-handshake), so there is no resolve-time fan-out. Season packs download as
  one job and are self-laid-out per episode at completion.
- **ExecuteAsync**:
  - Adds the torrent to the shared engine (`Torrent.Load(bytes)` for `.torrent`, `MagnetLink` for magnets).
  - Magnets pass through a **resolving-metadata** phase (`StatusText = "Resolving metadata"`,
    `Percent = null`) until `HasMetadata`, then stream `Percent / SpeedBps / EtaSeconds /
    DownloadedBytes / TotalBytes / Title` every second.
  - Stops the moment the payload is `Complete` — **never seeds**.
  - Cancellation → `StopAsync` + `RemoveAsync(CacheDataAndDownloadedData)` + `throw OperationCanceledException`.
  - Any failure throws (manager marks the job `Failed`, retryable); never crashes the server.
- **Completion**:
  - **Junk filtered** — `.nfo/.txt/.sfv/.jpg/...`, `sample` files, etc.
  - **Single episode / movie** → `PreLaidOut = false`; the core placer names it from `MediaMetadata`.
  - **Season pack** (multiple episodes) → self-laid-out `{Series}/Season NN/{Series} - SxxEyy.ext`
    with `PreLaidOut = true`; the placer moves the tree verbatim.
  - **Low-confidence parse** (< 0.5) → `Category = Other` (fallback root) rather than mis-file.

## Config

`Plugin.Instance.Configuration.TorrentListenPort` (default 6881) — applied to the engine's listen +
DHT endpoints. Engine cache (fast-resume / magnet metadata / DHT) lives under the staging/data root.

## Edits outside this directory (as specced)

1. One registration line in `PluginServiceRegistrator.cs` (fully-qualified
   `Download.Torrents.TorrentDownloadHandler`).
2. One `<PackageReference Include="MonoTorrent" Version="3.0.2" />` in `Jellyfetch.Plugin.csproj`.

## Tests

`tests/Jellyfetch.Plugin.Tests/ReleaseNameParserTests.cs` — 36 cases covering standard/multi/`1x02`
episodes, season packs, movies, year-in-title, Swedish names, junk/low-confidence, tag-bleed.

## Verified

- `dotnet build Jellyfetch.sln` — clean (0 warnings, 0 errors).
- Full test suite — 82 passed / 3 skipped (live-tool) / 0 failed.
- **Live**: the MonoTorrent flow (magnet + `.torrent`, metadata resolution, progress, clean cancel,
  fast-resume) was validated in a standalone spike against a real Debian 13.5 torrent (65 peers,
  ~5.6 MB/s). **Not** yet smoke-tested inside a running Jellyfin server.
