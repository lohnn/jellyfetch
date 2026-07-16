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
- `LibraryId` (optional, **[LIVE]** — honored by placement): explicit placement target, the
  `Id` from `GET /Jellyfetch/Libraries`. When set it supersedes category-driven root selection. See
  **[Library-driven placement (v2 contract)](#library-driven-placement-v2-contract--contract-owner-publication-phased)**
  for the frozen semantics and rollout status.

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
- Query param `libraryId` (optional, **[LIVE]**): explicit placement library id, mirrors the JSON
  `LibraryId` field on `POST /Jellyfetch/Downloads`. Honored by placement.
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

## Metadata correction

Jellyfin's own metadata provider matches a downloaded item to a movie/series — sometimes **wrongly**
(similar titles). These endpoints let a client (1) see what Jellyfin assigned to a downloaded item,
(2) correct it by searching an external metadata database (TMDb/TVDb) and picking the right match, and
(3) browse **all** movies/series on the server and correct any of them (not just app-originated
downloads). They are mounted under **`/Jellyfetch/Metadata`**, same auth as everything else
(elevated token / API key).

**Verified against the real Jellyfin 10.11.11 typed surface** (decompiled `MediaBrowser.Controller`/
`Model` 10.11.11 + the v10.11.11 `ItemLookupController` source), not assumed. Two facts that shape
the contract:

- **Free-text search of unrelated titles works.** The remote search takes an arbitrary title string;
  it is **not** constrained to refine around the item's current (wrong) match. So the picker can search
  *"Completely Different Movie"* even when Jellyfin matched the item as something else. No external
  browser is required to search — the native provider search is the primary mechanism. (An
  explicit-provider-id apply path is still offered as a fallback for pasting an id you found elsewhere.)
- **Apply is synchronous.** Applying a correction triggers a **full metadata + image refresh that the
  server awaits before responding**. The refreshed item is returned in the `200` body — a client does
  **not** need to poll for completion. (Under the hood this is `IProviderManager.RefreshFullItem` with
  `MetadataRefreshMode=FullRefresh`, `ReplaceAllMetadata=true`, `ReplaceAllImages=true`, mirroring
  Jellyfin's own `ItemLookupController.ApplySearchCriteria`.)

### Shared shapes

**`LibraryItem`** — a Jellyfin library item and its current metadata:

```json
{
  "ItemId": "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4",
  "Name": "The Matrix",
  "ProductionYear": 1999,
  "Type": "Movie",
  "ProviderIds": { "Tmdb": "603", "Imdb": "tt0133093" },
  "HasPrimaryImage": true,
  "PosterUrl": "/Items/a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4/Images/Primary?tag=8f2a1c",
  "PosterTag": "8f2a1c"
}
```

| Field | Type | Notes |
|---|---|---|
| `ItemId` | string | Jellyfin library item id, **GUID in "N" format** (32 hex chars, no dashes). Use verbatim in the per-item routes below. |
| `Name` | string | Item title as Jellyfin currently has it matched. |
| `ProductionYear` | number \| null | Production year when known. |
| `Type` | string | `"Movie"`, `"Series"`, or `"Episode"`. Stable PascalCase. (Job-resolution promotes an episode file to its owning `Series` — see below — so in practice you get `Movie`/`Series`.) |
| `ProviderIds` | object | Map of provider name → id, e.g. `{ "Tmdb": "603" }`. May be empty `{}`. |
| `HasPrimaryImage` | bool | Whether the item currently has a poster. |
| `PosterUrl` | string \| null | **Relative standard-Jellyfin** image route (NOT a `/Jellyfetch` route). Null when no poster. Fetch it against the same server with the client's own token — e.g. `GET {baseUrl}{PosterUrl}` with `X-Emby-Token`. The `tag` query param is a cache key. |
| `PosterTag` | string \| null | Primary-image cache tag; cache-busts `PosterUrl`. |

**`RemoteSearchCandidate`** — one external match candidate:

```json
{
  "Name": "The Matrix",
  "ProductionYear": 1999,
  "Overview": "Set in the 22nd century, …",
  "ProviderIds": { "Tmdb": "603" },
  "ImageUrl": "https://image.tmdb.org/t/p/original/….jpg",
  "SearchProviderName": "TheMovieDb"
}
```

| Field | Type | Notes |
|---|---|---|
| `Name` | string | Candidate title. |
| `ProductionYear` | number \| null | Candidate year when known. |
| `Overview` | string \| null | Synopsis when the provider supplies one. |
| `ProviderIds` | object | The id map to echo back to **Apply** to select this candidate. |
| `ImageUrl` | string \| null | Absolute external poster/thumbnail URL (provider-hosted), when available. |
| `SearchProviderName` | string \| null | Which provider produced the candidate (informational). |

### `GET /Jellyfetch/Metadata/Jobs/{jobId}/LibraryMatch` — resolve a completed job → its library item

"Show me what Jellyfin thinks this download is." Keyed by **JellyFetch `jobId`** (the `Id` from the
Downloads API). Resolves the job's `FinalPaths` to a Jellyfin `BaseItem` and, when that item is an
episode file, promotes it to its owning **Series** (that's what carries the correctable provider ids).

`200`:

```json
{
  "JobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "Matched": true,
  "Item": { /* LibraryItem, see above */ }
}
```

- `Matched` is `false` (and `Item` is `null`) when the job is not terminal, has no final paths, or
  the files are **not yet scanned into the library** (the scan is asynchronous after placement) — the
  client should render a "not matched / not scanned yet" state and let the user retry, or fall back to
  the browse-all list. Multi-file jobs resolve on the first path that matches.
- `404` when the `jobId` is unknown.

### `GET /Jellyfetch/Metadata/Items` — list all library movies & series (browse-all)

Paged, searchable, type-filterable. Backs Feature 3's "browse everything and correct any of it" page.
Never loads the library unbounded.

Query params:

- `type` (optional): `"Movie"` or `"Series"`. Omit for both.
- `searchTerm` (optional): case-insensitive name search.
- `startIndex` (optional, default `0`): page offset.
- `limit` (optional, default `50`, clamped `1..200`): page size.

`200`:

```json
{
  "Items": [ /* LibraryItem, … */ ],
  "TotalRecordCount": 1234,
  "StartIndex": 0
}
```

`TotalRecordCount` is the full match count across all pages (use for paging). `400` on an unknown
`type`.

### `GET /Jellyfetch/Metadata/Items/{itemId}` — single library item

`itemId` is the **"N"-format GUID** from a `LibraryItem.ItemId`. `200` with a `LibraryItem`; `404`
when unknown. (Handy for re-fetching an item after an apply, though **Apply already returns the
refreshed item**.)

### `GET /Jellyfetch/Metadata/Items/ByPath` — resolve the current item at a path

Resolves the **current** library item whose files live at an absolute path — the deterministic
post-conversion rebind. After a `ConvertType` moves files (see below), the item is re-created by an
**asynchronous rescan** with a **new item id and possibly a drifted name** (metadata is re-fetched), so
resolving it by the old id or a title search is unreliable. The moved-to **path is stable**, so the
client resolves the new item by path instead.

Query params:

- `path` (required): the absolute file-system path — a `ConvertType` `MovedPaths` entry or its
  `ItemDirectory`. As with all query values, URL-encode it.

`200` with a `LibraryItem` (same shape as `GET /Metadata/Items/{itemId}`: `ItemId`, `Name`,
`ProductionYear`, `Type`, `ProviderIds`, `HasPrimaryImage`, `PosterUrl`, `PosterTag`). A resolved
Episode/Video is promoted to its owning Series/Movie (as with `LibraryMatch`).

- `400` — `path` is missing/blank.
- `404` — **nothing is indexed at that path yet.** This is the "rescan still running / keep polling"
  signal, **not** a hard error: poll again (e.g. every ~2 s for ~20–30 s) until it returns `200`. A
  persistent `404` means the path is wrong or the rescan didn't pick the files up.

> **This is the reliable rebind.** Poll `ByPath` with `ConvertType`'s `ItemDirectory` (or any
> `MovedPaths` entry) after a convert — do not re-use the old `SourceItemId` (it's deleted) and do not
> rely on `Jobs/{jobId}/LibraryMatch` (the job's `FinalPaths` still point at the item's **pre-move**
> location, so it will not resolve the moved item — see the ConvertType client-flow note).

### `POST /Jellyfetch/Metadata/Items/{itemId}/Search` — remote-search candidates

Free-text search of external providers for the correction picker. The `Name` is arbitrary and **not**
tied to the item's current match. The `itemId` supplies provider context and is validated to exist.

Body:

```json
{ "Name": "The Matrix", "Type": "Movie", "Year": 1999 }
```

- `Name` (required): the title to search for.
- `Type` (required): `"Movie"` or `"Series"` (case-insensitive) — selects which provider search kind
  to run. For correcting a Series, pass `"Series"`; for a Movie, `"Movie"`.
- `Year` (optional): production year to disambiguate.

`200`: a **bare JSON array** of `RemoteSearchCandidate`. `400` on empty `Name` or bad `Type`; `404`
when `itemId` is unknown. An empty array `[]` means the providers returned no candidates (valid, not
an error).

### `POST /Jellyfetch/Metadata/Items/{itemId}/Apply` — apply a correction

Sets the chosen provider id(s) on the item and awaits a full metadata + image refresh (a
replace-all rewrite). **Two modes, one endpoint:**

- **Native pick** — the user tapped a candidate from `Search`: echo the whole candidate object back as
  `Candidate`. Its `ProviderIds` are used, and its name/image ride along as refresh context.
- **Explicit provider id** — the user pasted an id (e.g. from a browser): send `ProviderIds` directly.

At least one of the two must resolve to a non-empty provider-id map. If both are present, `ProviderIds`
wins for the ids.

Body (native):

```json
{ "Candidate": { "Name": "The Matrix", "ProductionYear": 1999, "ProviderIds": { "Tmdb": "603" }, "…": "…" } }
```

Body (explicit):

```json
{ "ProviderIds": { "Tmdb": "603" } }
```

`200`: the **refreshed** `LibraryItem` (the refresh is awaited server-side — no polling needed).
`400` when neither `Candidate` nor `ProviderIds` yields a provider id. `404` when `itemId` is unknown.

> **Timing note.** The server awaits `RefreshFullItem`, so the response reflects the freshly written
> metadata. In rare cases a provider fetch can be slow; the client should still treat a `200` as
> authoritative for the returned fields and may re-`GET` the item if it wants to re-confirm the poster
> after image caching settles.

curl example:

```bash
curl -X POST -H 'X-Emby-Token: KEY' -H 'Content-Type: application/json' \
     -d '{"ProviderIds":{"Tmdb":"603"}}' \
     'http://server:8096/Jellyfetch/Metadata/Items/a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4/Apply'
```

### `POST /Jellyfetch/Metadata/Items/{itemId}/ConvertType` — convert type (Movie / Series / Other)

Fixes an item Jellyfin typed **wrong at the type level** — e.g. a movie filed as a TV series, or a
YouTube clip that shouldn't be counted as a movie. This is a genuinely different operation from
provider-id correction (the correction picker only searches within the item's *existing* type), so it
has its own endpoint.

> **How it works — and why it's asynchronous (verified against the real 10.11.11 surface, W-063).**
> Jellyfin has **no in-place type change**. An item's type *is* its CLR subclass and on-disk shape — a
> `Movie` is a single video file, a `Series` is a folder of `Season`/`Episode` items — and there is no
> `ILibraryManager`/API call that reclassifies an item without moving files. So JellyFetch performs a
> **re-ingest**: it (1) collects the item's video file(s), (2) moves them into the target library root
> with the correct layout and a seed NFO, (3) deletes the old mis-typed library item **without deleting
> the (already-moved) files**, and (4) triggers a scoped library rescan. That rescan is **asynchronous
> and re-creates the item with a NEW item id** — so this endpoint **cannot** return the new item
> synchronously.

> **On `"Other"`.** Jellyfin has **no literal "Other"/unknown item type** (the type is the CLR
> subclass). In JellyFetch, "Other" is a **placement** concept — it selects the **fallback library
> root** (`FallbackLibraryPath`, falling back to the movie root when that's empty — the same precedence
> the downloader uses for unclassifiable content). "Convert to Other" therefore **relocates** the item
> into that fallback library and rescans. What Jellyfin re-types it as then depends on **what kind of
> library the fallback path belongs to** (the user's own Jellyfin config — e.g. a "Home Videos"
> library) — JellyFetch only moves it there.

Body:

```json
{ "TargetType": "Other" }
```

- `TargetType` (required): `"Movie"`, `"Series"`, or `"Other"` (case-insensitive) — the type to convert **to**.

Destination root & layout produced:

- **→ Movie**: `{MovieRoot}/{Title (Year)}/{Title (Year)}{ext}` + a `<movie>` NFO. Multiple source files
  (a mis-typed multi-file series) land in the same movie folder (`… - part2`, …).
- **→ Series**: `{SeriesRoot}/{Title}/Season 01/{Title} - S01Exx{ext}` + a `tvshow.nfo`. A single source
  file becomes a one-episode series; multiple files map to sequential `S01E01…` episodes.
- **→ Other**: `{FallbackRoot or MovieRoot}/{Title (Year)}/{Title (Year)}{ext}` + a `<movie>` NFO — the
  same titled layout as Movie, only the **root** differs (fallback library). The fallback library's own
  type decides what it becomes on rescan.

`202 Accepted` with a **rescan-pending** result:

```json
{
  "SourceItemId": "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4",
  "TargetType": "Other",
  "Status": "RescanPending",
  "NewLibraryRoot": "/media/home-videos",
  "MovedPaths": ["/media/home-videos/Some Clip (2024)/Some Clip (2024).mp4"],
  "ItemDirectory": "/media/home-videos/Some Clip (2024)",
  "Title": "Some Clip",
  "Message": "Files moved and a library rescan was triggered. Resolve the re-typed item RELIABLY by path: poll GET /Jellyfetch/Metadata/Items/ByPath?path=%2Fmedia%2Fhome-videos%2FSome%20Clip%20(2024) (or any MovedPaths entry) until it returns 200. Do not re-use SourceItemId (it is deleted), and prefer this over a title search (the rescan may rename the item)."
}
```

| Field | Type | Notes |
|---|---|---|
| `SourceItemId` | string | The converted (now-**deleted**) item's id, "N" format. **Do not re-use it** — a second convert on it returns `409` (see below). |
| `TargetType` | string | `"Movie"`, `"Series"`, or `"Other"` — what it was converted to. |
| `Status` | string | Currently always `"RescanPending"` on success. Additive vocabulary — treat unknown values as "in progress". |
| `NewLibraryRoot` | string \| null | The library root the files were moved into. |
| `MovedPaths` | string[] | Absolute paths the video files were moved to. Poll `ByPath` with `MovedPaths[0]` (or `ItemDirectory`). |
| `ItemDirectory` | string \| null | The absolute item folder the files were moved into (`{root}/{Title (Year)}` etc.). The **most stable rebind key** — the new item's own path is at/under this. |
| `Title` | string \| null | Best-known pre-move title (fallback poll seed only; may drift after rescan). |
| `Message` | string \| null | Human-readable next step (poll `ByPath`). |

**Client flow (honest — reliable by-PATH rebind):** after `202`, the conversion is correct
**server-side** (Jellyfin moves the files, rescans, and re-types the item). The rescan is
**asynchronous** and re-creates the item with a **new id and possibly a different name**, so:

1. **Poll `GET /Jellyfetch/Metadata/Items/ByPath?path={ItemDirectory}`** (URL-encoded; or any
   `MovedPaths` entry) until it returns `200` — that is the freshly-scanned, correctly-typed item. `404`
   means "not indexed yet, keep polling".
2. **Rebind the UI to that item** and invalidate anything cached under `SourceItemId`.
3. Do **not** re-use `SourceItemId` (it's deleted → `409`), do **not** rely on a `searchTerm` title
   match (the rescan may rename the item), and do **not** re-run `Jobs/{jobId}/LibraryMatch` to find it
   (the job's `FinalPaths` still point at the **pre-move** location).

Error responses:

- `400` — rejected conversion with `{ "Error": "..." }`: unknown `TargetType`; item is already that
  type; item is neither a Movie nor a Series (e.g. an Episode — correct its parent instead); the target
  library path isn't configured; no video files could be located on disk; **or (for `"Other"`) the
  fallback library isn't configured as a distinct root** — i.e. converting to Other would re-file the
  item into the same library it's already in (a misleading no-op). The message tells the user to set
  `FallbackLibraryPath` to a **distinct** library (e.g. a Home Videos library) first. JellyFetch does
  **not** silently perform the no-op move.
- `403` — the target library root isn't writable by the Jellyfin service user; the `Error` carries the
  exact `chown`/`chmod` fix. **No files are moved in this case** (write pre-flight fails first).
- `404` — the `itemId` is unknown.
- `409` — with `{ "Error": "..." }`: the item is **stale / already converted** — it still resolves by
  id (a cached/leftover entry, or the client is holding a now-dead id from a prior conversion) but its
  files are **no longer at its recorded location**. This replaces the previous misleading "could not
  locate any video files" message for this case. The client should stop acting on this id and
  **re-resolve the current item via `ByPath`** (using the earlier convert's `ItemDirectory`/`MovedPaths`).

curl example:

```bash
curl -X POST -H 'X-Emby-Token: KEY' -H 'Content-Type: application/json' \
     -d '{"TargetType":"Other"}' \
     'http://server:8096/Jellyfetch/Metadata/Items/a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4/ConvertType'
```

## Library-driven placement (v2 contract — CONTRACT-OWNER PUBLICATION, phased)

> **Status legend.** Each part below is tagged **[LIVE]** (implemented, compiled against the real
> 10.11.11 surface, and unit-tested — but see the LIVE-UNVERIFIED smoke-test note at the end: not yet
> exercised against a running server). The whole library-driven-placement phase has landed: the config
> `*LibraryPath` keys are removed, the placer resolves roots from `GetVirtualFolders()`, `LibraryId` is
> honored, and `ChangeLibrary` exists. The three consumer capabilities (`android-share`,
> `media-downloader`, `torrent-engine`) build against the frozen identifiers below.

This replaces "the user configures Movie/Series/Fallback library paths" with "JellyFetch reads the
libraries the user already defined in Jellyfin." Ground truth (decompiled from pinned Jellyfin
10.11.11): `ILibraryManager.GetVirtualFolders()` returns `List<VirtualFolderInfo>`, and each
`VirtualFolderInfo` carries `string Name`, `string[] Locations` (a library can span **several** root
folders), `CollectionTypeOptions? CollectionType` (a **nullable enum** — `movies`/`tvshows`/`music`/
`musicvideos`/`homevideos`/`boxsets`/`books`/`mixed`; **null** for a plain/undeclared library), and
`string ItemId`. JellyFetch picks the **first** `Location` as a library's placement root.

### `GET /Jellyfetch/Libraries` — list placement targets **[LIVE]**

Auth: same elevated scheme as every other endpoint (`RequiresElevation`). No params. The app fetches
this **lazily** (when the share popup opens) to populate its library dropdown.

`200`:

```json
{
  "Libraries": [
    {
      "Id": "f137a2dd21bbc1b99aa5c0f6bf02a805",
      "Name": "Movies",
      "CollectionType": "movies",
      "PrimaryLocation": "/media/movies",
      "Locations": ["/media/movies"],
      "IsPlaceable": true
    },
    {
      "Id": "a9c3f0e5b1d24e6f8a7b0c1d2e3f4a5b",
      "Name": "TV Shows",
      "CollectionType": "tvshows",
      "PrimaryLocation": "/media/tv",
      "Locations": ["/media/tv", "/media/tv-archive"],
      "IsPlaceable": true
    },
    {
      "Name": "Photos",
      "CollectionType": null,
      "PrimaryLocation": "/media/photos",
      "Locations": ["/media/photos"],
      "Id": null,
      "IsPlaceable": false
    }
  ]
}
```

| Field | Type | Notes |
|---|---|---|
| `Id` | string \| null | The library's `VirtualFolderInfo.ItemId` (GUID string) — the **token the app sends back on submit** as `LibraryId`. `null` when Jellyfin has assigned the folder no item id (rare); such a library is display-only and cannot be explicitly targeted. |
| `Name` | string | Display name for the dropdown row. |
| `CollectionType` | string \| null | Normalized **lowercase** Jellyfin collection type: `"movies"`, `"tvshows"`, `"music"`, `"musicvideos"`, `"homevideos"`, `"boxsets"`, `"books"`, `"mixed"`, or `null` (undeclared library). JellyFetch owns this string explicitly — it does **not** depend on the server's enum serializer. |
| `PrimaryLocation` | string \| null | The **first** of `Locations` — the absolute root JellyFetch places into for this library. Informational; the app does **not** send it back. `null` when the library declares no locations. |
| `Locations` | string[] | All root folders the library spans. May have >1 entry; JellyFetch places into the first. May be empty. |
| `IsPlaceable` | bool | `true` iff `Id` is non-null **and** there is ≥1 location — i.e. the app may offer it as an explicit target. Show non-placeable entries disabled or omit them. **Auto** is always available regardless of any entry. |

Ordering is **Jellyfin's own** (as `GetVirtualFolders()` returns them), so "the first `movies`
library" and "the first `tvshows` library" are deterministic — that is what **Auto** resolves to per
classified type. No per-type default config setting exists or is planned.

### Submit with an explicit library — `SubmitDownloadRequest.LibraryId` **[LIVE]**

`POST /Jellyfetch/Downloads` (and `POST /Jellyfetch/Downloads/Torrent`) gain **one new optional
field**, frozen name **`LibraryId`** (string, the `Id` from `GET /Jellyfetch/Libraries`):

```json
{ "Url": "https://www.svtplay.se/video/...", "Category": "Auto", "LibraryId": "a9c3f0e5b1d24e6f8a7b0c1d2e3f4a5b" }
```

Coexistence rules with the existing `Category` hint — **explicit and frozen**:

- **No `LibraryId` (or empty) + `Category: "Auto"`** → today's behavior, unchanged: backends classify,
  and Auto places into the **first** `movies`/`tvshows` library matching the classified category.
- **No `LibraryId` + explicit `Category`** → unchanged: the category hint steers classification/placement.
- **Explicit `LibraryId`** → the chosen library **supersedes** category-driven root selection: the
  file is placed into that library's `PrimaryLocation`, regardless of `Category`. `Category` (if also
  sent) still rides along as a **layout/classification hint** for the backend's naming (e.g. episode
  vs movie folder shape) but no longer picks the *root*. Sending `LibraryId` for a non-placeable /
  unknown library is rejected `400 { "Error": "..." }`.

**Now LIVE:** the field is honored by placement. `android-share` ships the dropdown and sends
`LibraryId`; the server resolves it to the chosen library's root. Fan-out children inherit the
parent's `LibraryId` (they carry no request, so it is copied onto the job). The one remaining caveat is
live-server verification (see LIVE-UNVERIFIED note at the end): the path is unit-tested but not yet
smoke-tested on a running Jellyfin.

### Flow: `LibraryId` → job model → placement **[LIVE]**

`SubmitDownloadRequest.LibraryId` (REST) → a new optional `DownloadRequest.LibraryId` (string?) →
carried on the job → at placement, the server resolves `LibraryId` to a root via
`GetVirtualFolders()` (id → `VirtualFolderInfo` → `Locations[0]`), replacing the
`Category → configured-path` switch in `NaiveMediaPlacer`. Per **I-125**, this changes **only** how
`PlacementResult.LibraryRootUsed` is *resolved* (from a queried library id instead of a configured
path) — the **`DownloadChild.RelativePath` / `PlacementResult.LibraryRootUsed` shapes are unchanged**,
and backends keep emitting library-root-relative paths exactly as today. A stage before placement
still must **not** emit absolute paths.

### Config model change **[LIVE]**

- **REMOVED**: `SeriesLibraryPath`, `MovieLibraryPath`, `FallbackLibraryPath` — placement roots come
  from the queried libraries, not config. The config page no longer shows those three path pickers.
- **KEPT**: `StagingPath` (empty ⇒ data-dir default), `YtDlpPath`, `SvtPlayDlPath`,
  `MaxConcurrentDownloads`, `TorrentListenPort`, `ToolRoutingOverrides`.
- **Migration**: existing installs with the three path fields set are **not** broken at read time
  (unknown/removed keys are ignored by Jellyfin's config deserializer). The values are simply no longer
  consulted. No data migration needed — the libraries are already defined in Jellyfin. Config is still
  read **live** per-use (`Plugin.Instance.Configuration`), so "which library" (a submit-time `LibraryId`
  + live `GetVirtualFolders()`), like the old "which path," takes effect without a server restart
  (**I-117**).

### Move a completed item between two same-type libraries **[LIVE]**

Generalizes the existing type-change re-ingest (`LibraryMetadataService.ConvertTypeAsync`) into a
**"change destination library"** operation, reusing the same move→delete-old-DB-item→async-rescan
mechanism and the **same outcome vocabulary** (`Superseded` / `RescanPending` / `PermissionDenied` /
`NotFound` / `Rejected`). Frozen shape:

- **Endpoint (frozen):** `POST /Jellyfetch/Metadata/Items/{itemId}/ChangeLibrary`
- **Request DTO (frozen):** `ChangeLibraryRequest { "LibraryId": "<target library id from GET /Jellyfetch/Libraries>" }`
- **Result:** the existing `ConvertTypeResultDto` shape (`202 RescanPending` on success; `MovedPaths`
  + `ItemDirectory` for the reliable **by-PATH rebind** — same client poll flow as `ConvertType`).
- **Semantics:** moves the item's files into the target library's `PrimaryLocation`, re-laying them out
  for that library's collection type (same-type move keeps the layout; the operation also subsumes the
  cross-type case). The old DB item is deleted (files kept) and a scoped rescan re-creates it.
- **Traps already handled by the reused mechanism (W-066):** the **by-PATH rebind** (poll `ByPath`,
  never re-use the deleted `SourceItemId`) and the **`409 Superseded`** stale-item guard (item still
  resolves by id but its files are already gone) carry over verbatim. A no-op move (target library's
  root == the item's current root) is `Rejected` rather than silently re-filing.

**Discriminating "genuine metadata" from "stale client cache" (W-065).** The **one** observable that
says a library *genuinely* is collection-type X is `VirtualFolderInfo.CollectionType` read **live from
`GetVirtualFolders()` at request time** — not anything the client sends, and not a cached list. Any
collection-type-driven decision (which layout to apply on a move, whether a target is placeable) reads
the live virtual-folder list per request; the client's dropdown snapshot is display-only and never the
source of truth. If a move looks wrong, suspect a stale client library list before adding server-side
type-validation logic.

### LIVE-UNVERIFIED — smoke-test checklist (W-057)

All of the above compiles clean and is unit-tested (240 tests green), but the following are **not yet
exercised against a running Jellyfin server** — they need a live smoke test before this is called done:

1. **Library enumeration** — `GET /Jellyfetch/Libraries` against a real server returns the real
   libraries with correct `CollectionType` / `Locations` (unit tests use synthetic `VirtualFolderInfo`).
2. **Auto with multiple same-type libraries** — a real server with two Movies libraries places into the
   FIRST one Jellyfin reports (deterministic ordering assumption).
3. **Explicit `LibraryId` placement** — a real submission with a chosen library id lands under that
   library's first folder and the scoped rescan surfaces it.
4. **Same-type `ChangeLibrary` move** — Movies A → Movies B on a real server: files move, old DB item
   is dropped, rescan re-creates it, and the **empty source folder is removed** (ghost cleanup).
5. **`409 Superseded` on double-action** — acting twice on the same (now-moved) item id returns 409, not
   a generic error, and the by-PATH rebind finds the new item.

## Config (FYI for clients)

Plugin configuration is read/written via Jellyfin's standard plugin-config endpoints
(`GET/POST /Plugins/3ed77eb2-77c8-49c9-8a14-8cfcb86cb6f3/Configuration`). The standard
Jellyfin endpoint is full-document; clients that only change one key should GET, merge,
POST (the config page does exactly that).

**Keys** (post-library-migration): `StagingPath`, `YtDlpPath`, `SvtPlayDlPath`,
`MaxConcurrentDownloads`, `TorrentListenPort`, `ToolRoutingOverrides`. The three `*LibraryPath` keys
were **removed** — placement targets are the user's Jellyfin libraries now (see **Library-driven
placement (v2 contract)** above). The Android app does not need these; submission classification and
placement are server-side.
