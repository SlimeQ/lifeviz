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
    private bool _liveVideoAudioAnalysisEnabled;
    private bool _lowContentionMode;
    private int _decoderThreadLimit;
    private int _videoDecodeFpsLimit;
    private bool _offlineRenderEnabled;
    private int _offlineRenderFps;
    private double _offlineRenderTimeSeconds;

    private static Task RunBackgroundLongRunning(Action action, string threadName)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ConfigureBackgroundWorkerThread(threadName);
            try
            {
                action();
                tcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = threadName,
            Priority = ThreadPriority.Normal
        };

        thread.Start();
        return tcs.Task;
    }

    private static Task<T> RunBackgroundLongRunning<T>(Func<T> action, string threadName)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ConfigureBackgroundWorkerThread(threadName);
            try
            {
                tcs.TrySetResult(action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = threadName,
            Priority = ThreadPriority.Normal
        };

        thread.Start();
        return tcs.Task;
    }

    private static void ConfigureBackgroundWorkerThread(string? threadName)
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Normal;
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(threadName) && string.IsNullOrWhiteSpace(Thread.CurrentThread.Name))
        {
            try
            {
                Thread.CurrentThread.Name = threadName;
            }
            catch
            {
            }
        }
    }

    private static void TryLowerChildProcessPriority(
        Process process,
        string label,
        bool lowContentionMode,
        bool preferThroughput = false)
    {
        try
        {
            process.PriorityClass = preferThroughput || !lowContentionMode
                ? ProcessPriorityClass.Normal
                : ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to lower priority for {label}: {ex.Message}");
        }
    }

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

    public void SetLiveVideoAudioAnalysisEnabled(bool enabled)
    {
        List<FileSession> sessionsToUpdate;
        lock (_lock)
        {
            if (_liveVideoAudioAnalysisEnabled == enabled)
            {
                return;
            }

            _liveVideoAudioAnalysisEnabled = enabled;
            sessionsToUpdate = _sessions.Values.ToList();
        }

        foreach (var session in sessionsToUpdate)
        {
            ApplyLiveAudioAnalysisSettingsToSession(session);
        }
    }

    public void SetPerformanceSettings(bool lowContentionMode, int decoderThreadLimit, int videoDecodeFpsLimit)
    {
        decoderThreadLimit = Math.Clamp(decoderThreadLimit, 0, 8);
        videoDecodeFpsLimit = videoDecodeFpsLimit == 15 || videoDecodeFpsLimit == 30 ? videoDecodeFpsLimit : 0;
        List<FileSession>? sessionsToUpdate = null;
        lock (_lock)
        {
            if (_lowContentionMode == lowContentionMode &&
                _decoderThreadLimit == decoderThreadLimit &&
                _videoDecodeFpsLimit == videoDecodeFpsLimit)
            {
                return;
            }

            _lowContentionMode = lowContentionMode;
            _decoderThreadLimit = decoderThreadLimit;
            _videoDecodeFpsLimit = videoDecodeFpsLimit;
            sessionsToUpdate = _sessions.Values.ToList();
        }

        foreach (var session in sessionsToUpdate)
        {
            ApplyPerformanceSettingsToSession(session);
        }
    }

    public void BeginOfflineRender(int fps, double startTimeSeconds)
    {
        List<FileSession> sessions;
        lock (_lock)
        {
            _offlineRenderEnabled = true;
            _offlineRenderFps = Math.Clamp(fps, 1, 144);
            _offlineRenderTimeSeconds = Math.Max(0, startTimeSeconds);
            sessions = _sessions.Values.ToList();
        }

        foreach (var session in sessions)
        {
            ApplyOfflineRenderSettingsToSession(session);
        }
    }

    public void SetOfflineRenderTime(double timeSeconds)
    {
        List<ImageSequenceSession> imageSessions;
        lock (_lock)
        {
            if (!_offlineRenderEnabled)
            {
                return;
            }

            _offlineRenderTimeSeconds = Math.Max(0, timeSeconds);
            imageSessions = _sessions.Values.OfType<ImageSequenceSession>().ToList();
        }

        foreach (var session in imageSessions)
        {
            session.SetOfflineRenderTime(_offlineRenderTimeSeconds);
        }
    }

    public void EndOfflineRender()
    {
        List<FileSession> sessions;
        lock (_lock)
        {
            if (!_offlineRenderEnabled)
            {
                return;
            }

            _offlineRenderEnabled = false;
            _offlineRenderFps = 0;
            sessions = _sessions.Values.ToList();
        }

        foreach (var session in sessions)
        {
            ApplyOfflineRenderSettingsToSession(session);
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
            return;
        }

        if (session is AutoClipSession autoClipSession)
        {
            autoClipSession.SetAudioMaster(_masterVideoAudioEnabled, _masterVideoAudioVolume);
        }
    }

    private void ApplyLiveAudioAnalysisSettingsToSession(object session)
    {
        if (session is VideoSession videoSession)
        {
            videoSession.SetLiveAudioAnalysisEnabled(_liveVideoAudioAnalysisEnabled);
            return;
        }

        if (session is VideoSequenceSession videoSequenceSession)
        {
            videoSequenceSession.SetLiveAudioAnalysisEnabled(_liveVideoAudioAnalysisEnabled);
            return;
        }

        if (session is AutoClipSession autoClipSession)
        {
            autoClipSession.SetLiveAudioAnalysisEnabled(_liveVideoAudioAnalysisEnabled);
        }
    }

    private void ApplyPerformanceSettingsToSession(object session)
    {
        if (session is VideoSession videoSession)
        {
            videoSession.SetPerformanceSettings(_lowContentionMode, _decoderThreadLimit, _videoDecodeFpsLimit);
            videoSession.SetOfflineRenderMode(_offlineRenderEnabled, _offlineRenderFps);
            return;
        }

        if (session is VideoSequenceSession videoSequenceSession)
        {
            videoSequenceSession.SetPerformanceSettings(_lowContentionMode, _decoderThreadLimit, _videoDecodeFpsLimit);
            videoSequenceSession.SetOfflineRenderMode(_offlineRenderEnabled, _offlineRenderFps);
            return;
        }

        if (session is AutoClipSession autoClipSession)
        {
            autoClipSession.SetPerformanceSettings(_lowContentionMode, _decoderThreadLimit, _videoDecodeFpsLimit);
            autoClipSession.SetOfflineRenderMode(_offlineRenderEnabled, _offlineRenderFps);
        }
    }

    private void ApplyOfflineRenderSettingsToSession(FileSession session)
    {
        if (session is ImageSequenceSession imageSession)
        {
            imageSession.SetOfflineRenderMode(_offlineRenderEnabled, _offlineRenderTimeSeconds);
        }
        else if (session is VideoSession videoSession)
        {
            videoSession.SetOfflineRenderMode(_offlineRenderEnabled, _offlineRenderFps);
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
            ApplyLiveAudioAnalysisSettingsToSession(session);
            ApplyPerformanceSettingsToSession(session);
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
            ApplyLiveAudioAnalysisSettingsToSession(session);
            ApplyPerformanceSettingsToSession(session);
            
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
        ApplyLiveAudioAnalysisSettingsToSession(session);
        ApplyPerformanceSettingsToSession(session);
        return true;
    }

    public bool TryCreateAutoClip(
        IReadOnlyList<string>? paths,
        double minClipSeconds,
        double maxClipSeconds,
        double minDelaySeconds,
        double maxDelaySeconds,
        out AutoClipSession? session,
        out string? error)
    {
        session = null;
        error = null;
        var normalized = new List<string>();

        foreach (string path in paths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!TryNormalizePath(path, out string fullPath))
            {
                error = "Invalid file path.";
                return false;
            }

            if (!File.Exists(fullPath))
            {
                error = $"File not found: {fullPath}";
                return false;
            }

            if (!VideoExtensions.Contains(Path.GetExtension(fullPath)))
            {
                error = $"AutoClip only supports video files. ({Path.GetFileName(fullPath)})";
                return false;
            }

            if (!normalized.Any(existing => string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                normalized.Add(fullPath);
            }
        }

        session = new AutoClipSession(normalized, minClipSeconds, maxClipSeconds, minDelaySeconds, maxDelaySeconds);
        ApplyMasterAudioSettingsToSession(session);
        ApplyLiveAudioAnalysisSettingsToSession(session);
        ApplyPerformanceSettingsToSession(session);
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

    public bool MixOfflineVideoAudioFrame(string path, Span<float> destination)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               TryResolveVideoSession(path, out var session) &&
               session.MixOfflineAudioFrame(destination);
    }

    public int MixLiveVideoAudioSamples(string path, Span<float> destination)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               TryResolveVideoSession(path, out var session)
            ? session.MixLiveAudioSamples(destination)
            : 0;
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

    internal double? GetVideoPlaybackOffsetSecondsForDiagnostics(string path)
    {
        if (!TryNormalizePath(path, out string fullPath))
        {
            return null;
        }

        lock (_lock)
        {
            return _sessions.TryGetValue(fullPath, out FileSession? session) && session is VideoSession videoSession
                ? videoSession.GetPlaybackOffsetSecondsForDiagnostics()
                : null;
        }
    }

    internal double? GetVideoPlaybackBaseOffsetSecondsForDiagnostics(string path)
    {
        if (!TryNormalizePath(path, out string fullPath))
        {
            return null;
        }

        lock (_lock)
        {
            return _sessions.TryGetValue(fullPath, out FileSession? session) && session is VideoSession videoSession
                ? videoSession.GetPlaybackBaseOffsetSecondsForDiagnostics()
                : null;
        }
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

    public FileSourceKind? GetKind(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        FileSession? session = null;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(path, out session) &&
                TryNormalizePath(path, out var fullPath))
            {
                _sessions.TryGetValue(fullPath, out session);
            }
        }

        if (session != null)
        {
            return session.Kind;
        }

        if (path.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) || IsVideoPath(path))
        {
            return FileSourceKind.Video;
        }

        return null;
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
        private bool _offlineRenderEnabled;
        private double _offlineRenderTimeSeconds;
        private double _offlineTimelineStartSeconds;
        private double _offlineSequenceStartMilliseconds;
        private byte[]? _downscaledBuffer;
        private int _cachedTargetWidth = -1;
        private int _cachedTargetHeight = -1;
        private FitMode _cachedFitMode = FitMode.Fill;
        private int _cachedFrameIndex = -1;

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

            int frameIndex = _frames.Length == 1 ? 0 : GetCurrentFrameIndex();
            var frame = _frames[frameIndex];

            int downscaledLength = targetWidth * targetHeight * 4;
            if (_downscaledBuffer == null || _downscaledBuffer.Length != downscaledLength)
            {
                _downscaledBuffer = new byte[downscaledLength];
                _cachedTargetWidth = -1;
            }

            bool canReuseDownscaled = _cachedTargetWidth == targetWidth &&
                                      _cachedTargetHeight == targetHeight &&
                                      _cachedFitMode == fitMode &&
                                      _cachedFrameIndex == frameIndex;
            if (!canReuseDownscaled)
            {
                Downscale(frame.Buffer, frame.Width, frame.Height, _downscaledBuffer, targetWidth, targetHeight, fitMode);
                _cachedTargetWidth = targetWidth;
                _cachedTargetHeight = targetHeight;
                _cachedFitMode = fitMode;
                _cachedFrameIndex = frameIndex;
            }

            return new FileCaptureFrame(_downscaledBuffer, targetWidth, targetHeight,
                includeSource ? frame.Buffer : null,
                frame.Width,
                frame.Height);
        }

        public override void Dispose()
        {
            // No unmanaged resources to clean up.
        }

        public void SetOfflineRenderMode(bool enabled, double timeSeconds)
        {
            if (enabled && !_offlineRenderEnabled)
            {
                _offlineTimelineStartSeconds = Math.Max(0, timeSeconds);
                _offlineSequenceStartMilliseconds = _clock.Elapsed.TotalMilliseconds;
            }

            _offlineRenderEnabled = enabled;
            _offlineRenderTimeSeconds = Math.Max(0, timeSeconds);
        }

        public void SetOfflineRenderTime(double timeSeconds)
        {
            if (_offlineRenderEnabled)
            {
                _offlineRenderTimeSeconds = Math.Max(0, timeSeconds);
            }
        }

        private int GetCurrentFrameIndex()
        {
            if (_frames.Length == 1)
            {
                return 0;
            }

            double elapsedMs = _offlineRenderEnabled
                ? _offlineSequenceStartMilliseconds +
                  (Math.Max(0, _offlineRenderTimeSeconds - _offlineTimelineStartSeconds) * 1000.0)
                : _clock.Elapsed.TotalMilliseconds;
            double mod = _totalDurationMs <= 0 ? 0 : elapsedMs % _totalDurationMs;
            for (int i = 0; i < _frameEndTimesMs.Length; i++)
            {
                if (mod <= _frameEndTimesMs[i])
                {
                    return i;
                }
            }

            return _frames.Length - 1;
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
        internal readonly struct VideoProbeInfo
        {
            public VideoProbeInfo(int width, int height, double durationSeconds)
            {
                Width = width;
                Height = height;
                DurationSeconds = durationSeconds;
            }

            public int Width { get; }
            public int Height { get; }
            public double DurationSeconds { get; }
        }

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
        private const int LiveAudioAnalysisBufferCapacity = 48000 * 2;
        private readonly object _liveAudioAnalysisLock = new();
        private readonly float[] _liveAudioAnalysisBuffer = new float[LiveAudioAnalysisBufferCapacity];
        private int _liveAudioAnalysisReadIndex;
        private int _liveAudioAnalysisWriteIndex;
        private int _liveAudioAnalysisCount;
        private bool _liveAudioAnalysisEnabled;
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
        private const int MaxQueuedOfflineRawFrames = 16;
        private const int MaxQueuedLiveRawFrames = 2;
        private const int ParallelDownscalePixelThreshold = 640 * 360;
        private byte[]? _readerBuffer;      // Worker writes here
        private byte[]? _readyDownscaled;   // Ready for UI
        private byte[]? _readyRaw;          // Ready for UI (Source)
        private long _readyFrameToken;
        private long _readyFramePublishTimestamp;
        private readonly Queue<byte[]> _pendingRawFrames = new();
        private readonly Stack<byte[]> _rawFramePool = new();

        private readonly object _lock = new();
        private bool _hasError;
        private volatile bool _ended;
        private string? _errorMessage;
        private bool _preferNativeProcessFrames;
        private bool _lowContentionMode;
        private int _decoderThreadLimit;
        private int _videoDecodeFpsLimit;
        private volatile bool _offlineRenderEnabled;
        private volatile int _offlineRenderFps;
        private long _offlineFramesConsumed;
        private double _offlineStartOffsetSeconds;
        private readonly object _offlineAudioLock = new();
        private Process? _offlineAudioProcess;
        private Stream? _offlineAudioStream;
        private byte[]? _offlineAudioReadBuffer;
        private bool _offlineAudioUnavailable;

        private int _nativeWidth;
        private int _nativeHeight;
        private int _processWidth;
        private int _processHeight;
        private bool _processProducesExactTargetFrames;
        private FitMode _processOutputFitMode = FitMode.Fill;
        
        // Capture State tracking
        private int _readyWidth;
        private int _readyHeight;
        private int _readyDownscaledWidth;
        private int _readyDownscaledHeight;
        public VideoSession(string path, string displayName, bool loopPlayback, Func<Task<ResolvedPlayback>>? urlResolver = null, VideoProbeInfo? cachedProbe = null)
            : base(path, displayName, 0, 0)
        {
            _loopPlayback = loopPlayback;
            _urlResolver = urlResolver;

            if (_urlResolver == null)
            {
                // Local file: Initialize immediately (blocking probe)
                InitializeSync(path, cachedProbe);
            }
            else
            {
                // Remote/Async: Initialize background
                _ = RunBackgroundLongRunning(
                    () => InitializeAsync().GetAwaiter().GetResult(),
                    "LifeViz.VideoInitialize");
            }
        }

        // Constructor for legacy usage
        public VideoSession(string path, bool loopPlayback, VideoProbeInfo? cachedProbe = null) 
            : this(path, System.IO.Path.GetFileName(path), loopPlayback, null, cachedProbe)
        {
        }

        private void InitializeSync(string path, VideoProbeInfo? cachedProbe)
        {
            try
            {
                Logger.Info($"Initializing local video: {path}");

                double durationSeconds;
                if (cachedProbe.HasValue)
                {
                    _nativeWidth = cachedProbe.Value.Width;
                    _nativeHeight = cachedProbe.Value.Height;
                    durationSeconds = cachedProbe.Value.DurationSeconds;
                }
                else if (!ProbeVideo(path, out _nativeWidth, out _nativeHeight, out durationSeconds))
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

            (_processWidth, _processHeight) = GetNativeProcessDimensions();
            _preferNativeProcessFrames = true;

            Logger.Info($"Video configured: Native={_nativeWidth}x{_nativeHeight}, Process={_processWidth}x{_processHeight}");
        }

        public void SetPerformanceSettings(bool lowContentionMode, int decoderThreadLimit, int videoDecodeFpsLimit)
        {
            decoderThreadLimit = Math.Clamp(decoderThreadLimit, 0, 8);
            videoDecodeFpsLimit = videoDecodeFpsLimit == 15 || videoDecodeFpsLimit == 30 ? videoDecodeFpsLimit : 0;
            if (_lowContentionMode == lowContentionMode &&
                _decoderThreadLimit == decoderThreadLimit &&
                _videoDecodeFpsLimit == videoDecodeFpsLimit)
            {
                return;
            }

            _lowContentionMode = lowContentionMode;
            _decoderThreadLimit = decoderThreadLimit;
            _videoDecodeFpsLimit = videoDecodeFpsLimit;

            if (_isDisposed)
            {
                return;
            }

            double seekOffset;
            bool shouldPause;
            lock (_audioLock)
            {
                seekOffset = GetEstimatedPlaybackOffsetSecondsNoLock();
                shouldPause = _playbackPaused;
            }

            if (_workerTask != null || _process != null)
            {
                RestartVideoPipeline(seekOffset, shouldPause, forceReResolve: false);
            }
            else if (!shouldPause)
            {
                RefreshAudioPlayback();
            }
        }

        public void SetOfflineRenderMode(bool enabled, int fps)
        {
            fps = enabled ? Math.Clamp(fps, 1, 144) : 0;
            if (_offlineRenderEnabled == enabled && _offlineRenderFps == fps)
            {
                return;
            }

            bool wasOffline = _offlineRenderEnabled;
            int priorOfflineFps = _offlineRenderFps;
            double seekOffset;
            bool shouldPause;
            lock (_audioLock)
            {
                // Offline rendering is a temporary timeline fork. Resume live playback at the
                // position where the export began instead of seeking it forward by the rendered
                // duration; otherwise the preview visibly jumps when the export finishes.
                seekOffset = !enabled && wasOffline && priorOfflineFps > 0
                    ? NormalizeOffsetNoLock(_offlineStartOffsetSeconds)
                    : GetEstimatedPlaybackOffsetSecondsNoLock();
                shouldPause = _playbackPaused;
                _playbackBaseOffsetSeconds = seekOffset;
                _pausedOffsetSeconds = seekOffset;
                if (!shouldPause)
                {
                    _playbackClock.Restart();
                }
            }

            _offlineRenderEnabled = enabled;
            _offlineRenderFps = fps;
            StopOfflineAudioDecoder();
            _offlineAudioUnavailable = false;
            if (enabled)
            {
                _offlineStartOffsetSeconds = seekOffset;
                _offlineFramesConsumed = 0;
            }
            if (_isDisposed)
            {
                return;
            }

            if (enabled)
            {
                StopAudioPlayback();
            }

            if (_workerTask != null || _process != null)
            {
                RestartVideoPipeline(seekOffset, shouldPause, forceReResolve: false);
            }
            else if (!enabled && !shouldPause)
            {
                RefreshAudioPlayback();
            }
        }

        public override FileSourceKind Kind => FileSourceKind.Video;
        public override FileCaptureState State => _hasError ? FileCaptureState.Error : (_readyDownscaled != null ? FileCaptureState.Ready : FileCaptureState.Pending);

        public override FileCaptureFrame? CaptureFrame(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource)
        {
            if (_hasError) return null;

            EnsureProcessDimensionsForRequest(targetWidth, targetHeight, fitMode, includeSource);
            EnsureVideoWorkerStarted();

            bool rebuildDownscaledFromReadyRaw = false;
            lock (_lock)
            {
                if (_offlineRenderEnabled && !_playbackPaused)
                {
                    long waitDeadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 30L);
                    while (_pendingRawFrames.Count == 0 &&
                           !_hasError &&
                           !_ended &&
                           !_isDisposed &&
                           Stopwatch.GetTimestamp() < waitDeadline)
                    {
                        Monitor.Wait(_lock, millisecondsTimeout: 100);
                    }
                }

                if (_pendingRawFrames.Count > 0)
                {
                    int requiredSize = _processWidth * _processHeight * 4;
                    if (includeSource)
                    {
                        if (_readyRaw != null && _readyRaw.Length == requiredSize)
                        {
                            RecycleRawFrameBufferNoLock(_readyRaw, requiredSize);
                        }

                        _readyRaw = _pendingRawFrames.Dequeue();
                        while (!_offlineRenderEnabled && _pendingRawFrames.Count > 0)
                        {
                            RecycleRawFrameBufferNoLock(_readyRaw, requiredSize);
                            _readyRaw = _pendingRawFrames.Dequeue();
                        }

                        _readyWidth = _processWidth;
                        _readyHeight = _processHeight;
                        _readyFrameToken++;
                        _readyFramePublishTimestamp = Stopwatch.GetTimestamp();
                        rebuildDownscaledFromReadyRaw = true;
                    }
                    else
                    {
                        byte[] nextInput = _pendingRawFrames.Dequeue();
                        while (!_offlineRenderEnabled && _pendingRawFrames.Count > 0)
                        {
                            RecycleRawFrameBufferNoLock(nextInput, requiredSize);
                            nextInput = _pendingRawFrames.Dequeue();
                        }

                        if (_readyDownscaled != null && _readyDownscaled.Length == requiredSize)
                        {
                            RecycleRawFrameBufferNoLock(_readyDownscaled, requiredSize);
                        }

                        // Publish the decoder-sized frame directly and let the
                        // compositor scale it. That avoids both giant decoder-side
                        // upscales and the old CPU resample worker on the common
                        // file-video underlay path.
                        _readyDownscaled = nextInput;
                        _readyRaw = null;
                        _readyWidth = _processWidth;
                        _readyHeight = _processHeight;
                        _readyDownscaledWidth = _processWidth;
                        _readyDownscaledHeight = _processHeight;
                        _readyFrameToken++;
                        _readyFramePublishTimestamp = Stopwatch.GetTimestamp();
                    }

                    Monitor.PulseAll(_lock);
                    if (_offlineRenderEnabled)
                    {
                        _offlineFramesConsumed++;
                    }
                }
            }

            if (includeSource && rebuildDownscaledFromReadyRaw)
            {
                byte[]? sourceBuffer;
                byte[]? targetBuffer = null;
                lock (_lock)
                {
                    sourceBuffer = _readyRaw;
                    int requiredDownscaledLength = targetWidth * targetHeight * 4;
                    if (_readyDownscaled != null && _readyDownscaled.Length == requiredDownscaledLength)
                    {
                        targetBuffer = _readyDownscaled;
                    }
                }

                if (sourceBuffer != null)
                {
                    int requiredDownscaledLength = targetWidth * targetHeight * 4;
                    byte[] downscaled = targetBuffer ?? new byte[requiredDownscaledLength];
                    Downscale(sourceBuffer, _readyWidth, _readyHeight, downscaled, targetWidth, targetHeight, fitMode);
                    lock (_lock)
                    {
                        _readyDownscaled = downscaled;
                        _readyDownscaledWidth = targetWidth;
                        _readyDownscaledHeight = targetHeight;
                    }
                }
            }

            lock (_lock)
            {
                if (_readyDownscaled == null) return null;
                long readyFrameToken = _readyFrameToken;
                long readyFramePublishTimestamp = _readyFramePublishTimestamp;

                int actualDownscaledLength = _readyDownscaledWidth * _readyDownscaledHeight * 4;
                if (_readyDownscaledWidth <= 0 || _readyDownscaledHeight <= 0 || _readyDownscaled.Length != actualDownscaledLength)
                {
                    _readyDownscaled = null;
                    _readyDownscaledWidth = 0;
                    _readyDownscaledHeight = 0;
                    return null;
                }

                if (includeSource && (_readyDownscaledWidth != targetWidth || _readyDownscaledHeight != targetHeight))
                {
                    if (_readyRaw == null)
                    {
                        _readyDownscaled = null;
                        _readyDownscaledWidth = 0;
                        _readyDownscaledHeight = 0;
                        return null;
                    }

                    int dsLen = targetWidth * targetHeight * 4;
                    var rebuiltDownscaled = new byte[dsLen];
                    Downscale(_readyRaw, _readyWidth, _readyHeight, rebuiltDownscaled, targetWidth, targetHeight, fitMode);
                    _readyDownscaled = rebuiltDownscaled;
                    _readyDownscaledWidth = targetWidth;
                    _readyDownscaledHeight = targetHeight;
                }
                return new FileCaptureFrame(
                    _readyDownscaled,
                    _readyDownscaledWidth,
                    _readyDownscaledHeight,
                    includeSource ? _readyRaw : null,
                    includeSource ? _readyWidth : _nativeWidth,
                    includeSource ? _readyHeight : _nativeHeight,
                    readyFrameToken,
                    readyFramePublishTimestamp);
            }
        }

        private (int width, int height) GetNativeProcessDimensions()
        {
            int width = _nativeWidth;
            int height = _nativeHeight;

            if (_nativeWidth > 1920 || _nativeHeight > 1080)
            {
                double aspect = (double)_nativeWidth / _nativeHeight;
                if (_nativeWidth > 1920)
                {
                    width = 1920;
                    height = (int)Math.Round(1920 / aspect);
                }
                else
                {
                    height = 1080;
                    width = (int)Math.Round(1080 * aspect);
                }
            }

            width = Math.Max(2, width) & ~1;
            height = Math.Max(2, height) & ~1;
            return (width, height);
        }

        private (int width, int height) GetAdaptiveProcessDimensions(int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0 || _nativeWidth <= 0 || _nativeHeight <= 0)
            {
                return GetNativeProcessDimensions();
            }

            double widthScale = targetWidth / (double)_nativeWidth;
            double heightScale = targetHeight / (double)_nativeHeight;
            double coverScale = Math.Max(widthScale, heightScale);
            if (double.IsNaN(coverScale) || double.IsInfinity(coverScale) || coverScale <= 0)
            {
                coverScale = 1.0;
            }

            int width = Math.Max(2, (int)Math.Ceiling(_nativeWidth * coverScale));
            int height = Math.Max(2, (int)Math.Ceiling(_nativeHeight * coverScale));
            width &= ~1;
            height &= ~1;

            width = Math.Min(width, _nativeWidth);
            height = Math.Min(height, _nativeHeight);

            if (width <= 0 || height <= 0)
            {
                return GetNativeProcessDimensions();
            }

            return (width, height);
        }

        private static bool SupportsDirectProcessOutput(FitMode fitMode)
        {
            FitMode normalized = ImageFit.Normalize(fitMode);
            return normalized == FitMode.Fill ||
                   normalized == FitMode.Fit ||
                   normalized == FitMode.Stretch;
        }

        private static string BuildDirectOutputVideoFilter(FitMode fitMode, int targetWidth, int targetHeight)
        {
            FitMode normalized = ImageFit.Normalize(fitMode);
            return normalized switch
            {
                FitMode.Fill =>
                    $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=increase,crop={targetWidth}:{targetHeight}",
                FitMode.Fit =>
                    $"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=decrease,pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2:black",
                FitMode.Stretch =>
                    $"scale={targetWidth}:{targetHeight}",
                _ => throw new InvalidOperationException($"Fit mode {fitMode} does not support direct ffmpeg output.")
            };
        }

        private string BuildDecoderThreadArgs()
        {
            int threadLimit = _decoderThreadLimit;
            if (_lowContentionMode && threadLimit <= 0)
            {
                threadLimit = 1;
            }

            // ffmpeg's automatic decoder fanout is wasteful when several file layers
            // are playing concurrently: every process can create a large codec thread
            // pool and retain a correspondingly large set of reference frames. Two
            // threads comfortably sustain normal HD playback while keeping live scenes
            // from oversubscribing the machine. Offline rendering keeps ffmpeg's full
            // automatic fanout because throughput, rather than latency, is the goal.
            if (!_offlineRenderEnabled && threadLimit <= 0)
            {
                threadLimit = 2;
            }

            if (threadLimit <= 0)
            {
                return string.Empty;
            }

            return $" -threads {threadLimit}";
        }

        private string BuildVideoOutputFilter(bool produceExactTargetFrames, FitMode processOutputFitMode, int processWidth, int processHeight)
        {
            string? directFilter = produceExactTargetFrames
                ? BuildDirectOutputVideoFilter(processOutputFitMode, processWidth, processHeight)
                : null;

            int decodeFps = _offlineRenderEnabled ? _offlineRenderFps : _videoDecodeFpsLimit;
            string? fpsFilter = decodeFps > 0
                ? $"fps={decodeFps}"
                : null;

            if (string.IsNullOrWhiteSpace(directFilter))
            {
                return fpsFilter ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(fpsFilter))
            {
                return directFilter;
            }

            return $"{directFilter},{fpsFilter}";
        }

        private void EnsureProcessDimensionsForRequest(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource)
        {
            if (_nativeWidth <= 0 || _nativeHeight <= 0 || _isDisposed)
            {
                return;
            }

            bool preferNative = includeSource;
            bool produceExactTargetFrames = !preferNative &&
                                            targetWidth > 0 &&
                                            targetHeight > 0 &&
                                            targetWidth <= _nativeWidth &&
                                            targetHeight <= _nativeHeight &&
                                            SupportsDirectProcessOutput(fitMode);

            (int width, int height) desired = preferNative
                ? GetNativeProcessDimensions()
                : produceExactTargetFrames
                    ? (Math.Max(2, targetWidth) & ~1, Math.Max(2, targetHeight) & ~1)
                    : GetAdaptiveProcessDimensions(targetWidth, targetHeight);

            if (_processWidth == desired.width &&
                _processHeight == desired.height &&
                _preferNativeProcessFrames == preferNative &&
                _processProducesExactTargetFrames == produceExactTargetFrames &&
                _processOutputFitMode == fitMode)
            {
                return;
            }

            _processWidth = desired.width;
            _processHeight = desired.height;
            _preferNativeProcessFrames = preferNative;
            _processProducesExactTargetFrames = produceExactTargetFrames;
            _processOutputFitMode = fitMode;

            Logger.Info($"Adjusted video decode resolution for {DisplayName}: Native={_nativeWidth}x{_nativeHeight}, Process={_processWidth}x{_processHeight}, Target={targetWidth}x{targetHeight}, NativeSource={preferNative}, DirectOutput={produceExactTargetFrames}, Fit={fitMode}.");

            bool paused;
            double offset;
            lock (_audioLock)
            {
                paused = _playbackPaused;
                offset = paused ? _pausedOffsetSeconds : GetEstimatedPlaybackOffsetSecondsNoLock();
            }

            RestartVideoPipeline(offset, shouldPause: paused, forceReResolve: false);
        }

        public override void Dispose()
        {
            lock (_audioLock)
            {
                _isDisposed = true;
                _audioEnabled = false;
            }

            StopOfflineAudioDecoder();
            StopVideoPipeline();
            StopAudioPlayback();
        }

        public bool MixOfflineAudioFrame(Span<float> destination)
        {
            if (!_offlineRenderEnabled || destination.Length == 0 || _offlineAudioUnavailable)
            {
                return false;
            }

            double volume;
            bool shouldDecode;
            lock (_audioLock)
            {
                volume = GetEffectiveVolumeNoLock();
                shouldDecode = _audioEnabled &&
                               !_playbackPaused &&
                               !_isDisposed &&
                               volume > 0.0001;
            }

            if (!shouldDecode || !EnsureOfflineAudioDecoder())
            {
                return false;
            }

            int bytesNeeded = destination.Length * sizeof(float);
            byte[] buffer;
            Stream? stream;
            lock (_offlineAudioLock)
            {
                if (_offlineAudioReadBuffer == null || _offlineAudioReadBuffer.Length < bytesNeeded)
                {
                    _offlineAudioReadBuffer = new byte[bytesNeeded];
                }

                buffer = _offlineAudioReadBuffer;
                stream = _offlineAudioStream;
            }

            if (stream == null)
            {
                return false;
            }

            int totalRead = 0;
            try
            {
                while (totalRead < bytesNeeded)
                {
                    int read = stream.Read(buffer, totalRead, bytesNeeded - totalRead);
                    if (read <= 0)
                    {
                        break;
                    }
                    totalRead += read;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Offline audio decode failed for {DisplayName}: {ex.Message}");
            }

            int sampleCount = Math.Min(destination.Length, totalRead / sizeof(float));
            for (int i = 0; i < sampleCount; i++)
            {
                destination[i] += BitConverter.ToSingle(buffer, i * sizeof(float)) * (float)volume;
            }

            if (sampleCount == 0)
            {
                _offlineAudioUnavailable = true;
                StopOfflineAudioDecoder();
                return false;
            }

            return true;
        }

        private bool EnsureOfflineAudioDecoder()
        {
            lock (_offlineAudioLock)
            {
                if (_offlineAudioStream != null && _offlineAudioProcess != null)
                {
                    return true;
                }
            }

            string? playbackUrl;
            lock (_audioLock)
            {
                playbackUrl = _audioPlaybackUrl ?? _videoPlaybackUrl;
            }
            if (string.IsNullOrWhiteSpace(playbackUrl))
            {
                return false;
            }

            string args = "-hide_banner -loglevel warning";
            if (_offlineStartOffsetSeconds > 0.05)
            {
                args += $" -ss {_offlineStartOffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture)}";
            }
            args += BuildDecoderThreadArgs();
            if (_loopPlayback)
            {
                args += " -stream_loop -1";
            }
            args += $" -i \"{playbackUrl}\"";
            args += " -map 0:a:0? -vn -ac 1 -ar 48000 -acodec pcm_f32le -f f32le -";

            try
            {
                Logger.Info($"Starting offline audio analysis decode for {DisplayName}.");
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                });
                if (process == null)
                {
                    return false;
                }

                TryLowerChildProcessPriority(
                    process,
                    $"ffmpeg offline audio decode for {DisplayName}",
                    _lowContentionMode,
                    preferThroughput: true);
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        Logger.Warn($"[ffmpeg-offline-audio:{DisplayName}] {e.Data}");
                    }
                };
                process.BeginErrorReadLine();

                lock (_offlineAudioLock)
                {
                    _offlineAudioProcess = process;
                    _offlineAudioStream = process.StandardOutput.BaseStream;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Offline audio analysis unavailable for {DisplayName}: {ex.Message}");
                _offlineAudioUnavailable = true;
                return false;
            }
        }

        private void StopOfflineAudioDecoder()
        {
            Process? process;
            Stream? stream;
            lock (_offlineAudioLock)
            {
                process = _offlineAudioProcess;
                stream = _offlineAudioStream;
                _offlineAudioProcess = null;
                _offlineAudioStream = null;
                _offlineAudioReadBuffer = null;
            }

            try { stream?.Dispose(); } catch { }
            SafeKillProcess(process);
            try { process?.Dispose(); } catch { }
        }

        private void FfmpegWorker(string url, CancellationToken token, double startOffsetSeconds)
        {
            try
            {
                int processWidth = _processWidth;
                int processHeight = _processHeight;
                bool produceExactTargetFrames = _processProducesExactTargetFrames;
                FitMode processOutputFitMode = _processOutputFitMode;
                int maxQueuedRawFrames = _offlineRenderEnabled
                    ? MaxQueuedOfflineRawFrames
                    : MaxQueuedLiveRawFrames;
                string args = $"-hide_banner -loglevel warning{BuildDecoderThreadArgs()}"; // increased verbosity for debug
                if (startOffsetSeconds > 0.05)
                {
                    args += $" -ss {startOffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture)}";
                }
                if (_loopPlayback) args += " -stream_loop -1";
                if (!_offlineRenderEnabled)
                {
                    args += " -re"; // Realtime reading for interactive playback.
                }
                args += $" -i \"{url}\" -map 0:v:0 -an -sn -dn";
                string videoFilter = BuildVideoOutputFilter(produceExactTargetFrames, processOutputFitMode, processWidth, processHeight);
                if (!string.IsNullOrWhiteSpace(videoFilter))
                {
                    args += $" -vf \"{videoFilter}\"";
                }
                if (!_offlineRenderEnabled)
                {
                    // Scaling a single live stream does not benefit enough from a
                    // second filter pool to justify multiplying worker threads across
                    // every active video layer.
                    args += " -filter_threads 1";
                }
                args += $" -f rawvideo -pix_fmt bgra -s {processWidth}x{processHeight} -";

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

                var process = Process.Start(psi);
                if (process == null) throw new InvalidOperationException("Failed to start ffmpeg.");
                TryLowerChildProcessPriority(
                    process,
                    "ffmpeg video decode",
                    _lowContentionMode,
                    preferThroughput: _offlineRenderEnabled);
                _process = process;

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) Logger.Warn($"[ffmpeg] {e.Data}");
                };
                process.BeginErrorReadLine();

                using var stream = process.StandardOutput.BaseStream;
                int frameSize = processWidth * processHeight * 4;
                _readerBuffer = new byte[frameSize];
                
                int framesRead = 0;

                while (!token.IsCancellationRequested && IsProcessAlive(process))
                {
                    byte[]? readBuffer = _readerBuffer;
                    if (readBuffer == null)
                    {
                        break;
                    }

                    int totalRead = 0;
                    while (totalRead < frameSize)
                    {
                        int read = stream.Read(readBuffer, totalRead, frameSize - totalRead);
                        if (read == 0) break; // EOF
                        totalRead += read;
                    }

                    if (totalRead < frameSize) 
                    {
                        bool exited = !IsProcessAlive(process);
                        Logger.Warn($"ffmpeg stream ended (read {totalRead}/{frameSize} bytes). Exited: {exited}");
                        if (!token.IsCancellationRequested && exited && TryGetProcessExitCode(process, out int exitCode) && exitCode == 0)
                        {
                            _ended = true;
                            lock (_lock)
                            {
                                Monitor.PulseAll(_lock);
                            }
                        }
                        break; 
                    }

                    framesRead++;

                    // We have a full frame. Publish it.
                    lock (_lock)
                    {
                        while (_offlineRenderEnabled &&
                               _pendingRawFrames.Count >= maxQueuedRawFrames &&
                               !token.IsCancellationRequested)
                        {
                            Monitor.Wait(_lock, millisecondsTimeout: 100);
                        }

                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        if (_pendingRawFrames.Count >= maxQueuedRawFrames)
                        {
                            RecycleRawFrameBufferNoLock(_pendingRawFrames.Dequeue(), frameSize);
                        }

                        _pendingRawFrames.Enqueue(readBuffer);
                        _readerBuffer = AcquireRawFrameBufferNoLock(frameSize);
                        Monitor.PulseAll(_lock);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"FFmpeg worker error: {ex.Message}", ex);
                _hasError = true;
                lock (_lock)
                {
                    Monitor.PulseAll(_lock);
                }
            }
        }

        private static bool IsProcessAlive(Process process)
        {
            try
            {
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetProcessExitCode(Process process, out int exitCode)
        {
            try
            {
                exitCode = process.ExitCode;
                return true;
            }
            catch
            {
                exitCode = -1;
                return false;
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

        internal void SetInitialPlaybackOffsetSeconds(double offsetSeconds)
        {
            lock (_audioLock)
            {
                double normalized = NormalizeOffsetNoLock(offsetSeconds);
                _pausedOffsetSeconds = normalized;
                _playbackBaseOffsetSeconds = normalized;
                if (_playbackPaused)
                {
                    _playbackClock.Reset();
                }
                else
                {
                    _playbackClock.Restart();
                }
            }
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

            StopVideoPipeline(preserveReadyFrame: true);
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
                _workerTask = RunBackgroundLongRunning(
                    () => InitializeAsync().GetAwaiter().GetResult(),
                    "LifeViz.VideoInitialize"); // Re-initialize (and re-resolve URL if needed).
                return;
            }

            _cts = new CancellationTokenSource();
            _workerTask = RunBackgroundLongRunning(
                () => FfmpegWorker(_videoPlaybackUrl!, _cts.Token, startOffsetSeconds),
                "LifeViz.VideoDecode");
            RefreshAudioPlayback();
        }

        private void EnsureVideoWorkerStarted()
        {
            if (_isDisposed || _workerTask != null || string.IsNullOrWhiteSpace(_videoPlaybackUrl))
            {
                return;
            }

            double startOffsetSeconds;
            lock (_audioLock)
            {
                if (_playbackPaused)
                {
                    return;
                }

                startOffsetSeconds = GetEstimatedPlaybackOffsetSecondsNoLock();
            }

            _cts = new CancellationTokenSource();
            _workerTask = RunBackgroundLongRunning(
                () => FfmpegWorker(_videoPlaybackUrl!, _cts.Token, startOffsetSeconds),
                "LifeViz.VideoDecode");
            RefreshAudioPlayback();
        }

        private void StopVideoPipeline(bool preserveReadyFrame = false)
        {
            var cts = _cts;
            _cts = null;
            var workerTask = _workerTask;
            _workerTask = null;
            try { cts?.Cancel(); } catch { }
            try { _process?.Kill(); } catch { }
            try { workerTask?.Wait(TimeSpan.FromMilliseconds(250)); } catch { }
            _process?.Dispose();
            _process = null;
            try { cts?.Dispose(); } catch { }

            lock (_lock)
            {
                _pendingRawFrames.Clear();
                _rawFramePool.Clear();
                _readerBuffer = null;
                if (!preserveReadyFrame)
                {
                    _readyDownscaled = null;
                    _readyRaw = null;
                    _readyWidth = 0;
                    _readyHeight = 0;
                    _readyDownscaledWidth = 0;
                    _readyDownscaledHeight = 0;
                }
            }
        }

        private byte[] AcquireRawFrameBufferNoLock(int requiredSize)
        {
            while (_rawFramePool.Count > 0)
            {
                byte[] buffer = _rawFramePool.Pop();
                if (buffer.Length == requiredSize)
                {
                    return buffer;
                }
            }

            return new byte[requiredSize];
        }

        private void RecycleRawFrameBufferNoLock(byte[] buffer, int requiredSize)
        {
            int maxPooledFrames = (_offlineRenderEnabled
                ? MaxQueuedOfflineRawFrames
                : MaxQueuedLiveRawFrames) * 2;
            if (buffer.Length == requiredSize && _rawFramePool.Count < maxPooledFrames)
            {
                _rawFramePool.Push(buffer);
            }
        }

        public void SetAudioEnabled(bool enabled)
        {
            lock (_audioLock)
            {
                _audioEnabled = enabled;
            }

            RefreshAudioPlayback();
        }

        public void SetLiveAudioAnalysisEnabled(bool enabled)
        {
            bool changed;
            lock (_audioLock)
            {
                changed = _liveAudioAnalysisEnabled != enabled;
                _liveAudioAnalysisEnabled = enabled;
            }

            if (!changed)
            {
                return;
            }

            StopAudioPlayback();
            RefreshAudioPlayback();
        }

        public int MixLiveAudioSamples(Span<float> destination)
        {
            if (destination.Length == 0)
            {
                return 0;
            }

            double volume;
            lock (_audioLock)
            {
                if (!_liveAudioAnalysisEnabled || !_audioEnabled || _playbackPaused || _isDisposed)
                {
                    return 0;
                }

                volume = GetEffectiveVolumeNoLock();
            }

            if (volume <= 0.0001)
            {
                return 0;
            }

            int sampleCount;
            lock (_liveAudioAnalysisLock)
            {
                sampleCount = Math.Min(destination.Length, _liveAudioAnalysisCount);
                for (int i = 0; i < sampleCount; i++)
                {
                    destination[i] += _liveAudioAnalysisBuffer[_liveAudioAnalysisReadIndex] * (float)volume;
                    _liveAudioAnalysisReadIndex = (_liveAudioAnalysisReadIndex + 1) % LiveAudioAnalysisBufferCapacity;
                }

                _liveAudioAnalysisCount -= sampleCount;
            }

            return sampleCount;
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
                                !IsAudioPipelineRunningNoLock();
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
                                !IsAudioPipelineRunningNoLock();
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

        private bool IsAudioPipelineRunningNoLock()
        {
            if (_audioDecodeProcess == null || _audioDecodeProcess.HasExited)
            {
                return false;
            }

            return _liveAudioAnalysisEnabled ||
                   (_audioOutput != null && _audioOutput.PlaybackState == PlaybackState.Playing);
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
                                  !_offlineRenderEnabled &&
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
                if (IsAudioPipelineRunningNoLock())
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
            bool analysisOnly;
            lock (_audioLock)
            {
                analysisOnly = _liveAudioAnalysisEnabled;
            }
            var seekSeconds = GetEstimatedPlaybackOffsetSeconds();
            string args = "-hide_banner -loglevel warning";
            if (seekSeconds > 0.05)
            {
                args += $" -ss {seekSeconds.ToString("0.###", CultureInfo.InvariantCulture)}";
            }
            args += $"{BuildDecoderThreadArgs()} -re";
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

                TryLowerChildProcessPriority(process, $"ffmpeg audio decode for {DisplayName}", _lowContentionMode);

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

                BufferedWaveProvider? bufferProvider = null;
                WaveOutEvent? output = null;
                if (!analysisOnly)
                {
                    bufferProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
                    {
                        BufferDuration = TimeSpan.FromSeconds(1.5),
                        DiscardOnBufferOverflow = true
                    };
                    output = new WaveOutEvent
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
                }

                var audioCts = new CancellationTokenSource();
                var decodeTask = RunBackgroundLongRunning(
                    () => AudioDecodeWorker(process, audioCts.Token, () => Volatile.Read(ref fatalAudioError) != 0),
                    "LifeViz.AudioDecode");

                bool shouldAbort = false;
                lock (_audioLock)
                {
                    shouldAbort = _isDisposed ||
                                  !_audioEnabled ||
                                  _playbackPaused ||
                                  GetEffectiveVolumeNoLock() <= 0.0001 ||
                                  _liveAudioAnalysisEnabled != analysisOnly;
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

                Logger.Info(analysisOnly
                    ? $"Started silent video-stack audio analysis for {DisplayName}."
                    : $"Started in-app audio for {DisplayName}.");
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

        internal double GetPlaybackOffsetSecondsForDiagnostics() => GetEstimatedPlaybackOffsetSeconds();

        internal double GetPlaybackBaseOffsetSecondsForDiagnostics()
        {
            lock (_audioLock)
            {
                return NormalizeOffsetNoLock(_playbackBaseOffsetSeconds);
            }
        }

        private double GetEstimatedPlaybackOffsetSecondsNoLock()
        {
            if (_playbackPaused)
            {
                return NormalizeOffsetNoLock(_pausedOffsetSeconds);
            }

            if (_offlineRenderEnabled && _offlineRenderFps > 0)
            {
                return NormalizeOffsetNoLock(
                    _offlineStartOffsetSeconds + (_offlineFramesConsumed / (double)_offlineRenderFps));
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
                    bool analyzeSilently;
                    lock (_audioLock)
                    {
                        provider = _audioBuffer;
                        analyzeSilently = _liveAudioAnalysisEnabled;
                    }

                    if (analyzeSilently)
                    {
                        AppendLiveAudioAnalysisSamples(buffer.AsSpan(0, read));
                    }
                    else
                    {
                        provider?.AddSamples(buffer, 0, read);
                    }
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

        private void AppendLiveAudioAnalysisSamples(ReadOnlySpan<byte> pcm16Stereo)
        {
            int frameCount = pcm16Stereo.Length / 4;
            if (frameCount <= 0)
            {
                return;
            }

            lock (_liveAudioAnalysisLock)
            {
                for (int frame = 0; frame < frameCount; frame++)
                {
                    int offset = frame * 4;
                    short left = (short)(pcm16Stereo[offset] | (pcm16Stereo[offset + 1] << 8));
                    short right = (short)(pcm16Stereo[offset + 2] | (pcm16Stereo[offset + 3] << 8));
                    float mono = ((left + right) * 0.5f) / 32768f;

                    if (_liveAudioAnalysisCount == LiveAudioAnalysisBufferCapacity)
                    {
                        _liveAudioAnalysisReadIndex =
                            (_liveAudioAnalysisReadIndex + 1) % LiveAudioAnalysisBufferCapacity;
                        _liveAudioAnalysisCount--;
                    }

                    _liveAudioAnalysisBuffer[_liveAudioAnalysisWriteIndex] = mono;
                    _liveAudioAnalysisWriteIndex =
                        (_liveAudioAnalysisWriteIndex + 1) % LiveAudioAnalysisBufferCapacity;
                    _liveAudioAnalysisCount++;
                }
            }
        }

        private void ClearLiveAudioAnalysisBuffer()
        {
            lock (_liveAudioAnalysisLock)
            {
                _liveAudioAnalysisReadIndex = 0;
                _liveAudioAnalysisWriteIndex = 0;
                _liveAudioAnalysisCount = 0;
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

            ClearLiveAudioAnalysisBuffer();

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

        internal static bool ProbeVideo(string path, out int width, out int height, out double durationSeconds)
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
        private readonly object _lock = new();
        private readonly Dictionary<string, VideoSession.VideoProbeInfo> _probeCache = new(StringComparer.OrdinalIgnoreCase);
        private int _index;
        private VideoSession? _current;
        private Task<VideoSession?>? _pendingAdvanceTask;
        private int _pendingAdvanceIndex = -1;
        private int _errorStreak;
        private bool _hasError;
        private bool _audioEnabled;
        private double _audioVolume = 1.0;
        private bool _masterAudioEnabled = true;
        private double _masterAudioVolume = 1.0;
        private bool _liveAudioAnalysisEnabled;
        private bool _playbackPaused;
        private bool _lowContentionMode;
        private int _decoderThreadLimit;
        private int _videoDecodeFpsLimit;
        private bool _offlineRenderEnabled;
        private int _offlineRenderFps;
        private FileCaptureFrame? _lastFrame;
            
        public VideoSequenceSession(IReadOnlyList<string> paths)
        {
            _paths = new List<string>(paths);
            _displayName = BuildDisplayName(_paths);
            _index = 0;
            TryCacheProbe(_paths[_index]);
            _current = new VideoSession(_paths[_index], loopPlayback: false, GetCachedProbe(_paths[_index]));
            _current.SetMasterAudio(_masterAudioEnabled, _masterAudioVolume);
            _current.SetLiveAudioAnalysisEnabled(_liveAudioAnalysisEnabled);
            _current.SetAudioVolume(_audioVolume);
            _current.SetAudioEnabled(_audioEnabled);
            _current.SetPlaybackPaused(_playbackPaused);
            _current.SetPerformanceSettings(_lowContentionMode, _decoderThreadLimit, _videoDecodeFpsLimit);
            _current.SetOfflineRenderMode(_offlineRenderEnabled, _offlineRenderFps);
            _ = RunBackgroundLongRunning(
                PrimeProbeCache,
                "LifeViz.SequenceProbePrime");
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

                            lock (_lock)
                            {
                                if (_pendingAdvanceTask != null)
                                {
                                    return FileCaptureState.Pending;
                                }

                                if (_current == null)
                                {
                                    return _lastFrame.HasValue
                                        ? FileCaptureState.Ready
                                        : FileCaptureState.Pending;
                                }

                                return _current.State;
                            }
                        }
                    }
            
                    public FileCaptureFrame? CaptureFrame(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource)
                    {
                        TryPromotePendingAdvance();
                        if (_offlineRenderEnabled)
                        {
                            Task<VideoSession?>? pendingTask;
                            lock (_lock)
                            {
                                pendingTask = _pendingAdvanceTask;
                            }

                            if (pendingTask != null && !pendingTask.IsCompleted)
                            {
                                try
                                {
                                    pendingTask.Wait(TimeSpan.FromSeconds(30));
                                }
                                catch (AggregateException)
                                {
                                    // TryPromotePendingAdvance records the underlying failure.
                                }

                                TryPromotePendingAdvance();
                            }
                        }

                        if (_hasError)
                        {
                            return _lastFrame;
                        }

                        VideoSession? current;
                        lock (_lock)
                        {
                            current = _current;
                        }

                        if (current == null)
                        {
                            return _lastFrame;
                        }
            
                        var frame = current.CaptureFrame(targetWidth, targetHeight, fitMode, includeSource);
                        if (frame.HasValue)
                        {
                            _lastFrame = frame;
                            _errorStreak = 0;
                        }

                        if (current.ConsumeEnded())
                        {
                            BeginAdvance(isError: false);
                            return frame ?? _lastFrame;
                        }
            
                        if (current.State == FileCaptureState.Error)
                        {
                            if (!BeginAdvance(isError: true))
                            {
                                Logger.Warn($"All videos in sequence failed: {_displayName}");
                                _hasError = true;
                            }

                            return frame ?? _lastFrame;
                        }
            
                        return frame ?? _lastFrame;
                    }
            
        public void Restart()
        {
            _hasError = false;
            _errorStreak = 0;
            lock (_lock)
            {
                _pendingAdvanceTask = null;
                _pendingAdvanceIndex = -1;
            }
            _current?.Dispose();
            _index = 0;
            _current = new VideoSession(_paths[_index], loopPlayback: false);
            _current.SetMasterAudio(_masterAudioEnabled, _masterAudioVolume);
            _current.SetLiveAudioAnalysisEnabled(_liveAudioAnalysisEnabled);
            _current.SetAudioVolume(_audioVolume);
            _current.SetAudioEnabled(_audioEnabled);
            _current.SetPlaybackPaused(_playbackPaused);
            _current.SetPerformanceSettings(_lowContentionMode, _decoderThreadLimit, _videoDecodeFpsLimit);
            _current.SetOfflineRenderMode(_offlineRenderEnabled, _offlineRenderFps);
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

        public void SetLiveAudioAnalysisEnabled(bool enabled)
        {
            _liveAudioAnalysisEnabled = enabled;
            _current?.SetLiveAudioAnalysisEnabled(enabled);
        }

        public void SetPlaybackPaused(bool paused)
        {
            _playbackPaused = paused;
            _current?.SetPlaybackPaused(paused);
        }

        public void SetPerformanceSettings(bool lowContentionMode, int decoderThreadLimit, int videoDecodeFpsLimit)
        {
            _lowContentionMode = lowContentionMode;
            _decoderThreadLimit = Math.Clamp(decoderThreadLimit, 0, 8);
            _videoDecodeFpsLimit = videoDecodeFpsLimit == 15 || videoDecodeFpsLimit == 30 ? videoDecodeFpsLimit : 0;
            _current?.SetPerformanceSettings(_lowContentionMode, _decoderThreadLimit, _videoDecodeFpsLimit);
        }

        public void SetOfflineRenderMode(bool enabled, int fps)
        {
            _offlineRenderEnabled = enabled;
            _offlineRenderFps = enabled ? Math.Clamp(fps, 1, 144) : 0;
            _current?.SetOfflineRenderMode(_offlineRenderEnabled, _offlineRenderFps);
        }

        public bool MixOfflineAudioFrame(Span<float> destination)
        {
            if (!_offlineRenderEnabled)
            {
                return false;
            }

            TryPromotePendingAdvance();
            Task<VideoSession?>? pendingTask;
            lock (_lock)
            {
                pendingTask = _pendingAdvanceTask;
            }
            if (pendingTask != null && !pendingTask.IsCompleted)
            {
                try { pendingTask.Wait(TimeSpan.FromSeconds(30)); } catch (AggregateException) { }
                TryPromotePendingAdvance();
            }

            return _current?.MixOfflineAudioFrame(destination) == true;
        }

        public int MixLiveAudioSamples(Span<float> destination)
        {
            TryPromotePendingAdvance();
            return _current?.MixLiveAudioSamples(destination) ?? 0;
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
                        lock (_lock)
                        {
                            _pendingAdvanceTask = null;
                            _pendingAdvanceIndex = -1;
                        }
                        _current?.Dispose();
                        _current = null;
                    }
            
        private bool BeginAdvance(bool isError)
        {
            lock (_lock)
            {
                if (_pendingAdvanceTask != null)
                {
                    return true;
                }
            }

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

            VideoSession? previous;
            int nextIndex;
            lock (_lock)
            {
                previous = _current;
                _current = null;
                nextIndex = (_index + 1) % _paths.Count;
                _pendingAdvanceIndex = nextIndex;
                _pendingAdvanceTask = RunBackgroundLongRunning<VideoSession?>(
                    () => CreateSequenceVideoSession(_paths[nextIndex]),
                    "LifeViz.SequenceAdvance");
            }

            if (previous != null)
            {
                _ = RunBackgroundLongRunning(
                    previous.Dispose,
                    "LifeViz.SequenceDispose");
            }

            return true;
        }

        private void TryPromotePendingAdvance()
        {
            Task<VideoSession?>? pendingTask;
            int pendingIndex;
            lock (_lock)
            {
                pendingTask = _pendingAdvanceTask;
                pendingIndex = _pendingAdvanceIndex;
            }

            if (pendingTask == null || !pendingTask.IsCompleted)
            {
                return;
            }

            VideoSession? nextSession = null;
            try
            {
                nextSession = pendingTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to advance video sequence {_displayName}. {ex.Message}");
            }

            lock (_lock)
            {
                _pendingAdvanceTask = null;
                _pendingAdvanceIndex = -1;
                if (nextSession != null)
                {
                    _current = nextSession;
                    _index = pendingIndex;
                }
            }

            if (nextSession == null)
            {
                _hasError = true;
            }
        }

        private VideoSession CreateSequenceVideoSession(string path)
        {
            var session = new VideoSession(path, loopPlayback: false, GetCachedProbe(path));
            session.SetMasterAudio(_masterAudioEnabled, _masterAudioVolume);
            session.SetLiveAudioAnalysisEnabled(_liveAudioAnalysisEnabled);
            session.SetAudioVolume(_audioVolume);
            session.SetAudioEnabled(_audioEnabled);
            session.SetPlaybackPaused(_playbackPaused);
            session.SetPerformanceSettings(_lowContentionMode, _decoderThreadLimit, _videoDecodeFpsLimit);
            session.SetOfflineRenderMode(_offlineRenderEnabled, _offlineRenderFps);
            return session;
        }

        private void PrimeProbeCache()
        {
            foreach (string path in _paths)
            {
                TryCacheProbe(path);
            }
        }

        private void TryCacheProbe(string path)
        {
            lock (_lock)
            {
                if (_probeCache.ContainsKey(path))
                {
                    return;
                }
            }

            if (!VideoSession.ProbeVideo(path, out int width, out int height, out double durationSeconds))
            {
                return;
            }

            var probe = new VideoSession.VideoProbeInfo(width, height, durationSeconds);
            lock (_lock)
            {
                _probeCache[path] = probe;
            }
        }

        private VideoSession.VideoProbeInfo? GetCachedProbe(string path)
        {
            lock (_lock)
            {
                return _probeCache.TryGetValue(path, out var probe) ? probe : null;
            }
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

    internal sealed class AutoClipSession : IDisposable
    {
        private enum Phase
        {
            Uninitialized,
            Playing,
            Delaying,
            Empty
        }

        private readonly List<string> _paths = new();
        private readonly object _pathsLock = new();
        private readonly Dictionary<string, VideoSession.VideoProbeInfo> _probeCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _offlineSeed = Random.Shared.Next();
        private Random _random = new();
        private VideoSession? _current;
        private FileCaptureFrame? _lastFrame;
        private string? _lastFramePath;
        private string? _currentPath;
        private Phase _phase = Phase.Uninitialized;
        private double _phaseStartSeconds;
        private double _phaseEndSeconds;
        private double _lastCaptureTimelineSeconds;
        private double _minClipSeconds;
        private double _maxClipSeconds;
        private double _minDelaySeconds;
        private double _maxDelaySeconds;
        private bool _loopSelectedFile;
        private bool _audioEnabled;
        private double _audioVolume = 1.0;
        private bool _masterAudioEnabled = true;
        private double _masterAudioVolume = 1.0;
        private bool _liveAudioAnalysisEnabled;
        private bool _lowContentionMode;
        private int _decoderThreadLimit;
        private int _videoDecodeFpsLimit;
        private bool _offlineRenderEnabled;
        private int _offlineRenderFps;
        private long _offlineFrameIndex;
        private Phase _savedLivePhase;
        private string? _savedLivePath;
        private double _savedLivePlaybackSeconds;
        private double _savedLivePhaseRemainingSeconds;
        private double _savedLivePhaseElapsedSeconds;
        private double _savedLiveClipDurationSeconds;
        private bool _disposed;

        public AutoClipSession(
            IReadOnlyList<string> paths,
            double minClipSeconds,
            double maxClipSeconds,
            double minDelaySeconds,
            double maxDelaySeconds)
        {
            ApplySettings(paths, minClipSeconds, maxClipSeconds, minDelaySeconds, maxDelaySeconds);
            _ = RunBackgroundLongRunning(PrimeProbeCache, "LifeViz.AutoClipProbePrime");
        }

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_pathsLock)
                {
                    return _paths.ToArray();
                }
            }
        }
        public string DisplayName => _paths.Count == 0 ? "AutoClip" : $"AutoClip ({_paths.Count})";
        public string? CurrentPath => _currentPath;
        public string? CurrentFramePath => _lastFramePath;
        public bool IsDelaying => _phase == Phase.Delaying;
        public bool IsEmpty => _paths.Count == 0;
        public FileCaptureState State => _paths.Count == 0
            ? FileCaptureState.Ready
            : _current?.State ?? FileCaptureState.Pending;

        public double GetVisualOpacity(double fadeSeconds)
        {
            if (_phase != Phase.Playing || _current == null)
            {
                return 0;
            }

            double duration = Math.Max(0, _phaseEndSeconds - _phaseStartSeconds);
            double effectiveFade = Math.Min(Math.Clamp(fadeSeconds, 0, 10), duration / 2.0);
            if (effectiveFade <= 0.0001)
            {
                return 1;
            }

            double elapsed = Math.Max(0, _lastCaptureTimelineSeconds - _phaseStartSeconds);
            double remaining = Math.Max(0, _phaseEndSeconds - _lastCaptureTimelineSeconds);
            return Math.Clamp(Math.Min(elapsed / effectiveFade, remaining / effectiveFade), 0, 1);
        }

        public void UpdateSettings(
            IReadOnlyList<string> paths,
            double minClipSeconds,
            double maxClipSeconds,
            double minDelaySeconds,
            double maxDelaySeconds)
        {
            if (_disposed)
            {
                return;
            }

            ApplySettings(paths, minClipSeconds, maxClipSeconds, minDelaySeconds, maxDelaySeconds);
            ResetSchedule();
            _ = RunBackgroundLongRunning(PrimeProbeCache, "LifeViz.AutoClipProbePrime");
        }

        public void SetLoopSelectedFile(bool enabled)
        {
            if (_loopSelectedFile == enabled)
            {
                return;
            }

            _loopSelectedFile = enabled;
            ResetSchedule();
        }

        public FileCaptureFrame? CaptureFrame(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource)
        {
            double now = GetTimelineSeconds();
            EnsurePhase(now);
            _lastCaptureTimelineSeconds = now;
            if (_phase == Phase.Delaying || _phase == Phase.Empty || _current == null)
            {
                AdvanceOfflineFrame();
                return null;
            }

            FileCaptureFrame? frame = _current.CaptureFrame(targetWidth, targetHeight, fitMode, includeSource);
            if (frame.HasValue)
            {
                _lastFrame = frame;
                _lastFramePath = _currentPath;
            }
            else if (_current.State == FileCaptureState.Error)
            {
                BeginNextPhase(now);
            }

            AdvanceOfflineFrame();
            return frame ?? _lastFrame;
        }

        public void Restart()
        {
            ResetSchedule();
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

        public void SetLiveAudioAnalysisEnabled(bool enabled)
        {
            _liveAudioAnalysisEnabled = enabled;
            _current?.SetLiveAudioAnalysisEnabled(enabled);
        }

        public void SetPerformanceSettings(bool lowContentionMode, int decoderThreadLimit, int videoDecodeFpsLimit)
        {
            _lowContentionMode = lowContentionMode;
            _decoderThreadLimit = Math.Clamp(decoderThreadLimit, 0, 8);
            _videoDecodeFpsLimit = videoDecodeFpsLimit == 15 || videoDecodeFpsLimit == 30 ? videoDecodeFpsLimit : 0;
            _current?.SetPerformanceSettings(_lowContentionMode, _decoderThreadLimit, _videoDecodeFpsLimit);
        }

        public void SetOfflineRenderMode(bool enabled, int fps)
        {
            fps = enabled ? Math.Clamp(fps, 1, 144) : 0;
            if (_offlineRenderEnabled == enabled && _offlineRenderFps == fps)
            {
                return;
            }

            if (enabled)
            {
                double liveNow = _clock.Elapsed.TotalSeconds;
                _savedLivePhase = _phase;
                _savedLivePath = _currentPath;
                _savedLivePhaseRemainingSeconds = Math.Max(0, _phaseEndSeconds - liveNow);
                _savedLivePhaseElapsedSeconds = Math.Max(0, liveNow - _phaseStartSeconds);
                _savedLiveClipDurationSeconds = Math.Max(0, _phaseEndSeconds - _phaseStartSeconds);
                _savedLivePlaybackSeconds = 0;
                if (_current?.TryGetPlaybackState(out VideoPlaybackState playbackState) == true)
                {
                    _savedLivePlaybackSeconds = playbackState.PositionSeconds;
                }

                DisposeCurrent(background: false);
                _offlineRenderEnabled = true;
                _offlineRenderFps = fps;
                _offlineFrameIndex = 0;
                _random = new Random(_offlineSeed);
                _phase = Phase.Uninitialized;
                _phaseStartSeconds = 0;
                _phaseEndSeconds = 0;
                _lastCaptureTimelineSeconds = 0;
                _lastFrame = null;
                _lastFramePath = null;
                return;
            }

            DisposeCurrent(background: false);
            _offlineRenderEnabled = false;
            _offlineRenderFps = 0;
            _clock.Restart();
            _random = new Random();
            _lastFrame = null;
            _lastFramePath = null;

            if (_savedLivePhase == Phase.Delaying && _savedLivePhaseRemainingSeconds > 0.0001)
            {
                _phase = Phase.Delaying;
                _phaseEndSeconds = _savedLivePhaseRemainingSeconds;
                return;
            }

            if (_savedLivePhase == Phase.Playing &&
                !string.IsNullOrWhiteSpace(_savedLivePath) &&
                _paths.Any(path => string.Equals(path, _savedLivePath, StringComparison.OrdinalIgnoreCase)))
            {
                double restoredDuration = Math.Max(
                    _savedLiveClipDurationSeconds,
                    _savedLivePhaseElapsedSeconds + _savedLivePhaseRemainingSeconds);
                StartSpecificClip(
                    _savedLivePath,
                    _savedLivePlaybackSeconds,
                    0,
                    Math.Max(0.05, restoredDuration),
                    _savedLivePhaseElapsedSeconds);
                return;
            }

            _phase = Phase.Uninitialized;
            _phaseStartSeconds = 0;
            _phaseEndSeconds = 0;
        }

        public bool MixOfflineAudioFrame(Span<float> destination)
        {
            if (!_offlineRenderEnabled)
            {
                return false;
            }

            EnsurePhase(GetTimelineSeconds());
            return _phase == Phase.Playing && _current?.MixOfflineAudioFrame(destination) == true;
        }

        public int MixLiveAudioSamples(Span<float> destination)
        {
            if (_offlineRenderEnabled)
            {
                return 0;
            }

            EnsurePhase(GetTimelineSeconds());
            return _phase == Phase.Playing ? _current?.MixLiveAudioSamples(destination) ?? 0 : 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeCurrent(background: false);
            lock (_pathsLock)
            {
                _paths.Clear();
            }
            _probeCache.Clear();
        }

        private void ApplySettings(
            IReadOnlyList<string> paths,
            double minClipSeconds,
            double maxClipSeconds,
            double minDelaySeconds,
            double maxDelaySeconds)
        {
            lock (_pathsLock)
            {
                _paths.Clear();
                foreach (string path in paths)
                {
                    if (!_paths.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
                    {
                        _paths.Add(path);
                    }
                }
            }

            NormalizeRange(minClipSeconds, maxClipSeconds, minimum: 0.05, out _minClipSeconds, out _maxClipSeconds);
            NormalizeRange(minDelaySeconds, maxDelaySeconds, minimum: 0, out _minDelaySeconds, out _maxDelaySeconds);
        }

        private static void NormalizeRange(double min, double max, double minimum, out double normalizedMin, out double normalizedMax)
        {
            min = double.IsFinite(min) ? Math.Clamp(min, minimum, 86400) : minimum;
            max = double.IsFinite(max) ? Math.Clamp(max, minimum, 86400) : min;
            normalizedMin = Math.Min(min, max);
            normalizedMax = Math.Max(min, max);
        }

        private void EnsurePhase(double now)
        {
            if (_disposed)
            {
                return;
            }

            if (_paths.Count == 0)
            {
                DisposeCurrent(background: true);
                _phase = Phase.Empty;
                _lastFrame = null;
                _lastFramePath = null;
                return;
            }

            for (int transitions = 0; transitions < 4; transitions++)
            {
                if (_phase == Phase.Uninitialized || _phase == Phase.Empty)
                {
                    StartRandomClip(now);
                    return;
                }

                if (now < _phaseEndSeconds)
                {
                    return;
                }

                if (_phase == Phase.Playing)
                {
                    BeginNextPhase(now);
                }
                else if (_phase == Phase.Delaying)
                {
                    StartRandomClip(now);
                }
            }
        }

        private void BeginNextPhase(double now)
        {
            DisposeCurrent(background: true);
            double delay = NextRange(_minDelaySeconds, _maxDelaySeconds);
            if (delay > 0.0001)
            {
                _lastFrame = null;
                _lastFramePath = null;
                _phase = Phase.Delaying;
                _phaseStartSeconds = now;
                _phaseEndSeconds = now + delay;
                return;
            }

            _phase = Phase.Uninitialized;
            StartRandomClip(now);
        }

        private void StartRandomClip(double now)
        {
            if (_paths.Count == 0)
            {
                _phase = Phase.Empty;
                return;
            }

            int index = _random.Next(_paths.Count);
            if (_paths.Count > 1 && string.Equals(_paths[index], _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                index = (index + 1 + _random.Next(_paths.Count - 1)) % _paths.Count;
            }

            string path = _paths[index];
            TryCacheProbe(path);
            double sourceDuration = GetCachedProbe(path)?.DurationSeconds ?? 0;
            double requestedClipSeconds = NextRange(_minClipSeconds, _maxClipSeconds);
            (double startSeconds, double clipSeconds) = SelectClipWindow(
                sourceDuration,
                requestedClipSeconds,
                _random.NextDouble(),
                _loopSelectedFile);
            StartSpecificClip(path, startSeconds, now, clipSeconds);
        }

        internal static (double StartSeconds, double ClipSeconds) SelectClipWindow(
            double sourceDurationSeconds,
            double requestedClipSeconds,
            double randomUnit,
            bool loopSelectedFile)
        {
            double requested = double.IsFinite(requestedClipSeconds)
                ? Math.Max(0.05, requestedClipSeconds)
                : 0.05;
            return loopSelectedFile
                ? (0, requested)
                : FitClipWindowToSource(sourceDurationSeconds, requested, randomUnit);
        }

        internal static (double StartSeconds, double ClipSeconds) FitClipWindowToSource(
            double sourceDurationSeconds,
            double requestedClipSeconds,
            double randomUnit)
        {
            double requested = double.IsFinite(requestedClipSeconds)
                ? Math.Max(0.05, requestedClipSeconds)
                : 0.05;
            if (!double.IsFinite(sourceDurationSeconds) || sourceDurationSeconds <= 0.001)
            {
                return (0, requested);
            }

            double sourceDuration = Math.Max(0.001, sourceDurationSeconds);
            // Probe durations and decoder endpoints are not always sample-exact. Leave
            // up to 250 ms (or 2% for short media) unused at the tail so ffmpeg never
            // has to cross EOF while an AutoClip phase is still supposed to be playing.
            double endGuard = Math.Min(0.25, sourceDuration * 0.02);
            double usableDuration = Math.Max(0.001, sourceDuration - endGuard);
            double clipSeconds = Math.Min(requested, usableDuration);
            double maxStartSeconds = Math.Max(0, usableDuration - clipSeconds);
            double unit = double.IsFinite(randomUnit) ? Math.Clamp(randomUnit, 0, 1) : 0;
            double startSeconds = maxStartSeconds * unit;
            return (startSeconds, clipSeconds);
        }

        private void StartSpecificClip(string path, double startSeconds, double now, double clipSeconds, double elapsedSeconds = 0)
        {
            DisposeCurrent(background: true);
            TryCacheProbe(path);
            var session = new VideoSession(path, loopPlayback: true, GetCachedProbe(path));
            session.SetInitialPlaybackOffsetSeconds(startSeconds);
            session.SetMasterAudio(_masterAudioEnabled, _masterAudioVolume);
            session.SetLiveAudioAnalysisEnabled(_liveAudioAnalysisEnabled);
            session.SetAudioVolume(_audioVolume);
            session.SetPerformanceSettings(_lowContentionMode, _decoderThreadLimit, _videoDecodeFpsLimit);
            session.SetOfflineRenderMode(_offlineRenderEnabled, _offlineRenderFps);
            session.SetAudioEnabled(_audioEnabled);
            _current = session;
            _currentPath = path;
            _phase = Phase.Playing;
            double duration = Math.Max(0.001, clipSeconds);
            double elapsed = Math.Clamp(elapsedSeconds, 0, duration);
            _phaseStartSeconds = now - elapsed;
            _phaseEndSeconds = _phaseStartSeconds + duration;
            _lastCaptureTimelineSeconds = now;
            Logger.Info($"AutoClip selected {Path.GetFileName(path)} at {startSeconds:0.###}s for {clipSeconds:0.###}s.");
        }

        private void ResetSchedule()
        {
            DisposeCurrent(background: true);
            _lastFrame = null;
            _lastFramePath = null;
            _currentPath = null;
            _phase = _paths.Count == 0 ? Phase.Empty : Phase.Uninitialized;
            _phaseStartSeconds = 0;
            _phaseEndSeconds = 0;
            _lastCaptureTimelineSeconds = 0;
            _offlineFrameIndex = 0;
        }

        private double GetTimelineSeconds() => _offlineRenderEnabled && _offlineRenderFps > 0
            ? _offlineFrameIndex / (double)_offlineRenderFps
            : _clock.Elapsed.TotalSeconds;

        private void AdvanceOfflineFrame()
        {
            if (_offlineRenderEnabled)
            {
                _offlineFrameIndex++;
            }
        }

        private double NextRange(double min, double max) => max <= min + 0.000001
            ? min
            : min + (_random.NextDouble() * (max - min));

        private void DisposeCurrent(bool background)
        {
            VideoSession? previous = _current;
            _current = null;
            if (previous == null)
            {
                return;
            }

            if (background)
            {
                _ = RunBackgroundLongRunning(previous.Dispose, "LifeViz.AutoClipDispose");
            }
            else
            {
                previous.Dispose();
            }
        }

        private void PrimeProbeCache()
        {
            string[] paths;
            lock (_pathsLock)
            {
                paths = _paths.ToArray();
            }

            foreach (string path in paths)
            {
                if (_disposed)
                {
                    return;
                }
                TryCacheProbe(path);
            }
        }

        private void TryCacheProbe(string path)
        {
            lock (_probeCache)
            {
                if (_probeCache.ContainsKey(path))
                {
                    return;
                }
            }

            if (!VideoSession.ProbeVideo(path, out int width, out int height, out double durationSeconds))
            {
                return;
            }

            lock (_probeCache)
            {
                _probeCache[path] = new VideoSession.VideoProbeInfo(width, height, durationSeconds);
            }
        }

        private VideoSession.VideoProbeInfo? GetCachedProbe(string path)
        {
            lock (_probeCache)
            {
                return _probeCache.TryGetValue(path, out VideoSession.VideoProbeInfo probe) ? probe : null;
            }
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
                    public FileCaptureFrame(byte[] overlayDownscaled, int downscaledWidth, int downscaledHeight, byte[]? overlaySource, int sourceWidth, int sourceHeight, long frameToken = 0, long framePublishTimestamp = 0)
                    {
                        OverlayDownscaled = overlayDownscaled;
                        DownscaledWidth = downscaledWidth;
                        DownscaledHeight = downscaledHeight;
                        OverlaySource = overlaySource;
                        SourceWidth = sourceWidth;
                        SourceHeight = sourceHeight;
                        FrameToken = frameToken;
                        FramePublishTimestamp = framePublishTimestamp;
                    }
            
                    public byte[] OverlayDownscaled { get; }
                    public int DownscaledWidth { get; }
                    public int DownscaledHeight { get; }
                    public byte[]? OverlaySource { get; }
                    public int SourceWidth { get; }
                    public int SourceHeight { get; }
                    public long FrameToken { get; }
                    public long FramePublishTimestamp { get; }
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
                    const int parallelDownscalePixelThreshold = 640 * 360;

                    if (sourceWidth == targetWidth && sourceHeight == targetHeight)
                    {
                        Buffer.BlockCopy(source, 0, destination, 0, Math.Min(source.Length, destination.Length));
                        return;
                    }

                    int sourceStride = sourceWidth * 4;
                    int destStride = targetWidth * 4;
                    FitMode normalizedFitMode = ImageFit.Normalize(fitMode);

                    if (sourceWidth == targetWidth)
                    {
                        if ((normalizedFitMode == FitMode.Fill || normalizedFitMode == FitMode.Center) && sourceHeight > targetHeight)
                        {
                            int cropTop = Math.Max(0, (sourceHeight - targetHeight) / 2);
                            Buffer.BlockCopy(source, cropTop * sourceStride, destination, 0, Math.Min(destination.Length, targetHeight * destStride));
                            return;
                        }

                        if ((normalizedFitMode == FitMode.Fit || normalizedFitMode == FitMode.Center) && sourceHeight < targetHeight)
                        {
                            Array.Clear(destination, 0, destination.Length);
                            int padTop = Math.Max(0, (targetHeight - sourceHeight) / 2);
                            Buffer.BlockCopy(source, 0, destination, padTop * destStride, Math.Min(source.Length, sourceHeight * sourceStride));
                            return;
                        }
                    }

                    if (sourceHeight == targetHeight)
                    {
                        if ((normalizedFitMode == FitMode.Fill || normalizedFitMode == FitMode.Center) && sourceWidth > targetWidth)
                        {
                            int cropLeftBytes = Math.Max(0, (sourceWidth - targetWidth) / 2) * 4;
                            for (int row = 0; row < targetHeight; row++)
                            {
                                Buffer.BlockCopy(source, row * sourceStride + cropLeftBytes, destination, row * destStride, destStride);
                            }
                            return;
                        }

                        if ((normalizedFitMode == FitMode.Fit || normalizedFitMode == FitMode.Center) && sourceWidth < targetWidth)
                        {
                            Array.Clear(destination, 0, destination.Length);
                            int padLeftBytes = Math.Max(0, (targetWidth - sourceWidth) / 2) * 4;
                            for (int row = 0; row < targetHeight; row++)
                            {
                                Buffer.BlockCopy(source, row * sourceStride, destination, row * destStride + padLeftBytes, sourceStride);
                            }
                            return;
                        }
                    }

                    var mapping = ImageFit.GetMapping(fitMode, sourceWidth, sourceHeight, targetWidth, targetHeight);

                    void DownscaleRow(int row)
                    {
                        int destRowOffset = row * destStride;

                        for (int col = 0; col < targetWidth; col++)
                        {
                            int destIndex = destRowOffset + (col * 4);
                            if (ImageFit.TrySampleMappedBgraSupersampled(source, sourceWidth, sourceHeight, mapping,
                                col + 0.5, row + 0.5, mirror: false,
                                out destination[destIndex],
                                out destination[destIndex + 1],
                                out destination[destIndex + 2],
                                out destination[destIndex + 3]))
                            {
                            }
                            else
                            {
                                destination[destIndex] = 0;
                                destination[destIndex + 1] = 0;
                                destination[destIndex + 2] = 0;
                                destination[destIndex + 3] = 0;
                            }
                        }
                    }

                    int totalPixels = targetWidth * targetHeight;
                    if (totalPixels < parallelDownscalePixelThreshold)
                    {
                        for (int row = 0; row < targetHeight; row++)
                        {
                            DownscaleRow(row);
                        }
                        return;
                    }

                    Parallel.For(0, targetHeight, DownscaleRow);
                }
            }
            
