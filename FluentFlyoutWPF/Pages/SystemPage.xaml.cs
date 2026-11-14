// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes.Utils;
using Microsoft.Win32;
using NLog;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;

namespace FluentFlyoutWPF.Pages;

public partial class SystemPage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    public SystemPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        Loaded += (_, _) =>
        {
            UpdateMonitorList();
            RefreshPriorityItems();
        };
    }

    /// <summary>
    /// Fills the priority ComboBox with "(none)" plus every app that currently has a
    /// media session; the stored choice is kept visible even if that app is closed.
    /// </summary>
    private void RefreshPriorityItems()
    {
        var items = new List<string> { "(none)" };

        var mainWindow = Application.Current?.MainWindow as MainWindow;
        if (mainWindow?.mediaManager != null)
        {
            items.AddRange(mainWindow.mediaManager.CurrentMediaSessions.Values
                .Select(s => MediaPlayerData.GetAndCacheMediaPlayerData(s.Id).Item1)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }

        var current = SettingsManager.Current.MediaSessionPriorityApp;
        if (!string.IsNullOrWhiteSpace(current) && !items.Contains(current, System.StringComparer.OrdinalIgnoreCase))
            items.Insert(1, current);

        PriorityComboBox.SelectedItem = null;
        PriorityComboBox.ItemsSource = items;
        PriorityComboBox.SelectedItem = string.IsNullOrWhiteSpace(current)
            ? "(none)"
            : items.FirstOrDefault(i => i.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? current;
    }

    private void PriorityComboBox_DropDownOpened(object sender, EventArgs e)
    {
        RefreshPriorityItems();
    }

    private void PriorityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PriorityComboBox.SelectedItem is not string app) return;

        var value = app == "(none)" ? "" : app;
        if (SettingsManager.Current.MediaSessionPriorityApp == value) return;

        SettingsManager.Current.MediaSessionPriorityApp = value;
        SettingsManager.SaveSettings();
    }

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        SetStartup(StartupSwitch.IsChecked ?? false);
    }

    private void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            const string appName = "FluentFlyout";
            var executablePath = Environment.ProcessPath;

            if (enable)
            {
                if (File.Exists(executablePath))
                {
                    key.SetValue(appName, executablePath);
                }
                else
                {
                    throw new FileNotFoundException("Application executable not found");
                }
            }
            else
            {
                if (key.GetValue(appName) != null)
                {
                    key.DeleteValue(appName, false);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox messageBox = new()
            {
                Title = "Error",
                Content = $"Failed to set startup: {ex.Message}",
                CloseButtonText = "OK",
            };

            _ = messageBox.ShowDialogAsync();
        }
    }

    private void StartupHyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void ToggleSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool isChecked = NIconHideSwitch.IsChecked == true;

        if (Application.Current?.MainWindow is not MainWindow mainWindow)
            return;

        if (!isChecked)
        {
            mainWindow.nIcon.Register();
        }
        else
        {
            mainWindow.nIcon.Unregister();
        }
    }

    private void UpdateMonitorList()
    {
        MonitorUtil.UpdateMonitorList(
            FlyoutSelectedMonitorComboBox,
            () => SettingsManager.Current.FlyoutSelectedMonitor,
            value => SettingsManager.Current.FlyoutSelectedMonitor = value);
    }


    private async void ExportButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"FluentFlyout_Settings_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}",
            DefaultExt = ".xml",
            Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                SettingsManager.SaveSettings(saveFileDialog.FileName, throwOnError: true);

                Wpf.Ui.Controls.MessageBox messageBox = new()
                {
                    Title = Application.Current?.TryFindResource("ExportSuccessful")?.ToString() ?? "Export Successful",
                    Content = Application.Current?.TryFindResource("SettingsExportedSuccessfully")?.ToString() ?? "Settings exported successfully.",
                    CloseButtonText = "OK",
                };

                await messageBox.ShowDialogAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error exporting settings");

                Wpf.Ui.Controls.MessageBox messageBox = new()
                {
                    Title = Application.Current?.TryFindResource("ExportFailed")?.ToString() ?? "Export Failed",
                    Content = Application.Current?.TryFindResource("FailedToExportSettings")?.ToString() ?? "Failed to export settings.",
                    CloseButtonText = "OK",
                };

                await messageBox.ShowDialogAsync();
            }
        }
    }

    private async void ImportButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            DefaultExt = ".xml",
            Filter = "XML Files (*.xml)|*.xml|All Files (*.*)|*.*"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            Wpf.Ui.Controls.MessageBox confirmBox = new()
            {
                Title = Application.Current?.TryFindResource("ImportSettings")?.ToString() ?? "Import Settings",
                Content = Application.Current?.TryFindResource("ImportSettingsWarning")?.ToString() ?? "Importing settings will overwrite your current configuration and restart the application. Continue?",
                CloseButtonText = "No",
                SecondaryButtonText = "Yes",
            };

            var result = await confirmBox.ShowDialogAsync();

            if (result == Wpf.Ui.Controls.MessageBoxResult.Secondary)
            {
                try
                {
                    SettingsManager.RestoreSettings(openFileDialog.FileName, throwOnError: true);
                    SettingsManager.SaveSettings(throwOnError: true);

                    Wpf.Ui.Controls.MessageBox messageBox = new()
                    {
                        Title = Application.Current?.TryFindResource("ImportSuccessful")?.ToString() ?? "Import Successful",
                        Content = Application.Current?.TryFindResource("SettingsImportedSuccessfully")?.ToString() ?? "Settings imported successfully.",
                        CloseButtonText = "OK",
                    };

                    await messageBox.ShowDialogAsync();

                    // Launch a detached helper that waits for this process (and its single-instance Mutex) to exit before restarting
                    using (var currentProcess = Process.GetCurrentProcess())
                    {
                        string? exePath = Environment.ProcessPath ?? currentProcess.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            string escapedExe = exePath.Replace("'", "''");
                            string psCommand = $"Wait-Process -Id {currentProcess.Id} -Timeout 30 -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 300; Start-Process -FilePath '{escapedExe}'";
                            using var helper = Process.Start(new ProcessStartInfo
                            {
                                FileName = "powershell.exe",
                                Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{psCommand}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                            });
                        }
                    }

                    Application.Current?.Shutdown();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error importing settings");

                    Wpf.Ui.Controls.MessageBox messageBox = new()
                    {
                        Title = Application.Current?.TryFindResource("ImportFailed")?.ToString() ?? "Import Failed",
                        Content = Application.Current?.TryFindResource("FailedToImportSettings")?.ToString() ?? "Failed to import settings.",
                        CloseButtonText = "OK",
                    };

                    await messageBox.ShowDialogAsync();
                }
            }
        }
    }

    private void AppFiltering_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(AppFilteringPage));
    }

    private void Advanced_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SettingsWindow.NavigateToPage(typeof(AdvancedPage));
    }
}