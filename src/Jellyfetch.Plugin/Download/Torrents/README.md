# Torrents backend — owned by `torrent-engine`

This directory belongs to the **torrent-engine** capability. It implements
`Jellyfetch.Plugin.Download.IDownloadHandler` (`Kind == "torrent"`) on MonoTorrent:
magnet URIs and .torrent files (delivered base64 in `DownloadRequest.TorrentFileBase64` /
`DownloadItem.TorrentFileBase64`), metadata resolution, download-and-done (no seeding),
progress into `JobProgress`.

Registration: add exactly one line in `PluginServiceRegistrator.RegisterServices`
(see the marked section there). Add the MonoTorrent PackageReference to the csproj.

Config available via `Plugin.Instance.Configuration`: `TorrentListenPort`.

Contract details: `../IDownloadHandler.cs`, `../DownloadModels.cs`, and `docs/api.md`.
