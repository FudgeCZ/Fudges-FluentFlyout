// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Lyrics;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Pages;

public partial class LyricsPage : Page
{
    public LyricsPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        Loaded += (_, _) => SessionStatusText.Text =
            SettingsManager.Current.LyricsYtmSession.Length > 0 ? "Session imported." : "";
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        LyricsService.ClearCache();
    }

    private void ImportSession_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            YtmSession.ImportFromApp();
            SessionStatusText.Text = "Session imported.";
        }
        catch (FileNotFoundException)
        {
            SessionStatusText.Text = "YouTube Music app not found.";
        }
        catch (IOException)
        {
            SessionStatusText.Text = "Close the YouTube Music app first, then import.";
        }
        catch (InvalidOperationException)
        {
            SessionStatusText.Text = "No logged-in session found in the app.";
        }
        catch (Exception ex)
        {
            SessionStatusText.Text = "Import failed: " + ex.Message;
        }
    }
}
