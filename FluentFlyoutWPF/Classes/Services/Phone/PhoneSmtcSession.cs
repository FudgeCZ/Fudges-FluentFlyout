// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Windows.Media;
using Windows.Storage.Streams;

namespace FluentFlyoutWPF.Classes.Services.Phone;

/// <summary>
/// Publishes the phone as a native Windows media session (SMTC) using a headless
/// MediaPlayer. FluentFlyout's existing MediaManager then treats the phone exactly
/// like any other player: the media flyout, taskbar widget, seekbar and buttons all
/// work with zero UI changes. Transport button presses arrive via the command
/// manager and are forwarded to the phone over TCP. PlaybackStatus=Closed hides the
/// session when the phone is not connected.
/// </summary>
public sealed class PhoneSmtcSession : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private global::Windows.Media.Playback.MediaPlayer? _mediaPlayer;
    private SystemMediaTransportControls? _smtc;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private volatile bool _disposed;

    /// <summary>Runs on a threadpool thread for each system transport button press.</summary>
    public event Action<SystemMediaTransportControlsButton>? ButtonPressed;

    /// <summary>Runs on a threadpool thread for shuffle/repeat change requests from the system.
    /// Payload: "shuffle" or a MediaPlaybackAutoRepeatMode name (None/List/Track).</summary>
    public event Action<string>? RequestReceived;

    public void Start()
    {
        if (_disposed) return;
        Stop();
        try
        {
            _mediaPlayer = new global::Windows.Media.Playback.MediaPlayer
            {
                AudioCategory = global::Windows.Media.Playback.MediaPlayerAudioCategory.Media,
            };

            _smtc = _mediaPlayer.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.ShuffleEnabled = false;
            _smtc.AutoRepeatMode = MediaPlaybackAutoRepeatMode.None;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;   // hidden until the phone connects

            _smtc.ShuffleEnabledChangeRequested += OnShuffleEnabledChangeRequested;
            _smtc.AutoRepeatModeChangeRequested += OnAutoRepeatModeChangeRequested;

            var cm = _mediaPlayer.CommandManager;
            cm.PlayReceived += OnPlayReceived;
            cm.PauseReceived += OnPauseReceived;
            cm.NextReceived += OnNextReceived;
            cm.PreviousReceived += OnPreviousReceived;

            Logger.Info("Phone SMTC session created");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Phone SMTC session creation failed");
            _smtc = null;
            _mediaPlayer = null;
        }
    }

    private void OnShuffleEnabledChangeRequested(SystemMediaTransportControls sender, ShuffleEnabledChangeRequestedEventArgs args)
        => DispatchRequest("shuffle");

    private void OnAutoRepeatModeChangeRequested(SystemMediaTransportControls sender, AutoRepeatModeChangeRequestedEventArgs args)
        => DispatchRequest(args.RequestedAutoRepeatMode.ToString());

    private void OnPlayReceived(global::Windows.Media.Playback.MediaPlaybackCommandManager sender, global::Windows.Media.Playback.MediaPlaybackCommandManagerPlayReceivedEventArgs args)
        => Dispatch(SystemMediaTransportControlsButton.Play);

    private void OnPauseReceived(global::Windows.Media.Playback.MediaPlaybackCommandManager sender, global::Windows.Media.Playback.MediaPlaybackCommandManagerPauseReceivedEventArgs args)
        => Dispatch(SystemMediaTransportControlsButton.Pause);

    private void OnNextReceived(global::Windows.Media.Playback.MediaPlaybackCommandManager sender, global::Windows.Media.Playback.MediaPlaybackCommandManagerNextReceivedEventArgs args)
        => Dispatch(SystemMediaTransportControlsButton.Next);

    private void OnPreviousReceived(global::Windows.Media.Playback.MediaPlaybackCommandManager sender, global::Windows.Media.Playback.MediaPlaybackCommandManagerPreviousReceivedEventArgs args)
        => Dispatch(SystemMediaTransportControlsButton.Previous);

    private void DispatchRequest(string request)
    {
        try
        {
            RequestReceived?.Invoke(request);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Phone SMTC request dispatch failed");
        }
    }

    private void Dispatch(SystemMediaTransportControlsButton button)
    {
        try
        {
            ButtonPressed?.Invoke(button);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Phone SMTC button dispatch failed");
        }
    }

    /// <summary>Must be fire-and-forget; serialized so updater state can't interleave.</summary>
    public async Task UpdateMetadataAsync(string? title, string? artist, string? album, byte[]? coverBytes)
    {
        if (_disposed || _smtc == null) return;
        try
        {
            await _updateLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed) return;
            var smtc = _smtc;
            if (smtc == null) return;

            var updater = smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = title ?? "";
            updater.MusicProperties.Artist = artist ?? "";
            updater.MusicProperties.AlbumTitle = album ?? "";
            updater.Thumbnail = coverBytes is { Length: > 0 }
                ? await BytesToReferenceAsync(coverBytes).ConfigureAwait(false)
                : null;
            if (_disposed || !ReferenceEquals(_smtc, smtc)) return;
            updater.Update();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Phone SMTC metadata update failed");
        }
        finally
        {
            try { _updateLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private static async Task<RandomAccessStreamReference> BytesToReferenceAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        return RandomAccessStreamReference.CreateFromStream(stream);
    }

    public void UpdateStatus(bool playing)
    {
        var smtc = _smtc;
        if (smtc == null) return;
        try { smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused; }
        catch (Exception ex) { Logger.Debug(ex, "Phone SMTC status update failed"); }
    }

    /// <summary>Reflects the phone's shuffle/repeat state in the system session.
    /// shuffleOn: null = leave as-is; repeatMode: 0 = off, 1 = all, 2 = one, null = leave as-is.</summary>
    public void UpdateShuffleRepeat(bool? shuffleOn, int? repeatMode)
    {
        var smtc = _smtc;
        if (smtc == null) return;
        try
        {
            if (shuffleOn.HasValue) smtc.ShuffleEnabled = shuffleOn.Value;
            if (repeatMode.HasValue)
                smtc.AutoRepeatMode = repeatMode.Value switch
                {
                    1 => MediaPlaybackAutoRepeatMode.List,
                    2 => MediaPlaybackAutoRepeatMode.Track,
                    _ => MediaPlaybackAutoRepeatMode.None,
                };
        }
        catch (Exception ex) { Logger.Debug(ex, "Phone SMTC shuffle/repeat update failed"); }
    }

    /// <summary>Show or hide the whole session (Closed = removed from system media lists).</summary>
    public void SetSessionVisible(bool visible)
    {
        var smtc = _smtc;
        if (smtc == null) return;
        try
        {
            smtc.PlaybackStatus = visible ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Closed;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Phone SMTC visibility update failed");
        }
    }

    public void UpdateTimeline(double positionMs, double durationMs)
    {
        var smtc = _smtc;
        if (smtc == null || durationMs <= 0) return;
        try
        {
            smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                EndTime = TimeSpan.FromMilliseconds(durationMs),
                MinSeekTime = TimeSpan.Zero,
                MaxSeekTime = TimeSpan.FromMilliseconds(durationMs),
                Position = TimeSpan.FromMilliseconds(Math.Clamp(positionMs, 0, durationMs)),
            });
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Phone SMTC timeline update failed");
        }
    }

    public void Stop()
    {
        bool lockTaken = false;
        try
        {
            // Wait for any in-flight UpdateMetadataAsync so _mediaPlayer isn't disposed mid-update
            try
            {
                _updateLock.Wait();
                lockTaken = true;
            }
            catch (ObjectDisposedException)
            {
            }

            var smtc = _smtc;
            var mp = _mediaPlayer;
            _smtc = null;
            _mediaPlayer = null;

            if (smtc != null)
            {
                try
                {
                    smtc.ShuffleEnabledChangeRequested -= OnShuffleEnabledChangeRequested;
                    smtc.AutoRepeatModeChangeRequested -= OnAutoRepeatModeChangeRequested;
                    smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                }
                catch { }
            }

            if (mp != null)
            {
                try
                {
                    var cm = mp.CommandManager;
                    cm.PlayReceived -= OnPlayReceived;
                    cm.PauseReceived -= OnPauseReceived;
                    cm.NextReceived -= OnNextReceived;
                    cm.PreviousReceived -= OnPreviousReceived;
                }
                catch { }
                try { mp.Dispose(); } catch { }
            }
        }
        finally
        {
            if (lockTaken)
            {
                try { _updateLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _updateLock.Dispose();
    }
}
