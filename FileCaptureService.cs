using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace lifeviz;

internal sealed class FileCaptureService : IDisposable
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg"
    };

    private readonly Dictionary<string, FileSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public bool TryGetOrAdd(string path, out FileSourceInfo info, out string? error)
    {
        info = default;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No file path provided.";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            error = "Invalid file path.";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = "File not found.";
            return false;
        }

        lock (_lock)
        {
            if (_sessions.TryGetValue(fullPath, out var existing))
            {
                info = existing.GetInfo();
                return true;
            }
        }

        try
        {
            var session = CreateSession(fullPath);
            info = session.GetInfo();
            lock (_lock)
            {
                _sessions[fullPath] = session;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Error($"Failed to load file source: {fullPath}", ex);
            return false;
        }
    }

    public FileCaptureFrame? CaptureFrame(string path, int targetWidth, int targetHeight, FitMode fitMode, bool includeSource = true)
    {
        if (string.IsNullOrWhiteSpace(path) || targetWidth <= 0 || targetHeight <= 0)
        {
            return null;
        }

        if (!TryNormalizePath(path, out var fullPath))
        {
            return null;
        }

        FileSession? session;
        lock (_lock)
        {
            _sessions.TryGetValue(fullPath, out session);
        }

        if (session == null)
        {
            if (!TryGetOrAdd(fullPath, out _, out _))
            {
                return null;
            }

            lock (_lock)
            {
                _sessions.TryGetValue(fullPath, out session);
            }
        }

        return session?.CaptureFrame(targetWidth, targetHeight, fitMode, includeSource);
    }

    public void Remove(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!TryNormalizePath(path, out var fullPath))
        {
            return;
        }

        FileSession? session;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(fullPath, out session))
            {
                return;
            }
            _sessions.Remove(fullPath);
        }

        session.Dispose();
    }

    public void Clear()
    {
        List<FileSession> sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    public void Dispose() => Clear();

    private static FileSession CreateSession(string path)
    {
        string extension = Path.GetExtension(path);
        if (VideoExtensions.Contains(extension))
        {
            return new VideoSession(path);
        }

        bool isGif = string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        if (isGif)
        {
            return new ImageSequenceSession(path, decoder.Frames, useDelays: true);
        }

        return new ImageSequenceSession(path, decoder.Frames.Take(1).ToList(), useDelays: false);
    }

    private static bool TryNormalizePath(string path, out string fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private abstract class FileSession : IDisposable
    {
        protected FileSession(string path, string displayName, int width, int height)
        {
            Path = path;
            DisplayName = displayName;
            Width = width;
            Height = height;
        }

        public string Path { get; }
        public string DisplayName { get; }
        public int Width { get; protected set; }
        public int Height { get; protected set; }
        public abstract FileSourceKind Kind { get; }

        public FileSourceInfo GetInfo() => new(Path, DisplayName, Width, Height, Kind);

        public abstract FileCaptureFrame? CaptureFrame(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource);

        public abstract void Dispose();
    }

    private sealed class ImageSequenceSession : FileSession
    {
        private readonly FrameData[] _frames;
        private readonly double[] _frameEndTimesMs;
        private readonly double _totalDurationMs;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private byte[]? _downscaledBuffer;

        public ImageSequenceSession(string path, IReadOnlyList<BitmapFrame> frames, bool useDelays)
            : base(path, System.IO.Path.GetFileName(path), 0, 0)
        {
            if (frames.Count == 0)
            {
                throw new InvalidOperationException("Image file contains no frames.");
            }

            _frames = new FrameData[frames.Count];
            _frameEndTimesMs = new double[frames.Count];

            double totalMs = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                var frameData = BuildFrameData(frame);
                if (i == 0)
                {
                    Width = frameData.Width;
                    Height = frameData.Height;
                }

                double delayMs = useDelays ? GetFrameDelayMs(frame) : double.MaxValue;
                if (!useDelays && frames.Count == 1)
                {
                    delayMs = double.MaxValue;
                }

                totalMs = SafeAdd(totalMs, delayMs);
                _frames[i] = frameData;
                _frameEndTimesMs[i] = totalMs;
            }

            _totalDurationMs = useDelays && frames.Count > 1 ? totalMs : double.MaxValue;
        }

        public override FileSourceKind Kind => _frames.Length > 1 ? FileSourceKind.Gif : FileSourceKind.Image;

        public override FileCaptureFrame? CaptureFrame(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
            {
                return null;
            }

            var frame = _frames.Length == 1 ? _frames[0] : GetCurrentFrame();

            int downscaledLength = targetWidth * targetHeight * 4;
            if (_downscaledBuffer == null || _downscaledBuffer.Length != downscaledLength)
            {
                _downscaledBuffer = new byte[downscaledLength];
            }

            Downscale(frame.Buffer, frame.Width, frame.Height, _downscaledBuffer, targetWidth, targetHeight, fitMode);

            return new FileCaptureFrame(_downscaledBuffer, targetWidth, targetHeight,
                includeSource ? frame.Buffer : null,
                frame.Width,
                frame.Height);
        }

        public override void Dispose()
        {
            // No unmanaged resources to clean up.
        }

        private FrameData GetCurrentFrame()
        {
            if (_frames.Length == 1)
            {
                return _frames[0];
            }

            double mod = _totalDurationMs <= 0 ? 0 : _clock.Elapsed.TotalMilliseconds % _totalDurationMs;
            for (int i = 0; i < _frameEndTimesMs.Length; i++)
            {
                if (mod <= _frameEndTimesMs[i])
                {
                    return _frames[i];
                }
            }

            return _frames[^1];
        }

        private static FrameData BuildFrameData(BitmapSource source)
        {
            BitmapSource frame = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int width = frame.PixelWidth;
            int height = frame.PixelHeight;
            int stride = width * 4;
            var buffer = new byte[stride * height];
            frame.CopyPixels(buffer, stride, 0);
            return new FrameData(buffer, width, height);
        }

        private static double GetFrameDelayMs(BitmapFrame frame)
        {
            const int defaultDelay = 10;
            int delay = defaultDelay;
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/grctlext/Delay"))
            {
                try
                {
                    object? value = metadata.GetQuery("/grctlext/Delay");
                    if (value is ushort delayValue)
                    {
                        delay = delayValue;
                    }
                    else if (value is int delayInt)
                    {
                        delay = delayInt;
                    }
                }
                catch
                {
                    delay = defaultDelay;
                }
            }

            if (delay <= 0)
            {
                delay = defaultDelay;
            }

            return delay * 10d;
        }

        private static double SafeAdd(double current, double delta)
        {
            if (double.IsInfinity(current) || double.IsInfinity(delta))
            {
                return double.MaxValue;
            }

            double next = current + delta;
            return double.IsInfinity(next) ? double.MaxValue : next;
        }
    }

    private sealed class VideoSession : FileSession
    {
        private readonly MediaPlayer _player;
        private readonly DrawingVisual _visual = new();
        private RenderTargetBitmap? _renderTarget;
        private byte[]? _latestBuffer;
        private byte[]? _downscaledBuffer;
        private bool _isReady;
        private bool _hasError;

        // Throttling and Caching
        private readonly Stopwatch _renderClock = Stopwatch.StartNew();
        private double _lastRenderTimeMs;
        private const double MinFrameIntervalMs = 33.0; // Cap at ~30 FPS
        private bool _frameUpdated;
        private int _lastTargetWidth;
        private int _lastTargetHeight;
        private FitMode _lastFitMode;

        private int _renderWidth;
        private int _renderHeight;

        public VideoSession(string path)
            : base(path, System.IO.Path.GetFileName(path), 0, 0)
        {
            _player = new MediaPlayer
            {
                Volume = 0,
                IsMuted = true
            };
            _player.MediaOpened += PlayerOnMediaOpened;
            _player.MediaEnded += PlayerOnMediaEnded;
            _player.MediaFailed += PlayerOnMediaFailed;
            _player.Open(new Uri(path, UriKind.Absolute));
            _player.Play();
        }

        public override FileSourceKind Kind => FileSourceKind.Video;

        public override FileCaptureFrame? CaptureFrame(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource)
        {
            if (_hasError || !_isReady || targetWidth <= 0 || targetHeight <= 0 || _renderWidth <= 0 || _renderHeight <= 0)
            {
                return null;
            }

            // Attempt to render the latest frame from the video player.
            // This method now includes throttling to avoid blocking the UI thread too often.
            if (!RenderLatestFrame())
            {
                return null;
            }

            // If the source video frame hasn't changed and the requested output dimensions/mode are the same,
            // we can reuse the previously downscaled buffer to save CPU cycles.
            bool paramsChanged = targetWidth != _lastTargetWidth || targetHeight != _lastTargetHeight || fitMode != _lastFitMode;

            if (!_frameUpdated && !paramsChanged && _downscaledBuffer != null && _latestBuffer != null)
            {
                return new FileCaptureFrame(_downscaledBuffer, targetWidth, targetHeight,
                    includeSource ? _latestBuffer : null,
                    _renderWidth,
                    _renderHeight);
            }

            int downscaledLength = targetWidth * targetHeight * 4;
            if (_downscaledBuffer == null || _downscaledBuffer.Length != downscaledLength)
            {
                _downscaledBuffer = new byte[downscaledLength];
            }

            Downscale(_latestBuffer!, _renderWidth, _renderHeight, _downscaledBuffer, targetWidth, targetHeight, fitMode);

            _frameUpdated = false;
            _lastTargetWidth = targetWidth;
            _lastTargetHeight = targetHeight;
            _lastFitMode = fitMode;

            return new FileCaptureFrame(_downscaledBuffer, targetWidth, targetHeight,
                includeSource ? _latestBuffer : null,
                _renderWidth,
                _renderHeight);
        }

        public override void Dispose()
        {
            _player.MediaOpened -= PlayerOnMediaOpened;
            _player.MediaEnded -= PlayerOnMediaEnded;
            _player.MediaFailed -= PlayerOnMediaFailed;
            _player.Close();
        }

        private void PlayerOnMediaOpened(object? sender, EventArgs e)
        {
            Width = _player.NaturalVideoWidth;
            Height = _player.NaturalVideoHeight;

            if (Width > 0 && Height > 0)
            {
                // Cap render resolution to max 1080p to avoid performance issues with 4K/8K videos
                // while preserving aspect ratio.
                if (Width > 1920 || Height > 1080)
                {
                    double aspect = (double)Width / Height;
                    if (Width > 1920)
                    {
                        _renderWidth = 1920;
                        _renderHeight = (int)(1920 / aspect);
                    }
                    else
                    {
                        _renderHeight = 1080;
                        _renderWidth = (int)(1080 * aspect);
                    }
                }
                else
                {
                    _renderWidth = Width;
                    _renderHeight = Height;
                }
                _isReady = true;
            }
            else
            {
                _renderWidth = 0;
                _renderHeight = 0;
                _isReady = false;
            }
        }

        private void PlayerOnMediaEnded(object? sender, EventArgs e)
        {
            _player.Position = TimeSpan.Zero;
            _player.Play();
        }

        private void PlayerOnMediaFailed(object? sender, ExceptionEventArgs e)
        {
            _hasError = true;
            Logger.Warn($"Video failed to play: {Path} ({e.ErrorException.Message})");
        }

        private bool RenderLatestFrame()
        {
            if (_renderWidth <= 0 || _renderHeight <= 0)
            {
                return false;
            }

            // Throttling: Check if enough time has passed since the last expensive render
            double now = _renderClock.Elapsed.TotalMilliseconds;
            if (_latestBuffer != null && (now - _lastRenderTimeMs < MinFrameIntervalMs))
            {
                return true;
            }
            _lastRenderTimeMs = now;

            if (_renderTarget == null || _renderTarget.PixelWidth != _renderWidth || _renderTarget.PixelHeight != _renderHeight)
            {
                _renderTarget = new RenderTargetBitmap(_renderWidth, _renderHeight, 96, 96, PixelFormats.Pbgra32);
            }

            using (var dc = _visual.RenderOpen())
            {
                // Draw at scaled resolution
                var rect = new System.Windows.Rect(0, 0, _renderWidth, _renderHeight);
                dc.DrawRectangle(Brushes.Black, null, rect);
                dc.DrawVideo(_player, rect);
            }

            _renderTarget.Render(_visual);

            int required = _renderWidth * _renderHeight * 4;
            if (_latestBuffer == null || _latestBuffer.Length != required)
            {
                _latestBuffer = new byte[required];
            }

            _renderTarget.CopyPixels(_latestBuffer, _renderWidth * 4, 0);
            _frameUpdated = true;
            return true;
        }
    }

    internal readonly struct FileSourceInfo
    {
        public FileSourceInfo(string path, string displayName, int width, int height, FileSourceKind kind)
        {
            Path = path;
            DisplayName = displayName;
            Width = width;
            Height = height;
            Kind = kind;
        }

        public string Path { get; }
        public string DisplayName { get; }
        public int Width { get; }
        public int Height { get; }
        public FileSourceKind Kind { get; }
    }

    internal readonly struct FileCaptureFrame
    {
        public FileCaptureFrame(byte[] overlayDownscaled, int downscaledWidth, int downscaledHeight, byte[]? overlaySource, int sourceWidth, int sourceHeight)
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

    internal enum FileSourceKind
    {
        Image,
        Gif,
        Video
    }

    private readonly struct FrameData
    {
        public FrameData(byte[] buffer, int width, int height)
        {
            Buffer = buffer;
            Width = width;
            Height = height;
        }

        public byte[] Buffer { get; }
        public int Width { get; }
        public int Height { get; }
    }

    private static void Downscale(byte[] source, int sourceWidth, int sourceHeight, byte[] destination, int targetWidth, int targetHeight, FitMode fitMode)
    {
        var mapping = ImageFit.GetMapping(fitMode, sourceWidth, sourceHeight, targetWidth, targetHeight);
        int destStride = targetWidth * 4;
        int sourceStride = sourceWidth * 4;

        Parallel.For(0, targetHeight, row =>
        {
            int destRowOffset = row * destStride;

            for (int col = 0; col < targetWidth; col++)
            {
                int destIndex = destRowOffset + (col * 4);
                if (ImageFit.TryMapPixel(mapping, col, row, out int srcX, out int srcY))
                {
                    int srcIndex = (srcY * sourceStride) + (srcX * 4);
                    destination[destIndex] = source[srcIndex];
                    destination[destIndex + 1] = source[srcIndex + 1];
                    destination[destIndex + 2] = source[srcIndex + 2];
                }
                else
                {
                    destination[destIndex] = 0;
                    destination[destIndex + 1] = 0;
                    destination[destIndex + 2] = 0;
                }
                destination[destIndex + 3] = 255;
            }
        });
    }
}
