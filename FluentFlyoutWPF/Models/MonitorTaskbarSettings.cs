// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.ViewModels;
using System.Windows;
using System.Xml.Serialization;

namespace FluentFlyoutWPF.Models;

/// <summary>
/// Per-monitor configuration for the taskbar media widget, visualizer, and lyrics strip.
/// </summary>
public partial class MonitorTaskbarSettings : ObservableObject
{
    [ObservableProperty]
    public partial int MonitorIndex { get; set; }

    [ObservableProperty]
    public partial bool TaskbarWidgetEnabled { get; set; }

    [ObservableProperty]
    public partial int TaskbarWidgetPosition { get; set; }

    [ObservableProperty]
    public partial bool TaskbarWidgetPadding { get; set; } = true;

    [ObservableProperty]
    public partial int TaskbarWidgetManualPadding { get; set; }

    [ObservableProperty]
    public partial bool TaskbarWidgetBackgroundBlur { get; set; }

    [ObservableProperty]
    public partial bool TaskbarWidgetHideCompletely { get; set; }

    [ObservableProperty]
    public partial bool TaskbarWidgetFixedWidth { get; set; }

    [ObservableProperty]
    public partial int TaskbarWidgetMaxWidth { get; set; } = 240;

    [ObservableProperty]
    public partial bool TaskbarWidgetAutoHide { get; set; }

    [ObservableProperty]
    public partial bool TaskbarWidgetControlsEnabled { get; set; }

    [ObservableProperty]
    public partial int TaskbarWidgetControlsPosition { get; set; } = 1;

    [ObservableProperty]
    public partial bool TaskbarWidgetShuffleRepeatEnabled { get; set; }

    [ObservableProperty]
    public partial bool TaskbarWidgetTimeEnabled { get; set; }

    [ObservableProperty]
    public partial bool TaskbarWidgetAnimated { get; set; } = true;

    [ObservableProperty]
    public partial bool TaskbarWidgetScrollingEnabled { get; set; }

    [ObservableProperty]
    public partial bool TaskbarWidgetScrollingTextLoopForever { get; set; }

    [ObservableProperty]
    public partial int TaskbarWidgetScrollingTextSpeed { get; set; } = 20;

    [ObservableProperty]
    public partial int TaskbarWidgetScrollVolumeMode { get; set; } = 1;

    [ObservableProperty]
    public partial bool TaskbarWidgetShowPauseOverlay { get; set; } = true;

    [ObservableProperty]
    public partial bool TaskbarWidgetProgressBarEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool TaskbarVisualizerEnabled { get; set; }

    [ObservableProperty]
    public partial int TaskbarVisualizerPosition { get; set; } = 1;

    [ObservableProperty]
    public partial int TaskbarVisualizerBarCount { get; set; } = 10;

    [ObservableProperty]
    public partial int TaskbarVisualizerLength { get; set; } = 84;

    [ObservableProperty]
    public partial int TaskbarVisualizerStyle { get; set; }

    [ObservableProperty]
    public partial bool TaskbarVisualizerBeatGlow { get; set; } = true;

    [ObservableProperty]
    public partial bool TaskbarVisualizerCenteredBars { get; set; }

    [ObservableProperty]
    public partial bool TaskbarVisualizerBaseline { get; set; }

    [ObservableProperty]
    public partial bool TaskbarVisualizerBaselineAutoHide { get; set; }

    [ObservableProperty]
    public partial bool TaskbarVisualizerClickable { get; set; } = true;

    [XmlIgnore]
    [ObservableProperty]
    public partial bool TaskbarVisualizerHasContent { get; set; }

    [ObservableProperty]
    public partial bool LyricsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int LyricsWidth { get; set; } = 200;

    [ObservableProperty]
    public partial int LyricsHidePausedAfterSeconds { get; set; } = 15;

    [XmlIgnore]
    public string FontFamily => SettingsManager.Current.FontFamily;

    [XmlIgnore]
    public FlowDirection FlowDirection => SettingsManager.Current.FlowDirection;

    public void NotifyGlobalAppearanceChanged()
    {
        OnPropertyChanged(nameof(FontFamily));
        OnPropertyChanged(nameof(FlowDirection));
    }

    public void CopyFromUserSettings(UserSettings s)
    {
        TaskbarWidgetEnabled = s.TaskbarWidgetEnabled;
        TaskbarWidgetPosition = s.TaskbarWidgetPosition;
        TaskbarWidgetPadding = s.TaskbarWidgetPadding;
        TaskbarWidgetManualPadding = s.TaskbarWidgetManualPadding;
        TaskbarWidgetBackgroundBlur = s.TaskbarWidgetBackgroundBlur;
        TaskbarWidgetHideCompletely = s.TaskbarWidgetHideCompletely;
        TaskbarWidgetFixedWidth = s.TaskbarWidgetFixedWidth;
        TaskbarWidgetMaxWidth = s.TaskbarWidgetMaxWidth <= 0 ? 240 : s.TaskbarWidgetMaxWidth;
        TaskbarWidgetAutoHide = s.TaskbarWidgetAutoHide;
        TaskbarWidgetControlsEnabled = s.TaskbarWidgetControlsEnabled;
        TaskbarWidgetControlsPosition = s.TaskbarWidgetControlsPosition;
        TaskbarWidgetShuffleRepeatEnabled = s.TaskbarWidgetShuffleRepeatEnabled;
        TaskbarWidgetTimeEnabled = s.TaskbarWidgetTimeEnabled;
        TaskbarWidgetAnimated = s.TaskbarWidgetAnimated;
        TaskbarWidgetScrollingEnabled = s.TaskbarWidgetScrollingEnabled;
        TaskbarWidgetScrollingTextLoopForever = s.TaskbarWidgetScrollingTextLoopForever;
        TaskbarWidgetScrollingTextSpeed = s.TaskbarWidgetScrollingTextSpeed <= 0 ? 20 : s.TaskbarWidgetScrollingTextSpeed;
        TaskbarWidgetScrollVolumeMode = s.TaskbarWidgetScrollVolumeMode;
        TaskbarWidgetShowPauseOverlay = s.TaskbarWidgetShowPauseOverlay;
        TaskbarWidgetProgressBarEnabled = s.TaskbarWidgetProgressBarEnabled;

        TaskbarVisualizerEnabled = s.TaskbarVisualizerEnabled;
        TaskbarVisualizerPosition = s.TaskbarVisualizerPosition;
        TaskbarVisualizerBarCount = s.TaskbarVisualizerBarCount <= 0 ? 10 : s.TaskbarVisualizerBarCount;
        TaskbarVisualizerLength = s.TaskbarVisualizerLength <= 0 ? 84 : s.TaskbarVisualizerLength;
        TaskbarVisualizerStyle = s.TaskbarVisualizerStyle;
        TaskbarVisualizerBeatGlow = s.TaskbarVisualizerBeatGlow;
        TaskbarVisualizerCenteredBars = s.TaskbarVisualizerCenteredBars;
        TaskbarVisualizerBaseline = s.TaskbarVisualizerBaseline;
        TaskbarVisualizerBaselineAutoHide = s.TaskbarVisualizerBaselineAutoHide;
        TaskbarVisualizerClickable = s.TaskbarVisualizerClickable;

        LyricsEnabled = s.LyricsEnabled;
        LyricsWidth = s.LyricsWidth <= 0 ? 200 : s.LyricsWidth;
        LyricsHidePausedAfterSeconds = s.LyricsHidePausedAfterSeconds;
    }

    public void ApplyToUserSettings(UserSettings s)
    {
        s.TaskbarWidgetEnabled = TaskbarWidgetEnabled;
        s.TaskbarWidgetPosition = TaskbarWidgetPosition;
        s.TaskbarWidgetPadding = TaskbarWidgetPadding;
        s.TaskbarWidgetManualPadding = TaskbarWidgetManualPadding;
        s.TaskbarWidgetBackgroundBlur = TaskbarWidgetBackgroundBlur;
        s.TaskbarWidgetHideCompletely = TaskbarWidgetHideCompletely;
        s.TaskbarWidgetFixedWidth = TaskbarWidgetFixedWidth;
        s.TaskbarWidgetMaxWidth = TaskbarWidgetMaxWidth <= 0 ? 240 : TaskbarWidgetMaxWidth;
        s.TaskbarWidgetAutoHide = TaskbarWidgetAutoHide;
        s.TaskbarWidgetControlsEnabled = TaskbarWidgetControlsEnabled;
        s.TaskbarWidgetControlsPosition = TaskbarWidgetControlsPosition;
        s.TaskbarWidgetShuffleRepeatEnabled = TaskbarWidgetShuffleRepeatEnabled;
        s.TaskbarWidgetTimeEnabled = TaskbarWidgetTimeEnabled;
        s.TaskbarWidgetAnimated = TaskbarWidgetAnimated;
        s.TaskbarWidgetScrollingEnabled = TaskbarWidgetScrollingEnabled;
        s.TaskbarWidgetScrollingTextLoopForever = TaskbarWidgetScrollingTextLoopForever;
        s.TaskbarWidgetScrollingTextSpeed = TaskbarWidgetScrollingTextSpeed <= 0 ? 20 : TaskbarWidgetScrollingTextSpeed;
        s.TaskbarWidgetScrollVolumeMode = TaskbarWidgetScrollVolumeMode;
        s.TaskbarWidgetShowPauseOverlay = TaskbarWidgetShowPauseOverlay;
        s.TaskbarWidgetProgressBarEnabled = TaskbarWidgetProgressBarEnabled;

        s.TaskbarVisualizerEnabled = TaskbarVisualizerEnabled;
        s.TaskbarVisualizerPosition = TaskbarVisualizerPosition;
        s.TaskbarVisualizerBarCount = TaskbarVisualizerBarCount <= 0 ? 10 : TaskbarVisualizerBarCount;
        s.TaskbarVisualizerLength = TaskbarVisualizerLength <= 0 ? 84 : TaskbarVisualizerLength;
        s.TaskbarVisualizerStyle = TaskbarVisualizerStyle;
        s.TaskbarVisualizerBeatGlow = TaskbarVisualizerBeatGlow;
        s.TaskbarVisualizerCenteredBars = TaskbarVisualizerCenteredBars;
        s.TaskbarVisualizerBaseline = TaskbarVisualizerBaseline;
        s.TaskbarVisualizerBaselineAutoHide = TaskbarVisualizerBaselineAutoHide;
        s.TaskbarVisualizerClickable = TaskbarVisualizerClickable;

        s.LyricsEnabled = LyricsEnabled;
        s.LyricsWidth = LyricsWidth <= 0 ? 200 : LyricsWidth;
        s.LyricsHidePausedAfterSeconds = LyricsHidePausedAfterSeconds;
    }

    public void CopyFrom(MonitorTaskbarSettings other)
    {
        TaskbarWidgetEnabled = other.TaskbarWidgetEnabled;
        TaskbarWidgetPosition = other.TaskbarWidgetPosition;
        TaskbarWidgetPadding = other.TaskbarWidgetPadding;
        TaskbarWidgetManualPadding = other.TaskbarWidgetManualPadding;
        TaskbarWidgetBackgroundBlur = other.TaskbarWidgetBackgroundBlur;
        TaskbarWidgetHideCompletely = other.TaskbarWidgetHideCompletely;
        TaskbarWidgetFixedWidth = other.TaskbarWidgetFixedWidth;
        TaskbarWidgetMaxWidth = other.TaskbarWidgetMaxWidth;
        TaskbarWidgetAutoHide = other.TaskbarWidgetAutoHide;
        TaskbarWidgetControlsEnabled = other.TaskbarWidgetControlsEnabled;
        TaskbarWidgetControlsPosition = other.TaskbarWidgetControlsPosition;
        TaskbarWidgetShuffleRepeatEnabled = other.TaskbarWidgetShuffleRepeatEnabled;
        TaskbarWidgetTimeEnabled = other.TaskbarWidgetTimeEnabled;
        TaskbarWidgetAnimated = other.TaskbarWidgetAnimated;
        TaskbarWidgetScrollingEnabled = other.TaskbarWidgetScrollingEnabled;
        TaskbarWidgetScrollingTextLoopForever = other.TaskbarWidgetScrollingTextLoopForever;
        TaskbarWidgetScrollingTextSpeed = other.TaskbarWidgetScrollingTextSpeed;
        TaskbarWidgetScrollVolumeMode = other.TaskbarWidgetScrollVolumeMode;
        TaskbarWidgetShowPauseOverlay = other.TaskbarWidgetShowPauseOverlay;
        TaskbarWidgetProgressBarEnabled = other.TaskbarWidgetProgressBarEnabled;

        TaskbarVisualizerEnabled = other.TaskbarVisualizerEnabled;
        TaskbarVisualizerPosition = other.TaskbarVisualizerPosition;
        TaskbarVisualizerBarCount = other.TaskbarVisualizerBarCount;
        TaskbarVisualizerLength = other.TaskbarVisualizerLength;
        TaskbarVisualizerStyle = other.TaskbarVisualizerStyle;
        TaskbarVisualizerBeatGlow = other.TaskbarVisualizerBeatGlow;
        TaskbarVisualizerCenteredBars = other.TaskbarVisualizerCenteredBars;
        TaskbarVisualizerBaseline = other.TaskbarVisualizerBaseline;
        TaskbarVisualizerBaselineAutoHide = other.TaskbarVisualizerBaselineAutoHide;
        TaskbarVisualizerClickable = other.TaskbarVisualizerClickable;

        LyricsEnabled = other.LyricsEnabled;
        LyricsWidth = other.LyricsWidth;
        LyricsHidePausedAfterSeconds = other.LyricsHidePausedAfterSeconds;
    }
}
