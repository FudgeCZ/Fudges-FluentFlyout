// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace FluentFlyoutWPF.Classes;

public class AudioDeviceMonitor : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static AudioDeviceMonitor? _instance;
    private static readonly object _instanceLock = new();

    private readonly object _lock = new();
    private MMDeviceEnumerator? _deviceEnumerator;
    private AudioDeviceNotificationClient? _notificationClient;
    private volatile bool _disposed;

    public event EventHandler<DefaultDeviceChangedEventArgs>? DefaultDeviceChanged;

    public static AudioDeviceMonitor Instance
    {
        get
        {
            lock (_instanceLock)
            {
                if (_instance == null || _instance._disposed)
                {
                    _instance = new AudioDeviceMonitor();
                }
                return _instance;
            }
        }
    }

    private AudioDeviceMonitor()
    {
        Initialize();
    }

    private void Initialize()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            try
            {
                _deviceEnumerator = new MMDeviceEnumerator();
                _notificationClient = new AudioDeviceNotificationClient();
                _notificationClient.DefaultDeviceChanged += OnDefaultDeviceChanged;
                _deviceEnumerator.RegisterEndpointNotificationCallback(_notificationClient);

                Logger.Info("Audio device monitoring initialized");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize audio device monitoring");
            }
        }
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (_disposed)
            return;

        // Match GetDefaultRenderDevice so other roles cannot redirect audio controls.
        if (e.DataFlow != DataFlow.Render || e.Role != Role.Multimedia)
            return;

        Logger.Info("Default audio output device changed");
        DefaultDeviceChanged?.Invoke(this, e);
    }

    public MMDevice? GetDefaultRenderDevice()
    {
        MMDevice? device = null;
        try
        {
            lock (_lock)
            {
                if (_disposed || _deviceEnumerator == null)
                    return null;

                device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            Logger.Debug("New device: {0}", device);
            return device;
        }
        catch (Exception ex)
        {
            device?.Dispose();
            Logger.Error(ex, "Failed to get default render device");
            return null;
        }
    }

    public MMDevice? GetDeviceById(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        MMDevice? device = null;
        try
        {
            lock (_lock)
            {
                if (_disposed || _deviceEnumerator == null)
                    return null;

                device = _deviceEnumerator.GetDevice(deviceId);
            }
            Logger.Debug("Got device by id {0}: {1}", deviceId, device);
            return device;
        }
        catch (Exception ex)
        {
            device?.Dispose();
            Logger.Error(ex, "Failed to get device by id {0}", deviceId);
            // TODO: we could investigate if returning GetDefaultRenderDevice() works as a fallback for returning null
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_notificationClient != null)
                _notificationClient.DefaultDeviceChanged -= OnDefaultDeviceChanged;

            if (_deviceEnumerator != null && _notificationClient != null)
            {
                try
                {
                    _deviceEnumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to unregister device notification callback");
                }
            }

            if (_deviceEnumerator != null)
            {
                try
                {
                    _deviceEnumerator.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to dispose device enumerator");
                }
                finally
                {
                    _deviceEnumerator = null;
                }
            }

            _notificationClient = null;
        }

        lock (_instanceLock)
        {
            if (ReferenceEquals(_instance, this))
                _instance = null;
        }

        GC.SuppressFinalize(this);
    }
}

// classes to handle audio device notifications
public class AudioDeviceNotificationClient : IMMNotificationClient
{
    public event EventHandler<DefaultDeviceChangedEventArgs>? DefaultDeviceChanged;

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string? defaultDeviceId)
    {
        DefaultDeviceChanged?.Invoke(this, new DefaultDeviceChangedEventArgs(flow, role, defaultDeviceId ?? string.Empty));
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}

public class DefaultDeviceChangedEventArgs(DataFlow dataFlow, Role role, string? deviceId) : EventArgs
{
    public DataFlow DataFlow { get; } = dataFlow;
    public Role Role { get; } = role;
    public string DeviceId { get; } = deviceId ?? string.Empty;
}