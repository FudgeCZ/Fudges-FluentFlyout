// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace FluentFlyoutWPF.Classes.Lyrics;

public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d+):(\d+(?:[.,:]\d+)?)\]", RegexOptions.CultureInvariant)]
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
            var matches = TimestampRegex().Matches(raw);
            if (matches.Count == 0)
                continue;

            string text = raw[(matches[^1].Index + matches[^1].Length)..].TrimEnd('\r');
            if (text.Length == 0)
                continue;

            foreach (Match m in matches)
            {
                int minutes = int.Parse(m.Groups[1].Value);
                double seconds = double.Parse(m.Groups[2].Value.Replace(',', '.'),
                    System.Globalization.CultureInfo.InvariantCulture);
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
