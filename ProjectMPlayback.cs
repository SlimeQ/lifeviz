using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace lifeviz;

// Plays a projectM playlist for one layer. Rendering happens on ProjectMRenderWorker threads so
// the UI thread never waits on projectM. Preset changes are seamless: the next preset loads
// (0.5-2 s of shader compilation) on a separate standby renderer while the current one keeps
// animating, then the two outputs crossfade for the playlist's transition time. Loading into the
// displayed renderer instead froze the layer's image for the whole compile on every change.
// Offline bakes follow the same sequence but wait for each load and frame, so exported frames stay
// tied to the fixed frame clock.
internal sealed class ProjectMPlayback : IDisposable
{
    private static readonly TimeSpan OfflineTimeout = TimeSpan.FromSeconds(60);
    // A fresh renderer starts from a blank feedback buffer. It plays this long off screen (with the
    // live audio) before the crossfade, so the new preset fades in already in motion, close to
    // MilkDrop's native morph, instead of revealing its empty first frames.
    internal const double PreRollSeconds = 0.75;
    // About one preset in ten only warps the image it inherits and draws nothing itself, so from
    // a fresh renderer it stays black. Those are loaded into the playing renderer instead, using
    // projectM's own soft transition (which hands them the previous image), accepting the brief
    // in-place load pause for just those switches.
    private const double BlackFrameLuminance = 3.0;

    private sealed class Renderer(string name) : IDisposable
    {
        public ProjectMRenderWorker Worker { get; } = new(name);
        public long Token;
        public byte[]? Frame;
        public int Width, Height;
        public int RequestedWidth, RequestedHeight;
        public void Dispose() => Worker.Dispose();
    }

    private readonly ProjectMPlaylist _playlist = new();
    private ProjectMSettings _settings = new();
    private readonly float[] _audio = new float[1024];
    private readonly HashSet<string> _failedPresets = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[]?[] _blendBuffers = new byte[]?[2];
    private Renderer? _active, _incoming, _outgoing;
    // Each layer keeps a second, idle renderer for the next preset. Creating an OpenGL context and
    // projectM instance stalls the driver for 0.3-0.5 s, which froze the playing layer, so the pair
    // is created once (while the first preset loads, before anything is on screen) and reused.
    private Renderer? _spare;
    private int _rendererCount;
    private string? _loaded;
    private string? _requested;
    private int _loadId;
    private double? _fadeStart;
    private double? _prerollUntil;
    private string? _prerolling;
    private string? _inPlaceRequested;
    internal long InPlaceSwitches { get; private set; }
    private double _fadeDuration;
    private double? _lastSceneTime;
    private double _time;
    private double _lastRenderedTime = -1;
    private int _lastWidth, _lastHeight;
    private int _blendIndex;
    private bool? _offline;
    private bool _fatal;
    private byte[]? _frame;
    public long FrameToken { get; private set; }
    internal bool IsSwitchingPresets => _incoming != null || _outgoing != null || _prerolling != null || _inPlaceRequested != null;
    internal long CrossfadeFrames { get; private set; }
    internal string Phase => _inPlaceRequested != null ? "in-place load" : _requested != null ? "standby load" : _prerolling != null ? "pre-roll" : _outgoing != null ? "crossfade" : "steady";
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
            if (_active == null && _incoming == null && _loaded == null) _time = 0;
            if (!ProjectMLibrary.EnsureReady(offline))
            {
                Status = "Preparing the bundled preset library for first use...";
                return null;
            }
            ThrowIfFatal(_active); ThrowIfFatal(_outgoing);
            _playlist.Tick(_time, audio.BeatCount);
            if (!ResolvePreset(offline, audio.BeatCount))
            {
                Status = "No playable presets. Check the playlist files; press Retry / Restart after fixing them.";
                if (offline) throw new InvalidOperationException(Status);
                return _frame;
            }
            // Allocating a renderer's scene-sized render targets (its first frame at a size) also
            // stalls the driver, so the idle standby renders once whenever the size changes instead
            // of doing it in the middle of the next preset switch.
            if (_spare != null && (_spare.RequestedWidth != width || _spare.RequestedHeight != height))
            {
                _spare.Worker.RequestRender(width, height, _time, _audio);
                _spare.RequestedWidth = width; _spare.RequestedHeight = height;
            }

            if (_active == null)
            {
                if (_requested != null) Status = $"Loading: {_requested}";
                return null;
            }

            if (_lastRenderedTime != _time || width != _lastWidth || height != _lastHeight)
            {
                audio.CopyProjectMPcm(_audio, offline);
                var standby = _prerolling != null ? _incoming : null;
                long activeBefore = _active.Token, outgoingBefore = _outgoing?.Token ?? 0, standbyBefore = standby?.Token ?? 0;
                _active.Worker.RequestRender(width, height, _time, _audio);
                _outgoing?.Worker.RequestRender(width, height, _time, _audio);
                standby?.Worker.RequestRender(width, height, _time, _audio);
                _lastRenderedTime = _time; _lastWidth = width; _lastHeight = height;
                if (offline)
                {
                    WaitForFrame(_active, activeBefore);
                    if (_outgoing != null) WaitForFrame(_outgoing, outgoingBefore);
                    if (standby != null) WaitForFrame(standby, standbyBefore);
                }
            }

            if (_prerolling != null)
            {
                ThrowIfFatal(_incoming);
                Acquire(_incoming!);
                if (_time >= _prerollUntil && _incoming!.Frame != null && _incoming.Width == width && _incoming.Height == height)
                {
                    if (MeanLuminance(_incoming.Frame) >= BlackFrameLuminance) Promote();
                    else SwitchInPlace(offline);
                }
            }

            bool changed = Acquire(_active!);
            if (_outgoing != null) changed |= Acquire(_outgoing);
            if (changed) Compose(width, height);
            ThrowIfFatal(_active); ThrowIfFatal(_outgoing);
            return _frame;
        }
        catch (Exception ex)
        {
            Status = $"projectM unavailable: {ex.Message}";
            Logger.Warn(Status);
            _fatal = true;
            DisposeRenderers();
            if (offline) throw new InvalidOperationException(Status, ex);
            return _frame;
        }
    }

    private static void ThrowIfFatal(Renderer? renderer)
    {
        if (renderer?.Worker.FatalError is string fatal) throw new InvalidOperationException(fatal);
    }

    private static void WaitForFrame(Renderer renderer, long afterToken)
    {
        if (!renderer.Worker.WaitForFrame(afterToken, OfflineTimeout))
            throw new InvalidOperationException(renderer.Worker.FatalError ?? "projectM did not finish a frame in time.");
    }

    private static bool Acquire(Renderer renderer)
    {
        byte[]? pixels = renderer.Worker.AcquireLatest(out int width, out int height, out long token);
        if (token == renderer.Token || pixels == null) return false;
        renderer.Token = token;
        renderer.Frame = pixels;
        renderer.Width = width;
        renderer.Height = height;
        return true;
    }

    private void Compose(int width, int height)
    {
        var active = _active!;
        if (active.Frame == null || active.Width != width || active.Height != height)
        {
            // The new preset has not produced its first frame yet: keep the previous preset moving.
            if (_outgoing?.Frame != null && _outgoing.Width == width && _outgoing.Height == height && _frame != _outgoing.Frame)
            {
                _frame = _outgoing.Frame;
                FrameToken++;
            }
            return;
        }

        if (_outgoing != null)
        {
            _fadeStart ??= _time; // The crossfade starts at the incoming preset's first frame.
            double alpha = _fadeDuration <= 0 ? 1 : Math.Clamp((_time - _fadeStart.Value) / _fadeDuration, 0, 1);
            var from = _outgoing.Frame;
            if (alpha >= 1 || from == null || _outgoing.Width != width || _outgoing.Height != height)
            {
                Retire(_outgoing);
                _outgoing = null;
                _fadeStart = null;
            }
            else
            {
                // Alternate output buffers: the previous frame may still be composited this tick.
                _blendIndex ^= 1;
                var output = _blendBuffers[_blendIndex];
                if (output == null || output.Length != active.Frame.Length) output = _blendBuffers[_blendIndex] = new byte[active.Frame.Length];
                Crossfade(from, active.Frame, output, alpha);
                CrossfadeFrames++;
                _frame = output;
                FrameToken++;
                return;
            }
        }

        _frame = active.Frame;
        FrameToken++;
    }

    // Returns false when every playlist entry has failed.
    private bool ResolvePreset(bool offline, long beatCount)
    {
        if (_inPlaceRequested != null)
        {
            var result = _active!.Worker.TakeLoadResult();
            if (result == null || result.Id != _loadId) return true;
            string candidate = _inPlaceRequested;
            _inPlaceRequested = null;
            if (result.Error == null)
            {
                _loaded = candidate;
                _lastRenderedTime = -1;
            }
            else
            {
                MarkFailed(candidate, result.Error);
                Status = $"Playing: {_loaded}";
                if (AllPresetsFailed) return false;
                _playlist.Move(1, _time, beatCount);
            }
        }

        if (_requested != null)
        {
            var result = _incoming!.Worker.TakeLoadResult();
            if (result == null || result.Id != _loadId)
            {
                ThrowIfFatal(_incoming);
                return true; // Still compiling on the standby renderer; the current preset keeps playing.
            }
            if (!CompleteLoad(result.Error))
            {
                if (AllPresetsFailed) return false;
                _playlist.Move(1, _time, beatCount);
            }
        }

        // Bound retries per frame; a broken preset cannot trap the UI in a loop.
        for (int attempt = 0; attempt < Math.Min(4, _settings.Presets.Count); attempt++)
        {
            string? candidate = _playlist.Current;
            if (candidate == null || candidate == _loaded || candidate == _prerolling || candidate == _inPlaceRequested) break;
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
                    // One change at a time: the next load starts after the current pre-roll and crossfade.
                    if (_outgoing != null || _prerolling != null || _inPlaceRequested != null) return true;
                    StartLoad(candidate);
                    if (!offline) return true;
                    var result = _incoming!.Worker.WaitForLoadResult(_loadId, OfflineTimeout)
                        ?? throw new InvalidOperationException($"projectM did not finish loading '{candidate}' in time.");
                    ThrowIfFatal(_incoming);
                    if (CompleteLoad(result.Error)) break;
                }
            }
            if (AllPresetsFailed) return false;
            _playlist.Move(1, _time, beatCount);
        }
        return true;
    }

    private void StartLoad(string candidate)
    {
        _incoming ??= _spare ?? new Renderer($"LifeViz projectM layer #{++_rendererCount}");
        _spare = null;
        // First load: nothing is on screen yet, so this is when the standby renderer is created.
        if (_active == null) _spare = new Renderer($"LifeViz projectM layer #{++_rendererCount}");
        _requested = candidate;
        Logger.Info($"projectM loading preset '{candidate}'.");
        _incoming.Worker.RequestLoad(++_loadId, candidate, _time, 0, true);
    }

    private bool CompleteLoad(string? error)
    {
        string candidate = _requested!;
        _requested = null;
        if (error != null)
        {
            Retire(_incoming);
            _incoming = null;
            MarkFailed(candidate, error);
            return false;
        }

        if (_active == null)
        {
            // Nothing is playing yet: show the first preset as soon as it renders.
            _prerolling = candidate;
            Promote();
            return true;
        }

        _prerolling = candidate;
        _prerollUntil = _time + PreRollSeconds;
        Status = $"Playing: {candidate}";
        return true;
    }

    private void SwitchInPlace(bool offline)
    {
        string candidate = _prerolling!;
        _prerolling = null;
        _prerollUntil = null;
        Retire(_incoming);
        _incoming = null;
        InPlaceSwitches++;
        Logger.Info($"projectM preset '{candidate}' only warps the previous image; switching in place with projectM's transition.");
        _inPlaceRequested = candidate;
        _active!.Worker.RequestLoad(++_loadId, candidate, _time, _settings.TransitionSeconds, false);
        if (!offline) return;
        var result = _active.Worker.WaitForLoadResult(_loadId, OfflineTimeout)
            ?? throw new InvalidOperationException($"projectM did not finish loading '{candidate}' in time.");
        ThrowIfFatal(_active);
        _inPlaceRequested = null;
        if (result.Error == null) { _loaded = candidate; _lastRenderedTime = -1; }
        else { MarkFailed(candidate, result.Error); Status = $"Playing: {_loaded}"; }
    }

    // Sampled mean of R+G+B/3 (0-255) over every 16th pixel.
    internal static double MeanLuminance(byte[] frame)
    {
        long sum = 0;
        int samples = 0;
        for (int i = 0; i + 2 < frame.Length; i += 64)
        {
            sum += frame[i] + frame[i + 1] + frame[i + 2];
            samples++;
        }
        return samples == 0 ? 0 : sum / (3.0 * samples);
    }

    private void Promote()
    {
        string candidate = _prerolling!;
        _prerolling = null;
        _prerollUntil = null;
        var previous = _active;
        _active = _incoming;
        _incoming = null;
        if (previous != null && _settings.TransitionSeconds > 0)
        {
            Retire(_outgoing);
            _outgoing = previous;
            _fadeDuration = _settings.TransitionSeconds;
            _fadeStart = null;
        }
        else
        {
            Retire(previous);
        }
        _loaded = candidate;
        _lastRenderedTime = -1;
        Status = $"Playing: {candidate}";
    }

    private bool AllPresetsFailed => _failedPresets.Count >= _settings.Presets.Count;

    private void MarkFailed(string candidate, string error)
    {
        _failedPresets.Add(candidate);
        Logger.Warn($"projectM skipped '{candidate}': {error}");
    }

    // dest = from + (to - from) * alpha per byte, in 8-bit fixed point.
    internal static void Crossfade(byte[] from, byte[] to, byte[] dest, double alpha)
    {
        ushort a = (ushort)Math.Clamp((int)Math.Round(alpha * 256), 0, 256);
        ushort ia = (ushort)(256 - a);
        var va = new Vector<ushort>(a);
        var via = new Vector<ushort>(ia);
        int i = 0;
        for (; i <= dest.Length - Vector<byte>.Count; i += Vector<byte>.Count)
        {
            Vector.Widen(new Vector<byte>(from, i), out var fromLow, out var fromHigh);
            Vector.Widen(new Vector<byte>(to, i), out var toLow, out var toHigh);
            var low = Vector.ShiftRightLogical(fromLow * via + toLow * va, 8);
            var high = Vector.ShiftRightLogical(fromHigh * via + toHigh * va, 8);
            Vector.Narrow(low, high).CopyTo(dest, i);
        }
        for (; i < dest.Length; i++) dest[i] = (byte)((from[i] * ia + to[i] * a) >> 8);
    }

    public void Move(int direction, long beatCount) => _playlist.Move(direction, _time, beatCount);

    // Keep one idle renderer for the next switch; dispose any extra.
    private void Retire(Renderer? renderer)
    {
        if (renderer == null || renderer == _spare) return;
        if (_spare == null && renderer.Worker.FatalError == null) _spare = renderer;
        else renderer.Dispose();
    }

    private void DisposeRenderers()
    {
        _active?.Dispose(); _incoming?.Dispose(); _outgoing?.Dispose(); _spare?.Dispose();
        _active = _incoming = _outgoing = _spare = null;
    }

    public void Reset()
    {
        DisposeRenderers();
        _playlist.Reset(); _failedPresets.Clear(); _loaded = null; _requested = null; _fatal = false;
        _fadeStart = null; _prerolling = null; _prerollUntil = null; _inPlaceRequested = null;
        _time = 0; _lastSceneTime = null; _lastRenderedTime = -1; _frame = null;
        Status = "Preparing projectM...";
    }

    public void Dispose() => DisposeRenderers();
}
