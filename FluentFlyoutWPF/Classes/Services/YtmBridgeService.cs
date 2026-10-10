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

    private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private int _configuredPort = -1;
    private bool? _reachability;                    // last known plugin reachability

    private static string BaseUrl =>
        $"http://127.0.0.1:{FluentFlyout.Classes.Settings.SettingsManager.Current.YtmBridgePort}";

    private HttpClient Client
    {
        get
        {
            int port = FluentFlyout.Classes.Settings.SettingsManager.Current.YtmBridgePort;
            if (_configuredPort != port)
            {
                _http.Dispose();
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3), BaseAddress = new Uri(BaseUrl) };
                _configuredPort = port;
            }
            return _http;
        }
    }

    /// <summary>Last known plugin reachability, for settings UI status text.</summary>
    public bool? Reachability => _reachability;

    private async Task<bool> EnsureAuthorizedAsync()
    {
        var settings = FluentFlyout.Classes.Settings.SettingsManager.Current;
        if (!string.IsNullOrEmpty(settings.YtmBridgeToken)) return true;

        try
        {
            var resp = await Client.PostAsync($"/auth/{settings.YtmBridgeClientId}", null);
            if (!resp.IsSuccessStatusCode)
            {
                _reachability = false;
                return false;
            }
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            settings.YtmBridgeToken = json.GetProperty("accessToken").GetString();
            _reachability = true;
            Logger.Info("YTM bridge authorized");
            return true;
        }
        catch (Exception ex)
        {
            _reachability = false;
            Logger.Debug(ex, "YTM bridge auth failed (plugin not running?)");
            return false;
        }
    }

    private async Task<JsonElement?> GetJsonAsync(string path)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            if (FluentFlyout.Classes.Settings.SettingsManager.Current.YtmBridgeToken is { } token)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await Client.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                FluentFlyout.Classes.Settings.SettingsManager.Current.YtmBridgeToken = null;
                return null;
            }
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<JsonElement>();
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
            using var req = new HttpRequestMessage(HttpMethod.Post, path);
            if (FluentFlyout.Classes.Settings.SettingsManager.Current.YtmBridgeToken is { } token)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (body != null) req.Content = JsonContent.Create(body);
            using var resp = await Client.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                FluentFlyout.Classes.Settings.SettingsManager.Current.YtmBridgeToken = null;
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

        _reachability = shuffle.HasValue || repeat.HasValue;
        return (shuffle, repeat);
    }
}
