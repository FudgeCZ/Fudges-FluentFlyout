// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes;
using MicaWPF.Controls;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Interaction logic for NextUpWindow.xaml
/// </summary>
public partial class NextUpWindow : MicaWindow
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private readonly MainWindow _mainWindow = (MainWindow)Application.Current.MainWindow;
    private readonly CancellationTokenSource _cts = new();
    private bool _isClosed;

    public NextUpWindow(string title, string artist, BitmapImage? thumbnail)
    {
        DataContext = SettingsManager.Current;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -Width - 9999; // move window out of bounds to prevent flickering, maybe needs better solution
        Top = 9999;
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        WindowHelper.SetTopmost(this);
        CustomWindowChrome.CaptionHeight = 0;

        int additionalMargin = 8; // additional margin to avoid text clipping
        var upNextWidth = StringWidth.GetStringWidth(UpNextTextBlock.Text, fontSize: 12) + additionalMargin;
        var titleWidth = StringWidth.GetStringWidth(title) + additionalMargin;
        var artistWidth = StringWidth.GetStringWidth(artist) + additionalMargin;

        double computedWidth = (titleWidth > artistWidth ? titleWidth : artistWidth) + 76 + upNextWidth;
        Width = Math.Clamp(computedWidth, 200, 400); // floor + max width to prevent clipping or overly wide window
        SongTitle.Text = title;
        SongArtist.Text = artist;
        UpdateThumbnail(thumbnail);
        Show();

        if (SettingsManager.Current.NextUpAcrylicWindowEnabled)
        {
            WindowBlurHelper.EnableBlur(this);
        }
        else
        {
            WindowBlurHelper.DisableBlur(this);
        }

        _mainWindow.OpenAnimation(this);

        _ = WaitAndCloseAsync(_cts.Token);
    }

    private async Task WaitAndCloseAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Math.Max(0, SettingsManager.Current.NextUpDuration), token);
            if (_isClosed || !IsLoaded || token.IsCancellationRequested)
                return;

            _mainWindow.CloseAnimation(this);
            await Task.Delay(MainWindow.getDuration(), token);
            if (_isClosed || !IsLoaded || token.IsCancellationRequested)
                return;

            Close();
        }
        catch (TaskCanceledException)
        {
            // window closed before auto-close timer elapsed
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed while auto-closing NextUpWindow");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _cts.Cancel();
        _cts.Dispose();
        base.OnClosed(e);
    }

    public void UpdateThumbnail(BitmapImage? thumbnail)
    {
        SongImage.ImageSource = thumbnail;
        SongImagePlaceholder.Visibility = SongImage.ImageSource == null ? Visibility.Visible : Visibility.Collapsed;
    }
}