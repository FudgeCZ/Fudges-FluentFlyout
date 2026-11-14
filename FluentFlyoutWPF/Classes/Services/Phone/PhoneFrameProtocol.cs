// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
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
    private const int HeaderSize = 5;
    private const int MaxFrame = 8 * 1024 * 1024;

    public static async Task<(byte Type, byte[] Payload)> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        uint len;
        byte type;
        byte[] header = ArrayPool<byte>.Shared.Rent(HeaderSize);
        try
        {
            await ReadExactAsync(stream, header.AsMemory(0, HeaderSize), ct).ConfigureAwait(false);
            len = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            type = header[4];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }

        if (len > MaxFrame) throw new IOException($"frame too large: {len}");
        if (len == 0) return (type, Array.Empty<byte>());

        byte[] payload = new byte[(int)len];
        await ReadExactAsync(stream, payload.AsMemory(), ct).ConfigureAwait(false);
        return (type, payload);
    }

    public static async Task WriteFrameAsync(NetworkStream stream, byte type, byte[] payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrame) throw new IOException($"frame too large to send: {payload.Length}");
        int totalLen = HeaderSize + payload.Length;
        byte[] frame = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
            frame[4] = type;
            if (payload.Length > 0)
                payload.AsSpan().CopyTo(frame.AsSpan(HeaderSize));

            await stream.WriteAsync(frame.AsMemory(0, totalLen), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> dest, CancellationToken ct)
    {
        int read = 0;
        while (read < dest.Length)
        {
            int n = await stream.ReadAsync(dest[read..], ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("socket closed");
            read += n;
        }
    }
}
