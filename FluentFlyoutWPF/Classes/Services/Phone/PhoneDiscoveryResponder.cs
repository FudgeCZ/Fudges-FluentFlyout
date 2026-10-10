// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FluentFlyoutWPF.Classes.Services.Phone;

/// <summary>
/// Answers the phone's discovery broadcast. The phone broadcasts "PML_DISCOVER" on
/// UDP 58711; we reply {"name":"&lt;machine&gt;","tcp":&lt;port&gt;} and the phone uses
/// the reply's source address as the PC's IP. Non-fatal if the port is taken.
/// </summary>
public sealed class PhoneDiscoveryResponder : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    public int TcpPort { get; set; } = 58710;

    public bool Start(int udpPort = 58711)
    {
        Stop();
        try
        {
            _udp = new UdpClient(udpPort);
            _cts = new CancellationTokenSource();
            _ = ListenAsync(_cts.Token);
            Logger.Info("Phone discovery responder listening on udp/{0}, answering tcp/{1}", udpPort, TcpPort);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Phone discovery responder failed to start (non-fatal)");
            _udp = null;
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(res.Buffer).Trim();
                if (text != "PML_DISCOVER") continue;
                var reply = Encoding.UTF8.GetBytes(
                    $$"""{"name":"{{Environment.MachineName}}","tcp":{{TcpPort}}}""");
                await _udp.SendAsync(reply, res.RemoteEndPoint).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Logger.Debug(ex, "Phone discovery receive error"); }
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); _udp?.Dispose(); } catch { }
        _udp = null;
        _cts = null;
    }

    public void Dispose() => Stop();
}
