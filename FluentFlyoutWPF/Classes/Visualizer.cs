// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Controls;
using FluentFlyoutWPF.Models;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FluentFlyoutWPF.Classes
{
    public class Visualizer : IDisposable
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public static int BarCount = 10;

        // Keep at 2: only an exact 2:1 minification turns WPF's default Linear
        // filter into a true box average. Other factors discard the supersampling.
        internal const int Supersample = 2;

        // TaskbarVisualizerControl's Image has Margin="4,0", so the drawable content
        // is the control length minus 8 (upstream default: 84 - 8 = 76).
        private const int ControlHorizontalMargin = 8;
        private const int MinContentWidth = 24;
        private const int MaxContentWidth = 292;

        internal const int ImageHeight = 32 * Supersample;
        private const int BarSpacing = 2 * Supersample;

        private WasapiLoopbackCapture? _capture;
        private MMDevice? _renderDevice;
        private bool _isRunning;

        private readonly int _fftLength = 4096;
        private int _fftPos = 0;
        private readonly Complex[] _fftBuffer;

        private readonly int _targetFps = 30;
        private DateTime _lastUpdateTime = DateTime.MinValue;

        private System.Timers.Timer? _captureWatchdog;
        private DateTime _lastDataAvailableUtc = DateTime.MinValue;
        private int _restartInProgress; // 0=false, 1=true (Interlocked)
        private string? _deviceId; // track current device ID for restart logic

        /// <summary>
        /// Smoothed 0..1 energy of the low-frequency bars, for beat-reactive effects.
        /// 0 when silent or stopped.
        /// </summary>
        public static float CurrentEnergy { get; private set; }

        /// <summary>
        /// Friendly app name the visualizer is gated to. Null/empty = animate on any
        /// audio. Set via <see cref="SetTargetApp"/>.
        /// </summary>
        private static string? TargetApp;

        /// <summary>
        /// Whether the gated app is currently the playing media session (pushed from
        /// MainWindow). When a target is set and this is false, bars decay to silence.
        /// </summary>
        public static bool TargetAudible { get; set; }

        private readonly object _dataLock = new();

        public Visualizer()
        {
            _fftBuffer = new Complex[_fftLength];

            AudioDeviceMonitor.Instance.DefaultDeviceChanged += OnDefaultDeviceChanged;
            TryRegisterSystemEvents();
        }

        internal static int ContentWidthFromLength(int controlLength)
        {
            return Math.Clamp(controlLength - ControlHorizontalMargin, MinContentWidth, MaxContentWidth) * Supersample;
        }

        private static bool IsAnyVisualizerEnabled() =>
            TaskbarVisualizerControl.HasAnyEnabledInstance() || SettingsManager.Current.TaskbarVisualizerEnabled;

        private void TryRegisterSystemEvents()
        {
            try
            {
                SystemEvents.SessionSwitch += OnSessionSwitch;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to register SystemEvents handlers for visualizer auto-restart");
            }
        }

        private void TryUnregisterSystemEvents()
        {
            try
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to unregister SystemEvents handlers for visualizer auto-restart");
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (!IsAnyVisualizerEnabled())
                return;

            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon)
            {
                RequestRestart($"session switch: {e.Reason}");
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (!IsAnyVisualizerEnabled())
                return;

            if (e.Mode == PowerModes.Resume)
            {
                RequestRestart("power resume");
            }
        }

        private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
        {
            _deviceId = e.DeviceId;

            if (!IsAnyVisualizerEnabled())
                return;
            RequestRestart("default audio output device changed");
        }

        private void RequestRestart(string reason)
        {
            if (!IsAnyVisualizerEnabled())
                return;

            if (Interlocked.Exchange(ref _restartInProgress, 1) == 1)
                return;

            Logger.Info($"Restarting visualizer ({reason})");

            Task.Run(async () =>
            {
                try
                {
                    Stop();

                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        await Task.Delay(500);
                        Start();
                        if (_isRunning)
                            return;
                        Logger.Warn($"Visualizer restart attempt {attempt + 1} failed, retrying...");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Visualizer restart failed");
                }
                finally
                {
                    Interlocked.Exchange(ref _restartInProgress, 0);
                }
            });
        }

        public static void ResizeBarList(int newBarCount)
        {
            BarCount = Math.Clamp(newBarCount, 1, 64);
        }

        /// <summary>
        /// Gates the visualizer to one app: bars animate only while that app is the
        /// playing media session. Null/empty = animate on any audio.
        /// </summary>
        public static void SetTargetApp(string? appName)
        {
            string? next = string.IsNullOrWhiteSpace(appName) ? null : appName.Trim();
            if (next == TargetApp)
                return;

            TargetApp = next;
            if (IsAnyVisualizerEnabled())
            {
                TaskbarVisualizerControl.SharedVisualizer?.RequestRestart("visualizer target app changed");
            }
        }

        public void Start()
        {
            if (_isRunning)
                return;

            try
            {
                _renderDevice?.Dispose();
                _renderDevice = string.IsNullOrWhiteSpace(_deviceId)
                     ? AudioDeviceMonitor.Instance.GetDefaultRenderDevice()
                     : AudioDeviceMonitor.Instance.GetDeviceById(_deviceId) ?? AudioDeviceMonitor.Instance.GetDefaultRenderDevice();

                if (_renderDevice == null)
                {
                    return;
                }

                _capture = new WasapiLoopbackCapture(_renderDevice);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
                _isRunning = true;
                _lastDataAvailableUtc = DateTime.UtcNow;
                StartWatchdog();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to start visualizer");
            }
        }

        private void StartWatchdog()
        {
            _captureWatchdog = new(500)
            {
                AutoReset = false
            };
            _captureWatchdog.Elapsed += OnWatchdogTick;
        }

        private void OnWatchdogTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_isRunning)
                return;

            var controls = TaskbarVisualizerControl.GetActiveInstances();
            lock (_dataLock)
            {
                CurrentEnergy = 0;
                foreach (var ctrl in controls)
                {
                    ctrl.ClearBarsToSilence();
                    RenderControlBitmap(ctrl);
                }
            }

            foreach (var ctrl in controls)
            {
                var s = ctrl.Settings;
                if (!s.TaskbarVisualizerBaseline || s.TaskbarVisualizerBaselineAutoHide)
                    s.TaskbarVisualizerHasContent = false;
            }

            var silenceFor = DateTime.UtcNow - _lastDataAvailableUtc;
            if (silenceFor > TimeSpan.FromSeconds(2))
            {
                RequestRestart($"no audio callbacks for {silenceFor.TotalSeconds:0.0}s");
            }
        }

        public void Stop()
        {
            if (!_isRunning)
                return;

            _isRunning = false;

            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.StopRecording();
                _capture.Dispose();
                _capture = null;
            }

            _renderDevice?.Dispose();
            _renderDevice = null;

            _captureWatchdog?.Stop();
            _captureWatchdog?.Dispose();
            _captureWatchdog = null;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!_isRunning || e.BytesRecorded == 0)
                return;

            int bytesPerSample = _capture!.WaveFormat.BitsPerSample / 8;
            HandleAudioData(e.Buffer, e.BytesRecorded, bytesPerSample);
        }

        private void HandleAudioData(byte[] buffer, int bytesRecorded, int bytesPerSample)
        {
            lock (_dataLock)
            {
                if (!_isRunning || bytesRecorded == 0)
                    return;

                _captureWatchdog?.Stop();
                _captureWatchdog?.Start();
                _lastDataAvailableUtc = DateTime.UtcNow;

                int samplesRecorded = bytesRecorded / bytesPerSample;

                for (int i = 0; i < samplesRecorded; i++)
                {
                    float sampleValue = 0;
                    if (bytesPerSample == 4)
                    {
                        sampleValue = BitConverter.ToSingle(buffer, i * 4);
                    }
                    else if (bytesPerSample == 2)
                    {
                        sampleValue = BitConverter.ToInt16(buffer, i * 2) / 32768f;
                    }

                    _fftBuffer[_fftPos].X = (float)(sampleValue * FastFourierTransform.HammingWindow(_fftPos, _fftLength));
                    _fftBuffer[_fftPos].Y = 0;
                    _fftPos++;

                    if (_fftPos < _fftLength)
                        continue;

                    _fftPos = 0;
                    FastFourierTransform.FFT(true, (int)Math.Log(_fftLength, 2.0), _fftBuffer);

                    DateTime now = DateTime.UtcNow;
                    double minFrameTime = 1000.0 / _targetFps;
                    double timeSinceLastUpdate = (now - _lastUpdateTime).TotalMilliseconds;

                    if (timeSinceLastUpdate < minFrameTime)
                        continue;

                    _lastUpdateTime = now;

                    var controls = TaskbarVisualizerControl.GetActiveInstances();
                    bool energyComputed = false;

                    foreach (var ctrl in controls)
                    {
                        if (!ctrl.Settings.TaskbarVisualizerEnabled)
                            continue;

                        bool hasAudio = ProcessFftForControl(ctrl, !energyComputed);
                        energyComputed = true;

                        if (ctrl.Settings.TaskbarVisualizerBaseline && !ctrl.Settings.TaskbarVisualizerBaselineAutoHide)
                        {
                            ctrl.Settings.TaskbarVisualizerHasContent = true;
                            RenderControlBitmap(ctrl);
                        }
                        else if (hasAudio)
                        {
                            ctrl.Settings.TaskbarVisualizerHasContent = true;
                            RenderControlBitmap(ctrl);
                        }
                        else
                        {
                            ctrl.Settings.TaskbarVisualizerHasContent = false;
                        }
                    }
                }
            }
        }

        private bool ProcessFftForControl(TaskbarVisualizerControl ctrl, bool updateGlobalEnergy)
        {
            int barCount = Math.Clamp(ctrl.Settings.TaskbarVisualizerBarCount, 1, 64);
            ctrl.EnsureBarArrays(barCount);
            float[] barValues = ctrl.BarValues;

            if (!string.IsNullOrEmpty(TargetApp) && !TargetAudible)
            {
                bool any = false;
                for (int i = 0; i < barCount; i++)
                {
                    barValues[i] *= 0.8f;
                    if (barValues[i] > 0.01f)
                        any = true;
                }
                if (updateGlobalEnergy)
                    CurrentEnergy *= 0.7f;
                return any;
            }

            int sampleRate = _capture?.WaveFormat.SampleRate ?? 48000;
            double frequencyPerBin = (double)sampleRate / _fftLength;

            double minFreq = 40;   // Hz
            double maxFreq = 8000; // Hz
            float minDb = (SettingsManager.Current.TaskbarVisualizerAudioSensitivity * -10f) - 30f;
            float maxDb = (SettingsManager.Current.TaskbarVisualizerAudioPeakLevel * 10f) - 30f;

            bool allZero = true;
            for (int i = 0; i < barCount; i++)
            {
                double startFreq = minFreq * Math.Pow(maxFreq / minFreq, (double)i / barCount);
                double endFreq = minFreq * Math.Pow(maxFreq / minFreq, (double)(i + 1) / barCount);

                int startBin = (int)(startFreq / frequencyPerBin);
                int endBin = (int)(endFreq / frequencyPerBin);

                if (endBin <= startBin) endBin = startBin + 1;
                if (endBin >= _fftBuffer.Length / 2) endBin = _fftBuffer.Length / 2 - 1;

                float maxAmplitude = 0;
                for (int j = startBin; j < endBin; j++)
                {
                    float amplitude = (float)Math.Sqrt(_fftBuffer[j].X * _fftBuffer[j].X + _fftBuffer[j].Y * _fftBuffer[j].Y);
                    if (amplitude > maxAmplitude)
                        maxAmplitude = amplitude;
                }

                float progress = (float)i / barCount;
                float linearBoost = 1.0f + (progress * 75.0f);
                maxAmplitude *= linearBoost;

                if (maxAmplitude < 0.001f) maxAmplitude = 0.001f;

                float db = 20f * (float)Math.Log10(maxAmplitude);
                float intensity = Math.Clamp((db - minDb) / (maxDb - minDb), 0f, 1f);

                if (intensity > barValues[i])
                    barValues[i] = intensity;
                else
                    barValues[i] = (barValues[i] * 0.8f) + (intensity * 0.2f);

                if (barValues[i] > 0.01f)
                    allZero = false;
            }

            if (updateGlobalEnergy)
            {
                int energyBars = Math.Min(8, barCount);
                float energy = 0;
                for (int i = 0; i < energyBars; i++)
                    energy += barValues[i];
                energy /= Math.Max(1, energyBars);
                CurrentEnergy = CurrentEnergy * 0.7f + energy * 0.3f;
            }

            return !allZero;
        }

        private void RenderControlBitmap(TaskbarVisualizerControl ctrl)
        {
            var bitmap = ctrl.Bitmap;
            if (bitmap == null)
                return;

            int imageWidth = ctrl.ImageWidth;
            int barCount = Math.Clamp(ctrl.Settings.TaskbarVisualizerBarCount, 1, 64);
            int style = ctrl.Settings.TaskbarVisualizerStyle;
            bool centeredBars = ctrl.Settings.TaskbarVisualizerCenteredBars;
            bool baselineEnabled = ctrl.Settings.TaskbarVisualizerBaseline;

            ctrl.EnsureBarArrays(barCount);
            float[] barValuesSnapshot = (float[])ctrl.BarValues.Clone();
            float[] peakValues = ctrl.PeakValues;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                lock (ctrl.BitmapLock)
                {
                    var bmp = ctrl.Bitmap;
                    if (bmp == null || bmp.PixelWidth != imageWidth)
                        return;

                    bmp.Lock();
                    try
                    {
                        unsafe
                        {
                            IntPtr pBackBuffer = bmp.BackBuffer;
                            int stride = bmp.BackBufferStride;
                            int bufferSize = stride * ImageHeight;
                            Span<byte> buffer = new Span<byte>(pBackBuffer.ToPointer(), bufferSize);
                            buffer.Clear();

                            if (style == 2)
                                DrawWave(stride, buffer, imageWidth, barCount, barValuesSnapshot, centeredBars, baselineEnabled);
                            else
                                DrawColumnBars(stride, buffer, imageWidth, barCount, barValuesSnapshot, peakValues, centeredBars, baselineEnabled, drawPeaks: style == 1);
                        }

                        bmp.AddDirtyRect(new Int32Rect(0, 0, imageWidth, ImageHeight));
                    }
                    finally
                    {
                        bmp.Unlock();
                    }
                }
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        private unsafe void DrawWave(
            int stride,
            Span<byte> buffer,
            int imageWidth,
            int barCount,
            float[] barValues,
            bool centered,
            bool baselineEnabled)
        {
            SolidColorBrush brush = BitmapHelper.SavedDominantColors.Count > 0
                ? BitmapHelper.SavedDominantColors.Last()
                : (SolidColorBrush)Application.Current.TryFindResource("MicaWPF.Brushes.SystemAccentColorTertiary");
            byte b = brush.Color.B, g = brush.Color.G, r = brush.Color.R;

            int baseline = baselineEnabled ? Supersample * 2 : 0;
            int centerY = ImageHeight / 2;

            float thickness = 3f * Supersample;
            float half = thickness / 2f;
            int effectiveCount = Math.Max(2, barCount);

            for (int x = 0; x < imageWidth; x++)
            {
                float t = (float)x / Math.Max(1, imageWidth - 1) * (effectiveCount - 1);
                int i = (int)t;
                if (i >= effectiveCount - 1) i = effectiveCount - 2;
                float frac = t - i;
                float v0 = i < barValues.Length ? barValues[i] : 0f;
                float v1 = (i + 1) < barValues.Length ? barValues[i + 1] : v0;
                float value = Math.Clamp(v0 * (1 - frac) + v1 * frac, 0f, 1f);
                int height = (int)(value * (ImageHeight - baseline * 2));

                int y0, y1;
                if (centered)
                {
                    y0 = centerY - height / 2 - (int)half;
                    y1 = centerY + height / 2 + (int)half;
                }
                else
                {
                    y0 = ImageHeight - baseline - height - (int)half;
                    y1 = ImageHeight - baseline;
                }

                for (int y = Math.Max(0, y0); y < Math.Min(ImageHeight, y1); y++)
                {
                    float edge = Math.Min(y - Math.Max(0, y0), Math.Min(ImageHeight - 1, y1 - 1) - y);
                    byte a = edge < 1 ? (byte)(255 * Math.Clamp(edge, 0f, 1f)) : (byte)255;
                    int index = y * stride + (x << 2);
                    if (index + 3 >= buffer.Length) continue;
                    WritePixel(buffer, index, b, g, r, a);
                }
            }
        }

        private unsafe void DrawColumnBars(
            int stride,
            Span<byte> buffer,
            int imageWidth,
            int barCount,
            float[] barValues,
            float[] peakValues,
            bool centeredBars,
            bool baselineEnabled,
            bool drawPeaks)
        {
            SolidColorBrush brush = BitmapHelper.SavedDominantColors.Count > 0
                ? BitmapHelper.SavedDominantColors.Last()
                : (SolidColorBrush)Application.Current.TryFindResource("MicaWPF.Brushes.SystemAccentColorTertiary");

            byte b = brush.Color.B;
            byte g = brush.Color.G;
            byte r = brush.Color.R;

            int barBaseline = baselineEnabled ? Supersample * 2 : 0;
            int centerY = ImageHeight / 2;

            ComputeLayout(imageWidth, barCount, BarSpacing, out int barWidth, out int offsetX);

            float baseRadius = (2f * Supersample) / MathF.Max(1f, barCount / 10f);
            const float aa = 1.25f;
            float invAA = 1f / aa;

            for (int i = 0; i < barCount && i < barValues.Length; i++)
            {
                int barX = offsetX + i * (barWidth + BarSpacing);
                int barHeight = GetBarHeight(barValues[i], barBaseline);

                if (barHeight <= 0)
                    continue;

                ComputeVertical(centeredBars, centerY, barHeight, out int barY, out int barEndY);

                float radius = ClampRadius(baseRadius, barWidth, barHeight);
                float radiusSq = radius * radius;

                RasterizeBar(
                    buffer, stride,
                    imageWidth,
                    barX, barWidth,
                    barY, barEndY,
                    centeredBars,
                    radius, radiusSq, invAA,
                    b, g, r);
            }

            if (!drawPeaks)
                return;

            for (int i = 0; i < barCount && i < barValues.Length && i < peakValues.Length; i++)
            {
                float peak = Math.Max(peakValues[i] * 0.985f, barValues[i]);
                peakValues[i] = peak;
                if (peak < 0.01f) continue;

                int barX = offsetX + i * (barWidth + BarSpacing);
                int capHeight = (int)(Math.Clamp(peak, 0f, 1f) * (ImageHeight - barBaseline * 2));
                int capY = centeredBars
                    ? centerY - capHeight / 2
                    : ImageHeight - barBaseline - capHeight;
                capY = Math.Clamp(capY, 0, ImageHeight - 2);

                for (int x = barX; x < barX + barWidth && x < imageWidth && x >= 0; x++)
                {
                    for (int y = capY; y < capY + 2 * Supersample && y < ImageHeight; y++)
                    {
                        int index = y * stride + (x << 2);
                        if (index + 3 >= buffer.Length) continue;
                        WritePixel(buffer, index, b, g, r, 255);
                    }
                }
            }
        }

        private static void ComputeLayout(
            int imageWidth,
            int barCount,
            int spacing,
            out int barWidth,
            out int offsetX)
        {
            if (barCount + (barCount - 1) * spacing + 1 > imageWidth)
            {
                spacing = Math.Max(1, (imageWidth - 1 - barCount) / Math.Max(1, barCount - 1));
            }

            int totalSpacing = (barCount - 1) * spacing;
            int availableWidth = imageWidth - totalSpacing - 1;
            barWidth = Math.Max(1, availableWidth / Math.Max(1, barCount));
            int usedWidth = barWidth * barCount + totalSpacing;
            offsetX = (imageWidth - usedWidth) >> 1;
        }

        private void ComputeVertical(bool centered, int centerY, int height, out int y, out int endY)
        {
            if (centered)
            {
                int half = height >> 1;
                y = centerY - half;
                endY = centerY + half;
            }
            else
            {
                y = ImageHeight - height;
                endY = ImageHeight;
            }
        }

        private int GetBarHeight(float value, int baseline)
        {
            return Math.Max((int)(Math.Clamp(value, 0f, 1f) * ImageHeight), baseline);
        }

        private static float ClampRadius(float r, int width, int height)
        {
            float max = MathF.Min(width, height) * 0.5f;
            return r > max ? max : r;
        }

        private unsafe void RasterizeBar(
            Span<byte> buffer,
            int stride,
            int imageWidth,
            int barX,
            int barWidth,
            int barY,
            int barEndY,
            bool centeredBars,
            float radius,
            float radiusSq,
            float invAA,
            byte b, byte g, byte r)
        {
            float left = barX;
            float right = barX + barWidth;
            float top = barY;
            float bottom = barEndY;

            float innerLeft = left + radius;
            float innerRight = right - radius;
            float innerTop = top + radius;
            float innerBottom = bottom - radius;

            for (int y = barY; y < barEndY && y < ImageHeight && y >= 0; y++)
            {
                int row = y * stride;
                float py = y + 0.5f;

                for (int x = barX; x < barX + barWidth && x < imageWidth && x >= 0; x++)
                {
                    int index = row + (x << 2);
                    if (index + 3 >= buffer.Length)
                        continue;

                    float px = x + 0.5f;

                    if (px >= innerLeft && px <= innerRight)
                    {
                        WritePixel(buffer, index, b, g, r, 255);
                        continue;
                    }

                    if (py >= innerTop && py <= innerBottom)
                    {
                        WritePixel(buffer, index, b, g, r, 255);
                        continue;
                    }

                    if (!centeredBars && py >= innerBottom)
                    {
                        WritePixel(buffer, index, b, g, r, 255);
                        continue;
                    }

                    float cx = px < innerLeft ? innerLeft : (px > innerRight ? innerRight : px);
                    float cy = py < innerTop ? innerTop : (py > innerBottom ? innerBottom : py);

                    float dx = px - cx;
                    float dy = py - cy;

                    float distSq = dx * dx + dy * dy;
                    float sdf = (distSq - radiusSq) / (2f * radius);

                    float alpha = 0.5f - sdf * invAA;

                    if (alpha <= 0f)
                        continue;

                    if (alpha > 1f) alpha = 1f;

                    WritePixel(buffer, index, b, g, r, (byte)(255 * alpha));
                }
            }
        }

        private static void WritePixel(Span<byte> buffer, int index, byte b, byte g, byte r, byte a)
        {
            buffer[index] = b;
            buffer[index + 1] = g;
            buffer[index + 2] = r;
            buffer[index + 3] = a;
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                Logger.Error(e.Exception, "Visualizer recording stopped due to an error");
            }
        }

        public void Dispose()
        {
            Stop();

            AudioDeviceMonitor.Instance.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            TryUnregisterSystemEvents();

            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }

            GC.SuppressFinalize(this);
        }
    }
}
