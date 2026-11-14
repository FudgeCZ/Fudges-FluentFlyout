// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Services;
using FluentFlyoutWPF.Classes.Utils;
using FluentFlyoutWPF.ViewModels;
using NLog;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Windows.ApplicationModel;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;

namespace FluentFlyoutWPF.Pages;

public partial class HomePage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public HomePage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;

        try
        {
            var version = Package.Current.Id.Version;
            VersionTextBlock.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            VersionTextBlock.Text = "debug version";
        }

        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
        UpdateLastCheckedText();
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateState.Current.PropertyChanged -= UpdateState_PropertyChanged;
        UpdateState.Current.PropertyChanged += UpdateState_PropertyChanged;
        UpdateNowButton.IsEnabled = !_updating;
        UpdateLastCheckedText();
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        UpdateState.Current.PropertyChanged -= UpdateState_PropertyChanged;
    }

    private void UpdateState_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UpdateState.LastUpdateCheck) or nameof(UpdateState.LastCheckedText))
        {
            Dispatcher.BeginInvoke(UpdateLastCheckedText);
        }
    }

    private void UpdateLastCheckedText()
    {
        if (UpdateState.Current.LastUpdateCheck != default)
        {
            var format = Application.Current?.TryFindResource("LastChecked")?.ToString() ?? "Last checked: {0}";
            LastCheckedText.Text = string.Format(
                format,
                UpdateState.Current.LastCheckedText);
        }
        else
        {
            LastCheckedText.Text = string.Empty;
        }
    }

    private void ViewUpdates_Click(object sender, RoutedEventArgs e)
    {
        Notifications.OpenChangelogInBrowser();
    }

    private static bool _updating;

    /// <summary>
    /// Custom updater: downloads the release asset, then hands over to a detached
    /// helper that swaps the files once this app exits and restarts it.
    /// </summary>
    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _updating = true;
        UpdateNowButton.IsEnabled = false;

        try
        {
            string version = UpdateState.Current.NewestVersion;
            string url = UpdateState.Current.DownloadUrl;

            var progress = new Progress<double>(p =>
            {
                UpdateStatusText.Text = $"Downloading update… {p:F0}%";
            });
            UpdateStatusText.Text = "Downloading update… 0%";

            string? zipPath = await UpdateInstallerService.DownloadUpdateAsync(url, version, progress);
            if (zipPath == null)
            {
                UpdateStatusText.Text = "Download failed — opening the releases page";
                UpdateCheckerService.OpenUpdateUrl(string.IsNullOrEmpty(UpdateState.Current.UpdateUrl)
                    ? "https://github.com/FudgeCZ/Fudges-FluentFlyout/releases/latest"
                    : UpdateState.Current.UpdateUrl);
                _updating = false;
                UpdateNowButton.IsEnabled = true;
                return;
            }

            UpdateStatusText.Text = "Restarting to apply the update…";
            if (UpdateInstallerService.LaunchStagedUpdate(zipPath))
            {
                Application.Current?.Shutdown();
                return;
            }

            // helper could not start — fall back to the manual route
            UpdateCheckerService.OpenUpdateUrl(UpdateState.Current.UpdateUrl);
            _updating = false;
            UpdateNowButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Custom update flow failed");
            _updating = false;
            UpdateNowButton.IsEnabled = true;
            UpdateStatusText.Text = "Update failed — try again later";
        }
    }

    private long _lastChecked = 0;

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        // prevent multiple clicks within 1 second
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastChecked < 1)
        {
            return;
        }

        _lastChecked = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (UpdateState.Current.IsUpdateAvailable)
        {
            string url = !string.IsNullOrEmpty(UpdateState.Current.UpdateUrl) ? UpdateState.Current.UpdateUrl : "https://github.com/FudgeCZ/Fudges-FluentFlyout/releases/latest";
            UpdateCheckerService.OpenUpdateUrl(url);
        }
        else
        {
            await CheckForUpdatesAsync();
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            UpdateStatusText.Text = Application.Current?.TryFindResource("CheckingForUpdates")?.ToString() ?? "Checking for updates...";

            var result = await UpdateCheckerService.CheckForUpdatesAsync(SettingsManager.Current.LastKnownVersion);

            if (result.Success)
            {
                UpdateState.Current.IsUpdateAvailable = result.IsUpdateAvailable;
                UpdateState.Current.NewestVersion = result.NewestVersion;
                UpdateState.Current.UpdateUrl = result.UpdateUrl;
                UpdateState.Current.DownloadUrl = result.DownloadUrl;
                UpdateState.Current.CanAutoUpdate = result.IsUpdateAvailable && !string.IsNullOrEmpty(result.DownloadUrl);
                UpdateState.Current.LastUpdateCheck = result.CheckedAt;

                UpdateLastCheckedText();

                _ = Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(500); // slight delay for better UX
                        // Clear local Text value so the XAML Style / DataTrigger (UpToDate / UpdateAvailable) stays active
                        UpdateStatusText.ClearValue(System.Windows.Controls.TextBlock.TextProperty);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Error while updating update status text on UI thread");
                    }
                });
            }
            else
            {
                UpdateStatusText.ClearValue(System.Windows.Controls.TextBlock.TextProperty);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for updates from HomePage");
            UpdateStatusText.Text = "Unable to check for updates"; // not localized
        }
    }

    private void MediaFlyout_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(MediaFlyoutPage));
    }

    private void VolumeFlyout_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(VolumeMixerPage));
    }

    private void TaskbarWidget_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(TaskbarWidgetPage));
    }

    private void NextUp_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(NextUpPage));
    }

    private void LockKeys_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(LockKeysPage));
    }

    private void TaskbarVisualizer_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(TaskbarVisualizerPage));
    }

    private void System_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(SystemPage));
    }

    private void ViewMicrosoftStore_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://apps.microsoft.com/detail/9N45NSM4TNBP",
                UseShellExecute = true
            });
        }
        catch
        {
            Logger.Error("Failed to open Microsoft Store page");
        }
    }

    private void ViewLogs_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Process.Start("explorer.exe", FileSystemHelper.GetLogsPath());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open logs folder");
        }
    }

    private void ReportBug_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/unchihugo/FluentFlyout/issues/new/choose",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to open bug report page");
        }
    }
}