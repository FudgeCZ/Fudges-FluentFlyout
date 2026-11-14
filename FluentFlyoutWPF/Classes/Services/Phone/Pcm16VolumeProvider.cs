// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

// 16-bit PCM sample scaler. NAudio's VolumeWaveProvider assumes 32-bit float input
// and corrupts PCM16 — never use it for the phone stream.

using NAudio.Wave;

namespace FluentFlyoutWPF.Classes.Services.Phone;

public sealed class Pcm16VolumeProvider : IWaveProvider
{
    private readonly IWaveProvider _source;
    private volatile float _volume = 1f;

    public Pcm16VolumeProvider(IWaveProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.WaveFormat.Encoding != WaveFormatEncoding.Pcm || source.WaveFormat.BitsPerSample != 16)
            throw new ArgumentException("Pcm16VolumeProvider requires 16-bit PCM input", nameof(source));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1.5f);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        // Keep reads aligned to whole 16-bit samples to preserve channel/sample framing
        int toRead = count & ~1;
        if (toRead <= 0) return 0;

        int read = _source.Read(buffer, offset, toRead);
        int samples = read / 2;
        int alignedRead = samples * 2;
        float vol = _volume;
        if (Math.Abs(vol - 1f) < 1e-6f)
            return alignedRead;

        for (int i = 0; i < samples; i++)
        {
            int idx = offset + i * 2;
            int s = (short)(buffer[idx] | (buffer[idx + 1] << 8));
            int scaled = (int)(s * vol);
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            else if (scaled < short.MinValue) scaled = short.MinValue;
            buffer[idx] = (byte)scaled;
            buffer[idx + 1] = (byte)(scaled >> 8);
        }
        return alignedRead;
    }
}
