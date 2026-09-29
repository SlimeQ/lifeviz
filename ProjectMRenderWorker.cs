using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace lifeviz;

// Owns one ProjectMRenderer on a dedicated thread. projectM preset loads (shader compiles,
// 0.5-2 s) and 1080p frames (~15 ms of GPU wait + readback) used to run on the WPF UI thread,
// which left almost no time for input and made LifeViz go "Not Responding". The UI thread now
// only posts requests and picks up the newest finished frame.
//
// Requests are latest-wins: a slow frame drops intermediate requests instead of queueing them.
// Finished frames are triple-buffered, so the buffer the UI acquired is never rewritten while
// it may still be composited, and a newer frame can be written meanwhile.
internal sealed class ProjectMRenderWorker : IDisposable
{
    internal sealed record LoadResult(int Id, string? Error);

    private sealed record LoadRequest(int Id, string Path, double Time, double TransitionSeconds, bool First);
    private sealed record RenderRequest(int Width, int Height, double Time, float[] Audio);

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly byte[]?[] _buffers = new byte[]?[3];
    private readonly int[] _widths = new int[3], _heights = new int[3];
    private LoadRequest? _pendingLoad;
    private RenderRequest? _pendingRender;
    private LoadResult? _loadResult;
    private int _published = -1, _acquired = -1;
    private long _publishedToken;
    private bool _disposed;
    private int _busy;

    public ProjectMRenderWorker(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Set when the renderer could not be created or failed irrecoverably.</summary>
    public string? FatalError
    {
        get => Volatile.Read(ref _fatalError);
        private set => Volatile.Write(ref _fatalError, value);
    }

    private string? _fatalError;

    /// <summary>No queued request and nothing in progress.</summary>
    public bool IsIdle
    {
        get { lock (_gate) return _pendingLoad == null && _pendingRender == null && _busy == 0; }
    }

    public void RequestLoad(int id, string path, double time, double transitionSeconds, bool first)
    {
        lock (_gate)
        {
            _pendingLoad = new LoadRequest(id, path, time, transitionSeconds, first);
            _loadResult = null;
        }
        _wake.Set();
    }

    public LoadResult? TakeLoadResult()
    {
        lock (_gate)
        {
            var result = _loadResult;
            _loadResult = null;
            return result;
        }
    }

    public void RequestRender(int width, int height, double time, float[] audio)
    {
        var copy = (float[])audio.Clone();
        lock (_gate) _pendingRender = new RenderRequest(width, height, time, copy);
        _wake.Set();
    }

    /// <summary>Returns the newest finished frame; it stays untouched until the next call.</summary>
    public byte[]? AcquireLatest(out int width, out int height, out long token)
    {
        lock (_gate)
        {
            token = _publishedToken;
            if (_published < 0)
            {
                width = height = 0;
                return null;
            }

            _acquired = _published;
            width = _widths[_acquired];
            height = _heights[_acquired];
            return _buffers[_acquired];
        }
    }

    /// <summary>Blocks until a load result arrives (offline/bake use only).</summary>
    public LoadResult? WaitForLoadResult(int id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_loadResult?.Id == id) return TakeLoadResultLocked();
                if (FatalError != null) return new LoadResult(id, FatalError);
            }
            Thread.Sleep(1);
        }
        return null;

        LoadResult? TakeLoadResultLocked()
        {
            var result = _loadResult;
            _loadResult = null;
            return result;
        }
    }

    /// <summary>Blocks until a frame newer than <paramref name="afterToken"/> is published (offline/bake use only).</summary>
    public bool WaitForFrame(long afterToken, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_publishedToken > afterToken) return true;
                if (FatalError != null) return false;
            }
            Thread.Sleep(1);
        }
        return false;
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _wake.Set();
    }

    /// <summary>Waits for the thread to release its native resources (tests and shutdown).</summary>
    public bool WaitForExit(TimeSpan timeout) => _thread.Join(timeout);

    private void Run()
    {
        ProjectMRenderer? renderer = null;
        try
        {
            renderer = new ProjectMRenderer();
            while (true)
            {
                PumpMessages();
                LoadRequest? load;
                RenderRequest? render;
                lock (_gate)
                {
                    if (_disposed) break;
                    load = _pendingLoad;
                    render = _pendingRender;
                    _pendingLoad = null;
                    _pendingRender = null;
                    if (load != null || render != null) _busy = 1;
                }

                if (load == null && render == null)
                {
                    WaitForWork();
                    continue;
                }

                try
                {
                    if (load != null)
                    {
                        string? error;
                        long loadStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        try { error = renderer.Load(load.Path, load.Time, load.TransitionSeconds, load.First); }
                        catch (Exception ex) when (ex is System.IO.IOException or ArgumentException or UnauthorizedAccessException) { error = ex.Message; }
                        lock (_gate) _loadResult = new LoadResult(load.Id, error);
                        LogIfSlow("preset load", loadStart, 1000);
                    }

                    if (render != null)
                    {
                        long renderStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        Publish(renderer.Render(render.Width, render.Height, render.Time, render.Audio), render.Width, render.Height);
                        LogIfSlow($"{render.Width}x{render.Height} frame", renderStart, 200);
                    }
                }
                finally
                {
                    lock (_gate) _busy = 0;
                }
            }
        }
        catch (Exception ex)
        {
            FatalError = ex.Message;
            Logger.Warn($"projectM render thread stopped: {ex.Message}");
        }
        finally
        {
            try { renderer?.Dispose(); }
            catch (Exception ex) { Logger.Warn($"projectM renderer cleanup failed: {ex.Message}"); }
            // _wake is deliberately not disposed: the UI thread may still signal it after exit.
        }
    }

    // Slow native calls are the usual cause of a stuttering MilkDrop layer; make them visible.
    // Rate-limited to one line per 10 s per renderer so a very heavy preset cannot flood the log.
    private void LogIfSlow(string operation, long startTimestamp, double thresholdMilliseconds)
    {
        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        if (elapsed < thresholdMilliseconds) return;
        if (_lastSlowLog != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastSlowLog).TotalSeconds < 10)
        {
            _suppressedSlowLogs++;
            return;
        }
        string more = _suppressedSlowLogs > 0 ? $" ({_suppressedSlowLogs} more slow operations in the previous 10 s)" : "";
        Logger.Info($"{_thread.Name}: {operation} took {elapsed:F0} ms{more}.");
        _lastSlowLog = System.Diagnostics.Stopwatch.GetTimestamp();
        _suppressedSlowLogs = 0;
    }

    private long _lastSlowLog;
    private int _suppressedSlowLogs;

    private void Publish(byte[] pixels, int width, int height)
    {
        int target;
        lock (_gate)
        {
            target = 0;
            while (target == _published || target == _acquired) target++;
        }

        // The target slot is neither published nor held by the UI, so it is safe to write unlocked.
        byte[]? buffer = _buffers[target];
        if (buffer == null || buffer.Length != pixels.Length) buffer = new byte[pixels.Length];
        Buffer.BlockCopy(pixels, 0, buffer, 0, pixels.Length);
        lock (_gate)
        {
            _buffers[target] = buffer;
            _widths[target] = width;
            _heights[target] = height;
            _published = target;
            _publishedToken++;
        }
    }

    // The hidden GL window belongs to this thread; keep its queue drained so system broadcasts
    // sent to top-level windows never wait on it.
    private void WaitForWork()
    {
        IntPtr handle = _wake.SafeWaitHandle.DangerousGetHandle();
        MsgWaitForMultipleObjects(1, new[] { handle }, false, 250, QsAllInput);
    }

    private static void PumpMessages()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private const uint QsAllInput = 0x04FF;
    private const uint PmRemove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds, uint wakeMask);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Msg message, IntPtr window, uint filterMin, uint filterMax, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg message);
}
