using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace lifeviz;

/// <summary>
/// Beat position source for tempo-synced video. MainWindow implements it over the
/// same <see cref="BeatClock"/> that drives layer animations.
/// </summary>
internal interface ITempoClock
{
    /// <summary>Bar-aligned beat position at the current render tick (beat 0 is a downbeat).</summary>
    double BeatPosition { get; }

    double Bpm { get; }
}

/// <summary>How a tempo-synced loop maps onto beats.</summary>
/// <param name="LoopBpm">Tempo the loop was authored at; its beat count is derived from its length.</param>
/// <param name="LoopBeatsOverride">Explicit loop length in beats (0 = derive from LoopBpm).</param>
internal readonly record struct TempoSyncSettings(double LoopBpm, int LoopBeatsOverride = 0)
{
    public const double DefaultLoopBpm = 140;

    public static double NormalizeBpm(double bpm) =>
        double.IsFinite(bpm) && bpm > 0 ? Math.Clamp(bpm, 20, 400) : DefaultLoopBpm;
}

internal static class TempoLoopMath
{
    /// <summary>Loop length in whole beats: frames / fps at the authored tempo.</summary>
    public static int ResolveLoopBeats(int frameCount, double frameRate, double loopBpm, int overrideBeats)
    {
        if (overrideBeats > 0)
        {
            return overrideBeats;
        }

        if (frameCount <= 0 || !(frameRate > 0))
        {
            return BeatClock.BeatsPerBar;
        }

        double beats = frameCount / frameRate * TempoSyncSettings.NormalizeBpm(loopBpm) / 60.0;
        return Math.Max(1, (int)Math.Round(beats));
    }

    /// <summary>
    /// Absolute frame number for a beat position: loop iteration * frameCount + frame
    /// within the loop. Monotonic in <paramref name="beatPosition"/>.
    /// </summary>
    public static long TargetAbsoluteFrame(double beatPosition, int loopBeats, int frameCount)
    {
        if (frameCount <= 0 || loopBeats <= 0 || !double.IsFinite(beatPosition))
        {
            return 0;
        }

        double cycles = beatPosition / loopBeats;
        double loopIndex = Math.Floor(cycles);
        double phase = cycles - loopIndex;
        long local = Math.Clamp((long)Math.Floor(phase * frameCount), 0, frameCount - 1);
        return (long)loopIndex * frameCount + local;
    }

    public static int LocalFrame(long absoluteFrame, int frameCount) =>
        frameCount <= 0 ? 0 : (int)(((absoluteFrame % frameCount) + frameCount) % frameCount);

    /// <summary>
    /// Seconds from now until the bar boundary nearest to now + <paramref name="durationSeconds"/>,
    /// at least half a bar away. Used to snap AutoClip phase ends onto bar lines.
    /// </summary>
    public static double SecondsUntilBarAlignedEnd(double beatPosition, double bpm, double durationSeconds)
    {
        double beatsPerSecond = Math.Clamp(bpm, 10, 400) / 60.0;
        double targetBeats = beatPosition + Math.Max(0, durationSeconds) * beatsPerSecond;
        double endBeats = Math.Round(targetBeats / BeatClock.BeatsPerBar) * BeatClock.BeatsPerBar;
        while (endBeats - beatPosition < BeatClock.BeatsPerBar * 0.5)
        {
            endBeats += BeatClock.BeatsPerBar;
        }

        return (endBeats - beatPosition) / beatsPerSecond;
    }
}

internal sealed partial class FileCaptureService
{
    internal static ITempoClock? TempoClock { get; set; }

    // Exact frame counts (stream-copy packet count) per file; the probe's
    // duration * fps can be off by one, which would drift a streamed loop by a
    // frame every iteration.
    private static readonly ConcurrentDictionary<string, Task<int>> LoopFrameCountTasks = new(StringComparer.OrdinalIgnoreCase);

    private static Task<int> GetLoopFrameCountAsync(string path) =>
        LoopFrameCountTasks.GetOrAdd(BuildVideoProbeCacheKey(path), _ => Task.Run(() => CountVideoFrames(path)));

    private static int CountVideoFrames(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in new[] { "-hide_banner", "-nostdin", "-i", path, "-map", "0:v:0", "-c", "copy", "-f", "null", "-" })
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = FfmpegProcessManager.Shared.Start(psi);
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            process.StandardOutput.BaseStream.CopyTo(Stream.Null);
            if (!process.WaitForExit(15000))
            {
                FfmpegProcessManager.Shared.TerminateAndDispose(process, TimeSpan.FromMilliseconds(500));
                return 0;
            }

            MatchCollection matches = Regex.Matches(stderr.GetAwaiter().GetResult(), @"frame=\s*(\d+)");
            return matches.Count > 0 && int.TryParse(matches[^1].Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames)
                ? frames
                : 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not count frames for tempo-synced loop {Path.GetFileName(path)}: {ex.Message}");
            return 0;
        }
    }

    // Synced File layers each own a player, keyed by layer rather than by path, so
    // two layers on the same file can differ in sync mode or loop BPM. Decoded frames
    // are still shared through LoopFrameCache, which is keyed by path/size/fit.
    private readonly Dictionary<Guid, (string Path, BeatLoopPlayer Player)> _tempoLayers = new();

    /// <summary>
    /// Captures a synced File layer's frame, creating or updating its player on demand
    /// (a new path, e.g. after file replacement, gets a fresh player).
    /// </summary>
    public FileCaptureFrame? CaptureTempoLayerFrame(
        Guid layerId,
        string path,
        TempoSyncSettings settings,
        int targetWidth,
        int targetHeight,
        FitMode fitMode,
        bool includeSource = false)
    {
        if (!SupportsTempoSyncPath(path) || !TryNormalizePath(path, out string fullPath))
        {
            return null;
        }

        BeatLoopPlayer player;
        BeatLoopPlayer? retired = null;
        lock (_lock)
        {
            if (_tempoLayers.TryGetValue(layerId, out var existing) &&
                string.Equals(existing.Path, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                player = existing.Player;
            }
            else
            {
                retired = existing.Player;
                player = new BeatLoopPlayer(fullPath, settings);
                player.SetPerformanceSettings(_decoderThreadLimit, _lowContentionMode);
                player.SetOfflineRenderMode(_offlineRenderEnabled);
                _tempoLayers[layerId] = (fullPath, player);
            }
        }

        if (retired != null)
        {
            MediaDisposalQueue.Enqueue(retired, "tempo loop replaced");
        }

        player.UpdateSettings(settings);
        return player.CaptureFrame(targetWidth, targetHeight, fitMode, includeSource);
    }

    /// <summary>Disposes a layer's synced player (sync turned off, layer removed).</summary>
    public void ReleaseTempoLayer(Guid layerId)
    {
        BeatLoopPlayer? player = null;
        lock (_lock)
        {
            if (_tempoLayers.Remove(layerId, out var existing))
            {
                player = existing.Player;
            }
        }

        if (player != null)
        {
            MediaDisposalQueue.Enqueue(player, "tempo loop released");
        }
    }

    public FileCaptureState GetTempoLayerState(Guid layerId)
    {
        lock (_lock)
        {
            return _tempoLayers.TryGetValue(layerId, out var existing)
                ? existing.Player.State
                : FileCaptureState.Pending;
        }
    }

    internal string? DescribeTempoLayer(Guid layerId)
    {
        lock (_lock)
        {
            return _tempoLayers.TryGetValue(layerId, out var existing) ? existing.Player.Describe() : null;
        }
    }

    internal bool IsTempoLayerActive(Guid layerId)
    {
        lock (_lock)
        {
            return _tempoLayers.ContainsKey(layerId);
        }
    }

    /// <summary>True when an ordinary (real-time) session exists for <paramref name="path"/>.</summary>
    internal bool HasSession(string path) => FindSession(path) != null;

    private void ApplySettingsToTempoLayers()
    {
        List<BeatLoopPlayer> players;
        lock (_lock)
        {
            players = _tempoLayers.Values.Select(entry => entry.Player).ToList();
        }

        foreach (BeatLoopPlayer player in players)
        {
            player.SetPerformanceSettings(_decoderThreadLimit, _lowContentionMode);
            player.SetOfflineRenderMode(_offlineRenderEnabled);
        }
    }

    private void DisposeTempoLayers()
    {
        List<BeatLoopPlayer> players;
        lock (_lock)
        {
            players = _tempoLayers.Values.Select(entry => entry.Player).ToList();
            _tempoLayers.Clear();
        }

        foreach (BeatLoopPlayer player in players)
        {
            player.Dispose();
        }
    }

    internal static bool SupportsTempoSyncPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase) &&
        (IsVideoPath(path) || string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase));


    // VideoSession keeps these members internal; expose them to AutoClip through the interface.
    private sealed partial class VideoSession
    {
        void IAutoClipPlayback.PrimeLiveFramePipeline(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource) =>
            PrimeLiveFramePipeline(targetWidth, targetHeight, fitMode, includeSource);
        double IAutoClipPlayback.GetFirstDecodedFrameAgeSeconds() => GetFirstDecodedFrameAgeSeconds();
        bool IAutoClipPlayback.ActivateLivePlaybackFromFirstFrame(double mediaAgeSeconds, long frameToken, bool deferAudioRefresh) =>
            ActivateLivePlaybackFromFirstFrame(mediaAgeSeconds, frameToken, deferAudioRefresh);
        void IAutoClipPlayback.RefreshActivatedAudioAfterHandoff() => RefreshActivatedAudioAfterHandoff();
        void IAutoClipPlayback.RequestRetirement() => RequestRetirement();
        double IAutoClipPlayback.GetPlaybackClockElapsedForSmoke() => GetPlaybackClockElapsedForSmoke();
    }

    /// <summary>AutoClip clip backed by a <see cref="BeatLoopPlayer"/>. Loops have no media clock or audio.</summary>
    private sealed class BeatLoopClip : IAutoClipPlayback
    {
        private readonly BeatLoopPlayer _player;

        public BeatLoopClip(string path, TempoSyncSettings settings)
        {
            _player = new BeatLoopPlayer(path, settings);
        }

        public FileCaptureState State => _player.State;
        public FileCaptureFrame? CaptureFrame(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource) =>
            _player.CaptureFrame(targetWidth, targetHeight, fitMode, includeSource);
        public void PrimeLiveFramePipeline(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource) =>
            _player.Prime(targetWidth, targetHeight, fitMode);
        public bool ConsumeEnded() => false;
        public double GetFirstDecodedFrameAgeSeconds() => 0;
        public bool ActivateLivePlaybackFromFirstFrame(double mediaAgeSeconds, long frameToken, bool deferAudioRefresh = false) => true;
        public void RefreshActivatedAudioAfterHandoff() { }
        public void RequestRetirement() => _player.StopDecoding();
        public void SetPlaybackPaused(bool paused) { }
        public bool SetPerformanceSettings(bool lowContentionMode, int decoderThreadLimit, int videoDecodeFpsLimit)
        {
            _player.SetPerformanceSettings(decoderThreadLimit, lowContentionMode);
            return false;
        }
        public void SetOfflineRenderMode(bool enabled, int fps) => _player.SetOfflineRenderMode(enabled);
        public void SetMasterAudio(bool enabled, double volume) { }
        public void SetLiveAudioAnalysisEnabled(bool enabled) { }
        public void SetAudioVolume(double volume) { }
        public void SetAudioEnabled(bool enabled) { }
        public bool MixOfflineAudioFrame(Span<float> destination) => false;
        public int MixLiveAudioSamples(Span<float> destination) => 0;
        public double GetPlaybackClockElapsedForSmoke() => 0;
        public bool TryGetPlaybackState(out VideoPlaybackState playbackState)
        {
            playbackState = default;
            return false;
        }

        public int LoopBeats => _player.LoopBeats;
        public void Dispose() => _player.Dispose();
    }

    /// <summary>
    /// Plays a video loop with its frame chosen by the musical clock instead of wall
    /// time: frame = (beat position / loop beats) * frame count. Frames come from the
    /// shared <see cref="LoopFrameCache"/> when the loop fits the RAM budget, otherwise
    /// from a <see cref="LoopStreamDecoder"/> that decodes sequentially and follows the
    /// (forward-only) beat clock, restarting with a seek only when it falls far behind
    /// or the target jumps backwards (downbeat resync).
    /// </summary>
    private sealed class BeatLoopPlayer : IDisposable
    {
        private const int MaxDirectProcessWidth = 1920;
        private const int MaxDirectProcessHeight = 1080;
        private const double OfflineFrameWaitSeconds = 20.0;

        private readonly object _sync = new();
        private readonly string _path;
        private readonly Task<VideoSession.VideoProbeInfo?> _probeTask;
        private readonly Task<int> _frameCountTask;
        private readonly Stopwatch _fallbackClock = Stopwatch.StartNew();
        private TempoSyncSettings _settings;
        private VideoSession.VideoProbeInfo? _probe;
        private int _frameCount;
        private bool _frameCountExact;
        private bool _failed;
        private string? _errorMessage;
        private bool _offline;
        private bool _disposed;
        private int _decoderThreadLimit;
        private bool _lowContentionMode;

        private LoopFrameKey? _key;
        private LoopFrameCache.Entry? _cacheEntry;
        private bool _cacheDeclined;
        private LoopStreamDecoder? _decoder;
        private long _lastRestartTimestamp;

        private byte[]? _displayedBuffer;
        private long _displayedAbsoluteFrame = long.MinValue;
        private byte[]? _downscaled;
        private long _downscaledFrame = long.MinValue;
        private int _downscaledWidth;
        private int _downscaledHeight;
        private FitMode _downscaledFit;
        private long _frameToken;

        public BeatLoopPlayer(string path, TempoSyncSettings settings)
        {
            _path = path;
            _settings = settings;
            _probeTask = GetVideoProbeAsync(path);
            _frameCountTask = GetLoopFrameCountAsync(path);
        }

        /// <summary>
        /// Queues a background cache fill for a loop that is not playing yet (AutoClip
        /// playlists), so later clips start from RAM without a decoder.
        /// </summary>
        public static void Prefetch(string path, TempoSyncSettings settings, int targetWidth, int targetHeight, FitMode fitMode)
        {
            if (LoopFrameCache.BudgetBytes <= 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using var player = new BeatLoopPlayer(path, settings);
                try
                {
                    await Task.WhenAll(player._probeTask, player._frameCountTask).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                lock (player._sync)
                {
                    player._frameCountExact = false;
                    if (!player.TryResolveMetadataNoLock())
                    {
                        return;
                    }

                    LoopFrameKey key = player.BuildKey(targetWidth, targetHeight, fitMode);
                    LoopFrameCache.Prefetch(key, player._frameCount, player.BuildDecodeArguments(key, loop: false, startFrame: 0));
                }
            });
        }

        public int NativeWidth => _probe?.Width ?? 0;
        public int NativeHeight => _probe?.Height ?? 0;

        public int LoopBeats
        {
            get
            {
                lock (_sync)
                {
                    return ResolveLoopBeatsNoLock();
                }
            }
        }

        public FileCaptureState State
        {
            get
            {
                lock (_sync)
                {
                    if (_failed) return FileCaptureState.Error;
                    return _displayedBuffer != null ? FileCaptureState.Ready : FileCaptureState.Pending;
                }
            }
        }

        public void UpdateSettings(TempoSyncSettings settings)
        {
            lock (_sync)
            {
                _settings = settings;
            }
        }

        public void SetPerformanceSettings(int decoderThreadLimit, bool lowContentionMode)
        {
            lock (_sync)
            {
                _decoderThreadLimit = Math.Clamp(decoderThreadLimit, 0, 8);
                _lowContentionMode = lowContentionMode;
            }
        }

        public void SetOfflineRenderMode(bool enabled)
        {
            lock (_sync)
            {
                _offline = enabled;
            }
        }

        public string Describe()
        {
            lock (_sync)
            {
                if (_failed) return $"Tempo sync error: {_errorMessage}";
                if (!_probe.HasValue || _frameCount <= 0) return "Tempo sync: loading";
                string source = _cacheEntry is { IsComplete: true } ? "cached" : _cacheEntry != null ? "caching" : "streaming";
                return $"Tempo sync: {ResolveLoopBeatsNoLock()} beats @ {TempoSyncSettings.NormalizeBpm(_settings.LoopBpm):0.#} BPM ({source})";
            }
        }

        public void StopDecoding()
        {
            lock (_sync)
            {
                DisposeDecoderNoLock();
            }
        }

        /// <summary>Keeps the decoder following the beat without publishing a frame (AutoClip pre-open).</summary>
        public void Prime(int targetWidth, int targetHeight, FitMode fitMode)
        {
            lock (_sync)
            {
                if (!TryPrepareNoLock(targetWidth, targetHeight, fitMode))
                {
                    return;
                }

                long target = ComputeTargetAbsoluteFrameNoLock();
                if (!TryGetCachedFrameNoLock(target, out _))
                {
                    EnsureDecoderFollowingNoLock(target);
                }
            }
        }

        public FileCaptureFrame? CaptureFrame(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
            {
                return null;
            }

            lock (_sync)
            {
                if (_offline)
                {
                    WaitForMetadataNoLock();
                }

                if (!TryPrepareNoLock(targetWidth, targetHeight, fitMode))
                {
                    return null;
                }

                long target = ComputeTargetAbsoluteFrameNoLock();
                if (TryGetCachedFrameNoLock(target, out byte[] cachedFrame))
                {
                    DisposeDecoderNoLock();
                    Publish(target, cachedFrame);
                }
                else if (_offline && _cacheEntry != null && !_cacheEntry.IsFailed)
                {
                    // Deterministic export: the sequential fill will reach the frame.
                    if (_cacheEntry.WaitForFrame(TempoLoopMath.LocalFrame(target, _frameCount), TimeSpan.FromSeconds(OfflineFrameWaitSeconds)) &&
                        TryGetCachedFrameNoLock(target, out cachedFrame))
                    {
                        Publish(target, cachedFrame);
                    }
                }
                else
                {
                    EnsureDecoderFollowingNoLock(target);
                    LoopStreamDecoder? decoder = _decoder;
                    if (decoder != null)
                    {
                        if (_offline)
                        {
                            decoder.WaitForFrame(target, TimeSpan.FromSeconds(OfflineFrameWaitSeconds));
                        }

                        if (decoder.TryTake(target, out long frameNumber, out byte[] buffer))
                        {
                            Publish(frameNumber, buffer);
                        }
                    }
                }

                if (_displayedBuffer == null || _key is not LoopFrameKey key)
                {
                    return null;
                }

                if (key.Width == targetWidth && key.Height == targetHeight)
                {
                    return new FileCaptureFrame(
                        _displayedBuffer,
                        targetWidth,
                        targetHeight,
                        includeSource ? _displayedBuffer : null,
                        includeSource ? key.Width : NativeWidth,
                        includeSource ? key.Height : NativeHeight,
                        _frameToken,
                        Stopwatch.GetTimestamp());
                }

                int length = targetWidth * targetHeight * 4;
                if (_downscaled == null || _downscaled.Length != length)
                {
                    _downscaled = new byte[length];
                    _downscaledFrame = long.MinValue;
                }

                if (_downscaledFrame != _displayedAbsoluteFrame ||
                    _downscaledWidth != targetWidth ||
                    _downscaledHeight != targetHeight ||
                    _downscaledFit != fitMode)
                {
                    Downscale(_displayedBuffer, key.Width, key.Height, _downscaled, targetWidth, targetHeight, fitMode);
                    _downscaledFrame = _displayedAbsoluteFrame;
                    _downscaledWidth = targetWidth;
                    _downscaledHeight = targetHeight;
                    _downscaledFit = fitMode;
                }

                return new FileCaptureFrame(
                    _downscaled,
                    targetWidth,
                    targetHeight,
                    includeSource ? _displayedBuffer : null,
                    includeSource ? key.Width : NativeWidth,
                    includeSource ? key.Height : NativeHeight,
                    _frameToken,
                    Stopwatch.GetTimestamp());
            }
        }

        private void Publish(long absoluteFrame, byte[] buffer)
        {
            if (ReferenceEquals(buffer, _displayedBuffer) && absoluteFrame == _displayedAbsoluteFrame)
            {
                return;
            }

            _displayedBuffer = buffer;
            _displayedAbsoluteFrame = absoluteFrame;
            _frameToken++;
        }

        private void Fail(string message)
        {
            if (_failed) return;
            _failed = true;
            _errorMessage = message;
            Logger.Warn($"Tempo-synced loop {Path.GetFileName(_path)} failed: {message}");
            DisposeDecoderNoLock();
        }

        private void WaitForMetadataNoLock()
        {
            try
            {
                Task.WaitAll(new Task[] { _probeTask, _frameCountTask }, TimeSpan.FromSeconds(OfflineFrameWaitSeconds));
            }
            catch
            {
                // Failures are surfaced by TryPrepareNoLock.
            }
        }

        private bool TryPrepareNoLock(int targetWidth, int targetHeight, FitMode fitMode)
        {
            if (_disposed || _failed)
            {
                return false;
            }

            if (!TryResolveMetadataNoLock())
            {
                return false;
            }

            LoopFrameKey key = BuildKey(targetWidth, targetHeight, fitMode);
            if (_key != key)
            {
                _key = key;
                _cacheEntry = null;
                _cacheDeclined = false;
                DisposeDecoderNoLock();
            }

            // Wait for the exact count before caching/streaming, unless it is slow.
            if (!_frameCountExact && !_offline && _fallbackClock.Elapsed.TotalSeconds < 3)
            {
                return false;
            }

            if (_cacheEntry == null && !_cacheDeclined)
            {
                _cacheEntry = LoopFrameCache.Acquire(key, _frameCount, BuildDecodeArguments(key, loop: false, startFrame: 0));
                _cacheDeclined = _cacheEntry == null;
            }

            if (_cacheEntry is { IsFailed: true })
            {
                _cacheEntry = null;
                _cacheDeclined = true;
            }

            return true;
        }

        private bool TryResolveMetadataNoLock()
        {
            if (!_probe.HasValue)
            {
                if (!_probeTask.IsCompleted)
                {
                    return false;
                }

                VideoSession.VideoProbeInfo? probe = _probeTask.IsCompletedSuccessfully ? _probeTask.Result : null;
                if (!probe.HasValue || probe.Value.Width <= 0 || probe.Value.Height <= 0)
                {
                    Fail("could not probe the video");
                    return false;
                }

                _probe = probe;
                double frameRate = probe.Value.FrameRate > 0 ? probe.Value.FrameRate : 30;
                _frameCount = Math.Max(1, (int)Math.Round(probe.Value.DurationSeconds * frameRate));
            }

            if (!_frameCountExact && _frameCountTask.IsCompleted)
            {
                _frameCountExact = true;
                int exact = _frameCountTask.IsCompletedSuccessfully ? _frameCountTask.Result : 0;
                if (exact > 0 && exact != _frameCount)
                {
                    _frameCount = exact;
                    DisposeDecoderNoLock();
                }
            }

            return true;
        }

        private LoopFrameKey BuildKey(int targetWidth, int targetHeight, FitMode fitMode)
        {
            VideoSession.VideoProbeInfo probe = _probe!.Value;
            FitMode normalized = ImageFit.Normalize(fitMode);
            bool direct = (normalized is FitMode.Fill or FitMode.Fit or FitMode.Stretch) &&
                          targetWidth <= probe.Width &&
                          targetHeight <= probe.Height;
            if (direct)
            {
                return new LoopFrameKey(_path, targetWidth, targetHeight, normalized, Direct: true);
            }

            // Other fit modes (and upscales) decode near native size and scale on the CPU.
            int width = probe.Width;
            int height = probe.Height;
            if (width > MaxDirectProcessWidth || height > MaxDirectProcessHeight)
            {
                double scale = Math.Min(MaxDirectProcessWidth / (double)width, MaxDirectProcessHeight / (double)height);
                width = (int)Math.Round(width * scale);
                height = (int)Math.Round(height * scale);
            }

            return new LoopFrameKey(_path, Math.Max(2, width) & ~1, Math.Max(2, height) & ~1, FitMode.Stretch, Direct: false);
        }

        /// <summary>
        /// FFmpeg arguments for one pass (<paramref name="loop"/> = false, optionally
        /// seeked to <paramref name="startFrame"/>) or an endless loop from frame 0.
        /// <c>-ss</c> and <c>-stream_loop</c> are never combined: FFmpeg wraps a seeked
        /// loop back near the seek point instead of frame 0.
        /// </summary>
        private string BuildDecodeArguments(LoopFrameKey key, bool loop, long startFrame)
        {
            VideoSession.VideoProbeInfo probe = _probe!.Value;
            double frameRate = probe.FrameRate > 0 ? probe.FrameRate : 30;
            string args = "-hide_banner -loglevel error -nostdin";
            int threads = _decoderThreadLimit > 0 ? _decoderThreadLimit : _lowContentionMode ? 1 : 0;
            if (threads > 0)
            {
                args += $" -threads {threads}";
            }

            int localStart = loop ? 0 : TempoLoopMath.LocalFrame(startFrame, _frameCount);
            if (localStart > 0)
            {
                // Input seek is frame accurate when transcoding (decodes from the
                // preceding keyframe and discards frames before the timestamp). Aim a
                // quarter frame early so rounding cannot skip the wanted frame.
                double seconds = (localStart - 0.25) / frameRate;
                args += $" -ss {seconds.ToString("0.######", CultureInfo.InvariantCulture)}";
            }

            if (loop)
            {
                args += " -stream_loop -1";
            }

            args += VideoSession.BuildPreferredVideoDecoderInputArg(
                VideoSession.ResolvePreferredVideoDecoder(probe.CodecName, probe.HasAlpha));
            args += $" -i \"{_path}\" -map 0:v:0 -an -sn -dn";
            string filter = key.Direct
                ? VideoSession.BuildDirectOutputVideoFilter(key.Fit, key.Width, key.Height, probe.HasAlpha)
                : $"scale={key.Width}:{key.Height}";
            args += $" -vf \"{filter}\" -f rawvideo -pix_fmt bgra -s {key.Width}x{key.Height} -";
            return args;
        }

        private int ResolveLoopBeatsNoLock()
        {
            double frameRate = _probe?.FrameRate > 0 ? _probe.Value.FrameRate : 30;
            return TempoLoopMath.ResolveLoopBeats(_frameCount, frameRate, _settings.LoopBpm, _settings.LoopBeatsOverride);
        }

        private long ComputeTargetAbsoluteFrameNoLock()
        {
            ITempoClock? clock = TempoClock;
            double beatPosition = clock?.BeatPosition
                ?? _fallbackClock.Elapsed.TotalSeconds * TempoSyncSettings.NormalizeBpm(_settings.LoopBpm) / 60.0;
            return TempoLoopMath.TargetAbsoluteFrame(beatPosition, ResolveLoopBeatsNoLock(), _frameCount);
        }

        private bool TryGetCachedFrameNoLock(long target, out byte[] frame)
        {
            frame = Array.Empty<byte>();
            LoopFrameCache.Entry? entry = _cacheEntry;
            if (entry == null)
            {
                return false;
            }

            if (entry.IsComplete && entry.FrameCount > 0 && entry.FrameCount != _frameCount)
            {
                // The full decode is authoritative for the loop length.
                _frameCount = entry.FrameCount;
                target = ComputeTargetAbsoluteFrameNoLock();
            }

            if (!entry.TryGetFrame(TempoLoopMath.LocalFrame(target, _frameCount), out frame))
            {
                return false;
            }

            LoopFrameCache.Touch(entry);
            return true;
        }

        private void EnsureDecoderFollowingNoLock(long target)
        {
            if (_key is not LoopFrameKey key)
            {
                return;
            }

            double frameRate = _probe?.FrameRate > 0 ? _probe.Value.FrameRate : 30;
            LoopStreamDecoder? decoder = _decoder;
            if (decoder != null)
            {
                string? error = decoder.Error;
                bool cooled = _offline || Stopwatch.GetElapsedTime(_lastRestartTimestamp).TotalSeconds > 0.75;
                // The decoder only moves forward. The beat clock does too, except on a
                // downbeat resync, which shows up as a target behind the newest one this
                // decoder was asked for and needs a seek; so does falling far behind.
                bool jumpedBack = target < decoder.HighestRequestedFrameNumber - 1;
                bool behind = target - decoder.NextFrameNumber > Math.Max(8, frameRate * 0.75);
                if (!jumpedBack && !((error != null || behind) && cooled))
                {
                    decoder.Request(target);
                    return;
                }

                if (error != null && _displayedBuffer == null)
                {
                    Fail(error);
                    return;
                }

                DisposeDecoderNoLock();
            }

            // Start slightly ahead of the beat so decoder warm-up does not leave it
            // behind; offline starts exactly on the requested frame.
            long start = _offline ? target : target + (long)Math.Ceiling(frameRate * 0.12);
            bool seeked = TempoLoopMath.LocalFrame(start, _frameCount) > 0;
            _decoder = new LoopStreamDecoder(
                Path.GetFileName(_path),
                seeked ? BuildDecodeArguments(key, loop: false, start) : null,
                BuildDecodeArguments(key, loop: true, 0),
                key.Width,
                key.Height,
                start,
                _frameCount,
                _lowContentionMode);
            _decoder.Request(target);
            _lastRestartTimestamp = Stopwatch.GetTimestamp();
        }

        private void DisposeDecoderNoLock()
        {
            LoopStreamDecoder? decoder = _decoder;
            _decoder = null;
            if (decoder != null)
            {
                MediaDisposalQueue.Enqueue(decoder, "tempo loop decoder");
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                DisposeDecoderNoLock();
                _cacheEntry = null;
            }
        }
    }

    internal readonly record struct LoopFrameKey(string Path, int Width, int Height, FitMode Fit, bool Direct);

    /// <summary>
    /// Sequential, forward-only loop decoder. There is no real-time pacing: the reader
    /// thread only pulls a frame when the owner wants it (pipe backpressure throttles
    /// FFmpeg), so playback speed follows the beat clock. Frames are numbered absolutely
    /// (loop iteration * frame count + index). Starting mid-loop runs a seeked one-pass
    /// process for the rest of that iteration, then hands over at the loop boundary to
    /// an endless <c>-stream_loop</c> process from frame 0 that was spawned up front.
    /// </summary>
    internal sealed class LoopStreamDecoder : IDisposable
    {
        private const int Lookahead = 2;
        private const int MaxHeldFrames = 4;

        private readonly object _sync = new();
        private readonly Queue<(long Number, byte[] Buffer)> _frames = new();
        private readonly Stack<byte[]> _pool = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Process> _processes = new();
        private readonly string _displayName;
        private readonly int _frameBytes;
        private readonly int _frameCount;
        private long _wanted;
        private long _highestRequested = long.MinValue;
        private long _nextFrameNumber;
        private byte[]? _handedOut;
        private byte[]? _previousHandedOut;
        private volatile string? _error;
        private readonly long _startTimestamp = Stopwatch.GetTimestamp();
        private static int _startedCount;

        /// <summary>Total decoders started in this process (smokes use it to catch restart loops).</summary>
        internal static int StartedCount => Volatile.Read(ref _startedCount);
        private double _firstFrameSeconds = -1;

        public LoopStreamDecoder(
            string displayName,
            string? seekedArguments,
            string loopArguments,
            int width,
            int height,
            long startFrameNumber,
            int frameCount,
            bool lowContentionMode)
        {
            _displayName = displayName;
            _frameBytes = width * height * 4;
            _frameCount = Math.Max(1, frameCount);
            StartFrameNumber = startFrameNumber;
            _nextFrameNumber = startFrameNumber;
            _wanted = startFrameNumber;
            Interlocked.Increment(ref _startedCount);
            var thread = new Thread(() => Run(seekedArguments, loopArguments, lowContentionMode))
            {
                IsBackground = true,
                Name = "LifeViz.TempoLoopDecode",
                Priority = ThreadPriority.AboveNormal
            };
            thread.Start();
        }

        public long StartFrameNumber { get; }
        public string? Error => _error;

        /// <summary>Seconds from construction to the first decoded frame, or -1 while starting.</summary>
        public double FirstFrameSeconds
        {
            get { lock (_sync) return _firstFrameSeconds; }
        }

        public double AgeSeconds => Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;

        public long NextFrameNumber
        {
            get { lock (_sync) return _nextFrameNumber; }
        }

        public long HighestRequestedFrameNumber
        {
            get { lock (_sync) return _highestRequested; }
        }

        public void Request(long frameNumber)
        {
            lock (_sync)
            {
                _highestRequested = Math.Max(_highestRequested, frameNumber);
                if (frameNumber > _wanted)
                {
                    _wanted = frameNumber;
                    Monitor.PulseAll(_sync);
                }
            }
        }

        public bool WaitForFrame(long frameNumber, TimeSpan timeout)
        {
            Request(frameNumber);
            long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            lock (_sync)
            {
                while (_nextFrameNumber <= frameNumber && _error == null && !_cts.IsCancellationRequested)
                {
                    long remaining = deadline - Stopwatch.GetTimestamp();
                    if (remaining <= 0) return false;
                    Monitor.Wait(_sync, TimeSpan.FromSeconds(Math.Min(0.25, remaining / (double)Stopwatch.Frequency)));
                }

                return _nextFrameNumber > frameNumber;
            }
        }

        /// <summary>Takes the newest decoded frame at or before <paramref name="frameNumber"/>.</summary>
        public bool TryTake(long frameNumber, out long takenNumber, out byte[] buffer)
        {
            takenNumber = 0;
            buffer = Array.Empty<byte>();
            lock (_sync)
            {
                bool found = false;
                while (_frames.Count > 0 && _frames.Peek().Number <= frameNumber)
                {
                    (long number, byte[] frame) = _frames.Dequeue();
                    if (found)
                    {
                        _pool.Push(buffer);
                    }

                    takenNumber = number;
                    buffer = frame;
                    found = true;
                }

                if (!found)
                {
                    return false;
                }

                // The compositor may still read the previous frame this tick; recycle
                // buffers two hand-outs later.
                if (_previousHandedOut != null)
                {
                    _pool.Push(_previousHandedOut);
                }

                _previousHandedOut = _handedOut;
                _handedOut = buffer;
                Monitor.PulseAll(_sync);
                return true;
            }
        }

        private Process? StartProcess(string arguments, bool lowContentionMode)
        {
            var psi = new ProcessStartInfo("ffmpeg", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process process = FfmpegProcessManager.Shared.Start(psi);
            lock (_sync)
            {
                if (_cts.IsCancellationRequested)
                {
                    FfmpegProcessManager.Shared.TerminateAndDispose(process, TimeSpan.FromMilliseconds(250));
                    return null;
                }

                _processes.Add(process);
            }

            TryLowerChildProcessPriority(process, "tempo loop decode", lowContentionMode, preferThroughput: false);
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data) && !_cts.IsCancellationRequested)
                {
                    Logger.Warn($"[ffmpeg-tempo:{_displayName}] {e.Data}");
                }
            };
            process.BeginErrorReadLine();
            return process;
        }

        private void Run(string? seekedArguments, string loopArguments, bool lowContentionMode)
        {
            try
            {
                Process? seeked = seekedArguments != null ? StartProcess(seekedArguments, lowContentionMode) : null;
                Process? looped = StartProcess(loopArguments, lowContentionMode);
                if (looped == null || (seekedArguments != null && seeked == null))
                {
                    return;
                }

                if (seeked != null)
                {
                    // Rest of the current iteration; its EOF is the loop boundary.
                    ReadFrames(seeked.StandardOutput.BaseStream, endOfStreamIsError: false);
                    if (_cts.IsCancellationRequested) return;
                    lock (_sync)
                    {
                        long loopStart = (long)Math.Floor(StartFrameNumber / (double)_frameCount) * _frameCount;
                        _nextFrameNumber = Math.Max(_nextFrameNumber, loopStart + _frameCount);
                    }

                    FfmpegProcessManager.Shared.TerminateAndDispose(seeked, TimeSpan.FromMilliseconds(250));
                }

                ReadFrames(looped.StandardOutput.BaseStream, endOfStreamIsError: true);
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                {
                    _error = ex.Message;
                }
            }
            finally
            {
                lock (_sync)
                {
                    Monitor.PulseAll(_sync);
                }
            }
        }

        private void ReadFrames(Stream stream, bool endOfStreamIsError)
        {
            while (!_cts.IsCancellationRequested)
            {
                byte[] buffer;
                lock (_sync)
                {
                    // Backpressure: decode at most a couple of frames past the target.
                    while (!_cts.IsCancellationRequested &&
                           (_nextFrameNumber > _wanted + Lookahead || _frames.Count >= MaxHeldFrames))
                    {
                        Monitor.Wait(_sync, 250);
                    }

                    if (_cts.IsCancellationRequested) return;
                    buffer = _pool.Count > 0 ? _pool.Pop() : new byte[_frameBytes];
                }

                int read = 0;
                while (read < _frameBytes)
                {
                    int count = stream.Read(buffer, read, _frameBytes - read);
                    if (count == 0) break;
                    read += count;
                }

                if (read < _frameBytes)
                {
                    lock (_sync)
                    {
                        _pool.Push(buffer);
                    }

                    if (endOfStreamIsError && !_cts.IsCancellationRequested)
                    {
                        _error = "decoder ended unexpectedly";
                    }

                    return;
                }

                lock (_sync)
                {
                    long number = _nextFrameNumber;
                    if (_firstFrameSeconds < 0)
                    {
                        _firstFrameSeconds = Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
                        Logger.Info($"Tempo loop decoder for {_displayName}: first frame {_firstFrameSeconds * 1000:0} ms after start (frame {number}).");
                    }

                    _frames.Enqueue((number, buffer));
                    _nextFrameNumber++;
                    if (number <= _wanted)
                    {
                        // Skipping ahead: older held frames can never be shown now.
                        while (_frames.Count > 1 && _frames.Peek().Number < number)
                        {
                            _pool.Push(_frames.Dequeue().Buffer);
                        }
                    }

                    Monitor.PulseAll(_sync);
                }
            }
        }

        public void Dispose()
        {
            Process[] processes;
            lock (_sync)
            {
                if (_cts.IsCancellationRequested) return;
                _cts.Cancel();
                processes = _processes.ToArray();
                _processes.Clear();
                Monitor.PulseAll(_sync);
            }

            foreach (Process process in processes)
            {
                FfmpegProcessManager.Shared.TerminateAndDispose(process, TimeSpan.FromMilliseconds(500));
            }

            _cts.Dispose();
        }
    }

    /// <summary>
    /// Process-wide RAM cache of fully decoded loops (BGRA at the layer's output size),
    /// bounded by a byte budget with least-recently-used eviction. Fills run in the
    /// background through one FFmpeg pass each, at most two at a time, on-demand
    /// requests ahead of prefetches. A loop that would not fit is never cached and the
    /// player streams it instead, so the budget is a hard ceiling rather than a target.
    /// </summary>
    internal static class LoopFrameCache
    {
        private const int MaxConcurrentFills = 2;
        private const long Gibibyte = 1024L * 1024 * 1024;
        private static readonly object Sync = new();
        private static readonly Dictionary<LoopFrameKey, Entry> Entries = new();
        private static readonly LinkedList<Entry> DemandQueue = new();
        private static readonly LinkedList<Entry> PrefetchQueue = new();
        private static long _budgetBytes = DefaultBudgetBytes();
        private static long _usedBytes;
        private static long _useCounter;
        private static int _activeFills;

        public static long BudgetBytes
        {
            get { lock (Sync) return _budgetBytes; }
        }

        public static long UsedBytes
        {
            get { lock (Sync) return _usedBytes; }
        }

        public static int CompleteEntryCount
        {
            get { lock (Sync) return Entries.Values.Count(entry => entry.IsComplete); }
        }

        /// <summary>Auto budget: a quarter of physical memory, at most 16 GiB.</summary>
        public static long DefaultBudgetBytes()
        {
            long physical = GetPhysicalMemoryBytes();
            return physical > 0 ? Math.Min(physical / 4, 16 * Gibibyte) : 2 * Gibibyte;
        }

        /// <summary>Sets the budget (0 disables caching); evicts down to it immediately.</summary>
        public static void SetBudgetBytes(long bytes)
        {
            lock (Sync)
            {
                _budgetBytes = Math.Max(0, bytes);
                EvictToFitNoLock(0, protect: null);
            }
        }

        public static Entry? Acquire(LoopFrameKey key, int expectedFrames, string decodeArguments) =>
            AcquireCore(key, expectedFrames, decodeArguments, prefetch: false);

        public static void Prefetch(LoopFrameKey key, int expectedFrames, string decodeArguments) =>
            AcquireCore(key, expectedFrames, decodeArguments, prefetch: true);

        public static void Touch(Entry entry)
        {
            entry.LastUse = Interlocked.Increment(ref _useCounter);
        }

        private static Entry? AcquireCore(LoopFrameKey key, int expectedFrames, string decodeArguments, bool prefetch)
        {
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out Entry? existing) && !existing.IsFailed)
                {
                    existing.LastUse = Interlocked.Increment(ref _useCounter);
                    if (!prefetch && existing.Node.List == PrefetchQueue)
                    {
                        // Promote: someone is waiting for this loop now.
                        PrefetchQueue.Remove(existing.Node);
                        DemandQueue.AddLast(existing.Node);
                    }

                    return existing;
                }

                long bytes = (long)Math.Max(1, expectedFrames) * key.Width * key.Height * 4;
                if (_budgetBytes <= 0 || bytes > _budgetBytes)
                {
                    return null;
                }

                if (!EvictToFitNoLock(bytes, protect: null))
                {
                    return null;
                }

                var entry = new Entry(key, expectedFrames, decodeArguments, bytes)
                {
                    LastUse = Interlocked.Increment(ref _useCounter)
                };
                Entries[key] = entry;
                _usedBytes += bytes;
                (prefetch ? PrefetchQueue : DemandQueue).AddLast(entry.Node);
                PumpNoLock();
                return entry;
            }
        }

        private static bool EvictToFitNoLock(long incomingBytes, Entry? protect)
        {
            while (_usedBytes + incomingBytes > _budgetBytes)
            {
                Entry? victim = Entries.Values
                    .Where(entry => !ReferenceEquals(entry, protect) && !entry.IsFilling)
                    .OrderBy(entry => entry.LastUse)
                    .FirstOrDefault();
                if (victim == null)
                {
                    return false;
                }

                RemoveNoLock(victim);
            }

            return true;
        }

        private static void RemoveNoLock(Entry entry)
        {
            if (Entries.TryGetValue(entry.Key, out Entry? current) && ReferenceEquals(current, entry))
            {
                Entries.Remove(entry.Key);
                _usedBytes -= entry.ReservedBytes;
            }

            entry.Node.List?.Remove(entry.Node);
            entry.Cancel();
        }

        private static void PumpNoLock()
        {
            while (_activeFills < MaxConcurrentFills)
            {
                LinkedList<Entry> queue = DemandQueue.Count > 0 ? DemandQueue : PrefetchQueue;
                if (queue.Count == 0)
                {
                    return;
                }

                Entry entry = queue.First!.Value;
                queue.RemoveFirst();
                _activeFills++;
                entry.MarkFilling();
                bool background = queue == PrefetchQueue;
                var thread = new Thread(() => Fill(entry, background))
                {
                    IsBackground = true,
                    Name = "LifeViz.TempoLoopCache",
                    Priority = background ? ThreadPriority.BelowNormal : ThreadPriority.Normal
                };
                thread.Start();
            }
        }

        private static void Fill(Entry entry, bool background)
        {
            Process? process = null;
            int frameBytes = entry.Key.Width * entry.Key.Height * 4;
            try
            {
                var psi = new ProcessStartInfo("ffmpeg", entry.DecodeArguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                process = FfmpegProcessManager.Shared.Start(psi);
                entry.AttachProcess(process);
                TryLowerChildProcessPriority(process, "tempo loop cache", lowContentionMode: background, preferThroughput: !background);
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data) && !entry.IsCancelled)
                    {
                        Logger.Warn($"[ffmpeg-tempo-cache:{Path.GetFileName(entry.Key.Path)}] {e.Data}");
                    }
                };
                process.BeginErrorReadLine();
                Stream stream = process.StandardOutput.BaseStream;
                int decoded = 0;
                while (!entry.IsCancelled)
                {
                    var buffer = new byte[frameBytes];
                    int read = 0;
                    while (read < frameBytes)
                    {
                        int count = stream.Read(buffer, read, frameBytes - read);
                        if (count == 0) break;
                        read += count;
                    }

                    if (read < frameBytes)
                    {
                        break;
                    }

                    if (!entry.Append(buffer))
                    {
                        throw new InvalidOperationException("more frames than the loop's probed length");
                    }

                    decoded++;
                }

                if (!entry.IsCancelled)
                {
                    if (decoded == 0)
                    {
                        throw new InvalidOperationException("no frames decoded");
                    }

                    lock (Sync)
                    {
                        long actual = (long)decoded * frameBytes;
                        if (Entries.TryGetValue(entry.Key, out Entry? current) && ReferenceEquals(current, entry))
                        {
                            _usedBytes += actual - entry.ReservedBytes;
                            entry.ReservedBytes = actual;
                        }
                    }

                    entry.Complete();
                    Logger.Info($"Tempo loop cached: {Path.GetFileName(entry.Key.Path)} {entry.Key.Width}x{entry.Key.Height}, {decoded} frames, {decoded * (double)frameBytes / (1024 * 1024):0} MiB (cache {UsedBytes / (double)Gibibyte:0.0}/{BudgetBytes / (double)Gibibyte:0.0} GiB).");
                }
            }
            catch (Exception ex)
            {
                if (!entry.IsCancelled)
                {
                    Logger.Warn($"Tempo loop cache fill failed for {Path.GetFileName(entry.Key.Path)}: {ex.Message}");
                }

                entry.MarkFailed();
                lock (Sync)
                {
                    RemoveNoLock(entry);
                }
            }
            finally
            {
                FfmpegProcessManager.Shared.TerminateAndDispose(process, TimeSpan.FromMilliseconds(500));
                lock (Sync)
                {
                    _activeFills--;
                    EvictToFitNoLock(0, protect: null);
                    PumpNoLock();
                }
            }
        }

        /// <summary>Drops every cached loop (used by smokes and when the budget is disabled).</summary>
        public static void Clear()
        {
            lock (Sync)
            {
                foreach (Entry entry in Entries.Values.ToArray())
                {
                    RemoveNoLock(entry);
                }
            }
        }

        private static long GetPhysicalMemoryBytes()
        {
            try
            {
                return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            }
            catch
            {
                return 0;
            }
        }

        internal sealed class Entry
        {
            private readonly object _sync = new();
            private byte[]?[] _slots;
            private byte[][] _frames = Array.Empty<byte[]>();
            private volatile int _filled;
            private volatile bool _complete;
            private volatile bool _failed;
            private volatile bool _cancelled;
            private volatile bool _filling;
            private Process? _process;

            public Entry(LoopFrameKey key, int expectedFrames, string decodeArguments, long reservedBytes)
            {
                Key = key;
                ExpectedFrames = expectedFrames;
                DecodeArguments = decodeArguments;
                ReservedBytes = reservedBytes;
                Node = new LinkedListNode<Entry>(this);
                MaxFrames = (int)Math.Ceiling(Math.Max(1, expectedFrames) * 1.25) + 2;
                _slots = new byte[MaxFrames][];
            }

            public LoopFrameKey Key { get; }
            public int ExpectedFrames { get; }
            public int MaxFrames { get; }
            public string DecodeArguments { get; }
            public long ReservedBytes { get; set; }
            public long LastUse { get; set; }
            public LinkedListNode<Entry> Node { get; }
            public bool IsComplete => _complete;
            public bool IsFailed => _failed;
            public bool IsCancelled => _cancelled;
            public bool IsFilling => _filling && !_complete && !_failed && !_cancelled;
            public int FrameCount => _complete ? _frames.Length : 0;

            public void MarkFilling() => _filling = true;

            public void AttachProcess(Process process)
            {
                lock (_sync)
                {
                    _process = process;
                    if (_cancelled)
                    {
                        FfmpegProcessManager.Shared.TerminateAndDispose(process, TimeSpan.FromMilliseconds(250));
                    }
                }
            }

            /// <summary>Appends the next decoded frame; returns false once the loop is longer than probed.</summary>
            public bool Append(byte[] frame)
            {
                lock (_sync)
                {
                    if (_filled >= _slots.Length)
                    {
                        return false;
                    }

                    _slots[_filled] = frame;
                    _filled++;
                    Monitor.PulseAll(_sync);
                    return true;
                }
            }

            public int Complete()
            {
                lock (_sync)
                {
                    _frames = _slots.Take(_filled).Select(frame => frame!).ToArray();
                    _slots = Array.Empty<byte[]>();
                    _complete = true;
                    _filling = false;
                    Monitor.PulseAll(_sync);
                    return _frames.Length;
                }
            }

            public void MarkFailed()
            {
                lock (_sync)
                {
                    _failed = true;
                    _filling = false;
                    _frames = Array.Empty<byte[]>();
                    _slots = Array.Empty<byte[]>();
                    _filled = 0;
                    Monitor.PulseAll(_sync);
                }
            }

            public void Cancel()
            {
                Process? process;
                lock (_sync)
                {
                    _cancelled = true;
                    _filling = false;
                    process = _process;
                    Monitor.PulseAll(_sync);
                }

                if (process != null && !_complete)
                {
                    FfmpegProcessManager.Shared.TerminateAndDispose(process, TimeSpan.FromMilliseconds(250));
                }
            }

            public bool TryGetFrame(int index, out byte[] frame)
            {
                frame = Array.Empty<byte>();
                if (_failed || index < 0)
                {
                    return false;
                }

                if (_complete)
                {
                    byte[][] frames = _frames;
                    if (index >= frames.Length) return false;
                    frame = frames[index];
                    return true;
                }

                lock (_sync)
                {
                    if (index >= _filled || index >= _slots.Length || _slots[index] is not byte[] partial)
                    {
                        return false;
                    }

                    frame = partial;
                    return true;
                }
            }

            public bool WaitForFrame(int index, TimeSpan timeout)
            {
                long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
                lock (_sync)
                {
                    while (!_complete && !_failed && !_cancelled && _filled <= index)
                    {
                        long remaining = deadline - Stopwatch.GetTimestamp();
                        if (remaining <= 0) return false;
                        Monitor.Wait(_sync, TimeSpan.FromSeconds(Math.Min(0.25, remaining / (double)Stopwatch.Frequency)));
                    }

                    return _complete || _filled > index;
                }
            }
        }
    }

    /// <summary>
    /// What an AutoClip phase needs from its clip player. Implemented by the ordinary
    /// real-time <see cref="FileCaptureService"/> video session and by tempo-synced loops.
    /// </summary>
    private interface IAutoClipPlayback : IDisposable
    {
        FileCaptureState State { get; }
        FileCaptureFrame? CaptureFrame(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource);
        void PrimeLiveFramePipeline(int targetWidth, int targetHeight, FitMode fitMode, bool includeSource);
        bool ConsumeEnded();
        double GetFirstDecodedFrameAgeSeconds();
        bool ActivateLivePlaybackFromFirstFrame(double mediaAgeSeconds, long frameToken, bool deferAudioRefresh = false);
        void RefreshActivatedAudioAfterHandoff();
        void RequestRetirement();
        void SetPlaybackPaused(bool paused);
        bool SetPerformanceSettings(bool lowContentionMode, int decoderThreadLimit, int videoDecodeFpsLimit);
        void SetOfflineRenderMode(bool enabled, int fps);
        void SetMasterAudio(bool enabled, double volume);
        void SetLiveAudioAnalysisEnabled(bool enabled);
        void SetAudioVolume(double volume);
        void SetAudioEnabled(bool enabled);
        bool MixOfflineAudioFrame(Span<float> destination);
        int MixLiveAudioSamples(Span<float> destination);
        double GetPlaybackClockElapsedForSmoke();
        bool TryGetPlaybackState(out VideoPlaybackState playbackState);
    }
}
