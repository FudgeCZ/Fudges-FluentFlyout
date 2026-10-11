// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Lyrics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FluentFlyout.Controls;

/// <summary>
/// Taskbar lyrics widget: vertically scrolling synced lyrics shown next to the
/// equalizer. Hidden completely when no lyrics are found. Right-click offers
/// per-track provider switching. The view snaps to the active line with an eased
/// animation; the active line pops (opacity + scale) and edges fade out.
/// </summary>
public partial class TaskbarLyricsControl : UserControl
{
    private const double SmallTaskbarLyricsHeight = 28;
    private const double DefaultTaskbarLyricsHeight = 40;
    private const double LineGap = 7;
    private const double AnchorFraction = 0.5; // active line's vertical resting point
    private const double InactiveLineOpacity = 0.25;
    private const double AdjacentLineOpacity = 0.45;
    private const double ActiveLineScale = 1.05;

    private static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan LinePopDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);

    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _loadingShowTimer;   // one-shot: show the loading state if the fetch is slow
    private readonly DispatcherTimer _loadingDotsTimer;   // cycles the "···" while loading
    private readonly List<(TextBlock Block, double Y, double Height, TimeSpan Time)> _lines = [];

    private bool _awaitingResult; // a fetch for the current track may still come back
    private bool _loadingVisible; // the loading text is on screen
    private int _dotCycle;

    // last requested track, to re-fetch when enabled/width changes
    private string? _lastArtist;
    private string? _lastTitle;
    private TimeSpan _lastDuration;

    // clock state fed by MainWindow (position @ lastUpdated, playing flag);
    // the tick uses it only to detect the active line
    private TimeSpan _basePosition;
    private DateTime _baseUpdated = DateTime.MinValue;
    private bool _playing;

    private int _activeLine = -1;
    private int _generation; // invalidates pending fade-outs when new lyrics load
    private DateTime? _pausedSince;
    private bool _hiddenByPause;
    private int? _trackOffsetMs;   // per-track sync offset (mouse wheel), overrides the global one
    private DispatcherTimer? _offsetHintTimer;

    /// <summary>Whether the control currently has lyrics (or a search in progress) to display.</summary>
    public bool HasContent => Root.Visibility == Visibility.Visible && (_lines.Count > 0 || _loadingVisible);

    /// <summary>Raised after content appears or disappears (taskbar window region must be updated).</summary>
    public event Action? ContentChanged;

    public TaskbarLyricsControl()
    {
        InitializeComponent();

        Width = SettingsManager.Current.LyricsWidth;
        SetSmallTaskbarMode(false);

        SettingsManager.Current.PropertyChanged += SettingsManager_PropertyChanged;
        Unloaded += (_, _) => SettingsManager.Current.PropertyChanged -= SettingsManager_PropertyChanged;

        LyricsService.Instance.LyricsChanged += OnLyricsServiceChanged;
        Unloaded += (_, _) => LyricsService.Instance.LyricsChanged -= OnLyricsServiceChanged;

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tickTimer.Tick += (_, _) => TickScroll();

        // make the strip hit-testable for wheel-based offset adjustment
        ClipBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
        ClipBorder.MouseWheel += LyricsControl_MouseWheel;

        _offsetHintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _offsetHintTimer.Tick += (_, _) =>
        {
            _offsetHintTimer.Stop();
            if (LoadingText.Text.StartsWith('+') || LoadingText.Text.StartsWith('-'))
            {
                LoadingText.Visibility = Visibility.Collapsed;
            }
        };

        // the loading indicator only appears if the fetch is still running after
        // a short grace period, so cache hits never flash it
        _loadingShowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _loadingShowTimer.Tick += (_, _) =>
        {
            _loadingShowTimer.Stop();
            if (_awaitingResult)
                ShowLoading();
        };

        _loadingDotsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _loadingDotsTimer.Tick += (_, _) =>
        {
            _dotCycle = (_dotCycle + 1) % 3;
            LoadingText.Text = "searching lyrics" + new string('·', _dotCycle + 1);
        };
    }

    private void ShowLoading()
    {
        if (!_awaitingResult)
            return;

        _loadingVisible = true;
        LoadingText.Foreground = GetTextBrush();
        LoadingText.Text = "searching lyrics·";
        LoadingText.Visibility = Visibility.Visible;
        _dotCycle = 0;
        _loadingDotsTimer.Start();

        if (Root.Visibility != Visibility.Visible)
        {
            Root.Visibility = Visibility.Visible;
            ContentChanged?.Invoke(); // region now includes the widget
        }
    }

    private void StopLoadingUi()
    {
        _loadingShowTimer.Stop();
        _loadingDotsTimer.Stop();
        LoadingText.Visibility = Visibility.Collapsed;
        bool was = _loadingVisible;
        _loadingVisible = false;
        if (was)
            ContentChanged?.Invoke();
    }

    private void SettingsManager_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsManager.Current.LyricsWidth))
        {
            ApplyWidthFromSetting();
        }
        else if (e.PropertyName == nameof(SettingsManager.Current.LyricsEnabled))
        {
            if (SettingsManager.Current.LyricsEnabled)
                SetTrack(_lastArtist, _lastTitle, _lastDuration); // re-fetch, nulls clear
            else
                Clear();
        }
    }

    /// <summary>
    /// Requests lyrics for a track. Null artist/title (or empty both) hides the widget.
    /// </summary>
    public void SetTrack(string? artist, string? title, TimeSpan duration)
    {
        _lastArtist = artist;
        _lastTitle = title;
        _lastDuration = duration;

        if (!SettingsManager.Current.LyricsEnabled || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            Clear();
            return;
        }

        _trackOffsetMs = LyricsService.Instance.GetOffsetMs(artist, title, duration);

        var svc = LyricsService.Instance;
        if (svc.StateOf(artist, title, duration) == LyricsTrackState.Found)
        {
            StopLoadingUi();
            LoadLyrics(svc.Current); // lyrics cached in memory — no fetch needed
            return;
        }

        // fresh fetch (also retries tracks that previously found nothing) — drop the
        // previous track's visuals immediately, arm the delayed loading indicator, and go
        TeardownLines();
        _awaitingResult = true;
        _loadingShowTimer.Stop();
        _loadingShowTimer.Start();
        _ = svc.FetchSmartAsync(artist, title, duration);
    }

    /// <summary>Instantly removes lyrics and collapses the widget (loading timers untouched).</summary>
    private void TeardownLines()
    {
        bool had = _lines.Count > 0;
        _lines.Clear();
        LinesCanvas.Children.Clear();
        _activeLine = -1;
        _tickTimer.Stop();
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 1;
        Root.Visibility = Visibility.Collapsed;
        if (had)
            ContentChanged?.Invoke();
    }

    /// <summary>
    /// Feeds the playback clock (position as of lastUpdated). Also drives the
    /// pause-auto-hide: after <see cref="UserSettings.LyricsHidePausedAfterSeconds"/>
    /// of continuous pause the widget fades out, and it returns when playback resumes.
    /// </summary>
    public void SetClock(TimeSpan position, DateTime lastUpdated, bool playing)
    {
        _basePosition = position;
        _baseUpdated = lastUpdated;

        if (playing == _playing)
        {
            if (!playing && _pausedSince == null && HasContent)
                _pausedSince = DateTime.Now; // heartbeat pushed while already paused
            return;
        }

        NLog.LogManager.GetCurrentClassLogger().Info(
            "Lyrics SetClock: playing {0} -> {1}, hiddenByPause={2}, lines={3}",
            _playing, playing, _hiddenByPause, _lines.Count);
        _playing = playing;
        if (playing)
        {
            _pausedSince = null;
            if (_hiddenByPause)
                ShowFromPause();
        }
        else
        {
            _pausedSince = HasContent ? DateTime.Now : null;
        }
    }

    private void OnLyricsServiceChanged()
    {
        LoadLyrics(LyricsService.Instance.Current);
    }

    private void LoadLyrics(LyricsResult? result)
    {
        _generation++;
        _awaitingResult = false;
        StopLoadingUi();
        _pausedSince = null;
        _hiddenByPause = false;

        if (result == null || result.Lines.Count == 0)
        {
            Clear();
            return;
        }

        _lines.Clear();
        LinesCanvas.Children.Clear();
        ScrollTransform.Y = 0;
        _activeLine = -1;

        var brush = GetTextBrush();
        int count = result.Lines.Count;
        double y = 0;

        for (int i = 0; i < count; i++)
        {
            var line = result.Lines[i];

            // unsynced lyrics: distribute lines evenly across the track duration
            TimeSpan time = result.Synced || _lastDuration <= TimeSpan.Zero
                ? line.Time
                : TimeSpan.FromMilliseconds(_lastDuration.TotalMilliseconds * (i + 0.5) / count);

            var tb = new TextBlock
            {
                Text = line.Text,
                FontSize = 12,
                Foreground = brush,
                Opacity = InactiveLineOpacity,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Width = ContentWidth,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };
            Canvas.SetTop(tb, y);
            Canvas.SetLeft(tb, LineBoxInset);
            LinesCanvas.Children.Add(tb);

            tb.Measure(new Size(ContentWidth, double.PositiveInfinity));
            _lines.Add((tb, y, tb.DesiredSize.Height, time));
            y += tb.DesiredSize.Height + LineGap;
        }

        // widen (up to 400 px) if any line would wrap to 3+ rows
        AutoFitWidth();

        // fade the whole widget in
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0;
        Root.Visibility = Visibility.Visible;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

        if (!_tickTimer.IsEnabled)
            _tickTimer.Start();

        ContentChanged?.Invoke();
    }

    /// <summary>Width available to a text line inside the control.</summary>
    private double ContentWidth => Math.Max(40, Width - 20);

    /// <summary>Horizontal inset that centers a ContentWidth-wide line box in the control.</summary>
    private double LineBoxInset => (Width - ContentWidth) / 2;

    /// <summary>
    /// Resets the width to the user setting, then re-applies the 2-line auto-fit.
    /// </summary>
    private void ApplyWidthFromSetting()
    {
        Width = SettingsManager.Current.LyricsWidth;
        if (_lines.Count == 0)
            return;

        RelayoutCore();
        AutoFitWidth();
        ContentChanged?.Invoke();
    }

    /// <summary>
    /// Widens the control until no lyric line wraps to more than 2 rows
    /// (capped at 400 px). Keeps the LyricsWidth setting as the base width.
    /// </summary>
    private void AutoFitWidth()
    {
        if (_lines.Count == 0)
            return;

        double rowHeight = MeasureRowHeight();
        if (rowHeight <= 1 || MaxRowCount(rowHeight) <= 2)
            return;

        int guard = 0;
        while (guard++ < 20 && Width < 400 && MaxRowCount(rowHeight) > 2)
        {
            Width = Math.Min(400, Width + 16);
            RelayoutCore();
        }
    }

    private double MeasureRowHeight()
    {
        var probe = new TextBlock { Text = "Xg", FontSize = 12 };
        probe.Measure(new Size(1000, double.PositiveInfinity));
        return Math.Max(1, probe.DesiredSize.Height);
    }

    private int MaxRowCount(double rowHeight)
    {
        int max = 1;
        foreach (var (tb, _, _, _) in _lines)
        {
            tb.Width = ContentWidth;
            tb.Measure(new Size(ContentWidth, double.PositiveInfinity));
            max = Math.Max(max, (int)Math.Round(tb.DesiredSize.Height / rowHeight));
        }
        return max;
    }

    private void RelayoutCore()
    {
        // re-measure line positions for the current width
        double y = 0;
        for (int i = 0; i < _lines.Count; i++)
        {
            var (tb, _, _, time) = _lines[i];
            tb.Width = ContentWidth;
            tb.Measure(new Size(ContentWidth, double.PositiveInfinity));
            Canvas.SetTop(tb, y);
            Canvas.SetLeft(tb, LineBoxInset);
            _lines[i] = (tb, y, tb.DesiredSize.Height, time);
            y += tb.DesiredSize.Height + LineGap;
        }

        ScrollTransform.Y = 0;
        _activeLine = -1;
    }

    public void Clear()
    {
        _awaitingResult = false;
        StopLoadingUi();

        if (Root.Visibility != Visibility.Visible)
        {
            // hidden (e.g. pause auto-hide) — tear down silently
            _lines.Clear();
            LinesCanvas.Children.Clear();
            _tickTimer.Stop();
            _hiddenByPause = false;
            _pausedSince = null;
            return;
        }

        // loading state has no fade history to preserve — hide instantly
        if (_loadingVisible || _lines.Count == 0)
        {
            _lines.Clear();
            LinesCanvas.Children.Clear();
            _tickTimer.Stop();
            Root.Visibility = Visibility.Collapsed;
            ContentChanged?.Invoke();
            return;
        }

        // fade the widget out, then tear down (skipped if new lyrics arrived meanwhile)
        _hiddenByPause = false;
        int gen = _generation;
        var fade = new DoubleAnimation(0, FadeDuration);
        fade.Completed += (_, _) =>
        {
            if (gen != _generation)
                return;

            _lines.Clear();
            LinesCanvas.Children.Clear();
            _tickTimer.Stop();
            Root.BeginAnimation(OpacityProperty, null);
            Root.Opacity = 1;
            Root.Visibility = Visibility.Collapsed;
            ContentChanged?.Invoke();
        };
        Root.BeginAnimation(OpacityProperty, fade);
    }

    public void SetSmallTaskbarMode(bool isSmallTaskbar)
    {
        Height = isSmallTaskbar ? SmallTaskbarLyricsHeight : DefaultTaskbarLyricsHeight;
    }

    private static CubicEase EaseOut() => new() { EasingMode = EasingMode.EaseOut };

    /// <summary>Mouse wheel over the widget nudges this track's sync offset (±100 ms).</summary>
    private void LyricsControl_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (_lastArtist == null || _lastTitle == null || !HasContent)
            return;

        int step = e.Delta > 0 ? 100 : -100;
        int current = _trackOffsetMs ?? SettingsManager.Current.LyricsOffsetMs;
        _trackOffsetMs = Math.Clamp(current + step, -5000, 5000);
        e.Handled = true;

        LyricsService.Instance.SetOffsetMs(_lastArtist, _lastTitle, _lastDuration, _trackOffsetMs.Value);

        // brief "+0.3 s" confirmation on the strip
        LoadingText.Foreground = GetTextBrush();
        LoadingText.Text = _trackOffsetMs.Value >= 0
            ? $"+{_trackOffsetMs.Value / 1000.0:0.#} s"
            : $"{_trackOffsetMs.Value / 1000.0:0.#} s";
        LoadingText.Visibility = Visibility.Visible;
        _offsetHintTimer.Stop();
        _offsetHintTimer.Start();
    }

    private void TickScroll()
    {
        // self-heal: a resume whose playing transition was missed while hidden must
        // still bring the strip back (the normal path is SetClock -> ShowFromPause)
        if (_hiddenByPause && _playing && _lines.Count > 0)
            ShowFromPause();

        if (_lines.Count == 0 || Root.Visibility != Visibility.Visible)
            return;

        var pos = _basePosition;
        if (_playing && _baseUpdated != DateTime.MinValue)
            pos += DateTime.Now - _baseUpdated;
        pos += TimeSpan.FromMilliseconds(_trackOffsetMs ?? SettingsManager.Current.LyricsOffsetMs);

        // pause auto-hide
        int hideAfter = SettingsManager.Current.LyricsHidePausedAfterSeconds;
        if (!_playing && hideAfter > 0)
        {
            _pausedSince ??= DateTime.Now;
            if (!_hiddenByPause && (DateTime.Now - _pausedSince.Value).TotalSeconds >= hideAfter)
            {
                HideForPause();
                return;
            }
        }
        else
        {
            _pausedSince = null;
        }

        int index = FindLineIndex(pos);
        if (index == _activeLine)
            return; // nothing changed since the last animation

        _activeLine = index;
        AnimateToLine(index);
    }

    private void HideForPause()
    {
        NLog.LogManager.GetCurrentClassLogger().Info("Lyrics HideForPause");
        _hiddenByPause = true;
        int gen = _generation;
        var fade = new DoubleAnimation(0, FadeDuration);
        fade.Completed += (_, _) =>
        {
            if (gen != _generation || !_hiddenByPause)
                return;

            Root.Visibility = Visibility.Collapsed;
            ContentChanged?.Invoke();
        };
        Root.BeginAnimation(OpacityProperty, fade);
    }

    private void ShowFromPause()
    {
        NLog.LogManager.GetCurrentClassLogger().Info("Lyrics ShowFromPause");
        _hiddenByPause = false;
        _pausedSince = _playing ? null : _pausedSince;
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0;
        Root.Visibility = Visibility.Visible;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        ContentChanged?.Invoke();
    }

    /// <summary>Eased scroll to a line + highlight pop on it, dimming the rest.</summary>
    private void AnimateToLine(int index)
    {
        // scroll: bring the line's center to the anchor point
        double target = index < 0
            ? ActualHeight * AnchorFraction // before the first line: first line sits at the anchor
            : ActualHeight * AnchorFraction - (_lines[index].Y + _lines[index].Height / 2);
        ScrollTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(target, ScrollDuration) { EasingFunction = EaseOut() });

        for (int i = 0; i < _lines.Count; i++)
        {
            var block = _lines[i].Block;
            bool active = i == index;

            double opacity = active ? 1.0
                : Math.Abs(i - index) == 1 ? AdjacentLineOpacity
                : InactiveLineOpacity;
            block.BeginAnimation(OpacityProperty,
                new DoubleAnimation(opacity, LinePopDuration) { EasingFunction = EaseOut() });

            // blur depth: lines near the active one are soft, distant ones stay unblurred
            int distance = Math.Abs(i - index);
            block.Effect = active || distance > 2
                ? null
                : new System.Windows.Media.Effects.BlurEffect { Radius = distance == 1 ? 1.6 : 2.4 };

            double scale = active ? ActiveLineScale : 1.0;
            if (block.RenderTransform is ScaleTransform st)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(scale, LinePopDuration) { EasingFunction = EaseOut() });
                st.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(scale, LinePopDuration) { EasingFunction = EaseOut() });
            }
        }
    }

    private int FindLineIndex(TimeSpan pos)
    {
        if (pos < _lines[0].Time)
            return -1;

        int lo = 0, hi = _lines.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_lines[mid].Time <= pos)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// Theme-aware text brush. Resource lookups resolve to light-theme values in this
    /// window's context, so detect the system theme like the widget's ApplyWindowsTheme.
    /// </summary>
    private static Brush GetTextBrush()
    {
        WindowsThemeDetector.GetWindowsTheme(out _, out var systemTheme);
        bool isDark = systemTheme == WindowsThemeDetector.ThemeMode.Dark;
        return new SolidColorBrush(isDark ? Colors.White : Colors.Black);
    }
}
