// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;

namespace FluentFlyoutWPF.ViewModels;

/// <summary>
/// ViewModel for the volume mixer, exposing master volume and per-application audio sessions.
/// </summary>
public partial class VolumeMixerViewModel : ObservableObject, IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private MMDevice? _device;
    private DispatcherTimer? _pollTimer;
    private bool _syncingFromDevice;
    private bool _disposed;

    [ObservableProperty]
    public partial float MasterVolume { get; set; }

    [ObservableProperty]
    public partial bool IsMasterMuted { get; set; }

    [ObservableProperty]
    public partial string DeviceName { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public ObservableCollection<AudioSessionModel> Sessions { get; } = [];
    public event EventHandler? SessionVolumeChanged;

    public VolumeMixerViewModel()
    {
        DeviceName = string.Empty;
        AudioDeviceMonitor.Instance.DefaultDeviceChanged += OnDefaultDeviceChanged;

        AttachDevice(AudioDeviceMonitor.Instance.GetDefaultRenderDevice());

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _pollTimer.Tick += OnPollTick;
    }

    public void StartPolling()
    {
        if (_disposed || _pollTimer == null || _pollTimer.IsEnabled) return;
        _pollTimer.Start();
    }

    public void StopPolling()
    {
        if (_pollTimer != null && _pollTimer.IsEnabled)
            _pollTimer.Stop();
    }

    partial void OnIsExpandedChanged(bool oldValue, bool newValue)
    {
        if (oldValue == newValue || !newValue) return;
        RefreshSessions();
    }

    private void AttachDevice(MMDevice? device)
    {
        if (_disposed)
        {
            device?.Dispose();
            return;
        }

        if (!ReferenceEquals(_device, device))
        {
            _device?.Dispose();
            _device = device;
        }

        if (_device == null)
        {
            _syncingFromDevice = true;
            try
            {
                DeviceName = string.Empty;
                MasterVolume = 0f;
                IsMasterMuted = false;
            }
            finally
            {
                _syncingFromDevice = false;
            }
            ClearSessions();
            return;
        }

        try
        {
            DeviceName = _device.FriendlyName;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to read audio device friendly name");
            DeviceName = string.Empty;
        }

        SyncMasterFromDevice();
        RefreshSessions();
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (_disposed) return;

        Logger.Info("Default render device changed, reattaching volume mixer");

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;

        dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            AttachDevice(AudioDeviceMonitor.Instance.GetDeviceById(e.DeviceId));
        });
    }

    private void OnSessionVolumeChanged(object? sender, EventArgs e)
    {
        SessionVolumeChanged?.Invoke(sender, e);
    }

    private void ClearSessions()
    {
        foreach (var session in Sessions)
        {
            session.VolumeChanged -= OnSessionVolumeChanged;
            session.Dispose();
        }

        Sessions.Clear();
    }


    [RelayCommand]
    private void ToggleMasterMute() => IsMasterMuted = !IsMasterMuted;

    [RelayCommand]
    private void OpenVolumeMixer() => IsExpanded = !IsExpanded;

    partial void OnMasterVolumeChanged(float value)
    {
        if (_syncingFromDevice || _disposed || _device == null) return;

        try
        {
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
            IsMasterMuted = value == 0f;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to update master volume");
        }
    }

    partial void OnIsMasterMutedChanged(bool value)
    {
        if (_syncingFromDevice || _disposed || _device == null) return;

        try
        {
            _device.AudioEndpointVolume.Mute = value;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to update master mute state");
        }
    }

    public bool TryAdjustMasterVolume(float delta)
    {
        if (_device == null) return false;

        MasterVolume = Math.Clamp(MasterVolume + delta, 0f, 1f);
        return true;
    }

    /// <summary>
    /// Applies a hardware volume-key step (volume up/down/mute) locally — used when
    /// the keyboard hook swallows the key so the native volume OSD can't appear.
    /// </summary>
    public void ApplyVolumeKey(int vkCode)
    {
        switch (vkCode)
        {
            case 0xAF: TryAdjustMasterVolume(+0.02f); break; // volume up
            case 0xAE: TryAdjustMasterVolume(-0.02f); break; // volume down
            case 0xAD: ToggleMasterMute(); break;            // mute
        }
    }

    public bool TryAdjustSessionVolume(int processId, float delta)
    {
        var session = FindSession(processId);

        if (session == null)
            return false;

        session.AdjustVolume(delta);
        return true;
    }

    public float? GetSessionVolume(int processId)
    {
        return FindSession(processId)?.Volume;
    }

    private AudioSessionModel? FindSession(int processId)
    {
        var session = Sessions.FirstOrDefault(session => session.ProcessId == processId);
        if (session != null)
            return session;

        RefreshSessions();
        return Sessions.FirstOrDefault(session => session.ProcessId == processId);
    }


    public void SyncMasterFromDevice()
    {
        if (_disposed || _device == null) return;

        try
        {
            var vol = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            var mute = _device.AudioEndpointVolume.Mute;

            _syncingFromDevice = true;
            try
            {
                if (MathF.Abs(MasterVolume - vol) > 0.001f)
                    MasterVolume = vol;

                if (IsMasterMuted != mute)
                    IsMasterMuted = mute;
            }
            finally
            {
                _syncingFromDevice = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to sync master volume from device");
        }
    }


    [RelayCommand]
    public void RefreshSessions()
    {
        ClearSessions();

        if (_disposed || _device == null)
            return;

        MMDevice? updatedDevice = null;
        try
        {
            // update device reference because previous _device doesn't have updated sessions
            updatedDevice = AudioDeviceMonitor.Instance.GetDeviceById(_device.ID);
            var deviceForSessions = updatedDevice ?? _device;
            var sessionManager = deviceForSessions.AudioSessionManager;
            var sessions = sessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                bool ownershipTransferred = false;
                try
                {
                    AudioSessionState sessionState = session.State;
                    if (sessionState == AudioSessionState.AudioSessionStateExpired) continue;

                    int pid = (int)session.GetProcessID;

                    string name = pid != 0 ? GetSessionDisplayName(session) : "System sounds";

                    if (name == "FluentFlyout") continue;

                    var icon = MediaPlayerData.GetAndCacheProcessIcon(pid, name);
                    var audioSession = new AudioSessionModel(session, name, pid, sessionState, icon);
                    audioSession.VolumeChanged += OnSessionVolumeChanged;
                    Sessions.Add(audioSession);
                    ownershipTransferred = true;
                }
                finally
                {
                    if (!ownershipTransferred)
                        session.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to enumerate audio sessions");
        }
        finally
        {
            if (updatedDevice != null && !ReferenceEquals(updatedDevice, _device))
                updatedDevice.Dispose();
        }
    }

    private static string GetSessionDisplayName(AudioSessionControl session)
    {
        if (!string.IsNullOrWhiteSpace(session.DisplayName))
            return session.DisplayName;

        try
        {
            uint pid = session.GetProcessID;
            if (pid != 0)
            {
                using var process = Process.GetProcessById((int)pid);
                try
                {
                    var mainModule = process.MainModule;
                    if (mainModule != null && !string.IsNullOrWhiteSpace(mainModule.FileVersionInfo.FileDescription))
                        return mainModule.FileVersionInfo.FileDescription;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected/elevated processes deny MainModule access; fall back to window title or process name
                }

                return process.MainWindowTitle is { Length: > 0 } title
                    ? title
                    : process.ProcessName;
            }
        }
        catch
        {
            // Process may have exited
        }

        return "Unknown";
    }


    public void OnPollTick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        try
        {
            SyncMasterFromDevice();

            foreach (var session in Sessions)
            {
                session.SyncFromDevice();
                //Logger.Trace("Session '{0}' (PID {1}) - Volume: {2}, Muted: {3}, State: {4}",
                //    session.DisplayName, session.ProcessId, session.Volume, session.IsMuted, session.State);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed during volume mixer poll tick");
        }
    }


    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_pollTimer != null)
        {
            _pollTimer.Tick -= OnPollTick;
            _pollTimer.Stop();
            _pollTimer = null;
        }

        AudioDeviceMonitor.Instance.DefaultDeviceChanged -= OnDefaultDeviceChanged;

        ClearSessions();
        _device?.Dispose();
        _device = null;

        GC.SuppressFinalize(this);
    }
}