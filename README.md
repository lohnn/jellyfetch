# JellyFetch

Turn Jellyfin into a URL-driven download manager: share an SVT Play / YouTube link, a
`magnet:` URI, or a `.torrent` file from your phone — JellyFetch downloads it and files it
into your library with names the metadata providers can match.

- Pure Jellyfin plugin (server 10.11.x, .NET 9) — no sidecar service.
- Web media via `yt-dlp` / `svtplay-dl` (installed on the host, paths configurable).
- Torrents via embedded MonoTorrent — download-and-done, no seeding.
- Companion Android app: share links straight from other apps, watch progress.

## Install & update

### Recommended: add the plugin repository once (one-click updates forever)

Add JellyFetch's permanent repository URL to Jellyfin **once** — every future release then shows up
in the Catalog as a one-click update.

1. Jellyfin **Dashboard → Plugins → Repositories → +** and add:

   ```
   https://lohnn.github.io/jellyfetch/manifest.json
   ```

2. **Catalog → JellyFetch → Install**, then restart Jellyfin.
3. **Updates:** whenever a new stable version is released, Jellyfin's Catalog offers it — install and
   restart. No re-adding the repository, no manual downloads.

This path also **avoids the boot white-screen** that manual unzips can cause: the repository installer
writes the plugin files owned by the jellyfin service user, so the on-boot `meta.json` rewrite doesn't
hit a permission error. Prefer it.

> The repository URL is served from GitHub Pages and is a small JSON catalog (the plugin binaries
> themselves ship as GitHub Release assets). See [AGENTS.md](AGENTS.md#distribution--releases) for how
> releases are cut and how to enable Pages the first time.

### Fallback: manual install

If you can't use the repository (air-gapped host, etc.), download `jellyfetch_<version>.zip` from the
[Releases page](https://github.com/lohnn/jellyfetch/releases) and unzip it into
`<jellyfin data>/plugins/JellyFetch/`, then restart.

> **Permissions (manual install only):** after a manual unzip the files are usually owned by `root`,
> and Jellyfin **fails to start (white screen)** because it can't rewrite `meta.json` on boot. Fix:
> ```bash
> sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/JellyFetch
> sudo chmod -R u+rwX /var/lib/jellyfin/plugins/JellyFetch
> sudo systemctl restart jellyfin
> ```
> The repository install above avoids this — the installer sets ownership. Keep exactly one plugin
> folder named `JellyFetch` (don't leave a stray lowercase `jellyfetch/` beside it).

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

### Release channels

- **Stable** (what users subscribe to): a maintainer triggers the **Plugin Release (stable)** workflow
  (`.github/workflows/plugin-release.yml`) manually, picks a version bump, and CI cuts a `v<version>`
  GitHub Release and appends it to the accumulating `manifest.json` on the `gh-pages` branch — the
  permanent repository URL above.
- **Dev/bleeding-edge:** every push to `master` refreshes a rolling prerelease under the
  **`plugin-latest`** tag (`.github/workflows/plugin-ci.yml`); the Android app has its own sibling
  workflow (**`android-latest`**).

Maintainers: see [AGENTS.md](AGENTS.md#distribution--releases) for exactly how to cut a stable release,
how auto-versioning works, and the one-time GitHub Pages setup.
