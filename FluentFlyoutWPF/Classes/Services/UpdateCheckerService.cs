// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

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
        public string DownloadUrl { get; set; } = string.Empty;
        public string AssetName { get; set; } = string.Empty;
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
            using var json = JsonDocument.Parse(response);

            result.NewestVersion = json.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
            result.UpdateUrl = json.RootElement.GetProperty("html_url").GetString() ?? string.Empty;
            result.Success = true;

            // find the zip asset the custom updater installs (prefer the x64 build)
            if (json.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { } name &&
                        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        bool isX64 = name.Contains("x64", StringComparison.OrdinalIgnoreCase);
                        if (isX64 || string.IsNullOrEmpty(result.DownloadUrl))
                        {
                            result.DownloadUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() ?? "" : "";
                            result.AssetName = name;
                        }
                        if (isX64) break;
                    }
                }
            }

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
            if (!TryParseSemVer(currentVersion, out var current, out var currentPre) ||
                !TryParseSemVer(newestVersion, out var newest, out var newestPre))
            {
                Logger.Warn($"Failed to parse versions for comparison: {currentVersion} vs {newestVersion}");
                return false;
            }

            int versionComparison = newest.CompareTo(current);
            if (versionComparison != 0)
            {
                return versionComparison > 0;
            }

            // Equal core versions: stable release (empty pre-release) is newer than a pre-release.
            if (string.IsNullOrEmpty(newestPre) && !string.IsNullOrEmpty(currentPre))
                return true;
            if (!string.IsNullOrEmpty(newestPre) && string.IsNullOrEmpty(currentPre))
                return false;

            return ComparePreRelease(newestPre, currentPre) > 0;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to compare versions: {currentVersion} vs {newestVersion}");
            return false;
        }
    }

    private static bool TryParseSemVer(string rawVersion, out Version version, out string preRelease)
    {
        version = new Version(0, 0);
        preRelease = string.Empty;

        if (string.IsNullOrWhiteSpace(rawVersion))
            return false;

        string trimmed = rawVersion.Trim().TrimStart('v', 'V');
        int buildMetaIdx = trimmed.IndexOf('+');
        if (buildMetaIdx >= 0)
            trimmed = trimmed[..buildMetaIdx];

        int preIdx = trimmed.IndexOf('-');
        string core = preIdx >= 0 ? trimmed[..preIdx] : trimmed;
        preRelease = preIdx >= 0 ? trimmed[(preIdx + 1)..] : string.Empty;

        if (!Version.TryParse(core, out var parsed) || parsed == null)
            return false;

        // Normalize 2/3/4 components so 1.2 compares equal to 1.2.0.0.
        version = new Version(
            parsed.Major,
            parsed.Minor,
            parsed.Build < 0 ? 0 : parsed.Build,
            parsed.Revision < 0 ? 0 : parsed.Revision);
        return true;
    }

    private static int ComparePreRelease(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return 0;

        var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int count = Math.Min(leftParts.Length, rightParts.Length);

        for (int i = 0; i < count; i++)
        {
            bool leftIsNum = int.TryParse(leftParts[i], out int leftNum);
            bool rightIsNum = int.TryParse(rightParts[i], out int rightNum);

            int cmp = (leftIsNum, rightIsNum) switch
            {
                (true, true) => leftNum.CompareTo(rightNum),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(leftParts[i], rightParts[i], StringComparison.OrdinalIgnoreCase)
            };

            if (cmp != 0)
                return cmp;
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }
}