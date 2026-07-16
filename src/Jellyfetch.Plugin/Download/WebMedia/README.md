# WebMedia backend — owned by `media-downloader`

This directory belongs to the **media-downloader** capability. It implements
`Jellyfetch.Plugin.Download.IDownloadHandler` (`Kind == "webMedia"`) for http(s) URLs:
yt-dlp / svtplay-dl subprocess orchestration, playlist/series expansion in `ResolveAsync`,
progress parsing into `JobProgress`, and Jellyfin-matchable naming.

Registration: add exactly one line in `PluginServiceRegistrator.RegisterServices`
(see the marked section there).

Config available via `Plugin.Instance.Configuration`:
`YtDlpPath`, `SvtPlayDlPath`, `ToolRoutingOverrides`. Library placement roots are **no longer**
configured here — under library-driven placement (docs/api.md "Library-driven placement (v2
contract)") the shared placer resolves the library root from the user's Jellyfin libraries
(`ILibraryRootResolver`); this backend only emits the resolved `MediaCategory` and a root-relative
layout, never a root path.

Contract details: `../IDownloadHandler.cs`, `../DownloadModels.cs`, and `docs/api.md`.
