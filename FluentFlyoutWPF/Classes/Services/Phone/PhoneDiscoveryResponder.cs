// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FluentFlyoutWPF.Classes.Services.Phone;

/// <summary>
/// Answers the phone's discovery broadcast. The phone broadcasts "PML_DISCOVER" on
/// UDP 58711; we reply {"name":"&lt;machine&gt;","tcp":&lt;port&gt;} and the phone uses
/// the reply's source address as the PC's IP. Non-fatal if the port is taken.
/// </summary>
public sealed class PhoneDiscoveryResponder : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly Lock _lock = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private volatile int _tcpPort = 58710;
    private bool _disposed;

    public int TcpPort
    {
        get => _tcpPort;
        set => _tcpPort = value;
    }

    public bool Start(int udpPort = 58711)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            StopLocked();
            UdpClient? udp = null;
            CancellationTokenSource? cts = null;
            try
            {
                udp = new UdpClient(udpPort);
                cts = new CancellationTokenSource();
                _udp = udp;
                _cts = cts;
                _ = ListenAsync(udp, cts.Token);
                Logger.Info("Phone discovery responder listening on udp/{0}, answering tcp/{1}", udpPort, _tcpPort);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Phone discovery responder failed to start (non-fatal)");
                try { cts?.Dispose(); } catch { }
                try { udp?.Dispose(); } catch { }
                _udp = null;
                _cts = null;
                return false;
            }
        }
    }

    private async Task ListenAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(res.Buffer).Trim();
                if (text != "PML_DISCOVER") continue;
                var reply = JsonSerializer.SerializeToUtf8Bytes(new { name = Environment.MachineName, tcp = _tcpPort });
                await udp.SendAsync(reply, res.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Logger.Debug(ex, "Phone discovery receive error"); }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        _cts = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            StopLocked();
        }
    }
}
