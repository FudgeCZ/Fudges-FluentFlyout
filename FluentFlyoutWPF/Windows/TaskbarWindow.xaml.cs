// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using FluentFlyoutWPF.Models;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyout.Windows;

/// <summary>
/// Interaction logic for TaskbarWindow.xaml
/// </summary>
public partial class TaskbarWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private const double SmallTaskbarDetectionThreshold = 40;

    private readonly DispatcherTimer _timer;
    private readonly int _nativeWidgetsPadding = 216;
    private readonly double _scale = 0.9;

    private IntPtr _trayHandle;
    private AutomationElement? _widgetElement;
    private AutomationElement? _trayElement;
    private AutomationElement? _taskbarFrameElement;
    private AutomationElement? _iconsAreaElement;       // cached taskbar root for the icons-area query
    private Rect _iconsAreaRectCache = Rect.Empty;      // union of pinned/running app buttons + Start/Search
    private DateTime _iconsAreaCacheTime = DateTime.MinValue;
    // reference to main window for flyout functions
    private MainWindow? _mainWindow;
    private int _lastSelectedMonitor = -1;
    private IntPtr _lastTaskbarHandle;
    private bool _positionUpdateInProgress;
    private bool _isClosing;
    private readonly Dictionary<string, Task> _pendingAutomationTasks = [];

    private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _lastPlaybackStatus;
    private DispatcherTimer? _autoHideTimer;

    public int MonitorIndex { get; }
    public MonitorTaskbarSettings MonitorSettings => SettingsManager.Current.GetMonitorSettings(MonitorIndex);

    public TaskbarWindow(int monitorIndex = 0)
    {
        MonitorIndex = monitorIndex;
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        WindowHelper.SetTopmost(this);

        var settings = MonitorSettings;
        DataContext = settings;
        Widget.BindMonitorSettings(settings);
        TaskbarVisualizer.BindMonitorSettings(settings);
        Lyrics.BindMonitorSettings(settings);
        settings.PropertyChanged += MonitorSettings_PropertyChanged;

        _timer = new DispatcherTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(1500); // slow auto-update for display changes
        _timer.Tick += (s, e) => UpdatePosition();
        _timer.Start();

        Show();
    }

    private void MonitorSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isClosing)
            return;

        if (e.PropertyName == nameof(MonitorTaskbarSettings.TaskbarWidgetEnabled))
        {
            if (!MonitorSettings.TaskbarWidgetEnabled || !SettingsManager.Current.IsPremiumUnlocked)
            {
                HideTaskbarWindow();
            }
            else
            {
                Dispatcher.BeginInvoke(() =>
                {
                    WidgetCanvas.Visibility = Visibility.Visible;
                    if (!_timer.IsEnabled)
                        _timer.Start();
                    _mainWindow?.UpdateTaskbar();
                    UpdatePosition();
                });
            }
            return;
        }

        if (e.PropertyName is nameof(MonitorTaskbarSettings.TaskbarWidgetPosition)
            or nameof(MonitorTaskbarSettings.TaskbarWidgetPadding)
            or nameof(MonitorTaskbarSettings.TaskbarWidgetManualPadding)
            or nameof(MonitorTaskbarSettings.TaskbarWidgetMaxWidth)
            or nameof(MonitorTaskbarSettings.TaskbarWidgetFixedWidth)
            or nameof(MonitorTaskbarSettings.TaskbarVisualizerEnabled)
            or nameof(MonitorTaskbarSettings.TaskbarVisualizerPosition)
            or nameof(MonitorTaskbarSettings.TaskbarVisualizerLength)
            or nameof(MonitorTaskbarSettings.LyricsEnabled)
            or nameof(MonitorTaskbarSettings.LyricsWidth))
        {
            UpdatePosition();
        }
    }

    private void HideTaskbarWindow()
    {
        if (_timer.IsEnabled)
            _timer.Stop();

        Dispatcher.Invoke(() =>
        {
            WidgetCanvas.Visibility = Visibility.Collapsed;
            var interop = new WindowInteropHelper(this);
            if (interop.Handle != IntPtr.Zero)
            {
                UpdateWindowRegion(interop.Handle);
            }
            Visibility = Visibility.Collapsed;
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource source = (HwndSource)PresentationSource.FromDependencyObject(this);
        source.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_DPICHANGED or WM_DPICHANGED_AFTERPARENT)
        {
            // WPF processes WM_DPICHANGED itself. Refresh placement after that layout
            // pass; PMv2 child windows receive the AFTERPARENT variant instead.
            Dispatcher.BeginInvoke(() =>
            {
                InvalidateMeasure();
                InvalidateArrange();
                InvalidateVisual();
                UpdateLayout();
                UpdatePosition();
            }, DispatcherPriority.Loaded);
        }

        // Some interface mods may collect information from all windows associated with the taskbar,
        // causing the widget and the entire taskbar to freeze.
        // For example, Nilesoft Shell and "Click on empty taskbar space" from Windhawk.
        // Therefore, we are preventing the propagation of this message.
        // Also prevents the widget from blocking taskbar's message processing, which is another source of freezes.
        switch (msg)
        {
            case 0x003D: // WM_GETOBJECT (Sent by Microsoft UI Automation to obtain information about an accessible object contained in a server application)
            case 0x0018: // WM_SHOWWINDOW
            case 0x0046: // WM_WINDOWPOSCHANGING - Triggers during alt-tabs, window changes
            case 0x0083: // WM_NCCALCSIZE - Can trigger layout storms
            case 0x0281: // WM_IME_SETCONTEXT - IME conflicts
            case 0x0282: // WM_IME_NOTIFY
                handled = true;
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetupWindow();
        _mainWindow = (MainWindow)Application.Current.MainWindow;
        Widget.SetMainWindow(_mainWindow);
        Lyrics.ContentChanged += () => UpdatePosition();
        _mainWindow.PushInitialLyricsTrack();
    }

    private IntPtr GetSelectedTaskbarHandle(out bool isMainTaskbarSelected)
    {
        var monitors = MonitorUtil.GetMonitors();
        isMainTaskbarSelected = false;
        if (monitors.Count == 0 || MonitorIndex < 0 || MonitorIndex >= monitors.Count)
            return IntPtr.Zero;

        var selectedMonitor = monitors[MonitorIndex];

        // Get the main taskbar and check if it is on this window's monitor.
        var mainHwnd = FindWindow("Shell_TrayWnd", null);
        if (mainHwnd != IntPtr.Zero && MonitorUtil.GetMonitor(mainHwnd).deviceId == selectedMonitor.deviceId)
        {
            isMainTaskbarSelected = true;
            return mainHwnd;
        }

        if (monitors.Count == 1)
            return IntPtr.Zero;

        IntPtr secondHwnd = IntPtr.Zero;
        StringBuilder className = new(256); // 256 is the maximum class name length
        IntPtr checkWindowClass(IntPtr wnd)
        {
            var len = GetClassName(wnd, className, className.Capacity);
            if (len > 0 && className.Equals("Shell_SecondaryTrayWnd"))
            {
                if (MonitorUtil.GetMonitor(wnd).deviceId == selectedMonitor.deviceId)
                {
                    return wnd;
                }
            }
            return IntPtr.Zero;
        }

        // First check windows in the main taskbar's thread (fast path).
        if (mainHwnd != IntPtr.Zero)
        {
            uint threadId = GetWindowThreadProcessId(mainHwnd, IntPtr.Zero);
            EnumThreadWindows(threadId, (wnd, param) =>
            {
                secondHwnd = checkWindowClass(wnd);
                if (secondHwnd != IntPtr.Zero)
                    return false; // stop

                return true;
            }, IntPtr.Zero);

            if (secondHwnd != IntPtr.Zero)
                return secondHwnd;
        }

        // Fallback: enumerate all top-level windows to find Shell_SecondaryTrayWnd on this monitor.
        EnumWindows((wnd, param) =>
        {
            secondHwnd = checkWindowClass(wnd);
            if (secondHwnd != IntPtr.Zero)
                return false; // stop

            return true;
        }, IntPtr.Zero);

        return secondHwnd;
    }

    private void SetupWindow()
    {
        try
        {
            var interop = new WindowInteropHelper(this);
            IntPtr taskbarWindowHandle = interop.Handle;

            IntPtr taskbarHandle = GetSelectedTaskbarHandle(out bool isMainTaskbarSelected);
            ResetTaskbarCachesIfHandleChanged(taskbarHandle);

            if (taskbarHandle == IntPtr.Zero)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            // This prevents the window from trying to float above the taskbar as a separate entity
            int style = GetWindowLong(taskbarWindowHandle, GWL_STYLE);
            style = (style & ~WS_POPUP) | WS_CHILD;
            SetWindowLong(taskbarWindowHandle, GWL_STYLE, style);

            SetParent(taskbarWindowHandle, taskbarHandle);

            CalculateAndSetPosition(taskbarHandle, taskbarWindowHandle, isMainTaskbarSelected);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during setup");
        }
    }

    private void UpdateWindowRegion(IntPtr windowHandle, params Rect[] rects)
    {
        IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
        foreach (var r in rects)
        {
            // make sure rect is not empty - happens when setting elements to collapsed
            if (r == Rect.Empty)
                continue;

            IntPtr newRgn = CreateRectRgn((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom);
            if (newRgn == IntPtr.Zero)
            {
                Logger.Error($"Taskbar Widget error during CreateRectRgn({(int)r.Left}, {(int)r.Top}, {(int)r.Right}, {(int)r.Bottom}).");
                goto on_error;
            }

            if (CombineRgn(rgn, rgn, newRgn, 2 /*RGN_OR*/) == 0)
            {
                Logger.Error($"Taskbar Widget error during CombineRgn. Combined regions: {string.Join(", ", rects.Select(i => $"RECT({(int)i.Left}, {(int)i.Top}, {(int)i.Right}, {(int)i.Bottom})"))}");
                DeleteObject(newRgn);
                goto on_error;
            }

            DeleteObject(newRgn);
        }

        if (SetWindowRgn(windowHandle, rgn, true) == 0)
        {
            Logger.Error($"Taskbar Widget error during SetWindowRgn.");
            goto on_error;
        }

        return;

on_error:

        // All regions that were not sent without errors to SetWindowRgn must be destroyed manually
        DeleteObject(rgn);
        if (SetWindowRgn(windowHandle, IntPtr.Zero, true) == 0)
            Logger.Error("Taskbar Widget error during window region reset.");
    }

    public void UpdatePosition()
    {
        if (_isClosing || MainWindow.ExplorerRestarting)
        {
            // Explorer is restarting -- do NOTHING
            return;
        }

        // Check premium status before allowing widget to be displayed
        if (!MonitorSettings.TaskbarWidgetEnabled || !SettingsManager.Current.IsPremiumUnlocked)
        {
            HideTaskbarWindow();
            return;
        }

        try
        {
            var interop = new WindowInteropHelper(this);
            IntPtr taskbarHandle = GetSelectedTaskbarHandle(out bool isMainTaskbarSelected);
            ResetTaskbarCachesIfHandleChanged(taskbarHandle);

            if (interop.Handle == IntPtr.Zero)
            {
                if (MainWindow.ExplorerRestarting)
                {
                    Logger.Info("Skipping TaskbarWindow recovery during Explorer restart");
                    return;
                }

                _timer.Stop();

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _mainWindow?.RecreateTaskbarWindow();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to signal MainWindow to recover Taskbar Widget window");
                    }
                }, DispatcherPriority.Background);

                return;
            }

            if (taskbarHandle == IntPtr.Zero)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            // Ensure WS_CHILD is set before parenting into the taskbar
            int style = GetWindowLong(interop.Handle, GWL_STYLE);
            if ((style & WS_CHILD) == 0)
            {
                style = (style & ~WS_POPUP) | WS_CHILD;
                SetWindowLong(interop.Handle, GWL_STYLE, style);
            }

            // If the Taskbar was not found during initialization or another taskbar was selected,
            // then we need to set the Taskbar as the Parent here.
            if (GetParent(interop.Handle) != taskbarHandle)
            {
                SetParent(interop.Handle, taskbarHandle);
            }

            if (taskbarHandle != IntPtr.Zero && interop.Handle != IntPtr.Zero)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    CalculateAndSetPosition(taskbarHandle, interop.Handle, isMainTaskbarSelected);
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during position update");
        }
    }

    private void ResetTaskbarCachesIfHandleChanged(IntPtr taskbarHandle)
    {
        if (_lastTaskbarHandle == taskbarHandle)
            return;

        _lastTaskbarHandle = taskbarHandle;
        _trayHandle = IntPtr.Zero;
        _widgetElement = null;
        _trayElement = null;
        _taskbarFrameElement = null;
        _pendingAutomationTasks.Clear();
    }

    private void CalculateAndSetPosition(IntPtr taskbarHandle, IntPtr taskbarWindowHandle, bool isMainTaskbarSelected)
    {
        // Prevent overlapping updates - if a previous update is still running
        // (e.g. waiting for an automation query timeout), skip this tick.
        if (_positionUpdateInProgress)
            return;
        _positionUpdateInProgress = true;

        try
        {
            // get DPI scaling
            double dpiScale = GetDpiForWindow(taskbarHandle) / 96.0;

            // Guard against invalid DPI (e.g. during explorer restart when handle is stale)
            if (dpiScale <= 0)
                return;

            // Get Taskbar dimensions
            RECT taskbarRect;

            if (!SettingsManager.Current.LegacyTaskbarWidthEnabled)
            {
                // first, try to find the Taskbar.TaskbarFrame element in the XAML
                // this should give us the actual bounds of the taskbar, excluding invisible margins on some Windows configurations
                (bool success, Rect result) = GetTaskbarFrameRect(taskbarHandle);
                if (success)
                {
                    taskbarRect = new RECT
                    {
                        Left = (int)result.Left,
                        Top = (int)result.Top,
                        Right = (int)result.Right,
                        Bottom = (int)result.Bottom
                    };
                }
                else
                {
                    // fallback to GetWindowRect if we fail to get the frame bounds for some reason
                    GetWindowRect(taskbarHandle, out taskbarRect);
                }
            }
            else
            {
                // legacy method - GetWindowRect on the entire taskbar, which includes invisible margins on some Windows configurations
                GetWindowRect(taskbarHandle, out taskbarRect);
            }

            int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
            int taskbarWidth = taskbarRect.Right - taskbarRect.Left;

            // Vertical taskbar support: rotate and reposition widget when taskbar is taller than wide
            bool isVertical = taskbarHeight > taskbarWidth;
            double taskbarCrossSize = (isVertical ? taskbarWidth : taskbarHeight) / dpiScale;
            bool isSmallTaskbar = taskbarCrossSize < SmallTaskbarDetectionThreshold;
            int containerWidth = taskbarWidth;
            int containerHeight = taskbarHeight;

            Widget.SetSmallTaskbarMode(isSmallTaskbar);
            TaskbarVisualizer.SetSmallTaskbarMode(isSmallTaskbar);
            Lyrics.SetSmallTaskbarMode(isSmallTaskbar);

            // Following SetWindowPos will set the position relative to the parent window,
            // so those coordinates need to be converted.
            POINT containerPos = new() { X = taskbarRect.Left, Y = taskbarRect.Top };
            ScreenToClient(taskbarHandle, ref containerPos);

            if (!MonitorSettings.TaskbarWidgetEnabled || !SettingsManager.Current.IsPremiumUnlocked)
            {
                WidgetCanvas.Visibility = Visibility.Collapsed;
                UpdateWindowRegion(taskbarWindowHandle);
                return;
            }

            WidgetCanvas.Visibility = Visibility.Visible;

            // Apply using SetWindowPos (Bypassing WPF layout engine).
            // HWND_TOP keeps this child window at the top of the taskbar's child z-order:
            // Explorer's taskbar XAML content bridge (Windows.UI.Composition.DesktopWindowContentBridge)
            // spans the whole taskbar and otherwise ends up above this window, hiding the widget.
            // Do NOT pass SWP_NOZORDER here, it would turn hWndInsertAfter into a no-op.
            SetWindowPos(taskbarWindowHandle, HWND_TOP,
                     containerPos.X, containerPos.Y,
                     containerWidth, containerHeight,
                     SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | SWP_SHOWWINDOW);
            double? iconsLeftRel = null;
            if (!isVertical)
            {
                // physical px, taskbar-relative left edge of the pinned/running app
                // icons — the boundary our widget chain must not cross
                (bool iconsOk, Rect iconsRect) = GetTaskbarIconsAreaRect(taskbarHandle);
                if (iconsOk)
                    iconsLeftRel = iconsRect.Left - taskbarRect.Left;
            }
            var wRect = PositionWidget(taskbarHandle, taskbarRect, dpiScale, isMainTaskbarSelected, isVertical, Lyrics.HasContent, iconsLeftRel);
            var vRect = PositionVisualizer(taskbarHandle, taskbarRect, dpiScale, isMainTaskbarSelected, isVertical);
            var lRect = PositionLyrics(taskbarHandle, taskbarRect, dpiScale, isMainTaskbarSelected, isVertical);

            UpdateWindowRegion(taskbarWindowHandle, wRect, vRect, lRect);

            _lastSelectedMonitor = MonitorIndex;
        }
        finally
        {
            _positionUpdateInProgress = false;
        }
    }

    private Rect PositionWidget(IntPtr taskbarHandle, RECT taskbarRect, double dpiScale, bool isMainTaskbarSelected, bool isVertical, bool lyricsVisible, double? iconsLeftRel = null)
    {
        var settings = MonitorSettings;
        if (!settings.TaskbarWidgetEnabled)
            return Rect.Empty;

        Widget.SetVerticalMode(isVertical);

        // When the widget+visualizer+lyrics chain would run into the pinned/running
        // app icons, narrow the widget only (the cap shrinks its song-text area in
        // CalculateSize); the visualizer and lyrics keep their widths.
        Widget.WidthCap = ComputeIconsOverlapCap(taskbarRect, dpiScale, settings, isVertical, lyricsVisible, iconsLeftRel);

        // Calculate widget size
        var (logicalWidth, logicalHeight) = Widget.CalculateSize(dpiScale);

        int physicalWidth = (int)(logicalWidth * dpiScale * _scale);
        int physicalHeight = (int)(logicalHeight * dpiScale);

        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;

        // Apply orientation transform
        Widget.LayoutTransform = isVertical ? new System.Windows.Media.RotateTransform(90) : null;
        Widget.RenderTransform = System.Windows.Media.Transform.Identity;

        int primarySize = isVertical ? taskbarHeight : taskbarWidth;
        int crossSize = isVertical ? taskbarWidth : taskbarHeight;

        // Center on the cross axis; both orientations use physicalHeight for the cross dimension
        int crossPos = (crossSize - physicalHeight) / 2;

        // Primary axis position (calculated per-case below)
        int primaryPos = 0;

        switch (settings.TaskbarWidgetPosition)
        {
            case 0: // near start (left for horizontal, top for vertical)
                primaryPos = 20;

                if (settings.TaskbarVisualizerEnabled && settings.TaskbarVisualizerPosition == 0)
                    primaryPos += (int)(TaskbarVisualizer.Width * dpiScale) + 4;

                // lyrics sit on the outer side of the visualizer chain (same side rules)
                if (lyricsVisible && settings.TaskbarVisualizerPosition == 0)
                    primaryPos += (int)(Lyrics.Width * dpiScale) + 4;

                if (!settings.TaskbarWidgetPadding)
                    break;

                // automatic widget padding to the start
                try
                {
                    // find widget button in XAML
                    (bool found, Rect nativeWidgetRect) = GetTaskbarWidgetRect(taskbarHandle);

                    // Accept only if the native Widgets button is in the start half of the taskbar
                    bool inStartHalf = isVertical
                        ? nativeWidgetRect.Bottom < (taskbarRect.Top + taskbarRect.Bottom) / 2.0
                        : nativeWidgetRect.Right < (taskbarRect.Left + taskbarRect.Right) / 2.0;

                    if (found && inStartHalf)
                    {
                        // Convert absolute screen position to relative position within taskbar
                        primaryPos = isVertical
                            ? (int)(nativeWidgetRect.Bottom - taskbarRect.Top) + 2
                            : (int)(nativeWidgetRect.Right - taskbarRect.Left) + 2;
                    }
                }
                catch (Exception ex)
                {
                    // fallback to default padding
                    Logger.Warn(ex, "Failed to get Widgets button position.");
                    primaryPos += _nativeWidgetsPadding + 2;
                }
                break;

            case 1: // center of the taskbar
                primaryPos = (primarySize - physicalWidth) / 2;

                if (settings.TaskbarVisualizerEnabled)
                    if (settings.TaskbarVisualizerPosition == 0)
                        primaryPos += (int)(TaskbarVisualizer.Width * dpiScale) / 2 + 4;
                    else
                        primaryPos -= (int)(TaskbarVisualizer.Width * dpiScale) / 2 - 4;

                if (lyricsVisible)
                    if (settings.TaskbarVisualizerPosition == 0)
                        primaryPos += (int)(Lyrics.Width * dpiScale) / 2 + 4;
                    else
                        primaryPos -= (int)(Lyrics.Width * dpiScale) / 2 + 4;
                break;

            case 2: // near end (right for horizontal, bottom for vertical)
                try
                {
                    if (settings.TaskbarVisualizerEnabled && settings.TaskbarVisualizerPosition == 1)
                        primaryPos -= (int)(TaskbarVisualizer.Width * dpiScale) - 4;

                    if (lyricsVisible && settings.TaskbarVisualizerPosition == 1)
                        primaryPos -= (int)(Lyrics.Width * dpiScale) + 4;

                    // Horizontal only: try to position next to native Widgets button on the end side
                    if (!isVertical && settings.TaskbarWidgetPadding)
                    {
                        try
                        {
                            // find widget button in XAML
                            (bool found, Rect nativeWidgetRect) = GetTaskbarWidgetRect(taskbarHandle);

                            // make sure it's on the right side, otherwise ignore (widget might be to the left)
                            if (found && nativeWidgetRect.Left > (taskbarRect.Left + taskbarRect.Right) / 2.0)
                            {
                                // Convert absolute screen position to relative position within taskbar
                                primaryPos += (int)(nativeWidgetRect.Left - taskbarRect.Left) - 1 - physicalWidth;
                                break;
                            }
                        }
                        catch (Exception ex) // catch exception when getting widget position
                        {
                            Logger.Warn(ex, "Failed to get Widgets button position.");
                        }
                    }

                    // try to position next to system tray
                    if (!isMainTaskbarSelected)
                    {
                        // find secondary tray with automation
                        (bool found, Rect trayRect) = GetSystemTrayRect(taskbarHandle);

                        if (found)
                        {
                            // Convert absolute screen position to relative position within taskbar
                            double trayOffset = isVertical
                                ? trayRect.Top - taskbarRect.Top
                                : trayRect.Left - taskbarRect.Left;
                            primaryPos += (int)trayOffset - physicalWidth - (isVertical ? 2 : 1);
                            break;
                        }
                    }
                    else
                    {
                        // Primary taskbar: for vertical, try automation first (more reliable on ExplorerPatcher)
                        if (isVertical)
                        {
                            (bool trayFound, Rect trayAutomationRect) = GetSystemTrayRect(taskbarHandle);
                            if (trayFound && trayAutomationRect.Top >= taskbarRect.Top)
                            {
                                primaryPos += (int)(trayAutomationRect.Top - taskbarRect.Top) - physicalWidth - 2;
                                break;
                            }
                        }

                        // Primary taskbar: TrayNotifyWnd (original approach for horizontal, fallback for vertical)
                        if (_trayHandle == IntPtr.Zero || _lastSelectedMonitor != MonitorIndex)
                            _trayHandle = FindWindowEx(taskbarHandle, IntPtr.Zero, "TrayNotifyWnd", null);

                        if (_trayHandle != IntPtr.Zero)
                        {
                            GetWindowRect(_trayHandle, out RECT trayWndRect);
                            // Convert absolute screen position to relative position within taskbar
                            double trayOffset = isVertical
                                ? trayWndRect.Top - taskbarRect.Top
                                : trayWndRect.Left - taskbarRect.Left;

                            // For vertical: validate the tray is in the lower half of the taskbar
                            if (!isVertical || trayOffset > taskbarHeight / 2)
                            {
                                primaryPos += (int)trayOffset - physicalWidth - (isVertical ? 2 : 6); // trayOffset isn't 100% accurate, so we subtract a few pixels
                                break;
                            }
                        }
                        else if (!isVertical)
                        {
                            // TrayNotifyWnd not found on horizontal: fallback to right alignment,
                            // since we are aligning to the right side and know the size of the taskbar.
                            primaryPos += taskbarWidth - physicalWidth - 20;
                            break;
                        }
                    }

                    // Final fallback: place near the end of the taskbar
                    primaryPos += primarySize - physicalWidth - 20;
                }
                catch (Exception ex)
                {
                    // Fallback to left alignment
                    Logger.Warn(ex, "Failed to get System Tray position.");
                    primaryPos = isVertical ? primarySize - physicalWidth - 20 : 20;
                }
                break;
        }

        primaryPos += settings.TaskbarWidgetManualPadding;

        // Set widget position within canvas
        // primaryPos → left (horizontal) or top (vertical); crossPos → top (horizontal) or left (vertical)
        Canvas.SetLeft(Widget, (isVertical ? crossPos : primaryPos) / dpiScale);
        Canvas.SetTop(Widget, (isVertical ? primaryPos : crossPos) / dpiScale);
        Widget.Width = physicalWidth / dpiScale;
        Widget.Height = physicalHeight / dpiScale;

        // After 90° LayoutTransform the visual bounding rect has swapped dimensions
        double rectW = isVertical ? physicalHeight : physicalWidth;
        double rectH = isVertical ? physicalWidth : physicalHeight;
        return new Rect(Canvas.GetLeft(Widget) * dpiScale, Canvas.GetTop(Widget) * dpiScale, rectW, rectH);
    }

    /// <summary>
    /// Widest logical width the widget may take so the widget+visualizer+lyrics chain
    /// stays clear of the pinned/running app icons. Null when the icons area is unknown,
    /// the taskbar is vertical, or the widget is end-aligned (its own tray alignment
    /// governs that case). Start position: the chain begins at a known offset, so the
    /// allowed width is boundary − chainStart − chainAfter. Center position: the widget
    /// is centered, so solving chainEnd(w) ≤ boundary gives w ≤ 2·boundary − taskbarWidth
    /// − vizWidth − lyricsWidth − margins.
    /// </summary>
    private double? ComputeIconsOverlapCap(RECT taskbarRect, double dpiScale, MonitorTaskbarSettings settings, bool isVertical, bool lyricsVisible, double? iconsLeftRel)
    {
        if (isVertical || iconsLeftRel is not double boundaryRel)
            return null;
        if (settings.TaskbarWidgetPosition is not (0 or 1))
            return null;

        double boundary = boundaryRel - 12; // physical px safety gap
        double taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        double vizPhys = settings.TaskbarVisualizerEnabled ? TaskbarVisualizer.Width * dpiScale : 0;
        double lyrPhys = lyricsVisible ? Lyrics.Width * dpiScale : 0;

        double allowedPhys;
        if (settings.TaskbarWidgetPosition == 0)
        {
            double chainStart = 20 + settings.TaskbarWidgetManualPadding;
            // approximate widgets-button padding (exact button query would need another
            // automation pass; the safety gap absorbs the difference)
            if (settings.TaskbarWidgetPadding)
                chainStart += _nativeWidgetsPadding + 2;
            bool vizBefore = settings.TaskbarVisualizerEnabled && settings.TaskbarVisualizerPosition == 0;
            bool lyrBefore = lyricsVisible && settings.TaskbarVisualizerPosition == 0;
            if (vizBefore) chainStart += vizPhys + 4;
            if (lyrBefore) chainStart += lyrPhys + 4;

            double chainAfter = 0;
            if (settings.TaskbarVisualizerEnabled && settings.TaskbarVisualizerPosition == 1) chainAfter += vizPhys + 4;
            if (lyricsVisible && settings.TaskbarVisualizerPosition == 1) chainAfter += lyrPhys + 4;

            allowedPhys = boundary - chainStart - chainAfter;
        }
        else
        {
            bool vizRight = settings.TaskbarVisualizerEnabled && settings.TaskbarVisualizerPosition == 1;
            bool lyrRight = lyricsVisible && settings.TaskbarVisualizerPosition == 1;
            double centerExtras = (vizRight ? vizPhys + 8 : 0) + (lyrRight ? lyrPhys + 8 : 0)
                + (!vizRight && settings.TaskbarVisualizerEnabled ? vizPhys : 0)
                + (!lyrRight && lyricsVisible ? lyrPhys : 0);
            // manual padding shifts the centered group; each padding px moves the
            // chain end one px right, and centering halves it back, so 2× total
            allowedPhys = 2 * (boundary - settings.TaskbarWidgetManualPadding) - taskbarWidth - centerExtras;
        }

        double allowedLogical = allowedPhys / (dpiScale * _scale);
        return Math.Max(allowedLogical, 120); // never crush the widget below a usable minimum
    }

    private Rect PositionVisualizer(IntPtr taskbarHandle, RECT taskbarRect, double dpiScale, bool isMainTaskbarSelected, bool isVertical)
    {
        var settings = MonitorSettings;
        if (!settings.TaskbarVisualizerEnabled)
            return Rect.Empty;

        // Rotate visualizer 90° on vertical taskbar so it fits the slim width
        TaskbarVisualizer.LayoutTransform = isVertical ? new System.Windows.Media.RotateTransform(90) : null;

        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;

        int crossSize = isVertical ? taskbarWidth : taskbarHeight;
        int crossOffset = isVertical ? 0 : -1; // -1 aligns with native taskbar elements on horizontal
        int crossPos = (crossSize - (int)(TaskbarVisualizer.Height * dpiScale)) / 2 + crossOffset;

        double widgetPrimaryStart = isVertical ? Canvas.GetTop(Widget) : Canvas.GetLeft(Widget);
        int primaryPos;

        switch (settings.TaskbarVisualizerPosition)
        {
            case 0: // before widget (left for horizontal, above for vertical)
                primaryPos = (int)(widgetPrimaryStart * dpiScale) - (int)(TaskbarVisualizer.Width * dpiScale);
                break;

            case 1: // after widget (right for horizontal, below for vertical)
                // Widget.Width holds the logical width; after 90° rotation its visual height = Widget.Width * dpiScale
                primaryPos = (int)(widgetPrimaryStart * dpiScale) + (int)(Widget.Width * dpiScale);
                break;

            default:
                primaryPos = 0;
                break;
        }

        // Set visualizer position within canvas
        // primaryPos → left (horizontal) or top (vertical); crossPos → top (horizontal) or left (vertical)
        Canvas.SetLeft(TaskbarVisualizer, (isVertical ? crossPos : primaryPos) / dpiScale);
        Canvas.SetTop(TaskbarVisualizer, (isVertical ? primaryPos : crossPos) / dpiScale);

        // After 90° LayoutTransform the visual bounding rect has swapped dimensions
        double rectW = isVertical ? TaskbarVisualizer.Height * dpiScale : TaskbarVisualizer.Width * dpiScale;
        double rectH = isVertical ? TaskbarVisualizer.Width * dpiScale : TaskbarVisualizer.Height * dpiScale;
        return new Rect(Canvas.GetLeft(TaskbarVisualizer) * dpiScale, Canvas.GetTop(TaskbarVisualizer) * dpiScale, rectW, rectH);
    }

    private Rect PositionLyrics(IntPtr taskbarHandle, RECT taskbarRect, double dpiScale, bool isMainTaskbarSelected, bool isVertical)
    {
        var settings = MonitorSettings;
        if (!settings.LyricsEnabled || !Lyrics.HasContent)
            return Rect.Empty;

        // rotate like the visualizer on vertical taskbars
        Lyrics.LayoutTransform = isVertical ? new System.Windows.Media.RotateTransform(90) : null;

        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;

        int crossSize = isVertical ? taskbarWidth : taskbarHeight;
        int crossOffset = isVertical ? 0 : -1;
        int crossPos = (crossSize - (int)(Lyrics.Height * dpiScale)) / 2 + crossOffset;

        // lyrics sit on the outer side of the visualizer chain: [lyrics][viz][widget]
        // or [widget][viz][lyrics]; when the visualizer is disabled, lyrics takes its slot
        double widgetPrimaryStart = isVertical ? Canvas.GetTop(Widget) : Canvas.GetLeft(Widget);
        bool onStartSide = settings.TaskbarVisualizerPosition == 0;

        int lyrWidth = (int)(Lyrics.Width * dpiScale);
        bool vizEnabled = settings.TaskbarVisualizerEnabled;
        int widgetExtent = (int)(Widget.Width * dpiScale);

        int primaryPos;
        if (onStartSide)
        {
            int vizSlot = vizEnabled ? (int)(TaskbarVisualizer.Width * dpiScale) + 4 : 0;
            primaryPos = (int)(widgetPrimaryStart * dpiScale) - 4 - vizSlot - lyrWidth;
        }
        else
        {
            int vizSlot = vizEnabled ? (int)(TaskbarVisualizer.Width * dpiScale) : 0;
            primaryPos = (int)(widgetPrimaryStart * dpiScale) + widgetExtent + vizSlot + 4;
        }

        Canvas.SetLeft(Lyrics, (isVertical ? crossPos : primaryPos) / dpiScale);
        Canvas.SetTop(Lyrics, (isVertical ? primaryPos : crossPos) / dpiScale);

        double rectW = isVertical ? Lyrics.Height * dpiScale : Lyrics.Width * dpiScale;
        double rectH = isVertical ? Lyrics.Width * dpiScale : Lyrics.Height * dpiScale;
        return new Rect(Canvas.GetLeft(Lyrics) * dpiScale, Canvas.GetTop(Lyrics) * dpiScale, rectW, rectH);
    }

    public void UpdateUi(string title, string artist, BitmapImage? icon, GlobalSystemMediaTransportControlsSessionPlaybackStatus? playbackStatus, GlobalSystemMediaTransportControlsSessionPlaybackControls? playbackControls = null)
    {
        var settings = MonitorSettings;
        // Check premium status - hide widget if not unlocked
        if (!settings.TaskbarWidgetEnabled || !SettingsManager.Current.IsPremiumUnlocked)
        {
            HideTaskbarWindow();
            return;
        }

        Dispatcher.Invoke(() =>
        {
            WidgetCanvas.Visibility = Visibility.Visible;
        });

        // Autohide - Widget hides when playback is paused
        _lastPlaybackStatus = playbackStatus;

        if (settings.TaskbarWidgetAutoHide)
        {
            if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                _autoHideTimer?.Stop();
                _autoHideTimer = null;

                Dispatcher.Invoke(() =>
                {
                    Visibility = Visibility.Visible;
                });
            }
            else
            {
                // Start delayed hide
                if (_autoHideTimer == null)
                {
                    var localTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(750)
                    };

                    localTimer.Tick += (s, e) =>
                    {
                        localTimer.Stop();
                        if (_autoHideTimer == localTimer)
                            _autoHideTimer = null;

                        if (_lastPlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            Dispatcher.Invoke(Widget.SlideOutThenCollapse);
                        }
                    };

                    _autoHideTimer = localTimer;
                    localTimer.Start();
                }
            }
        }

        if (!_timer.IsEnabled)
            _timer.Start();

        // Delegate UI update to widget control
        Widget.UpdateUi(title, artist, icon, playbackStatus, playbackControls);

        // Update position after UI change
        Dispatcher.BeginInvoke(() => UpdatePosition(), DispatcherPriority.Background);

        Dispatcher.Invoke(() =>
        {
            if (GetSelectedTaskbarHandle(out _) != IntPtr.Zero)
                Visibility = Visibility.Visible;
        });
    }

    public void RefreshAppVolumeTooltip()
    {
        Widget.RefreshAppVolumeTooltip();
    }

    private (bool, Rect) GetTaskbarXamlElementRect(IntPtr taskbarHandle, ref AutomationElement? elementCache, string elementName)
    {
        if (taskbarHandle == IntPtr.Zero)
            return (false, Rect.Empty);

        try
        {
            // reset if monitor changed
            if (_lastSelectedMonitor != MonitorIndex)
                elementCache = null;

            // find widget in XAML
            if (elementCache == null)
            {
                if (_pendingAutomationTasks.TryGetValue(elementName, out var pendingTask) && !pendingTask.IsCompleted)
                    return (false, Rect.Empty);

                AutomationElement? found = null;
                var findTask = Task.Run(() =>
                {
                    var root = AutomationElement.FromHandle(taskbarHandle);
                    found = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, elementName));
                });
                _pendingAutomationTasks[elementName] = findTask;

                if (!findTask.Wait(1000))
                {
                    Logger.Warn("Timeout querying taskbar XAML element: " + elementName);
                    return (false, Rect.Empty);
                }

                // Propagate any exception from the background thread
                findTask.GetAwaiter().GetResult();
                elementCache = found;
            }

            if (elementCache == null) // widget most likely disabled
                return (false, Rect.Empty);

            try
            {
                if (_pendingAutomationTasks.TryGetValue(elementName, out var pendingTask) && !pendingTask.IsCompleted)
                {
                    elementCache = null;
                    return (false, Rect.Empty);
                }

                var cachedElement = elementCache;
                var boundsTask = Task.Run(() => cachedElement.Current.BoundingRectangle);
                _pendingAutomationTasks[elementName] = boundsTask;

                if (!boundsTask.Wait(500))
                {
                    Logger.Warn("Timeout getting bounds for taskbar XAML element: " + elementName);
                    elementCache = null;
                    return (false, Rect.Empty);
                }

                Rect elementRect = boundsTask.GetAwaiter().GetResult();

                if (elementRect == Rect.Empty) // widget shown before but most likely disabled now
                {
                    elementCache = null; // reset cache
                    return (false, Rect.Empty);
                }

                return (true, elementRect);
            }
            catch (ElementNotAvailableException)
            {
                // element became stale, reset cache
                Logger.Warn("Taskbar XAML element became stale, resetting cache: " + elementName);
                elementCache = null;
                return (false, Rect.Empty);
            }
        }
        catch (COMException ex)
        {
            Logger.Warn(ex, "COM error retrieving taskbar XAML element Rect: " + elementName);
            elementCache = null; // reset cache on error
            return (false, Rect.Empty);
        }
        catch (ElementNotAvailableException)
        {
            Logger.Warn("Taskbar XAML element not available, resetting cache: " + elementName);
            elementCache = null;
            return (false, Rect.Empty);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error retrieving taskbar XAML element Rect: " + elementName);
            elementCache = null; // reset cache on error
            return (false, Rect.Empty);
        }
    }

    private (bool, Rect) GetTaskbarWidgetRect(IntPtr taskbarHandle)
    {
        return GetTaskbarXamlElementRect(taskbarHandle, ref _widgetElement, "WidgetsButton");
    }

    /// <summary>
    /// Union rect of the taskbar's pinned/running app buttons plus Start and Search
    /// (the "icons" a growing widget chain must not overlap). Cached for 10 s — the
    /// union query is heavier than a single-element lookup and the icons move rarely.
    /// Screen coordinates in physical pixels.
    /// </summary>
    private (bool success, Rect rect) GetTaskbarIconsAreaRect(IntPtr taskbarHandle)
    {
        if (taskbarHandle == IntPtr.Zero)
            return (false, Rect.Empty);

        if ((DateTime.Now - _iconsAreaCacheTime).TotalSeconds < 10)
        {
            bool cached = !_iconsAreaRectCache.IsEmpty;
            return (cached, _iconsAreaRectCache);
        }

        try
        {
            if (_pendingAutomationTasks.TryGetValue("TaskbarIcons", out var pendingTask) && !pendingTask.IsCompleted)
                return (false, Rect.Empty);

            Rect? found = null;
            var findTask = Task.Run(() =>
            {
                _iconsAreaElement ??= AutomationElement.FromHandle(taskbarHandle);
                var condition = new OrCondition(
                    new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer"),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SearchButton"));
                var buttons = _iconsAreaElement.FindAll(TreeScope.Descendants, condition);
                double minLeft = double.MaxValue, maxRight = double.MinValue;
                foreach (AutomationElement button in buttons)
                {
                    Rect r = button.Current.BoundingRectangle;
                    if (r.IsEmpty || r.Width <= 0)
                        continue;
                    minLeft = Math.Min(minLeft, r.Left);
                    maxRight = Math.Max(maxRight, r.Right);
                }
                if (minLeft < maxRight)
                    found = new Rect(minLeft, 0, maxRight - minLeft, 1);
            });
            _pendingAutomationTasks["TaskbarIcons"] = findTask;

            if (!findTask.Wait(1000))
                return (false, Rect.Empty);

            findTask.GetAwaiter().GetResult();
            if (found is { } rect)
            {
                _iconsAreaRectCache = rect;
                _iconsAreaCacheTime = DateTime.Now;
                return (true, rect);
            }
            return (false, Rect.Empty);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to measure taskbar icon area");
            return (false, Rect.Empty);
        }
    }

    private (bool, Rect) GetSystemTrayRect(IntPtr taskbarHandle)
    {
        return GetTaskbarXamlElementRect(taskbarHandle, ref _trayElement, "SystemTrayIcon");
    }

    private (bool, Rect) GetTaskbarFrameRect(IntPtr taskbarHandle)
    {
        return GetTaskbarXamlElementRect(taskbarHandle, ref _taskbarFrameElement, "TaskbarFrame");
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        MonitorSettings.PropertyChanged -= MonitorSettings_PropertyChanged;
        _timer.Stop();
        _autoHideTimer?.Stop();
        _autoHideTimer = null;
        _widgetElement = null;
        _trayElement = null;
        _taskbarFrameElement = null;
        _pendingAutomationTasks.Clear();
        base.OnClosed(e);
    }
}
