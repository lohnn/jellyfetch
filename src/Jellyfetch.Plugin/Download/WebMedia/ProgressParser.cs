using System;
using System.Globalization;

namespace Jellyfetch.Plugin.Download.WebMedia;

/// <summary>A parsed progress snapshot from a yt-dlp progress line.</summary>
internal sealed class ProgressSnapshot
{
    public double? Percent { get; init; }

    public long? DownloadedBytes { get; init; }

    public long? TotalBytes { get; init; }

    public long? SpeedBytesPerSecond { get; init; }

    public long? EtaSeconds { get; init; }

    public bool Finished { get; init; }
}

/// <summary>
/// Parses yt-dlp progress lines emitted with the pinned --progress-template:
///   download:PROG|status|downloaded_bytes|total_bytes|total_bytes_estimate|speed|eta
/// e.g.  PROG|downloading|261120|629172|NA|2223790.17|0
///
/// Ground truth (yt-dlp 2026.06.09): fields can be "NA" — treat as null, never 0.
/// total_bytes is frequently NA → fall back to total_bytes_estimate. speed/eta are NA
/// on the first updates. status flips downloading→finished. Combine with --newline.
/// </summary>
internal static class ProgressParser
{
    public const string Template =
        "download:PROG|%(progress.status)s|%(progress.downloaded_bytes)s|" +
        "%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|" +
        "%(progress.speed)s|%(progress.eta)s";

    public static string[] ProgressArgs() => new[] { "--newline", "--progress-template", Template };

    /// <summary>Try to parse one yt-dlp progress line; null for non-progress noise.</summary>
    public static ProgressSnapshot? TryParseYtDlpLine(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var idx = line.IndexOf("PROG|", StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var parts = line.Substring(idx + "PROG|".Length).Split('|');
        if (parts.Length < 6)
        {
            return null;
        }

        var status = parts[0];
        var downloaded = ParseLong(parts[1]);
        var total = ParseLong(parts[2]) ?? ParseLong(parts[3]);
        var speed = ParseLongFromDouble(parts[4]);
        var eta = ParseLong(parts[5]);

        var finished = string.Equals(status, "finished", StringComparison.OrdinalIgnoreCase);

        double? pct = null;
        if (total is > 0 && downloaded is >= 0)
        {
            pct = Math.Clamp(downloaded.Value * 100.0 / total.Value, 0, 100);
        }
        else if (finished)
        {
            pct = 100;
        }

        return new ProgressSnapshot
        {
            Percent = pct,
            DownloadedBytes = downloaded,
            TotalBytes = total,
            SpeedBytesPerSecond = speed,
            EtaSeconds = eta,
            Finished = finished,
        };
    }

    private static bool IsNa(string s)
        => string.IsNullOrEmpty(s)
           || s.Equals("NA", StringComparison.OrdinalIgnoreCase)
           || s.Equals("None", StringComparison.OrdinalIgnoreCase);

    private static long? ParseLong(string s)
        => IsNa(s) ? (long?)null
            : long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : (long?)null;

    private static long? ParseLongFromDouble(string s)
        => IsNa(s) ? (long?)null
            : double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? (long)v : (long?)null;
}
