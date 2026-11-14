// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Controls;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using FluentFlyoutWPF.Windows;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Xml.Serialization;

namespace FluentFlyoutWPF.ViewModels;

/**
 * User Settings data model.
 */
public partial class UserSettings : ObservableObject
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    // List of non-XmlIgnore property names
    private static readonly HashSet<string> PersistedPropertyNames =
    [
        .. typeof(UserSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanWrite && property.GetCustomAttribute<XmlIgnoreAttribute>() is null)
            .Select(property => property.Name)
    ];

    /// <summary>
    /// Saved width of the settings window
    /// </summary>
    [ObservableProperty]
    public partial double SettingsWindowWidth { get; set; }

    /// <summary>
    /// Saved height of the settings window
    /// </summary>
    [ObservableProperty]
    public partial double SettingsWindowHeight { get; set; }

    /// <summary>
    /// Saved horizontal position of the settings window
    /// </summary>
    [ObservableProperty]
    public partial double SettingsWindowLeft { get; set; }

    /// <summary>
    /// Saved vertical position of the settings window
    /// </summary>
    [ObservableProperty]
    public partial double SettingsWindowTop { get; set; }

    /// <summary>
    /// Saved window state of the settings window
    /// </summary>
    [ObservableProperty]
    public partial WindowState SettingsWindowState { get; set; }

    /// <summary>
    /// Use a compact layout
    /// </summary>
    [ObservableProperty]
    public partial bool CompactLayout { get; set; }

    /// <summary>
    /// Flyout Target Display
    /// </summary>
    [ObservableProperty]
    public partial int FlyoutSelectedMonitor { get; set; }

    /// <summary>
    /// Flyout position on screen
    /// </summary>
    [ObservableProperty]
    public partial int Position { get; set; }

    /// <summary>
    /// Scale for flyout animation speed
    /// </summary>
    [ObservableProperty]
    public partial int FlyoutAnimationSpeed { get; set; }

    /// <summary>
    /// Show player information in the flyout
    /// </summary>
    [ObservableProperty]
    public partial bool PlayerInfoEnabled { get; set; }

    /// <summary>
    /// Enable repeat button
    /// </summary>
    [ObservableProperty]
    public partial bool RepeatEnabled { get; set; }

    /// <summary>
    /// Enable shuffle button
    /// </summary>
    [ObservableProperty]
    public partial bool ShuffleEnabled { get; set; }

    /// <summary>
    /// Start minimized to tray when Windows starts
    /// </summary>
    [ObservableProperty]
    public partial bool Startup { get; set; }

    /// <summary>
    /// MediaFlyout Always Display
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDurationEditable))]
    public partial bool MediaFlyoutAlwaysDisplay { get; set; }

    [XmlIgnore] public bool IsDurationEditable => !MediaFlyoutAlwaysDisplay;

    /// <summary>
    /// Flyout display duration (milliseconds)
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    public partial int Duration { get; set; }

    [XmlIgnore]
    public string DurationText
    {
        get => Duration.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                Duration = result switch
                {
                    > 10000 => 10000,
                    < 0 => 0,
                    _ => result
                };
            }
            else
            {
                Duration = 3000;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Enable the 'Next Up' flyout (experimental)
    /// </summary>
    [ObservableProperty]
    public partial bool NextUpEnabled { get; set; }

    /// <summary>
    /// 'Next Up' flyout display duration (milliseconds)
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextUpDurationText))]
    public partial int NextUpDuration { get; set; }

    [XmlIgnore]
    public string NextUpDurationText
    {
        get => NextUpDuration.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                NextUpDuration = result switch
                {
                    > 10000 => 10000,
                    < 0 => 0,
                    _ => result
                };
            }
            else
            {
                NextUpDuration = 2000;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Tray icon left-click behavior
    /// </summary>
    [ObservableProperty]
    [XmlElement(ElementName = "nIconLeftClick")]
    public partial int NIconLeftClick { get; set; }

    /// <summary>
    /// Center the title and artist text
    /// </summary>
    [ObservableProperty]
    public partial bool CenterTitleArtist { get; set; }

    /// <summary>
    /// Animation easing style index
    /// </summary>
    [ObservableProperty]
    public partial int FlyoutAnimationEasingStyle { get; set; }

    /// <summary>
    /// Enable lock keys flyout (shows Caps/Num/Scroll status)
    /// </summary>
    [ObservableProperty]
    public partial bool LockKeysEnabled { get; set; }

    [ObservableProperty]
    public partial bool LockKeysCapsEnabled { get; set; }

    [ObservableProperty]
    public partial bool LockKeysNumEnabled { get; set; }

    [ObservableProperty]
    public partial bool LockKeysScrollEnabled { get; set; }

    /// <summary>
    /// Lock keys flyout display duration (milliseconds)
    /// </summary>

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockKeysDurationText))]
    public partial int LockKeysDuration { get; set; }

    [XmlIgnore]
    public string LockKeysDurationText
    {
        get => LockKeysDuration.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                LockKeysDuration = result switch
                {
                    > 10000 => 10000,
                    < 0 => 0,
                    _ => result
                };
            }
            else
            {
                LockKeysDuration = 2000;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// App theme. 0 for default, 1 for light, 2 for dark.
    /// </summary>
    [ObservableProperty]
    public partial int AppTheme { get; set; }

    /// <summary>
    /// Enable media flyout
    /// </summary>
    [ObservableProperty]
    public partial bool MediaFlyoutEnabled { get; set; }

    /// <summary>
    /// Exclude volume keys from triggering media flyout
    /// </summary>
    [ObservableProperty]
    public partial bool MediaFlyoutVolumeKeysExcluded { get; set; }

    /// <summary>
    /// Use symbol-style tray icon
    /// </summary>
    [ObservableProperty]
    [XmlElement(ElementName = "nIconSymbol")]
    public partial bool NIconSymbol { get; set; }

    /// <summary>
    /// Hide tray icon completely
    /// </summary>
    [ObservableProperty]
    public partial bool NIconHide { get; set; }

    /// <summary>
    /// Disable flyout when a DirectX exclusive fullscreen app is detected
    /// </summary>
    [ObservableProperty]
    public partial bool DisableIfFullscreen { get; set; }

    /// <summary>
    /// Use bold symbol and font in the lock keys flyout
    /// </summary>
    [ObservableProperty]
    [XmlElement(ElementName = "LockKeysBoldUI")]
    public partial bool LockKeysBoldUi { get; set; }

    /// Selects which monitor to use for the lock keys flyout when multiple monitors are in use.
    /// 0 = Default behavior, 1 = Monitor containing the focused window, 2 = Monitor containing the cursor.
    [ObservableProperty]
    public partial int LockKeysMonitorPreference { get; set; }

    /// <summary>
    /// Determines if the user has updated to a new version
    /// </summary>
    [ObservableProperty]
    public partial string LastKnownVersion { get; set; }

    /// <summary>
    /// Show seekbar if the player supports it
    /// </summary>
    [ObservableProperty]
    public partial bool SeekbarEnabled { get; set; }

    /// <summary>
    /// Pause other media sessions when focusing a new one
    /// </summary>
    [ObservableProperty]
    public partial bool PauseOtherSessionsEnabled { get; set; }

    [ObservableProperty]
    public partial string PinnedSessionId { get; set; }

    /// <summary>
    /// Enable subtle animations for the lock keys flyout indicator
    /// </summary>
    [ObservableProperty]
    public partial bool LockKeysAnimated { get; set; }

    /// <summary>
    /// Receive music streamed from the Phone Music Listener Android app and publish it as a system media session
    /// </summary>
    [ObservableProperty]
    public partial bool PhoneReceiverEnabled { get; set; }

    /// <summary>
    /// TCP port the phone sender connects to (1024..65535)
    /// </summary>
    [ObservableProperty]
    public partial int PhoneReceiverPort { get; set; }

    /// <summary>
    /// Answer the phone's discovery broadcast (UDP 58711)
    /// </summary>
    [ObservableProperty]
    public partial bool PhoneReceiverDiscovery { get; set; }

    /// <summary>
    /// Mix phone music + microphone into VB-Cable for call apps
    /// </summary>
    [ObservableProperty]
    public partial bool PhoneReceiverVbCableMix { get; set; }

    /// <summary>
    /// Show shuffle and repeat buttons in the media flyout (order: shuffle, back, play, next, repeat)
    /// </summary>
    [ObservableProperty]
    public partial bool MediaFlyoutShuffleRepeatEnabled { get; set; }

    /// <summary>
    /// Show shuffle and repeat buttons on the taskbar widget
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetShuffleRepeatEnabled { get; set; }

    /// <summary>
    /// Show the playback time (2:10 / 3:42) on the taskbar widget
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetTimeEnabled { get; set; }

    /// <summary>
    /// Control YouTube Music's shuffle and repeat through its API Server plugin
    /// </summary>
    [ObservableProperty]
    public partial bool YtmBridgeEnabled { get; set; }

    /// <summary>
    /// Port of the YouTube Music API Server plugin (default 26538)
    /// </summary>
    [ObservableProperty]
    public partial int YtmBridgePort { get; set; }

    /// <summary>
    /// Stable client id used for the YT Music API Server auth dialog
    /// </summary>
    [ObservableProperty]
    public partial string YtmBridgeClientId { get; set; } = "";

    /// <summary>
    /// Persisted access token from the YT Music API Server
    /// </summary>
    [ObservableProperty]
    public partial string YtmBridgeToken { get; set; } = "";

    /// <summary>
    /// Show LockKeys flyout when the Insert key is pressed
    /// </summary>
    [ObservableProperty]
    public partial bool LockKeysInsertEnabled { get; set; }

    /// <summary>
    /// Preset for media flyout background blur styles
    /// </summary>
    [ObservableProperty]
    public partial int MediaFlyoutBackgroundBlur { get; set; }

    /// <summary>
    /// Enable acrylic blur effect on the flyout window
    /// </summary>
    [ObservableProperty]
    public partial bool MediaFlyoutAcrylicWindowEnabled { get; set; }

    /// <summary>
    /// Enable acrylic blur effect on the Next Up window
    /// </summary>
    [ObservableProperty]
    public partial bool NextUpAcrylicWindowEnabled { get; set; }

    /// <summary>
    /// Enable acrylic blur effect on the Lock Keys window
    /// </summary>
    [ObservableProperty]
    public partial bool LockKeysAcrylicWindowEnabled { get; set; }

    [ObservableProperty]
    public partial bool VolumeMixerAcrylicWindowEnabled { get; set; }

    /// <summary>
    /// User's preferred app language (e.g., "system" for system default)
    /// </summary>
    [ObservableProperty]
    public partial string AppLanguage { get; set; }

    /// <summary>
    /// Language Options
    /// </summary>
    [XmlIgnore]
    public ObservableCollection<LanguageOption> LanguageOptions { get; } = [];

    [XmlIgnore]
    [ObservableProperty]
    public partial LanguageOption SelectedLanguage { get; set; }

    [XmlIgnore]
    [ObservableProperty]
    public partial FlowDirection FlowDirection { get; set; }

    [XmlIgnore]
    [ObservableProperty]
    public partial string FontFamily { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget is enabled
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetEnabled { get; set; }

    /// <summary>
    /// Widget Target Display
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetSelectedMonitor { get; set; }

    /// <summary>
    /// Autohide Widget after a few milliseconds after pause 
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetAutoHide { get; set; }

    /// <summary>
    /// Gets or sets the position of the taskbar widget, represented as an integer value.
    /// 0: Left, 1: Center, 2: Right
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetPosition { get; set; }

    /// <summary>
    /// Determines whether padding should be applied to the taskbar widget for the native Windows Widgets button
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetPadding { get; set; }

    /// <summary>
    /// Manual padding value in pixels applied to the taskbar widget
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetManualPaddingText))]
    public partial int TaskbarWidgetManualPadding { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetManualPaddingText
    {
        get => TaskbarWidgetManualPadding.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                TaskbarWidgetManualPadding = result switch
                {
                    > 9999 => 9999,
                    < -9999 => -9999,
                    _ => result
                };
            }
            else
            {
                TaskbarWidgetManualPadding = 0;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets a value indication whether the taskbar widget background should have a blur effect
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetBackgroundBlur { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget should be completely hidden from view when no media is playing.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetHideCompletely { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget should always be sized at its
    /// maximum width, so right-aligned controls don't shift when the song changes.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetFixedWidth { get; set; }

    /// <summary>
    /// Maximum logical width of the taskbar widget (cover + song info section) in pixels.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetMaxWidth { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the pause icon overlay should be completely hidden from view.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetShowPauseOverlay { get; set; }

    /// <summary>
    /// Whether taskbar widget controls (pause, previous, next) are enabled.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetControlsEnabled { get; set; }

    /// <summary>
    /// Position of the taskbar widget controls. 0: Left, 1: Right
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetControlsPosition { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget should play animations.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetAnimated { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget scrolling text (marquee) is enabled for long titles.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetScrollingEnabled { get; set; }

    /// <summary>
    /// Controls whether scrolling over the taskbar widget adjusts master or player volume.
    /// 0: Off, 1: Master volume, 2: Player volume
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarWidgetScrollVolumeMode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar widget scrolling text should loop forever.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetScrollingTextLoopForever { get; set; }

    /// <summary>
    /// Gets or sets the speed of the taskbar widget scrolling text.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarWidgetScrollingTextSpeedText))]
    public partial int TaskbarWidgetScrollingTextSpeed { get; set; }

    [XmlIgnore]
    public string TaskbarWidgetScrollingTextSpeedText
    {
        get => TaskbarWidgetScrollingTextSpeed.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                TaskbarWidgetScrollingTextSpeed = result switch
                {
                    > 100 => 100,
                    < 1 => 1,
                    _ => result
                };
            }
            else
            {
                TaskbarWidgetScrollingTextSpeed = 20;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the taskbar visualizer is enabled.
    /// </summary>
    /// <remarks>For now, this requires Premium and Taskbar Widget to be enabled.</remarks>
    [ObservableProperty]
    public partial bool TaskbarVisualizerEnabled { get; set; }

    /// <summary>
    /// Returns whether app filtering is enabled or disabled.
    /// </summary>
    [ObservableProperty]
    public partial bool AppFilteringEnabled { get; set; }

    /// <summary>
    /// Returns the active filtering mode. 0 for Blacklist, 1 for Whitelist.
    /// </summary>
    [ObservableProperty]
    public partial int AppFilteringMode { get; set; }

    /// <summary>
    /// Returns a list of apps that are allowed to display media/update the taskbar.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<string> AllowedApps { get; set; }

    /// <summary>
    /// Returns a list of apps that are NOT allowed to display media/update the taskbar.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<string> BlockedApps { get; set; }

    /// <summary>
    /// Position of the visualizer, where 0 and 1 are to the left or right of the widget.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarVisualizerPosition { get; set; }

    /// <summary>
    /// Whether the visualizer is clickable to open the visualizer settings page.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarVisualizerClickable { get; set; }

    /// <summary>
    /// Indicates whether the visualizer has content to display, and is not persisted since it's only relevant at runtime.
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial bool TaskbarVisualizerHasContent { get; set; }

    /// <summary>
    /// The number of visualizer bars to display.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarVisualizerBarCount { get; set; }

    /// <summary>
    /// Show a thin playback progress bar at the bottom of the taskbar widget.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarWidgetProgressBarEnabled { get; set; }

    /// <summary>
    /// Make the taskbar visualizer glow with the beat of the playing audio.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarVisualizerBeatGlow { get; set; }

    /// <summary>
    /// Visualizer drawing style: 0 = bars, 1 = bars with peak caps, 2 = wave.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarVisualizerStyle { get; set; }

    /// <summary>
    /// Friendly app name whose audio only the taskbar visualizer shows
    /// (WASAPI process loopback). Empty = whole system.
    /// </summary>
    [ObservableProperty]
    public partial string TaskbarVisualizerTargetApp { get; set; }

    /// <summary>
    /// Friendly name of the app whose media session wins over the focused session
    /// (e.g. "YouTube Music"). Empty = follow the focused session.
    /// </summary>
    [ObservableProperty]
    public partial string MediaSessionPriorityApp { get; set; }

    /// <summary>
    /// The length (width) of the taskbar visualizer in pixels, from 40 to 300. Default is 84.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarVisualizerLength { get; set; }

    /// <summary>
    /// Whether the taskbar lyrics widget is enabled. It only shows when synced/plain lyrics
    /// are found for the current track.
    /// </summary>
    [ObservableProperty]
    public partial bool LyricsEnabled { get; set; }

    /// <summary>
    /// Main lyrics provider: 0 = LRCLIB, 1 = Musixmatch.
    /// </summary>
    [ObservableProperty]
    public partial int LyricsProvider { get; set; }

    /// <summary>
    /// Width of the taskbar lyrics widget in pixels, from 60 to 400. Default is 200.
    /// </summary>
    [ObservableProperty]
    public partial int LyricsWidth { get; set; }

    /// <summary>
    /// Lyrics sync offset in milliseconds, from -5000 to 5000.
    /// </summary>
    [ObservableProperty]
    public partial int LyricsOffsetMs { get; set; }

    /// <summary>
    /// Seconds after which the lyrics widget fades out while playback is paused.
    /// 0 disables auto-hide.
    /// </summary>
    [ObservableProperty]
    public partial int LyricsHidePausedAfterSeconds { get; set; }


    /// <summary>
    /// Cookie session imported from the YouTube Music desktop app, for the
    /// YouTube Music lyrics provider. Empty = anonymous requests.
    /// </summary>
    [ObservableProperty]
    public partial string LyricsYtmSession { get; set; }

    /// <summary>
    /// Number of days after which a disabled-lyrics song automatically expires and is re-enabled.
    /// 0 disables automatic expiration. Default is 7.
    /// </summary>
    [ObservableProperty]
    public partial int DisabledLyricsExpireDays { get; set; }

    /// <summary>
    /// Whether the visualizer should be symmetrical/mirrored.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarVisualizerCenteredBars { get; set; }

    /// <summary>
    /// Gets or sets whether a bar baseline is shown.
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarVisualizerBaseline { get; set; }

    /// <summary>
    /// Gets or sets the audio sensitivity for the taskbar visualizer from 1 to 3, where 2 is the default.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarVisualizerAudioSensitivity { get; set; }

    /// <summary>
    /// Autohide taskbar. Only does something if TaskbarVisualizerBaseline is enabled (doesn't autohide by default).
    /// </summary>
    [ObservableProperty]
    public partial bool TaskbarVisualizerBaselineAutoHide { get; set; }

    [ObservableProperty]
    public partial bool VolumeControlEnabled { get; set; }

    [ObservableProperty]
    public partial bool VolumeFlyoutShortcutEnabled { get; set; }

    [ObservableProperty]
    public partial bool VolumeControlAboveMediaFlyout { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeControlDurationText))]
    public partial int VolumeControlDuration { get; set; }

    [XmlIgnore]
    public string VolumeControlDurationText
    {
        get => VolumeControlDuration.ToString();
        set
        {
            if (int.TryParse(value, out var result))
            {
                VolumeControlDuration = result switch
                {
                    > 10000 => 10000,
                    < 0 => 0,
                    _ => result
                };
            }
            else
            {
                VolumeControlDuration = 3000;
            }

            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    public partial bool VolumeMixerEnabled { get; set; }

    [ObservableProperty]
    public partial bool VolumeMixerHighlightActiveApps { get; set; }

    /// <summary>
    /// The audio peak level for the taskbar visualizer from 1 to 3.
    /// This is used to calibrate the visualizer bar height to the audio output.
    /// </summary>
    [ObservableProperty]
    public partial int TaskbarVisualizerAudioPeakLevel { get; set; }

    /// <summary>
    /// Gets whether premium features are unlocked (runtime only, not persisted)
    /// </summary>
    [XmlIgnore]
    [ObservableProperty]
    public partial bool IsPremiumUnlocked { get; set; }

    /// <summary>
    /// Gets or sets the opacity level of the acrylic blur effect.
    /// </summary>
    [ObservableProperty]
    public partial uint AcrylicBlurOpacity { get; set; }

    [ObservableProperty]
    public partial bool UseAlbumArtAsAccentColor { get; set; }

    /// <summary>
    /// Gets whether this is a Store version. Once false, always false (only if last known version was not null).
    /// </summary>
    [ObservableProperty]
    public partial bool IsStoreVersion { get; set; }

    [XmlIgnore]
    [ObservableProperty]
    public partial string PremiumPrice { get; set; }

    [XmlIgnore]
    [ObservableProperty]
    public partial string PremiumPurchaseAction { get; set; }

    /// <summary>
    /// Last time the program has sent an update notification in Unix seconds.
    /// </summary>
    [ObservableProperty]
    public partial long LastUpdateNotificationUnixSeconds { get; set; }

    /// <summary>
    /// Determines whether user will get Windows notifications when a new update is available.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowUpdateNotifications { get; set; }

    /// <summary>
    /// Determines whether to use the legacy method for calculating taskbar width for widget positioning for compatibility with other taskbar mods
    /// </summary>
    [ObservableProperty]
    public partial bool LegacyTaskbarWidthEnabled { get; set; }

    [ObservableProperty]
    public partial Guid Uuid { get; set; }

    [XmlIgnore]
    [ObservableProperty]
    public partial Guid SessionId { get; set; } = Guid.NewGuid();

    [ObservableProperty]
    public partial bool AnonymousTelemetryAllowed { get; set; }

    /// <summary>
    /// Per-monitor taskbar widget, visualizer, and lyrics profiles.
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<MonitorTaskbarSettings> MonitorProfiles { get; set; } = [];

    [XmlIgnore]
    private bool _initializing = true;

    [XmlIgnore]
    private bool _switchingMonitorProfile;

    [XmlIgnore]
    public bool IsAnyMonitorWidgetEnabled =>
        MonitorProfiles.Count > 0 ? MonitorProfiles.Any(m => m.TaskbarWidgetEnabled) : TaskbarWidgetEnabled;

    [XmlIgnore]
    public bool IsAnyMonitorLyricsEnabled =>
        MonitorProfiles.Count > 0 ? MonitorProfiles.Any(m => m.TaskbarWidgetEnabled && m.LyricsEnabled) : (TaskbarWidgetEnabled && LyricsEnabled);

    /// <summary>
    /// Returns the per-monitor taskbar settings profile for <paramref name="monitorIndex"/>,
    /// creating and seeding it if it does not exist yet.
    /// </summary>
    public MonitorTaskbarSettings GetMonitorSettings(int monitorIndex)
    {
        monitorIndex = Math.Max(0, monitorIndex);
        var existing = MonitorProfiles.FirstOrDefault(m => m.MonitorIndex == monitorIndex);
        if (existing != null)
            return existing;

        var created = new MonitorTaskbarSettings { MonitorIndex = monitorIndex };
        var primary = MonitorProfiles.FirstOrDefault(m => m.MonitorIndex == 0);
        if (primary != null && monitorIndex != 0)
            created.CopyFrom(primary);
        else
            created.CopyFromUserSettings(this);

        MonitorProfiles.Add(created);
        return created;
    }

    /// <summary>
    /// Syncs the flat taskbar/visualizer/lyrics properties on <see cref="UserSettings"/>
    /// into the currently selected monitor's <see cref="MonitorTaskbarSettings"/>.
    /// </summary>
    public void SyncActiveMonitorFromUserSettings()
    {
        if (_initializing || _switchingMonitorProfile)
            return;

        var profile = GetMonitorSettings(TaskbarWidgetSelectedMonitor);
        profile.CopyFromUserSettings(this);
    }

    /// <summary>
    /// Copies the currently selected monitor's profile to all monitors up to <paramref name="monitorCount"/>.
    /// </summary>
    public void CopyCurrentMonitorSettingsToAll(int monitorCount)
    {
        SyncActiveMonitorFromUserSettings();
        var source = GetMonitorSettings(TaskbarWidgetSelectedMonitor);
        int count = Math.Max(monitorCount, MonitorProfiles.Count);
        for (int i = 0; i < count; i++)
        {
            if (i == source.MonitorIndex)
                continue;
            var target = GetMonitorSettings(i);
            target.CopyFrom(source);
        }
        TaskbarVisualizerControl.RefreshSharedVisualizerState();
        Application.Current?.Dispatcher?.BeginInvoke(() => TaskbarVisualizerControl.ApplyBeatGlow());
        UpdateTaskbar();
        SettingsManager.SaveSettings();
    }

    /// <summary>
    /// Enables or disables a taskbar feature ("Widget", "Visualizer", or "Lyrics") on a specific monitor index.
    /// </summary>
    public void SetMonitorFeatureEnabled(int monitorIndex, string feature, bool enabled)
    {
        if ((feature == "Widget" || feature == "Visualizer" || feature == "Lyrics") && enabled && !IsPremiumUnlocked)
            return;

        SyncActiveMonitorFromUserSettings();

        var profile = GetMonitorSettings(monitorIndex);
        switch (feature)
        {
            case "Widget":
                profile.TaskbarWidgetEnabled = enabled;
                if (monitorIndex == TaskbarWidgetSelectedMonitor)
                {
                    _switchingMonitorProfile = true;
                    try { TaskbarWidgetEnabled = enabled; }
                    finally { _switchingMonitorProfile = false; }
                }
                TaskbarVisualizerControl.RefreshSharedVisualizerState();
                break;

            case "Visualizer":
                profile.TaskbarVisualizerEnabled = enabled;
                if (enabled && !profile.TaskbarWidgetEnabled && IsPremiumUnlocked)
                {
                    profile.TaskbarWidgetEnabled = true;
                    if (monitorIndex == TaskbarWidgetSelectedMonitor)
                    {
                        _switchingMonitorProfile = true;
                        try { TaskbarWidgetEnabled = true; }
                        finally { _switchingMonitorProfile = false; }
                    }
                }
                if (monitorIndex == TaskbarWidgetSelectedMonitor)
                {
                    _switchingMonitorProfile = true;
                    try { TaskbarVisualizerEnabled = enabled; }
                    finally { _switchingMonitorProfile = false; }
                }
                TaskbarVisualizerControl.RefreshSharedVisualizerState();
                Application.Current?.Dispatcher?.BeginInvoke(() => TaskbarVisualizerControl.ApplyBeatGlow());
                break;

            case "Lyrics":
                profile.LyricsEnabled = enabled;
                if (enabled && !profile.TaskbarWidgetEnabled && IsPremiumUnlocked)
                {
                    profile.TaskbarWidgetEnabled = true;
                    if (monitorIndex == TaskbarWidgetSelectedMonitor)
                    {
                        _switchingMonitorProfile = true;
                        try { TaskbarWidgetEnabled = true; }
                        finally { _switchingMonitorProfile = false; }
                    }
                }
                if (monitorIndex == TaskbarWidgetSelectedMonitor)
                {
                    _switchingMonitorProfile = true;
                    try { LyricsEnabled = enabled; }
                    finally { _switchingMonitorProfile = false; }
                }
                break;
        }

        UpdateTaskbar();
        SettingsManager.SaveSettings();
    }

    public UserSettings()
    {
        foreach (var supportedLanguage in LocalizationManager.SupportedLanguages)
        {
            LanguageOptions.Add(new LanguageOption(supportedLanguage.Key, supportedLanguage.Value));
        }

        CompactLayout = false;
        FlyoutSelectedMonitor = 0;
        Position = 0;
        FlyoutAnimationSpeed = 2;
        PlayerInfoEnabled = true;
        RepeatEnabled = false;
        ShuffleEnabled = false;
        Startup = true;
        Duration = 3000;
        NextUpEnabled = false;
        NextUpDuration = 2000;
        NIconLeftClick = 0;
        CenterTitleArtist = false;
        SettingsWindowWidth = 900;
        SettingsWindowHeight = 700;
        SettingsWindowLeft = double.NaN;
        SettingsWindowTop = double.NaN;
        SettingsWindowState = WindowState.Normal;
        FlyoutAnimationEasingStyle = 2;
        LockKeysEnabled = true;
        LockKeysCapsEnabled = true;
        LockKeysNumEnabled = true;
        LockKeysScrollEnabled = true;
        LockKeysDuration = 2000;
        AppTheme = 0;
        MediaFlyoutEnabled = true;
        MediaFlyoutAlwaysDisplay = false;
        MediaFlyoutVolumeKeysExcluded = false;
        NIconSymbol = false;
        NIconHide = false;
        DisableIfFullscreen = true;
        LockKeysBoldUi = false;
        LockKeysMonitorPreference = 0;
        LastKnownVersion = string.Empty;
        SeekbarEnabled = false;
        PauseOtherSessionsEnabled = false;
        PinnedSessionId = string.Empty;
        LockKeysAnimated = true;
        LockKeysInsertEnabled = true;
        MediaFlyoutBackgroundBlur = 0;
        AppLanguage = "system";
        SelectedLanguage = LanguageOptions.First(l => l.Tag == "system");
        FlowDirection = FlowDirection.LeftToRight;
        FontFamily = "Segoe UI Variable, Microsoft YaHei UI, Yu Gothic UI";
        MediaFlyoutAcrylicWindowEnabled = true;
        NextUpAcrylicWindowEnabled = true;
        LockKeysAcrylicWindowEnabled = true;
        VolumeMixerAcrylicWindowEnabled = true;
        TaskbarWidgetEnabled = false;
        TaskbarWidgetSelectedMonitor = 0;
        TaskbarWidgetPosition = 0;
        TaskbarWidgetPadding = true;
        TaskbarWidgetManualPadding = 0;
        TaskbarWidgetBackgroundBlur = false;
        TaskbarWidgetHideCompletely = false;
        TaskbarWidgetFixedWidth = false;
        TaskbarWidgetMaxWidth = 240;
        TaskbarWidgetShowPauseOverlay = true;
        TaskbarWidgetControlsEnabled = false;
        TaskbarWidgetControlsPosition = 1;
        TaskbarWidgetAnimated = true;
        TaskbarWidgetScrollingEnabled = false;
        TaskbarWidgetScrollVolumeMode = 1;
        TaskbarWidgetScrollingTextSpeed = 20;
        TaskbarWidgetScrollingTextLoopForever = false;
        TaskbarVisualizerEnabled = false;
        AppFilteringEnabled = false;
        AppFilteringMode = 0;
        TaskbarVisualizerPosition = 1;
        TaskbarVisualizerClickable = true;
        TaskbarVisualizerBarCount = 10;
        TaskbarVisualizerLength = 84;
        MediaSessionPriorityApp = "";
        LyricsEnabled = true;
        LyricsProvider = 0;
        LyricsWidth = 200;
        LyricsOffsetMs = 0;
        LyricsHidePausedAfterSeconds = 15;
        LyricsYtmSession = "";
        DisabledLyricsExpireDays = 7;
        TaskbarWidgetProgressBarEnabled = true;
        TaskbarVisualizerBeatGlow = true;
        TaskbarVisualizerStyle = 0;
        TaskbarVisualizerTargetApp = "";
        TaskbarVisualizerCenteredBars = false;
        TaskbarVisualizerBaseline = false;
        TaskbarVisualizerAudioSensitivity = 2;
        TaskbarVisualizerAudioPeakLevel = 3;
        TaskbarVisualizerBaselineAutoHide = false;
        VolumeControlEnabled = false;
        VolumeFlyoutShortcutEnabled = true;
        VolumeControlAboveMediaFlyout = false;
        VolumeControlDuration = 3000;
        VolumeMixerEnabled = false;
        VolumeMixerHighlightActiveApps = false;
        AcrylicBlurOpacity = 175;
        UseAlbumArtAsAccentColor = false;
        LastUpdateNotificationUnixSeconds = 0;
        ShowUpdateNotifications = true;
        LegacyTaskbarWidthEnabled = false;
        Uuid = Guid.NewGuid();
        AnonymousTelemetryAllowed = true;
        AllowedApps = [];
        BlockedApps = [];
        PhoneReceiverEnabled = true;
        PhoneReceiverPort = 58710;
        PhoneReceiverDiscovery = true;
        PhoneReceiverVbCableMix = false;
        MediaFlyoutShuffleRepeatEnabled = true;
        TaskbarWidgetShuffleRepeatEnabled = false;
        TaskbarWidgetTimeEnabled = false;
        YtmBridgeEnabled = true;
        YtmBridgePort = 26538;
        YtmBridgeClientId = $"fluentflyout-{Random.Shared.Next(100000, 999999)}";
        YtmBridgeToken = "";

        PropertyChanged += OnPropertyChangedSaveSettings;
    }

    [XmlIgnore]
    private CancellationTokenSource? _saveSettingsCts;

    internal void CancelPendingSave()
    {
        var oldCts = Interlocked.Exchange(ref _saveSettingsCts, null);
        if (oldCts != null)
        {
            try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
            try { oldCts.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    private async void OnPropertyChangedSaveSettings(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_initializing || _switchingMonitorProfile) return;

        // Only trigger save if a persisted property changed
        if (string.IsNullOrEmpty(e.PropertyName) || !PersistedPropertyNames.Contains(e.PropertyName))
            return;

        if (e.PropertyName != nameof(TaskbarWidgetSelectedMonitor))
        {
            SyncActiveMonitorFromUserSettings();
        }

#if DEBUG
        Logger.Debug("Property '{PropertyName}' changed, scheduling settings save.", e.PropertyName);
#endif

        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _saveSettingsCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();

        try
        {
            await Task.Delay(500, newCts.Token);

            if (ReferenceEquals(_saveSettingsCts, newCts))
            {
                SettingsManager.SaveSettings();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when replaced by a new property change
#if DEBUG
            Logger.Debug("Settings save canceled due to another property change.");
#endif
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "An error occurred while saving settings from property change.");
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _saveSettingsCts, null, newCts) == newCts)
            {
                newCts.Dispose();
            }
        }
    }

    /// <summary>
    /// Called after deserialization to finalize initialization
    /// </summary>
    internal void CompleteInitialization()
    {
        if (PhoneReceiverPort is < 1024 or > 65535)
            PhoneReceiverPort = 58710;
        if (YtmBridgePort is < 1024 or > 65535)
            YtmBridgePort = 26538;

        var matchedLanguage = LanguageOptions.FirstOrDefault(l => l.Tag == AppLanguage)
            ?? LanguageOptions.FirstOrDefault(l => l.Tag == "system");
        if (matchedLanguage != null)
        {
            SelectedLanguage = matchedLanguage;
            if (AppLanguage != matchedLanguage.Tag)
            {
                AppLanguage = matchedLanguage.Tag;
            }
        }

        if (MonitorProfiles.Count == 0)
        {
            var initial = new MonitorTaskbarSettings { MonitorIndex = Math.Max(0, TaskbarWidgetSelectedMonitor) };
            initial.CopyFromUserSettings(this);
            MonitorProfiles.Add(initial);
            if (initial.MonitorIndex != 0)
            {
                var zero = new MonitorTaskbarSettings { MonitorIndex = 0 };
                zero.CopyFrom(initial);
                MonitorProfiles.Add(zero);
            }
        }
        else
        {
            _switchingMonitorProfile = true;
            try
            {
                var activeProfile = GetMonitorSettings(TaskbarWidgetSelectedMonitor);
                activeProfile.ApplyToUserSettings(this);
                TaskbarVisualizerHasContent = activeProfile.TaskbarVisualizerHasContent;
            }
            finally
            {
                _switchingMonitorProfile = false;
            }
        }

        _initializing = false;
        Visualizer.ResizeBarList(TaskbarVisualizerBarCount);
    }

    partial void OnTaskbarWidgetSelectedMonitorChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile || newValue < 0)
            return;

        if (oldValue >= 0)
        {
            GetMonitorSettings(oldValue).CopyFromUserSettings(this);
        }

        _switchingMonitorProfile = true;
        try
        {
            var nextProfile = GetMonitorSettings(newValue);
            nextProfile.ApplyToUserSettings(this);
            TaskbarVisualizerHasContent = nextProfile.TaskbarVisualizerHasContent;
        }
        finally
        {
            _switchingMonitorProfile = false;
        }

        Visualizer.ResizeBarList(TaskbarVisualizerBarCount);
        TaskbarVisualizerControl.RefreshSharedVisualizerState();
        Application.Current?.Dispatcher?.BeginInvoke(() => TaskbarVisualizerControl.ApplyBeatGlow());
    }

    partial void OnAppLanguageChanged(string oldValue, string newValue)
    {
        if (oldValue == newValue || _initializing) return;
        var matched = LanguageOptions.FirstOrDefault(l => l.Tag == newValue);
        if (matched != null)
        {
            SelectedLanguage = matched;
        }
    }

    partial void OnSelectedLanguageChanged(LanguageOption oldValue, LanguageOption newValue)
    {
        if (oldValue == newValue || _initializing || newValue == null) return;
        AppLanguage = newValue.Tag;
        LocalizationManager.ApplyLocalization();
    }

    /// <summary>
    /// Changes the application theme when the selection is changed. 0 for default, 1 for light, 2 for dark.
    /// </summary>
    partial void OnAppThemeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        ThemeManager.ApplyThemeForSetting(newValue);
    }

    partial void OnNIconSymbolChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        ThemeManager.UpdateTrayIcon();
    }

    partial void OnAcrylicBlurOpacityChanged(uint oldValue, uint newValue)
    {
        if (oldValue == newValue || _initializing) return;
        WindowBlurHelper.AdjustBlurOpacityForAllWindows(newValue);
    }

    partial void OnPhoneReceiverEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        Classes.Services.Phone.PhoneReceiverService.Instance.ApplyFromSettings();
    }

    partial void OnPhoneReceiverPortChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        if (newValue is < 1024 or > 65535)
        {
            int fallback = oldValue is >= 1024 and <= 65535 ? oldValue : 58710;
            PhoneReceiverPort = fallback;
            Application.Current?.Dispatcher?.BeginInvoke(() => OnPropertyChanged(nameof(PhoneReceiverPort)));
            return;
        }
        Classes.Services.Phone.PhoneReceiverService.Instance.ApplyFromSettings();
    }

    partial void OnYtmBridgePortChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        if (newValue is < 1024 or > 65535)
        {
            int fallback = oldValue is >= 1024 and <= 65535 ? oldValue : 26538;
            YtmBridgePort = fallback;
            Application.Current?.Dispatcher?.BeginInvoke(() => OnPropertyChanged(nameof(YtmBridgePort)));
        }
    }

    partial void OnPhoneReceiverDiscoveryChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        Classes.Services.Phone.PhoneReceiverService.Instance.ApplyFromSettings();
    }

    partial void OnPhoneReceiverVbCableMixChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        Classes.Services.Phone.PhoneReceiverService.Instance.ApplyFromSettings();
    }

    partial void OnMediaFlyoutShuffleRepeatEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        Application.Current?.Dispatcher?.Invoke(() => (Application.Current?.MainWindow as MainWindow)?.RefreshMediaUi());
    }

    partial void OnTaskbarWidgetShuffleRepeatEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetTimeEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;

        // Check premium status before allowing widget to be enabled
        if (newValue && !SettingsManager.Current.IsPremiumUnlocked)
        {
            // Revert the change if premium is not unlocked
            TaskbarWidgetEnabled = false;
            return;
        }

        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    // Update taskbar when relevant settings change
    partial void OnTaskbarWidgetPositionChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetManualPaddingChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetBackgroundBlurChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetHideCompletelyChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetFixedWidthChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetMaxWidthChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetShowPauseOverlayChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetControlsEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnTaskbarWidgetControlsPositionChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnTaskbarVisualizerPositionChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnLegacyTaskbarWidthEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        UpdateTaskbar();
    }

    private void UpdateTaskbar()
    {
        if (Application.Current?.MainWindow is MainWindow mainWindow)
        {
            mainWindow.UpdateTaskbar();
        }
    }

    partial void OnTaskbarWidgetScrollingEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbarMarquees();
    }

    partial void OnTaskbarWidgetScrollingTextLoopForeverChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbarMarquees();
    }

    partial void OnTaskbarWidgetScrollingTextSpeedChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbarMarquees();
    }

    private void UpdateTaskbarMarquees()
    {
        if (Application.Current?.MainWindow is not MainWindow mainWindow) return;
        foreach (var tw in mainWindow.taskbarWindows)
        {
            var widget = tw.Widget;
            widget?.Dispatcher.Invoke(widget.UpdateMarquees);
        }
    }

    partial void OnTaskbarVisualizerEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;

        // Check premium status before allowing visualizer to be enabled
        if (newValue && !SettingsManager.Current.IsPremiumUnlocked)
        {
            TaskbarVisualizerEnabled = false;
            return;
        }

        SyncActiveMonitorFromUserSettings();
        TaskbarVisualizerControl.OnTaskbarVisualizerEnabledChanged(newValue);
        UpdateTaskbar();
    }

    partial void OnTaskbarVisualizerBarCountChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        Visualizer.ResizeBarList(newValue);
    }

    partial void OnTaskbarVisualizerLengthChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar(); // control resizes itself + bitmap via PropertyChanged; this only repositions it
    }

    partial void OnTaskbarVisualizerBeatGlowChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        Application.Current?.Dispatcher?.BeginInvoke(() => FluentFlyout.Controls.TaskbarVisualizerControl.ApplyBeatGlow());
    }

    partial void OnTaskbarVisualizerTargetAppChanged(string oldValue, string newValue)
    {
        if (oldValue == newValue || _initializing) return;
        Application.Current?.Dispatcher?.BeginInvoke(() => FluentFlyoutWPF.Classes.Visualizer.SetTargetApp(newValue));
    }

    partial void OnLyricsEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnLyricsProviderChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;
        Classes.Lyrics.LyricsService.Instance.RefreshCurrentTrack();
    }

    partial void OnLyricsWidthChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        UpdateTaskbar();
    }

    partial void OnDisabledLyricsExpireDaysChanged(int oldValue, int newValue)
    {
        if (newValue < 0)
        {
            DisabledLyricsExpireDays = 0;
            return;
        }
        if (oldValue == newValue || _initializing) return;
        Classes.Lyrics.LyricsService.Instance.PruneExpiredDisabledTracks();
    }

    partial void OnMediaSessionPriorityAppChanged(string oldValue, string newValue)
    {
        if (oldValue == newValue || _initializing) return;
        if (Application.Current?.MainWindow is MainWindow mainWindow)
        {
            mainWindow.UpdateTaskbar();
            mainWindow.RefreshMediaUi();
        }
    }

    partial void OnTaskbarVisualizerBaselineChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        if (newValue)
        {
            TaskbarVisualizerHasContent = true;
            GetMonitorSettings(TaskbarWidgetSelectedMonitor).TaskbarVisualizerHasContent = true;
        }
    }

    partial void OnTaskbarVisualizerBaselineAutoHideChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing || _switchingMonitorProfile) return;
        SyncActiveMonitorFromUserSettings();
        // If newValue is true, refresh the visualizer by hiding it:
        // if audio is playing, it will be shown again, if not, it will remain hidden.
        TaskbarVisualizerHasContent = !newValue;
        GetMonitorSettings(TaskbarWidgetSelectedMonitor).TaskbarVisualizerHasContent = !newValue;
    }

    partial void OnUseAlbumArtAsAccentColorChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;
        BitmapHelper.GetDominantColors(1);
    }

    partial void OnAppFilteringEnabledChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        (Application.Current?.MainWindow as MainWindow)?.RefreshFilteredMedia();
    }

    partial void OnAppFilteringModeChanged(int oldValue, int newValue)
    {
        if (oldValue == newValue || _initializing) return;

        (Application.Current?.MainWindow as MainWindow)?.RefreshFilteredMedia();
    }

    partial void OnVolumeMixerHighlightActiveAppsChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || _initializing) return;

        // Check premium status before allowing highlight to be enabled
        if (newValue && !SettingsManager.Current.IsPremiumUnlocked)
        {
            VolumeMixerHighlightActiveApps = false;
            return;
        }
    }

    partial void OnVolumeControlEnabledChanged(bool oldValue, bool newValue)
    {
        if (newValue == true || oldValue == newValue || _initializing) return;

        // re-enable native volume flyout
        VolumeMixerWindow.ShowVolumeOsd();
    }
}