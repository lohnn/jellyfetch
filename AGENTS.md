# JellyFetch — Jellyfin download-manager plugin

A **pure Jellyfin plugin** (C#/.NET class library, no sidecar) that turns Jellyfin into a
URL-driven download manager: share an SVT Play / YouTube / magnet / .torrent link from a phone,
the plugin downloads it and files it into the library with metadata-matchable names.

**Monorepo**: the companion Android app lives in `android/` (owned by `android-share`; the old
separate jellyfetch-android repo is archived). Everything else in the repo is the plugin.
Remote: `https://github.com/lohnn/jellyfetch` (private, branch `master`).

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
| `android/`, `.github/workflows/android-ci.yml` | **android-share** |

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

### Config page directory picker (`Configuration/configPage.html`)

The 4 path fields keep an editable text box **and** a "Browse" button. Jellyfin 10.11's native
`directorybrowser` component is bundled as an **internal webpack module (numeric id, no AMD/string
import)** and is **not reachable from an embedded plugin page** — the classic
`require(['components/directorybrowser/directorybrowser'])` no longer resolves. So the page ships a
small self-contained picker dialog built on the stable **public ApiClient methods** the native
dialog uses under the hood: `ApiClient.getDrives()`, `ApiClient.getDirectoryContents(path, {includeDirectories:true, includeFiles:false})`,
`ApiClient.getParentPath(path)` (→ `/Environment/Drives`, `/DirectoryContents`, `/ParentPath`;
all return `[{Name,Path,Type}]` or a parent string). Styled with theme CSS variables so it inherits
the skin. Don't reach for the native component again without re-verifying it's importable — it wasn't
in 10.11.

**Mobile gotchas (fixed after a field bug where the dialog showed no folder list on phones):**
(1) the page **must** declare `<meta name="viewport" content="width=device-width, initial-scale=1">`
— without it a mobile browser lays the config page out at a ~980px layout viewport and a
`position:fixed` overlay sized to that inflated box pushes its scrollable list off the real screen.
(2) the dialog is sized to the visual viewport (`100dvw`/`100dvh`, `vw`/`vh` fallback),
`max-width:560px`, so it fits narrow screens; the scrollable list uses `flex:1 1 auto` **plus a
`min-height`** so it neither collapses to zero nor overflows the dialog. (3) a failed/empty
`getDrives()`/`getDirectoryContents()` shows a visible message row (and a manual `/` root), never a
blank dialog. Verified live in real Chromium at desktop + iPhone/Pixel viewports — when touching
this dialog, re-run an actual mobile-viewport browser check; CSS-only reasoning missed this once.

## CI

GitHub Actions, two **sibling workflows** in `.github/workflows/` — path-filtered so plugin and
Android changes build independently:

- `plugin-ci.yml` (owned by **jellyfin-plugin**): triggers on push to `master` / PR /
  `workflow_dispatch` for `src/**`, `tests/**`, `Jellyfetch.sln`, `build.sh`, `docs/**`.
  Runs `dotnet test` (the 3 `JELLYFETCH_LIVE`-gated tests self-skip in CI; expected 83 pass /
  3 skip), then `./build.sh`, and uploads `dist/jellyfetch_*.zip` + `manifest.json` as the
  `jellyfetch-plugin` artifact (30-day retention). On push to `master` (not PRs) it also
  publishes a **rolling prerelease** on the fixed tag **`plugin-latest`** (via
  `softprops/action-gh-release@v2`, needs `permissions: contents: write`) — the release assets
  are replaced in place each build so the zip + manifest are grabbable from the repo main page.
- `android-ci.yml` (owned by **android-share**): path-filtered to `android/**`; publishes the
  equivalent rolling APK release under tag **`android-latest`** (separate release by design).

If you fix something shared-shaped in one workflow (trigger shape, artifact settings), mirror it
in the sibling. CI provisions .NET via `actions/setup-dotnet` — the `/usr/local/dotnet` PATH shim
mentioned under Build is a dev-host-only shim (guarded inside `build.sh`).

## Testing / smoke test

Compiling proves nothing about a plugin that loads. Smoke test = run a real Jellyfin 10.11 server
with the built plugin in `<data>/plugins/JellyFetch/` and hit `/Jellyfetch/Ping` with an API key.
See README for the local run recipe. Report honestly what was verified live vs compile-only.

## Deployment runbook — directory permissions (self-hosted)

The jellyfin **service user** must be able to write **every** directory the plugin touches. This
bites in two separate places that present as two different failures (both verified on a real
self-hosted Debian arm64 box, both non-obvious):

**1. Plugin install dir — server white-screens on boot.**
- Symptom: after a manual unzip, Jellyfin fails to start (white screen / "issue starting it up").
- Cause: unzipped files are owned by `root`; on startup `PluginManager` rewrites `meta.json`
  (`SaveManifest`) and the permission-denied throws `UnauthorizedAccessException` in `FindParts()`
  → **fatal, whole server down**. Misleading: all assemblies load fine first, so it looks like a
  plugin-load/packaging bug but is purely ownership.
- Fix:
  ```bash
  sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/JellyFetch
  sudo chmod -R u+rwX /var/lib/jellyfin/plugins/JellyFetch
  sudo systemctl restart jellyfin
  ```
- Avoid it: install via the plugin **repository** (`manifest.json`) rather than a manual unzip —
  the installer sets ownership correctly.

**2. Staging + library target dirs — server runs, downloads fail.**
- Symptom: server healthy, but every download fails at the staging/placement step.
- Cause: a user-created dir referenced by `StagingPath` / `SeriesLibraryPath` / `MovieLibraryPath` /
  `FallbackLibraryPath` isn't writable by the jellyfin user. The repository installer does **not**
  help here — these are user-chosen paths.
- Fix (per configured path):
  ```bash
  sudo chown -R jellyfin:jellyfin /path/to/dir && sudo chmod -R u+rwX /path/to/dir
  ```

**Folder-name collision:** keep exactly one plugin folder, canonical name **`JellyFetch`**. Having
both `plugins/jellyfetch/` (lowercase) and `plugins/JellyFetch/` can confuse `PluginManager`.
