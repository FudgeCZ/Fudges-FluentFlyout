// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentFlyoutWPF.Classes.Lyrics;

public enum LyricsTrackState
{
    Unknown, // never fetched
    Found,   // current track has lyrics (Current holds them)
    Missing, // current track was fetched, nothing found
}

/// <summary>
/// Fetches lyrics for the current track from the configured provider with
/// disk caching and one-flight semantics. Raises <see cref="LyricsChanged"/>
/// on the UI thread when <see cref="Current"/> changes.
/// </summary>
public sealed class LyricsService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly Lazy<LyricsService> _instance = new(() => new LyricsService());
    public static LyricsService Instance => _instance.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentFlyout", "lyrics-cache");

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly ConcurrentDictionary<string, LyricsResult?> _memoryCache = new();
    private readonly object _stateLock = new();

    private long _requestSeq;
    private string _activeFetchId = "";
    private Task<LyricsResult?>? _activeFetchTask;
    private bool _isFetching;
    private (string Artist, string Title, TimeSpan Duration)? _currentTrack;

    /// <summary>Track identity of <see cref="Current"/> ("artist|title|seconds"), or "" when idle.</summary>
    public string CurrentKey { get; private set; } = "";

    /// <summary>
    /// Whether this exact track already has a fetch outcome (so the UI can skip
    /// the loading state instead of re-fetching a known-missing track).
    /// </summary>
    public LyricsTrackState StateOf(string artist, string title, TimeSpan duration)
    {
        if (IsTrackDisabled(artist, title))
            return LyricsTrackState.Missing;
        lock (_stateLock)
        {
            if (CurrentKey != MakeKey(artist, title, duration))
                return LyricsTrackState.Unknown;
            if (Current != null)
                return LyricsTrackState.Found;
            return _isFetching ? LyricsTrackState.Unknown : LyricsTrackState.Missing;
        }
    }

    /// <summary>Lyrics for the current track, or null when none found / nothing playing.</summary>
    public LyricsResult? Current { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="Current"/> is replaced.</summary>
    public event Action? LyricsChanged;

    /// <summary>Raised on the UI thread when per-track disabled state changes.</summary>
    public event Action? TrackDisabledStateChanged;

    /// <summary>Raised on the UI thread when per-track sync offset changes.</summary>
    public event Action? TrackOffsetChanged;

    public LyricsService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FluentFlyout-Lyrics/1.0 (personal taskbar widget)");
        _http.Timeout = TimeSpan.FromSeconds(8);
    }

    private bool IsSuperseded(long seq)
    {
        lock (_stateLock)
            return _requestSeq != seq;
    }

    /// <summary>
    /// Fetches lyrics for a track (main provider unless <paramref name="providerOverride"/> is given).
    /// Safe to call repeatedly; results only apply if this track is still current.
    /// </summary>
    public async Task FetchAsync(string artist, string title, TimeSpan duration, int? providerOverride = null)
    {
        if (IsTrackDisabled(artist, title))
        {
            string disabledKey = MakeKey(artist, title, duration);
            lock (_stateLock)
            {
                _requestSeq++;
                _activeFetchId = "";
                _activeFetchTask = null;
                _isFetching = false;
                CurrentKey = disabledKey;
                _currentTrack = (artist, title, duration);
                Current = null;
            }
            NotifyChanged();
            return;
        }

        int provider = providerOverride ?? SettingsManager.Current.LyricsProvider;
        string key = MakeKey(artist, title, duration);

        long seq;
        lock (_stateLock)
        {
            // same key but an explicit provider switch must still refetch
            if (providerOverride == null && key == CurrentKey && Current != null)
                return;

            seq = ++_requestSeq;
            _activeFetchId = "";
            _activeFetchTask = null;
            _isFetching = true;
            CurrentKey = key;
            _currentTrack = (artist, title, duration);
            Current = null; // stale; no notify — the control drops the old lyrics itself,
                            // and a terminal miss notifies when the fetch completes
        }

        try
        {
            string cacheKey = $"{key}|p{provider}";
            string winningCacheKey = cacheKey;
            LyricsResult? result;
            if (_memoryCache.TryGetValue(cacheKey, out var cached) && cached != null)
            {
                result = cached;
            }
            else
            {
                result = await FetchLockedAsync(seq, cacheKey, artist, title, duration, provider);
            }

            // main provider found nothing (and the user didn't pick one manually) —
            // fall through the remaining providers before giving up
            if (result == null && providerOverride == null)
            {
                foreach (var alt in LyricsProviders.All())
                {
                    if (alt == provider)
                        continue;
                    if (IsSuperseded(seq))
                        return;

                    string altKey = $"{key}|p{alt}";
                    if (_memoryCache.TryGetValue(altKey, out var altCached) && altCached != null)
                    {
                        result = altCached;
                        winningCacheKey = altKey;
                        break;
                    }

                    Logger.Info($"No lyrics from {LyricsProviders.Name(provider)}, trying {LyricsProviders.Name(alt)}");
                    result = await FetchLockedAsync(seq, altKey, artist, title, duration, alt);
                    if (result != null)
                    {
                        winningCacheKey = altKey;
                        break;
                    }
                }
            }

            lock (_stateLock)
            {
                if (_requestSeq != seq)
                    return; // a newer request superseded this one

                Current = result;
                _isFetching = false;
            }

            if (result != null)
                _memoryCache[winningCacheKey] = result; // misses are NOT cached — retried on next play
            NotifyChanged();
        }
        finally
        {
            lock (_stateLock)
            {
                if (_requestSeq == seq)
                    _isFetching = false;
            }
        }
    }

    /// <summary>
    /// Convenience entry used by the widget: main provider from settings with
    /// per-track winner memory and automatic fallback.
    /// </summary>
    public Task<LyricsResult?> FetchSmartAsync(string artist, string title, TimeSpan duration) =>
        FetchWithMemoryAsync(artist, title, duration, SettingsManager.Current.LyricsProvider);

    /// <summary>Clears the current lyrics and refetches (provider switch, cache invalidation).</summary>
    public void RefreshCurrentTrack()
    {
        string artist, title;
        TimeSpan duration;
        lock (_stateLock)
        {
            if (_currentTrack is { } t)
            {
                artist = t.Artist;
                title = t.Title;
                duration = t.Duration;
            }
            else
            {
                string key = CurrentKey;
                if (key.Length == 0)
                    return;

                var parts = SplitEscapedKey(key);
                if (parts.Count != 3 ||
                    !double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out double secs))
                    return;

                artist = parts[0];
                title = parts[1];
                duration = TimeSpan.FromSeconds(secs);
            }

            _activeFetchId = "";
            _activeFetchTask = null;
        }

        _ = FetchWithMemoryAsync(artist, title, duration, SettingsManager.Current.LyricsProvider, ignoreWinner: true);
    }

    /// <summary>
    /// Fetches lyrics with per-track winner memory: the provider that previously
    /// won for this track is tried first, then the main provider, then the rest.
    /// Always terminates with exactly one LyricsChanged notification.
    /// </summary>
    public Task<LyricsResult?> FetchWithMemoryAsync(
        string artist, string title, TimeSpan duration, int mainProvider, bool ignoreWinner = false)
    {
        string key = MakeKey(artist, title, duration);
        string inFlightId = $"{key}|p{mainProvider}|{(ignoreWinner ? 1 : 0)}";

        lock (_stateLock)
        {
            if (_activeFetchTask != null && !_activeFetchTask.IsCompleted && _activeFetchId == inFlightId)
                return _activeFetchTask; // share the in-flight task with other monitors

            long seq = ++_requestSeq;
            _activeFetchId = inFlightId;
            _isFetching = true;
            CurrentKey = key;
            _currentTrack = (artist, title, duration);
            Current = null;

            var task = RunFetchWithMemoryAsync(seq, inFlightId, key, artist, title, duration, mainProvider, ignoreWinner);
            _activeFetchTask = task;
            return task;
        }
    }

    private async Task<LyricsResult?> RunFetchWithMemoryAsync(
        long seq, string inFlightId, string key, string artist, string title, TimeSpan duration, int mainProvider, bool ignoreWinner)
    {
        try
        {
            if (IsTrackDisabled(artist, title))
            {
                lock (_stateLock)
                {
                    if (_requestSeq != seq)
                        return null;
                    Current = null;
                    _isFetching = false;
                }
                NotifyChanged();
                return null;
            }

            int? winner = ignoreWinner ? null : GetWinner(artist, title, duration);
            var order = new List<int> { mainProvider };
            order.AddRange(LyricsProviders.All().Where(p => p != mainProvider));
            if (winner.HasValue && winner.Value != mainProvider)
            {
                order.Remove(winner.Value);
                order.Insert(0, winner.Value);
            }

            foreach (int provider in order)
            {
                if (IsSuperseded(seq))
                    return null;

                var result = await TryProviderCachedAsync(seq, key, artist, title, duration, provider);
                if (IsSuperseded(seq))
                    return null; // superseded by a newer track

                if (result != null)
                {
                    if (winner != provider)
                        SetWinner(artist, title, duration, provider);
                    lock (_stateLock)
                    {
                        if (_requestSeq != seq)
                            return null;
                        Current = result;
                        _isFetching = false;
                    }
                    NotifyChanged();
                    return result;
                }
            }

            lock (_stateLock)
            {
                if (_requestSeq != seq)
                    return null;
                Current = null;
                _isFetching = false;
            }
            NotifyChanged(); // terminal miss — the widget must hide
            return null;
        }
        finally
        {
            lock (_stateLock)
            {
                if (_requestSeq == seq)
                    _isFetching = false;
                if (_activeFetchId == inFlightId)
                {
                    _activeFetchId = "";
                    _activeFetchTask = null;
                }
            }
        }
    }

    /// <summary>Per-provider lookup: memory cache, then disk cache, then network.</summary>
    private async Task<LyricsResult?> TryProviderCachedAsync(
        long seq, string key, string artist, string title, TimeSpan duration, int provider)
    {
        string cacheKey = $"{key}|p{provider}";

        if (_memoryCache.TryGetValue(cacheKey, out var cached) && cached != null)
            return cached;

        var disk = LoadFromDisk(cacheKey);
        if (disk != null)
        {
            _memoryCache[cacheKey] = disk;
            return disk;
        }

        var fetched = await FetchLockedAsync(seq, cacheKey, artist, title, duration, provider);
        if (fetched != null)
            _memoryCache[cacheKey] = fetched;
        return fetched;
    }



    // --- per-track provider winners ---

    private static string WinnersPath => Path.Combine(CacheDir, "winners.json");
    private readonly Dictionary<string, int> _winners = [];
    private bool _winnersLoaded;
    private readonly object _winnersLock = new();

    private int? GetWinner(string artist, string title, TimeSpan duration)
    {
        lock (_winnersLock)
        {
            EnsureWinnersLoaded();
            return _winners.TryGetValue(MakeKey(artist, title, duration), out var v) ? v : null;
        }
    }

    private void SetWinner(string artist, string title, TimeSpan duration, int provider)
    {
        lock (_winnersLock)
        {
            EnsureWinnersLoaded();
            _winners[MakeKey(artist, title, duration)] = provider;
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(WinnersPath, JsonSerializer.Serialize(_winners, JsonOptions));
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to save lyrics winners");
            }
        }
    }

    private void EnsureWinnersLoaded()
    {
        if (_winnersLoaded)
            return;
        _winnersLoaded = true;
        try
        {
            if (File.Exists(WinnersPath))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(WinnersPath), JsonOptions);
                if (data != null)
                    foreach (var kv in data)
                        _winners[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to load lyrics winners");
        }
    }

    private async Task<LyricsResult?> FetchLockedAsync(long seq, string providerCacheKey, string artist, string title, TimeSpan duration, int provider)
    {
        if (IsSuperseded(seq))
            return null;

        var disk = LoadFromDisk(providerCacheKey);
        if (disk != null)
            return disk;

        string cleanTitle = LrcParser.CleanQueryTitle(title);

        // Fetch YTM bridge state before acquiring _fetchLock so the bridge HTTP call doesn't block other fetches
        Classes.Services.YtmBridgeService.BridgeSong? bridgeSong = null;
        if (provider == LyricsProviders.YouTubeMusic)
        {
            bridgeSong = await Classes.Services.YtmBridgeService.Instance.GetSongAsync();
            if (IsSuperseded(seq))
                return null;
        }

        await _fetchLock.WaitAsync();
        try
        {
            if (IsSuperseded(seq))
                return null; // superseded

            disk = LoadFromDisk(providerCacheKey);
            if (disk != null)
                return disk;

            Logger.Info($"Fetching lyrics ({LyricsProviders.Name(provider)}): {artist} - {cleanTitle}");

            LyricsResult? result = provider switch
            {
                LyricsProviders.Musixmatch => await MusixmatchFetchAsync(artist, cleanTitle, duration),
                LyricsProviders.YouTubeMusic => await YouTubeMusicFetchAsync(artist, cleanTitle, bridgeSong),
                _ => await LrcLibFetchAsync(artist, cleanTitle, duration),
            };

            if (result != null)
                SaveToDisk(providerCacheKey, result);

            return result;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Lyrics fetch failed");
            return null;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    // --- LRCLIB ---

    private async Task<LyricsResult?> LrcLibFetchAsync(string artist, string title, TimeSpan duration)
    {
        // multi-artist strings ("A, B a C") don't match LRCLIB's single-artist index;
        // fall back to progressively simpler artist variants
        foreach (var artistVariant in ArtistVariants(artist))
        {
            var result = await LrcLibSearchAsync(artistVariant, title, duration);
            if (result != null)
                return result;
        }
        return null;
    }

    private static IEnumerable<string> ArtistVariants(string artist)
    {
        yield return artist;
        int comma = artist.IndexOf(',');
        if (comma > 0)
            yield return artist[..comma].Trim();
        int czAnd = artist.IndexOf(" a ", StringComparison.Ordinal);
        if (czAnd > 0)
            yield return artist[..czAnd].Trim();
    }

    private async Task<LyricsResult?> LrcLibSearchAsync(string artist, string title, TimeSpan duration)
    {
        string url = "https://lrclib.net/api/search" +
                     $"?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
        using var resp = await _http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        // prefer synced results whose duration is closest to the player's
        JsonElement best = default;
        int bestScore = int.MinValue;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            int score = 0;
            double? dur = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetDouble() : null;
            if (dur.HasValue && duration.TotalSeconds > 1)
                score -= (int)Math.Abs(dur.Value - duration.TotalSeconds);

            if (item.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String && s.GetString()?.Length > 0)
                score += 10000;

            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        if (best.ValueKind == JsonValueKind.Undefined)
            return null;

        if (best.TryGetProperty("syncedLyrics", out var syncedEl) &&
            syncedEl.ValueKind == JsonValueKind.String &&
            syncedEl.GetString() is { Length: > 0 } synced &&
            LrcParser.Parse(synced) is { Count: > 0 } lines)
        {
            return new LyricsResult(LyricsProviders.Name(LyricsProviders.LrcLib), true, lines);
        }

        if (best.TryGetProperty("plainLyrics", out var plainEl) &&
            plainEl.ValueKind == JsonValueKind.String &&
            plainEl.GetString() is { Length: > 0 } plain)
        {
            var plainLines = plain.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => new LyricLine(default, t)).ToList();
            return plainLines.Count > 0 ? new LyricsResult(LyricsProviders.Name(LyricsProviders.LrcLib), false, plainLines) : null;
        }

        return null;
    }

    // --- Musixmatch (community desktop endpoint; experimental, may stop working any time) ---

    private const string MusixmatchToken = "200219bbfdd9ad6fo4uanfnbafff5a6a1cfa3fb6";
    private const string MusixmatchBase = "https://apic-desktop.musixmatch.com/ws/1.1/";

    private async Task<LyricsResult?> MusixmatchFetchAsync(string artist, string title, TimeSpan duration)
    {
        string query = $"format=json&app_id=web-desktop-app-v1.0&usertoken={MusixmatchToken}" +
                       $"&q_artist={Uri.EscapeDataString(artist)}&q_track={Uri.EscapeDataString(title)}";

        // synced lyrics first
        using var subtitle = await MusixmatchJsonAsync("matcher.subtitle.get", query + "&subtitle_format=lrc");
        if (subtitle != null &&
            ExtractMusixmatch(subtitle, "subtitle") is { } subBody &&
            LrcParser.Parse(subBody) is { Count: > 0 } subLines)
        {
            return new LyricsResult(LyricsProviders.Name(LyricsProviders.Musixmatch), true, subLines);
        }

        // fallback: plain lyrics
        using var lyrics = await MusixmatchJsonAsync("matcher.lyrics.get", query);
        if (lyrics != null &&
            ExtractMusixmatch(lyrics, "lyrics") is { } body &&
            body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } arr)
        {
            var lines = arr.Select(t => new LyricLine(default, t)).ToList();
            return new LyricsResult(LyricsProviders.Name(LyricsProviders.Musixmatch), false, lines);
        }

        return null;
    }

    private static string? ExtractMusixmatch(JsonDocument doc, string bodyName)
    {
        try
        {
            if (doc.RootElement.TryGetProperty("message", out var message) &&
                message.TryGetProperty("header", out var header) &&
                header.TryGetProperty("status_code", out var code) &&
                code.GetInt32() == 200 &&
                message.TryGetProperty("body", out var body) &&
                body.TryGetProperty(bodyName, out var target) &&
                target.ValueKind == JsonValueKind.Object &&
                target.TryGetProperty(bodyName == "subtitle" ? "subtitle_body" : "lyrics_body", out var textEl) &&
                textEl.ValueKind == JsonValueKind.String)
            {
                string text = textEl.GetString() ?? "";
                // musixmatch plain lyrics end with a "///" separator block
                int sep = text.IndexOf("///", StringComparison.Ordinal);
                if (sep > 0)
                    text = text[..sep];
                return text.Trim().Length > 0 ? text : null;
            }
        }
        catch
        {
            // malformed response handled as "no lyrics"
        }
        return null;
    }

    private async Task<JsonDocument?> MusixmatchJsonAsync(string method, string query)
    {
        try
        {
            using var resp = await _http.GetAsync(MusixmatchBase + method + "?" + query);
            if (!resp.IsSuccessStatusCode)
                return null;
            await using var stream = await resp.Content.ReadAsStreamAsync();
            return await JsonDocument.ParseAsync(stream);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Musixmatch {method} request failed");
            return null;
        }
    }

    // --- YouTube Music (Innertube) ---
    // WEB_REMIX serves the search (classic, typed results); ANDROID_MUSIC is required for
    // the lyrics fetch — the mobile client is the only one that returns timed lyrics
    // (timedLyricsData), web clients get at most the plain description shelf. The
    // X-YouTube-Client-Name/-Version headers must match the body context or the backend
    // answers "Lyrics not available at this time."
    private const string InnertubeSearchContext =
        "{\"context\":{\"client\":{\"clientName\":\"WEB_REMIX\",\"clientVersion\":\"1.20240401.01.00\",\"hl\":\"en\",\"gl\":\"US\"}}}";

    private const string InnertubeAndroidContext =
        "{\"context\":{\"client\":{\"clientName\":\"ANDROID_MUSIC\",\"clientVersion\":\"7.21.50\",\"hl\":\"en\",\"gl\":\"US\"}}}";

    private const string InnertubeSearchClientId = "26";
    private const string InnertubeAndroidClientId = "21";
    private const string InnertubeAndroidClientVersion = "7.21.50";

    /// <summary>Appends top-level fields to an Innertube context JSON.</summary>
    private static string InnertubeBody(string context, params (string Name, string JsonValue)[] fields)
    {
        string extra = string.Join(",", fields.Select(f => $"\"{f.Name}\":{f.JsonValue}"));
        return context.Insert(context.Length - 1, "," + extra);
    }

    private async Task<string?> InnertubeCallAsync(string endpoint, string body, string clientName, string clientVersion)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://music.youtube.com/youtubei/v1/{endpoint}?prettyPrint=false")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("Origin", "https://music.youtube.com");
            req.Headers.Add("Referer", "https://music.youtube.com/");
            req.Headers.Add("X-YouTube-Client-Name", clientName);
            req.Headers.Add("X-YouTube-Client-Version", clientVersion);
            var auth = YtmSession.GetAuth();
            if (auth != null)
            {
                req.Headers.Add("Cookie", auth.Value.Cookie);
                if (auth.Value.Authorization.Length > 0)
                    req.Headers.Add("Authorization", auth.Value.Authorization);
            }
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Innertube {endpoint} request failed");
            return null;
        }
    }

    /// <summary>Depth-first walk for the first string value of a JSON key.</summary>
    private static string? FindStringValue(JsonElement element, string key, string? startsWith = null)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.NameEquals(key) && prop.Value.ValueKind == JsonValueKind.String)
                {
                    var v = prop.Value.GetString();
                    if (v != null && (startsWith == null || v.StartsWith(startsWith, StringComparison.Ordinal)))
                        return v;
                }
                var nested = FindStringValue(prop.Value, key, startsWith);
                if (nested != null)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindStringValue(item, key, startsWith);
                if (nested != null)
                    return nested;
            }
        }
        return null;
    }

    private static string NormalizeForMatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private async Task<LyricsResult?> YouTubeMusicFetchAsync(
        string artist, string title, Classes.Services.YtmBridgeService.BridgeSong? bridge)
    {
        try
        {
            // 1) videoId — exact one from the YTM desktop app when it plays this track,
            //    otherwise search. Skipping search also avoids landing on the official
            //    music video (OMV), which has no lyrics tab; only song (ATV) tracks do.
            string? videoId = null;
            if (bridge != null && bridge.VideoId.Length > 0)
            {
                string normBridgeTitle = NormalizeForMatch(LrcParser.CleanQueryTitle(bridge.Title));
                string normTitle = NormalizeForMatch(title);
                if (normBridgeTitle.Length > 0 && string.Equals(normBridgeTitle, normTitle, StringComparison.Ordinal))
                {
                    string normBridgeArtist = NormalizeForMatch(bridge.Artist);
                    string normArtist = NormalizeForMatch(artist);
                    if (normBridgeArtist.Length == 0 || normArtist.Length == 0 ||
                        normBridgeArtist.Contains(normArtist, StringComparison.Ordinal) ||
                        normArtist.Contains(normBridgeArtist, StringComparison.Ordinal))
                    {
                        videoId = bridge.VideoId;
                    }
                }
            }

            if (videoId == null)
            {
                string search = await InnertubeCallAsync("search",
                    InnertubeBody(InnertubeSearchContext, ("query",
                        System.Text.Json.JsonSerializer.Serialize(artist + " " + title))),
                    InnertubeSearchClientId, "1.20240401.01.00");
                if (search == null)
                    return null;

                using var searchDoc = JsonDocument.Parse(search);
                videoId = FindSongVideoId(searchDoc.RootElement);
                if (string.IsNullOrEmpty(videoId))
                    return null;
            }

            // 2) get the lyrics browse id (MPLYt_...) from the song's tab bar
            string next = await InnertubeCallAsync("next",
                InnertubeBody(InnertubeAndroidContext, ("videoId",
                    System.Text.Json.JsonSerializer.Serialize(videoId))),
                InnertubeAndroidClientId, InnertubeAndroidClientVersion);
            if (next == null)
                return null;

            using var nextDoc = JsonDocument.Parse(next);
            string? browseId = FindStringValue(nextDoc.RootElement, "browseId", "MPLYt")
                ?? FindStringValue(nextDoc.RootElement, "contentId", "MPLYt");
            if (string.IsNullOrEmpty(browseId))
                return null;

            // 3) fetch the lyrics themselves (ANDROID_MUSIC client for timed data)
            string browse = await InnertubeCallAsync("browse",
                InnertubeBody(InnertubeAndroidContext, ("browseId",
                    System.Text.Json.JsonSerializer.Serialize(browseId))),
                InnertubeAndroidClientId, InnertubeAndroidClientVersion);
            if (browse == null)
                return null;

            using var browseDoc = JsonDocument.Parse(browse);
            var lines = FindTimedLyrics(browseDoc.RootElement);
            if (lines != null && lines.Count > 0)
                return new LyricsResult(LyricsProviders.Name(LyricsProviders.YouTubeMusic), true, lines);

            string? text = FindDescriptionShelfText(browseDoc.RootElement);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => new LyricLine(default, t)).ToList();
            return lines.Count > 0 ? new LyricsResult(LyricsProviders.Name(LyricsProviders.YouTubeMusic), false, lines) : null;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "YouTube Music lyrics fetch failed");
            return null;
        }
    }

    /// <summary>Picks a song (ATV) videoId from search results — official music videos
    /// (OMV) and user uploads have no lyrics tab, so they are skipped when a song
    /// version exists.</summary>
    private static string? FindSongVideoId(JsonElement element)
    {
        List<string> songIds = [];
        List<string> anyIds = [];
        CollectSongCandidates(element, songIds, anyIds);
        if (songIds.Count > 0) return songIds[0];
        if (anyIds.Count > 0) return anyIds[0];
        return null;
    }

    private static void CollectSongCandidates(JsonElement element, List<string> songIds, List<string> anyIds)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if ((prop.NameEquals("playNavigationEndpoint") || prop.NameEquals("navigationEndpoint")) &&
                    prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("watchEndpoint", out var watch) &&
                    watch.TryGetProperty("videoId", out var vid) &&
                    vid.ValueKind == JsonValueKind.String)
                {
                    string? id = vid.GetString();
                    if (id == null)
                        continue;
                    anyIds.Add(id);
                    if (watch.TryGetProperty("watchEndpointMusicSupportedConfigs", out var configs) &&
                        configs.TryGetProperty("watchEndpointMusicConfig", out var musicConfig) &&
                        musicConfig.TryGetProperty("musicVideoType", out var type) &&
                        type.ValueKind == JsonValueKind.String)
                    {
                        string t = type.GetString() ?? "";
                        if (t is "MUSIC_VIDEO_TYPE_OFFICIAL_SOURCE_MUSIC" or "MUSIC_VIDEO_TYPE_ATV")
                            songIds.Add(id);
                    }
                    continue;
                }
                CollectSongCandidates(prop.Value, songIds, anyIds);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectSongCandidates(item, songIds, anyIds);
        }
    }

    /// <summary>Finds timedLyricsData and converts its cue ranges (milliseconds as JSON
    /// strings) into synced lines. Null when the response has none.</summary>
    private static List<LyricLine>? FindTimedLyrics(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.NameEquals("timedLyricsData") && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    List<LyricLine> lines = [];
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object ||
                            !item.TryGetProperty("lyricLine", out var line) ||
                            line.ValueKind != JsonValueKind.String ||
                            !item.TryGetProperty("cueRange", out var cue) ||
                            !cue.TryGetProperty("startTimeMilliseconds", out var start) ||
                            start.ValueKind != JsonValueKind.String ||
                            !long.TryParse(start.GetString(), out long ms))
                            continue;
                        string text = line.GetString() ?? "";
                        if (text.Length == 0)
                            continue;
                        lines.Add(new LyricLine(TimeSpan.FromMilliseconds(ms), text));
                    }
                    if (lines.Count > 0)
                        return lines;
                }
                var nested = FindTimedLyrics(prop.Value);
                if (nested != null)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindTimedLyrics(item);
                if (nested != null)
                    return nested;
            }
        }
        return null;
    }

    /// <summary>Finds a musicDescriptionShelfRenderer and joins its description runs.</summary>
    private static string? FindDescriptionShelfText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.NameEquals("musicDescriptionShelfRenderer") &&
                    prop.Value.TryGetProperty("description", out var desc) &&
                    desc.TryGetProperty("runs", out var runs) &&
                    runs.ValueKind == JsonValueKind.Array)
                {
                    string text = string.Join("", runs.EnumerateArray()
                        .Where(r => r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        .Select(r => r.GetProperty("text").GetString()));
                    if (text.Trim().Length > 0)
                        return text;
                }
                var nested = FindDescriptionShelfText(prop.Value);
                if (nested != null)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindDescriptionShelfText(item);
                if (nested != null)
                    return nested;
            }
        }
        return null;
    }

    // --- cache & helpers ---

    private LyricsResult? LoadFromDisk(string cacheKey)
    {
        try
        {
            string path = Path.Combine(CacheDir, Sha1(cacheKey) + ".json");
            if (!File.Exists(path))
                return null;

            var dto = JsonSerializer.Deserialize<CachedLyrics>(File.ReadAllText(path), JsonOptions);
            if (dto == null)
                return null;

            return new LyricsResult(dto.Provider, dto.Synced,
                dto.Lines.Select(l => new LyricLine(TimeSpan.FromSeconds(l.T), l.Text ?? "")).ToList());
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Lyrics cache read failed");
            return null;
        }
    }

    private void SaveToDisk(string cacheKey, LyricsResult result)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var dto = new CachedLyrics
            {
                Provider = result.Provider,
                Synced = result.Synced,
                Lines = result.Lines.Select(l => new CachedLine { T = l.Time.TotalSeconds, Text = l.Text }).ToList(),
            };
            File.WriteAllText(Path.Combine(CacheDir, Sha1(cacheKey) + ".json"),
                JsonSerializer.Serialize(dto, JsonOptions));

            PruneCache();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Lyrics cache write failed");
        }
    }

    private void PruneCache()
    {
        var dir = new DirectoryInfo(CacheDir);
        var files = dir.GetFiles("*.json")
            .Where(f => f.Name is not ("winners.json" or "offsets.json" or "disabled.json"))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        for (int i = 200; i < files.Count; i++)
        {
            try { files[i].Delete(); } catch { /* best effort */ }
        }
    }

    public static void ClearCache()
    {
        try
        {
            var dir = new DirectoryInfo(CacheDir);
            if (dir.Exists)
                foreach (var f in dir.GetFiles("*.json"))
                    f.Delete();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to clear lyrics cache");
        }
        Instance._memoryCache.Clear();
        lock (Instance._stateLock)
        {
            Instance._requestSeq++;
            Instance._activeFetchId = "";
            Instance._activeFetchTask = null;
            Instance._isFetching = false;
            Instance.Current = null;
            Instance.CurrentKey = "";
            Instance._currentTrack = null;
        }
        lock (Instance._winnersLock)
        {
            Instance._winners.Clear();
        }
        try { File.Delete(WinnersPath); } catch { /* best effort */ }
        lock (Instance._offsetsLock)
        {
            Instance._offsets.Clear();
        }
        try { File.Delete(OffsetsPath); } catch { /* best effort */ }
        Instance.ClearDisabledTracks();
    }

    // --- per-track disabled lyrics memory ---

    private static string DisabledPath => Path.Combine(CacheDir, "disabled.json");
    private readonly HashSet<string> _disabledTracks = new(StringComparer.OrdinalIgnoreCase);
    private bool _disabledLoaded;
    private readonly object _disabledLock = new();

    private static string MakeTrackId(string? artist, string? title) =>
        $"{EscapeKeyPart((artist ?? "").Trim().ToLowerInvariant())}|{EscapeKeyPart((title ?? "").Trim().ToLowerInvariant())}";

    /// <summary>Whether the user has disabled lyrics for this track.</summary>
    public bool IsTrackDisabled(string? artist, string? title)
    {
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title))
            return false;

        lock (_disabledLock)
        {
            EnsureDisabledLoaded();
            return _disabledTracks.Contains(MakeTrackId(artist, title));
        }
    }

    /// <summary>Total number of tracks with lyrics disabled.</summary>
    public int DisabledTracksCount
    {
        get
        {
            lock (_disabledLock)
            {
                EnsureDisabledLoaded();
                return _disabledTracks.Count;
            }
        }
    }

    /// <summary>Enables or disables lyrics for a specific track and persists the choice.</summary>
    public void SetTrackDisabled(string? artist, string? title, bool disabled)
    {
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title))
            return;

        string id = MakeTrackId(artist, title);
        bool changed;
        lock (_disabledLock)
        {
            EnsureDisabledLoaded();
            changed = disabled ? _disabledTracks.Add(id) : _disabledTracks.Remove(id);
            if (changed)
                SaveDisabledLocked();
        }

        if (!changed)
            return;

        bool matchesCurrent = false;
        lock (_stateLock)
        {
            if (_currentTrack is { } t)
            {
                matchesCurrent = MakeTrackId(t.Artist, t.Title) == id;
            }
            else if (CurrentKey.Length > 0)
            {
                var parts = SplitEscapedKey(CurrentKey);
                matchesCurrent = parts.Count >= 2 && MakeTrackId(parts[0], parts[1]) == id;
            }

            if (matchesCurrent)
            {
                if (disabled)
                {
                    _requestSeq++;
                    _activeFetchId = "";
                    _activeFetchTask = null;
                    _isFetching = false;
                    Current = null;
                }
                else
                {
                    CurrentKey = "";
                    _currentTrack = null;
                }
            }
        }

        if (disabled && matchesCurrent)
            NotifyChanged();

        NotifyDisabledChanged();
    }

    /// <summary>Re-enables lyrics for all previously disabled tracks.</summary>
    public void ClearDisabledTracks()
    {
        lock (_disabledLock)
        {
            EnsureDisabledLoaded();
            _disabledTracks.Clear();
            try { File.Delete(DisabledPath); } catch { /* best effort */ }
        }
        lock (_stateLock)
        {
            _requestSeq++;
            _activeFetchId = "";
            _activeFetchTask = null;
            _isFetching = false;
            Current = null;
            CurrentKey = "";
            _currentTrack = null;
        }
        NotifyDisabledChanged();
    }

    private void SaveDisabledLocked()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(DisabledPath,
                JsonSerializer.Serialize(_disabledTracks.OrderBy(x => x, StringComparer.Ordinal).ToList(), JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to save disabled lyrics tracks");
        }
    }

    private void EnsureDisabledLoaded()
    {
        if (_disabledLoaded)
            return;
        _disabledLoaded = true;
        try
        {
            if (File.Exists(DisabledPath))
            {
                var data = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(DisabledPath), JsonOptions);
                if (data != null)
                    foreach (var item in data)
                        if (!string.IsNullOrWhiteSpace(item))
                            _disabledTracks.Add(item);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to load disabled lyrics tracks");
        }
    }

    private void NotifyDisabledChanged()
    {
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() => TrackDisabledStateChanged?.Invoke());
    }

    // --- per-track sync offset memory ---

    private static string OffsetsPath => Path.Combine(CacheDir, "offsets.json");
    private readonly Dictionary<string, int> _offsets = [];
    private bool _offsetsLoaded;
    private readonly object _offsetsLock = new();

    /// <summary>Per-track sync offset in ms, or null when the track has none.</summary>
    public int? GetOffsetMs(string artist, string title, TimeSpan duration)
    {
        lock (_offsetsLock)
        {
            EnsureOffsetsLoaded();
            return _offsets.TryGetValue(MakeKey(artist, title, duration), out var v) ? v : null;
        }
    }

    public void SetOffsetMs(string artist, string title, TimeSpan duration, int offsetMs)
    {
        lock (_offsetsLock)
        {
            EnsureOffsetsLoaded();
            _offsets[MakeKey(artist, title, duration)] = offsetMs;
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(OffsetsPath,
                    JsonSerializer.Serialize(_offsets, JsonOptions));
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to save lyrics offsets");
            }
        }
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() => TrackOffsetChanged?.Invoke());
    }

    private void EnsureOffsetsLoaded()
    {
        if (_offsetsLoaded)
            return;
        _offsetsLoaded = true;
        try
        {
            if (File.Exists(OffsetsPath))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(OffsetsPath), JsonOptions);
                if (data != null)
                    foreach (var kv in data)
                        _offsets[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to load lyrics offsets");
        }
    }

    private static string EscapeKeyPart(string? value) =>
        (value ?? "").Replace("\\", "\\\\").Replace("|", "\\|");

    private static List<string> SplitEscapedKey(string key)
    {
        var parts = new List<string>(3);
        var sb = new StringBuilder();
        bool escape = false;
        foreach (char c in key)
        {
            if (escape)
            {
                sb.Append(c);
                escape = false;
            }
            else if (c == '\\')
            {
                escape = true;
            }
            else if (c == '|')
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        if (escape)
            sb.Append('\\');
        parts.Add(sb.ToString());
        return parts;
    }

    private static string MakeKey(string artist, string title, TimeSpan duration) =>
        $"{EscapeKeyPart(artist)}|{EscapeKeyPart(title)}|{(int)duration.TotalSeconds}";

    private static string Sha1(string input) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(input)));

    private void NotifyChanged()
    {
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() => LyricsChanged?.Invoke());
    }

    private sealed class CachedLyrics
    {
        public string Provider { get; set; } = "";
        public bool Synced { get; set; }
        public List<CachedLine> Lines { get; set; } = [];
    }

    private sealed class CachedLine
    {
        public double T { get; set; }
        public string? Text { get; set; }
    }
}
