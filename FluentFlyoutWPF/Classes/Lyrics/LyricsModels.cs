// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes.Lyrics;

/// <summary>
/// A single lyric line. <see cref="Time"/> is the line's start timestamp;
/// always populated (unsynced lyrics get evenly distributed timestamps).
/// </summary>
public readonly record struct LyricLine(TimeSpan Time, string Text);

/// <summary>
/// Lyrics for one track from one provider.
/// </summary>
public sealed record LyricsResult(
    string Provider,
    bool Synced,
    IReadOnlyList<LyricLine> Lines);

public static class LyricsProviders
{
    public const int LrcLib = 0;
    public const int Musixmatch = 1;
    public const int YouTubeMusic = 2;

    public static string Name(int provider) => provider switch
    {
        LrcLib => "LRCLIB",
        Musixmatch => "Musixmatch",
        YouTubeMusic => "YouTube Music",
        _ => $"Provider({provider})",
    };

    /// <summary>All providers in fallback order (main provider first, then the rest).</summary>
    public static IEnumerable<int> All() => [LrcLib, YouTubeMusic, Musixmatch];
}
