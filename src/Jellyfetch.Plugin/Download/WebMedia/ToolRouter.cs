using System;
using System.Collections.Generic;

namespace Jellyfetch.Plugin.Download.WebMedia;

/// <summary>Which CLI backend handles a URL.</summary>
internal enum DownloadTool
{
    YtDlp,
    SvtPlayDl,
}

/// <summary>
/// Chooses the download tool for a URL by host. Default routing (see
/// docs/tool-routing.md): svtplay.se / svt.se → svtplay-dl (yt-dlp's SVT
/// canonical-URL extraction is broken and drops season/episode numbers);
/// everything else → yt-dlp (broadest extractor coverage).
/// </summary>
internal sealed class ToolRouter
{
    private static readonly Dictionary<string, DownloadTool> Hosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["svtplay.se"] = DownloadTool.SvtPlayDl,
            ["www.svtplay.se"] = DownloadTool.SvtPlayDl,
            ["svt.se"] = DownloadTool.SvtPlayDl,
            ["www.svt.se"] = DownloadTool.SvtPlayDl,
        };

    public DownloadTool Route(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return DownloadTool.YtDlp;
        }

        var host = uri.Host;
        if (Hosts.TryGetValue(host, out var tool))
        {
            return tool;
        }

        foreach (var pair in Hosts)
        {
            if (host.EndsWith("." + pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return DownloadTool.YtDlp;
    }
}
