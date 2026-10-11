// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FluentFlyout.Windows;

/// <summary>
/// Small popup listing the YouTube Music queue; clicking an item jumps to it.
/// Refreshes every two seconds while open.
/// </summary>
public partial class QueuePopup : Window
{
    private static QueuePopup? _instance;
    private readonly DispatcherTimer _refreshTimer;
    private bool _busy;

    public QueuePopup()
    {
        InitializeComponent();
        FluentFlyoutWPF.Classes.WindowHelper.SetTopmost(this);
        FluentFlyoutWPF.Classes.WindowHelper.SetNoActivate(this);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
    }

    /// <summary>Shows the popup near the taskbar widget (bottom-center-ish).</summary>
    public static void Toggle()
    {
        if (_instance != null)
        {
            _instance.Close();
            return;
        }

        _instance = new QueuePopup();
        _instance.PositionNearTaskbar();
        _instance.Show();
    }

    private void PositionNearTaskbar()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + 12;
        Top = wa.Bottom - Height - 60;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ = RefreshAsync();
        _refreshTimer.Start();
    }

    private void Window_Deactivated(object sender, EventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _instance = null;
        base.OnClosed(e);
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var queue = await FluentFlyoutWPF.Classes.Services.YtmBridgeService.Instance.GetQueueAsync();
            if (queue == null)
            {
                NowPlayingText.Text = "YouTube Music not reachable";
                return;
            }

            QueueList.Children.Clear();
            foreach (var item in queue)
            {
                var row = new Button
                {
                    Content = CreateRow(item),
                    Tag = item.Index,
                    Cursor = Cursors.Hand,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 2),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                };
                row.Click += async (s, e) =>
                {
                    if (s is Button { Tag: int index })
                        await FluentFlyoutWPF.Classes.Services.YtmBridgeService.Instance.JumpToAsync(index);
                    Close();
                };
                QueueList.Children.Add(row);
            }

            NowPlayingText.Text = queue.Count + (queue.Count == 1 ? " track" : " tracks");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private static UIElement CreateRow(FluentFlyoutWPF.Classes.Services.YtmBridgeService.QueueItem item)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = item.Artist,
            FontSize = 11,
            Opacity = 0.55,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return panel;
    }
}
