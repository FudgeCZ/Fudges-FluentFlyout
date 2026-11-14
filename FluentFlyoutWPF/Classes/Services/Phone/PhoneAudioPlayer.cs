// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FluentFlyoutWPF.Classes.Services.Phone;

/// <summary>
/// Plays the phone's PCM16 stream on the default output device:
/// BufferedWaveProvider → Pcm16VolumeProvider → WasapiOut (shared, 50 ms).
/// A watchdog starts playback at ≥50 ms buffered and pauses at 0. The buffer is
/// cleared whenever it exceeds 0.4 s — this pins latency; without it device clock
/// drift grows the delay without bound. Reset() flushes stale audio on
/// play/pause/seek/skip transitions.
/// </summary>
public sealed class PhoneAudioPlayer : IDisposable
{
    private const int StartThresholdMs = 50;
    private const int FlushThresholdMs = 400;
    private const int EmptyPauseTicks = 4; // ~200 ms of empty buffer before pausing

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly Lock _lock = new();
    private MMDevice? _device;
    private BufferedWaveProvider? _buffer;
    private Pcm16VolumeProvider? _volume;
    private WasapiOut? _out;
    private readonly System.Timers.Timer _watchdog;
    private float _volumeLevel = 1f;
    private int _emptyTicks;
    private bool _disposed;

    public PhoneAudioPlayer()
    {
        _watchdog = new System.Timers.Timer(50) { AutoReset = true };
        _watchdog.Elapsed += (_, _) => WatchdogTick();
    }

    /// <summary>0..1 volume applied to samples only (never the system mixer).</summary>
    public double Volume
    {
        get
        {
            lock (_lock)
                return _volume?.Volume ?? _volumeLevel;
        }
        set
        {
            lock (_lock)
            {
                _volumeLevel = Math.Clamp((float)value, 0f, 1.5f);
                if (_volume != null)
                    _volume.Volume = _volumeLevel;
            }
        }
    }

    /// <summary>(Re)create the pipeline for a new PCM format. Safe to call repeatedly.</summary>
    public void Init(int sampleRate, int channels)
    {
        if (sampleRate is <= 0 or > 384000 || channels is <= 0 or > 8)
        {
            Logger.Warn("Invalid phone audio format: {0} Hz, {1} ch", sampleRate, channels);
            return;
        }

        lock (_lock)
        {
            if (_disposed) return;

            var fmt = new WaveFormat(sampleRate, 16, channels);
            if (_out != null && _buffer != null && _buffer.WaveFormat.Equals(fmt))
                return;

            StopAndDisposePipelineLocked();

            _buffer = new BufferedWaveProvider(fmt)
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true,
                // Keep WasapiOut in Playing state across brief jitter/flushes instead of hitting EOF Stopped
                ReadFully = true,
            };
            _volume = new Pcm16VolumeProvider(_buffer) { Volume = _volumeLevel };

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _out = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: false, 50);
                _out.Init(_volume);
                _watchdog.Start();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Phone audio init failed");
                StopAndDisposePipelineLocked();
            }
        }
    }

    public void AddSamples(byte[] pcm)
    {
        if (pcm.Length == 0) return;
        lock (_lock)
        {
            if (_disposed || _buffer == null) return;
            try { _buffer.AddSamples(pcm, 0, pcm.Length); }
            catch (Exception ex) { Logger.Debug(ex, "Phone audio add failed"); }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _emptyTicks = 0;
            _buffer?.ClearBuffer();
            if (_out != null && _out.PlaybackState == PlaybackState.Playing)
                _out.Pause();
        }
    }

    private void WatchdogTick()
    {
        lock (_lock)
        {
            try
            {
                if (_disposed || _buffer == null || _out == null) return;
                var buffered = _buffer.BufferedDuration.TotalMilliseconds;
                if (buffered > FlushThresholdMs)
                {
                    _buffer.ClearBuffer();                    // latency pinning — required
                    _emptyTicks = 0;
                }
                else if (buffered >= StartThresholdMs)
                {
                    _emptyTicks = 0;
                    if (_out.PlaybackState != PlaybackState.Playing)
                        _out.Play();
                }
                else if (buffered == 0 && _out.PlaybackState == PlaybackState.Playing)
                {
                    if (++_emptyTicks >= EmptyPauseTicks)
                    {
                        _emptyTicks = 0;
                        _out.Pause();
                    }
                }
                else if (buffered > 0)
                {
                    _emptyTicks = 0;
                }
            }
            catch (Exception ex) { Logger.Debug(ex, "Phone audio watchdog"); }
        }
    }

    private void StopAndDisposePipelineLocked()
    {
        _watchdog.Stop();
        _emptyTicks = 0;
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        _out = null;
        _volume = null;
        _buffer = null;
        try { _device?.Dispose(); } catch { }
        _device = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            StopAndDisposePipelineLocked();
            _watchdog.Dispose();
        }
    }
}
