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
./build.sh                            # release build → dist/
```

`./build.sh [version]` emits, into `dist/`: `jellyfetch_<version>.zip` (the plugin payload),
`version-entry.json` (the single manifest version-entry fragment the release workflow merges), and a
standalone single-entry `manifest.json` (for **local** install/testing only — the *published*
accumulating manifest lives on gh-pages, see [Distribution & releases](#distribution--releases)).

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
| repo root, `Plugin.cs`, `PluginServiceRegistrator.cs`, `Api/`, `Jobs/`, `Configuration/`, `Download/*.cs` (contract files + NaiveMediaPlacer), `docs/`, `build.sh`, `ci/` (release/merge/version scripts), `.github/workflows/plugin-ci.yml` + `plugin-release.yml`, packaging | **jellyfin-plugin** |
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

GitHub Actions, **three workflows** in `.github/workflows/` — path-filtered / dispatch-scoped so
plugin, stable-release, and Android concerns are independent:

- `plugin-ci.yml` (owned by **jellyfin-plugin**) — the **DEV channel**: triggers on push to
  `master` / PR / `workflow_dispatch` for `src/**`, `tests/**`, `Jellyfetch.sln`, `build.sh`,
  `docs/**`. Runs `dotnet test` (the live-tool tests self-skip in CI), then `./build.sh`, and
  uploads `dist/jellyfetch_*.zip` + `manifest.json` as the `jellyfetch-plugin` artifact (30-day
  retention). On push to `master` (not PRs) it also publishes a **rolling prerelease** on the
  fixed tag **`plugin-latest`** (via `softprops/action-gh-release@v2`, needs
  `permissions: contents: write`). This is bleeding-edge, NOT what users subscribe to.
- `plugin-release.yml` (owned by **jellyfin-plugin**) — the **STABLE channel**: manual
  `workflow_dispatch` only. Cuts a real `v<version>` release and updates the permanent gh-pages
  `manifest.json`. Full mechanics in **[Distribution & releases](#distribution--releases)** below.
- `android-ci.yml` (owned by **android-share**): path-filtered to `android/**`; publishes the
  equivalent rolling APK release under tag **`android-latest`** (separate release by design).

If you fix something shared-shaped in one workflow (trigger shape, artifact settings), mirror it
in the sibling. CI provisions .NET via `actions/setup-dotnet` — the `/usr/local/dotnet` PATH shim
mentioned under Build is a dev-host-only shim (guarded inside `build.sh`).

## Distribution & releases

**End users subscribe to ONE permanent URL** and get one-click updates from Jellyfin's Catalog:

```
https://lohnn.github.io/jellyfetch/manifest.json      # <owner>.github.io/<repo>/manifest.json
```

That URL is a Jellyfin **plugin-repository catalog** (`manifest.json`) hosted on the `gh-pages`
branch via GitHub Pages. The catalog is small JSON only — the plugin **zips ship as GitHub Release
assets**, referenced by each version entry's `sourceUrl`. The end-user-facing side of this lives in
[README.md](README.md#install--update); this section is the maintainer source of truth.

### Two channels

| Channel | Workflow | Trigger | Tag | Prerelease | Users subscribe? |
|---|---|---|---|---|---|
| **Stable** | `plugin-release.yml` | manual `workflow_dispatch` | `v<version>` | no | **yes** (gh-pages manifest) |
| **Dev** | `plugin-ci.yml` | every `master` push | `plugin-latest` (rolling) | yes | no (bleeding-edge grab) |

### The manifest schema (net-new knowledge — Jellyfin plugin-repo format)

`manifest.json` is a **JSON array** of plugin objects. Each plugin object carries identity fields
(`guid`, `name`, `description`, `overview`, `owner`, `category`, `imageUrl`) and a `versions` array.
Each **version entry** is:

```json
{
  "version": "1.2.0.0",
  "changelog": "freetext from the dispatch input",
  "targetAbi": "10.11.0.0",
  "sourceUrl": "https://github.com/lohnn/jellyfetch/releases/download/v1.2.0.0/jellyfetch_1.2.0.0.zip",
  "checksum": "<md5 of the zip>",
  "timestamp": "2026-07-04T14:01:03Z"
}
```

- **`checksum` is MD5** (Jellyfin verifies the downloaded zip against it — keep MD5, not SHA).
- **`sourceUrl` MUST be the real release-asset URL**, never localhost. The original distribution bug
  was exactly this: the CI-published manifest pointed `sourceUrl` at `http://localhost:8000`, so every
  repository install failed. The workflow now substitutes `github.repository` into the release URL and
  a merge-step sanity check fails the release if `sourceUrl` isn't a `…/releases/download/…` URL.
- **`targetAbi` is `10.11.0.0`.** An ABI mismatch on server 10.10+ silently *skips* the plugin (no
  crash, no error) — so a wrong bump here means "plugin invisible", not "plugin broken". Keep in sync
  with the server line (see Verified ground truth).
- The `versions` array is **accumulated, newest-first** — see below.

### How to cut a stable release

1. GitHub → **Actions → "Plugin Release (stable)" → Run workflow**.
2. Pick **`bump`**: `patch` (default) / `minor` / `major`. There is **no manual version typing** — CI
   computes it (next section).
3. Optionally fill **`changelog`** (freetext) — it lands in the version entry's `changelog` and the
   GitHub Release body. Quotes/newlines are JSON-escaped by `build.sh`.
4. Run. The workflow then:
   - resolves the next version (`ci/next-version.sh`),
   - writes it into the csproj `<Version>` (so `meta.json` + assembly version match),
   - `dotnet test` + `./build.sh` (produces the zip, MD5, and `dist/version-entry.json`),
   - **appends** that entry to the gh-pages `manifest.json` (`ci/merge-manifest.sh`), substituting the
     real release-asset `sourceUrl`,
   - creates the `v<version>` GitHub Release with the zip attached (`softprops/action-gh-release@v2`,
     `prerelease: false`, `permissions: contents: write` — the **I-110** must-haves, but a real tag),
   - force-pushes the updated catalog to `gh-pages` (orphan branch = manifest + a redirect `index.html`
     + `.nojekyll`, no build-history bloat),
   - commits the version bump back to `master` (`[skip ci]`) so the csproj fallback stays honest.

### How auto-versioning resolves "last released version"

`ci/next-version.sh <bump> <manifest.json> <csproj-fallback>`:

1. **Preferred:** the **highest** `version` across the plugin's `versions[]` in the *committed gh-pages*
   `manifest.json` (robust even if the on-disk array were unsorted) — the authoritative record of what
   actually shipped.
2. **Fallback:** the csproj `<Version>` (bootstrap / first-ever release, when gh-pages doesn't exist).

Versions use Jellyfin's 4-component `major.minor.patch.build`. Bump keeps `.build` at 0:
`patch → M.m.(p+1).0`, `minor → M.(m+1).0.0`, `major → (M+1).0.0.0`.

### One-time GitHub Pages enablement (repo settings)

The first stable release **creates** the `gh-pages` branch (the workflow force-pushes an orphan
branch). After that first run, enable Pages **once**:

- **Settings → Pages → Build and deployment → Source: "Deploy from a branch"**,
  **Branch: `gh-pages` / `/ (root)`**, Save.

Then `https://lohnn.github.io/jellyfetch/manifest.json` serves the catalog. (`.nojekyll` is included
so Pages serves the JSON verbatim.) Repo Actions also need **Settings → Actions → General → Workflow
permissions: Read and write** (the workflow declares `permissions: contents: write`, but the repo-level
setting must allow it).

### build.sh vs. workflow split (who owns what)

- **`build.sh`** owns the *build artifacts for one version*: the `dist/jellyfetch_<version>.zip`
  (payload = every DLL `dotnet publish` emits + `meta.json`; **the host-assembly leak guard stays —
  W-048, never add a DLL name-filter**), its **MD5**, and a single **`dist/version-entry.json`**
  fragment (with a `SOURCE_URL_PLACEHOLDER`). It also still emits a standalone single-entry
  `dist/manifest.json` for *local* testing, with `sourceUrl` defaulting to the release-asset shape
  (override with `BASE_URL` to point at a local `python3 -m http.server` zip).
- **The workflow** owns *accumulation & publication*: merging the fragment into the committed
  `manifest.json` and rewriting `sourceUrl` to the real release-asset URL.
- **`ci/merge-manifest.sh`** does the merge: locate the plugin by `guid`, **replace-if-same-version
  else append** (idempotent), sort `versions` **newest-first**. Prior versions are preserved for
  history/rollback (append-not-overwrite — the fix for the old single-entry overwrite).

**Test the merge/version logic locally** (no server needed):

```bash
./build.sh 1.0.0                                   # produces dist/version-entry.json + zip + md5
ci/merge-manifest.sh /tmp/absent.json dist/version-entry.json \
    https://github.com/lohnn/jellyfetch/releases/download/v1.0.0/jellyfetch_1.0.0.zip /tmp/m.json
ci/next-version.sh minor /tmp/m.json 0.1.0.0       # -> 1.1.0.0 (from manifest, not csproj)
```

Re-running the merge with the same version replaces (never duplicates) that entry; a lower version
merged after a higher one still sorts newest-first. Both are covered by the local desk-check above.

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
