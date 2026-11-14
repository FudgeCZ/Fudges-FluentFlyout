// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;

namespace FluentFlyoutWPF.Classes.Lyrics;

public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d+):(\d+(?:[.,:]\d+)?)\]", RegexOptions.CultureInvariant | RegexOptions.ECMAScript)]
    private static partial Regex TimestampRegex();

    /// <summary>
    /// Parses LRC text ([mm:ss.xx] line, multiple timestamps per line supported).
    /// Returns lines sorted by time; metadata tags ([ar:], [ti:]…) are skipped.
    /// </summary>
    public static List<LyricLine> Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var trimmedRaw = raw.TrimEnd('\r');
            var matches = TimestampRegex().Matches(trimmedRaw);
            if (matches.Count == 0)
                continue;

            // Consecutive timestamps before any lyric text share the full line (e.g. [00:10.00][01:20.00]Chorus);
            // later timestamps embedded in the text are inline karaoke word cues and are stripped from text.
            int leadingCount = 1;
            for (int i = 1; i < matches.Count; i++)
            {
                int gapStart = matches[i - 1].Index + matches[i - 1].Length;
                if (string.IsNullOrWhiteSpace(trimmedRaw[gapStart..matches[i].Index]))
                    leadingCount++;
                else
                    break;
            }

            string text = TimestampRegex().Replace(trimmedRaw, "").Trim();
            if (leadingCount < matches.Count && text.Contains("  ", StringComparison.Ordinal))
                text = Regex.Replace(text, @" {2,}", " ");
            if (text.Length == 0)
                continue;

            for (int i = 0; i < leadingCount; i++)
            {
                Match m = matches[i];
                if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
                    minutes > 100000)
                    continue;

                string secStr = m.Groups[2].Value.Replace(',', '.').Replace(':', '.');
                if (!double.TryParse(secStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds) ||
                    double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds >= 1000)
                    continue;

                lines.Add(new LyricLine(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds), text));
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    /// <summary>
    /// Strips noise from track titles before querying providers
    /// (e.g. "(Official Video)", "[HD]", trailing "- feat. X" segments are kept for artist fallback).
    /// </summary>
    public static string CleanQueryTitle(string title)
    {
        string t = Regex.Replace(title, @"\s*[\(\[\{][^)\]}]*(official|video|audio|lyrics?|hd|hq|mv|visualizer)[^)\]}]*[\)\]\}]", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        t = Regex.Replace(t, @"\s*\|\s*official.*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return t.Trim();
    }
}
