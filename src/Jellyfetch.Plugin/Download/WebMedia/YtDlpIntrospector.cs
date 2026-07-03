using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Jellyfetch.Plugin.Download.WebMedia;

/// <summary>One expanded child of a playlist/series — enough to create an item.</summary>
internal sealed class ExpandedEntry
{
    public ExpandedEntry(string url, string? title, int? ordinal)
    {
        Url = url;
        Title = title;
        Ordinal = ordinal;
    }

    public string Url { get; }

    public string? Title { get; }

    public int? Ordinal { get; }
}

/// <summary>Outcome of classifying a URL: single vs. multi, plus expanded entries.</summary>
internal sealed class UrlClassification
{
    public bool IsMultiJob { get; init; }

    public bool Failed { get; init; }

    public string? ContainerTitle { get; init; }

    public IReadOnlyList<ExpandedEntry> Entries { get; init; } = Array.Empty<ExpandedEntry>();
}

/// <summary>
/// Parses yt-dlp <c>-J --flat-playlist</c> JSON (stdout only) into a
/// <see cref="UrlClassification"/>, and single-item <c>-J</c> JSON into contract
/// <see cref="MediaMetadata"/>.
///
/// Ground truth (yt-dlp 2026.06.09): top-level "_type" is "video" or "playlist";
/// playlist entries carry "_type":"url","url","title"; YouTube singles have no
/// season/episode/series fields → classified <see cref="MediaCategory.Other"/>.
/// JSON is on stdout; warnings/errors on stderr — never merge the streams.
/// </summary>
internal static class YtDlpIntrospector
{
    public static string[] ClassifyArgs(string url) => new[] { "-J", "--flat-playlist", "--no-warnings", url };

    public static string[] MetadataArgs(string url) => new[] { "-J", "--no-warnings", url };

    public static UrlClassification Classify(string stdoutJson)
    {
        if (string.IsNullOrWhiteSpace(stdoutJson))
        {
            return new UrlClassification { Failed = true };
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(stdoutJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new UrlClassification { Failed = true };
        }

        var type = GetString(root, "_type") ?? "video";
        if (type == "playlist")
        {
            var entries = new List<ExpandedEntry>();
            if (root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var ordinal = 1;
                foreach (var e in arr.EnumerateArray())
                {
                    var url = GetString(e, "url") ?? GetString(e, "webpage_url");
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    entries.Add(new ExpandedEntry(url!, GetString(e, "title"), ordinal++));
                }
            }

            return new UrlClassification
            {
                IsMultiJob = true,
                ContainerTitle = GetString(root, "title"),
                Entries = entries,
            };
        }

        return new UrlClassification
        {
            IsMultiJob = false,
            ContainerTitle = GetString(root, "title"),
        };
    }

    public static MediaMetadata ParseMetadata(string json, MediaCategory webVideoDefault)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var series = GetString(root, "series");
        var season = GetInt(root, "season_number");
        var episode = GetInt(root, "episode_number");

        MediaCategory category;
        if (!string.IsNullOrWhiteSpace(series) && (season != null || episode != null))
        {
            category = MediaCategory.Series;
        }
        else
        {
            category = webVideoDefault;
        }

        var title = GetString(root, "title") ?? GetString(root, "id") ?? "Untitled";
        var year = YearFrom(GetString(root, "release_date") ?? GetString(root, "upload_date"))
                   ?? GetInt(root, "release_year");

        return new MediaMetadata
        {
            Category = category,
            Title = title,
            SeriesName = series,
            SeasonNumber = season,
            EpisodeNumber = episode,
            Year = year,
        };
    }

    private static string? GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object &&
           el.TryGetProperty(name, out var p) &&
           p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static int? GetInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p))
        {
            return null;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetInt32(out var i) ? i : (int?)null,
            JsonValueKind.String => int.TryParse(p.GetString(), out var i) ? i : (int?)null,
            _ => null,
        };
    }

    private static int? YearFrom(string? yyyymmdd)
    {
        if (yyyymmdd != null && yyyymmdd.Length >= 4 &&
            int.TryParse(yyyymmdd.Substring(0, 4), out var y) && y > 1900)
        {
            return y;
        }

        return null;
    }
}
