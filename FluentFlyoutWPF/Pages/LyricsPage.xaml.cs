// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Lyrics;
using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Pages;

public partial class LyricsPage : Page
{
    public LyricsPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        LyricsService.ClearCache();
    }
}
