// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

// 16-bit PCM sample scaler. NAudio's VolumeWaveProvider assumes 32-bit float input
// and corrupts PCM16 — never use it for the phone stream.

using NAudio.Wave;

namespace FluentFlyoutWPF.Classes.Services.Phone;

public sealed class Pcm16VolumeProvider : IWaveProvider
{
    private readonly IWaveProvider _source;
    private float _volume = 1f;

    public Pcm16VolumeProvider(IWaveProvider source)
    {
        _source = source;
        if (source.WaveFormat.BitsPerSample != 16)
            throw new ArgumentException("Pcm16VolumeProvider requires 16-bit PCM input");
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1.5f);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int samples = read / 2;
        for (int i = 0; i < samples; i++)
        {
            int s = (short)(buffer[offset + i * 2] | buffer[offset + i * 2 + 1] << 8);
            int scaled = (int)(s * _volume);
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            else if (scaled < short.MinValue) scaled = short.MinValue;
            buffer[offset + i * 2] = (byte)scaled;
            buffer[offset + i * 2 + 1] = (byte)(scaled >> 8);
        }
        return read;
    }
}
