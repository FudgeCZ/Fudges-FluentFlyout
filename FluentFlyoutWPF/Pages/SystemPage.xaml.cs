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
        UpdateMonitorList();
        Loaded += (_, _) => RefreshPriorityItems();
    }

    /// <summary>
    /// Fills the priority ComboBox with "(none)" plus every app that currently has a
    /// media session; the stored choice is kept visible even if that app is closed.
    /// </summary>
    private void RefreshPriorityItems()
    {
        var items = new List<string> { "(none)" };

        var mainWindow = Application.Current.MainWindow as MainWindow;
        if (mainWindow?.mediaManager != null)
        {
            items.AddRange(mainWindow.mediaManager.CurrentMediaSessions.Values
                .Select(s => MediaPlayerData.GetAndCacheMediaPlayerData(s.Id).Item1)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderBy(n => n));
        }

        var current = SettingsManager.Current.MediaSessionPriorityApp;
        if (!string.IsNullOrWhiteSpace(current) && !items.Contains(current, System.StringComparer.OrdinalIgnoreCase))
            items.Insert(1, current);

        PriorityComboBox.SelectedItem = null;
        PriorityComboBox.ItemsSource = items;
        PriorityComboBox.SelectedItem = string.IsNullOrWhiteSpace(current) ? "(none)" : current;
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
        bool isChecked = (bool)NIconHideSwitch.IsChecked;

        MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;

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
                SettingsManager.SaveSettings(saveFileDialog.FileName);

                Wpf.Ui.Controls.MessageBox messageBox = new()
                {
                    Title = Application.Current.FindResource("ExportSuccessful").ToString(),
                    Content = Application.Current.FindResource("SettingsExportedSuccessfully").ToString(),
                    CloseButtonText = "OK",
                };

                _ = messageBox.ShowDialogAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error exporting settings");

                Wpf.Ui.Controls.MessageBox messageBox = new()
                {
                    Title = Application.Current.FindResource("ExportFailed").ToString(),
                    Content = Application.Current.FindResource("FailedToExportSettings").ToString(),
                    CloseButtonText = "OK",
                };

                _ = messageBox.ShowDialogAsync();
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
                Title = Application.Current.FindResource("ImportSettings").ToString(),
                Content = Application.Current.FindResource("ImportSettingsWarning").ToString(),
                CloseButtonText = "No",
                SecondaryButtonText = "Yes",
            };

            var result = await confirmBox.ShowDialogAsync();

            if (result == Wpf.Ui.Controls.MessageBoxResult.Secondary)
            {
                try
                {
                    SettingsManager.RestoreSettings(openFileDialog.FileName);
                    SettingsManager.SaveSettings();

                    Wpf.Ui.Controls.MessageBox messageBox = new()
                    {
                        Title = Application.Current.FindResource("ImportSuccessful").ToString(),
                        Content = Application.Current.FindResource("SettingsImportedSuccessfully").ToString(),
                        CloseButtonText = "OK",
                    };

                    _ = messageBox.ShowDialogAsync();

                    // Restart the application
                    Application.Current.Shutdown();
                    System.Diagnostics.Process.Start(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error importing settings");

                    Wpf.Ui.Controls.MessageBox messageBox = new()
                    {
                        Title = Application.Current.FindResource("ImportFailed").ToString(),
                        Content = Application.Current.FindResource("FailedToImportSettings").ToString(),
                        CloseButtonText = "OK",
                    };

                    _ = messageBox.ShowDialogAsync();
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