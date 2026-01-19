using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace lifeviz;

internal sealed class FileCaptureService : IDisposable
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg"
    };

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
        return true;
    }

    public bool RestartVideo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!TryNormalizePath(path, out var fullPath))
        {
            return false;
        }

        FileSession? session;
        lock (_lock)
        {
            _sessions.TryGetValue(fullPath, out session);
        }

        if (session is VideoSession video)
        {
            video.RestartPlayback();
            return true;
        }

        return false;
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

    public FileCaptureState GetState(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FileCaptureState.Error;
        }

        if (!TryNormalizePath(path, out var fullPath))
        {
            return FileCaptureState.Error;
        }

        lock (_lock)
        {
            if (_sessions.TryGetValue(fullPath, out var session))
            {
                return session.State;
            }
        }

        return FileCaptureState.Error;
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
            return new VideoSession(path, loopPlayback: true);
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
        private readonly bool _loopPlayback;
        private Process? _process;
        private CancellationTokenSource? _cts;
        private Task? _workerTask;
        
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

        public VideoSession(string path, bool loopPlayback)
            : base(path, System.IO.Path.GetFileName(path), 0, 0)
        {
            _loopPlayback = loopPlayback;
            Initialize();
        }

        private void Initialize()
        {
            try
            {
                // 1. Probe video
                if (!ProbeVideo(Path, out _nativeWidth, out _nativeHeight))
                {
                    _hasError = true;
                    _errorMessage = "Failed to probe video dimensions.";
                    Logger.Error(_errorMessage);
                    return;
                }

                Width = _nativeWidth;
                Height = _nativeHeight;

                // 2. Determine processing resolution (cap at 1080p)
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
                    // Ensure even dimensions for ffmpeg compatibility
                    _processWidth &= ~1;
                    _processHeight &= ~1;
                }

                // 3. Start Worker
                _cts = new CancellationTokenSource();
                _workerTask = Task.Run(() => FfmpegWorker(_cts.Token));
            }
            catch (Exception ex)
            {
                _hasError = true;
                _errorMessage = ex.Message;
                Logger.Error($"Failed to initialize video session: {ex.Message}", ex);
            }
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
                    // We need a copy or just a reference swap?
                    // _latestRawFrame is owned by the reader thread (producer) but only when it swaps.
                    // Once it swaps, _latestRawFrame is stable until next swap.
                    // But we can't read it while reader is swapping.
                    // Let's copy reference to _downscaleInput
                    int requiredSize = _processWidth * _processHeight * 4;
                    if (_downscaleInput == null || _downscaleInput.Length != requiredSize)
                    {
                        _downscaleInput = new byte[requiredSize];
                    }
                    
                    // Fast copy
                    Buffer.BlockCopy(_latestRawFrame, 0, _downscaleInput, 0, requiredSize);
                    
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
                                
                                // Update Ready Raw (for includeSource)
                                // We want to show the frame corresponding to the downscaled one, 
                                // so we copy _downscaleInput to _readyRaw
                                int rawSize = pWidth * pHeight * 4;
                                if (_readyRaw == null || _readyRaw.Length != rawSize)
                                {
                                    _readyRaw = new byte[rawSize];
                                }
                                Buffer.BlockCopy(_downscaleInput, 0, _readyRaw, 0, rawSize);
                                
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

                // Return copy or ref? 
                // Ref is unsafe if we modify it in background.
                // But we only modify _readyDownscaled by swapping the reference.
                // So the reference we grab here is safe to read as long as we don't return the array instance to the pool 
                // (we don't use a pool here, just simple double buffering).
                // However, the caller might use it for a while.
                // WPF rendering usually happens immediately.
                // To be perfectly safe, we should probably let it be.
                // But wait, if _readyDownscaled is overwritten next frame, it's fine, the array object itself is just dropped from our view.
                // The only risk is if we REUSE the array instance in _downscaledOutput.
                // We DO recycle: `_downscaledOutput = temp;`.
                // So if we return _readyDownscaled, and then next frame we reuse it as _downscaledOutput and write to it...
                // ...while WPF is rendering the previous frame... -> Tearing/Glitching.
                //
                // Fix: Allocate new buffer if we returned the previous one?
                // Or just assume WPF consumes it fast enough.
                // Given the performance issues, avoiding allocation is key.
                // Let's perform a fast copy for the return value? 8MB copy is ~1ms. 
                // Downscaled is small (e.g. 100x100). That's tiny.
                // Raw (1080p) is big.
                
                // For downscaled: Copy it.
                int dsLen = targetWidth * targetHeight * 4;
                var safeDs = new byte[dsLen];
                Buffer.BlockCopy(_readyDownscaled, 0, safeDs, 0, dsLen);

                byte[]? safeRaw = null;
                if (includeSource && _readyRaw != null)
                {
                    // Copy raw? 8MB. 
                    // Use a cached buffer for the return value? 
                    // The caller (FileCaptureService) wraps it in FileCaptureFrame.
                    // MainWindow uses it.
                    // If we want to be safe and fast, maybe just return the ref and hope we don't overwrite it too fast.
                    // But we overwrite it in `DownscaleTask` -> `Swap`.
                    // At 60fps, 16ms.
                    // Let's copy for now. Reliability > Micro-optimization of 8MB copy (1-2ms).
                    safeRaw = new byte[_readyRaw.Length];
                    Buffer.BlockCopy(_readyRaw, 0, safeRaw, 0, safeRaw.Length);
                }

                return new FileCaptureFrame(safeDs, targetWidth, targetHeight, safeRaw, _readyWidth, _readyHeight);
            }
        }

        public override void Dispose()
        {
            _cts?.Cancel();
            if (_process != null && !_process.HasExited)
            {
                try { _process.Kill(); } catch { }
                _process.Dispose();
            }
            _cts?.Dispose();
        }

        private void FfmpegWorker(CancellationToken token)
        {
            try
            {
                string args = $"-hide_banner -loglevel error";
                if (_loopPlayback) args += " -stream_loop -1";
                args += " -re"; // Realtime reading
                args += $" -i \"{Path}\"";
                args += $" -f rawvideo -pix_fmt bgra -s {_processWidth}x{_processHeight} -";

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                _process = Process.Start(psi);
                if (_process == null) throw new InvalidOperationException("Failed to start ffmpeg.");

                using var stream = _process.StandardOutput.BaseStream;
                int frameSize = _processWidth * _processHeight * 4;
                _readerBuffer = new byte[frameSize];

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
                        _ended = true;
                        break; 
                    }

                    // We have a full frame. Publish it.
                    lock (_lock)
                    {
                        if (_latestRawFrame == null || _latestRawFrame.Length != frameSize)
                        {
                            _latestRawFrame = new byte[frameSize];
                        }
                        Buffer.BlockCopy(_readerBuffer, 0, _latestRawFrame, 0, frameSize);
                        _hasNewFrame = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"FFmpeg worker error: {ex.Message}");
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
            _cts?.Cancel();
            try { _process?.Kill(); } catch { }
            _process?.Dispose();
            
            _ended = false;
            _hasError = false;
            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => FfmpegWorker(_cts.Token));
        }

        private static bool ProbeVideo(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
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

        public VideoSequenceSession(IReadOnlyList<string> paths)
        {
            _paths = new List<string>(paths);
            _displayName = BuildDisplayName(_paths);
            _index = 0;
            _current = new VideoSession(_paths[_index], loopPlayback: false);
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
