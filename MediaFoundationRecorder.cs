using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace lifeviz;

internal enum RecordingQuality
{
    Lossless,
    LosslessCompatible,
    Uncompressed,
    High,
    Balanced,
    Compact
}

internal readonly struct RecordingSettings
{
    public RecordingSettings(RecordingQuality quality, Guid outputSubtype, string fileExtension, int bitrate, int? encoderQuality, int? encoderQualityVsSpeed, bool useFfmpeg, string? ffmpegOutputArgs)
    {
        Quality = quality;
        OutputSubtype = outputSubtype;
        FileExtension = fileExtension;
        Bitrate = bitrate;
        EncoderQuality = encoderQuality;
        EncoderQualityVsSpeed = encoderQualityVsSpeed;
        UseFfmpeg = useFfmpeg;
        FfmpegOutputArgs = ffmpegOutputArgs;
    }

    public RecordingQuality Quality { get; }
    public Guid OutputSubtype { get; }
    public string FileExtension { get; }
    public int Bitrate { get; }
    public int? EncoderQuality { get; }
    public int? EncoderQualityVsSpeed { get; }
    public bool UseFfmpeg { get; }
    public string? FfmpegOutputArgs { get; }

    public bool UseQualityRateControl => !UseFfmpeg && OutputSubtype == MfInterop.MFVideoFormat_H264 && EncoderQuality.HasValue;

    public static RecordingSettings FromQuality(RecordingQuality quality, int width, int height, int fps)
    {
        if (quality == RecordingQuality.Lossless)
        {
            return new RecordingSettings(quality, Guid.Empty, "mkv", 0, null, null, useFfmpeg: true,
                ffmpegOutputArgs: "-c:v ffv1 -level 3 -g 1 -pix_fmt bgr0");
        }

        if (quality == RecordingQuality.LosslessCompatible)
        {
            return new RecordingSettings(quality, Guid.Empty, "mp4", 0, null, null, useFfmpeg: true,
                ffmpegOutputArgs: "-c:v libx264 -preset veryfast -crf 1 -pix_fmt yuv420p -profile:v high -level 4.1 -movflags +faststart");
        }

        if (quality == RecordingQuality.Uncompressed)
        {
            return new RecordingSettings(quality, MfInterop.MFVideoFormat_RGB32, "avi", 0, null, null, useFfmpeg: false, ffmpegOutputArgs: null);
        }

        int bitrate = EstimateBitrate(width, height, fps, quality);
        (int encoderQuality, int encoderQualityVsSpeed) = quality switch
        {
            RecordingQuality.High => (90, 30),
            RecordingQuality.Balanced => (80, 50),
            RecordingQuality.Compact => (65, 70),
            _ => (80, 50)
        };

        return new RecordingSettings(quality, MfInterop.MFVideoFormat_H264, "mp4", bitrate, encoderQuality, encoderQualityVsSpeed, useFfmpeg: false, ffmpegOutputArgs: null);
    }

    private static int EstimateBitrate(int width, int height, int fps, RecordingQuality quality)
    {
        if (width <= 0 || height <= 0 || fps <= 0)
        {
            return 2_000_000;
        }

        double bitsPerPixelPerSecond = quality switch
        {
            RecordingQuality.High => 0.20,
            RecordingQuality.Compact => 0.07,
            _ => 0.12
        };

        (long min, long max) = quality switch
        {
            RecordingQuality.High => (6_000_000, 60_000_000),
            RecordingQuality.Compact => (1_500_000, 18_000_000),
            _ => (3_000_000, 35_000_000)
        };

        long raw = (long)Math.Round(width * height * fps * bitsPerPixelPerSecond);
        long bitrate = Math.Clamp(raw, min, max);
        return (int)bitrate;
    }
}

internal interface IRecordingWriter : IDisposable
{
    void WriteFrame(byte[] buffer, int length);
}

internal sealed class RecordingSession : IDisposable
{
    private readonly int _frameSize;
    private readonly BlockingCollection<byte[]> _frames;
    private readonly Task _writerTask;
    private readonly string _path;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly RecordingSettings _settings;
    private readonly object _errorLock = new();
    private string? _errorMessage;
    private bool _completed;

    public RecordingSession(string path, int width, int height, int fps, RecordingSettings settings)
    {
        _frameSize = width * height * 4;
        _path = path;
        _width = width;
        _height = height;
        _fps = fps;
        _settings = settings;
        _frames = new BlockingCollection<byte[]>(boundedCapacity: 4);
        _writerTask = Task.Run(ProcessFrames);
    }

    public bool TryEnqueue(byte[] buffer)
    {
        if (HasError)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return false;
        }

        if (buffer.Length < _frameSize)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return false;
        }

        if (!_frames.TryAdd(buffer))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return false;
        }

        return true;
    }

    private void ProcessFrames()
    {
        IRecordingWriter? recorder = null;
        try
        {
            recorder = _settings.UseFfmpeg
                ? new FfmpegRecorder(_path, _width, _height, _fps, _settings)
                : new MediaFoundationRecorder(_path, _width, _height, _fps, _settings);
            foreach (var buffer in _frames.GetConsumingEnumerable())
            {
                try
                {
                    recorder.WriteFrame(buffer, _frameSize);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            SetError(ex);
            _frames.CompleteAdding();
        }
        finally
        {
            if (recorder != null)
            {
                try
                {
                    recorder.Dispose();
                }
                catch (Exception ex)
                {
                    SetError(ex);
                }
            }
        }
    }

    public bool TryGetError(out string? message)
    {
        lock (_errorLock)
        {
            message = _errorMessage;
            return message != null;
        }
    }

    public void Dispose()
    {
        if (!_completed)
        {
            _frames.CompleteAdding();
            try
            {
                _writerTask.Wait();
            }
            catch (AggregateException ex)
            {
                SetError(ex.InnerException ?? ex);
            }
            _completed = true;
        }

        _frames.Dispose();
    }

    private void SetError(Exception ex)
    {
        lock (_errorLock)
        {
            _errorMessage ??= ex.Message;
        }
    }

    private bool HasError
    {
        get
        {
            lock (_errorLock)
            {
                return _errorMessage != null;
            }
        }
    }
}

internal sealed class MediaFoundationRecorder : IRecordingWriter
{
    private const int MfVersion = 0x00020070;
    private const int MfStartupFull = 0;
    private const int MfVideoInterlaceProgressive = 2;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly long _frameDuration;
    private long _nextTimestamp;
    private IMFSinkWriter? _writer;
    private int _streamIndex;
    private bool _finalized;

    public MediaFoundationRecorder(string path, int width, int height, int fps, RecordingSettings settings)
    {
        _width = width;
        _height = height;
        _stride = width * 4;
        _frameDuration = 10_000_000L / Math.Max(1, fps);

        MfInterop.Check(MfInterop.MFStartup(MfVersion, MfStartupFull));

        IMFMediaType? outputType = null;
        IMFMediaType? inputType = null;
        try
        {
            IMFAttributes? writerAttributes = null;
            IntPtr writerAttributesPtr = IntPtr.Zero;
            IntPtr writerPtr = IntPtr.Zero;
            try
            {
                MfInterop.Check(MfInterop.MFCreateAttributes(out writerAttributes, 1));
                MfInterop.Check(writerAttributes.SetUINT32(MfInterop.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 0));

                writerAttributesPtr = Marshal.GetIUnknownForObject(writerAttributes);
                int hr = MfInterop.MFCreateSinkWriterFromURL(path, IntPtr.Zero, writerAttributesPtr, out writerPtr);
                MfInterop.Check(hr);
                if (writerPtr == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to create sink writer (null COM pointer).");
                }

                _writer = Marshal.GetObjectForIUnknown(writerPtr) as IMFSinkWriter;
                if (_writer == null)
                {
                    throw new InvalidOperationException("Media Foundation sink writer is unavailable. If you are on a Windows N edition, install the Media Feature Pack.");
                }
            }
            finally
            {
                if (writerPtr != IntPtr.Zero)
                {
                    Marshal.Release(writerPtr);
                }
                if (writerAttributesPtr != IntPtr.Zero)
                {
                    Marshal.Release(writerAttributesPtr);
                }
                if (writerAttributes != null)
                {
                    Marshal.ReleaseComObject(writerAttributes);
                }
            }

            MfInterop.Check(MfInterop.MFCreateMediaType(out outputType));
            MfInterop.Check(outputType.SetGUID(MfInterop.MF_MT_MAJOR_TYPE, MfInterop.MFMediaType_Video));
            MfInterop.Check(outputType.SetGUID(MfInterop.MF_MT_SUBTYPE, settings.OutputSubtype));
            if (settings.OutputSubtype == MfInterop.MFVideoFormat_H264 && settings.Bitrate > 0)
            {
                MfInterop.Check(outputType.SetUINT32(MfInterop.MF_MT_AVG_BITRATE, (uint)settings.Bitrate));
            }
            MfInterop.Check(outputType.SetUINT32(MfInterop.MF_MT_INTERLACE_MODE, MfVideoInterlaceProgressive));
            MfInterop.SetAttributeSize(outputType, MfInterop.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            MfInterop.SetAttributeRatio(outputType, MfInterop.MF_MT_FRAME_RATE, (uint)fps, 1);
            MfInterop.SetAttributeRatio(outputType, MfInterop.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

            MfInterop.Check(_writer!.AddStream(outputType, out _streamIndex));

            MfInterop.Check(MfInterop.MFCreateMediaType(out inputType));
            MfInterop.Check(inputType.SetGUID(MfInterop.MF_MT_MAJOR_TYPE, MfInterop.MFMediaType_Video));
            MfInterop.Check(inputType.SetGUID(MfInterop.MF_MT_SUBTYPE, MfInterop.MFVideoFormat_RGB32));
            MfInterop.Check(inputType.SetUINT32(MfInterop.MF_MT_INTERLACE_MODE, MfVideoInterlaceProgressive));
            MfInterop.Check(inputType.SetUINT32(MfInterop.MF_MT_DEFAULT_STRIDE, (uint)_stride));
            MfInterop.Check(inputType.SetUINT32(MfInterop.MF_MT_FIXED_SIZE_SAMPLES, 1));
            MfInterop.Check(inputType.SetUINT32(MfInterop.MF_MT_ALL_SAMPLES_INDEPENDENT, 1));
            MfInterop.Check(inputType.SetUINT32(MfInterop.MF_MT_SAMPLE_SIZE, (uint)(_stride * height)));
            MfInterop.SetAttributeSize(inputType, MfInterop.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            MfInterop.SetAttributeRatio(inputType, MfInterop.MF_MT_FRAME_RATE, (uint)fps, 1);
            MfInterop.SetAttributeRatio(inputType, MfInterop.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

            MfInterop.Check(_writer.SetInputMediaType(_streamIndex, inputType, IntPtr.Zero));
            ConfigureEncoderQuality(settings);
            MfInterop.Check(_writer.BeginWriting());
        }
        finally
        {
            if (outputType != null)
            {
                Marshal.ReleaseComObject(outputType);
            }
            if (inputType != null)
            {
                Marshal.ReleaseComObject(inputType);
            }
        }
    }

    public void WriteFrame(byte[] buffer, int length)
    {
        if (_writer == null || _finalized)
        {
            return;
        }

        IMFMediaBuffer? mediaBuffer = null;
        IMFSample? sample = null;
        try
        {
            MfInterop.Check(MfInterop.MFCreateMemoryBuffer((uint)length, out mediaBuffer));
            MfInterop.Check(mediaBuffer.Lock(out IntPtr ptr, out int maxLen, out int currentLen));
            if (maxLen < length)
            {
                throw new InvalidOperationException("Media buffer is smaller than expected.");
            }

            Marshal.Copy(buffer, 0, ptr, length);
            MfInterop.Check(mediaBuffer.Unlock());
            MfInterop.Check(mediaBuffer.SetCurrentLength((uint)length));

            MfInterop.Check(MfInterop.MFCreateSample(out sample));
            MfInterop.Check(sample.AddBuffer(mediaBuffer));
            MfInterop.Check(sample.SetSampleTime(_nextTimestamp));
            MfInterop.Check(sample.SetSampleDuration(_frameDuration));

            MfInterop.Check(_writer.WriteSample(_streamIndex, sample));
            _nextTimestamp += _frameDuration;
        }
        finally
        {
            if (mediaBuffer != null)
            {
                Marshal.ReleaseComObject(mediaBuffer);
            }
            if (sample != null)
            {
                Marshal.ReleaseComObject(sample);
            }
        }
    }

    public void Dispose()
    {
        if (_writer != null && !_finalized)
        {
            try
            {
                MfInterop.Check(_writer.Finalize_());
            }
            catch
            {
                // Ignore finalize errors to avoid losing the file.
            }
            _finalized = true;
        }

        if (_writer != null)
        {
            Marshal.ReleaseComObject(_writer);
            _writer = null;
        }

        MfInterop.MFShutdown();
    }

    private void ConfigureEncoderQuality(RecordingSettings settings)
    {
        if (_writer == null || !settings.UseQualityRateControl)
        {
            return;
        }

        Guid service = Guid.Empty;
        Guid iid = typeof(ICodecAPI).GUID;
        IntPtr codecPtr = IntPtr.Zero;
        try
        {
            int hr = _writer.GetServiceForStream(_streamIndex, ref service, ref iid, out codecPtr);
            if (hr < 0 || codecPtr == IntPtr.Zero)
            {
                return;
            }

            var codecApi = Marshal.GetObjectForIUnknown(codecPtr) as ICodecAPI;
            if (codecApi == null)
            {
                return;
            }

            try
            {
                object rateControl = (uint)MfInterop.AVEncCommonRateControlMode_Quality;
                Guid rateControlGuid = MfInterop.CODECAPI_AVEncCommonRateControlMode;
                codecApi.SetValue(ref rateControlGuid, ref rateControl);

                if (settings.EncoderQuality.HasValue)
                {
                    object quality = (uint)Math.Clamp(settings.EncoderQuality.Value, 1, 100);
                    Guid qualityGuid = MfInterop.CODECAPI_AVEncCommonQuality;
                    codecApi.SetValue(ref qualityGuid, ref quality);
                }

                if (settings.EncoderQualityVsSpeed.HasValue)
                {
                    object qualityVsSpeed = (uint)Math.Clamp(settings.EncoderQualityVsSpeed.Value, 1, 100);
                    Guid qualityVsSpeedGuid = MfInterop.CODECAPI_AVEncCommonQualityVsSpeed;
                    codecApi.SetValue(ref qualityVsSpeedGuid, ref qualityVsSpeed);
                }

                if (settings.Bitrate > 0)
                {
                    object meanBitrate = (uint)settings.Bitrate;
                    Guid meanBitrateGuid = MfInterop.CODECAPI_AVEncCommonMeanBitRate;
                    codecApi.SetValue(ref meanBitrateGuid, ref meanBitrate);

                    object maxBitrate = (uint)Math.Max(1, settings.Bitrate + (settings.Bitrate / 2));
                    Guid maxBitrateGuid = MfInterop.CODECAPI_AVEncCommonMaxBitRate;
                    codecApi.SetValue(ref maxBitrateGuid, ref maxBitrate);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(codecApi);
            }
        }
        catch
        {
            // Ignore encoder configuration failures to keep recording available.
        }
    }
}

internal sealed class FfmpegRecorder : IRecordingWriter
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Task<string> _stderrTask;
    private bool _disposed;

    public FfmpegRecorder(string path, int width, int height, int fps, RecordingSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.FfmpegOutputArgs))
        {
            throw new InvalidOperationException("Recording requires ffmpeg output settings.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = BuildArguments(path, width, height, fps, settings.FfmpegOutputArgs),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        _stdin = _process.StandardInput.BaseStream;
        _stderrTask = Task.Run(() => _process.StandardError.ReadToEnd());
    }

    public void WriteFrame(byte[] buffer, int length)
    {
        if (_disposed)
        {
            return;
        }

        if (_process.HasExited)
        {
            throw new InvalidOperationException(BuildExitMessage());
        }

        _stdin.Write(buffer, 0, length);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stdin.Flush();
        }
        catch
        {
            // Ignore flush errors.
        }

        try
        {
            _stdin.Close();
        }
        catch
        {
            // Ignore close errors.
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.WaitForExit();
            }
        }
        catch
        {
            // Ignore wait errors.
        }

        if (_process.HasExited && _process.ExitCode != 0)
        {
            throw new InvalidOperationException(BuildExitMessage());
        }

        _process.Dispose();
    }

    private string BuildExitMessage()
    {
        string errorOutput = string.Empty;
        if (_stderrTask.IsCompletedSuccessfully)
        {
            errorOutput = _stderrTask.Result;
        }
        else
        {
            try
            {
                errorOutput = _process.StandardError.ReadToEnd();
            }
            catch
            {
                errorOutput = string.Empty;
            }
        }

        string trimmed = errorOutput.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? $"ffmpeg exited with code {_process.ExitCode}."
            : trimmed;
    }

    private static string BuildArguments(string path, int width, int height, int fps, string outputArgs)
    {
        string safePath = path.Replace("\"", "\\\"");
        return $"-hide_banner -loglevel error -y -f rawvideo -pix_fmt bgra -s {width}x{height} -r {fps} -i - {outputArgs} -an \"{safePath}\"";
    }
}

internal static class MfInterop
{
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MF_MT_FIXED_SIZE_SAMPLES = new("b8ebefaf-b718-4e04-b0a9-116775e3321b");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46f0e25e595c");
    public static readonly Guid MF_MT_SAMPLE_SIZE = new("dad3ab78-1990-408b-bce2-1e2ebc0a76e5");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41d1-9db1-41a7f2ed9921");
    public static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CODECAPI_AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    public static readonly Guid CODECAPI_AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid CODECAPI_AVEncCommonMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public const uint AVEncCommonRateControlMode_Quality = 3;
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

    public static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public static void SetAttributeSize(IMFAttributes attributes, Guid guidKey, uint width, uint height)
    {
        ulong packed = ((ulong)width << 32) | height;
        Check(attributes.SetUINT64(guidKey, packed));
    }

    public static void SetAttributeRatio(IMFAttributes attributes, Guid guidKey, uint numerator, uint denominator)
    {
        ulong packed = ((ulong)numerator << 32) | denominator;
        Check(attributes.SetUINT64(guidKey, packed));
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(int version, int dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string outputUrl, IntPtr byteStream, IntPtr attributes, out IntPtr sinkWriter);
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(Guid guidKey, IntPtr value);
    [PreserveSig] int GetItemType(Guid guidKey, out int type);
    [PreserveSig] int CompareItem(Guid guidKey, IntPtr value, out bool result);
    [PreserveSig] int Compare(IMFAttributes attributes, int matchType, out bool result);
    [PreserveSig] int GetUINT32(Guid guidKey, out uint value);
    [PreserveSig] int GetUINT64(Guid guidKey, out ulong value);
    [PreserveSig] int GetDouble(Guid guidKey, out double value);
    [PreserveSig] int GetGUID(Guid guidKey, out Guid value);
    [PreserveSig] int GetStringLength(Guid guidKey, out uint length);
    [PreserveSig] int GetString(Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] char[] value, uint size, out uint length);
    [PreserveSig] int GetAllocatedString(Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string value, out uint length);
    [PreserveSig] int GetBlobSize(Guid guidKey, out uint size);
    [PreserveSig] int GetBlob(Guid guidKey, [Out] byte[] buffer, uint size, out uint length);
    [PreserveSig] int GetAllocatedBlob(Guid guidKey, out IntPtr buffer, out uint size);
    [PreserveSig] int GetUnknown(Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int SetItem(Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(Guid guidKey, uint value);
    [PreserveSig] int SetUINT64(Guid guidKey, ulong value);
    [PreserveSig] int SetDouble(Guid guidKey, double value);
    [PreserveSig] int SetGUID(Guid guidKey, Guid value);
    [PreserveSig] int SetString(Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(Guid guidKey, byte[] buffer, uint size);
    [PreserveSig] int SetUnknown(Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid guidKey, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
}

[ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    [PreserveSig] int GetItem(Guid guidKey, IntPtr value);
    [PreserveSig] int GetItemType(Guid guidKey, out int type);
    [PreserveSig] int CompareItem(Guid guidKey, IntPtr value, out bool result);
    [PreserveSig] int Compare(IMFAttributes attributes, int matchType, out bool result);
    [PreserveSig] int GetUINT32(Guid guidKey, out uint value);
    [PreserveSig] int GetUINT64(Guid guidKey, out ulong value);
    [PreserveSig] int GetDouble(Guid guidKey, out double value);
    [PreserveSig] int GetGUID(Guid guidKey, out Guid value);
    [PreserveSig] int GetStringLength(Guid guidKey, out uint length);
    [PreserveSig] int GetString(Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] char[] value, uint size, out uint length);
    [PreserveSig] int GetAllocatedString(Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string value, out uint length);
    [PreserveSig] int GetBlobSize(Guid guidKey, out uint size);
    [PreserveSig] int GetBlob(Guid guidKey, [Out] byte[] buffer, uint size, out uint length);
    [PreserveSig] int GetAllocatedBlob(Guid guidKey, out IntPtr buffer, out uint size);
    [PreserveSig] int GetUnknown(Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int SetItem(Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(Guid guidKey, uint value);
    [PreserveSig] int SetUINT64(Guid guidKey, ulong value);
    [PreserveSig] int SetDouble(Guid guidKey, double value);
    [PreserveSig] int SetGUID(Guid guidKey, Guid value);
    [PreserveSig] int SetString(Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(Guid guidKey, byte[] buffer, uint size);
    [PreserveSig] int SetUnknown(Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid guidKey, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
    [PreserveSig] int GetSampleFlags(out int flags);
    [PreserveSig] int SetSampleFlags(int flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out int count);
    [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(int index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out int totalLength);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int currentLength);
    [PreserveSig] int SetCurrentLength(uint currentLength);
    [PreserveSig] int GetMaxLength(out int maxLength);
}

[ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    [PreserveSig] int AddStream(IMFMediaType mediaType, out int streamIndex);
    [PreserveSig] int SetInputMediaType(int streamIndex, IMFMediaType mediaType, IntPtr attributes);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
    [PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
    [PreserveSig] int PlaceMarker(int streamIndex, IntPtr context);
    [PreserveSig] int NotifyEndOfSegment(int streamIndex);
    [PreserveSig] int Flush(int streamIndex);
    [PreserveSig] int Finalize_();
    [PreserveSig] int GetServiceForStream(int streamIndex, ref Guid service, ref Guid riid, out IntPtr result);
    [PreserveSig] int GetStatistics(int streamIndex, out IntPtr stats);
}

[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int IsModifiable(ref Guid api);
    [PreserveSig] int GetParameterRange(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object valueMin, [MarshalAs(UnmanagedType.Struct)] out object valueMax, [MarshalAs(UnmanagedType.Struct)] out object steppingDelta);
    [PreserveSig] int GetParameterValues(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object values, [MarshalAs(UnmanagedType.Struct)] out object descriptions);
    [PreserveSig] int GetDefaultValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int GetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int SetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
    [PreserveSig] int RegisterForEvent(ref Guid api, IntPtr userData);
    [PreserveSig] int UnregisterForEvent(ref Guid api);
}
