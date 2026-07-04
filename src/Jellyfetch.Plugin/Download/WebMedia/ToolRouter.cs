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
/// Chooses the download tool for a URL by host. Config overrides
/// (<c>PluginConfiguration.ToolRoutingOverrides</c>, a list of <c>domain=tool</c> lines) are
/// consulted FIRST; a host match there wins over the built-in defaults. Default routing (see
/// TOOL-ROUTING.md): svtplay.se / svt.se → svtplay-dl (yt-dlp's SVT canonical-URL extraction is
/// broken and drops season/episode numbers); everything else → yt-dlp (broadest extractor
/// coverage). Route is pure over its arguments — overrides are passed in per-call (read live from
/// config by the caller), so edits apply without a server restart and the router stays unit-testable.
/// </summary>
internal sealed class ToolRouter
{
    private static readonly Dictionary<string, DownloadTool> DefaultHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["svtplay.se"] = DownloadTool.SvtPlayDl,
            ["www.svtplay.se"] = DownloadTool.SvtPlayDl,
            ["svt.se"] = DownloadTool.SvtPlayDl,
            ["www.svt.se"] = DownloadTool.SvtPlayDl,
        };

    /// <summary>
    /// Routes a URL to a download tool, consulting <paramref name="overrides"/> first
    /// (<c>domain=tool</c> lines) then the built-in defaults. A null/empty override list
    /// reproduces the built-in default behaviour exactly.
    /// </summary>
    public DownloadTool Route(string url, IReadOnlyList<string>? overrides = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return DownloadTool.YtDlp;
        }

        var host = uri.Host;

        var overrideMap = ParseOverrides(overrides);
        if (TryMatchHost(overrideMap, host, out var overridden))
        {
            return overridden;
        }

        if (TryMatchHost(DefaultHosts, host, out var tool))
        {
            return tool;
        }

        return DownloadTool.YtDlp;
    }

    /// <summary>Exact-then-suffix host lookup against a domain→tool map.</summary>
    private static bool TryMatchHost(
        IReadOnlyDictionary<string, DownloadTool> map, string host, out DownloadTool tool)
    {
        if (map.Count == 0)
        {
            tool = default;
            return false;
        }

        if (map.TryGetValue(host, out tool))
        {
            return true;
        }

        foreach (var pair in map)
        {
            if (host.EndsWith("." + pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                tool = pair.Value;
                return true;
            }
        }

        tool = default;
        return false;
    }

    /// <summary>
    /// Parses <c>domain=tool</c> config lines into a host→tool map. Blank lines, lines without
    /// '=', and lines with an unrecognized tool token are skipped (defensive against free-text
    /// config). Later duplicate domains win.
    /// </summary>
    private static IReadOnlyDictionary<string, DownloadTool> ParseOverrides(IReadOnlyList<string>? overrides)
    {
        var map = new Dictionary<string, DownloadTool>(StringComparer.OrdinalIgnoreCase);
        if (overrides == null)
        {
            return map;
        }

        foreach (var raw in overrides)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var idx = raw.IndexOf('=');
            if (idx <= 0 || idx == raw.Length - 1)
            {
                continue;
            }

            var domain = raw[..idx].Trim();
            var toolToken = raw[(idx + 1)..].Trim();
            if (domain.Length == 0 || !TryParseTool(toolToken, out var tool))
            {
                continue;
            }

            map[domain] = tool;
        }

        return map;
    }

    /// <summary>Parses a tool token (yt-dlp / ytdlp / svtplay-dl / svtplay, case-insensitive).</summary>
    private static bool TryParseTool(string token, out DownloadTool tool)
    {
        switch (token.Trim().ToLowerInvariant())
        {
            case "yt-dlp":
            case "ytdlp":
            case "youtube-dl":
                tool = DownloadTool.YtDlp;
                return true;
            case "svtplay-dl":
            case "svtplaydl":
            case "svtplay":
                tool = DownloadTool.SvtPlayDl;
                return true;
            default:
                tool = default;
                return false;
        }
    }
}
