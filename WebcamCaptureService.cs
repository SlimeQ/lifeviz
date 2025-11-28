using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace lifeviz;

internal sealed class WebcamCaptureService : IDisposable
{
    private readonly ConcurrentDictionary<string, CaptureData> _captureData = new();
    private readonly ConcurrentDictionary<string, MediaCapture> _mediaCaptures = new();
    private readonly ConcurrentDictionary<string, MediaFrameReader> _frameReaders = new();
    private readonly ConcurrentDictionary<string, Task<bool>> _initializeTasks = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private class CaptureData
    {
        public readonly object FrameLock = new();
        public byte[]? LatestBuffer;
        public int LatestWidth;
        public int LatestHeight;
        public byte[]? DownscaledBuffer;
        public byte[]? SourceCopyBuffer;
    }

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

        if (!_captureData.TryGetValue(cameraId, out var data))
        {
            return null;
        }

        byte[]? latest;
        int width;
        int height;
        lock (data.FrameLock)
        {
            latest = data.LatestBuffer;
            width = data.LatestWidth;
            height = data.LatestHeight;
            if (latest == null || width <= 0 || height <= 0)
            {
                return null;
            }

            int downscaledLength = targetWidth * targetHeight * 4;
            if (data.DownscaledBuffer == null || data.DownscaledBuffer.Length != downscaledLength)
            {
                data.DownscaledBuffer = new byte[downscaledLength];
            }

            Downscale(latest, width, height, data.DownscaledBuffer, targetWidth, targetHeight);

            if (includeSource)
            {
                if (data.SourceCopyBuffer == null || data.SourceCopyBuffer.Length != latest.Length)
                {
                    data.SourceCopyBuffer = new byte[latest.Length];
                }
                Buffer.BlockCopy(latest, 0, data.SourceCopyBuffer, 0, latest.Length);
            }
            else
            {
                data.SourceCopyBuffer = null;
            }

            return new WebcamFrame(data.DownscaledBuffer, targetWidth, targetHeight,
                includeSource ? data.SourceCopyBuffer : null,
                width,
                height);
        }
    }

    public bool EnsureInitialized(string cameraId)
    {
        if (_mediaCaptures.ContainsKey(cameraId))
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
            Reset(cameraId);
            return false;
        }
    }

    private async Task<bool> EnsureInitializedAsync(string cameraId)
    {
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_mediaCaptures.ContainsKey(cameraId))
            {
                return true;
            }

            // If another initialization for the same device is in progress, await it.
            if (_initializeTasks.TryGetValue(cameraId, out var existingTask))
            {
                return await existingTask.ConfigureAwait(false);
            }

            var newTask = Task.Run(() => InitializeInternalAsync(cameraId));
            _initializeTasks[cameraId] = newTask;
            
            var result = await newTask.ConfigureAwait(false);

            // Clean up the task from the dictionary once it's complete.
            _initializeTasks.TryRemove(cameraId, out _);
            return result;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<bool> InitializeInternalAsync(string cameraId)
    {
        try
        {
            Logger.Info($"Initializing webcam: {cameraId}");
            var mediaCapture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                VideoDeviceId = cameraId,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly
            };

            await mediaCapture.InitializeAsync(settings);
            _mediaCaptures[cameraId] = mediaCapture;

            var source = mediaCapture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
            if (source == null)
            {
                Logger.Warn($"No color source found for webcam {cameraId}");
                Reset(cameraId);
                return false;
            }

            var frameReader = await mediaCapture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            frameReader.FrameArrived += (sender, args) => FrameReaderOnFrameArrived(sender, args, cameraId);
            _frameReaders[cameraId] = frameReader;

            var status = await frameReader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                Logger.Warn($"MediaFrameReader failed to start for webcam {cameraId}: {status}");
                Reset(cameraId);
                return false;
            }

            _captureData.TryAdd(cameraId, new CaptureData());
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Webcam initialization failed: {cameraId}", ex);
            Reset(cameraId);
            return false;
        }
    }

    private void FrameReaderOnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args, string deviceId)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var softwareBitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (softwareBitmap == null)
            {
                return;
            }
            
            using var converted = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            CopyToLatestBuffer(converted, deviceId);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Webcam frame read failed for {deviceId}: {ex.Message}");
        }
    }

    private void CopyToLatestBuffer(SoftwareBitmap bitmap, string deviceId)
    {
        if (!_captureData.TryGetValue(deviceId, out var data))
        {
            return;
        }
        
        int required = bitmap.PixelWidth * bitmap.PixelHeight * 4;
        var scratch = ArrayPool<byte>.Shared.Rent(required);
        try
        {
            var ibuffer = new Windows.Storage.Streams.Buffer((uint)required);
            bitmap.CopyToBuffer(ibuffer);
            ibuffer.CopyTo(scratch);

            lock (data.FrameLock)
            {
                if (data.LatestBuffer == null || data.LatestBuffer.Length != required)
                {
                    data.LatestBuffer = new byte[required];
                }

                Buffer.BlockCopy(scratch, 0, data.LatestBuffer, 0, required);
                data.LatestWidth = bitmap.PixelWidth;
                data.LatestHeight = bitmap.PixelHeight;
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

        Parallel.For(0, targetHeight, row =>
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
        });
    }

    public void Reset(string? cameraId = null)
    {
        if (string.IsNullOrWhiteSpace(cameraId))
        {
            var allIds = _mediaCaptures.Keys.ToList();
            foreach (var id in allIds)
            {
                Reset(id);
            }
            return;
        }

        try
        {
            if (_frameReaders.TryRemove(cameraId, out var frameReader))
            {
                // The FrameArrived handler is an anonymous lambda, can't easily unsubscribe.
                // Assuming Dispose() is enough to stop callbacks.
                frameReader.StopAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
                frameReader.Dispose();
            }

            if (_mediaCaptures.TryRemove(cameraId, out var mediaCapture))
            {
                mediaCapture.Dispose();
            }
            
            _initializeTasks.TryRemove(cameraId, out _);
            
            if(_captureData.TryRemove(cameraId, out var data))
            {
                lock (data.FrameLock)
                {
                    data.LatestBuffer = null;
                    data.DownscaledBuffer = null;
                    data.SourceCopyBuffer = null;
                    data.LatestWidth = 0;
                    data.LatestHeight = 0;
                }
            }

            Logger.Info($"Webcam capture reset for device: {cameraId}");
        }
        catch(Exception ex)
        {
            Logger.Error($"Error during reset for device {cameraId}", ex);
        }
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
