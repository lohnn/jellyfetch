using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfetch.Plugin.Download.WebMedia;

/// <summary>
/// Sanitizes titles into filesystem-safe path components while preserving Unicode
/// letters (notably Swedish å ä ö Å Ä Ö). Removes only characters genuinely hostile
/// to the common target filesystems (Linux ext4 plus SMB/exFAT shares, since a
/// Jellyfin library often lives on a Samba/exFAT mount).
/// </summary>
internal static class FilenameSanitizer
{
    // Reserved on Windows/SMB shares and/or path separators on Linux; union so a
    // library survives on an exFAT/SMB-backed share.
    private static readonly char[] IllegalChars =
    {
        '<', '>', ':', '"', '/', '\\', '|', '?', '*',
    };

    private static readonly System.Collections.Generic.HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

    /// <summary>
    /// Sanitize a single path component (not a full path — separators are stripped).
    /// Keeps Unicode letters; collapses whitespace; trims trailing dots/spaces
    /// (illegal on Windows/SMB). Returns <paramref name="fallback"/> if the input
    /// reduces to nothing.
    /// </summary>
    public static string Sanitize(string? input, string fallback = "Unknown")
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        var sb = new StringBuilder(input!.Length);
        foreach (var ch in input)
        {
            if (Array.IndexOf(IllegalChars, ch) >= 0 || char.IsControl(ch))
            {
                sb.Append(' ');
                continue;
            }

            sb.Append(ch);
        }

        var collapsed = CollapseWhitespace(sb.ToString()).TrimEnd('.', ' ').TrimStart(' ');
        if (string.IsNullOrWhiteSpace(collapsed))
        {
            return fallback;
        }

        var stem = collapsed;
        var dot = stem.IndexOf('.');
        if (dot > 0)
        {
            stem = stem.Substring(0, dot);
        }

        if (ReservedNames.Contains(stem))
        {
            collapsed = "_" + collapsed;
        }

        return collapsed;
    }

    /// <summary>True if the string is safe to place on disk unchanged (test oracle).</summary>
    public static bool IsSafe(string s)
    {
        foreach (var ch in s)
        {
            if (Array.IndexOf(IllegalChars, ch) >= 0 || char.IsControl(ch))
            {
                return false;
            }
        }

        return s.Length == 0 || (s[s.Length - 1] != '.' && s[s.Length - 1] != ' ');
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s)
        {
            var isSpace = char.IsWhiteSpace(ch);
            if (isSpace)
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                }

                prevSpace = true;
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }

        return sb.ToString().Trim();
    }
}
