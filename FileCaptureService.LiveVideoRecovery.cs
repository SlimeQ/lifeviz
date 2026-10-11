using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace lifeviz;

internal sealed partial class FileCaptureService
{
    private sealed partial class VideoSession
    {
        private string _sourceCodecName = string.Empty;
        private readonly object _playbackCommandLock = new();
        private volatile bool _hardwareVideoDecodeUnavailable;
        private int _liveAdaptiveFpsLimit;
        private double _liveVideoProgressSeconds = double.NaN;
        private double _liveVideoProgressSeekSeconds;
        private long _lateVideoSinceTimestamp;
        private int _liveVideoRecoveryPending;
        private int _minimumRateLagReported;
        private int _liveFrameReadDelayMillisecondsForSmoke;
        internal string LiveVideoRecoveryStatus { get; private set; } = string.Empty;

        private bool ShouldUseHardwareVideoDecode() =>
            OperatingSystem.IsWindows() && !_offlineRenderEnabled && !_sourceHasAlpha &&
            !_hardwareVideoDecodeUnavailable && (_nativeWidth > 1920 || _nativeHeight > 1080) &&
            (_sourceCodecName is "hevc" or "h264");

        private int GetEffectiveLiveDecodeFpsLimit()
        {
            int adaptive = Volatile.Read(ref _liveAdaptiveFpsLimit);
            return adaptive <= 0 ? _videoDecodeFpsLimit :
                _videoDecodeFpsLimit <= 0 ? adaptive : Math.Min(adaptive, _videoDecodeFpsLimit);
        }

        internal void SetLiveFrameReadDelayForSmoke(int milliseconds)
        {
            if (!App.IsDiagnosticTestMode) throw new InvalidOperationException("Decoder delay is diagnostic-only.");
            Volatile.Write(ref _liveFrameReadDelayMillisecondsForSmoke, Math.Clamp(milliseconds, 0, 200));
        }

        private void ResetLiveVideoProgress(double seekSeconds)
        {
            Volatile.Write(ref _liveVideoProgressSeconds, double.NaN);
            Volatile.Write(ref _liveVideoProgressSeekSeconds, seekSeconds);
            Interlocked.Exchange(ref _lateVideoSinceTimestamp, 0);
        }

        private bool ConsumeLiveVideoProgress(string? line, long generation)
        {
            if (line == null) return false;
            int separator = line.IndexOf('=');
            if (separator <= 0) return false;
            string key = line.Substring(0, separator);
            if (key == "out_time_us" && long.TryParse(line.AsSpan(separator + 1),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds) &&
                IsCurrentVideoPipeline(generation))
                Volatile.Write(ref _liveVideoProgressSeconds, Math.Max(0, microseconds / 1_000_000.0));
            return key is "frame" or "fps" or "bitrate" or "total_size" or "out_time_us" or
                "out_time_ms" or "out_time" or "dup_frames" or "drop_frames" or "speed" or "progress" ||
                key.StartsWith("stream_", StringComparison.Ordinal);
        }

        private void RecoverSustainedLiveVideoLag()
        {
            // FFmpeg pacing cannot repair a decoder that is continuously slower
            // than the movie. Progress carries actual output PTS, independently
            // of the bookmark and the frame queue's bounded memory usage.
            if (_offlineRenderEnabled || _ownerControlsLivePlaybackActivation ||
                _playbackPaused || _isDisposed || _ended || _liveVideoRecoveryPending != 0) return;
            double outputSeconds = Volatile.Read(ref _liveVideoProgressSeconds);
            if (!double.IsFinite(outputSeconds)) return;
            double elapsed, expectedSeconds;
            lock (_audioLock)
            {
                if (_livePlaybackActivationPending || _playbackPaused) return;
                elapsed = _playbackClock.Elapsed.TotalSeconds;
                expectedSeconds = _playbackBaseOffsetSeconds + elapsed -
                    Volatile.Read(ref _liveVideoProgressSeekSeconds);
            }
            if (elapsed < 10 || expectedSeconds - outputSeconds <= 2.5)
            {
                Interlocked.Exchange(ref _lateVideoSinceTimestamp, 0);
                return;
            }
            long since = Volatile.Read(ref _lateVideoSinceTimestamp);
            if (since == 0)
            {
                Interlocked.CompareExchange(ref _lateVideoSinceTimestamp, Stopwatch.GetTimestamp(), 0);
                return;
            }
            if (Stopwatch.GetElapsedTime(since).TotalSeconds < 3) return;
            int effectiveLimit = GetEffectiveLiveDecodeFpsLimit();
            int currentFps = _sourceFrameRate > 0 ? (int)Math.Ceiling(_sourceFrameRate) : 30;
            if (effectiveLimit > 0) currentFps = Math.Min(currentFps, effectiveLimit);
            if (currentFps <= 5)
            {
                LiveVideoRecoveryStatus = $"Video playback is still falling behind at {currentFps} fps";
                if (Interlocked.Exchange(ref _minimumRateLagReported, 1) == 0)
                    Logger.Warn($"Live video still cannot sustain {currentFps} fps for {DisplayName}; decoder/render resources are insufficient.");
                return;
            }
            if (Interlocked.CompareExchange(ref _liveVideoRecoveryPending, 1, 0) != 0) return;
            int reducedFps = Math.Max(5, currentFps / 2);
            long generation;
            lock (_videoPipelineLock) generation = _videoPipelineGeneration;
            _ = Task.Run(() =>
            {
                try
                {
                    lock (_playbackCommandLock)
                    {
                        if (!IsCurrentVideoPipeline(generation) || _playbackPaused || _isDisposed) return;
                        Volatile.Write(ref _liveAdaptiveFpsLimit, reducedFps);
                        LiveVideoRecoveryStatus = $"Playback adjusted to {reducedFps} fps to keep audio synchronized";
                        Logger.Warn($"Live video fell {expectedSeconds - outputSeconds:F2}s behind for {DisplayName}; reducing decode output to {reducedFps} fps and resynchronizing.");
                        // One recovery skips the accumulated lag, gates the clock and
                        // audio during warm-up, and retains this source's lower budget
                        // across subsequent pause/seek operations. File FPS and saved
                        // scene settings are unchanged. Offline bakes keep every frame.
                        RestartVideoPipeline(GetEstimatedPlaybackOffsetSeconds(), shouldPause: false, forceReResolve: false);
                    }
                }
                finally { Interlocked.Exchange(ref _liveVideoRecoveryPending, 0); }
            });
        }
    }
}
