// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static FluentFlyout.Classes.NativeMethods;
namespace FluentFlyout.Classes.Utils;

public static class MediaPlayerData
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private class CachedMediaPlayerInfo
    {
        public required string Title { get; set; }
        public ImageSource? Icon { get; set; }
        public int ProcessId { get; set; }
        public string? ProcessName { get; set; }
        public required DateTime ProcessIdCheckedAt { get; set; }
    }
    // cache for media player info to avoid redundant process lookups
    private static readonly ConcurrentDictionary<string, CachedMediaPlayerInfo> mediaPlayerCache = [];

    // id variants of media players where the key is the mediaPlayerId and the value is the mediaPlayerCache key
    private static readonly ConcurrentDictionary<string, string> mediaPlayerIdVariants = [];

    private static readonly HashSet<string> IgnoredIdSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "com",
        "github",
        "exe"
    };

    private static readonly Lock _processCacheLock = new();
    private static Process[]? cachedProcesses = null;
    private static DateTime lastCacheTime = DateTime.MinValue;
    private const int CACHE_DURATION_SECONDS = 5;

    public static (string, ImageSource?) GetAndCacheMediaPlayerData(string mediaPlayerId)
    {
        if (mediaPlayerCache.TryGetValue(mediaPlayerId, out var cachedInfo)
            || mediaPlayerIdVariants.TryGetValue(mediaPlayerId, out var variantKey)
            && mediaPlayerCache.TryGetValue(variantKey, out cachedInfo))
        {
            return (cachedInfo.Title, cachedInfo.Icon);
        }

        string mediaTitle = mediaPlayerId;
        ImageSource? mediaIcon = null;

        // get sanitized media title name
        string[] mediaSessionIdVariants = mediaPlayerId.Split('.');

        // remove common non-informative segments without corrupting words like "Intercom"
        var variants = mediaSessionIdVariants
            .Select(variant => variant.Trim())
            .Where(variant => !string.IsNullOrWhiteSpace(variant) && !IgnoredIdSegments.Contains(variant))
            .ToList();

        // add original id to the end of the array to ensure at least one variant
        variants.Add(mediaPlayerId);
        if (variants.Any(v => v.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase)
            || v.Equals("MSEdge", StringComparison.OrdinalIgnoreCase)
            || v.EndsWith("!MSEdge", StringComparison.OrdinalIgnoreCase))) variants.Add("msedge");

        string? matchedTitle = null;
        string? matchedPath = null;
        int matchedProcessId = 0;
        string? matchedProcessName = null;
        bool hasProcessMatch = false;

        lock (_processCacheLock)
        {
            // use cache to avoid frequent process enumeration
            if (cachedProcesses == null || (DateTime.UtcNow - lastCacheTime).TotalSeconds > CACHE_DURATION_SECONDS)
            {
                if (cachedProcesses != null)
                {
                    foreach (var oldProcess in cachedProcesses)
                    {
                        try { oldProcess.Dispose(); } catch { }
                    }
                }
                cachedProcesses = Process.GetProcesses();
                lastCacheTime = DateTime.UtcNow;
            }

            var processData = cachedProcesses.Select(p =>
                {
                    try
                    {
                        // pre-filter processes without a main window handle unless they are an exact match to the media player id
                        bool isExactMatch = variants.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase);
                        if (!isExactMatch && p.MainWindowHandle == IntPtr.Zero)
                        {
                            return null;
                        }

                        var mainModule = p.MainModule;
                        if (mainModule == null) return null;

                        string path = mainModule.FileName;

                        if (isExactMatch || variants.Any(v => path.Contains(v, StringComparison.OrdinalIgnoreCase)))
                        {
                            // prioritize the FileDescription for a user-friendly name
                            // fall back to MainWindowTitle if the description is empty
                            string title = !string.IsNullOrWhiteSpace(mainModule.FileVersionInfo.FileDescription)
                                            ? mainModule.FileVersionInfo.FileDescription
                                            : p.MainWindowTitle;

                            return new { Title = title, Path = path, ProcessId = p.Id, ProcessName = p.ProcessName, IsExactMatch = isExactMatch };
                        }
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // silently ignore the exception for inaccessible processes
                    }
                    catch (InvalidOperationException)
                    {
                        // process exited while being inspected
                    }
                    return null;
                })
                .OrderByDescending(data => data != null && data.IsExactMatch)
                .FirstOrDefault(data => data != null); // use first result

            if (processData != null)
            {
                hasProcessMatch = true;
                matchedTitle = processData.Title;
                matchedPath = processData.Path;
                matchedProcessId = processData.ProcessId;
                matchedProcessName = processData.ProcessName;
            }
        }

        if (!hasProcessMatch)
        {
            var (shellTitle, shellIcon) = ResolveViaAppsFolder(mediaPlayerId);
            if (shellTitle == null) return (mediaTitle, mediaIcon);

            mediaPlayerCache[mediaPlayerId] = new CachedMediaPlayerInfo
            {
                Title = shellTitle,
                Icon = shellIcon,
                ProcessId = -1, // no process match, -1 avoids colliding with real PIDs
                ProcessIdCheckedAt = DateTime.UtcNow
            };

            return (shellTitle, shellIcon);
        }

        mediaTitle = !string.IsNullOrWhiteSpace(matchedTitle) ? matchedTitle : mediaPlayerId;

        // check cache again because we have the sanitized title
        if (mediaPlayerCache.TryGetValue(mediaTitle, out cachedInfo))
        {
            // map the original id to the sanitized title for future lookups
            mediaPlayerIdVariants[mediaPlayerId] = mediaTitle;
            return (cachedInfo.Title, cachedInfo.Icon);
        }

        mediaIcon = GetIconFromPath(matchedPath!);

        mediaPlayerCache[mediaPlayerId] = new CachedMediaPlayerInfo
        {
            Title = mediaTitle,
            Icon = mediaIcon,
            ProcessId = matchedProcessId,
            ProcessName = matchedProcessName,
            ProcessIdCheckedAt = DateTime.UtcNow
        };

        return (mediaTitle, mediaIcon);
    }

    private static bool IsProcessAliveAndMatching(int processId, string? expectedProcessName)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return false;
            if (!string.IsNullOrEmpty(expectedProcessName) &&
                !string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int? GetAndCacheProcessId(string mediaPlayerId)
    {
        GetAndCacheMediaPlayerData(mediaPlayerId);

        string cacheKey;
        if (mediaPlayerCache.TryGetValue(mediaPlayerId, out var cachedInfo))
        {
            cacheKey = mediaPlayerId;
        }
        else if (mediaPlayerIdVariants.TryGetValue(mediaPlayerId, out var variantKey)
            && mediaPlayerCache.TryGetValue(variantKey, out cachedInfo))
        {
            cacheKey = variantKey;
        }
        else
        {
            return null;
        }

        if (cachedInfo.ProcessId > 0)
        {
            if (IsProcessAliveAndMatching(cachedInfo.ProcessId, cachedInfo.ProcessName))
                return cachedInfo.ProcessId;
        }
        else if (DateTime.UtcNow - cachedInfo.ProcessIdCheckedAt < TimeSpan.FromSeconds(CACHE_DURATION_SECONDS))
        {
            return null;
        }

        mediaPlayerCache.TryRemove(cacheKey, out _);
        mediaPlayerIdVariants.TryRemove(mediaPlayerId, out _);
        GetAndCacheMediaPlayerData(mediaPlayerId);

        if (!mediaPlayerCache.TryGetValue(mediaPlayerId, out cachedInfo)
            && (!mediaPlayerIdVariants.TryGetValue(mediaPlayerId, out var refreshedVariantKey)
            || !mediaPlayerCache.TryGetValue(refreshedVariantKey, out cachedInfo)))
            return null;

        return IsProcessAliveAndMatching(cachedInfo.ProcessId, cachedInfo.ProcessName)
            ? cachedInfo.ProcessId
            : null;
    }

    public static bool TryActivateMediaPlayer(string mediaPlayerId, string? mediaTitle = null)
    {
        int? processId = GetAndCacheProcessId(mediaPlayerId);
        if (processId is not int pid) return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            IntPtr handle = process.MainWindowHandle;
            if (IsBrowser(process.ProcessName)) return TryActivateBrowserTab(process.ProcessName, mediaTitle);

            if (handle == IntPtr.Zero)
            {
                var candidates = Process.GetProcessesByName(process.ProcessName);
                try
                {
                    foreach (var candidate in candidates)
                    {
                        if (handle == IntPtr.Zero)
                        {
                            handle = candidate.MainWindowHandle;
                        }
                    }
                }
                finally
                {
                    foreach (var candidate in candidates)
                        candidate.Dispose();
                }
            }

            if (handle != IntPtr.Zero)
            {
                if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                return SetForegroundWindow(handle);
            }

            string? path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path) && !IsBrowser(System.IO.Path.GetFileNameWithoutExtension(path)))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to activate media player");
        }

        return false;
    }

    private static bool TryActivateBrowserTab(string processName, string? mediaTitle)
    {
        if (!IsBrowser(processName) || string.IsNullOrWhiteSpace(mediaTitle)) return false;

        var browserProcesses = Process.GetProcessesByName(processName);
        try
        {
            foreach (var browserProcess in browserProcesses)
            {
                try
                {
                    var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                        new AndCondition(
                            new PropertyCondition(AutomationElement.ProcessIdProperty, browserProcess.Id),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)));

                    foreach (AutomationElement window in windows)
                    {
                        IntPtr handle = new(window.Current.NativeWindowHandle);
                        if (handle == IntPtr.Zero) continue;
                        if (IsIconic(handle))
                        {
                            ShowWindow(handle, SW_RESTORE);
                            Thread.Sleep(100);
                        }

                        var tabs = window.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

                        foreach (AutomationElement tab in tabs)
                        {
                            if (!tab.Current.Name.Contains(mediaTitle, StringComparison.OrdinalIgnoreCase)) continue;
                            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
                                ((SelectionItemPattern)pattern).Select();
                            else if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                                ((InvokePattern)pattern).Invoke();
                            else continue;

                            if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
                            return SetForegroundWindow(handle);
                        }
                    }
                }
                catch { }
            }
        }
        finally
        {
            foreach (var browserProcess in browserProcesses)
            {
                browserProcess.Dispose();
            }
        }

        return false;
    }

    private static bool IsBrowser(string processName) =>
        new[] { "chrome", "msedge" }
        .Contains(processName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the associated icon for a given process ID. Returns null if the process is inaccessible.
    /// </summary>
    public static ImageSource? GetAndCacheProcessIcon(int processId, string title)
    {
        try
        {
            if (title == "System sounds") return null;

            // search in cache
            foreach (var item in mediaPlayerCache.Values)
            {
                if (item.ProcessId == processId &&
                    string.Equals(item.Title, title, StringComparison.OrdinalIgnoreCase) &&
                    IsProcessAliveAndMatching(processId, item.ProcessName))
                {
                    return item.Icon;
                }
            }

            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            if (path == null) return null;

            // store in cache for future lookups
            var icon = GetIconFromPath(path);
            if (icon != null)
            {
                mediaPlayerCache[title] = new CachedMediaPlayerInfo
                {
                    Title = title,
                    Icon = icon,
                    ProcessId = processId,
                    ProcessName = process.ProcessName,
                    ProcessIdCheckedAt = DateTime.UtcNow
                };
            }

            return icon;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? GetIconFromPath(string exePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();

            return source;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves an AppUserModelId through the shell Apps folder ("shell:AppsFolder"),
    /// the same way the native media flyout does. Handles apps whose session id
    /// doesn't match their process path, e.g. Firefox-based browsers (#949).
    /// </summary>
    private static (string? Title, ImageSource? Icon) ResolveViaAppsFolder(string appUserModelId)
    {
        object? shellObj = null;
        object? folderObj = null;
        object? itemObj = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return (null, null);

            shellObj = Activator.CreateInstance(shellType);
            if (shellObj == null) return (null, null);
            dynamic shell = shellObj;
            folderObj = shell.NameSpace("shell:AppsFolder");
            if (folderObj == null) return (null, null);
            dynamic folder = folderObj;
            itemObj = folder.ParseName(appUserModelId);
            if (itemObj == null) return (null, null);
            dynamic item = itemObj;

            string name = item.Name;
            if (string.IsNullOrWhiteSpace(name)) return (null, null);

            // desktop apps expose their start menu shortcut target, so the icon can
            // be extracted the same way as everywhere else; packaged apps don't
            // have one and keep a null icon
            var targetPath = item.ExtendedProperty("System.Link.TargetParsingPath") as string;
            ImageSource? icon = targetPath != null ? GetIconFromPath(targetPath) : null;

            return (name, icon);
        }
        catch
        {
            // id is not registered in the apps folder, nothing we can do
            return (null, null);
        }
        finally
        {
            ReleaseComObject(itemObj);
            ReleaseComObject(folderObj);
            ReleaseComObject(shellObj);
        }
    }

    private static void ReleaseComObject(object? comObj)
    {
        if (comObj != null && System.Runtime.InteropServices.Marshal.IsComObject(comObj))
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(comObj);
        }
    }
}