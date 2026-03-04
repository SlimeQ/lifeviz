using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace lifeviz;

internal sealed class FileCaptureService : IDisposable
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg"
    };

    private readonly YoutubeClient _youtube = new();

    internal static bool IsVideoPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string extension = Path.GetExtension(path);
        return VideoExtensions.Contains(extension);
    }

    private readonly Dictionary<string, FileSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _masterVideoAudioEnabled = true;
    private double _masterVideoAudioVolume = 1.0;

    public void SetMasterVideoAudioEnabled(bool enabled)
    {
        List<FileSession>? sessionsToUpdate = null;
        lock (_lock)
        {
            if (_masterVideoAudioEnabled == enabled)
            {
                return;
            }

            _masterVideoAudioEnabled = enabled;
            sessionsToUpdate = _sessions.Values.ToList();
        }

        foreach (var session in sessionsToUpdate)
        {
            ApplyMasterAudioSettingsToSession(session);
        }
    }

    public void SetMasterVideoAudioVolume(double volume)
    {
        double clamped = Math.Clamp(volume, 0, 1);
        List<FileSession>? sessionsToUpdate = null;
        lock (_lock)
        {
            if (Math.Abs(_masterVideoAudioVolume - clamped) < 0.0001)
            {
                return;
            }

            _masterVideoAudioVolume = clamped;
            sessionsToUpdate = _sessions.Values.ToList();
        }

        foreach (var session in sessionsToUpdate)
        {
            ApplyMasterAudioSettingsToSession(session);
        }
    }

    private void ApplyMasterAudioSettingsToSession(object session)
    {
        if (session is VideoSession videoSession)
        {
            videoSession.SetMasterAudio(_masterVideoAudioEnabled, _masterVideoAudioVolume);
            return;
        }

        if (session is VideoSequenceSession videoSequenceSession)
        {
            videoSequenceSession.SetAudioMaster(_masterVideoAudioEnabled, _masterVideoAudioVolume);
        }
    }

    public bool TryGetOrAdd(string path, out FileSourceInfo info, out string? error)
    {
        info = default;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "No file path provided.";
            return false;
        }

        if (path.StartsWith("youtube:"))
        {
            string url = path.Substring(8);
            try
            {
                var task = TryCreateYoutubeSource(url);
                task.Wait();
                var result = task.Result;
                if (result.success)
                {
                    info = result.info;
                    return true;
                }
                error = result.error;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
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
            ApplyMasterAudioSettingsToSession(session);
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

    public async Task<(bool success, FileSourceInfo info, string? error)> TryCreateYoutubeSource(string url)
    {
        try
        {
            var video = await _youtube.Videos.GetAsync(url).ConfigureAwait(false);
            string title = video.Title;
            string id = video.Id;
            
            // Unique key for session cache
            string key = $"youtube:{id}";

            lock (_lock)
            {
                if (_sessions.TryGetValue(key, out var existing))
                {
                    return (true, existing.GetInfo(), null);
                }
            }

            // Resolver function to get fresh video/audio stream URLs
            Func<Task<VideoSession.ResolvedPlayback>> resolver = async () =>
            {
                var manifest = await _youtube.Videos.Streams.GetManifestAsync(id).ConfigureAwait(false);
                var muxedStream = manifest.GetMuxedStreams().GetWithHighestVideoQuality();
                var videoOnlyStream = manifest.GetVideoStreams().GetWithHighestVideoQuality();
                var audioOnlyStream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

                string? videoUrl = videoOnlyStream?.Url ?? muxedStream?.Url;
                if (string.IsNullOrWhiteSpace(videoUrl))
                {
                    throw new Exception("No suitable video stream found.");
                }

                string? audioUrl = audioOnlyStream?.Url ?? muxedStream?.Url;
                return new VideoSession.ResolvedPlayback(videoUrl, audioUrl);
            };

            // Pre-flight check (optional, but ensures we can actually get the URL)
            // Actually, we'll let the session handle it so it retries on restart.
            
            var session = new VideoSession(key, title, loopPlayback: true, resolver);
            ApplyMasterAudioSettingsToSession(session);
            
            lock (_lock)
            {
                _sessions[key] = session;
            }

            return (true, session.GetInfo(), null);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to create YouTube source: {url}", ex);
            return (false, default, ex.Message);
        }
    }

    public bool TryCreateVideoSequence(IReadOnlyList<string> paths, out VideoSequenceSession? session, out string? error)
    {
        session = null;
        error = null;

        if (paths == null || paths.Count == 0)
        {
            error = "No video files selected.";
            return false;
        }

        var normalized = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!TryNormalizePath(path, out var fullPath))
            {
                error = "Invalid file path.";
                return false;
            }

            if (!File.Exists(fullPath))
            {
                error = $"File not found: {fullPath}";
                return false;
            }

            string extension = Path.GetExtension(fullPath);
            if (!VideoExtensions.Contains(extension))
            {
                error = $"Video sequences only support video files. ({Path.GetFileName(fullPath)})";
                return false;
            }

            bool duplicate = normalized.Any(existing =>
                string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase));
            if (!duplicate)
            {
                normalized.Add(fullPath);
            }
        }

        if (normalized.Count == 0)
        {
            error = "No valid video files selected.";
            return false;
        }

        session = new VideoSequenceSession(normalized);
        ApplyMasterAudioSettingsToSession(session);
        return true;
    }

    public bool RestartVideo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Check if it's a YouTube key
        if (path.StartsWith("youtube:"))
        {
            FileSession? session;
            lock (_lock)
            {
                _sessions.TryGetValue(path, out session);
            }
            if (session is VideoSession video)
            {
                video.RestartPlayback();
                return true;
            }
            return false;
        }

        if (!TryNormalizePath(path, out var fullPath))
        {
            return false;
        }

        FileSession? fsSession;
        lock (_lock)
        {
            _sessions.TryGetValue(fullPath, out fsSession);
        }

        if (fsSession is VideoSession vid)
        {
            vid.RestartPlayback();
            return true;
        }

        return false;
    }

    public bool SetVideoPaused(string path, bool paused)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (TryResolveVideoSession(path, out var session))
        {
            session.SetPlaybackPaused(paused);
            return true;
        }

        return false;
    }

    public bool SeekVideo(string path, double normalizedPosition)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (TryResolveVideoSession(path, out var session))
        {
            session.SeekNormalized(normalizedPosition);
            return true;
        }

        return false;
    }

    public bool TryGetVideoPlaybackState(string path, out VideoPlaybackState playbackState)
    {
        playbackState = default;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (TryResolveVideoSession(path, out var session))
        {
            return session.TryGetPlaybackState(out playbackState);
        }

        return false;
    }

    private bool TryResolveVideoSession(string path, out VideoSession session)
    {
        session = null!;
        FileSession? fileSession = null;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(path, out fileSession))
            {
                if (!path.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) &&
                    TryNormalizePath(path, out var fullPath))
                {
                    _sessions.TryGetValue(fullPath, out fileSession);
                }
            }
        }

        if (fileSession is VideoSession directSession)
        {
            session = directSession;
            return true;
        }

        if (!path.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) &&
            TryNormalizePath(path, out var normalizedPath) &&
            TryGetOrAdd(normalizedPath, out _, out _))
        {
            lock (_lock)
            {
                _sessions.TryGetValue(normalizedPath, out fileSession);
            }

            if (fileSession is VideoSession recoveredSession)
            {
                session = recoveredSession;
                return true;
            }
        }

        return false;
    }

    public bool SetVideoAudioEnabled(string path, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        FileSession? session = null;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(path, out session))
            {
                if (!path.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) &&
                    TryNormalizePath(path, out var fullPath))
                {
                    _sessions.TryGetValue(fullPath, out session);
                }
            }
        }

        if (session is VideoSession videoSession)
        {
            videoSession.SetAudioEnabled(enabled);
            return true;
        }

        if (!path.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) &&
            TryNormalizePath(path, out var normalizedPath) &&
            TryGetOrAdd(normalizedPath, out _, out _))
        {
            lock (_lock)
            {
                _sessions.TryGetValue(normalizedPath, out session);
            }

            if (session is VideoSession recoveredVideoSession)
            {
                recoveredVideoSession.SetAudioEnabled(enabled);
                return true;
            }
        }

        return false;
    }

    public bool SetVideoAudioVolume(string path, double volume)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        double clamped = Math.Clamp(volume, 0, 1);
        FileSession? session = null;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(path, out session))
            {
                if (!path.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) &&
                    TryNormalizePath(path, out var fullPath))
                {
                    _sessions.TryGetValue(fullPath, out session);
                }
            }
        }

        if (session is VideoSession videoSession)
        {
            videoSession.SetAudioVolume(clamped);
            return true;
        }

        if (!path.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) &&
            TryNormalizePath(path, out var normalizedPath) &&
            TryGetOrAdd(normalizedPath, out _, out _))
        {
            lock (_lock)
            {
                _sessions.TryGetValue(normalizedPath, out session);
            }

            if (session is VideoSession recoveredVideoSession)
            {
                recoveredVideoSession.SetAudioVolume(clamped);
                return true;
            }
        }

        return false;
    }

    public FileCaptureFrame? CaptureFrame(string path, int targetWidth, int targetHeight, FitMode fitMode, bool includeSource = false)
    {
        if (string.IsNullOrWhiteSpace(path) || targetWidth <= 0 || targetHeight <= 0)
        {
            return null;
        }

        FileSession? session;
        lock (_lock)
        {
            // Direct lookup first (handles youtube keys)
            if (!_sessions.TryGetValue(path, out session))
            {
                // Try normalizing path if not found
                if (TryNormalizePath(path, out var fullPath))
                {
                     _sessions.TryGetValue(fullPath, out session);
                }
            }
        }

        if (session == null)
        {
            // Auto-load local files if missing
            if (!path.StartsWith("youtube:") && TryNormalizePath(path, out var fullPath))
            {
                 if (TryGetOrAdd(fullPath, out _, out _))
                 {
                    lock (_lock)
                    {
                        _sessions.TryGetValue(fullPath, out session);
                    }
                 }
            }
        }

        return session?.CaptureFrame(targetWidth, targetHeight, fitMode, includeSource);
    }

    public FileCaptureState GetState(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FileCaptureState.Error;
        }

        FileSession? session = null;
        lock (_lock)
        {
             if (!_sessions.TryGetValue(path, out session))
             {
                 if (TryNormalizePath(path, out var fullPath))
                 {
                     _sessions.TryGetValue(fullPath, out session);
                 }
             }
        }
        
        return session?.State ?? FileCaptureState.Error;
    }

    public void Remove(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string key = path;
        if (!path.StartsWith("youtube:") && TryNormalizePath(path, out var fullPath))
        {
            key = fullPath;
        }

        FileSession? session;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(key, out session))
            {
                return;
            }
            _sessions.Remove(key);
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
            return new VideoSession(path, System.IO.Path.GetFileName(path), loopPlayback: true);
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
        public virtual FileCaptureState State => FileCaptureState.Ready;

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
        public readonly struct ResolvedPlayback
        {
            public ResolvedPlayback(string videoUrl, string? audioUrl)
            {
                VideoUrl = videoUrl;
                AudioUrl = audioUrl;
            }

            public string VideoUrl { get; }
            public string? AudioUrl { get; }
        }

        private readonly bool _loopPlayback;
        private readonly Func<Task<ResolvedPlayback>>? _urlResolver;
        private readonly object _audioLock = new();
        private Process? _process;
        private Process? _audioDecodeProcess;
        private CancellationTokenSource? _cts;
        private Task? _workerTask;
        private CancellationTokenSource? _audioDecodeCts;
        private Task? _audioDecodeTask;
        private WaveOutEvent? _audioOutput;
        private BufferedWaveProvider? _audioBuffer;
        private readonly Stopwatch _playbackClock = new();
        private double _playbackBaseOffsetSeconds;
        private double _pausedOffsetSeconds;
        private bool _playbackPaused;
        private bool _audioEnabled;
        private bool _isDisposed;
        private bool _masterAudioEnabled = true;
        private double _masterAudioVolume = 1.0;
        private double _sourceAudioVolume = 1.0;
        private double _estimatedDurationSeconds;
        private string? _videoPlaybackUrl;
        private string? _audioPlaybackUrl;
        
        // Buffers
        private byte[]? _readerBuffer;      // Worker writes here
        private byte[]? _latestRawFrame;    // Latest complete frame from worker
        private byte[]? _downscaleInput;    // Snapshot for downscaler
        private byte[]? _downscaledOutput;  // Downscaler writes here
        private byte[]? _readyDownscaled;   // Ready for UI
        private byte[]? _readyRaw;          // Ready for UI (Source)

        private readonly object _lock = new();
        private volatile bool _hasNewFrame;
        private volatile bool _isDownscaling;
        private bool _hasError;
        private volatile bool _ended;
        private string? _errorMessage;

        private int _nativeWidth;
        private int _nativeHeight;
        private int _processWidth;
        private int _processHeight;
        
        // Capture State tracking
        private int _readyWidth;
        private int _readyHeight;

        public VideoSession(string path, string displayName, bool loopPlayback, Func<Task<ResolvedPlayback>>? urlResolver = null)
            : base(path, displayName, 0, 0)
        {
            _loopPlayback = loopPlayback;
            _urlResolver = urlResolver;

            if (_urlResolver == null)
            {
                // Local file: Initialize immediately (blocking probe)
                InitializeSync(path);
            }
            else
            {
                // Remote/Async: Initialize background
                Task.Run(InitializeAsync);
            }
        }

        // Constructor for legacy usage
        public VideoSession(string path, bool loopPlayback) 
            : this(path, System.IO.Path.GetFileName(path), loopPlayback, null)
        {
        }

        private void InitializeSync(string path)
        {
            try
            {
                Logger.Info($"Initializing local video: {path}");
                
                if (!ProbeVideo(path, out _nativeWidth, out _nativeHeight, out var durationSeconds))
                {
                    _hasError = true;
                    _errorMessage = "Failed to probe video dimensions.";
                    Logger.Error(_errorMessage);
                    return;
                }
                
                ConfigureDimensions();
                _videoPlaybackUrl = path;
                _audioPlaybackUrl = path;
                double startOffsetSeconds;
                bool startPaused;
                lock (_audioLock)
                {
                    _estimatedDurationSeconds = durationSeconds;
                    startOffsetSeconds = NormalizeOffsetNoLock(_pausedOffsetSeconds);
                    _pausedOffsetSeconds = startOffsetSeconds;
                    _playbackBaseOffsetSeconds = startOffsetSeconds;
                    startPaused = _playbackPaused;
                    if (startPaused)
                    {
                        _playbackClock.Reset();
                    }
                    else
                    {
                        _playbackClock.Restart();
                    }
                }

                if (!startPaused)
                {
                    _cts = new CancellationTokenSource();
                    _workerTask = Task.Run(() => FfmpegWorker(path, _cts.Token, startOffsetSeconds));
                    RefreshAudioPlayback();
                }
            }
            catch (Exception ex)
            {
                _hasError = true;
                Logger.Error($"Failed to initialize video session: {ex.Message}", ex);
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                string targetVideoUrl = Path;
                string? targetAudioUrl = Path;
                if (_urlResolver != null)
                {
                    Logger.Info($"Resolving URL for: {DisplayName}...");
                    var resolved = await _urlResolver();
                    targetVideoUrl = resolved.VideoUrl;
                    targetAudioUrl = resolved.AudioUrl;
                    Logger.Info($"Resolved video URL: {targetVideoUrl}");
                    if (!string.IsNullOrWhiteSpace(targetAudioUrl))
                    {
                        Logger.Info($"Resolved audio URL for {DisplayName}.");
                    }
                    else
                    {
                        Logger.Warn($"No audio URL resolved for {DisplayName}.");
                    }
                }

                Logger.Info($"Probing video: {targetVideoUrl}");
                if (!ProbeVideo(targetVideoUrl, out _nativeWidth, out _nativeHeight, out var durationSeconds))
                {
                    _hasError = true;
                    _errorMessage = "Failed to probe video dimensions.";
                    Logger.Error(_errorMessage);
                    return;
                }
                Logger.Info($"Probe success: {_nativeWidth}x{_nativeHeight}");

                ConfigureDimensions();
                _videoPlaybackUrl = targetVideoUrl;
                _audioPlaybackUrl = targetAudioUrl;
                double startOffsetSeconds;
                bool startPaused;
                lock (_audioLock)
                {
                    _estimatedDurationSeconds = durationSeconds;
                    startOffsetSeconds = NormalizeOffsetNoLock(_pausedOffsetSeconds);
                    _pausedOffsetSeconds = startOffsetSeconds;
                    _playbackBaseOffsetSeconds = startOffsetSeconds;
                    startPaused = _playbackPaused;
                    if (startPaused)
                    {
                        _playbackClock.Reset();
                    }
                    else
                    {
                        _playbackClock.Restart();
                    }
                }

                if (!startPaused)
                {
                    _cts = new CancellationTokenSource();
                    _workerTask = Task.Run(() => FfmpegWorker(targetVideoUrl, _cts.Token, startOffsetSeconds));
                    RefreshAudioPlayback();
                }
            }
            catch (Exception ex)
            {
                _hasError = true;
                _errorMessage = ex.Message;
                Logger.Error($"Failed to initialize video session: {ex.Message}", ex);
            }
        }

        private void ConfigureDimensions()
        {
            Width = _nativeWidth;
            Height = _nativeHeight;

            _processWidth = _nativeWidth;
            _processHeight = _nativeHeight;
            
            if (_nativeWidth > 1920 || _nativeHeight > 1080)
            {
                double aspect = (double)_nativeWidth / _nativeHeight;
                if (_nativeWidth > 1920)
                {
                    _processWidth = 1920;
                    _processHeight = (int)(1920 / aspect);
                }
                else
                {
                    _processHeight = 1080;
                    _processWidth = (int)(1080 * aspect);
                }
            }
            
            // Ensure even dimensions for ffmpeg compatibility
            _processWidth &= ~1;
            _processHeight &= ~1;
            
            Logger.Info($"Video configured: Native={_nativeWidth}x{_nativeHeight}, Process={_processWidth}x{_processHeight}");
        }

        public override FileSourceKind Kind => FileSourceKind.Video;
        public override FileCaptureState State => _hasError ? FileCaptureState.Error : (_readyDownscaled != null ? FileCaptureState.Ready : FileCaptureState.Pending);

        public override FileCaptureFrame? CaptureFrame(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource)
        {
            if (_hasError) return null;

            // Check if we have a new raw frame to process
            bool launchDownscale = false;
            lock (_lock)
            {
                if (_hasNewFrame && !_isDownscaling && _latestRawFrame != null)
                {
                    // Swap latest raw to downscale input
                    int requiredSize = _processWidth * _processHeight * 4;
                    if (_downscaleInput == null || _downscaleInput.Length != requiredSize)
                    {
                        _downscaleInput = new byte[requiredSize];
                    }
                    
                    var temp = _latestRawFrame;
                    _latestRawFrame = _downscaleInput;
                    _downscaleInput = temp;
                    
                    _hasNewFrame = false;
                    _isDownscaling = true;
                    launchDownscale = true;
                }
            }

            if (launchDownscale)
            {
                // Capture params
                int pWidth = _processWidth;
                int pHeight = _processHeight;
                int tWidth = targetWidth;
                int tHeight = targetHeight;
                FitMode mode = fitMode;

                // Ensure output buffer
                int reqOut = tWidth * tHeight * 4;
                if (_downscaledOutput == null || _downscaledOutput.Length != reqOut)
                {
                    _downscaledOutput = new byte[reqOut];
                }

                Task.Run(() =>
                {
                    try
                    {
                        if (_downscaleInput != null)
                        {
                            Downscale(_downscaleInput, pWidth, pHeight, _downscaledOutput!, tWidth, tHeight, mode);
                            
                            lock (_lock)
                            {
                                // Swap Output -> Ready
                                var temp = _readyDownscaled;
                                _readyDownscaled = _downscaledOutput;
                                _downscaledOutput = temp; // Recycle
                                
                                // Swap Input (Raw) -> Ready Raw
                                if (_readyRaw == null || _readyRaw.Length != _downscaleInput.Length)
                                {
                                    _readyRaw = new byte[_downscaleInput.Length];
                                }
                                
                                var tempRaw = _readyRaw;
                                _readyRaw = _downscaleInput;
                                _downscaleInput = tempRaw;
                                
                                _readyWidth = pWidth;
                                _readyHeight = pHeight;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Downscale error: {ex.Message}");
                    }
                    finally
                    {
                        _isDownscaling = false;
                    }
                });
            }

            lock (_lock)
            {
                if (_readyDownscaled == null) return null;

                int dsLen = targetWidth * targetHeight * 4;
                if (_readyDownscaled.Length != dsLen)
                {
                    _readyDownscaled = null;
                    return null;
                }
                var safeDs = new byte[dsLen];
                Buffer.BlockCopy(_readyDownscaled, 0, safeDs, 0, dsLen);

                byte[]? safeRaw = null;
                if (includeSource && _readyRaw != null)
                {
                    safeRaw = new byte[_readyRaw.Length];
                    Buffer.BlockCopy(_readyRaw, 0, safeRaw, 0, safeRaw.Length);
                }

                return new FileCaptureFrame(safeDs, targetWidth, targetHeight, safeRaw, _readyWidth, _readyHeight);
            }
        }

        public override void Dispose()
        {
            lock (_audioLock)
            {
                _isDisposed = true;
                _audioEnabled = false;
            }

            StopVideoPipeline();
            StopAudioPlayback();
        }

        private void FfmpegWorker(string url, CancellationToken token, double startOffsetSeconds)
        {
            try
            {
                string args = $"-hide_banner -loglevel warning"; // increased verbosity for debug
                if (startOffsetSeconds > 0.05)
                {
                    args += $" -ss {startOffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture)}";
                }
                if (_loopPlayback) args += " -stream_loop -1";
                args += " -re"; // Realtime reading
                args += $" -i \"{url}\"";
                args += $" -f rawvideo -pix_fmt bgra -s {_processWidth}x{_processHeight} -";

                Logger.Info($"Starting ffmpeg: {args}");

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _process = Process.Start(psi);
                if (_process == null) throw new InvalidOperationException("Failed to start ffmpeg.");

                _process.ErrorDataReceived += (s, e) => 
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) Logger.Warn($"[ffmpeg] {e.Data}");
                };
                _process.BeginErrorReadLine();

                using var stream = _process.StandardOutput.BaseStream;
                int frameSize = _processWidth * _processHeight * 4;
                _readerBuffer = new byte[frameSize];
                
                int framesRead = 0;

                while (!token.IsCancellationRequested && !_process.HasExited)
                {
                    int totalRead = 0;
                    while (totalRead < frameSize)
                    {
                        int read = stream.Read(_readerBuffer, totalRead, frameSize - totalRead);
                        if (read == 0) break; // EOF
                        totalRead += read;
                    }

                    if (totalRead < frameSize) 
                    {
                        Logger.Warn($"ffmpeg stream ended (read {totalRead}/{frameSize} bytes). Exited: {_process.HasExited}");
                        if (!token.IsCancellationRequested && _process.HasExited && _process.ExitCode == 0)
                        {
                            _ended = true;
                        }
                        break; 
                    }

                    if (framesRead++ % 60 == 0)
                    {
                        // Sample center pixel
                        int centerIdx = ((_processHeight / 2) * _processWidth * 4) + ((_processWidth / 2) * 4);
                        if (centerIdx + 3 < _readerBuffer.Length)
                        {
                            Logger.Info($"ffmpeg frame {framesRead} acquired. Center px: B={_readerBuffer[centerIdx]} G={_readerBuffer[centerIdx+1]} R={_readerBuffer[centerIdx+2]} A={_readerBuffer[centerIdx+3]}");
                        }
                    }

                    // We have a full frame. Publish it.
                    lock (_lock)
                    {
                        if (_latestRawFrame == null || _latestRawFrame.Length != frameSize)
                        {
                            _latestRawFrame = new byte[frameSize];
                        }
                        // Swap
                        var temp = _latestRawFrame;
                        _latestRawFrame = _readerBuffer;
                        _readerBuffer = temp;
                        
                        _hasNewFrame = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"FFmpeg worker error: {ex.Message}", ex);
                _hasError = true;
            }
        }

        public bool ConsumeEnded()
        {
            if (!_ended) return false;
            _ended = false;
            return true;
        }

        public void RestartPlayback()
        {
            if (_isDisposed)
            {
                return;
            }

            RestartVideoPipeline(0, shouldPause: false, forceReResolve: true);
        }

        public void SetPlaybackPaused(bool paused)
        {
            if (_isDisposed)
            {
                return;
            }

            double seekOffset = 0;
            bool shouldRestart = false;
            lock (_audioLock)
            {
                if (_playbackPaused == paused)
                {
                    return;
                }

                if (paused)
                {
                    _pausedOffsetSeconds = GetEstimatedPlaybackOffsetSecondsNoLock();
                    _playbackPaused = true;
                    _playbackClock.Reset();
                }
                else
                {
                    _playbackPaused = false;
                    seekOffset = NormalizeOffsetNoLock(_pausedOffsetSeconds);
                    _playbackBaseOffsetSeconds = seekOffset;
                    _playbackClock.Restart();
                    shouldRestart = true;
                }
            }

            if (paused)
            {
                StopVideoPipeline();
                StopAudioPlayback();
                return;
            }

            if (shouldRestart)
            {
                RestartVideoPipeline(seekOffset, shouldPause: false, forceReResolve: false);
            }
        }

        public void SeekNormalized(double normalizedPosition)
        {
            if (_isDisposed)
            {
                return;
            }

            double seekOffset;
            bool paused;
            lock (_audioLock)
            {
                double duration = _estimatedDurationSeconds;
                if (duration <= 0.001)
                {
                    return;
                }

                double clamped = Math.Clamp(normalizedPosition, 0, 1);
                seekOffset = NormalizeOffsetNoLock(duration * clamped);
                _pausedOffsetSeconds = seekOffset;
                _playbackBaseOffsetSeconds = seekOffset;
                paused = _playbackPaused;
                if (paused)
                {
                    _playbackClock.Reset();
                }
                else
                {
                    _playbackClock.Restart();
                }
            }

            RestartVideoPipeline(seekOffset, shouldPause: paused, forceReResolve: false);
        }

        public bool TryGetPlaybackState(out VideoPlaybackState playbackState)
        {
            lock (_audioLock)
            {
                double duration = _estimatedDurationSeconds;
                double position = GetEstimatedPlaybackOffsetSecondsNoLock();
                bool isSeekable = duration > 0.001;
                double normalized = isSeekable ? Math.Clamp(position / duration, 0, 1) : 0;
                playbackState = new VideoPlaybackState(duration, position, normalized, _playbackPaused, isSeekable);
                return true;
            }
        }

        private void RestartVideoPipeline(double startOffsetSeconds, bool shouldPause, bool forceReResolve)
        {
            if (_isDisposed)
            {
                return;
            }

            StopVideoPipeline();
            StopAudioPlayback();

            _ended = false;
            _hasError = false;

            lock (_audioLock)
            {
                double normalized = NormalizeOffsetNoLock(startOffsetSeconds);
                _pausedOffsetSeconds = normalized;
                _playbackBaseOffsetSeconds = normalized;
                _playbackPaused = shouldPause;
                if (shouldPause)
                {
                    _playbackClock.Reset();
                }
                else
                {
                    _playbackClock.Restart();
                }
            }

            if (shouldPause)
            {
                return;
            }

            if (forceReResolve || string.IsNullOrWhiteSpace(_videoPlaybackUrl))
            {
                _cts = new CancellationTokenSource();
                _workerTask = Task.Run(InitializeAsync); // Re-initialize (and re-resolve URL if needed).
                return;
            }

            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => FfmpegWorker(_videoPlaybackUrl!, _cts.Token, startOffsetSeconds));
            RefreshAudioPlayback();
        }

        private void StopVideoPipeline()
        {
            var cts = _cts;
            _cts = null;
            try { cts?.Cancel(); } catch { }
            try { _process?.Kill(); } catch { }
            _process?.Dispose();
            _process = null;
            try { cts?.Dispose(); } catch { }
        }

        public void SetAudioEnabled(bool enabled)
        {
            lock (_audioLock)
            {
                _audioEnabled = enabled;
            }

            RefreshAudioPlayback();
        }

        public void SetMasterAudio(bool enabled, double volume)
        {
            bool shouldRefresh;
            bool shouldStop;
            lock (_audioLock)
            {
                _masterAudioEnabled = enabled;
                _masterAudioVolume = Math.Clamp(volume, 0, 1);
                ApplyOutputVolumeNoLock();
                shouldStop = GetEffectiveVolumeNoLock() <= 0.0001;
                shouldRefresh = !shouldStop && _audioEnabled && !_isDisposed &&
                                (_audioDecodeProcess == null || _audioOutput == null || _audioOutput.PlaybackState != PlaybackState.Playing);
            }

            if (shouldStop)
            {
                StopAudioPlayback();
                return;
            }

            if (shouldRefresh)
            {
                RefreshAudioPlayback();
            }
        }

        public void SetAudioVolume(double volume)
        {
            bool shouldRefresh;
            bool shouldStop;
            lock (_audioLock)
            {
                _sourceAudioVolume = Math.Clamp(volume, 0, 1);
                ApplyOutputVolumeNoLock();
                shouldStop = GetEffectiveVolumeNoLock() <= 0.0001;
                shouldRefresh = !shouldStop && _audioEnabled && !_isDisposed &&
                                (_audioDecodeProcess == null || _audioOutput == null || _audioOutput.PlaybackState != PlaybackState.Playing);
            }

            if (shouldStop)
            {
                StopAudioPlayback();
                return;
            }

            if (shouldRefresh)
            {
                RefreshAudioPlayback();
            }
        }

        private double GetEffectiveVolumeNoLock()
        {
            if (!_masterAudioEnabled)
            {
                return 0;
            }

            return Math.Clamp(_masterAudioVolume * _sourceAudioVolume, 0, 1);
        }

        private void ApplyOutputVolumeNoLock()
        {
            if (_audioOutput == null)
            {
                return;
            }

            _audioOutput.Volume = (float)GetEffectiveVolumeNoLock();
        }

        private void RefreshAudioPlayback()
        {
            string? playbackUrl;
            bool shouldBeEnabled;
            lock (_audioLock)
            {
                playbackUrl = _audioPlaybackUrl ?? _videoPlaybackUrl;
                shouldBeEnabled = !_isDisposed &&
                                  _audioEnabled &&
                                  !_playbackPaused &&
                                  !string.IsNullOrWhiteSpace(playbackUrl) &&
                                  GetEffectiveVolumeNoLock() > 0.0001;
            }

            if (!shouldBeEnabled)
            {
                StopAudioPlayback();
                return;
            }

            lock (_audioLock)
            {
                if (_audioDecodeProcess != null &&
                    !_audioDecodeProcess.HasExited &&
                    _audioOutput != null &&
                    _audioOutput.PlaybackState == PlaybackState.Playing)
                {
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(playbackUrl))
            {
                return;
            }

            StartAudioPlayback(playbackUrl);
        }

        private void StartAudioPlayback(string playbackUrl)
        {
            StopAudioPlayback();

            int fatalAudioError = 0;
            var seekSeconds = GetEstimatedPlaybackOffsetSeconds();
            string args = "-hide_banner -loglevel warning";
            if (seekSeconds > 0.05)
            {
                args += $" -ss {seekSeconds.ToString("0.###", CultureInfo.InvariantCulture)}";
            }
            args += " -re";
            args += $" -i \"{playbackUrl}\"";
            args += " -map 0:a:0 -vn -ac 2 -ar 48000 -acodec pcm_s16le -f s16le -";

            try
            {
                Logger.Info($"Starting ffmpeg audio decode: {args}");

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                var process = Process.Start(psi);
                if (process == null)
                {
                    Logger.Warn($"Failed to start ffmpeg audio decode process for {DisplayName}.");
                    return;
                }

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        Logger.Warn($"[ffmpeg-audio:{DisplayName}] {e.Data}");
                        if (IsFatalAudioError(e.Data))
                        {
                            Interlocked.Exchange(ref fatalAudioError, 1);
                        }
                    }
                };
                process.BeginErrorReadLine();

                var bufferProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
                {
                    BufferDuration = TimeSpan.FromSeconds(1.5),
                    DiscardOnBufferOverflow = true
                };
                var output = new WaveOutEvent
                {
                    DesiredLatency = 80,
                    NumberOfBuffers = 2
                };
                output.Init(bufferProvider);
                lock (_audioLock)
                {
                    output.Volume = (float)GetEffectiveVolumeNoLock();
                }
                output.Play();

                var audioCts = new CancellationTokenSource();
                var decodeTask = Task.Run(
                    () => AudioDecodeWorker(process, audioCts.Token, () => Volatile.Read(ref fatalAudioError) != 0),
                    audioCts.Token);

                bool shouldAbort = false;
                lock (_audioLock)
                {
                    shouldAbort = _isDisposed || !_audioEnabled || _playbackPaused || GetEffectiveVolumeNoLock() <= 0.0001;
                    if (!shouldAbort)
                    {
                        _audioDecodeProcess = process;
                        _audioDecodeCts = audioCts;
                        _audioDecodeTask = decodeTask;
                        _audioBuffer = bufferProvider;
                        _audioOutput = output;
                    }
                }

                if (shouldAbort)
                {
                    try { audioCts.Cancel(); } catch { }
                    SafeStopOutput(output);
                    SafeKillProcess(process);
                    audioCts.Dispose();
                    return;
                }

                Logger.Info($"Started in-app audio for {DisplayName}.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Audio playback unavailable for {DisplayName}: {ex.Message}");
            }
        }

        private static bool IsFatalAudioError(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;

            return line.IndexOf("Failed to open file", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("No such file or directory", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("configure filtergraph", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("does not contain any stream", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("cannot open audio device", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("error while opening", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("matches no streams", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private double GetEstimatedPlaybackOffsetSeconds()
        {
            lock (_audioLock)
            {
                return GetEstimatedPlaybackOffsetSecondsNoLock();
            }
        }

        private double GetEstimatedPlaybackOffsetSecondsNoLock()
        {
            if (_playbackPaused)
            {
                return NormalizeOffsetNoLock(_pausedOffsetSeconds);
            }

            double elapsed = _playbackClock.Elapsed.TotalSeconds;
            double offset = _playbackBaseOffsetSeconds + elapsed;
            return NormalizeOffsetNoLock(offset);
        }

        private double NormalizeOffsetNoLock(double offsetSeconds)
        {
            if (offsetSeconds < 0)
            {
                offsetSeconds = 0;
            }

            if (_loopPlayback && _estimatedDurationSeconds > 0.001)
            {
                offsetSeconds %= _estimatedDurationSeconds;
                if (offsetSeconds < 0)
                {
                    offsetSeconds += _estimatedDurationSeconds;
                }
            }
            else if (_estimatedDurationSeconds > 0.001)
            {
                offsetSeconds = Math.Clamp(offsetSeconds, 0, _estimatedDurationSeconds);
            }

            return offsetSeconds;
        }

        private void AudioDecodeWorker(Process process, CancellationToken token, Func<bool> hasFatalError)
        {
            int exitCode = 0;
            try
            {
                using var stream = process.StandardOutput.BaseStream;
                var buffer = new byte[8192];
                while (!token.IsCancellationRequested)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    BufferedWaveProvider? provider;
                    lock (_audioLock)
                    {
                        provider = _audioBuffer;
                    }

                    provider?.AddSamples(buffer, 0, read);
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Logger.Warn($"Audio decode worker failed for {DisplayName}: {ex.Message}");
                }
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.WaitForExit(200);
                    }
                }
                catch
                {
                    // Ignore wait failures.
                }

                try
                {
                    if (process.HasExited)
                    {
                        exitCode = process.ExitCode;
                    }
                }
                catch
                {
                    // Ignore exit code failures.
                }

                ClearAudioStateForProcess(process);
                Logger.Info($"ffmpeg audio decode exited for {DisplayName} (code {exitCode}).");

                bool shouldRestart;
                lock (_audioLock)
                {
                    shouldRestart = !token.IsCancellationRequested
                        && _audioEnabled
                        && !_playbackPaused
                        && !_isDisposed
                        && _loopPlayback
                        && exitCode == 0
                        && !hasFatalError();
                }

                if (shouldRestart)
                {
                    Logger.Info($"Restarting audio decode for looping source: {DisplayName}");
                    Task.Run(RefreshAudioPlayback);
                }
            }
        }

        private void StopAudioPlayback()
        {
            Process? processToStop;
            CancellationTokenSource? ctsToStop;
            Task? taskToWait;
            WaveOutEvent? outputToStop;
            lock (_audioLock)
            {
                processToStop = _audioDecodeProcess;
                _audioDecodeProcess = null;
                ctsToStop = _audioDecodeCts;
                _audioDecodeCts = null;
                taskToWait = _audioDecodeTask;
                _audioDecodeTask = null;
                outputToStop = _audioOutput;
                _audioOutput = null;
                _audioBuffer = null;
            }

            try
            {
                ctsToStop?.Cancel();
            }
            catch
            {
                // Ignore cancellation failures.
            }

            SafeStopOutput(outputToStop);
            SafeKillProcess(processToStop);

            if (taskToWait != null && Task.CurrentId != taskToWait.Id)
            {
                try
                {
                    taskToWait.Wait(200);
                }
                catch
                {
                    // Ignore wait failures.
                }
            }

            try
            {
                ctsToStop?.Dispose();
            }
            catch
            {
                // Ignore dispose failures.
            }
        }

        private void ClearAudioStateForProcess(Process process)
        {
            WaveOutEvent? outputToDispose = null;
            CancellationTokenSource? ctsToDispose = null;
            bool ownsCurrentPipeline = false;
            lock (_audioLock)
            {
                ownsCurrentPipeline = ReferenceEquals(_audioDecodeProcess, process);
                if (ownsCurrentPipeline)
                {
                    _audioDecodeProcess = null;

                    outputToDispose = _audioOutput;
                    _audioOutput = null;
                    _audioBuffer = null;

                    ctsToDispose = _audioDecodeCts;
                    _audioDecodeCts = null;
                    _audioDecodeTask = null;
                }
            }

            if (ownsCurrentPipeline)
            {
                SafeStopOutput(outputToDispose);
            }
            SafeKillProcess(process);
            try
            {
                ctsToDispose?.Dispose();
            }
            catch
            {
                // Ignore dispose failures.
            }
        }

        private static void SafeStopOutput(WaveOutEvent? output)
        {
            if (output == null)
            {
                return;
            }

            try
            {
                output.Stop();
            }
            catch
            {
                // Ignore stop failures.
            }
            try
            {
                output.Dispose();
            }
            catch
            {
                // Ignore dispose failures.
            }
        }

        private static void SafeKillProcess(Process? process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // Ignore cleanup failures.
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                    // Ignore dispose failures.
                }
            }
        }

        private static bool ProbeVideo(string path, out int width, out int height, out double durationSeconds)
        {
            width = 0;
            height = 0;
            durationSeconds = 0;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -i \"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using var p = Process.Start(psi);
                if (p == null) return false;
                
                string output = p.StandardError.ReadToEnd();
                p.WaitForExit();

                var durationMatch = Regex.Match(output, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (durationMatch.Success &&
                    int.TryParse(durationMatch.Groups[1].Value, out int hours) &&
                    int.TryParse(durationMatch.Groups[2].Value, out int minutes) &&
                    double.TryParse(durationMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                {
                    durationSeconds = (hours * 3600) + (minutes * 60) + seconds;
                }
                
                // Regex for "Video: ..., 1920x1080"
                var match = Regex.Match(output, @"Video:.*?, (\d+)x(\d+)");
                if (match.Success)
                {
                    width = int.Parse(match.Groups[1].Value);
                    height = int.Parse(match.Groups[2].Value);
                    return width > 0 && height > 0;
                }
            }
                        catch (Exception ex)
                        {
                            Logger.Warn($"Probe failed: {ex.Message}");
                        }
                        return false;
                    }
                }
            
    internal sealed class VideoSequenceSession : IDisposable
    {
        private readonly List<string> _paths;
        private readonly string _displayName;
        private int _index;
        private VideoSession? _current;
        private int _errorStreak;
        private bool _hasError;
        private bool _audioEnabled;
        private double _audioVolume = 1.0;
        private bool _masterAudioEnabled = true;
        private double _masterAudioVolume = 1.0;
        private bool _playbackPaused;
            
        public VideoSequenceSession(IReadOnlyList<string> paths)
        {
            _paths = new List<string>(paths);
            _displayName = BuildDisplayName(_paths);
            _index = 0;
            _current = new VideoSession(_paths[_index], loopPlayback: false);
            _current.SetMasterAudio(_masterAudioEnabled, _masterAudioVolume);
            _current.SetAudioVolume(_audioVolume);
            _current.SetAudioEnabled(_audioEnabled);
            _current.SetPlaybackPaused(_playbackPaused);
        }
            
                    public IReadOnlyList<string> Paths => _paths;
                    public string DisplayName => _displayName;
            
                    public FileCaptureState State
                    {
                        get
                        {
                            if (_hasError)
                            {
                                return FileCaptureState.Error;
                            }
            
                            if (_current == null)
                            {
                                return FileCaptureState.Error;
                            }
            
                            return _current.State;
                        }
                    }
            
                    public FileCaptureFrame? CaptureFrame(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource)
                    {
                        if (_hasError || _current == null)
                        {
                            return null;
                        }
            
                        var frame = _current.CaptureFrame(targetWidth, targetHeight, fitMode, includeSource);
            
                        if (_current.ConsumeEnded())
                        {
                            Advance(isError: false);
                            return null;
                        }
            
                        if (_current.State == FileCaptureState.Error)
                        {
                            if (!Advance(isError: true))
                            {
                                Logger.Warn($"All videos in sequence failed: {_displayName}");
                                _hasError = true;
                                return null;
                            }
                        }
                        else if (frame.HasValue)
                        {
                            _errorStreak = 0;
                        }
            
                        return frame;
                    }
            
        public void Restart()
        {
            _hasError = false;
            _errorStreak = 0;
            _current?.Dispose();
            _index = 0;
            _current = new VideoSession(_paths[_index], loopPlayback: false);
            _current.SetMasterAudio(_masterAudioEnabled, _masterAudioVolume);
            _current.SetAudioVolume(_audioVolume);
            _current.SetAudioEnabled(_audioEnabled);
            _current.SetPlaybackPaused(_playbackPaused);
        }

        public void SetAudioEnabled(bool enabled)
        {
            _audioEnabled = enabled;
            _current?.SetAudioEnabled(enabled);
        }

        public void SetAudioVolume(double volume)
        {
            _audioVolume = Math.Clamp(volume, 0, 1);
            _current?.SetAudioVolume(_audioVolume);
        }

        public void SetAudioMaster(bool enabled, double volume)
        {
            _masterAudioEnabled = enabled;
            _masterAudioVolume = Math.Clamp(volume, 0, 1);
            _current?.SetMasterAudio(_masterAudioEnabled, _masterAudioVolume);
        }

        public void SetPlaybackPaused(bool paused)
        {
            _playbackPaused = paused;
            _current?.SetPlaybackPaused(paused);
        }

        public void SeekNormalized(double normalizedPosition)
        {
            _current?.SeekNormalized(normalizedPosition);
        }

        public bool TryGetPlaybackState(out VideoPlaybackState playbackState)
        {
            if (_current == null)
            {
                playbackState = default;
                return false;
            }

            return _current.TryGetPlaybackState(out playbackState);
        }
            
                    public void Dispose()
                    {
                        _current?.Dispose();
                        _current = null;
                    }
            
        private bool Advance(bool isError)
        {
            _current?.Dispose();
            _index = (_index + 1) % _paths.Count;
            _current = new VideoSession(_paths[_index], loopPlayback: false);
            _current.SetMasterAudio(_masterAudioEnabled, _masterAudioVolume);
            _current.SetAudioVolume(_audioVolume);
            _current.SetAudioEnabled(_audioEnabled);
            _current.SetPlaybackPaused(_playbackPaused);
            
                        if (isError)
                        {
                            _errorStreak++;
                            if (_errorStreak >= _paths.Count)
                            {
                                return false;
                            }
                        }
                        else
                        {
                            _errorStreak = 0;
                        }
            
                        return true;
                    }
            
                    private static string BuildDisplayName(IReadOnlyList<string> paths)
                    {
                        if (paths == null || paths.Count == 0)
                        {
                            return "Video Sequence";
                        }
            
                        string baseName = Path.GetFileName(paths[0]);
                        if (paths.Count == 1)
                        {
                            return baseName;
                        }
            
                return $"{baseName} (+{paths.Count - 1})";
                    }
                }

    internal readonly struct VideoPlaybackState
    {
        public VideoPlaybackState(double durationSeconds, double positionSeconds, double normalizedPosition, bool isPaused, bool isSeekable)
        {
            DurationSeconds = durationSeconds;
            PositionSeconds = positionSeconds;
            NormalizedPosition = normalizedPosition;
            IsPaused = isPaused;
            IsSeekable = isSeekable;
        }

        public double DurationSeconds { get; }
        public double PositionSeconds { get; }
        public double NormalizedPosition { get; }
        public bool IsPaused { get; }
        public bool IsSeekable { get; }
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
            
            internal enum FileCaptureState
            {
                Ready,
                Pending,
                Error
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
                                destination[destIndex + 3] = source[srcIndex + 3];
                            }
                            else
                            {
                                destination[destIndex] = 0;
                                destination[destIndex + 1] = 0;
                                destination[destIndex + 2] = 0;
                                destination[destIndex + 3] = 0;
                            }
                        }
                    });
                }
            }
            
