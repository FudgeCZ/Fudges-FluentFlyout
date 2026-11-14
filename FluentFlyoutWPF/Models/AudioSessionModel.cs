// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Windows.Media;

namespace FluentFlyoutWPF.Models;

public partial class AudioSessionModel : ObservableObject, IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private readonly AudioSessionControl _sessionControl;
    private bool _syncingFromDevice;
    private bool _disposed;

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    public partial int ProcessId { get; set; }

    [ObservableProperty]
    public partial AudioSessionState State { get; set; }

    [ObservableProperty]
    public partial float Volume { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    public ImageSource? Icon { get; }

    public bool HasIcon => Icon != null;

    public bool IsActive => State == AudioSessionState.AudioSessionStateActive;

    public event EventHandler? VolumeChanged;

    public AudioSessionModel(AudioSessionControl sessionControl, string displayName, int processId, AudioSessionState sessionState, ImageSource? icon)
    {
        _sessionControl = sessionControl;
        DisplayName = displayName;
        ProcessId = processId;
        State = sessionState;
        Icon = icon;

        _syncingFromDevice = true;
        try
        {
            Volume = _sessionControl.SimpleAudioVolume.Volume;
            IsMuted = _sessionControl.SimpleAudioVolume.Mute;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to read initial audio session volume for '{0}'", displayName);
        }
        finally
        {
            _syncingFromDevice = false;
        }
    }

    partial void OnVolumeChanged(float value)
    {
        if (_syncingFromDevice || _disposed) return;

        try
        {
            _sessionControl.SimpleAudioVolume.Volume = Math.Clamp(value, 0f, 1f);
            IsMuted = value == 0f;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to update audio session volume for '{0}'", DisplayName);
        }

        VolumeChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_syncingFromDevice || _disposed) return;

        try
        {
            _sessionControl.SimpleAudioVolume.Mute = value;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to update audio session mute for '{0}'", DisplayName);
        }
    }

    public void AdjustVolume(float delta)
    {
        Volume = Math.Clamp(Volume + delta, 0f, 1f);
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>
    /// refreshes Volume and IsMuted from the audio session without pushing changes back
    /// </summary>
    public void SyncFromDevice()
    {
        if (_disposed) return;

        try
        {
            var vol = _sessionControl.SimpleAudioVolume.Volume;
            var mute = _sessionControl.SimpleAudioVolume.Mute;

            _syncingFromDevice = true;
            try
            {
                if (MathF.Abs(Volume - vol) > 0.001f)
                    Volume = vol;

                if (IsMuted != mute)
                    IsMuted = mute;
            }
            finally
            {
                _syncingFromDevice = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to sync audio session '{0}' from device", DisplayName);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        VolumeChanged = null;

        try
        {
            _sessionControl.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to dispose AudioSessionControl for '{0}'", DisplayName);
        }

        GC.SuppressFinalize(this);
    }
}