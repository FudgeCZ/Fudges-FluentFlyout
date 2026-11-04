// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace FluentFlyout.Controls;

/// <summary>
/// Interaction logic for TaskbarVisualizerControl.xaml
/// </summary>
public partial class TaskbarVisualizerControl : UserControl
{
    private const double DefaultTaskbarVisualizerHeight = 40;
    private const double SmallTaskbarVisualizerHeight = 28;

    // reference to shared audio capture / FFT engine
    private static readonly Visualizer visualizer = new();

    /// <summary>The single shared Visualizer instance (for static hooks).</summary>
    internal static Visualizer SharedVisualizer => visualizer;

    private static readonly List<TaskbarVisualizerControl> _instances = [];
    private static readonly object _instancesLock = new();
    private static System.Windows.Media.Effects.DropShadowEffect? _glowEffect;
    private static System.Windows.Threading.DispatcherTimer? _glowTimer;

    internal MonitorTaskbarSettings Settings { get; private set; } = new();
    internal WriteableBitmap? Bitmap { get; private set; }
    internal object BitmapLock { get; } = new();
    internal int ImageWidth { get; private set; } = 76 * Visualizer.Supersample;
    internal float[] BarValues { get; private set; } = new float[10];
    internal float[] PeakValues { get; private set; } = new float[10];

    public TaskbarVisualizerControl()
    {
        InitializeComponent();

        lock (_instancesLock)
        {
            if (!_instances.Contains(this))
                _instances.Add(this);
        }

        Loaded += (_, _) =>
        {
            lock (_instancesLock)
            {
                if (!_instances.Contains(this))
                    _instances.Add(this);
            }
            RefreshSharedVisualizerState();
            ApplyBeatGlow();
        };

        Unloaded += (_, _) =>
        {
            Settings.PropertyChanged -= Settings_PropertyChanged;
            lock (_instancesLock)
            {
                _instances.Remove(this);
            }
            RefreshSharedVisualizerState();
            ApplyBeatGlow();
        };

        BindMonitorSettings(SettingsManager.Current.GetMonitorSettings(0));

        // settings hooks are skipped during deserialization — apply the audio source here
        Visualizer.SetTargetApp(SettingsManager.Current.TaskbarVisualizerTargetApp);

        // for hover animation
        if (MainBorder.Background is not SolidColorBrush)
        {
            MainBorder.Background = new SolidColorBrush(Colors.Transparent);
            MainBorder.Background.Opacity = 0;
        }

        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    }

    public void BindMonitorSettings(MonitorTaskbarSettings settings)
    {
        Settings.PropertyChanged -= Settings_PropertyChanged;
        Settings = settings;
        DataContext = settings;
        Settings.PropertyChanged += Settings_PropertyChanged;

        EnsureBarArrays(Math.Clamp(Settings.TaskbarVisualizerBarCount, 1, 64));
        ApplyLength(Settings.TaskbarVisualizerLength);
        UpdateBaselineVisibilityState();
        RefreshSharedVisualizerState();
        ApplyBeatGlow();
    }

    internal static List<TaskbarVisualizerControl> GetActiveInstances()
    {
        lock (_instancesLock)
        {
            return [.. _instances];
        }
    }

    internal static bool HasAnyEnabledInstance()
    {
        lock (_instancesLock)
        {
            return _instances.Any(i => i.Settings.TaskbarWidgetEnabled && i.Settings.TaskbarVisualizerEnabled);
        }
    }

    internal void EnsureBarArrays(int barCount)
    {
        barCount = Math.Clamp(barCount, 1, 64);
        if (BarValues.Length != barCount)
        {
            var nextBars = new float[barCount];
            Array.Copy(BarValues, nextBars, Math.Min(BarValues.Length, barCount));
            BarValues = nextBars;
        }
        if (PeakValues.Length != barCount)
        {
            var nextPeaks = new float[barCount];
            Array.Copy(PeakValues, nextPeaks, Math.Min(PeakValues.Length, barCount));
            PeakValues = nextPeaks;
        }
    }

    internal void ClearBarsToSilence()
    {
        Array.Clear(BarValues, 0, BarValues.Length);
        Array.Clear(PeakValues, 0, PeakValues.Length);
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorTaskbarSettings.TaskbarVisualizerLength))
        {
            ApplyLength(Settings.TaskbarVisualizerLength);
        }
        else if (e.PropertyName == nameof(MonitorTaskbarSettings.TaskbarVisualizerEnabled))
        {
            UpdateBaselineVisibilityState();
            RefreshSharedVisualizerState();
            ApplyBeatGlow();
        }
        else if (e.PropertyName == nameof(MonitorTaskbarSettings.TaskbarVisualizerBeatGlow))
        {
            ApplyBeatGlow();
        }
        else if (e.PropertyName is nameof(MonitorTaskbarSettings.TaskbarVisualizerBaseline)
            or nameof(MonitorTaskbarSettings.TaskbarVisualizerBaselineAutoHide))
        {
            UpdateBaselineVisibilityState();
        }
        else if (e.PropertyName == nameof(MonitorTaskbarSettings.TaskbarVisualizerBarCount))
        {
            EnsureBarArrays(Settings.TaskbarVisualizerBarCount);
        }
    }

    private void UpdateBaselineVisibilityState()
    {
        if (!Settings.TaskbarVisualizerEnabled)
        {
            Settings.TaskbarVisualizerHasContent = false;
        }
        else if (Settings.TaskbarVisualizerBaseline && !Settings.TaskbarVisualizerBaselineAutoHide)
        {
            Settings.TaskbarVisualizerHasContent = true;
        }
    }

    private void ApplyLength(int length)
    {
        Width = length;
        int w = Visualizer.ContentWidthFromLength(length);
        lock (BitmapLock)
        {
            if (Bitmap == null || ImageWidth != w)
            {
                ImageWidth = w;
                Bitmap = new WriteableBitmap(ImageWidth, Visualizer.ImageHeight, 96, 96, PixelFormats.Pbgra32, null);
            }
        }
        VisualizerContainer.Source = Bitmap;

        // reposition once the new Width has been laid out (settings hook fires before it)
        Dispatcher.BeginInvoke(() => (System.Windows.Application.Current?.MainWindow as MainWindow)?.UpdateTaskbar(),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    public void SetSmallTaskbarMode(bool isSmallTaskbar)
    {
        Height = isSmallTaskbar ? SmallTaskbarVisualizerHeight : DefaultTaskbarVisualizerHeight;
    }

    public static void RefreshSharedVisualizerState()
    {
        if (visualizer == null)
            return;

        if (HasAnyEnabledInstance())
        {
            visualizer.Start();
        }
        else
        {
            visualizer.Stop();
        }
    }

    public static void OnTaskbarVisualizerEnabledChanged(bool value)
    {
        RefreshSharedVisualizerState();
        ApplyBeatGlow();
    }

    /// <summary>
    /// Beat-reactive glow: a soft DropShadow around the visualizer whose blur and
    /// opacity follow the smoothed low-frequency energy of the audio.
    /// </summary>
    public static void ApplyBeatGlow()
    {
        if (System.Windows.Application.Current == null)
            return;

        System.Windows.Application.Current.Dispatcher.VerifyAccess();

        var instances = GetActiveInstances();
        bool anyGlowEnabled = instances.Any(i => i.Settings.TaskbarVisualizerEnabled && i.Settings.TaskbarVisualizerBeatGlow);

        if (!anyGlowEnabled)
        {
            _glowTimer?.Stop();
            _glowTimer = null;
            foreach (var inst in instances)
                inst.MainBorder.Effect = null;
            _glowEffect = null;
            return;
        }

        _glowEffect ??= new System.Windows.Media.Effects.DropShadowEffect
        {
            ShadowDepth = 0,
            BlurRadius = 0,
            Opacity = 0,
        };

        foreach (var inst in instances)
        {
            inst.MainBorder.Effect = (inst.Settings.TaskbarVisualizerEnabled && inst.Settings.TaskbarVisualizerBeatGlow)
                ? _glowEffect
                : null;
        }

        if (_glowTimer == null)
        {
            _glowTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _glowTimer.Tick += (_, _) =>
            {
                if (_glowEffect == null)
                    return;

                // only touch the effect when a value meaningfully changed — every
                // BlurRadius write forces a full shadow re-blur on the GPU
                float energy = Math.Clamp(Visualizer.CurrentEnergy, 0f, 1f);
                double radius = 6 + energy * 30;
                double opacity = 0.15 + energy * 0.55;
                if (Math.Abs(_glowEffect.BlurRadius - radius) < 0.6 &&
                    Math.Abs(_glowEffect.Opacity - opacity) < 0.02)
                    return;

                _glowEffect.BlurRadius = (float)radius;
                _glowEffect.Opacity = opacity;

                var brush = BitmapHelper.SavedDominantColors.Count > 0
                    ? BitmapHelper.SavedDominantColors.Last()
                    : (SolidColorBrush)System.Windows.Application.Current.TryFindResource("MicaWPF.Brushes.SystemAccentColorTertiary");
                if (brush != null && _glowEffect.Color != brush.Color)
                    _glowEffect.Color = brush.Color;
            };
        }
        _glowTimer.Start();
    }

    public static void DisposeVisualizer()
    {
        if (visualizer == null)
            return;

        visualizer.Dispose();
    }

    private void Grid_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!Settings.TaskbarVisualizerClickable || !Settings.TaskbarVisualizerHasContent) return;

        SolidColorBrush targetBackgroundBrush;
        WindowsThemeDetector.GetWindowsTheme(out _, out var systemTheme);
        bool isDark = systemTheme == WindowsThemeDetector.ThemeMode.Dark;
        if (isDark)
        { // dark mode
            targetBackgroundBrush = new SolidColorBrush(Color.FromArgb(197, 255, 255, 255)) { Opacity = 0.075 };
            TopBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(93, 255, 255, 255)) { Opacity = 0.25 };
        }
        else
        { // light mode
            targetBackgroundBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)) { Opacity = 0.6 };
            TopBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(93, 255, 255, 255)) { Opacity = 1 };
        }

        // Animate background
        var backgroundAnimation = new ColorAnimation
        {
            To = targetBackgroundBrush.Color,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var backgroundOpacityAnimation = new DoubleAnimation
        {
            To = targetBackgroundBrush.Opacity,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (MainBorder.Background is not SolidColorBrush)
        {
            MainBorder.Background = new SolidColorBrush(Colors.Transparent);
            MainBorder.Background.Opacity = 0;
        }

        MainBorder.Background.BeginAnimation(SolidColorBrush.ColorProperty, backgroundAnimation);
        MainBorder.Background.BeginAnimation(SolidColorBrush.OpacityProperty, backgroundOpacityAnimation);
    }

    private void Grid_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!Settings.TaskbarVisualizerClickable || !Settings.TaskbarVisualizerHasContent) return;

        // Animate back to transparent
        var backgroundAnimation = new ColorAnimation
        {
            To = Colors.Transparent,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        var backgroundOpacityAnimation = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        MainBorder.Background?.BeginAnimation(SolidColorBrush.ColorProperty, backgroundAnimation);
        MainBorder.Background?.BeginAnimation(SolidColorBrush.OpacityProperty, backgroundOpacityAnimation);

        TopBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
    }

    private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!Settings.TaskbarVisualizerClickable || !Settings.TaskbarVisualizerHasContent) return;

        SettingsManager.Current.TaskbarWidgetSelectedMonitor = Settings.MonitorIndex;
        SettingsWindow.ShowInstance("TaskbarVisualizerPage");
    }
}
