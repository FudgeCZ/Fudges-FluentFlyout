// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using FluentFlyoutWPF.Models;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FluentFlyout.Controls;

/// <summary>
/// Windows 11 Display Settings-style spatial monitor selector with per-monitor
/// enable/disable toggles, monitor identification overlays, and profile copying.
/// </summary>
public partial class MonitorSelectorControl : UserControl
{
    public static readonly DependencyProperty FeatureModeProperty =
        DependencyProperty.Register(
            nameof(FeatureMode),
            typeof(string),
            typeof(MonitorSelectorControl),
            new PropertyMetadata("Widget", OnFeatureModeChanged));

    /// <summary>
    /// Which taskbar feature this page primarily controls ("Widget", "Visualizer", or "Lyrics").
    /// </summary>
    public string FeatureMode
    {
        get => (string)GetValue(FeatureModeProperty);
        set => SetValue(FeatureModeProperty, value);
    }

    private readonly List<MonitorTaskbarSettings> _subscribedProfiles = [];
    private static readonly List<Window> _identifyWindows = [];
    private static DispatcherTimer? _identifyTimer;

    public MonitorSelectorControl()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MonitorsCanvas.SizeChanged += (_, _) => RenderMonitors();
    }

    private static void OnFeatureModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MonitorSelectorControl control && control.IsLoaded)
        {
            control.RenderMonitors();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.PropertyChanged -= OnUserSettingsPropertyChanged;
        SettingsManager.Current.PropertyChanged += OnUserSettingsPropertyChanged;

        EnsureValidSelectedMonitor();
        SubscribeMonitorProfiles();
        RenderMonitors();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.PropertyChanged -= OnUserSettingsPropertyChanged;
        UnsubscribeMonitorProfiles();
    }

    private void EnsureValidSelectedMonitor()
    {
        var monitors = MonitorUtil.GetMonitors();
        if (monitors.Count == 0)
            return;

        int current = SettingsManager.Current.TaskbarWidgetSelectedMonitor;
        if (current < 0 || current >= monitors.Count)
        {
            int primaryIdx = 0;
            for (int i = 0; i < monitors.Count; i++)
            {
                if (monitors[i].isPrimary)
                {
                    primaryIdx = i;
                    break;
                }
            }
            SettingsManager.Current.TaskbarWidgetSelectedMonitor = primaryIdx;
        }
    }

    private void SubscribeMonitorProfiles()
    {
        UnsubscribeMonitorProfiles();
        var monitors = MonitorUtil.GetMonitors();
        int count = Math.Max(1, monitors.Count);
        for (int i = 0; i < count; i++)
        {
            var profile = SettingsManager.Current.GetMonitorSettings(i);
            profile.PropertyChanged += OnMonitorProfilePropertyChanged;
            _subscribedProfiles.Add(profile);
        }
    }

    private void UnsubscribeMonitorProfiles()
    {
        foreach (var profile in _subscribedProfiles)
        {
            profile.PropertyChanged -= OnMonitorProfilePropertyChanged;
        }
        _subscribedProfiles.Clear();
    }

    private void OnUserSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsManager.Current.TaskbarWidgetSelectedMonitor)
            or nameof(SettingsManager.Current.TaskbarWidgetEnabled)
            or nameof(SettingsManager.Current.TaskbarVisualizerEnabled)
            or nameof(SettingsManager.Current.LyricsEnabled)
            or nameof(SettingsManager.Current.IsPremiumUnlocked))
        {
            Dispatcher.BeginInvoke(RenderMonitors);
        }
    }

    private void OnMonitorProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitorTaskbarSettings.TaskbarWidgetEnabled)
            or nameof(MonitorTaskbarSettings.TaskbarVisualizerEnabled)
            or nameof(MonitorTaskbarSettings.LyricsEnabled))
        {
            Dispatcher.BeginInvoke(RenderMonitors);
        }
    }

    private bool IsFeatureEnabledOnMonitor(MonitorTaskbarSettings profile)
    {
        return FeatureMode switch
        {
            "Visualizer" => profile.TaskbarWidgetEnabled && profile.TaskbarVisualizerEnabled,
            "Lyrics" => profile.TaskbarWidgetEnabled && profile.LyricsEnabled,
            _ => profile.TaskbarWidgetEnabled
        };
    }

    public void RenderMonitors()
    {
        var monitors = MonitorUtil.GetMonitors();
        MonitorsCanvas.Children.Clear();

        if (monitors.Count == 0)
            return;

        int selectedIdx = Math.Clamp(SettingsManager.Current.TaskbarWidgetSelectedMonitor, 0, monitors.Count - 1);

        double minX = monitors.Min(m => m.monitorArea.Left);
        double minY = monitors.Min(m => m.monitorArea.Top);
        double maxX = monitors.Max(m => m.monitorArea.Right);
        double maxY = monitors.Max(m => m.monitorArea.Bottom);

        double totalW = Math.Max(1.0, maxX - minX);
        double totalH = Math.Max(1.0, maxY - minY);

        double canvasW = MonitorsCanvas.ActualWidth > 20 ? MonitorsCanvas.ActualWidth : 680.0;
        double canvasH = MonitorsCanvas.ActualHeight > 20 ? MonitorsCanvas.ActualHeight : 164.0;

        double availW = Math.Max(220.0, canvasW - 24.0);
        double availH = Math.Max(100.0, canvasH - 12.0);

        double maxSingleMonitorH = monitors.Max(m => Math.Max(1.0, m.monitorArea.Height));
        double scale = Math.Min(availW / totalW, availH / totalH);
        scale = Math.Min(scale, 134.0 / maxSingleMonitorH);

        double scaledTotalW = totalW * scale;
        double scaledTotalH = totalH * scale;

        double originX = (canvasW - scaledTotalW) / 2.0;
        double originY = (canvasH - scaledTotalH) / 2.0;

        const double gapInset = 3.0;

        for (int i = 0; i < monitors.Count; i++)
        {
            int monitorIndex = i;
            var monitor = monitors[i];
            var profile = SettingsManager.Current.GetMonitorSettings(monitorIndex);

            bool isSelected = monitorIndex == selectedIdx;
            bool isEnabled = IsFeatureEnabledOnMonitor(profile);

            double rawLeft = originX + (monitor.monitorArea.Left - minX) * scale;
            double rawTop = originY + (monitor.monitorArea.Top - minY) * scale;
            double rawW = Math.Max(110.0, monitor.monitorArea.Width * scale);
            double rawH = Math.Max(72.0, monitor.monitorArea.Height * scale);

            double tileLeft = rawLeft + gapInset;
            double tileTop = rawTop + gapInset;
            double tileW = Math.Max(96.0, rawW - gapInset * 2);
            double tileH = Math.Max(64.0, rawH - gapInset * 2);

            Color normalBgColor = isSelected
                ? (isEnabled ? Color.FromRgb(0x00, 0x78, 0xD4) : Color.FromRgb(0x1B, 0x46, 0x6B))
                : (isEnabled ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0x22, 0x22, 0x22));

            Color hoverBgColor = isSelected
                ? (isEnabled ? Color.FromRgb(0x10, 0x84, 0xDF) : Color.FromRgb(0x23, 0x55, 0x80))
                : (isEnabled ? Color.FromRgb(0x3E, 0x3E, 0x3E) : Color.FromRgb(0x2B, 0x2B, 0x2B));

            Color borderColor = isSelected
                ? (isEnabled ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x78, 0xD4))
                : (isEnabled ? Color.FromRgb(0x4C, 0x4C, 0x4C) : Color.FromRgb(0x36, 0x36, 0x36));

            var tileBgBrush = new SolidColorBrush(normalBgColor);
            var tileBorder = new Border
            {
                Width = tileW,
                Height = tileH,
                CornerRadius = new CornerRadius(6),
                Background = tileBgBrush,
                BorderBrush = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(isSelected ? 1.75 : 1.0),
                Opacity = (!isSelected && !isEnabled) ? 0.72 : 1.0,
                Cursor = Cursors.Hand,
                ToolTip = $"Display {monitorIndex + 1}: {monitor.deviceName} ({(int)monitor.monitorArea.Width}×{(int)monitor.monitorArea.Height})" +
                          (monitor.isPrimary ? " — Primary" : "")
            };

            tileBorder.MouseEnter += (_, _) => tileBgBrush.Color = hoverBgColor;
            tileBorder.MouseLeave += (_, _) => tileBgBrush.Color = normalBgColor;
            tileBorder.MouseLeftButtonDown += (_, args) =>
            {
                SettingsManager.Current.TaskbarWidgetSelectedMonitor = monitorIndex;
                args.Handled = true;
            };

            var innerGrid = new Grid();

            // Top-left: Primary indicator if primary display
            if (monitor.isPrimary)
            {
                var primaryBadge = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(7, 6, 0, 0),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0x00, 0x00)),
                    Padding = new Thickness(5, 1.5, 5, 1.5),
                    Child = new TextBlock
                    {
                        Text = "Primary",
                        FontSize = 9.5,
                        FontWeight = FontWeights.Medium,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF))
                    }
                };
                innerGrid.Children.Add(primaryBadge);
            }

            // Top-right: Clickable On/Off badge for this monitor
            var togglePillBg = new SolidColorBrush(
                isEnabled
                    ? Color.FromArgb(0x55, 0x00, 0x00, 0x00)
                    : Color.FromArgb(0x66, 0x00, 0x00, 0x00));

            var toggleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var statusDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(isEnabled
                    ? Color.FromRgb(0x4A, 0xDE, 0x80)
                    : Color.FromRgb(0x9C, 0xA3, 0xAF))
            };

            var statusText = new TextBlock
            {
                Text = isEnabled ? "On" : "Off",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };

            toggleRow.Children.Add(statusDot);
            toggleRow.Children.Add(statusText);

            var togglePill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0),
                CornerRadius = new CornerRadius(9),
                Background = togglePillBg,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 2, 7, 2),
                Cursor = Cursors.Hand,
                ToolTip = $"Click to {(isEnabled ? "disable" : "enable")} {FeatureMode.ToLowerInvariant()} on Display {monitorIndex + 1}",
                Child = toggleRow
            };

            togglePill.MouseEnter += (_, _) =>
            {
                togglePillBg.Color = Color.FromArgb(0x88, 0x00, 0x00, 0x00);
            };
            togglePill.MouseLeave += (_, _) =>
            {
                togglePillBg.Color = isEnabled
                    ? Color.FromArgb(0x55, 0x00, 0x00, 0x00)
                    : Color.FromArgb(0x66, 0x00, 0x00, 0x00);
            };
            togglePill.MouseLeftButtonDown += (_, args) =>
            {
                args.Handled = true;
                SettingsManager.Current.SetMonitorFeatureEnabled(monitorIndex, FeatureMode, !isEnabled);
                RenderMonitors();
            };

            innerGrid.Children.Add(togglePill);

            // Center: Large Windows 11-style display number
            var numberText = new TextBlock
            {
                Text = (monitorIndex + 1).ToString(),
                FontSize = 23,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(isEnabled ? Colors.White : Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            innerGrid.Children.Add(numberText);

            // Bottom: Resolution & device name caption
            var bottomText = new TextBlock
            {
                Text = $"{(int)monitor.monitorArea.Width}×{(int)monitor.monitorArea.Height}",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(6, 0, 6, 5),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            innerGrid.Children.Add(bottomText);

            tileBorder.Child = innerGrid;

            Canvas.SetLeft(tileBorder, tileLeft);
            Canvas.SetTop(tileBorder, tileTop);
            MonitorsCanvas.Children.Add(tileBorder);
        }

        UpdateBottomBar(monitors, selectedIdx);
    }

    private void UpdateBottomBar(IReadOnlyList<MonitorUtil.MonitorInfo> monitors, int selectedIdx)
    {
        var selectedMonitor = monitors[selectedIdx];
        var selectedProfile = SettingsManager.Current.GetMonitorSettings(selectedIdx);

        string primaryLabel = selectedMonitor.isPrimary ? " (Primary)" : "";
        SelectedMonitorInfoText.Text =
            $"Display {selectedIdx + 1}{primaryLabel} — {selectedMonitor.deviceName} ({(int)selectedMonitor.monitorArea.Width} × {(int)selectedMonitor.monitorArea.Height})";

        bool widgetOn = selectedProfile.TaskbarWidgetEnabled;
        bool vizOn = selectedProfile.TaskbarWidgetEnabled && selectedProfile.TaskbarVisualizerEnabled;
        bool lyricsOn = selectedProfile.TaskbarWidgetEnabled && selectedProfile.LyricsEnabled;

        StyleFeatureButton(ToggleWidgetButton, "Widget", widgetOn, FeatureMode == "Widget");
        StyleFeatureButton(ToggleVisualizerButton, "Visualizer", vizOn, FeatureMode == "Visualizer");
        StyleFeatureButton(ToggleLyricsButton, "Lyrics", lyricsOn, FeatureMode == "Lyrics");

        SubtitleTextBlock.Text = FeatureMode switch
        {
            "Visualizer" => "Select a display to configure its visualizer settings, or click On/Off on any display to enable or disable the visualizer on that screen.",
            "Lyrics" => "Select a display to configure its lyrics settings, or click On/Off on any display to enable or disable lyrics on that screen.",
            _ => "Select a display to configure its taskbar widget settings, or click On/Off on any display to enable or disable the widget on that screen."
        };
    }

    private static void StyleFeatureButton(Button button, string label, bool isOn, bool isCurrentPageFeature)
    {
        button.Content = $"{(isOn ? "●" : "○")} {label}: {(isOn ? "On" : "Off")}";
        button.Background = new SolidColorBrush(
            isOn
                ? (isCurrentPageFeature ? Color.FromRgb(0x00, 0x78, 0xD4) : Color.FromRgb(0x2D, 0x4A, 0x3E))
                : Color.FromRgb(0x2D, 0x2D, 0x2D));
        button.BorderBrush = new SolidColorBrush(
            isCurrentPageFeature
                ? Color.FromRgb(0x60, 0xCD, 0xFF)
                : (isOn ? Color.FromRgb(0x4A, 0xDE, 0x80) : Color.FromRgb(0x44, 0x44, 0x44)));
        button.Foreground = Brushes.White;
    }

    private void ToggleWidgetButton_Click(object sender, RoutedEventArgs e)
    {
        int idx = SettingsManager.Current.TaskbarWidgetSelectedMonitor;
        var profile = SettingsManager.Current.GetMonitorSettings(idx);
        SettingsManager.Current.SetMonitorFeatureEnabled(idx, "Widget", !profile.TaskbarWidgetEnabled);
        RenderMonitors();
    }

    private void ToggleVisualizerButton_Click(object sender, RoutedEventArgs e)
    {
        int idx = SettingsManager.Current.TaskbarWidgetSelectedMonitor;
        var profile = SettingsManager.Current.GetMonitorSettings(idx);
        bool currentlyOn = profile.TaskbarWidgetEnabled && profile.TaskbarVisualizerEnabled;
        SettingsManager.Current.SetMonitorFeatureEnabled(idx, "Visualizer", !currentlyOn);
        RenderMonitors();
    }

    private void ToggleLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        int idx = SettingsManager.Current.TaskbarWidgetSelectedMonitor;
        var profile = SettingsManager.Current.GetMonitorSettings(idx);
        bool currentlyOn = profile.TaskbarWidgetEnabled && profile.LyricsEnabled;
        SettingsManager.Current.SetMonitorFeatureEnabled(idx, "Lyrics", !currentlyOn);
        RenderMonitors();
    }

    private void CopyToAllButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.CopyCurrentMonitorSettingsToAll(MonitorUtil.GetMonitors().Count);
        RenderMonitors();
    }

    private void IdentifyButton_Click(object sender, RoutedEventArgs e)
    {
        ShowIdentifyOverlays();
    }

    private static void ShowIdentifyOverlays()
    {
        CloseIdentifyOverlays();

        var monitors = MonitorUtil.GetMonitors();
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var win = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false,
                Width = 130,
                Height = 130,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x1F, 0x1F, 0x1F)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1.5),
                    Child = new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        FontSize = 56,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };

            WindowHelper.SetNoActivate(win);
            win.Show();

            double dpiScaleX = m.dpiX > 0 ? m.dpiX / 96.0 : 1.0;
            double dpiScaleY = m.dpiY > 0 ? m.dpiY / 96.0 : 1.0;
            double targetX = m.workArea.Left + 48 * dpiScaleX;
            double targetY = m.workArea.Bottom - (130 + 48) * dpiScaleY;

            WindowHelper.SetPosition(win, targetX, targetY);
            WindowHelper.SetTopmost(win);

            _identifyWindows.Add(win);
        }

        _identifyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        _identifyTimer.Tick += (_, _) => CloseIdentifyOverlays();
        _identifyTimer.Start();
    }

    private static void CloseIdentifyOverlays()
    {
        _identifyTimer?.Stop();
        _identifyTimer = null;

        foreach (var win in _identifyWindows.ToList())
        {
            try
            {
                win.Close();
            }
            catch { }
        }
        _identifyWindows.Clear();
    }
}
