// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Net.Sockets;

namespace FluentFlyoutWPF.Classes.Services.Phone;

/// <summary>
/// Framing for the Android "Phone Music Listener" sender wire protocol:
/// [4-byte big-endian payload length][1-byte type][payload].
/// Types: 1 = JSON (UTF-8), 2 = PCM16LE audio, 3 = JPEG cover. Max frame 8 MB.
/// </summary>
public static class PhoneFrameProtocol
{
    public const byte TypeJson = 1;
    public const byte TypePcm = 2;
    public const byte TypeCover = 3;
    private const int MaxFrame = 8 * 1024 * 1024;

    public static async Task<(byte Type, byte[] Payload)> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = await ReadExactAsync(stream, 5, ct).ConfigureAwait(false);
        uint len = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
        if (len > MaxFrame) throw new IOException($"frame too large: {len}");
        byte type = header[4];
        byte[] payload = len == 0 ? Array.Empty<byte>() : await ReadExactAsync(stream, (int)len, ct).ConfigureAwait(false);
        return (type, payload);
    }

    public static async Task WriteFrameAsync(NetworkStream stream, byte type, byte[] payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrame) throw new IOException($"frame too large to send: {payload.Length}");
        var header = new byte[5]
        {
            (byte)(payload.Length >> 24), (byte)(payload.Length >> 16),
            (byte)(payload.Length >> 8), (byte)payload.Length, type,
        };
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("socket closed");
            read += n;
        }
        return buf;
    }
}
