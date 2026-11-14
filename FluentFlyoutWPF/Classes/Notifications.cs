// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using Microsoft.Toolkit.Uwp.Notifications;
using NLog;
using System.Runtime.InteropServices;
using System.Windows;

namespace FluentFlyout.Classes;

[ComVisible(true)]
[Guid("79086E7F-0D65-4507-82B6-85F2288930D5")]
internal static class Notifications
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Handle toast notification activation
    /// </summary>
    public static void HandleNotificationActivation(ToastNotificationActivatedEventArgsCompat toastArgs)
    {
        try
        {
            // Obtain the arguments from the notification
            ToastArguments args = ToastArguments.Parse(toastArgs.Argument);

            // Check if the user clicked the notification
            if (args.TryGetValue("action", out string action))
            {
                switch (action)
                {
                    case "viewChanges":
                        OpenChangelogInBrowser();
                        break;
                    case "downloadUpdate":
                        if (args.TryGetValue("url", out string url))
                        {
                            OpenUrlInBrowser(url);
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to handle notification activation");
        }
    }

    public static void OpenChangelogInBrowser()
    {
        OpenUrlInBrowser("https://fluentflyout.com/changelog/");
    }

    /// <summary>
    /// Show a Windows notification if the application is run for the first time or has been updated.
    /// </summary>
    /// <param name="lastKnownVersion"></param>
    /// <param name="currentVersion"></param>
    public static void ShowFirstOrUpdateNotification(string lastKnownVersion, string currentVersion)
    {
        if (string.IsNullOrEmpty(lastKnownVersion) || currentVersion == "debug")
        {
            return;
        }

        if (lastKnownVersion != currentVersion)
        {
            try
            {
                // updated app version
                new ToastContentBuilder()
                    .AddText(Application.Current?.TryFindResource("UpdateToastTitle")?.ToString() ?? "FluentFlyout Updated")
                    .AddText(string.Format(Application.Current?.TryFindResource("UpdateToastMessage")?.ToString() ?? "Updated to {0}", currentVersion))
                    .AddArgument("action", "viewChanges")
                    .AddButton(new ToastButton()
                        .SetContent(Application.Current?.TryFindResource("UpdateToastButton")?.ToString() ?? "View Changelog")
                        .AddArgument("action", "viewChanges")
                        .SetBackgroundActivation())
                    .Show();

                Logger.Info($"Displayed update notification for {currentVersion}.");

                return;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to show update notification");
                return;
            }
        }
    }

    /// <summary>
    /// Show a Windows notification when an update is available
    /// </summary>
    /// <param name="newVersion">The new version available</param>
    /// <param name="updateUrl">The URL to download the update (can be empty)</param>
    public static void ShowUpdateAvailableNotification(string newVersion, string updateUrl)
    {
        if (!SettingsManager.Current.ShowUpdateNotifications) return;

        long currentUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (currentUnixSeconds - SettingsManager.Current.LastUpdateNotificationUnixSeconds < TimeSpan.FromDays(5).TotalSeconds) // 5 days cooldown
        {
            return;
        }

        try
        {
            var builder = new ToastContentBuilder()
                .AddText(Application.Current?.TryFindResource("UpdateAvailableNotificationTitle")?.ToString() ?? "Update Available!")
                .AddText(string.Format(Application.Current?.TryFindResource("UpdateAvailableNotificationMessage")?.ToString() ?? "Version {0} is available", newVersion))
                .AddArgument("action", "downloadUpdate");

            // only add download button if URL is available
            if (!string.IsNullOrEmpty(updateUrl))
            {
                builder.AddButton(new ToastButton()
                    .SetContent(Application.Current?.TryFindResource("UpdateAvailableNotificationButton")?.ToString() ?? "Download Update")
                    .AddArgument("action", "downloadUpdate")
                    .AddArgument("url", updateUrl)
                    .SetBackgroundActivation());
            }

            builder.Show();

            SettingsManager.Current.LastUpdateNotificationUnixSeconds = currentUnixSeconds;

            Logger.Info($"Displayed update available notification for {newVersion}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to show update available notification");
        }
    }

    public static void OpenUrlInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Logger.Warn($"Blocked opening non-HTTP(S) URL in browser: {url}");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open URL in browser");
        }
    }
}