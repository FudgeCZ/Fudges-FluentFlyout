// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes.Utils;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Pages;

public partial class TaskbarVisualizerPage : Page
{
    public TaskbarVisualizerPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        Loaded += (_, _) =>
        {
            TaskbarVisualizerMonitorSelector.RenderMonitors();
            RefreshTargetItems();
        };
    }

    // same as SystemPage.StartupHyperlink_RequestNavigate
    private void StartupHyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    /// <summary>
    /// Fills the audio-source ComboBox with "Whole system" plus every app that
    /// currently has a media session; the stored choice stays visible when closed.
    /// </summary>
    private void RefreshTargetItems()
    {
        var items = new List<string> { "Whole system" };

        var mainWindow = Application.Current.MainWindow as MainWindow;
        if (mainWindow?.mediaManager != null)
        {
            items.AddRange(mainWindow.mediaManager.CurrentMediaSessions.Values
                .Select(s => MediaPlayerData.GetAndCacheMediaPlayerData(s.Id).Item1)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderBy(n => n));
        }

        var current = SettingsManager.Current.TaskbarVisualizerTargetApp;
        if (!string.IsNullOrWhiteSpace(current) && !items.Contains(current, System.StringComparer.OrdinalIgnoreCase))
            items.Insert(1, current);

        TargetAppComboBox.SelectedItem = null;
        TargetAppComboBox.ItemsSource = items;
        TargetAppComboBox.SelectedItem = string.IsNullOrWhiteSpace(current) ? "Whole system" : current;
    }

    private void TargetAppComboBox_DropDownOpened(object sender, EventArgs e)
    {
        RefreshTargetItems();
    }

    private void TargetAppComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetAppComboBox.SelectedItem is not string item) return;

        var value = item == "Whole system" ? "" : item;
        if (SettingsManager.Current.TaskbarVisualizerTargetApp == value) return;

        SettingsManager.Current.TaskbarVisualizerTargetApp = value;
        SettingsManager.SaveSettings();
    }
}
