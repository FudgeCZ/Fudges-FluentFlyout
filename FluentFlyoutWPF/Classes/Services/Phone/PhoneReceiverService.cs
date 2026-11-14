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

    private readonly Lock _stateLock = new();
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
            lock (_stateLock)
            {
                if (!_disposed && _started && _listener == null) StartListenerLocked();
            }
        };

        _applyDebounce = new System.Timers.Timer(500) { AutoReset = false };
        _applyDebounce.Elapsed += (_, _) => ApplyNow();
    }

    /// <summary>Apply current UserSettings. Debounced so slider/typing changes don't thrash.</summary>
    public void ApplyFromSettings()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _applyDebounce.Stop();
            _applyDebounce.Start();
        }
    }

    private void ApplyNow()
    {
        var settings = FluentFlyout.Classes.Settings.SettingsManager.Current;
        bool want = settings.PhoneReceiverEnabled;
        int port = settings.PhoneReceiverPort;
        bool discovery = settings.PhoneReceiverDiscovery;
        bool mix = settings.PhoneReceiverVbCableMix;

        lock (_stateLock)
        {
            if (_disposed) return;

            if (!want)
            {
                if (_started) ShutdownLocked();
                return;
            }

            if (_started && port == _port && discovery == _discoveryEnabled && mix == _mixEnabled)
                return;   // already running as configured

            ShutdownLocked();
            _audio = new PhoneAudioPlayer();
            _port = port;
            _discoveryEnabled = discovery;
            _mixEnabled = mix;
            _discovery.TcpPort = _port;
            StartListenerLocked();
            if (discovery) _discovery.Start();
            _smtc.Start();
            _retryTimer.Start();
            _started = true;
            Logger.Info("Phone receiver started on tcp/{0} (discovery: {1}, VB-Cable mix: {2})",
                port, discovery, mix);
        }
    }

    private void StartListenerLocked()
    {
        try
        {
            try { _listener?.Stop(); } catch { }
            _listener = null;
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;

            var cts = new CancellationTokenSource();
            var listener = new TcpListener(IPAddress.Any, _port);
            listener.Start();
            _cts = cts;
            _listener = listener;
            Logger.Info("Phone listener on tcp/{0}", _port);
            _ = AcceptLoopAsync(listener, cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Phone listener failed on tcp/{0} — is another instance running?", _port);
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _listener = null;
        }
    }

    private void ShutdownLocked()
    {
        bool wasStarted = _started;
        _retryTimer.Stop();
        _interpTimer.Stop();
        _discovery.Stop();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        CloseClientLocked();
        OnDisconnectedLocked();
        _smtc.Stop();
        _audio.Dispose();
        _mic?.Dispose();
        _mic = null;
        _started = false;
        if (wasStarted)
            Logger.Info("Phone receiver stopped");
    }

    // ---- TCP -------------------------------------------------------------

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Logger.Debug(ex, "Phone accept ended"); break; }

            lock (_stateLock)
            {
                if (ct.IsCancellationRequested || _disposed || !ReferenceEquals(_listener, listener))
                {
                    try { client.Dispose(); } catch { }
                    break;
                }
                Logger.Info("Phone connected from {0}", client.Client.RemoteEndPoint);
                CloseClientLocked();
                _client = client;
                client.NoDelay = true;
                _ = ClientLoopAsync(client, ct);
            }
        }
    }

    private void CloseClientLocked()
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
                var (type, payload) = await PhoneFrameProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
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

        lock (_stateLock)
        {
            if (ReferenceEquals(_client, client))
            {
                Logger.Info("Phone disconnected");
                CloseClientLocked();
                OnDisconnectedLocked();
            }
        }
    }

    private void OnDisconnectedLocked()
    {
        _interpTimer.Stop();
        _audio.Reset();
        _mic?.Dispose();
        _mic = null;
        _playing = false;
        _trackId = null;
        _title = _artist = _album = null;
        _cover = null;
        _awaitingCoverId = null;
        _positionMs = _durationMs = 0;
        // fully remove the session so the system media list can't keep a ghost
        // entry (a merely Closed session lingers and freezes the widget)
        _smtc.Stop();
    }

    // ---- frame handlers ----------------------------------------------------

    private void HandleAudio(byte[] pcm)
    {
        PhoneAudioPlayer audio;
        PhoneMicMixService? mic;
        lock (_stateLock)
        {
            if (_disposed) return;
            audio = _audio;
            mic = _mic;
        }
        audio.AddSamples(pcm);
        mic?.AddMusic(pcm);
    }

    private void HandleCover(byte[] jpeg)
    {
        string? title, artist, album;
        byte[] cover;
        lock (_stateLock)
        {
            if (_disposed || _awaitingCoverId == null) return;
            bool match = _trackId == _awaitingCoverId;
            _awaitingCoverId = null;
            if (!match) return;

            _cover = jpeg;
            title = _title;
            artist = _artist;
            album = _album;
            cover = _cover;
        }
        _ = _smtc.UpdateMetadataAsync(title, artist, album, cover);
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
                    int rate = (int)(JNum(root, "sampleRate") ?? 48000);
                    int ch = (int)(JNum(root, "channels") ?? 2);
                    if (rate is <= 0 or > 384000 || ch is <= 0 or > 8)
                    {
                        Logger.Warn("Ignoring phone hello with invalid audio format: {0} Hz, {1} ch", rate, ch);
                        break;
                    }

                    lock (_stateLock)
                    {
                        if (_disposed) break;
                        _device = JStr(root, "device");
                        _audio.Init(rate, ch);
                        _musicFormat = new WaveFormat(rate, 16, ch);
                        _mic?.Dispose();
                        _mic = null;
                        if (_mixEnabled && _musicFormat != null)
                        {
                            _mic = new PhoneMicMixService();
                            _mic.Start(_musicFormat);
                        }
                        _smtc.Start();
                    }
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
        string? title, artist, album;
        byte[]? cover;
        bool playing;
        double positionMs, durationMs;
        double? shuffle = JNum(root, "shuffle");
        double? repeat = JNum(root, "repeat");

        lock (_stateLock)
        {
            if (_disposed) return;
            string? id = JStr(root, "id");
            playing = JBool(root, "playing") ?? _playing;
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

            title = _title;
            artist = _artist;
            album = _album;
            cover = _cover;
            positionMs = _positionMs;
            durationMs = _durationMs;

            if (!_interpTimer.Enabled && _playing) _interpTimer.Start();
            else if (_interpTimer.Enabled && !_playing) _interpTimer.Stop();
        }

        // reflect the phone's shuffle/repeat state in the system session
        if (shuffle.HasValue || repeat.HasValue)
            _smtc.UpdateShuffleRepeat(shuffle.HasValue ? shuffle.Value == 1 : null,
                repeat.HasValue ? (int)repeat.Value : null);

        _ = _smtc.UpdateMetadataAsync(title, artist, album, cover);
        _smtc.UpdateStatus(playing);
        _smtc.UpdateTimeline(positionMs, durationMs);
    }

    private void InterpTick()
    {
        double positionMs, durationMs;
        lock (_stateLock)
        {
            if (_disposed || !_playing) return;
            _positionMs += (DateTime.UtcNow - _positionAt).TotalMilliseconds;
            _positionAt = DateTime.UtcNow;
            if (_durationMs > 0) _positionMs = Math.Min(_positionMs, _durationMs);
            positionMs = _positionMs;
            durationMs = _durationMs;
        }
        _smtc.UpdateTimeline(positionMs, durationMs);
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
        TcpClient? client;
        lock (_stateLock)
        {
            if (_disposed) return;
            client = _client;
        }
        if (client == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _sendLock.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (_disposed || !ReferenceEquals(_client, client) || !client.Connected)
                    return;
                var stream = client.GetStream();
                await PhoneFrameProtocol.WriteFrameAsync(stream, PhoneFrameProtocol.TypeJson,
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { Logger.Debug(ex, "Phone command send failed"); }
            finally
            {
                try { _sendLock.Release(); } catch (ObjectDisposedException) { }
            }
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
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _applyDebounce.Stop();
            ShutdownLocked();
            _interpTimer.Dispose();
            _retryTimer.Dispose();
            _applyDebounce.Dispose();
            _discovery.Dispose();
            _smtc.Dispose();
            _sendLock.Dispose();
        }
    }
}
