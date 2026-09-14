using System;
using System.Collections.Generic;
using System.IO;

namespace lifeviz;

internal sealed class ProjectMPlayback : IDisposable
{
    private ProjectMRenderer? _renderer;
    private readonly ProjectMPlaylist _playlist = new();
    private ProjectMSettings _settings = new();
    private readonly float[] _audio = new float[1024];
    private readonly HashSet<string> _failedPresets = new(StringComparer.OrdinalIgnoreCase);
    private string? _loaded;
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
            if (_renderer == null && _loaded == null) _time = 0;
            if (!ProjectMLibrary.EnsureReady(offline))
            {
                Status = "Preparing the bundled preset library for first use...";
                return null;
            }
            _renderer ??= new ProjectMRenderer();
            _playlist.Tick(_time, audio.BeatCount);
            // Bound retries per frame; a broken preset cannot trap the UI in a loop.
            for (int attempt = 0; attempt < Math.Min(4, _settings.Presets.Count); attempt++)
            {
                string? candidate = _playlist.Current;
                if (candidate == null || candidate == _loaded) break;
                if (!_failedPresets.Contains(candidate))
                {
                    string? error;
                    try { error = File.Exists(ProjectMLibrary.Resolve(candidate))
                        ? _renderer.Load(candidate, _time, _settings.TransitionSeconds, _loaded == null)
                        : "Preset file is missing."; }
                    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { error = ex.Message; }
                    if (error == null)
                    {
                        _loaded = candidate;
                        _lastRenderedTime = -1;
                        Status = $"Playing: {candidate}";
                        break;
                    }
                    _failedPresets.Add(candidate);
                    Logger.Warn($"projectM skipped '{candidate}': {error}");
                }
                if (_failedPresets.Count >= _settings.Presets.Count)
                {
                    Status = "No playable presets. Check the playlist files; press Retry / Restart after fixing them.";
                    if (offline) throw new InvalidOperationException(Status);
                    return _frame;
                }
                _playlist.Move(1, _time, audio.BeatCount);
            }
            if (_loaded == null) return null;
            if (_lastRenderedTime == _time && width == _lastWidth && height == _lastHeight) return _frame;
            audio.CopyProjectMPcm(_audio, offline);
            _frame = _renderer.Render(width, height, _time, _audio);
            _lastRenderedTime = _time; _lastWidth = width; _lastHeight = height;
            FrameToken++;
            return _frame;
        }
        catch (Exception ex)
        {
            Status = $"projectM unavailable: {ex.Message}";
            Logger.Warn(Status);
            _fatal = true;
            _renderer?.Dispose(); _renderer = null;
            if (offline) throw new InvalidOperationException(Status, ex);
            return _frame;
        }
    }

    public void Move(int direction, long beatCount) => _playlist.Move(direction, _time, beatCount);
    public void Reset()
    {
        _renderer?.Dispose(); _renderer = null;
        _playlist.Reset(); _failedPresets.Clear(); _loaded = null; _fatal = false;
        _time = 0; _lastSceneTime = null; _lastRenderedTime = -1; _frame = null;
        Status = "Preparing projectM...";
    }
    public void Dispose() { _renderer?.Dispose(); _renderer = null; }
}
