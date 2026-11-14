// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyoutWPF.Classes;

public static class WindowHelper
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly ConditionalWeakTable<Window, EventHandler> _sourceInitializedHandlers = new();

    public static void SetTopmost(Window window) // workaround to set window even more topmost
    {
        if (window == null)
            return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        SetWindowPos(handle, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static void SetVisibility(Window window, bool visible) // workaround to set window even more topmost
    {
        if (window == null)
            return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, (uint)(SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | (visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW)));
    }

    public static Rect GetPlacement(Window window) // get the window position, ignoring WPF
    {
        if (window == null)
            return Rect.Empty;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out RECT windowRect))
            return Rect.Empty;

        return new Rect(
            windowRect.Left,
            windowRect.Top,
            windowRect.Right - windowRect.Left,
            windowRect.Bottom - windowRect.Top);
    }

    public static void SetPosition(Window window, double x, double y, bool async = false) // set the position of the window, ignoring WPF
    {
        if (window == null || !double.IsFinite(x) || !double.IsFinite(y))
            return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        int ix = (int)Math.Round(x);
        int iy = (int)Math.Round(y);
        uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | (async ? SWP_ASYNCWINDOWPOS : (uint)0);
        bool result = SetWindowPos(handle, IntPtr.Zero, ix, iy, 0, 0, flags);

        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            Logger.Warn($"SetPosition failed for '{window.GetType().Name}' (HWND=0x{handle.ToInt64():X}, X={ix}, Y={iy}, Flags=0x{flags:X}), Win32Error={error}");
        }

        return;
    }

    public static void SetNoActivate(Window window) // prevent window from stealing focus
    {
        if (window == null)
            return;

        window.ShowActivated = false;

        static void ApplyNoActivateStyle(Window w)
        {
            var helper = new WindowInteropHelper(w);
            if (helper.Handle == IntPtr.Zero)
                return;

            SetWindowLong(helper.Handle, GWL_EXSTYLE, GetWindowLong(helper.Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyNoActivateStyle(window);
            return;
        }

        if (!_sourceInitializedHandlers.TryGetValue(window, out _))
        {
            EventHandler? handler = null;
            handler = (sender, e) =>
            {
                window.SourceInitialized -= handler;
                _sourceInitializedHandlers.Remove(window);
                ApplyNoActivateStyle(window);
            };
            _sourceInitializedHandlers.Add(window, handler);
            window.SourceInitialized += handler;
        }
    }

    // Check if the mouse cursor is currently over the specified window
    // More reliable than WPF's IsMouseOver, it sometimes doesn't detect mouse over the background
    public static bool IsMouseOverWindow(Window window)
    {
        if (window == null || !GetCursorPos(out POINT cursor))
            return false;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect))
            return false;

        return cursor.X >= rect.Left && cursor.X < rect.Right &&
               cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
    }
}