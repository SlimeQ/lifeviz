using System;
using System.Collections.Generic;
using System.IO;

namespace lifeviz;

internal sealed class ProjectMPlayback : IDisposable
{
    private static readonly TimeSpan OfflineTimeout = TimeSpan.FromSeconds(60);
    private ProjectMRenderWorker? _worker;
    private readonly ProjectMPlaylist _playlist = new();
    private ProjectMSettings _settings = new();
    private readonly float[] _audio = new float[1024];
    private readonly HashSet<string> _failedPresets = new(StringComparer.OrdinalIgnoreCase);
    private string? _loaded;
    private string? _requested;
    private int _loadId;
    private long _consumedToken;
    private double? _lastSceneTime;
    private double _time;
    private double _lastRenderedTime = -1;
    private int _lastWidth, _lastHeight;
    private bool? _offline;
    private bool _fatal;
    private byte[]? _frame;
    public long FrameToken { get; private set; }
    public string Status { get; private set; } = "Preparing projectM...";

    public void Configure(ProjectMSettings settings)
    {
        var next = settings.Clone();
        if (!System.Linq.Enumerable.SequenceEqual(_settings.Presets, next.Presets, StringComparer.OrdinalIgnoreCase)) _failedPresets.Clear();
        _settings = next;
        _playlist.Configure(next);
    }

    public void Pause(double sceneTime) => _lastSceneTime = sceneTime;

    // Live playback never blocks the UI thread on projectM: preset loads and frames run on the
    // layer's render thread and the newest finished frame is shown (one frame of latency).
    // Offline bakes wait for each result so exported frames stay exact and deterministic.
    public byte[]? Render(int width, int height, double sceneTime, bool offline, AudioBeatDetector audio)
    {
        if (_offline != offline || (_lastSceneTime.HasValue && sceneTime < _lastSceneTime))
        {
            Reset();
            _offline = offline;
        }
        if (_lastSceneTime.HasValue) _time += Math.Max(0, sceneTime - _lastSceneTime.Value);
        _lastSceneTime = sceneTime;
        if (_settings.Presets.Count == 0)
        {
            Status = "No presets selected. Open Presets & Playback to build a playlist.";
            return null;
        }
        if (_fatal) return _frame;
        try
        {
            if (_worker == null && _loaded == null) _time = 0;
            if (!ProjectMLibrary.EnsureReady(offline))
            {
                Status = "Preparing the bundled preset library for first use...";
                return null;
            }
            _worker ??= new ProjectMRenderWorker("LifeViz projectM layer");
            if (_worker.FatalError is string fatal) throw new InvalidOperationException(fatal);
            _playlist.Tick(_time, audio.BeatCount);
            if (!ResolvePreset(offline, audio.BeatCount))
            {
                Status = "No playable presets. Check the playlist files; press Retry / Restart after fixing them.";
                if (offline) throw new InvalidOperationException(Status);
                return _frame;
            }
            if (_loaded == null)
            {
                if (_requested != null) Status = $"Loading: {_requested}";
                return null;
            }

            bool stale = _lastRenderedTime != _time || width != _lastWidth || height != _lastHeight;
            if (stale)
            {
                audio.CopyProjectMPcm(_audio, offline);
                _worker.RequestRender(width, height, _time, _audio);
                _lastRenderedTime = _time; _lastWidth = width; _lastHeight = height;
                if (offline && !_worker.WaitForFrame(_consumedToken, OfflineTimeout))
                    throw new InvalidOperationException(_worker.FatalError ?? "projectM did not finish a frame in time.");
            }

            byte[]? pixels = _worker.AcquireLatest(out int frameWidth, out int frameHeight, out long token);
            if (token != _consumedToken)
            {
                _consumedToken = token;
                if (pixels != null && frameWidth == width && frameHeight == height)
                {
                    _frame = pixels;
                    FrameToken++;
                }
            }
            if (_worker.FatalError is string failed) throw new InvalidOperationException(failed);
            return _frame;
        }
        catch (Exception ex)
        {
            Status = $"projectM unavailable: {ex.Message}";
            Logger.Warn(Status);
            _fatal = true;
            _worker?.Dispose(); _worker = null;
            if (offline) throw new InvalidOperationException(Status, ex);
            return _frame;
        }
    }

    // Returns false when every playlist entry has failed.
    private bool ResolvePreset(bool offline, long beatCount)
    {
        if (_requested != null)
        {
            // Live: a load is in flight on the render thread; keep showing the current preset.
            var result = _worker!.TakeLoadResult();
            if (result == null || result.Id != _loadId) return true;
            string requested = _requested;
            _requested = null;
            if (!ApplyLoadResult(requested, result.Error))
            {
                if (AllPresetsFailed) return false;
                _playlist.Move(1, _time, beatCount);
            }
        }

        // Bound retries per frame; a broken preset cannot trap the UI in a loop.
        for (int attempt = 0; attempt < Math.Min(4, _settings.Presets.Count); attempt++)
        {
            string? candidate = _playlist.Current;
            if (candidate == null || candidate == _loaded) break;
            if (!_failedPresets.Contains(candidate))
            {
                string? missing = null;
                try { if (!File.Exists(ProjectMLibrary.Resolve(candidate))) missing = "Preset file is missing."; }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { missing = ex.Message; }
                if (missing != null)
                {
                    MarkFailed(candidate, missing);
                }
                else
                {
                    int id = ++_loadId;
                    _requested = candidate;
                    Logger.Info($"projectM loading preset '{candidate}'.");
                    _worker!.RequestLoad(id, candidate, _time, _settings.TransitionSeconds, _loaded == null);
                    if (!offline) return true;
                    var result = _worker.WaitForLoadResult(id, OfflineTimeout)
                        ?? throw new InvalidOperationException($"projectM did not finish loading '{candidate}' in time.");
                    if (_worker.FatalError is string fatal) throw new InvalidOperationException(fatal);
                    _requested = null;
                    if (ApplyLoadResult(candidate, result.Error)) break;
                }
            }
            if (AllPresetsFailed) return false;
            _playlist.Move(1, _time, beatCount);
        }
        return true;
    }

    private bool AllPresetsFailed => _failedPresets.Count >= _settings.Presets.Count;

    private bool ApplyLoadResult(string candidate, string? error)
    {
        if (error == null)
        {
            _loaded = candidate;
            _lastRenderedTime = -1;
            Status = $"Playing: {candidate}";
            return true;
        }
        MarkFailed(candidate, error);
        return false;
    }

    private void MarkFailed(string candidate, string error)
    {
        _failedPresets.Add(candidate);
        Logger.Warn($"projectM skipped '{candidate}': {error}");
    }

    public void Move(int direction, long beatCount) => _playlist.Move(direction, _time, beatCount);
    public void Reset()
    {
        _worker?.Dispose(); _worker = null;
        _playlist.Reset(); _failedPresets.Clear(); _loaded = null; _requested = null; _fatal = false;
        _consumedToken = 0;
        _time = 0; _lastSceneTime = null; _lastRenderedTime = -1; _frame = null;
        Status = "Preparing projectM...";
    }
    public void Dispose() { _worker?.Dispose(); _worker = null; }
}
