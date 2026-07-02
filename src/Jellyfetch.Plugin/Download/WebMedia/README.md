# WebMedia backend — owned by `media-downloader`

This directory belongs to the **media-downloader** capability. It implements
`Jellyfetch.Plugin.Download.IDownloadHandler` (`Kind == "webMedia"`) for http(s) URLs:
yt-dlp / svtplay-dl subprocess orchestration, playlist/series expansion in `ResolveAsync`,
progress parsing into `JobProgress`, and Jellyfin-matchable naming.

Registration: add exactly one line in `PluginServiceRegistrator.RegisterServices`
(see the marked section there).

Config available via `Plugin.Instance.Configuration`:
`YtDlpPath`, `SvtPlayDlPath`, `SeriesLibraryPath`, `MovieLibraryPath`, `FallbackLibraryPath`.

Contract details: `../IDownloadHandler.cs`, `../DownloadModels.cs`, and `docs/api.md`.
