// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Lyrics;
using FluentFlyoutWPF.Classes.Utils;
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
        Loaded += (_, _) =>
        {
            LyricsMonitorSelector.RenderMonitors();
            SessionStatusText.Text = SettingsManager.Current.LyricsYtmSession.Length > 0 ? "Session imported." : "";
            UpdateDisabledSongsUi();
        };
        LyricsService.Instance.TrackDisabledStateChanged += UpdateDisabledSongsUi;
        Unloaded += (_, _) => LyricsService.Instance.TrackDisabledStateChanged -= UpdateDisabledSongsUi;
    }

    private void UpdateDisabledSongsUi()
    {
        int count = LyricsService.Instance.DisabledTracksCount;
        DisabledSongsText.Text = count == 0
            ? "No songs currently have lyrics disabled."
            : count == 1
                ? "1 song has lyrics disabled."
                : $"{count} songs have lyrics disabled.";
        ResetDisabledSongsButton.IsEnabled = count > 0;
    }

    private void ResetDisabledSongs_Click(object sender, RoutedEventArgs e)
    {
        LyricsService.Instance.ClearDisabledTracks();
        UpdateDisabledSongsUi();
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        LyricsService.ClearCache();
        UpdateDisabledSongsUi();
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
