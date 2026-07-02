# JellyFetch — Jellyfin download-manager plugin

A **pure Jellyfin plugin** (C#/.NET class library, no sidecar) that turns Jellyfin into a
URL-driven download manager: share an SVT Play / YouTube / magnet / .torrent link from a phone,
the plugin downloads it and files it into the library with metadata-matchable names.
Companion Android app lives in `projects/jellyfetch-android/` (capability `android-share`).

## Verified ground truth (2026-07-02)

- Target server: **Jellyfin 10.11.x** (current stable 10.11.11). **TFM: net9.0**.
- NuGet: `Jellyfin.Controller` + `Jellyfin.Model` **10.11.11** with `<ExcludeAssets>runtime</ExcludeAssets>`.
  The NuGet pin follows the *server* version you target — the official template's pin lags, don't copy it.
- Plugin shape: `BasePlugin<PluginConfiguration>` + `IHasWebPages`; ctor `(IApplicationPaths, IXmlSerializer)`.
- DI + background services: `IPluginServiceRegistrator.RegisterServices(IServiceCollection, IServerApplicationHost)`
  (parameterless ctor); register `IHostedService` there for the job-queue pump.
- API controllers: plain `ControllerBase` with route attributes, discovered automatically from the
  plugin assembly. Requires `<FrameworkReference Include="Microsoft.AspNetCore.App" />`.
- Auth policy: `[Authorize(Policy = "RequiresElevation")]` (string literal; `DefaultAuthorization`
  was removed in 10.9). Jellyfin API keys are elevated.
- Server JSON: PascalCase property names; we serialize enums to strings explicitly in DTOs.

## Build

```bash
export PATH=/usr/local/dotnet:$PATH   # this dev host; SDK 9.0.x
dotnet build Jellyfetch.sln           # compile
./build.sh                            # release build + plugin zip + repo manifest.json → dist/
```

Plugin GUID: `3ed77eb2-77c8-49c9-8a14-8cfcb86cb6f3`.

## Architecture

```
Submission (REST) ──► DownloadJobManager (queue, concurrency, persistence)
                         │  CanHandle → first matching IDownloadHandler
                         │  ResolveAsync (state Resolving; 1..N items; N>1 ⇒ fan out into child jobs)
                         │  ExecuteAsync (state Downloading; staging dir; IProgress<JobProgress>)
                         │  IMediaPlacer.PlaceAsync (state Processing; move staging → library root)
                         └► ILibraryMonitor.ReportFileSystemChanged(finalPath)  (scoped scan)
```

- Job state persists to `<jellyfin-data>/jellyfetch/jobs.json` (atomic write, throttled ~3 s).
  On restart: mid-flight jobs → `Failed` ("Interrupted by server restart", retryable); `Queued` resumes.
- Every handler call is exception-wrapped: a failing backend produces a `Failed` job, never an
  unhandled exception in the Jellyfin process.
- Staging: `<data>/jellyfetch/staging/<jobId>/`, deleted after placement/cancel/failure.

## Contracts (owned by jellyfin-plugin — changes are breaking, announce via HIVEmind)

1. **REST API** — `docs/api.md` (consumed by `android-share`). Wire truth: `Api/DownloadsController.cs` + `Api/JobDto.cs`.
2. **`IDownloadHandler`** — `src/Jellyfetch.Plugin/Download/IDownloadHandler.cs` + `DownloadModels.cs`
   (implemented by `media-downloader` and `torrent-engine`).

## Directory ownership (pre-settled — do not edit outside your area)

| Path | Owner |
|---|---|
| repo root, `Plugin.cs`, `PluginServiceRegistrator.cs`, `Api/`, `Jobs/`, `Configuration/`, `Download/*.cs` (contract files + NaiveMediaPlacer), `docs/`, `build.sh`, packaging | **jellyfin-plugin** |
| `src/Jellyfetch.Plugin/Download/WebMedia/` | **media-downloader** |
| `src/Jellyfetch.Plugin/Download/Torrents/` | **torrent-engine** |

Shared-file exceptions: backends each add **one registration line** in the marked section of
`PluginServiceRegistrator.cs`, and may add their own `<PackageReference>` (e.g. MonoTorrent) to
`Jellyfetch.Plugin.csproj`. Nothing else outside your directory. `media-downloader` may additionally
replace the `IMediaPlacer` registration with its production naming implementation (it owns naming
conventions; `NaiveMediaPlacer` is the placeholder default).

## Config keys (PluginConfiguration — part of the shared contract)

`SeriesLibraryPath`, `MovieLibraryPath`, `FallbackLibraryPath` (unclassifiable content; empty ⇒ movie path),
`StagingPath` (empty ⇒ data-dir default), `YtDlpPath` (default `yt-dlp`), `SvtPlayDlPath` (default
`svtplay-dl`), `MaxConcurrentDownloads` (default 2), `TorrentListenPort` (default 6881).
Read live via `Plugin.Instance.Configuration` — config changes apply without restart (values are
read per-use, not cached at startup).

## Testing / smoke test

Compiling proves nothing about a plugin that loads. Smoke test = run a real Jellyfin 10.11 server
with the built plugin in `<data>/plugins/JellyFetch/` and hit `/Jellyfetch/Ping` with an API key.
See README for the local run recipe. Report honestly what was verified live vs compile-only.
