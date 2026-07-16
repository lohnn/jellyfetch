using System;
using System.Globalization;
using System.Text.RegularExpressions;

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

    // svtplay-dl live download progress line (verified against svtplay-dl 4.191 on a real SVT Play
    // HLS download, 2026-07-16). It is a DIFFERENT CLI with a DIFFERENT format from yt-dlp — do not
    // route it through TryParseYtDlpLine. Ground truth of a record:
    //   [06/47][==..................] ETA: 0:00:10 | 93 KB/s
    //   - [NN/MM] = current segment / total segments; percent = NN/MM*100 (the only exact source —
    //     svtplay-dl emits NO byte counts live).
    //   - The 20-char [=...] bar mirrors NN/MM (we ignore it, NN/MM is exact).
    //   - "ETA: H:MM:SS" (may be 0:00:00 on the first record).
    //   - " | 93 KB/s" speed suffix is ABSENT on the very first record; units KB/s or MB/s (also
    //     GB/s/B/s tolerated).
    // IMPORTANT: records are CR-terminated (\r), not newline — the ProcessRunner pump must split on
    // CR for these to arrive incrementally (see ProcessRunner.PumpAsync). A download runs in phases
    // (video track segments, then audio track segments) so NN/MM RESETS to 01/MM mid-download; the
    // percent is per-phase and non-monotonic across the phase boundary — acceptable for a live bar.
    private static readonly Regex SvtProgressRegex = new(
        @"\[(?<cur>\d+)/(?<tot>\d+)\]\[[=.]*\]\s*ETA:\s*(?<eta>\d+:\d{2}:\d{2})(?:\s*\|\s*(?<speed>[\d.]+)\s*(?<unit>[KMGT]?B)/s)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Try to parse one svtplay-dl live download progress record; null for non-progress noise
    /// (INFO: lines, blank input). Percent is derived from the segment counter [NN/MM]; svtplay-dl
    /// emits no byte totals, so DownloadedBytes/TotalBytes stay null. Speed is converted to
    /// bytes/second; ETA (H:MM:SS) to seconds. A record with cur == tot is NOT treated as globally
    /// Finished — a download has multiple phases (video then audio), each ending at MM/MM — so
    /// Finished stays false and final completion is asserted by the exit code in the handler.
    /// </summary>
    public static ProgressSnapshot? TryParseSvtPlayDlLine(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var m = SvtProgressRegex.Match(line);
        if (!m.Success)
        {
            return null;
        }

        if (!long.TryParse(m.Groups["cur"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cur)
            || !long.TryParse(m.Groups["tot"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tot)
            || tot <= 0)
        {
            return null;
        }

        var pct = Math.Clamp(cur * 100.0 / tot, 0, 100);
        var eta = ParseHmsToSeconds(m.Groups["eta"].Value);

        long? speed = null;
        if (m.Groups["speed"].Success
            && double.TryParse(m.Groups["speed"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var sv))
        {
            speed = (long)(sv * UnitFactor(m.Groups["unit"].Value));
        }

        return new ProgressSnapshot
        {
            Percent = pct,
            SpeedBytesPerSecond = speed,
            EtaSeconds = eta,
            Finished = false,
        };
    }

    private static long? ParseHmsToSeconds(string hms)
    {
        var parts = hms.Split(':');
        if (parts.Length != 3
            || !long.TryParse(parts[0], out var h)
            || !long.TryParse(parts[1], out var min)
            || !long.TryParse(parts[2], out var s))
        {
            return null;
        }

        return (h * 3600) + (min * 60) + s;
    }

    private static double UnitFactor(string unit) => unit.ToUpperInvariant() switch
    {
        "B" => 1d,
        "KB" => 1024d,
        "MB" => 1024d * 1024,
        "GB" => 1024d * 1024 * 1024,
        "TB" => 1024d * 1024 * 1024 * 1024,
        _ => 1024d, // svtplay-dl's bare default is KB/s; be lenient
    };

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
