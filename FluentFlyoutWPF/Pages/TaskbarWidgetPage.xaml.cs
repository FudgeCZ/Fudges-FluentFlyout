// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Pages;

public partial class TaskbarWidgetPage : Page
{
    public TaskbarWidgetPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        Loaded += (_, _) => UpdateMonitorList();
        UpdateMonitorList();
    }

    private void UpdateMonitorList()
    {
        MonitorUtil.UpdateMonitorList(
            TaskbarWidgetSelectedMonitorComboBox,
            () => SettingsManager.Current.TaskbarWidgetSelectedMonitor,
            value => SettingsManager.Current.TaskbarWidgetSelectedMonitor = value);
    }

    private void CopyToAllMonitors_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.CopyCurrentMonitorSettingsToAll(MonitorUtil.GetMonitors().Count);
    }
}