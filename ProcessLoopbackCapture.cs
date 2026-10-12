using System.Runtime.InteropServices;

namespace lifeviz;

// Windows per-process loopback (Windows 10 build 20348+): captures everything one process tree
// renders, on any output endpoint, after its session volume. This is the same API Discord and OBS
// use for application audio capture. NAudio 2.2 does not expose it, so the activation and the two
// COM interfaces it needs are declared here. The capture is polled: Read drains whatever packets
// are ready without blocking, so it can be called from an audio render callback.
internal sealed class ProcessLoopbackCapture : IDisposable
{
    private const int AudioClientShareModeShared = 0;
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsAutoConvertPcm = 0x80000000;
    private const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    private const uint BufferFlagsSilent = 0x2;
    private const long BufferDuration100Ns = 2_000_000; // 200 ms
    private const string ProcessLoopbackDevicePath = @"VAD\Process_Loopback";
    private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientIid = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private IAudioClient? _client;
    private IAudioCaptureClient? _captureClient;

    private ProcessLoopbackCapture(int processId, int sampleRate)
    {
        ProcessId = processId;
        SampleRate = sampleRate;
    }

    public int ProcessId { get; }
    public int SampleRate { get; }
    public const int Channels = 2;

    public static async Task<ProcessLoopbackCapture> StartAsync(int processId, bool includeProcessTree, int sampleRate)
    {
        var capture = new ProcessLoopbackCapture(processId, sampleRate);
        try
        {
            capture._client = await ActivateAsync(processId, includeProcessTree).ConfigureAwait(false);
            capture.InitializeAndStart();
            return capture;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    /// <summary>Appends all ready interleaved stereo float frames to <paramref name="sink"/>; returns frames read.</summary>
    public int Read(Action<ReadOnlySpan<float>> sink)
    {
        var captureClient = _captureClient;
        if (captureClient == null)
        {
            return 0;
        }

        int total = 0;
        while (captureClient.GetNextPacketSize(out uint packetFrames) >= 0 && packetFrames > 0)
        {
            int hr = captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
            if (hr < 0)
            {
                break;
            }

            try
            {
                if (frames > 0)
                {
                    int samples = (int)frames * Channels;
                    if ((flags & BufferFlagsSilent) != 0 || data == IntPtr.Zero)
                    {
                        float[] silence = System.Buffers.ArrayPool<float>.Shared.Rent(samples);
                        Array.Clear(silence, 0, samples);
                        sink(silence.AsSpan(0, samples));
                        System.Buffers.ArrayPool<float>.Shared.Return(silence);
                    }
                    else
                    {
                        unsafe
                        {
                            sink(new ReadOnlySpan<float>((void*)data, samples));
                        }
                    }

                    total += (int)frames;
                }
            }
            finally
            {
                captureClient.ReleaseBuffer(frames);
            }
        }

        return total;
    }

    public void Dispose()
    {
        try
        {
            _client?.Stop();
        }
        catch
        {
            // Ignore teardown races with a process that already exited.
        }

        if (_captureClient != null)
        {
            Marshal.ReleaseComObject(_captureClient);
            _captureClient = null;
        }

        if (_client != null)
        {
            Marshal.ReleaseComObject(_client);
            _client = null;
        }
    }

    private void InitializeAndStart()
    {
        var client = _client ?? throw new InvalidOperationException("Process loopback client was not activated.");
        // The process loopback endpoint has no mix format; request 32-bit float stereo and let
        // the audio engine convert and resample into it.
        IntPtr format = Marshal.AllocHGlobal(18);
        try
        {
            Marshal.WriteInt16(format, 0, 3); // WAVE_FORMAT_IEEE_FLOAT
            Marshal.WriteInt16(format, 2, Channels);
            Marshal.WriteInt32(format, 4, SampleRate);
            Marshal.WriteInt32(format, 8, SampleRate * Channels * sizeof(float));
            Marshal.WriteInt16(format, 12, Channels * sizeof(float));
            Marshal.WriteInt16(format, 14, 32);
            Marshal.WriteInt16(format, 16, 0);
            Marshal.ThrowExceptionForHR(client.Initialize(
                AudioClientShareModeShared,
                StreamFlagsLoopback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality,
                BufferDuration100Ns,
                0,
                format,
                IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(format);
        }

        Guid captureIid = AudioCaptureClientIid;
        Marshal.ThrowExceptionForHR(client.GetService(ref captureIid, out object captureClient));
        _captureClient = (IAudioCaptureClient)captureClient;
        Marshal.ThrowExceptionForHR(client.Start());
    }

    private static Task<IAudioClient> ActivateAsync(int processId, bool includeProcessTree)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType, TargetProcessId, ProcessLoopbackMode }
        // wrapped in a VT_BLOB PROPVARIANT.
        IntPtr activationParams = Marshal.AllocHGlobal(12);
        IntPtr propVariant = Marshal.AllocHGlobal(24);
        var handler = new ActivationHandler();
        try
        {
            Marshal.WriteInt32(activationParams, 0, 1); // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(activationParams, 4, processId);
            Marshal.WriteInt32(activationParams, 8, includeProcessTree ? 0 : 1);
            for (int i = 0; i < 24; i += 8)
            {
                Marshal.WriteInt64(propVariant, i, 0);
            }

            Marshal.WriteInt16(propVariant, 0, 65); // VT_BLOB
            Marshal.WriteInt32(propVariant, 8, 12);
            Marshal.WriteIntPtr(propVariant, 16, activationParams);

            Guid iid = AudioClientIid;
            ActivateAudioInterfaceAsync(ProcessLoopbackDevicePath, ref iid, propVariant, handler, out _);
        }
        catch (Exception ex)
        {
            handler.Fail(ex);
        }

        return handler.Task.ContinueWith(task =>
        {
            Marshal.FreeHGlobal(propVariant);
            Marshal.FreeHGlobal(activationParams);
            return task;
        }, TaskScheduler.Default).Unwrap();
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComVisible(true)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Task => _completion.Task;

        public void Fail(Exception ex) => _completion.TrySetException(ex);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                Marshal.ThrowExceptionForHR(activateOperation.GetActivateResult(out int activateHr, out object audioInterface));
                Marshal.ThrowExceptionForHR(activateHr);
                _completion.TrySetResult((IAudioClient)audioInterface);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    // Marks the completion handler free-threaded; activation fails with E_ILLEGAL_METHOD_CALL otherwise.
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }
}
