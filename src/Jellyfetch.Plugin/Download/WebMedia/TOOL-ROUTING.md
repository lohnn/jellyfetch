# Web-media download tool routing

**Decision (verified against real binaries, 2026-07-02):**

| Domain | Primary tool | Rationale |
|--------|--------------|-----------|
| `svtplay.se` (and `svt.se` clips) | **svtplay-dl** | See below |
| YouTube, and every other yt-dlp-supported site | **yt-dlp** | Broadest extractor coverage, deterministic progress |

Routing is **configurable**: the `PluginConfiguration.ToolRoutingOverrides` config key holds a
list of `domain=tool` lines (tool ∈ `yt-dlp` / `svtplay-dl`). `ToolRouter.Route` consults the
overrides **first** — an exact- or suffix-host match there wins over the built-in defaults — then
falls back to the table above. Overrides are read live per-use (via `Plugin.Instance.Configuration`),
so edits apply without a server restart. An empty override list reproduces the default table exactly.
Malformed lines (no `=`, empty domain/tool, unknown tool token) are skipped defensively.

## Why svtplay-dl for SVT Play (not yt-dlp's built-in SVT extractor)

Tested with `yt-dlp 2026.06.09` and `svtplay-dl 4.191` from a non-Swedish host.

1. **yt-dlp's canonical-URL extraction is broken.** The URL shape a user actually shares —
   `https://www.svtplay.se/video/<id>/<slug>` — fails in yt-dlp with
   `ERROR: [svt:play] <id>: Unable to extract video data`. Only the internal
   `svt:<id>` reference (obtained from a `svt:play:series` flat-playlist listing)
   extracts. We cannot require users to hand us internal refs.
2. **yt-dlp loses season/episode numbers.** Even when extraction succeeds,
   `season_number` and `episode_number` come back `None`; the episode index only
   survives as a prefix in the title text (`"1. …"`). svtplay-dl reports the correct
   `s03e06` for the same episode. Correct S/E numbers are load-bearing for the
   Jellyfin naming convention — this alone is decisive.
3. **svtplay-dl tracks Swedish-service quirks faster** and has first-class
   `-A/--all-episodes`, `--all-last NN`, subtitle handling (`-S`, `--all-subtitles`),
   and NFO generation (`--nfo`).
4. **svtplay-dl `--nfo --force-nfo` is a download-free metadata probe.** It writes a
   Jellyfin-shaped `episodedetails` NFO (showtitle/title/season/episode/plot/aired)
   plus `tvshow.nfo` *without* fetching the video — we reuse it both as our
   introspection path for SVT and as the sidecar generator, avoiding hand-rolling
   SVT NFO XML.

## Why yt-dlp for YouTube / everything else

- YouTube single video: `yt-dlp -J --flat-playlist` → `_type: video`, no series
  fields (expected — YouTube content is classified to the fallback root).
- YouTube playlist: `_type: playlist`, `entries[]` each `_type: url` carrying a full
  `url` + `title` → clean 1-job-per-entry fan-out.
- Deterministic machine progress via `--newline --progress-template`.
- Enormous extractor coverage for the "later: anything yt-dlp supports" goal.

## Introspection commands (ground-truthed)

- **yt-dlp**: `yt-dlp -J --flat-playlist <url>` — JSON on **stdout**, warnings on
  **stderr**. Never merge the streams. `_type` ∈ {`video`, `playlist`}.
- **svtplay-dl program → episode list**:
  `svtplay-dl --get-only-episode-url -A <program-url>` — emits
  `INFO: Url: <episode-url>` lines on **stderr** (not stdout), **newest-first**
  (reverse order). No JSON mode exists.
- **svtplay-dl episode metadata**:
  `svtplay-dl --nfo --force-nfo -o <dir> <episode-url>` — writes NFO sidecar(s)
  without downloading video; parse the `episodedetails` XML for S/E/title/plot/aired.

## Progress parsing (ground-truthed)

- **yt-dlp**: `--newline --progress-template
  "download:PROG|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s"`
  → pipe-delimited lines, e.g. `PROG|downloading|261120|629172|NA|2223790.17|0`.
  `total_bytes` may be `NA` → fall back to `total_bytes_estimate`. `speed`/`eta` are
  `NA` for the first updates. Treat `NA` as null, not 0.
- **svtplay-dl**: no machine progress; parses from its human `[download] NN.N% ...`
  / `INFO:` stderr lines (rougher — verify per version).

## Operational notes / hazards

- yt-dlp now warns `No supported JavaScript runtime` (wants `deno`); progressive
  formats still download but some formats may be missing. Install `deno` on the host
  for full format coverage.
- SVT metadata API was reachable from a non-Swedish host; actual **stream** download
  geo/DRM status not yet confirmed — expect possible geo-block on the media segments.
- Tools detect non-TTY and can go quiet/buffer; always pass `--newline` (yt-dlp) and
  do line-by-line async reads.
