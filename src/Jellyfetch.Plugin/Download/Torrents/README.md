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
| `SeasonPackChildBuilder.cs` | Pure mapping from a parsed season pack + its video files → one `SeasonPackEpisode` spec per episode (source file, library-relative target path, per-episode metadata). One computation feeds BOTH the physical layout AND the post-download child fan-out, so they can't drift. |

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
    with `PreLaidOut = true`; the placer moves the tree verbatim. ALSO emits
    `DownloadResult.Children` (one library-relative `DownloadChild` per episode) so the dashboard
    shows a group parent with one Completed row per episode — parity with the web series fan-out.
    See **Post-download fan-out** below.
  - **Low-confidence parse** (< 0.5) → `Category = Other` (fallback root) rather than mis-file.

## Post-download fan-out (season packs → per-episode rows)

Web series fan out at **resolve** time: `ResolveAsync` returns N items and the manager spins up N
independent downloads. Torrents can't do that — a season pack is **one** physical torrent (one
`TorrentManager`, one `ExecuteAsync`), and the true file/episode list is only known **after** the
metadata handshake. So the fan-out happens at the **completion** boundary instead:

- `BuildSeasonPackResult` lays the tree out in staging (`PreLaidOut = true`) AND fills
  `DownloadResult.Children` with one `DownloadChild { RelativePath, Metadata }` per episode
  (`RelativePath` is **library-root-relative** — the handler can't know the library root at
  `ExecuteAsync` time; that's placer/config territory).
- The manager (jellyfin-plugin's `MaterializeCompletedChildren`, invoked only when
  `Children.Count > 1`) marks the job a group parent, resolves each `RelativePath` against
  `PlacementResult.LibraryRootUsed`, and materializes one **born-terminal Completed** display child
  per episode. No child re-runs `ExecuteAsync`; `RecomputeGroupState` aggregates them.
- A pack that resolved to a **single** episode keeps `Children.Count == 1` ⇒ the manager does **not**
  fan out (stays a single job). Single-file/movie torrents emit no `Children` at all.

The child specs are computed by the pure `SeasonPackChildBuilder`, unit-tested independently of any
torrent or filesystem.

## Config

`Plugin.Instance.Configuration.TorrentListenPort` (default 6881) — applied to the engine's listen +
DHT endpoints. Engine cache (fast-resume / magnet metadata / DHT) lives under the staging/data root.

## Edits outside this directory (as specced)

1. One registration line in `PluginServiceRegistrator.cs` (fully-qualified
   `Download.Torrents.TorrentDownloadHandler`).
2. One `<PackageReference Include="MonoTorrent" Version="3.0.2" />` in `Jellyfetch.Plugin.csproj`.

## Tests

- `tests/Jellyfetch.Plugin.Tests/ReleaseNameParserTests.cs` — 36 cases covering standard/multi/`1x02`
  episodes, season packs, movies, year-in-title, Swedish names, junk/low-confidence, tag-bleed.
- `tests/Jellyfetch.Plugin.Tests/SeasonPackChildBuilderTests.cs` — 11 cases: N-episode pack → N specs
  with correct per-episode metadata + library-relative paths; single-file → one spec (no fan-out);
  per-file season override; unpinnable-episode fallback; pack-title fallback; `ToChildren` mapping;
  the group-threshold guard (>1 fans out, ==1 doesn't).

## Verified

- `dotnet build Jellyfetch.sln` — clean (0 warnings, 0 errors).
- Full test suite — 170 passed / 4 skipped (live-tool) / 0 failed.
- **Live standalone spike**: the MonoTorrent flow (magnet + `.torrent`, metadata resolution,
  progress, clean cancel, fast-resume) was validated against a real Debian 13.5 torrent.
- **Live IN-SERVER (2026-07-04, Jellyfin 10.11.11 headless)** — the previously-open shadow is CLOSED:
  - Plugin loads clean (`Loaded plugin: JellyFetch`; job manager reports **2 handler(s)**); all
    MonoTorrent DLLs load; no white-screen.
  - A **Debian 13.5 magnet** ran Queued→Downloading→Completed: metadata resolved ~2 s, ~6 MB/s via
    **tracker** peers (DHT irrelevant — I-097), 735 MB placed exactly under the fallback root, staging
    auto-cleaned. `.torrent`-upload and mid-download **cancel** (partial data purged) both verified.
  - A **3-episode season pack** (`The.Office.US.S03…`, seeded locally over LPD) → group parent
    `Completed`, `ChildCount=3`, `"3/3 items finished"`, three Completed child rows with correct
    `S03E01/02/03` metadata and resolved absolute paths under the **series** root. Survives a server
    restart (children reload Completed, not resurrected mid-flight).
  - In-server behaviour matched the standalone spike exactly — the shared-engine / per-job-manager
    model works identically inside the Jellyfin process.
