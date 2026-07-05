# JellyFetch REST API — v1 contract

**Owner**: `jellyfin-plugin`. **Consumers**: `android-share`, curl-wielding humans.
Source of truth together with `src/Jellyfetch.Plugin/Api/DownloadsController.cs` and `Api/JobDto.cs`.
Changes to paths or field names are **breaking** and must be announced via HIVEmind.

## Base

All routes are mounted on the Jellyfin server, base path **`/Jellyfetch`**
(e.g. `http://server:8096/Jellyfetch/Downloads`). If Jellyfin runs with a base URL
prefix, prepend it as for any other Jellyfin route.

## Authentication

Jellyfin-native, no parallel scheme. All endpoints require an **elevated** token
(admin user token or a Jellyfin **API key** created under Dashboard → API Keys; API keys
are elevated).

Send one of:

```
Authorization: MediaBrowser Token="YOUR_API_KEY"
```

or the simpler equivalent:

```
X-Emby-Token: YOUR_API_KEY
```

Notes for clients: in the `MediaBrowser` scheme the quotes around the value are the
canonical form Jellyfin's parser expects (unquoted values happen to work in current
versions, but send quotes). `X-Emby-Token` takes the bare key, no quotes. Either header
is sufficient on its own. Unauthenticated → `401`; non-elevated user token → `403`.

## JSON conventions

- Property names are **PascalCase** (Jellyfin server default), e.g. `"State"`, `"EtaSeconds"`.
  Request bodies are parsed case-insensitively, so `"url"` works, but PascalCase is canonical.
- Enum-like values (`State`, `Kind`, category) are **strings**.
- Timestamps are ISO 8601 UTC with offset, e.g. `"2026-07-02T16:30:00+00:00"`.
- `null`-valued fields may be omitted from responses; treat absent as null.

## The Job object

```json
{
  "Id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "ParentId": null,
  "IsGroup": false,
  "Kind": "webMedia",
  "State": "Downloading",
  "Title": "Rederiet S01E02",
  "SourceUrl": "https://www.svtplay.se/video/...",
  "Percent": 42.3,
  "SpeedBps": 1250000,
  "EtaSeconds": 95,
  "DownloadedBytes": 52428800,
  "TotalBytes": 123456789,
  "StatusText": "downloading",
  "ErrorMessage": null,
  "FinalPaths": [],
  "Category": null,
  "SeriesName": null,
  "SeasonNumber": null,
  "EpisodeNumber": null,
  "EpisodeTitle": null,
  "CreatedAt": "2026-07-02T16:30:00+00:00",
  "UpdatedAt": "2026-07-02T16:31:12+00:00",
  "CompletedAt": null,
  "ChildCount": 0,
  "Children": null
}
```

| Field | Type | Notes |
|---|---|---|
| `Id` | GUID string | Job id, use in all per-job routes. |
| `ParentId` | GUID string \| null | Set on children of an expanded playlist/series submission. |
| `IsGroup` | bool | True when this submission fanned out into child jobs. Group progress/state aggregates its children. |
| `Kind` | string \| null | `"webMedia"` or `"torrent"`. |
| `State` | string | `Queued`, `Resolving`, `Downloading`, `Processing`, `Completed`, `Failed`, `Cancelled` (PascalCase — parse case-insensitively). |
| `Title` | string | Best-known title; starts as the URL, improves as the backend learns more. |
| `SourceUrl` | string \| null | Submitted / per-episode URL. Null for .torrent uploads. |
| `Percent` | number \| null | 0–100; null = indeterminate (render spinner). |
| `SpeedBps` | number \| null | Bytes per second. |
| `EtaSeconds` | number \| null | Estimated seconds remaining. |
| `DownloadedBytes` / `TotalBytes` | number \| null | Byte counters when known. |
| `StatusText` | string \| null | Short human status line (`"fetching metadata"`, `"3/8 items finished"`). |
| `ErrorMessage` | string \| null | Set when `State == "Failed"` (on group parents: summary like `"2 of 8 items failed"`). |
| `FinalPaths` | string[] | Absolute library paths after completion. |
| `Category` | string \| null | **Resolved** media category: `"Movie"`, `"Series"`, or `"Other"`. **Additive/optional — always tolerate `null`/absent.** Null until the backend classifies the item (queued/resolving/downloading jobs, torrents that don't classify, and jobs from before this field existed). Populated at completion from the backend's resolution. The internal `"Auto"` placeholder is **never** emitted here — an unclassified job is `null`, not `"Auto"`. Distinct from the request `Category` hint (which *does* accept `"Auto"`, see submit endpoint). When null, a client may fall back to inferring from `SeriesName`/`EpisodeNumber` (series ⇒ `SeriesName` set; movie ⇒ both null). |
| `SeriesName` | string \| null | Series name for episode jobs (e.g. `"Abbormästarna"`). Null when not a known episode. Populated at completion from the backend's `--nfo` probe. |
| `SeasonNumber` | number \| null | Season number when known. **SVT quirk:** for SVT content this carries the **YEAR** (e.g. `2024`) — that's how SVT dates its shows; render it verbatim, do not treat as a 1-based season. |
| `EpisodeNumber` | number \| null | Episode number within the season (e.g. `2`). Null when unknown. |
| `EpisodeTitle` | string \| null | Episode title (e.g. `"Avsnitt 2"`). Null when unknown. Distinct from `Title` (which is the best-known display title and may equal this). |
| `CreatedAt` / `UpdatedAt` / `CompletedAt` | ISO 8601 | `CompletedAt` set on any terminal state. |
| `ChildCount` | number | Number of child jobs (0 for non-groups). |
| `Children` | Job[] \| null | Only populated by the **detail** endpoint. |

### State machine

```
Queued → Resolving → Downloading → Processing → Completed
                 └───────┴──────────────┴─────→ Failed | Cancelled
```

- Terminal states: `Completed`, `Failed`, `Cancelled`. `Failed` and `Cancelled` are retryable.
- A playlist/series submission fans out **server-side during `Resolving`**: the submission job
  becomes a group parent (`IsGroup: true`) and one independent child job per episode is created.
  One failed episode fails only that child; the parent reports aggregate percent and
  `Failed` only if at least one child failed (with `Completed` winning if any child completed).

## Endpoints

### `GET /Jellyfetch/Ping` — test connection

Cheap authenticated ping. `200`:

```json
{ "Name": "JellyFetch", "Version": "0.1.0.0" }
```

Use for "test connection": `401`/`403` → bad key; `404` → plugin not installed; `200` → good.

### `POST /Jellyfetch/Downloads` — submit URL or magnet

Body (`Content-Type: application/json`):

```json
{ "Url": "https://www.svtplay.se/video/...", "Category": "Auto" }
```

- `Url` (required): `http(s)://...` or `magnet:?...`.
- `Category` (optional): `"Auto"` (default) | `"Series"` | `"Movie"` | `"Other"` — case-insensitive
  user **hint**; backends may refine it during resolve. Note this **request** hint accepts `"Auto"`,
  whereas the **resolved** `Category` on the returned Job never does (it is `null` until classified,
  then one of `"Movie"`/`"Series"`/`"Other"` — see the Job field table above).

Responses: `201` with the created Job (always exactly one — fan-out happens later, poll the job),
`Location: /Jellyfetch/Downloads/{Id}`. `400` with `{ "Error": "..." }` for unsupported/invalid input.

### `POST /Jellyfetch/Downloads/Torrent` — submit .torrent file

**Raw body upload, NOT multipart.** Send the .torrent file bytes as the request body:

```
POST /Jellyfetch/Downloads/Torrent?category=Movie
Content-Type: application/x-bittorrent

<raw .torrent bytes>
```

- Query param `category` (optional, case-insensitive): same values as above.
- Max size 10 MiB.

Responses: `201` with the created Job; `400` with `{ "Error": "..." }` on empty/oversized body.

curl example:

```bash
curl -X POST -H 'X-Emby-Token: KEY' -H 'Content-Type: application/x-bittorrent' \
     --data-binary @file.torrent 'http://server:8096/Jellyfetch/Downloads/Torrent?category=Movie'
```

### `GET /Jellyfetch/Downloads` — list jobs

Query params:

- `state` (optional): filter, case-insensitive (`?state=downloading`).
- `includeChildren` (optional bool, default `false`): when false, children of group jobs are
  omitted — dashboards get one row per submission with aggregate progress on the parent
  (`ChildCount > 0` tells you it's a group; fetch detail for per-episode rows).

`200`: **bare JSON array** of Job objects, newest first. `Children` is never populated here.

### `GET /Jellyfetch/Downloads/{id}` — job detail

`200`: Job object; for group parents `Children` is populated (oldest first). `404` if unknown.

Each child in `Children` is a full Job object carrying its **own** `State`, `Percent`,
`ErrorMessage`, `FinalPaths`, and — once completed — its own per-episode `Category` / `SeriesName` /
`SeasonNumber` / `EpisodeNumber` / `EpisodeTitle`. Children are independent: one child in
`Failed` (with its own `ErrorMessage`) does not fail its siblings. This is the endpoint the
tap-to-expand detail UI calls to render N labelled episodes (e.g.
`Abbormästarna · S2024E02 · Avsnitt 2`).

### `POST /Jellyfetch/Downloads/{id}/Cancel`

Cancels a queued/running job; group parents cascade to all non-terminal children. No body.
`204` on success · `404` unknown · `409` already terminal.

### `POST /Jellyfetch/Downloads/{id}/Retry`

Retries a `Failed`/`Cancelled` job (re-queued from scratch). Group parents retry all
failed/cancelled children. No body.
`200` with the updated Job · `404` unknown · `409` not retryable.

### `DELETE /Jellyfetch/Downloads/{id}`

Removes a **terminal** job from history (group parents remove children too). Never deletes
downloaded media files.
`204` on success · `404` unknown · `409` still active (cancel first).

## Config (FYI for clients)

Plugin configuration is read/written via Jellyfin's standard plugin-config endpoints
(`GET/POST /Plugins/3ed77eb2-77c8-49c9-8a14-8cfcb86cb6f3/Configuration`). The standard
Jellyfin endpoint is full-document; clients that only change one key should GET, merge,
POST (the config page does exactly that). Keys:
`SeriesLibraryPath`, `MovieLibraryPath`, `FallbackLibraryPath`, `StagingPath`,
`YtDlpPath`, `SvtPlayDlPath`, `MaxConcurrentDownloads`, `TorrentListenPort`.
The Android app does not need these for v1; submission classification is server-side.
