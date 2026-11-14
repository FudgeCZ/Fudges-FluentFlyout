// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;

namespace FluentFlyoutWPF;

public partial class MainWindow
{
    private const int LocalInputGracePeriodMs = 500;

    private ExternalVolumeMonitor? _externalVolumeMonitor;

    private void InitializeHeadsetControls()
    {
        _externalVolumeMonitor = new ExternalVolumeMonitor(LocalInputGracePeriodMs);
        _externalVolumeMonitor.ExternalVolumeChanged += HeadsetControls_OnExternalVolumeChanged;
    }

    private void DisposeHeadsetControls()
    {
        if (_externalVolumeMonitor != null)
        {
            _externalVolumeMonitor.ExternalVolumeChanged -= HeadsetControls_OnExternalVolumeChanged;
            _externalVolumeMonitor.Dispose();
            _externalVolumeMonitor = null;
        }
    }

    private void HeadsetControls_OnExternalVolumeChanged(object? sender, EventArgs e)
    {
        if (_isCleaningUp || Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (_isCleaningUp || Dispatcher.HasShutdownStarted)
                return;

            if (!SettingsManager.Current.MediaFlyoutVolumeKeysExcluded)
                TryShowMediaFlyoutDebounced();

            ShowVolumeFlyout();
        });
    }
}