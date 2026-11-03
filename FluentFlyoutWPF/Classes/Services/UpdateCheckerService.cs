// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Classes.Clients;
using NLog;
using System.Net.Http;
using System.Text.Json;

namespace FluentFlyoutWPF.Classes.Services;

/// <summary>
/// Handles checking for application updates from the GitHub releases of this fork
/// (replaces upstream's fluentflyout.com API so fork releases drive in-app updates).
/// </summary>
public static class UpdateCheckerService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private const string ReleasesApiUrl = "https://api.github.com/repos/FudgeCZ/Fudges-FluentFlyout/releases/latest";

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // GitHub API rejects requests without a User-Agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FluentFlyout-Fork-UpdateCheck");
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// Result of an update check
    /// </summary>
    public class UpdateCheckResult
    {
        public bool IsUpdateAvailable { get; set; }
        public string NewestVersion { get; set; } = string.Empty;
        public string UpdateUrl { get; set; } = string.Empty;
        public DateTime CheckedAt { get; set; }
        public bool Success { get; set; }
    }

    /// <summary>
    /// Check for updates from the API
    /// </summary>
    /// <param name="currentVersion">The current app version (e.g., "v2.5.0")</param>
    /// <returns>UpdateCheckResult with update information</returns>
    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion)
    {
        var result = new UpdateCheckResult
        {
            CheckedAt = DateTime.Now
        };

        try
        {
            var response = await _http.GetStringAsync(ReleasesApiUrl);
            var json = JsonDocument.Parse(response);

            result.NewestVersion = json.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
            result.UpdateUrl = json.RootElement.GetProperty("html_url").GetString() ?? string.Empty;
            result.Success = true;

            // Compare versions
            result.IsUpdateAvailable = currentVersion != "debug" && IsNewerVersion(currentVersion, result.NewestVersion);

            Logger.Info($"Update check complete. Current: {currentVersion}, Newest: {result.NewestVersion}, Update available: {result.IsUpdateAvailable}");
        }
        catch (HttpRequestException ex)
        {
            Logger.Info($"Failed to check for updates - network error: {ex.Message}");
            result.Success = false;
        }
        catch (TaskCanceledException ex)
        {
            Logger.Info(ex, "Failed to check for updates - request timed out.");
            result.Success = false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected error checking for updates");
            result.Success = false;
        }

        return result;
    }

    public static void OpenUpdateUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open update URL");
        }
    }

    private static bool IsNewerVersion(string currentVersion, string newestVersion)
    {
        try
        {
            var current = Version.Parse(currentVersion.TrimStart('v'));
            var newest = Version.Parse(newestVersion.TrimStart('v'));
            return newest > current;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to compare versions: {currentVersion} vs {newestVersion}");
            return false;
        }
    }
}