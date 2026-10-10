// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FluentFlyoutWPF.Classes.Services.Phone;

/// <summary>
/// Optional mix of phone music + local microphone into VB-Cable's render device
/// "CABLE Input", so call apps can send "voice + music" as one microphone. Mic via
/// WaveInEvent (tries 48k/2 → 48k/1 → 44.1k/2 → 44.1k/1), resampled into a
/// MixingSampleProvider (ReadFully=true) → WasapiOut on the cable with
/// useEventSync:false (event sync produces NO output on VB-Cable). The mic is
/// deliberately never monitored locally.
/// </summary>
public sealed class PhoneMicMixService : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private MMDevice? _cable;
    private WasapiOut? _cableOut;
    private MixingSampleProvider? _mixer;
    private BufferedWaveProvider? _musicBuffer;
    private WaveInEvent? _micIn;
    private VolumeSampleProvider? _volume;
    private bool _disposed;

    public bool Available { get; private set; }

    public static bool CableExists()
    {
        try
        {
            var devices = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var d in devices)
                if (d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
                    d.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { }
        return false;
    }

    /// <summary>Start mixing. musicFormat = the phone's PCM16 format from hello.</summary>
    public bool Start(WaveFormat musicFormat)
    {
        Stop();
        try
        {
            var devices = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            _cable = devices.FirstOrDefault(d =>
                d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
                d.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VB-Cable search failed");
            return false;
        }
        if (_cable == null)
        {
            Logger.Warn("VB-Cable not found — mic mix unavailable");
            return false;
        }

        try
        {
            var mixFormat = _cable.AudioClient.MixFormat;   // usually float32 48k/2
            _musicBuffer = new BufferedWaveProvider(musicFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true,
                ReadFully = false,
            };

            ISampleProvider music = _musicBuffer.ToSampleProvider();
            if (music.WaveFormat.SampleRate != mixFormat.SampleRate)
                music = new WdlResamplingSampleProvider(music, mixFormat.SampleRate);
            music = ToChannels(music, mixFormat.Channels);

            var mic = BuildMicSampleProvider(mixFormat.SampleRate, mixFormat.Channels);
            var sources = new List<ISampleProvider> { music };
            if (mic != null) sources.Add(mic);

            _mixer = new MixingSampleProvider(mixFormat) { ReadFully = true };
            foreach (var s in sources) _mixer.AddMixerInput(s);

            _volume = new VolumeSampleProvider(_mixer) { Volume = 1f };
            _cableOut = new WasapiOut(_cable, AudioClientShareMode.Shared, useEventSync: false, 80);
            _cableOut.Init(_volume);
            _cableOut.Play();

            Available = true;
            Logger.Info("Phone mic mix started into CABLE Input (music={0}, mix={1})", musicFormat, mixFormat);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Phone mic mix start failed");
            Stop();
            return false;
        }
    }

    private ISampleProvider? BuildMicSampleProvider(int targetRate, int targetChannels)
    {
        var formats = new[]
        {
            new WaveFormat(48000, 2), new WaveFormat(48000, 1),
            new WaveFormat(44100, 2), new WaveFormat(44100, 1),
        };
        foreach (var fmt in formats)
        {
            WaveInEvent? micIn = null;
            try
            {
                var micBuffer = new BufferedWaveProvider(fmt)
                {
                    BufferDuration = TimeSpan.FromSeconds(1),
                    DiscardOnBufferOverflow = true,
                    ReadFully = false,
                };
                micIn = new WaveInEvent { WaveFormat = fmt, BufferMilliseconds = 40 };
                micIn.DataAvailable += (_, e) =>
                {
                    try { micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { }
                };
                micIn.StartRecording();

                ISampleProvider mic = new Pcm16ToFloatSampleProvider(micBuffer);
                if (fmt.Channels == 1) mic = new MonoToStereoSampleProvider(mic);
                if (mic.WaveFormat.SampleRate != targetRate)
                    mic = new WdlResamplingSampleProvider(mic, targetRate);
                return ToChannels(mic, targetChannels);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Mic capture format {0} failed", fmt);
                try { micIn?.Dispose(); } catch { }
            }
        }
        Logger.Warn("No usable microphone capture format — mixing music only");
        return null;
    }

    /// <summary>Duplicate mono→N channels / fold stereo down when the mix format differs.</summary>
    private static ISampleProvider ToChannels(ISampleProvider src, int channels)
    {
        if (src.WaveFormat.Channels == channels) return src;
        if (src.WaveFormat.Channels == 1 && channels == 2) return new MonoToStereoSampleProvider(src);
        return new ChannelAdapterSampleProvider(src, channels);
    }

    public void AddMusic(byte[] pcm)
    {
        try { _musicBuffer?.AddSamples(pcm, 0, pcm.Length); } catch { }
    }

    public void ResetMusic() => _musicBuffer?.ClearBuffer();

    public void Stop()
    {
        try { _micIn?.StopRecording(); } catch { }
        _micIn?.Dispose(); _micIn = null;
        try { _cableOut?.Stop(); } catch { }
        _cableOut?.Dispose(); _cableOut = null;
        _mixer = null;
        _volume = null;
        _musicBuffer = null;
        Available = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>Wraps a PCM16 BufferedWaveProvider as a float ISampleProvider.</summary>
    private sealed class Pcm16ToFloatSampleProvider : ISampleProvider
    {
        private readonly Pcm16VolumeProvider _wave;
        private byte[] _tmp = Array.Empty<byte>();

        public Pcm16ToFloatSampleProvider(BufferedWaveProvider source) => _wave = new Pcm16VolumeProvider(source);
        public WaveFormat WaveFormat => _wave.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int bytes = count * 2;
            var tmp = _tmp.Length >= bytes ? _tmp : (_tmp = new byte[bytes]);
            int read = _wave.Read(tmp, 0, bytes);
            int samples = read / 2;
            for (int i = 0; i < samples; i++)
                buffer[offset + i] = (short)(tmp[i * 2] | tmp[i * 2 + 1] << 8) / 32768f;
            return samples;
        }
    }

    /// <summary>Averages src channels into dst channels (output float32).</summary>
    private sealed class ChannelAdapterSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly int _dstChannels;
        private readonly WaveFormat _format;

        public ChannelAdapterSampleProvider(ISampleProvider src, int dstChannels)
        {
            _src = src;
            _dstChannels = dstChannels;
            _format = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, dstChannels);
        }

        public WaveFormat WaveFormat => _format;

        public int Read(float[] buffer, int offset, int count)
        {
            int srcCh = _src.WaveFormat.Channels;
            int frames = count / _dstChannels;
            var tmp = new float[frames * srcCh];
            int read = _src.Read(tmp, 0, tmp.Length);
            int outFrames = read / srcCh;
            for (int f = 0; f < outFrames; f++)
                for (int c = 0; c < _dstChannels; c++)
                    buffer[offset + f * _dstChannels + c] = tmp[f * srcCh + Math.Min(c, srcCh - 1)];
            return outFrames * _dstChannels;
        }
    }
}
