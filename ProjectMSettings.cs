using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace lifeviz;

// Shared by autosaves, exported scenes, editor drafts and bake snapshots.
internal sealed class ProjectMSettings
{
    public List<string> Presets { get; set; } = new();
    public string Order { get; set; } = "Shuffle";
    public string Advance { get; set; } = "Timed";
    public double DurationSeconds { get; set; } = 30;
    public double TransitionSeconds { get; set; } = 3;
    public int BeatsPerPreset { get; set; } = 16;
    public double MinimumSeconds { get; set; } = 5;
    public int ShuffleSeed { get; set; } = 1;

    public ProjectMSettings Clone() => new()
    {
        Presets = (Presets ?? new()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Order = Order == "Ordered" ? "Ordered" : "Shuffle",
        Advance = Advance is "Hold" or "Beats" or "TimedOnBeat" ? Advance : "Timed",
        DurationSeconds = Clamp(DurationSeconds, 0.1, 86400, 30),
        TransitionSeconds = Clamp(TransitionSeconds, 0, 30, 3),
        BeatsPerPreset = Math.Clamp(BeatsPerPreset, 1, 4096),
        MinimumSeconds = Clamp(MinimumSeconds, 0, 86400, 5),
        ShuffleSeed = ShuffleSeed
    };

    private static double Clamp(double n, double min, double max, double fallback) => double.IsFinite(n) ? Math.Clamp(n, min, max) : fallback;
}

internal static class ProjectMLibrary
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "projectm");
    private const string AssetVersion = "sdl-2.0.0-pre1-7129cae0-v1";
    private static readonly Lazy<string> ExtractedRoot = new(ExtractAssets);
    private static readonly Lazy<Task<string>> Extraction = new(() => Task.Run(() => ExtractedRoot.Value));
    public static string PresetRoot => Path.Combine(ExtractedRoot.Value, "presets");
    public static string TextureRoot => Path.Combine(ExtractedRoot.Value, "textures");
    private static readonly Lazy<string[]> Library = new(() =>
    {
        using var archive = ZipFile.OpenRead(Path.Combine(Root, "assets.zip"));
        return archive.Entries.Where(e => e.FullName.StartsWith("presets/") && e.FullName.EndsWith(".milk", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.FullName[8..]).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    });
    public static IReadOnlyList<string> Presets => Library.Value;

    public static bool EnsureReady(bool wait)
    {
        Task<string> task = Extraction.Value;
        if (!wait && !task.IsCompleted) return false;
        task.GetAwaiter().GetResult();
        return true;
    }

    private static string ExtractAssets()
    {
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lifeviz", "projectm", AssetVersion);
        using var mutex = new Mutex(false, "Local\\LifeViz-ProjectM-Assets");
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromMinutes(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Timed out preparing the projectM preset library.");
            string ready = Path.Combine(cache, ".complete");
            if (File.Exists(ready)) return cache;
            Directory.CreateDirectory(cache);
            using var archive = ZipFile.OpenRead(Path.Combine(Root, "assets.zip"));
            foreach (var entry in archive.Entries)
            {
                if (entry.Name.Length == 0) continue;
                string destination = Path.GetFullPath(Path.Combine(cache, entry.FullName));
                if (!destination.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid preset archive path.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
            File.WriteAllText(ready, AssetVersion);
            return cache;
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    public static string Resolve(string path)
    {
        if (Path.IsPathFullyQualified(path)) return path;
        string full = Path.GetFullPath(Path.Combine(PresetRoot, path));
        if (!full.StartsWith(Path.GetFullPath(PresetRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bundled preset path escapes the preset library.");
        return full;
    }

    public static ProjectMSettings Defaults()
    {
        try { return new() { Presets = Presets.Where(p => p.Contains("Waveform/Spectrum/", StringComparison.OrdinalIgnoreCase)).Take(3).ToList() }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Bundled projectM presets are unavailable: {ex.Message}");
            return new();
        }
    }
}

// No wall clock or rendering dependency: live playback and offline baking share this scheduler.
internal sealed class ProjectMPlaylist
{
    private ProjectMSettings _settings = new();
    private Random _random = new(1);
    private readonly List<string> _bag = new();
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private double _changedAt;
    private long _lastBeat;
    private long _beats;
    public string? Current { get; private set; }

    public void Configure(ProjectMSettings settings)
    {
        var next = settings.Clone();
        bool sequenceChanged = !_settings.Presets.SequenceEqual(next.Presets, StringComparer.OrdinalIgnoreCase)
            || _settings.Order != next.Order || _settings.ShuffleSeed != next.ShuffleSeed;
        _settings = next;
        if (sequenceChanged)
        {
            _random = new Random(next.ShuffleSeed);
            _bag.Clear();
            _history.Clear();
            _historyIndex = -1;
            if (Current != null && next.Presets.Contains(Current, StringComparer.OrdinalIgnoreCase))
            {
                _history.Add(Current);
                _historyIndex = 0;
            }
            else Current = null;
        }
    }

    public bool Tick(double time, long beatCount)
    {
        if (time < _changedAt) Reset();
        long delta = beatCount >= _lastBeat ? beatCount - _lastBeat : 0;
        _lastBeat = beatCount;
        if (Current == null) return Move(1, time, beatCount);
        _beats += delta;
        double elapsed = time - _changedAt;
        bool due = _settings.Advance switch
        {
            "Timed" => elapsed >= _settings.DurationSeconds,
            "Beats" => delta > 0 && _beats >= _settings.BeatsPerPreset && elapsed >= _settings.MinimumSeconds,
            "TimedOnBeat" => delta > 0 && elapsed >= Math.Max(_settings.DurationSeconds, _settings.MinimumSeconds),
            _ => false
        };
        return due && elapsed >= _settings.TransitionSeconds && Move(1, time, beatCount);
    }

    public bool Move(int direction, double time, long beatCount)
    {
        if (_settings.Presets.Count == 0) { Current = null; return false; }
        if (direction < 0 && Current != null && _historyIndex <= 0) return false;
        string? previous = Current;
        if (direction < 0 && _historyIndex > 0) Current = _history[--_historyIndex];
        else if (direction > 0 && _historyIndex + 1 < _history.Count) Current = _history[++_historyIndex];
        else
        {
            if (_settings.Order == "Ordered")
            {
                int index = Current == null ? (direction < 0 ? 0 : -1) : _settings.Presets.FindIndex(p => string.Equals(p, Current, StringComparison.OrdinalIgnoreCase));
                Current = _settings.Presets[(index + direction + _settings.Presets.Count) % _settings.Presets.Count];
            }
            else
            {
                if (_bag.Count == 0)
                {
                    _bag.AddRange(_settings.Presets);
                    for (int i = _bag.Count - 1; i > 0; i--)
                    {
                        int j = _random.Next(i + 1);
                        (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
                    }
                    // Preserve the no-repeat guarantee across cycle boundaries.
                    if (_bag.Count > 1 && _bag[0] == Current) (_bag[0], _bag[1]) = (_bag[1], _bag[0]);
                }
                Current = _bag[0];
                _bag.RemoveAt(0);
            }
            _history.Add(Current);
            if (_history.Count > 256) _history.RemoveAt(0);
            _historyIndex = _history.Count - 1;
        }
        _changedAt = time;
        _lastBeat = beatCount;
        _beats = 0;
        return previous != Current;
    }

    public void Reset()
    {
        Current = null; _changedAt = 0; _lastBeat = 0; _beats = 0;
        _bag.Clear(); _history.Clear(); _historyIndex = -1;
        _random = new Random(_settings.ShuffleSeed);
    }
}
