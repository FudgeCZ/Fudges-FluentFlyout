// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using Windows.Media;

namespace FluentFlyoutWPF.Classes.Services.Phone;

/// <summary>
/// Receives music streamed from the Android "Phone Music Listener" sender app:
/// TCP listener (framed JSON/PCM16/JPEG), UDP discovery responder, local playback,
/// optional VB-Cable mic mix, and a native SMTC session so the phone appears in
/// FluentFlyout's media flyout / taskbar widget like any other player.
/// Wire protocol per the upstream project's AI_NOTES.md §2.
/// </summary>
public sealed class PhoneReceiverService : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public static PhoneReceiverService Instance { get; } = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private TcpClient? _client;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private PhoneAudioPlayer _audio = new();
    private PhoneMicMixService? _mic;
    private readonly PhoneDiscoveryResponder _discovery = new();
    private readonly PhoneSmtcSession _smtc = new();
    private readonly System.Timers.Timer _interpTimer;
    private readonly System.Timers.Timer _retryTimer;
    private readonly System.Timers.Timer _applyDebounce;

    // phone-published state
    private string? _trackId;
    private string? _title, _artist, _album, _device;
    private byte[]? _cover;
    private string? _awaitingCoverId;
    private double _positionMs, _durationMs;
    private DateTime _positionAt = DateTime.UtcNow;
    private bool _playing;
    private WaveFormat? _musicFormat;
    private int _port = 58710;
    private bool _discoveryEnabled, _mixEnabled;
    private bool _started;
    private bool _disposed;

    private PhoneReceiverService()
    {
        _smtc.ButtonPressed += OnSmtcButton;
        _smtc.RequestReceived += request =>
        {
            // the phone cycles its own modes; re-pushed meta state updates the session
            switch (request)
            {
                case "shuffle": SendCommand(new { t = "shuffle" }); break;
                case "List": SendCommand(new { t = "repeat" }); break;
                case "Track": SendCommand(new { t = "repeat" }); break;
                case "None": SendCommand(new { t = "repeat" }); break;
            }
        };

        _interpTimer = new System.Timers.Timer(500) { AutoReset = true };
        _interpTimer.Elapsed += (_, _) => InterpTick();

        _retryTimer = new System.Timers.Timer(30000) { AutoReset = true };
        _retryTimer.Elapsed += (_, _) =>
        {
            if (_started && _listener == null) StartListener();
        };

        _applyDebounce = new System.Timers.Timer(500) { AutoReset = false };
        _applyDebounce.Elapsed += (_, _) => ApplyNow();
    }

    /// <summary>Apply current UserSettings. Debounced so slider/typing changes don't thrash.</summary>
    public void ApplyFromSettings()
    {
        if (_disposed) return;
        _applyDebounce.Stop();
        _applyDebounce.Start();
    }

    private void ApplyNow()
    {
        var settings = FluentFlyout.Classes.Settings.SettingsManager.Current;
        bool want = settings.PhoneReceiverEnabled;
        int port = settings.PhoneReceiverPort;
        bool discovery = settings.PhoneReceiverDiscovery;
        bool mix = settings.PhoneReceiverVbCableMix;

        if (!want)
        {
            if (_started) Shutdown();
            return;
        }

        if (_started && port == _port && discovery == _discoveryEnabled && mix == _mixEnabled)
            return;   // already running as configured

        Shutdown();
        _audio = new PhoneAudioPlayer();
        _port = port;
        _discoveryEnabled = discovery;
        _mixEnabled = mix;
        StartListener();
        if (discovery) _discovery.Start();
        _smtc.Start();
        _retryTimer.Start();
        _started = true;
        Logger.Info("Phone receiver started on tcp/{0} (discovery: {1}, VB-Cable mix: {2})",
            port, discovery, mix);
    }

    private void StartListener()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            Logger.Info("Phone listener on tcp/{0}", _port);
            _ = AcceptLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Phone listener failed on tcp/{0} — is another instance running?", _port);
            _listener = null;
        }
    }

    private void Shutdown()
    {
        _retryTimer.Stop();
        _interpTimer.Stop();
        _discovery.Stop();
        try { _listener?.Stop(); _cts?.Cancel(); } catch { }
        _listener = null;
        CloseClient();
        OnDisconnected();
        _smtc.Stop();
        _audio.Dispose();
        _mic?.Dispose(); _mic = null;
        _started = false;
        Logger.Info("Phone receiver stopped");
    }

    // ---- TCP -------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Logger.Debug(ex, "Phone accept ended"); break; }

            Logger.Info("Phone connected from {0}", client.Client.RemoteEndPoint);
            CloseClient();
            _client = client;
            client.NoDelay = true;
            _ = ClientLoopAsync(client, ct);
        }
    }

    private void CloseClient()
    {
        var old = _client;
        _client = null;
        if (old == null) return;
        try { old.Dispose(); } catch { }
    }

    private async Task ClientLoopAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            while (!ct.IsCancellationRequested && client.Connected)
            {
                var (type, payload) = await PhoneFrameProtocol.ReadFrameAsync(stream, ct);
                switch (type)
                {
                    case PhoneFrameProtocol.TypeJson: HandleJson(Encoding.UTF8.GetString(payload)); break;
                    case PhoneFrameProtocol.TypePcm: HandleAudio(payload); break;
                    case PhoneFrameProtocol.TypeCover: HandleCover(payload); break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Debug(ex, "Phone read loop ended"); }

        if (ReferenceEquals(_client, client))
        {
            Logger.Info("Phone disconnected");
            CloseClient();
            OnDisconnected();
        }
    }

    private void OnDisconnected()
    {
        _audio.Reset();
        _mic?.ResetMusic();
        _playing = false;
        _trackId = null;
        _title = _artist = _album = null;
        _cover = null;
        _positionMs = _durationMs = 0;
        // fully remove the session so the system media list can't keep a ghost
        // entry (a merely Closed session lingers and freezes the widget)
        _smtc.Stop();
    }

    // ---- frame handlers ----------------------------------------------------

    private void HandleAudio(byte[] pcm)
    {
        _audio.AddSamples(pcm);
        _mic?.AddMusic(pcm);
    }

    private void HandleCover(byte[] jpeg)
    {
        if (_awaitingCoverId == null) return;
        if (_trackId == _awaitingCoverId)
        {
            _cover = jpeg;
            _ = _smtc.UpdateMetadataAsync(_title, _artist, _album, _cover);
        }
        _awaitingCoverId = null;
    }

    private void HandleJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (JStr(root, "t"))
            {
                case "hello":
                    _device = JStr(root, "device");
                    int rate = (int)(JNum(root, "sampleRate") ?? 48000);
                    int ch = (int)(JNum(root, "channels") ?? 2);
                    _audio.Init(rate, ch);
                    _musicFormat = new WaveFormat(rate, 16, ch);
                    if (_mixEnabled && _musicFormat != null)
                    {
                        _mic = new PhoneMicMixService();
                        _mic.Start(_musicFormat);
                    }
                    _smtc.Start();
                    break;

                case "meta": HandleMeta(root); break;
                case "queue": break; // the system media session has no queue surface; ignored for now
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Phone JSON parse failed: {0}", json[..Math.Min(200, json.Length)]);
        }
    }

    private void HandleMeta(JsonElement root)
    {
        string? id = JStr(root, "id");
        bool playing = JBool(root, "playing") ?? _playing;
        double position = JNum(root, "position") ?? _positionMs;

        // flush stale audio on playing/pause/seek/skip transitions (state flip or a
        // position jump far from our interpolated value)
        double interpolated = _positionMs + (_playing ? (DateTime.UtcNow - _positionAt).TotalMilliseconds : 0);
        if (_playing != playing || Math.Abs(position - interpolated) > 1500)
        {
            _audio.Reset();
            _mic?.ResetMusic();
        }

        bool trackChanged = _trackId != id;
        _trackId = id;
        _title = JStr(root, "title") ?? _title;
        _artist = JStr(root, "artist") ?? _artist;
        _album = JStr(root, "album") ?? _album;
        _durationMs = JNum(root, "duration") ?? _durationMs;
        _positionMs = position;
        _positionAt = DateTime.UtcNow;
        _playing = playing;

        if (trackChanged) _cover = null;                       // fresh cover frame follows if any
        if (JBool(root, "cover") == true && id != null) _awaitingCoverId = id;

        // reflect the phone's shuffle/repeat state in the system session
        var shuffle = JNum(root, "shuffle");
        var repeat = JNum(root, "repeat");
        if (shuffle.HasValue || repeat.HasValue)
            _smtc.UpdateShuffleRepeat(shuffle.HasValue ? shuffle.Value == 1 : null,
                repeat.HasValue ? (int)repeat.Value : null);

        _ = _smtc.UpdateMetadataAsync(_title, _artist, _album, _cover);
        _smtc.UpdateStatus(_playing);
        _smtc.UpdateTimeline(_positionMs, _durationMs);

        if (!_interpTimer.Enabled && _playing) _interpTimer.Start();
        else if (_interpTimer.Enabled && !_playing) _interpTimer.Stop();
    }

    private void InterpTick()
    {
        if (!_playing) return;
        _positionMs += (DateTime.UtcNow - _positionAt).TotalMilliseconds;
        _positionAt = DateTime.UtcNow;
        if (_durationMs > 0) _positionMs = Math.Min(_positionMs, _durationMs);
        _smtc.UpdateTimeline(_positionMs, _durationMs);
    }

    // ---- commands (system transport → phone) --------------------------------

    private void OnSmtcButton(SystemMediaTransportControlsButton button)
    {
        switch (button)
        {
            case SystemMediaTransportControlsButton.Play: SendCommand(new { t = "play" }); break;
            case SystemMediaTransportControlsButton.Pause: SendCommand(new { t = "pause" }); break;
            case SystemMediaTransportControlsButton.Stop: SendCommand(new { t = "pause" }); break;
            case SystemMediaTransportControlsButton.Next: SendCommand(new { t = "next" }); break;
            case SystemMediaTransportControlsButton.Previous: SendCommand(new { t = "prev" }); break;
        }
    }

    private void SendCommand(object payload)
    {
        var client = _client;
        if (client == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _sendLock.WaitAsync();
                var stream = client.GetStream();
                await PhoneFrameProtocol.WriteFrameAsync(stream, PhoneFrameProtocol.TypeJson,
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), CancellationToken.None);
            }
            catch (Exception ex) { Logger.Debug(ex, "Phone command send failed"); }
            finally { _sendLock.Release(); }
        });
    }

    // ---- json helpers ---------------------------------------------------------

    private static string? JStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static double? JNum(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;

    private static bool? JBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True ? true
        : e.TryGetProperty(name, out var q) && q.ValueKind == JsonValueKind.False ? false : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _applyDebounce.Stop();
        if (_started) Shutdown();
        _interpTimer.Dispose();
        _retryTimer.Dispose();
        _applyDebounce.Dispose();
    }
}
