using System;
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private readonly object _sync = new();
        private ID3D11Device1? _device;
        private ID3D11DeviceContext1? _context;
        private readonly ID3D11Texture2D?[] _textures = new ID3D11Texture2D?[SnapshotBufferCount];
        private readonly ID3D11ShaderResourceView?[] _shaderResourceViews = new ID3D11ShaderResourceView?[SnapshotBufferCount];
        private readonly IntPtr[] _sharedHandles = new IntPtr[SnapshotBufferCount];
        private int _width;
        private int _height;
        private int _nextIndex;
        private bool _available;

        private static int _snapshotCount;
        private static ulong _snapshotHandleMask;

        public GpuPresentationSurfaceSnapshotter()
        {
            TryInitialize();
        }

        public bool IsAvailable => _available;

        internal static void ResetSmokeCounters()
        {
            _snapshotCount = 0;
            _snapshotHandleMask = 0;
        }

        internal static (int snapshotCount, int distinctHandleCount) GetSmokeStats()
        {
            return (_snapshotCount, CountBits(_snapshotHandleMask));
        }

        public GpuCompositeSurface? Snapshot(GpuCompositeSurface source)
        {
            if (!_available || source.Width <= 0 || source.Height <= 0)
            {
                return null;
            }

            lock (_sync)
            {
                EnsureResources(source.Width, source.Height);
                int index = _nextIndex;
                _nextIndex = (_nextIndex + 1) % SnapshotBufferCount;

                var texture = _textures[index];
                var shaderResourceView = _shaderResourceViews[index];
                IntPtr sharedHandle = _sharedHandles[index];
                if (_context == null ||
                    texture == null ||
                    shaderResourceView == null ||
                    sharedHandle == IntPtr.Zero)
                {
                    return null;
                }

                _context.CopyResource(texture, source.Texture);
                _context.Flush();

                _snapshotCount++;
                _snapshotHandleMask |= 1UL << index;
                return new GpuCompositeSurface(texture, shaderResourceView, sharedHandle, source.Width, source.Height);
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < SnapshotBufferCount; i++)
            {
                _shaderResourceViews[i]?.Dispose();
                _shaderResourceViews[i] = null;
                _textures[i]?.Dispose();
                _textures[i] = null;
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
            _available = false;
        }

        private void TryInitialize()
        {
            try
            {
                var sharedDevice = GpuSharedDevice.GetOrCreate();
                _device = sharedDevice.Device;
                _context = sharedDevice.Context;
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
                if (_sharedHandles[i] != IntPtr.Zero)
                {
                    CloseHandle(_sharedHandles[i]);
                    _sharedHandles[i] = IntPtr.Zero;
                }
            }

            _width = width;
            _height = height;
            _nextIndex = 0;

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
