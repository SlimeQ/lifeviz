using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace lifeviz;

// Stream Audio Relay: replays what an output-loopback audio source is analyzing from LifeViz's
// own process, so per-application capture (Discord "stream an application", OBS Application Audio
// Capture) carries the music LifeViz reacts to.
//
// The relay plays on a separate "relay device" that the user does not listen to (for example a
// virtual cable). Per-application capture follows the process, not the endpoint, so the stream
// still receives it while the user hears nothing extra and the analyzed device never sees it.
//
// Rather than replaying the device loopback directly, it captures each app that has an audio
// session on the analyzed device and mixes them. That lets it leave out:
//   - LifeViz itself: its own movie audio already reaches the stream directly, and the relay
//     output must never be fed back in.
//   - Discord: replaying the call would echo everyone's voices back to them through the stream.
internal sealed class StreamAudioRelay : IDisposable
{
    private static readonly string[] ExcludedProcessNamePrefixes = { "Discord", "Vesktop" };
    private const int RefreshIntervalMs = 1000;
    private const int RelayLatencyMs = 30;
    private const double PrimeSeconds = 0.020;
    private const double MaxQueueSeconds = 0.120;

    private readonly object _sync = new();
    private readonly object _sourcesLock = new();
    private readonly List<RelaySource> _sources = new();
    private readonly int _ownProcessId = Environment.ProcessId;
    private Timer? _refreshTimer;
    private int _refreshing;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _relayDevice;
    private WasapiOut? _output;
    private string? _relayDeviceId;
    private string _relayDeviceName = string.Empty;
    private string? _monitoredDeviceId; // null = system default output
    private int _sampleRate = 48000;
    private int _outputChannels = 2;
    private volatile bool _muteBecauseSameDevice;
    private volatile string _status = "Off";
    private bool _disposed;

    public string Status => _status;
    public bool IsActive => _output != null;

    /// <summary>Processes currently being relayed (for tests and status).</summary>
    public IReadOnlyList<(int ProcessId, string Name)> RelayedProcesses
    {
        get
        {
            lock (_sourcesLock)
            {
                return _sources.Select(s => (s.ProcessId, s.Name)).ToList();
            }
        }
    }

    /// <summary>Test hook: receives each mixed output block (interleaved, output channel count).</summary>
    internal Action<ReadOnlySpan<float>, int>? OutputTap { get; set; }

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    /// <summary>
    /// Starts (or retargets) the relay. <paramref name="monitoredRenderDeviceId"/> is the output
    /// device being analyzed (null = system default output).
    /// </summary>
    public void Start(string relayDeviceId, string? monitoredRenderDeviceId)
    {
        // Core Audio objects created on an STA thread (the WPF UI thread, smoke tests) would be
        // marshaled back to it from the render and refresh threads, which can deadlock.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            Task.Run(() => Start(relayDeviceId, monitoredRenderDeviceId)).GetAwaiter().GetResult();
            return;
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsSupported)
            {
                StopInternal("Requires Windows 10 build 20348 or Windows 11");
                return;
            }

            _monitoredDeviceId = monitoredRenderDeviceId;
            if (_output != null && string.Equals(_relayDeviceId, relayDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                ScheduleRefresh(0);
                return;
            }

            StopInternal("Off");
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _relayDevice = _enumerator.GetDevice(relayDeviceId);
                if (_relayDevice.State != DeviceState.Active)
                {
                    throw new InvalidOperationException($"Relay device \"{_relayDevice.FriendlyName}\" is not active.");
                }

                var mixFormat = _relayDevice.AudioClient.MixFormat;
                _sampleRate = mixFormat.SampleRate;
                _outputChannels = Math.Max(1, mixFormat.Channels);
                _relayDeviceId = relayDeviceId;
                _relayDeviceName = _relayDevice.FriendlyName;
                _output = new WasapiOut(_relayDevice, AudioClientShareMode.Shared, useEventSync: true, RelayLatencyMs);
                _output.Init(new RelayMixProvider(this, WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _outputChannels)));
                _output.PlaybackStopped += (_, e) =>
                {
                    if (e.Exception != null)
                    {
                        _status = $"Stopped: relay device error ({e.Exception.Message}); pick the relay device again";
                        Logger.Warn($"Stream audio relay output stopped: {e.Exception.Message}");
                    }
                };
                _output.Play();
                _status = $"Relaying to {_relayDevice.FriendlyName}";
                Logger.Info($"Stream audio relay started on \"{_relayDevice.FriendlyName}\" ({_sampleRate} Hz, {_outputChannels} ch).");
                _refreshTimer = new Timer(_ => RefreshSources(), null, 0, RefreshIntervalMs);
            }
            catch (Exception ex)
            {
                Logger.Error("Stream audio relay failed to start", ex);
                StopInternal($"Could not start: {ex.Message}");
            }
        }
    }

    public void Stop(string reason = "Off")
    {
        lock (_sync)
        {
            StopInternal(reason);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            StopInternal("Off");
            _disposed = true;
        }
    }

    internal static bool IsExcludedProcessName(string processName) =>
        ExcludedProcessNamePrefixes.Any(prefix => processName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private void StopInternal(string reason)
    {
        var timer = _refreshTimer;
        _refreshTimer = null;
        if (timer != null)
        {
            using var stopped = new ManualResetEvent(false);
            if (timer.Dispose(stopped))
            {
                stopped.WaitOne(TimeSpan.FromSeconds(3));
            }
        }

        var output = _output;
        _output = null;
        if (output != null)
        {
            try
            {
                output.Stop();
            }
            catch
            {
                // Device removal can make Stop throw; disposal still releases it.
            }

            output.Dispose();
            Logger.Info("Stream audio relay stopped.");
        }

        lock (_sourcesLock)
        {
            foreach (var source in _sources)
            {
                source.Dispose();
            }

            _sources.Clear();
        }

        _relayDevice?.Dispose();
        _relayDevice = null;
        _enumerator?.Dispose();
        _enumerator = null;
        _relayDeviceId = null;
        _muteBecauseSameDevice = false;
        _status = reason;
    }

    private void ScheduleRefresh(int dueMs)
    {
        _refreshTimer?.Change(dueMs, RefreshIntervalMs);
    }

    private void RefreshSources()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            string? relayDeviceId;
            string? monitoredDeviceId;
            string relayName;
            // TryEnter: Stop holds _sync while it waits for this callback to finish.
            if (!Monitor.TryEnter(_sync))
            {
                return;
            }

            try
            {
                if (_output == null || _relayDevice == null)
                {
                    return;
                }

                relayDeviceId = _relayDeviceId;
                monitoredDeviceId = _monitoredDeviceId;
                relayName = _relayDeviceName;
            }
            finally
            {
                Monitor.Exit(_sync);
            }

            using var enumerator = new MMDeviceEnumerator();
            using var monitored = monitoredDeviceId == null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(monitoredDeviceId);
            bool sameDevice = string.Equals(monitored.ID, relayDeviceId, StringComparison.OrdinalIgnoreCase);
            _muteBecauseSameDevice = sameDevice;
            if (sameDevice)
            {
                // Playing the relay where it is analyzed would double everything the user hears.
                _status = $"Paused: \"{relayName}\" is the device being analyzed; pick another relay device";
                SyncSources(Array.Empty<(int, string)>());
                return;
            }

            var wanted = FindRelayProcesses(monitored);
            SyncSources(wanted);
            int count;
            lock (_sourcesLock)
            {
                count = _sources.Count;
            }

            _status = $"Relaying {count} app{(count == 1 ? "" : "s")} from \"{monitored.FriendlyName}\" to \"{relayName}\"";
        }
        catch (Exception ex)
        {
            _status = $"Error: {ex.Message}";
            Logger.Warn($"Stream audio relay refresh failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private List<(int ProcessId, string Name)> FindRelayProcesses(MMDevice monitored)
    {
        var candidates = new Dictionary<int, string>();
        var sessionManager = monitored.AudioSessionManager;
        sessionManager.RefreshSessions();
        var sessions = sessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired)
            {
                continue;
            }

            int processId = (int)session.GetProcessID;
            if (processId <= 0 || processId == _ownProcessId || candidates.ContainsKey(processId))
            {
                continue;
            }

            string? name = TryGetProcessName(processId);
            if (name == null || IsExcludedProcessName(name))
            {
                continue;
            }

            candidates[processId] = name;
        }

        // Capture uses the whole process tree, so drop a process whose ancestor is already
        // relayed (e.g. a browser and its audio service) to avoid summing it twice.
        var parents = ProcessTree.GetParentMap();
        return candidates
            .Where(entry => !HasAncestorIn(entry.Key, candidates, parents))
            .Select(entry => (entry.Key, entry.Value))
            .ToList();
    }

    private static bool HasAncestorIn(int processId, Dictionary<int, string> candidates, Dictionary<int, int> parents)
    {
        var seen = new HashSet<int> { processId };
        int current = processId;
        while (parents.TryGetValue(current, out int parent) && parent > 0 && seen.Add(parent))
        {
            if (candidates.ContainsKey(parent))
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    private static string? TryGetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private void SyncSources(IReadOnlyCollection<(int ProcessId, string Name)> wanted)
    {
        var wantedIds = wanted.Select(w => w.ProcessId).ToHashSet();
        List<RelaySource> removed;
        HashSet<int> existing;
        lock (_sourcesLock)
        {
            removed = _sources.Where(s => !wantedIds.Contains(s.ProcessId)).ToList();
            _sources.RemoveAll(s => !wantedIds.Contains(s.ProcessId));
            existing = _sources.Select(s => s.ProcessId).ToHashSet();
        }

        foreach (var source in removed)
        {
            source.Dispose();
        }

        foreach (var (processId, name) in wanted)
        {
            if (existing.Contains(processId))
            {
                continue;
            }

            try
            {
                var capture = ProcessLoopbackCapture.StartAsync(processId, includeProcessTree: true, _sampleRate)
                    .GetAwaiter().GetResult();
                var source = new RelaySource(processId, name, capture, _sampleRate);
                lock (_sourcesLock)
                {
                    if (_output == null)
                    {
                        source.Dispose();
                        return;
                    }

                    _sources.Add(source);
                }

                Logger.Info($"Stream audio relay: capturing {name} ({processId}).");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Stream audio relay could not capture {name} ({processId}): {ex.Message}");
            }
        }
    }

    // Pulled by the relay device's render thread. Drains every per-app capture into its own
    // queue, then sums what each queue has. A queue starts contributing once it holds 20 ms (so
    // capture/render packet jitter does not cause dropouts) and is trimmed to 120 ms so latency
    // cannot build up.
    private int Mix(float[] buffer, int offset, int count)
    {
        // NAudio passes a byte[] reinterpreted as float[] here; Array.Clear would use the runtime
        // element size (1 byte) and clear only a quarter of the block, so clear through a span.
        buffer.AsSpan(offset, count).Clear();
        int channels = _outputChannels;
        int frames = count / channels;
        if (frames <= 0 || _muteBecauseSameDevice)
        {
            return count;
        }

        int primeFrames = (int)(_sampleRate * PrimeSeconds);
        int maxFrames = (int)(_sampleRate * MaxQueueSeconds);
        lock (_sourcesLock)
        {
            foreach (var source in _sources)
            {
                source.MixInto(buffer, offset, frames, channels, primeFrames, maxFrames);
            }
        }

        for (int i = offset; i < offset + count; i++)
        {
            buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
        }

        OutputTap?.Invoke(buffer.AsSpan(offset, count), channels);
        return count;
    }

    private sealed class RelayMixProvider : ISampleProvider
    {
        private readonly StreamAudioRelay _owner;

        public RelayMixProvider(StreamAudioRelay owner, WaveFormat format)
        {
            _owner = owner;
            WaveFormat = format;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count) => _owner.Mix(buffer, offset, count);
    }

    private sealed class RelaySource : IDisposable
    {
        private readonly ProcessLoopbackCapture _capture;
        private readonly Action<ReadOnlySpan<float>> _enqueue;
        private readonly float[] _queue; // interleaved stereo ring
        private int _readFrame;
        private int _queuedFrames;
        private bool _primed;

        public RelaySource(int processId, string name, ProcessLoopbackCapture capture, int sampleRate)
        {
            ProcessId = processId;
            Name = name;
            _capture = capture;
            _enqueue = Enqueue;
            _queue = new float[(int)(sampleRate * MaxQueueSeconds * 2) * ProcessLoopbackCapture.Channels];
        }

        public int ProcessId { get; }
        public string Name { get; }

        public void MixInto(float[] buffer, int offset, int frames, int outputChannels, int primeFrames, int maxFrames)
        {
            try
            {
                _capture.Read(_enqueue);
            }
            catch
            {
                // The process exited between refreshes; the next refresh removes this source.
            }

            int capacityFrames = _queue.Length / 2;
            maxFrames = Math.Min(maxFrames, capacityFrames);
            if (_queuedFrames > maxFrames)
            {
                Skip(_queuedFrames - maxFrames);
            }

            if (!_primed)
            {
                if (_queuedFrames < Math.Min(primeFrames + frames, maxFrames))
                {
                    return;
                }

                _primed = true;
            }

            int take = Math.Min(frames, _queuedFrames);
            for (int frame = 0; frame < take; frame++)
            {
                int q = ((_readFrame + frame) % capacityFrames) * 2;
                int o = offset + frame * outputChannels;
                if (outputChannels == 1)
                {
                    buffer[o] += (_queue[q] + _queue[q + 1]) * 0.5f;
                }
                else
                {
                    buffer[o] += _queue[q];
                    buffer[o + 1] += _queue[q + 1];
                }
            }

            Skip(take);
            if (_queuedFrames == 0)
            {
                _primed = false;
            }
        }

        private void Enqueue(ReadOnlySpan<float> interleaved)
        {
            int capacityFrames = _queue.Length / 2;
            int frames = interleaved.Length / 2;
            if (frames >= capacityFrames)
            {
                interleaved = interleaved[^(capacityFrames * 2)..];
                frames = capacityFrames;
                _readFrame = 0;
                _queuedFrames = 0;
            }
            else if (_queuedFrames + frames > capacityFrames)
            {
                Skip(_queuedFrames + frames - capacityFrames);
            }

            int writeFrame = (_readFrame + _queuedFrames) % capacityFrames;
            for (int frame = 0; frame < frames; frame++)
            {
                int q = ((writeFrame + frame) % capacityFrames) * 2;
                _queue[q] = interleaved[frame * 2];
                _queue[q + 1] = interleaved[frame * 2 + 1];
            }

            _queuedFrames += frames;
        }

        private void Skip(int frames)
        {
            int capacityFrames = _queue.Length / 2;
            frames = Math.Min(frames, _queuedFrames);
            _readFrame = (_readFrame + frames) % capacityFrames;
            _queuedFrames -= frames;
        }

        public void Dispose() => _capture.Dispose();
    }

    private static class ProcessTree
    {
        private const uint Th32csSnapProcess = 0x00000002;

        public static Dictionary<int, int> GetParentMap()
        {
            var map = new Dictionary<int, int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return map;
            }

            try
            {
                var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
                if (!Process32First(snapshot, ref entry))
                {
                    return map;
                }

                do
                {
                    map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                }
                while (Process32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return map;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
