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

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private BufferedWaveProvider? _buffer;
    private Pcm16VolumeProvider? _volume;
    private WasapiOut? _out;
    private readonly System.Timers.Timer _watchdog;
    private bool _disposed;

    public PhoneAudioPlayer()
    {
        _watchdog = new System.Timers.Timer(50) { AutoReset = true };
        _watchdog.Elapsed += (_, _) => WatchdogTick();
    }

    /// <summary>0..1 volume applied to samples only (never the system mixer).</summary>
    public double Volume
    {
        get => _volume?.Volume ?? 1f;
        set { if (_volume != null) _volume.Volume = (float)value; }
    }

    /// <summary>(Re)create the pipeline for a new PCM format. Safe to call repeatedly.</summary>
    public void Init(int sampleRate, int channels)
    {
        var fmt = new WaveFormat(sampleRate, 16, channels);
        if (_buffer != null && _buffer.WaveFormat.Equals(fmt)) return;
        StopAndDisposePipeline();

        _buffer = new BufferedWaveProvider(fmt)
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        _volume = new Pcm16VolumeProvider(_buffer);

        try
        {
            var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _out = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, 50);
            _out.Init(_volume);
            _watchdog.Start();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Phone audio init failed");
            _out = null;
        }
    }

    public void AddSamples(byte[] pcm)
    {
        if (_buffer == null) return;
        try { _buffer.AddSamples(pcm, 0, pcm.Length); }
        catch (Exception ex) { Logger.Debug(ex, "Phone audio add failed"); }
    }

    public void Reset()
    {
        _buffer?.ClearBuffer();
        if (_out != null && _out.PlaybackState == PlaybackState.Playing)
            _out.Pause();
    }

    private void WatchdogTick()
    {
        try
        {
            if (_buffer == null || _out == null) return;
            var buffered = _buffer.BufferedDuration.TotalMilliseconds;
            if (buffered > FlushThresholdMs)
                _buffer.ClearBuffer();                    // latency pinning — required
            else if (buffered >= StartThresholdMs && _out.PlaybackState != PlaybackState.Playing)
                _out.Play();
            else if (buffered == 0 && _out.PlaybackState == PlaybackState.Playing)
                _out.Pause();
        }
        catch (Exception ex) { Logger.Debug(ex, "Phone audio watchdog"); }
    }

    private void StopAndDisposePipeline()
    {
        _watchdog.Stop();
        try { _out?.Stop(); } catch { }
        _out?.Dispose(); _out = null;
        _volume = null;
        _buffer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAndDisposePipeline();
        _watchdog.Dispose();
    }
}
