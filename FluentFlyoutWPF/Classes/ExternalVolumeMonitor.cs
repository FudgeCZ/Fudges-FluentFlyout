// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using NAudio.CoreAudioApi;

namespace FluentFlyoutWPF.Classes;

public class ExternalVolumeMonitor : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly int _localInputGracePeriodMs;
    private readonly object _lock = new();
    private MMDevice? _device;
    private float _lastVolume;
    private bool _lastMuted;
    private volatile bool _disposed;

    public event EventHandler? ExternalVolumeChanged;

    public ExternalVolumeMonitor(int localInputGracePeriodMs)
    {
        _localInputGracePeriodMs = localInputGracePeriodMs;

        AudioDeviceMonitor.Instance.DefaultDeviceChanged += OnDefaultDeviceChanged;
        AttachDevice(AudioDeviceMonitor.Instance.GetDefaultRenderDevice());
    }

    private void AttachDevice(MMDevice? device)
    {
        DetachDevice();

        if (device == null)
            return;

        if (_disposed)
        {
            device.Dispose();
            return;
        }

        try
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    device.Dispose();
                    return;
                }

                _lastVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                _lastMuted = device.AudioEndpointVolume.Mute;
                device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                _device = device;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to watch volume of device {0}", device.FriendlyName);
            device.Dispose();
        }
    }

    private void DetachDevice()
    {
        MMDevice? deviceToDispose;
        lock (_lock)
        {
            deviceToDispose = _device;
            _device = null;
        }

        if (deviceToDispose == null)
            return;

        try
        {
            deviceToDispose.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to stop watching device volume");
        }
        finally
        {
            try
            {
                deviceToDispose.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to dispose audio device");
            }
        }
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (_disposed)
            return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        dispatcher.InvokeAsync(() =>
        {
            if (_disposed)
                return;

            var device = string.IsNullOrWhiteSpace(e.DeviceId)
                ? AudioDeviceMonitor.Instance.GetDefaultRenderDevice()
                : AudioDeviceMonitor.Instance.GetDeviceById(e.DeviceId);
            AttachDevice(device);
        });
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        if (_disposed)
            return;

        bool changed;
        lock (_lock)
        {
            if (_disposed || _device == null)
                return;

            // the volume mixer writes the same volume back when syncing
            changed = MathF.Abs(data.MasterVolume - _lastVolume) > 0.001f || data.Muted != _lastMuted;
            _lastVolume = data.MasterVolume;
            _lastMuted = data.Muted;
        }

        if (!changed || LocalInputDetector.HadInputWithin(_localInputGracePeriodMs))
            return;

#if DEBUG
        Logger.Debug("External volume change detected: {0:P0}, muted: {1}", data.MasterVolume, data.Muted);
#endif
        ExternalVolumeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        AudioDeviceMonitor.Instance.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        DetachDevice();

        GC.SuppressFinalize(this);
    }
}