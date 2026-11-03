// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;

namespace FluentFlyoutWPF.ViewModels;

/// <summary>
/// Runtime state for update checking (not persisted)
/// </summary>
public partial class UpdateState : ObservableObject
{
    public static UpdateState Current { get; } = new();

    /// <summary>
    /// Whether an update is available
    /// </summary>
    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; set; }

    /// <summary>
    /// The newest version available from the API
    /// </summary>
    [ObservableProperty]
    public partial string NewestVersion { get; set; } = string.Empty;

    /// <summary>
    /// The URL to download the update (if available)
    /// </summary>
    [ObservableProperty]
    public partial string UpdateUrl { get; set; } = string.Empty;

    /// <summary>
    /// Direct download URL of the release asset (empty when the release has no zip)
    /// </summary>
    [ObservableProperty]
    public partial string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether the custom in-app updater can install the update (update available + asset found)
    /// </summary>
    [ObservableProperty]
    public partial bool CanAutoUpdate { get; set; }

    /// <summary>
    /// Timestamp of the last update check
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCheckedText))]
    public partial DateTime LastUpdateCheck { get; set; }

    /// <summary>
    /// Formatted text for the last update check time
    /// </summary>
    public string LastCheckedText => LastUpdateCheck == default
        ? string.Empty
        : LastUpdateCheck.ToString("G");
}