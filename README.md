# JellyFetch

Turn Jellyfin into a URL-driven download manager: share an SVT Play / YouTube link, a
`magnet:` URI, or a `.torrent` file from your phone — JellyFetch downloads it and files it
into your library with names the metadata providers can match.

- Pure Jellyfin plugin (server 10.11.x, .NET 9) — no sidecar service.
- Web media via `yt-dlp` / `svtplay-dl` (installed on the host, paths configurable).
- Torrents via embedded MonoTorrent — download-and-done, no seeding.
- Companion Android app: share links straight from other apps, watch progress.

## Install

### From plugin repository

1. Build (or download) a release: `./build.sh` → `dist/jellyfetch_<version>.zip` + `dist/manifest.json`.
2. Host the `dist/` directory anywhere HTTP (e.g. `cd dist && python3 -m http.server 8000`),
   set `BASE_URL` when building if it's not `http://localhost:8000`.
3. Jellyfin Dashboard → Plugins → Repositories → add `http://<host>:8000/manifest.json`.
4. Catalog → install **JellyFetch** → restart Jellyfin.

### Manual

Unzip `dist/jellyfetch_<version>.zip` into `<jellyfin data>/plugins/JellyFetch/` and restart.

> **Permissions (manual install):** the jellyfin service user must own the plugin folder. After a
> manual unzip the files are usually owned by `root`, and Jellyfin **fails to start (white screen)**
> because it can't rewrite `meta.json` on boot. Fix:
> ```bash
> sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/JellyFetch
> sudo chmod -R u+rwX /var/lib/jellyfin/plugins/JellyFetch
> sudo systemctl restart jellyfin
> ```
> Installing from the repository (above) avoids this — the installer sets ownership. Keep exactly
> one plugin folder named `JellyFetch` (don't leave a stray lowercase `jellyfetch/` beside it).

## Setup

1. Install downloader tools on the Jellyfin host: `pip install yt-dlp svtplay-dl`.
2. Dashboard → Plugins → JellyFetch: set the **series library path**, **movie library path**,
   optional fallback path, tool paths, max concurrent downloads, torrent listen port.
3. Create an API key (Dashboard → API Keys) for the Android app / scripts.

> **Permissions (library + staging dirs):** the jellyfin user must be able to **write** every path
> you configure (staging, series/movie/fallback library roots). If not, the server runs fine but
> **downloads fail** at the staging/placement step. For each configured dir:
> ```bash
> sudo chown -R jellyfin:jellyfin /path/to/dir && sudo chmod -R u+rwX /path/to/dir
> ```

## Use

```bash
# Submit a URL
curl -X POST -H 'X-Emby-Token: KEY' -H 'Content-Type: application/json' \
     -d '{"Url": "https://www.svtplay.se/video/..."}' http://server:8096/Jellyfetch/Downloads

# Watch it
curl -H 'X-Emby-Token: KEY' http://server:8096/Jellyfetch/Downloads
```

Full API: [docs/api.md](docs/api.md).

## Development

See [AGENTS.md](AGENTS.md) for architecture, contracts, and directory ownership.

```bash
dotnet build Jellyfetch.sln
./build.sh
```

CI: GitHub Actions builds, tests, and uploads the plugin zip on every push/PR touching plugin
paths (`.github/workflows/plugin-ci.yml`); the Android app has its own sibling workflow. Each
push to `master` also refreshes a rolling prerelease — the plugin under the **`plugin-latest`**
tag, the APK under **`android-latest`** — so the latest builds are downloadable from the repo's
Releases page.
