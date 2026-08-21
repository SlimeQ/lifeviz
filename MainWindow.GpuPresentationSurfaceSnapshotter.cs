using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace lifeviz;

public partial class MainWindow
{
    private sealed class GpuPresentationSurfaceSnapshotter : IDisposable
    {
        private const int SnapshotBufferCount = 3;
        private static readonly TimeSpan CopyCompletionTimeout = TimeSpan.FromSeconds(2);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private readonly object _sync = new();
        private ID3D11Device1? _device;
        private ID3D11DeviceContext1? _context;
        private object? _deviceSync;
        private readonly ID3D11Texture2D?[] _textures = new ID3D11Texture2D?[SnapshotBufferCount];
        private readonly ID3D11ShaderResourceView?[] _shaderResourceViews = new ID3D11ShaderResourceView?[SnapshotBufferCount];
        private readonly IntPtr[] _sharedHandles = new IntPtr[SnapshotBufferCount];
        private readonly ID3D11Query?[] _copyQueries = new ID3D11Query?[SnapshotBufferCount];
        private readonly bool[] _leasedSlots = new bool[SnapshotBufferCount];
        private int _width;
        private int _height;
        private int _nextIndex;
        private int _pendingCopyIndex = -1;
        private long _pendingCopyTimestamp;
        private int _resourceGeneration;
        private bool _available;

        private static int _snapshotCount;
        private static int _heldSnapshotCount;
        private static int _notReadyCount;
        private static int _forcedNotReadyChecks;
        private static ulong _snapshotHandleMask;

        public GpuPresentationSurfaceSnapshotter()
        {
            TryInitialize();
        }

        public bool IsAvailable => _available;

        internal static void ResetSmokeCounters()
        {
            _snapshotCount = 0;
            _heldSnapshotCount = 0;
            _notReadyCount = 0;
            _forcedNotReadyChecks = 0;
            _snapshotHandleMask = 0;
        }

        internal static (int snapshotCount, int distinctHandleCount) GetSmokeStats()
        {
            return (_snapshotCount, CountBits(_snapshotHandleMask));
        }

        internal static (int completedSnapshots, int heldSnapshots, int notReadyChecks) GetSynchronizationSmokeStats()
        {
            return (_snapshotCount, _heldSnapshotCount, _notReadyCount);
        }

        internal static void ForceNotReadyChecksForSmoke(int count)
        {
            _forcedNotReadyChecks = App.IsSmokeTestMode ? Math.Max(0, count) : 0;
        }

        public GpuCompositeSurface? Snapshot(GpuCompositeSurface source)
        {
            if (!_available || source.Width <= 0 || source.Height <= 0)
            {
                return null;
            }

            lock (_sync)
            {
                lock (_deviceSync ?? _sync)
                {
                    EnsureResources(source.Width, source.Height);
                    if (_context == null)
                    {
                        return null;
                    }

                    GpuCompositeSurface? completedSurface = null;

                // A producer query proves that CopyResource completed before a slot
                // is handed to WPF. The one-shot lease then keeps that slot out of
                // the producer's free set until the presentation device reports its
                // own copy complete. Both sides therefore have explicit GPU
                // completion, rather than assuming a three-slot delay is enough.
                    if (_pendingCopyIndex >= 0)
                    {
                        QueryPollResult queryResult = PollQuery(_copyQueries[_pendingCopyIndex]);
                        if (queryResult == QueryPollResult.Pending)
                        {
                            if (_pendingCopyTimestamp > 0 &&
                                Stopwatch.GetElapsedTime(_pendingCopyTimestamp) >= CopyCompletionTimeout)
                            {
                                Logger.Warn("GPU presentation snapshot copy did not complete within two seconds; holding the last presented frame and disabling this synchronization path.");
                                _available = false;
                                _heldSnapshotCount++;
                                return null;
                            }

                            _notReadyCount++;
                            _heldSnapshotCount++;
                            return null;
                        }

                        if (queryResult == QueryPollResult.Failed)
                        {
                            Logger.Warn("GPU presentation snapshot query failed; holding the last presented frame and disabling the unsafe shared-surface path.");
                            _available = false;
                            _heldSnapshotCount++;
                            return null;
                        }

                        int completedIndex = _pendingCopyIndex;
                        _pendingCopyIndex = -1;
                        _pendingCopyTimestamp = 0;
                        completedSurface = CreateLeasedSurface(completedIndex, source.Width, source.Height);
                    }

                    int copyIndex = FindWritableIndex();
                    if (copyIndex >= 0)
                    {
                        var copyTexture = _textures[copyIndex];
                        var copyQuery = _copyQueries[copyIndex];
                        if (copyTexture != null && copyQuery != null)
                        {
                            _context.CopyResource(copyTexture, source.Texture);
                            _context.End(copyQuery);
                            _context.Flush();
                            _pendingCopyIndex = copyIndex;
                            _pendingCopyTimestamp = Stopwatch.GetTimestamp();
                        }
                    }

                    if (completedSurface == null)
                    {
                        _heldSnapshotCount++;
                    }

                    return completedSurface;
                }
            }
        }

        private enum QueryPollResult
        {
            Complete,
            Pending,
            Failed
        }

        private QueryPollResult PollQuery(ID3D11Query? query)
        {
            if (_context == null || query == null)
            {
                return QueryPollResult.Failed;
            }

            if (App.IsSmokeTestMode && _forcedNotReadyChecks > 0)
            {
                _forcedNotReadyChecks--;
                return QueryPollResult.Pending;
            }

            int code = _context.GetData(query, IntPtr.Zero, 0, AsyncGetDataFlags.DoNotFlush).Code;
            return code == 0
                ? QueryPollResult.Complete
                : code > 0
                    ? QueryPollResult.Pending
                    : QueryPollResult.Failed;
        }

        private int FindWritableIndex()
        {
            for (int offset = 0; offset < SnapshotBufferCount; offset++)
            {
                int candidate = (_nextIndex + offset) % SnapshotBufferCount;
                if (!_leasedSlots[candidate] && candidate != _pendingCopyIndex)
                {
                    _nextIndex = (candidate + 1) % SnapshotBufferCount;
                    return candidate;
                }
            }

            return -1;
        }

        private GpuCompositeSurface? CreateLeasedSurface(int index, int width, int height)
        {
            var texture = _textures[index];
            var view = _shaderResourceViews[index];
            IntPtr handle = _sharedHandles[index];
            if (texture == null || view == null || handle == IntPtr.Zero)
            {
                return null;
            }

            _leasedSlots[index] = true;
            int generation = _resourceGeneration;
            var lease = new GpuSurfaceLease(() => RetireSlot(index, generation));
            _snapshotCount++;
            _snapshotHandleMask |= 1UL << index;

            return new GpuCompositeSurface(texture, view, handle, width, height, lease);
        }

        private void RetireSlot(int index, int generation)
        {
            lock (_sync)
            {
                if (generation == _resourceGeneration &&
                    index >= 0 &&
                    index < _leasedSlots.Length)
                {
                    _leasedSlots[index] = false;
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                lock (_deviceSync ?? _sync)
                {
                    for (int i = 0; i < SnapshotBufferCount; i++)
                    {
                        _shaderResourceViews[i]?.Dispose();
                        _shaderResourceViews[i] = null;
                        _textures[i]?.Dispose();
                        _textures[i] = null;
                        _copyQueries[i]?.Dispose();
                        _copyQueries[i] = null;
                        if (_sharedHandles[i] != IntPtr.Zero)
                        {
                            CloseHandle(_sharedHandles[i]);
                            _sharedHandles[i] = IntPtr.Zero;
                        }
                    }

                    _device = null;
                    _context = null;
                    _width = 0;
                    _height = 0;
                    _nextIndex = 0;
                    _pendingCopyIndex = -1;
                    _pendingCopyTimestamp = 0;
                    _resourceGeneration++;
                    Array.Clear(_leasedSlots);
                    _available = false;
                }

                _deviceSync = null;
            }
        }

        private void TryInitialize()
        {
            try
            {
                var sharedDevice = GpuSharedDevice.GetOrCreate();
                _device = sharedDevice.Device;
                _context = sharedDevice.Context;
                _deviceSync = sharedDevice.SyncRoot;
                _available = true;
                Logger.Info("GPU presentation snapshotter initialized.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"GPU presentation snapshotter unavailable. {ex.Message}");
                _available = false;
                Dispose();
            }
        }

        private void EnsureResources(int width, int height)
        {
            if (_device == null || width <= 0 || height <= 0)
            {
                return;
            }

            if (_width == width &&
                _height == height &&
                _textures.All(texture => texture != null) &&
                _shaderResourceViews.All(view => view != null) &&
                _copyQueries.All(query => query != null) &&
                _sharedHandles.All(handle => handle != IntPtr.Zero))
            {
                return;
            }

            for (int i = 0; i < SnapshotBufferCount; i++)
            {
                _shaderResourceViews[i]?.Dispose();
                _shaderResourceViews[i] = null;
                _textures[i]?.Dispose();
                _textures[i] = null;
                _copyQueries[i]?.Dispose();
                _copyQueries[i] = null;
                if (_sharedHandles[i] != IntPtr.Zero)
                {
                    CloseHandle(_sharedHandles[i]);
                    _sharedHandles[i] = IntPtr.Zero;
                }
            }

            _width = width;
            _height = height;
            _nextIndex = 0;
            _pendingCopyIndex = -1;
            _pendingCopyTimestamp = 0;
            _resourceGeneration++;
            Array.Clear(_leasedSlots);

            var description = new Texture2DDescription(
                Format.B8G8R8A8_UNorm,
                (uint)width,
                (uint)height,
                1,
                1,
                BindFlags.ShaderResource,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                1,
                0,
                ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle);

            for (int i = 0; i < SnapshotBufferCount; i++)
            {
                _textures[i] = _device.CreateTexture2D(description);
                _shaderResourceViews[i] = _device.CreateShaderResourceView(_textures[i]!);
                _copyQueries[i] = _device.CreateQuery(new QueryDescription(QueryType.Event));
                using var resource = _textures[i]!.QueryInterface<IDXGIResource1>();
                _sharedHandles[i] = resource.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read, null);
            }
        }

        private static int CountBits(ulong value)
        {
            int count = 0;
            while (value != 0)
            {
                count += (int)(value & 1UL);
                value >>= 1;
            }

            return count;
        }
    }
}
