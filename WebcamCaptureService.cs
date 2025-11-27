using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace lifeviz;

internal sealed class WebcamCaptureService : IDisposable
{
    private readonly object _frameLock = new();
    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _frameReader;
    private byte[]? _latestBuffer;
    private int _latestWidth;
    private int _latestHeight;
    private string? _currentDeviceId;
    private Task<bool>? _initializeTask;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private byte[]? _downscaledBuffer;
    private byte[]? _sourceCopyBuffer;

    public IReadOnlyList<CameraInfo> EnumerateCameras()
    {
        try
        {
            var devices = Task.Run(() => DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask())
                .ConfigureAwait(false).GetAwaiter().GetResult();
            return devices.Select(d => new CameraInfo(d.Id, d.Name)).ToList();
        }
        catch
        {
            Logger.Error("Failed to enumerate webcams.");
            return Array.Empty<CameraInfo>();
        }
    }

    public WebcamFrame? CaptureFrame(string cameraId, int targetWidth, int targetHeight, bool includeSource = true)
    {
        if (string.IsNullOrWhiteSpace(cameraId) || targetWidth <= 0 || targetHeight <= 0)
        {
            return null;
        }

        if (!EnsureInitialized(cameraId))
        {
            return null;
        }

        byte[]? latest;
        int width;
        int height;
        lock (_frameLock)
        {
            latest = _latestBuffer;
            width = _latestWidth;
            height = _latestHeight;
            if (latest == null || width <= 0 || height <= 0)
            {
                return null;
            }

            int downscaledLength = targetWidth * targetHeight * 4;
            if (_downscaledBuffer == null || _downscaledBuffer.Length != downscaledLength)
            {
                _downscaledBuffer = new byte[downscaledLength];
            }

            Downscale(latest, width, height, _downscaledBuffer, targetWidth, targetHeight);

            if (includeSource)
            {
                if (_sourceCopyBuffer == null || _sourceCopyBuffer.Length != latest.Length)
                {
                    _sourceCopyBuffer = new byte[latest.Length];
                }
                System.Buffer.BlockCopy(latest, 0, _sourceCopyBuffer, 0, latest.Length);
            }
            else
            {
                _sourceCopyBuffer = null;
            }
        }

        return new WebcamFrame(_downscaledBuffer!, targetWidth, targetHeight,
            includeSource ? _sourceCopyBuffer : null,
            width,
            height);
    }

    private bool EnsureInitialized(string cameraId)
    {
        if (_mediaCapture != null && string.Equals(_currentDeviceId, cameraId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return EnsureInitializedAsync(cameraId).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Error($"Webcam ensure-init failed: {cameraId}", ex);
            Reset();
            return false;
        }
    }

    private async Task<bool> EnsureInitializedAsync(string cameraId)
    {
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_mediaCapture != null && string.Equals(_currentDeviceId, cameraId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Reset();
            _initializeTask = Task.Run(() => InitializeInternalAsync(cameraId));
        }
        finally
        {
            _initLock.Release();
        }

        return _initializeTask != null && await _initializeTask.ConfigureAwait(false);
    }

    private async Task<bool> InitializeInternalAsync(string cameraId)
    {
        try
        {
            Logger.Info($"Initializing webcam: {cameraId}");
            _mediaCapture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                VideoDeviceId = cameraId,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly
            };

            await _mediaCapture.InitializeAsync(settings);

            var source = _mediaCapture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (source == null)
            {
                Logger.Warn($"No color source found for webcam {cameraId}");
                Reset();
                return false;
            }

            _frameReader = await _mediaCapture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            _frameReader.FrameArrived += FrameReaderOnFrameArrived;
            var status = await _frameReader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                Logger.Warn($"MediaFrameReader failed to start for webcam {cameraId}: {status}");
                Reset();
                return false;
            }

            _currentDeviceId = cameraId;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Webcam initialization failed: {cameraId}", ex);
            Reset();
            return false;
        }
    }

    private void FrameReaderOnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var softwareBitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (softwareBitmap == null)
            {
                return;
            }

            SoftwareBitmap? converted = null;
            try
            {
                converted = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                CopyToLatestBuffer(converted);
            }
            finally
            {
                converted?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Webcam frame read failed: {ex.Message}");
            // Ignore capture errors; the next frame may recover.
        }
    }

    private void CopyToLatestBuffer(SoftwareBitmap bitmap)
    {
        int required = bitmap.PixelWidth * bitmap.PixelHeight * 4;
        var scratch = ArrayPool<byte>.Shared.Rent(required);
        try
        {
            var ibuffer = new Windows.Storage.Streams.Buffer((uint)required);
            bitmap.CopyToBuffer(ibuffer);
            ibuffer.CopyTo(scratch);

            lock (_frameLock)
            {
                if (_latestBuffer == null || _latestBuffer.Length != required)
                {
                    _latestBuffer = new byte[required];
                }

                System.Buffer.BlockCopy(scratch, 0, _latestBuffer, 0, required);
                _latestWidth = bitmap.PixelWidth;
                _latestHeight = bitmap.PixelHeight;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static void Downscale(byte[] source, int sourceWidth, int sourceHeight, byte[] destination, int targetWidth, int targetHeight)
    {
        double scaleX = sourceWidth / (double)targetWidth;
        double scaleY = sourceHeight / (double)targetHeight;
        int destStride = targetWidth * 4;
        int sourceStride = sourceWidth * 4;

        for (int row = 0; row < targetHeight; row++)
        {
            int srcY = Math.Min(sourceHeight - 1, (int)Math.Floor(row * scaleY));
            int destRowOffset = row * destStride;
            int srcRowOffset = srcY * sourceStride;

            for (int col = 0; col < targetWidth; col++)
            {
                int srcX = Math.Min(sourceWidth - 1, (int)Math.Floor(col * scaleX));
                int destIndex = destRowOffset + (col * 4);
                int srcIndex = srcRowOffset + (srcX * 4);

                destination[destIndex] = source[srcIndex];
                destination[destIndex + 1] = source[srcIndex + 1];
                destination[destIndex + 2] = source[srcIndex + 2];
                destination[destIndex + 3] = 255;
            }
        }
    }

    public void Reset()
    {
        try
        {
            if (_frameReader != null)
            {
                _frameReader.FrameArrived -= FrameReaderOnFrameArrived;
                _frameReader.StopAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
                _frameReader.Dispose();
            }
        }
        catch
        {
            // Best-effort cleanup.
        }

        _frameReader = null;

        _mediaCapture?.Dispose();
        _mediaCapture = null;
        _currentDeviceId = null;
        _initializeTask = null;

        lock (_frameLock)
        {
            _latestBuffer = null;
            _latestWidth = 0;
            _latestHeight = 0;
            _downscaledBuffer = null;
            _sourceCopyBuffer = null;
        }

        Logger.Info("Webcam capture reset.");
    }

    public void Dispose() => Reset();

    internal readonly struct CameraInfo
    {
        public CameraInfo(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
    }

    internal sealed class WebcamFrame
    {
        public WebcamFrame(byte[] overlayDownscaled, int downscaledWidth, int downscaledHeight, byte[]? overlaySource, int sourceWidth, int sourceHeight)
        {
            OverlayDownscaled = overlayDownscaled;
            DownscaledWidth = downscaledWidth;
            DownscaledHeight = downscaledHeight;
            OverlaySource = overlaySource;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
        }

        public byte[] OverlayDownscaled { get; }
        public int DownscaledWidth { get; }
        public int DownscaledHeight { get; }
        public byte[]? OverlaySource { get; }
        public int SourceWidth { get; }
        public int SourceHeight { get; }
    }
}
