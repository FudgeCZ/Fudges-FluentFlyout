// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using FluentFlyoutWPF.Models;
using Microsoft.Win32;
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
    private bool _renderPending;

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
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        EnsureValidSelectedMonitor();
        SubscribeMonitorProfiles();
        RenderMonitors();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.PropertyChanged -= OnUserSettingsPropertyChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        UnsubscribeMonitorProfiles();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded)
                return;

            EnsureValidSelectedMonitor();
            SubscribeMonitorProfiles();
            RenderMonitors();
        });
    }

    private void ScheduleRenderMonitors()
    {
        if (_renderPending)
            return;

        _renderPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_renderPending)
                return;
            _renderPending = false;
            if (IsLoaded)
                RenderMonitors();
        }, DispatcherPriority.Background);
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
            ScheduleRenderMonitors();
        }
    }

    private void OnMonitorProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitorTaskbarSettings.TaskbarWidgetEnabled)
            or nameof(MonitorTaskbarSettings.TaskbarVisualizerEnabled)
            or nameof(MonitorTaskbarSettings.LyricsEnabled))
        {
            ScheduleRenderMonitors();
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

    private Brush GetThemeBrush(string resourceKey, Brush fallback)
    {
        return TryFindResource(resourceKey) as Brush
            ?? Application.Current?.TryFindResource(resourceKey) as Brush
            ?? fallback;
    }

    private Color GetThemeColor(string resourceKey, Color fallback)
    {
        if (GetThemeBrush(resourceKey, new SolidColorBrush(fallback)) is SolidColorBrush scb)
            return scb.Color;
        return fallback;
    }

    private static Rect[] ComputeNonOverlappingTileRects(
        IReadOnlyList<MonitorUtil.MonitorInfo> monitors,
        double canvasW,
        double canvasH,
        double availW,
        double availH)
    {
        double minX = monitors.Min(m => m.monitorArea.Left);
        double minY = monitors.Min(m => m.monitorArea.Top);
        double maxX = monitors.Max(m => m.monitorArea.Right);
        double maxY = monitors.Max(m => m.monitorArea.Bottom);

        double totalW = Math.Max(1.0, maxX - minX);
        double totalH = Math.Max(1.0, maxY - minY);

        double maxSingleMonitorH = monitors.Max(m => Math.Max(1.0, m.monitorArea.Height));
        double scale = Math.Min(availW / totalW, availH / totalH);
        scale = Math.Min(scale, 134.0 / maxSingleMonitorH);

        const double minTileW = 110.0;
        const double minTileH = 72.0;

        var rects = new Rect[monitors.Count];
        for (int i = 0; i < monitors.Count; i++)
        {
            var area = monitors[i].monitorArea;
            double x = (area.Left - minX) * scale;
            double y = (area.Top - minY) * scale;
            double w = Math.Max(minTileW, area.Width * scale);
            double h = Math.Max(minTileH, area.Height * scale);
            rects[i] = new Rect(x, y, w, h);
        }

        // Push apart any tiles that overlap because one or more monitors were clamped to minTileW/minTileH
        var order = Enumerable.Range(0, monitors.Count)
            .OrderBy(i => monitors[i].monitorArea.Left)
            .ThenBy(i => monitors[i].monitorArea.Top)
            .ToArray();

        for (int pass = 0; pass < monitors.Count; pass++)
        {
            bool moved = false;
            for (int a = 0; a < order.Length; a++)
            {
                int i = order[a];
                for (int b = a + 1; b < order.Length; b++)
                {
                    int j = order[b];
                    if (!rects[i].IntersectsWith(rects[j]))
                        continue;

                    double overlapX = Math.Min(rects[i].Right, rects[j].Right) - Math.Max(rects[i].Left, rects[j].Left);
                    double overlapY = Math.Min(rects[i].Bottom, rects[j].Bottom) - Math.Max(rects[i].Top, rects[j].Top);
                    if (overlapX <= 0.5 || overlapY <= 0.5)
                        continue;

                    var mi = monitors[i].monitorArea;
                    var mj = monitors[j].monitorArea;

                    if (mj.Left >= mi.Right - 1)
                    {
                        rects[j] = new Rect(rects[i].Right, rects[j].Y, rects[j].Width, rects[j].Height);
                        moved = true;
                    }
                    else if (mj.Top >= mi.Bottom - 1)
                    {
                        rects[j] = new Rect(rects[j].X, rects[i].Bottom, rects[j].Width, rects[j].Height);
                        moved = true;
                    }
                    else if (mi.Top >= mj.Bottom - 1)
                    {
                        rects[i] = new Rect(rects[i].X, rects[j].Bottom, rects[i].Width, rects[i].Height);
                        moved = true;
                    }
                    else if (overlapX <= overlapY)
                    {
                        rects[j] = new Rect(rects[i].Right, rects[j].Y, rects[j].Width, rects[j].Height);
                        moved = true;
                    }
                    else
                    {
                        rects[j] = new Rect(rects[j].X, rects[i].Bottom, rects[j].Width, rects[j].Height);
                        moved = true;
                    }
                }
            }
            if (!moved)
                break;
        }

        // Uniformly fit into available canvas area if pushing apart expanded the group, then center
        double gMinX = rects.Min(r => r.Left);
        double gMinY = rects.Min(r => r.Top);
        double gMaxX = rects.Max(r => r.Right);
        double gMaxY = rects.Max(r => r.Bottom);
        double gW = Math.Max(1.0, gMaxX - gMinX);
        double gH = Math.Max(1.0, gMaxY - gMinY);

        double fit = Math.Min(1.0, Math.Min(availW / gW, availH / gH));
        double originX = (canvasW - gW * fit) / 2.0;
        double originY = (canvasH - gH * fit) / 2.0;

        for (int i = 0; i < rects.Length; i++)
        {
            var r = rects[i];
            rects[i] = new Rect(
                originX + (r.Left - gMinX) * fit,
                originY + (r.Top - gMinY) * fit,
                r.Width * fit,
                r.Height * fit);
        }

        return rects;
    }

    public void RenderMonitors()
    {
        _renderPending = false;
        var monitors = MonitorUtil.GetMonitors();
        MonitorsCanvas.Children.Clear();

        if (monitors.Count == 0)
            return;

        int selectedIdx = Math.Clamp(SettingsManager.Current.TaskbarWidgetSelectedMonitor, 0, monitors.Count - 1);

        double canvasW = MonitorsCanvas.ActualWidth > 20 ? MonitorsCanvas.ActualWidth : 680.0;
        double canvasH = MonitorsCanvas.ActualHeight > 20 ? MonitorsCanvas.ActualHeight : 164.0;

        double availW = Math.Max(220.0, canvasW - 24.0);
        double availH = Math.Max(100.0, canvasH - 12.0);

        Rect[] tileRects = ComputeNonOverlappingTileRects(monitors, canvasW, canvasH, availW, availH);

        const double gapInset = 3.0;

        Color accentColor = GetThemeColor("SystemAccentColorPrimary", Color.FromRgb(0x00, 0x78, 0xD4));
        Color accentHoverColor = GetThemeColor("SystemAccentColorLight1", Color.FromRgb(0x10, 0x84, 0xDF));
        Color accentMutedColor = Color.FromRgb(
            (byte)(accentColor.R * 0.45 + 0x1A),
            (byte)(accentColor.G * 0.45 + 0x1A),
            (byte)(accentColor.B * 0.45 + 0x1A));
        Color unselectedEnabledBg = GetThemeColor("ControlFillColorDefaultBrush", Color.FromRgb(0x33, 0x33, 0x33));
        Color unselectedDisabledBg = GetThemeColor("ControlFillColorSecondaryBrush", Color.FromRgb(0x22, 0x22, 0x22));
        Color unselectedHoverBg = GetThemeColor("ControlFillColorTertiaryBrush", Color.FromRgb(0x3E, 0x3E, 0x3E));
        Color unselectedBorderColor = GetThemeColor("ControlStrokeColorDefaultBrush", Color.FromRgb(0x4C, 0x4C, 0x4C));
        Brush unselectedPrimaryText = GetThemeBrush("TextFillColorPrimaryBrush", Brushes.White);
        Brush unselectedSecondaryText = GetThemeBrush("TextFillColorSecondaryBrush", new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
        Brush selectedTextBrush = GetThemeBrush("TextOnAccentFillColorPrimaryBrush", Brushes.White);

        for (int i = 0; i < monitors.Count; i++)
        {
            int monitorIndex = i;
            var monitor = monitors[i];
            var profile = SettingsManager.Current.GetMonitorSettings(monitorIndex);

            bool isSelected = monitorIndex == selectedIdx;
            bool isEnabled = IsFeatureEnabledOnMonitor(profile);

            Rect rawRect = tileRects[i];
            double tileLeft = rawRect.Left + gapInset;
            double tileTop = rawRect.Top + gapInset;
            double tileW = Math.Max(48.0, rawRect.Width - gapInset * 2);
            double tileH = Math.Max(40.0, rawRect.Height - gapInset * 2);

            Color normalBgColor = isSelected
                ? (isEnabled ? accentColor : accentMutedColor)
                : (isEnabled ? unselectedEnabledBg : unselectedDisabledBg);

            Color hoverBgColor = isSelected
                ? (isEnabled ? accentHoverColor : Color.FromRgb(
                    (byte)Math.Min(255, accentMutedColor.R + 14),
                    (byte)Math.Min(255, accentMutedColor.G + 14),
                    (byte)Math.Min(255, accentMutedColor.B + 14)))
                : unselectedHoverBg;

            Color borderColor = isSelected
                ? (isEnabled ? Color.FromRgb(0x60, 0xCD, 0xFF) : accentColor)
                : unselectedBorderColor;

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
                        Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF))
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
                ScheduleRenderMonitors();
            };

            innerGrid.Children.Add(togglePill);

            // Center: Large Windows 11-style display number
            var numberText = new TextBlock
            {
                Text = (monitorIndex + 1).ToString(),
                FontSize = 23,
                FontWeight = FontWeights.SemiBold,
                Foreground = isSelected
                    ? selectedTextBrush
                    : (isEnabled ? unselectedPrimaryText : unselectedSecondaryText),
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
                Foreground = isSelected
                    ? new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF))
                    : unselectedSecondaryText,
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

    private void StyleFeatureButton(Button button, string label, bool isOn, bool isCurrentPageFeature)
    {
        button.Content = $"{(isOn ? "●" : "○")} {label}: {(isOn ? "On" : "Off")}";
        if (isOn)
        {
            if (isCurrentPageFeature)
            {
                button.Background = GetThemeBrush("AccentFillColorDefaultBrush", new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)));
                button.BorderBrush = GetThemeBrush("AccentFillColorSecondaryBrush", new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)));
                button.Foreground = GetThemeBrush("TextOnAccentFillColorPrimaryBrush", Brushes.White);
            }
            else
            {
                button.Background = GetThemeBrush("ControlFillColorDefaultBrush", new SolidColorBrush(Color.FromRgb(0x2D, 0x4A, 0x3E)));
                button.BorderBrush = GetThemeBrush("SystemFillColorSuccessBrush", new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));
                button.Foreground = GetThemeBrush("TextFillColorPrimaryBrush", Brushes.White);
            }
        }
        else
        {
            button.Background = GetThemeBrush("ControlFillColorDefaultBrush", new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)));
            button.BorderBrush = GetThemeBrush("ControlStrokeColorDefaultBrush", new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)));
            button.Foreground = GetThemeBrush("TextFillColorPrimaryBrush", Brushes.White);
        }
    }

    private int GetClampedSelectedMonitorIndex()
    {
        var monitors = MonitorUtil.GetMonitors();
        if (monitors.Count == 0)
            return -1;

        int idx = Math.Clamp(SettingsManager.Current.TaskbarWidgetSelectedMonitor, 0, monitors.Count - 1);
        if (idx != SettingsManager.Current.TaskbarWidgetSelectedMonitor)
            SettingsManager.Current.TaskbarWidgetSelectedMonitor = idx;
        return idx;
    }

    private void ToggleWidgetButton_Click(object sender, RoutedEventArgs e)
    {
        int idx = GetClampedSelectedMonitorIndex();
        if (idx < 0)
            return;

        var profile = SettingsManager.Current.GetMonitorSettings(idx);
        SettingsManager.Current.SetMonitorFeatureEnabled(idx, "Widget", !profile.TaskbarWidgetEnabled);
        ScheduleRenderMonitors();
    }

    private void ToggleVisualizerButton_Click(object sender, RoutedEventArgs e)
    {
        int idx = GetClampedSelectedMonitorIndex();
        if (idx < 0)
            return;

        var profile = SettingsManager.Current.GetMonitorSettings(idx);
        bool currentlyOn = profile.TaskbarWidgetEnabled && profile.TaskbarVisualizerEnabled;
        SettingsManager.Current.SetMonitorFeatureEnabled(idx, "Visualizer", !currentlyOn);
        ScheduleRenderMonitors();
    }

    private void ToggleLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        int idx = GetClampedSelectedMonitorIndex();
        if (idx < 0)
            return;

        var profile = SettingsManager.Current.GetMonitorSettings(idx);
        bool currentlyOn = profile.TaskbarWidgetEnabled && profile.LyricsEnabled;
        SettingsManager.Current.SetMonitorFeatureEnabled(idx, "Lyrics", !currentlyOn);
        ScheduleRenderMonitors();
    }

    private void CopyToAllButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.CopyCurrentMonitorSettingsToAll(MonitorUtil.GetMonitors().Count);
        ScheduleRenderMonitors();
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
