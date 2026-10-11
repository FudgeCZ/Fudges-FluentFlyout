// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
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
    private readonly Dictionary<string, LyricsResult?> _memoryCache = new();

    private string _requestKey = "";

    /// <summary>Track identity of <see cref="Current"/> ("artist|title|seconds"), or "" when idle.</summary>
    public string CurrentKey { get; private set; } = "";

    /// <summary>
    /// Whether this exact track already has a fetch outcome (so the UI can skip
    /// the loading state instead of re-fetching a known-missing track).
    /// </summary>
    public LyricsTrackState StateOf(string artist, string title, TimeSpan duration)
    {
        if (CurrentKey != MakeKey(artist, title, duration))
            return LyricsTrackState.Unknown;
        return Current != null ? LyricsTrackState.Found : LyricsTrackState.Missing;
    }

    /// <summary>Lyrics for the current track, or null when none found / nothing playing.</summary>
    public LyricsResult? Current { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="Current"/> is replaced.</summary>
    public event Action? LyricsChanged;

    public LyricsService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FluentFlyout-Lyrics/1.0 (personal taskbar widget)");
        _http.Timeout = TimeSpan.FromSeconds(8);
    }

    /// <summary>
    /// Fetches lyrics for a track (main provider unless <paramref name="providerOverride"/> is given).
    /// Safe to call repeatedly; results only apply if this track is still current.
    /// </summary>
    public async Task FetchAsync(string artist, string title, TimeSpan duration, int? providerOverride = null)
    {
        int provider = providerOverride ?? SettingsManager.Current.LyricsProvider;
        string key = MakeKey(artist, title, duration);

        // same key but an explicit provider switch must still refetch
        if (providerOverride == null && key == CurrentKey && Current != null)
            return;

        string cacheKey = $"{key}|p{provider}";
        _requestKey = cacheKey;
        CurrentKey = key;
        Current = null; // stale; no notify — the control drops the old lyrics itself,
                        // and a terminal miss notifies when the fetch completes

        LyricsResult? result;
        if (_memoryCache.TryGetValue(cacheKey, out var cached))
        {
            result = cached;
        }
        else
        {
            result = await FetchLockedAsync(cacheKey, cacheKey, artist, title, duration, provider);
        }

        // main provider found nothing (and the user didn't pick one manually) —
        // fall through the remaining providers before giving up
        if (result == null && providerOverride == null)
        {
            foreach (var alt in LyricsProviders.All())
            {
                if (alt == provider)
                    continue;

                string altKey = $"{key}|p{alt}";
                if (_memoryCache.TryGetValue(altKey, out var altCached) && altCached != null)
                {
                    result = altCached;
                    break;
                }

                Logger.Info($"No lyrics from {LyricsProviders.Name(provider)}, trying {LyricsProviders.Name(alt)}");
                result = await FetchLockedAsync(cacheKey, altKey, artist, title, duration, alt);
                if (result != null)
                    break;
            }
        }

        if (_requestKey != cacheKey)
            return; // a newer request superseded this one

        Current = result;
        if (result != null)
            _memoryCache[cacheKey] = result; // misses are NOT cached — retried on next play
        NotifyChanged();
    }

    /// <summary>Clears the current lyrics and refetches (provider switch, cache invalidation).</summary>
    public void RefreshCurrentTrack()
    {
        string key = CurrentKey;
        if (key.Length == 0)
            return;

        var parts = key.Split('|');
        if (parts.Length != 3)
            return;

        _ = FetchAsync(parts[0], parts[1], TimeSpan.FromSeconds(double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Fetches one provider with its own disk cache. <paramref name="mainCacheKey"/> is
    /// the request identity (staleness check); <paramref name="providerCacheKey"/> is the
    /// per-provider cache entry.
    /// </summary>
    private async Task<LyricsResult?> FetchLockedAsync(string mainCacheKey, string providerCacheKey, string artist, string title, TimeSpan duration, int provider)
    {
        await _fetchLock.WaitAsync();
        try
        {
            if (_requestKey != mainCacheKey)
                return null;

            var disk = LoadFromDisk(providerCacheKey);
            if (disk != null)
                return disk;

            string cleanTitle = LrcParser.CleanQueryTitle(title);
            Logger.Info($"Fetching lyrics ({LyricsProviders.Name(provider)}): {artist} - {cleanTitle}");

            LyricsResult? result = provider switch
            {
                LyricsProviders.Musixmatch => await MusixmatchFetchAsync(artist, cleanTitle, duration),
                LyricsProviders.Genius => await GeniusFetchAsync(artist, cleanTitle),
                LyricsProviders.YouTubeMusic => await YouTubeMusicFetchAsync(artist, cleanTitle),
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
        var subtitle = await MusixmatchJsonAsync("matcher.subtitle.get", query + "&subtitle_format=lrc");
        if (subtitle != null &&
            ExtractMusixmatch(subtitle, "subtitle") is { } subBody &&
            LrcParser.Parse(subBody) is { Count: > 0 } subLines)
        {
            return new LyricsResult(LyricsProviders.Name(LyricsProviders.Musixmatch), true, subLines);
        }

        // fallback: plain lyrics
        var lyrics = await MusixmatchJsonAsync("matcher.lyrics.get", query);
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

    // --- LyricsGenius (needs an API access token in Settings → Lyrics) ---

    private async Task<LyricsResult?> GeniusFetchAsync(string artist, string title)
    {
        var token = SettingsManager.Current.LyricsGeniusToken;
        if (string.IsNullOrWhiteSpace(token))
            return null; // no token configured — provider disabled

        try
        {
            // 1) search for the song
            string query = $"https://api.genius.com/search?q={Uri.EscapeDataString(artist + " " + title)}";
            using var search = new HttpRequestMessage(HttpMethod.Get, query);
            search.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var searchResp = await _http.SendAsync(search);
            if (!searchResp.IsSuccessStatusCode)
                return null;

            string? songUrl;
            using (var stream = await searchResp.Content.ReadAsStreamAsync())
            using (var doc = await JsonDocument.ParseAsync(stream))
            {
                songUrl = doc.RootElement
                    .GetProperty("response").GetProperty("hits").EnumerateArray()
                    .Select(h => h.GetProperty("result"))
                    .Where(r => r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    .Select(r => r.GetProperty("url").GetString())
                    .FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(songUrl))
                return null;

            // 2) the Genius API doesn't serve lyric text — pull the public song page
            using var pageResp = await _http.GetAsync(songUrl);
            if (!pageResp.IsSuccessStatusCode)
                return null;

            string html = await pageResp.Content.ReadAsStringAsync();
            string? lyrics = ExtractGeniusLyrics(html);
            if (string.IsNullOrWhiteSpace(lyrics))
                return null;

            var lines = lyrics.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => new LyricLine(default, t)).ToList();
            return lines.Count > 0 ? new LyricsResult(LyricsProviders.Name(LyricsProviders.Genius), false, lines) : null;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Genius fetch failed");
            return null;
        }
    }

    /// <summary>
    /// Extracts lyric text from a Genius song page. Tries the rendered
    /// Lyrics__Container blocks first, then the __NEXT_DATA__ JSON payload.
    /// </summary>
    private static string? ExtractGeniusLyrics(string html)
    {
        string? ExtractContainer(string block)
        {
            string text = System.Text.RegularExpressions.Regex.Replace(block, @"<br\s*/?>", "\n");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "");
            text = System.Net.WebUtility.HtmlDecode(text);
            return text.Trim();
        }

        // old layout: <div class="Lyrics__Container">…</div> (non-nested, lazy match)
        var containerMatches = System.Text.RegularExpressions.Regex.Matches(
            html, @"<div class=""Lyrics__Container[^""]*""[^>]*>(.*?)</div>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (containerMatches.Count > 0)
        {
            string lyrics = string.Join("\n", containerMatches.Select(m => ExtractContainer(m.Groups[1].Value))
                .Where(t => t.Length > 0));
            if (lyrics.Length > 40)
                return lyrics;
        }

        // new layout: lyrics embedded as a JSON string inside __NEXT_DATA__
        int idx = html.IndexOf("__NEXT_DATA__", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                html[idx..], @"""lyrics"":\s*""((?:[^""\\]|\\.)*)""",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (match.Success)
            {
                string raw = match.Groups[1].Value
                    .Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
                string text = System.Text.RegularExpressions.Regex.Replace(raw, @"<[^>]+>", "");
                text = System.Net.WebUtility.HtmlDecode(text).Trim();
                if (text.Length > 40)
                    return text;
            }
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
            var stream = await resp.Content.ReadAsStreamAsync();
            return await JsonDocument.ParseAsync(stream);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Musixmatch {method} request failed");
            return null;
        }
    }

    // --- YouTube Music (Innertube web client; same flow as ytmusicapi.get_lyrics) ---

    private const string InnertubeContext =
        "{\"context\":{\"client\":{\"clientName\":\"WEB_REMIX\",\"clientVersion\":\"1.20240401.01.00\",\"hl\":\"en\",\"gl\":\"US\"}}}";

    /// <summary>Appends top-level fields to the Innertube context JSON.</summary>
    private static string InnertubeBody(params (string Name, string JsonValue)[] fields)
    {
        string extra = string.Join(",", fields.Select(f => $"\"{f.Name}\":{f.JsonValue}"));
        return InnertubeContext.Insert(InnertubeContext.Length - 1, "," + extra);
    }

    private async Task<string?> InnertubeCallAsync(string endpoint, string body)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://music.youtube.com/youtubei/v1/{endpoint}?prettyPrint=false")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            var auth = YtmSession.GetAuth();
            if (auth != null)
            {
                req.Headers.Add("Cookie", auth.Value.Cookie);
                req.Headers.Add("Origin", "https://music.youtube.com");
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

    private async Task<LyricsResult?> YouTubeMusicFetchAsync(string artist, string title)
    {
        try
        {
            // 1) search for the song
            string search = await InnertubeCallAsync("search", InnertubeBody(("query",
                System.Text.Json.JsonSerializer.Serialize(artist + " " + title))));
            if (search == null)
                return null;

            using var searchDoc = JsonDocument.Parse(search);
            string? videoId = FindStringValue(searchDoc.RootElement, "videoId");
            if (string.IsNullOrEmpty(videoId))
                return null;

            // 2) get the lyrics browse id (MPLYt_...) from the song's toggle menu
            string next = await InnertubeCallAsync("next", InnertubeBody(("videoId",
                System.Text.Json.JsonSerializer.Serialize(videoId))));
            if (next == null)
                return null;

            using var nextDoc = JsonDocument.Parse(next);
            string? browseId = FindStringValue(nextDoc.RootElement, "contentId", "MPLYt");
            if (string.IsNullOrEmpty(browseId))
                return null;

            // 3) fetch the lyrics themselves
            string browse = await InnertubeCallAsync("browse", InnertubeBody(("browseId",
                System.Text.Json.JsonSerializer.Serialize(browseId))));
            if (browse == null)
                return null;

            using var browseDoc = JsonDocument.Parse(browse);
            string? text = FindDescriptionShelfText(browseDoc.RootElement);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => new LyricLine(default, t)).ToList();
            return lines.Count > 0 ? new LyricsResult(LyricsProviders.Name(LyricsProviders.YouTubeMusic), false, lines) : null;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "YouTube Music lyrics fetch failed");
            return null;
        }
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
        var files = dir.GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
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
        lock (Instance._offsets)
        {
            Instance._offsets.Clear();
        }
        try { File.Delete(OffsetsPath); } catch { /* best effort */ }
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

    private static string MakeKey(string artist, string title, TimeSpan duration) =>
        $"{artist}|{title}|{(int)duration.TotalSeconds}";

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
