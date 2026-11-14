// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Captures the audio of a single process via WASAPI process loopback
/// (ActivateAudioInterfaceAsync with AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK),
/// unlike <see cref="NAudio.Wasapi.WasapiLoopbackCapture"/> which mixes the whole device.
/// Emits 48 kHz stereo float32 packets through <see cref="DataAvailable"/>.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private const int AudclntSharemodeShared = 0;
    private const int AudclntStreamflagsLoopback = 0x00020000;
    private const int AudclntStreamflagsEventCallback = 0x00040000;
    private const int AudclntStreamflagsAutoconvertPcm = unchecked((int)0x80000000);
    private const int AudclntStreamflagsSrcDefaultQuality = 0x08000000;
    private const int AudclntBufferflagsSilent = 0x2;
    private const int ProcessLoopbackModeProcessTarget = 0; // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    public event Action<byte[], int>? DataAvailable;

    private readonly int _pid;
    private readonly object _lock = new();
    private IntPtr _captureClient;
    private EventWaitHandle? _eventHandle;
    private Thread? _thread;
    private volatile bool _stopping;
    private bool _disposed;
    private IAudioClient? _clientRcw;

    public ProcessLoopbackCapture(int pid) => _pid = pid;

    public bool Start()
    {
        lock (_lock)
        {
            if (_disposed || _thread != null)
                return false;

            try
            {
                var handler = new ActivationHandler();
                IntPtr handlerPtr = Marshal.GetIUnknownForObject(handler);
                try
                {
                    var activationParams = new AudioClientActivationParams
                    {
                        ActivationType = 1 /* PROCESS_LOOPBACK */,
                        ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                        {
                            ProcessLoopbackMode = ProcessLoopbackModeProcessTarget,
                            TargetProcessId = (uint)_pid,
                        },
                    };

                    int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
                    IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
                    IntPtr propVariant = Marshal.AllocHGlobal(24);
                    try
                    {
                        Marshal.StructureToPtr(activationParams, paramsPtr, false);

                        // Zero-init PROPVARIANT (VT_BLOB) pointing at the activation params
                        Marshal.WriteInt64(propVariant, 0, 0);
                        Marshal.WriteInt64(propVariant, 8, 0);
                        Marshal.WriteInt64(propVariant, 16, 0);
                        Marshal.WriteInt16(propVariant, 0, 65); // VT_BLOB
                        Marshal.WriteInt32(propVariant, 8, paramsSize);
                        Marshal.WriteIntPtr(propVariant, 8 + IntPtr.Size, paramsPtr);

                        int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", in IidAudioClient,
                            propVariant, handlerPtr, out IntPtr operationPtr);

                        if (operationPtr != IntPtr.Zero)
                            Marshal.Release(operationPtr);
                        if (hr != 0)
                        {
                            Logger.Warn($"Process loopback activation failed for PID {_pid}: 0x{hr:X8}");
                            return false;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(propVariant); // HGlobal, NOT a COM pointer
                        Marshal.FreeHGlobal(paramsPtr);
                    }
                }
                finally
                {
                    Marshal.Release(handlerPtr);
                }

                if (!handler.Task.Task.Wait(TimeSpan.FromSeconds(4)) || !handler.Task.Task.IsCompletedSuccessfully || handler.Task.Task.Result == null)
                {
                    Logger.Warn($"Process loopback activation timed out for PID {_pid}");
                    return false;
                }

                _clientRcw = (IAudioClient)handler.Task.Task.Result!;

                // 48 kHz stereo IEEE float
                var format = new WaveFormatEx
                {
                    FormatTag = 3,
                    Channels = 2,
                    SampleRate = 48000,
                    BitsPerSample = 32,
                    BlockAlign = 8,
                    AvgBytesPerSec = 48000 * 8,
                    ExtraSize = 0,
                };
                IntPtr formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
                int initHr;
                try
                {
                    Marshal.StructureToPtr(format, formatPtr, false);
                    initHr = _clientRcw.Initialize(AudclntSharemodeShared,
                        AudclntStreamflagsLoopback | AudclntStreamflagsEventCallback |
                        AudclntStreamflagsAutoconvertPcm | AudclntStreamflagsSrcDefaultQuality,
                        1_000_000, 0, formatPtr, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeHGlobal(formatPtr);
                }
                if (initHr != 0)
                {
                    Logger.Warn($"Process loopback Initialize failed for PID {_pid}: 0x{initHr:X8}");
                    CleanupResources();
                    return false;
                }

                _eventHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
                if (_clientRcw.SetEventHandle(_eventHandle.SafeWaitHandle.DangerousGetHandle()) != 0)
                {
                    Logger.Warn($"SetEventHandle failed for PID {_pid}");
                    CleanupResources();
                    return false;
                }

                Guid captureIid = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
                if (_clientRcw.GetService(ref captureIid, out IntPtr capturePtr) != 0 || capturePtr == IntPtr.Zero)
                {
                    Logger.Warn($"GetService(IAudioCaptureClient) failed for PID {_pid}");
                    CleanupResources();
                    return false;
                }
                _captureClient = capturePtr;

                if (_clientRcw.Start() != 0)
                {
                    Logger.Warn($"Start failed for PID {_pid}");
                    CleanupResources();
                    return false;
                }

                var captureThread = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = $"ProcessLoopback-{_pid}",
                };
                _thread = captureThread;
                captureThread.Start();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Process loopback start failed for PID {_pid}");
                CleanupResources();
                return false;
            }
        }
    }

    private unsafe void CaptureLoop()
    {
        IntPtr captureClientPtr = _captureClient;
        EventWaitHandle? eventHandle = _eventHandle;
        if (captureClientPtr == IntPtr.Zero || eventHandle == null)
            return;

        var capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(captureClientPtr);

        try
        {
            byte[] buffer = new byte[192000]; // >= 1 s of 48k stereo float
            while (!_stopping)
            {
                if (!eventHandle.WaitOne(2000))
                    continue;

                while (!_stopping)
                {
                    if (capture.GetNextPacketSize(out uint packetFrames) != 0 || packetFrames == 0)
                        break;

                    if (capture.GetBuffer(out IntPtr dataPtr, out uint frames, out int flags, out _, out _) != 0)
                        break;

                    long totalBytes = (long)frames * 8;
                    if (totalBytes > 0 && totalBytes <= int.MaxValue)
                    {
                        int bytes = (int)totalBytes;
                        if (bytes > buffer.Length)
                            buffer = new byte[bytes];

                        if ((flags & AudclntBufferflagsSilent) != 0 || dataPtr == IntPtr.Zero)
                        {
                            Array.Clear(buffer, 0, bytes);
                        }
                        else
                        {
                            fixed (byte* dst = buffer)
                            {
                                Buffer.MemoryCopy((void*)dataPtr, dst, buffer.Length, bytes);
                            }
                        }

                        capture.ReleaseBuffer(frames);

                        if (!_stopping)
                            DataAvailable?.Invoke(buffer, bytes);
                    }
                    else
                    {
                        capture.ReleaseBuffer(frames);
                    }
                }
            }
        }
        catch (ObjectDisposedException) { /* shutting down */ }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Process loopback capture loop ended for PID {_pid}");
        }
        finally
        {
            Marshal.ReleaseComObject(capture);
        }
    }

    private void CleanupResources()
    {
        if (_captureClient != IntPtr.Zero)
        {
            Marshal.Release(_captureClient);
            _captureClient = IntPtr.Zero;
        }
        if (_clientRcw != null)
        {
            try
            {
                if (Marshal.IsComObject(_clientRcw))
                    Marshal.ReleaseComObject(_clientRcw);
            }
            catch { /* client may already be gone */ }
            _clientRcw = null;
        }
        _eventHandle?.Dispose();
        _eventHandle = null;
    }

    public void Dispose()
    {
        Thread? threadToJoin;
        EventWaitHandle? eventToSignal;
        IAudioClient? clientToStop;

        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stopping = true;

            threadToJoin = _thread;
            _thread = null;
            eventToSignal = _eventHandle;
            clientToStop = _clientRcw;
        }

        try
        {
            clientToStop?.Stop();
        }
        catch { /* client may already be gone */ }

        try
        {
            eventToSignal?.Set();
        }
        catch { /* handle may already be closed */ }

        try
        {
            threadToJoin?.Join(500);
        }
        catch { /* thread may have never started */ }

        lock (_lock)
        {
            CleanupResources();
        }
    }

    // --- COM interop ---

    [DllImport("mmdevapi.dll", PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        in Guid interfaceGuid,
        IntPtr activationParams, // PROPVARIANT*
        IntPtr completionHandler, // IActivateAudioInterfaceCompletionHandler*
        out IntPtr operation);    // IActivateAudioInterfaceAsyncOperation**

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;   // SDK order: pid first, then mode
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public short FormatTag;
        public short Channels;
        public int SampleRate;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short ExtraSize;
    }

    [ComVisible(true)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public TaskCompletionSource<object?> Task { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                int callHr = operation.GetActivateResult(out int hr, out object? punk);
                if (callHr != 0 || hr != 0 || punk == null)
                    Task.TrySetResult(null);
                else
                    Task.TrySetResult(punk);
            }
            catch (Exception ex)
            {
                Task.TrySetException(ex);
            }
        }
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A711C994E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        void ActivateCompleted([MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D79-CDE4-431D-B8CC-843A711C994E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.Interface)] out object? activatedInterface);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
            IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig]
        int GetBufferSize(out uint bufferFrames);
        [PreserveSig]
        int GetStreamLatency(out long latency);
        [PreserveSig]
        int GetCurrentPadding(out uint padding);
        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        [PreserveSig]
        int GetMixFormat(out IntPtr format);
        [PreserveSig]
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        [PreserveSig]
        int Start();
        [PreserveSig]
        int Stop();
        [PreserveSig]
        int Reset();
        [PreserveSig]
        int SetEventHandle(IntPtr @event);
        [PreserveSig]
        int GetService(ref Guid interfaceId, out IntPtr service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint frames, out int flags,
            out long devicePosition, out long qpcPosition);
        [PreserveSig]
        int ReleaseBuffer(uint frames);
        [PreserveSig]
        int GetNextPacketSize(out uint frames);
    }
}
