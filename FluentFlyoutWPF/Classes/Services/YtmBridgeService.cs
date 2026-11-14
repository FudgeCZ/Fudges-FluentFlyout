// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace FluentFlyoutWPF.Classes.Services;

/// <summary>
/// Bridge to the YouTube Music desktop app (Pear/th-ch) "API Server" plugin for
/// shuffle and repeat control, which SMTC cannot set for other apps' sessions.
/// First use requires approving the Allow dialog inside YT Music (POST /auth/{id} →
/// JWT, persisted in settings). All requests fail fast and silently when the plugin
/// is off so callers can fall back to SMTC.
/// </summary>
public sealed class YtmBridgeService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public static YtmBridgeService Instance { get; } = new();

    private readonly Lock _clientLock = new();
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private int _configuredPort = -1;
    private bool? _reachability;                    // last known plugin reachability

    private HttpClient? GetClient()
    {
        int port = FluentFlyout.Classes.Settings.SettingsManager.Current.YtmBridgePort;
        lock (_clientLock)
        {
            if (_configuredPort == port && _http.BaseAddress != null)
                return _http;

            if (port is < 1 or > 65535 || !Uri.TryCreate($"http://127.0.0.1:{port}", UriKind.Absolute, out var baseUri))
            {
                Logger.Debug("Invalid YTM bridge port: {0}", port);
                return null;
            }

            // Validate URI and construct new HttpClient before disposing the old one
            var next = new HttpClient { Timeout = TimeSpan.FromSeconds(3), BaseAddress = baseUri };
            var old = _http;
            _http = next;
            _configuredPort = port;
            old.Dispose();
            return _http;
        }
    }

    private static void SaveSettingsOnUiThread()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            FluentFlyout.Classes.Settings.SettingsManager.SaveSettings();
        else
            dispatcher.BeginInvoke(() => FluentFlyout.Classes.Settings.SettingsManager.SaveSettings());
    }

    /// <summary>Last known plugin reachability, for settings UI status text.</summary>
    public bool? Reachability => _reachability;

    private async Task<bool> EnsureAuthorizedAsync()
    {
        var settings = FluentFlyout.Classes.Settings.SettingsManager.Current;
        if (!string.IsNullOrEmpty(settings.YtmBridgeToken)) return true;

        await _authLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(settings.YtmBridgeToken)) return true;

            var client = GetClient();
            if (client == null)
            {
                _reachability = false;
                return false;
            }

            using var resp = await client.PostAsync($"/auth/{settings.YtmBridgeClientId}", null).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _reachability = false;
                return false;
            }
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
            string? accessToken = json.TryGetProperty("accessToken", out var tok) && tok.ValueKind == JsonValueKind.String
                ? tok.GetString()
                : null;
            if (string.IsNullOrEmpty(accessToken))
            {
                _reachability = false;
                return false;
            }
            settings.YtmBridgeToken = accessToken;
            _reachability = true;
            SaveSettingsOnUiThread();
            Logger.Info("YTM bridge authorized");
            return true;
        }
        catch (Exception ex)
        {
            _reachability = false;
            Logger.Debug(ex, "YTM bridge auth failed (plugin not running?)");
            return false;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<JsonElement?> GetJsonAsync(string path)
    {
        try
        {
            var client = GetClient();
            if (client == null)
            {
                _reachability = false;
                return null;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            if (FluentFlyout.Classes.Settings.SettingsManager.Current.YtmBridgeToken is { Length: > 0 } token)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await client.SendAsync(req).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                FluentFlyout.Classes.Settings.SettingsManager.Current.YtmBridgeToken = "";
                SaveSettingsOnUiThread();
                _reachability = false;
                return null;
            }
            _reachability = resp.IsSuccessStatusCode;
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _reachability = false;
            Logger.Debug(ex, "YTM bridge GET {0} failed", path);
            return null;
        }
    }

    private async Task<bool> PostAsync(string path, object? body = null)
    {
        try
        {
            var client = GetClient();
            if (client == null)
            {
                _reachability = false;
                return false;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, path);
            if (FluentFlyout.Classes.Settings.SettingsManager.Current.YtmBridgeToken is { Length: > 0 } token)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (body != null) req.Content = JsonContent.Create(body);
            using var resp = await client.SendAsync(req).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                FluentFlyout.Classes.Settings.SettingsManager.Current.YtmBridgeToken = "";
                SaveSettingsOnUiThread();
                _reachability = false;
                return false;
            }
            _reachability = resp.IsSuccessStatusCode;
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _reachability = false;
            Logger.Debug(ex, "YTM bridge POST {0} failed", path);
            return false;
        }
    }

    /// <summary>Toggles shuffle in YT Music. Returns false when the plugin is unreachable.</summary>
    public async Task<bool> ToggleShuffleAsync()
    {
        if (!await EnsureAuthorizedAsync()) return false;

        // read the current state so the toggle is deterministic
        var state = await GetJsonAsync("/api/v1/shuffle");
        if (state.HasValue && state.Value.TryGetProperty("state", out var current) && current.ValueKind == JsonValueKind.True)
        {
            Logger.Info("YTM bridge: shuffle off");
            return await PostAsync("/api/v1/shuffle");
        }

        Logger.Info("YTM bridge: shuffle on");
        return await PostAsync("/api/v1/shuffle");
    }

    /// <summary>Advances YT Music's repeat mode by one press. Returns false when unreachable.</summary>
    public async Task<bool> CycleRepeatAsync()
    {
        if (!await EnsureAuthorizedAsync()) return false;
        Logger.Info("YTM bridge: repeat next mode");
        return await PostAsync("/api/v1/switch-repeat", new { iteration = 1 });
    }

    /// <summary>Currently playing song from the bridge, for the lyrics provider's exact
    /// videoId path and the lag-free play state. Nulls when unreachable/idle.</summary>
    public sealed record BridgeSong(string VideoId, string Title, string Artist, bool IsPaused);

    /// <summary>Reads the currently playing song (GET /api/v1/song). Null when idle or unreachable.</summary>
    public async Task<BridgeSong?> GetSongAsync()
    {
        var json = await GetJsonAsync("/api/v1/song");
        if (!json.HasValue) return null;
        try
        {
            if (json.Value.ValueKind != JsonValueKind.Object) return null;
            string? videoId = json.Value.TryGetProperty("videoId", out var vid) ? vid.GetString() : null;
            string? title = json.Value.TryGetProperty("title", out var t) ? t.GetString() : null;
            string? artist = json.Value.TryGetProperty("artist", out var a) ? a.GetString() : null;
            bool isPaused = json.Value.TryGetProperty("isPaused", out var p) && p.ValueKind == JsonValueKind.True;
            if (string.IsNullOrEmpty(videoId)) return null;
            return new BridgeSong(videoId, title ?? "", artist ?? "", isPaused);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "YTM bridge song parse failed");
            return null;
        }
    }

    /// <summary>Reads YT Music's shuffle/repeat state. Nulls when unreachable/unknown.</summary>
    public async Task<(bool? Shuffle, int? Repeat)> GetShuffleRepeatStateAsync()
    {
        if (!await EnsureAuthorizedAsync()) return (null, null);

        bool? shuffle = null;
        var s = await GetJsonAsync("/api/v1/shuffle");
        if (s.HasValue && s.Value.TryGetProperty("state", out var st))
        {
            if (st.ValueKind == JsonValueKind.True) shuffle = true;
            else if (st.ValueKind == JsonValueKind.False) shuffle = false;
        }

        int? repeat = null;
        var r = await GetJsonAsync("/api/v1/repeat-mode");
        if (r.HasValue && r.Value.TryGetProperty("mode", out var mode))
            repeat = mode.GetString() switch
            {
                "ONE" => 2,
                "ALL" => 1,
                "NONE" => 0,
                _ => null,
            };

        return (shuffle, repeat);
    }
}
