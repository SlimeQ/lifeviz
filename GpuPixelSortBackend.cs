using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace lifeviz;

internal sealed class GpuPixelSortBackend : IGpuSimulationSurfaceBackend
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelSortParameters
    {
        public uint Width;
        public uint Height;
        public uint CellWidth;
        public uint CellHeight;
        public uint PassParity;
        public uint SortAxis;
        public uint Padding0;
        public uint Padding1;
    }

    private object _sync = new();
    private GpuSharedDevice? _sharedDevice;
    private ID3D11Device1? _device;
    private ID3D11DeviceContext1? _context;
    private ID3D11ComputeShader? _injectCompositeShader;
    private ID3D11ComputeShader? _sortPassShader;
    private ID3D11ComputeShader? _publishOutputShader;
    private ID3D11ComputeShader? _publishPresentationShader;
    private ID3D11Buffer? _parameterBuffer;

    private ID3D11Texture2D? _snapshotTexture;
    private ID3D11ShaderResourceView? _snapshotSrv;
    private ID3D11UnorderedAccessView? _snapshotUav;

    private ID3D11Texture2D? _workTextureA;
    private ID3D11Texture2D? _workTextureB;
    private ID3D11ShaderResourceView? _workSrvA;
    private ID3D11ShaderResourceView? _workSrvB;
    private ID3D11UnorderedAccessView? _workUavA;
    private ID3D11UnorderedAccessView? _workUavB;

    private ID3D11Texture2D? _publishedTexture;
    private ID3D11ShaderResourceView? _publishedSrv;
    private ID3D11UnorderedAccessView? _publishedUav;
    private IntPtr _publishedSharedHandle;
    private ID3D11Texture2D? _presentationTexture;
    private ID3D11ShaderResourceView? _presentationSrv;
    private ID3D11UnorderedAccessView? _presentationUav;
    private ID3D11Texture2D? _stagingTexture;

    private int _columns = 256;
    private int _rows = 144;
    private int _depth = 24;
    private int _cellWidth = 12;
    private int _cellHeight = 8;
    private double _aspectRatio = 16d / 9d;
    private bool _workAIsSource = true;
    private bool _hasSnapshot;
    private bool _publishedTextureDirty = true;
    private GameOfLifeEngine.LifeMode _mode = GameOfLifeEngine.LifeMode.NaiveGrayscale;
    private GameOfLifeEngine.BinningMode _binningMode = GameOfLifeEngine.BinningMode.Fill;
    private GameOfLifeEngine.InjectionMode _injectionMode = GameOfLifeEngine.InjectionMode.Threshold;

    public GpuPixelSortBackend()
    {
        InitializeGpu();
    }

    public int Columns => _columns;
    public int Rows => _rows;
    public int Depth => _depth;
    public double AspectRatio => _aspectRatio;
    public GameOfLifeEngine.LifeMode Mode => _mode;
    public GameOfLifeEngine.BinningMode BinMode => _binningMode;
    public int RDepth => 8;
    public int GDepth => 8;
    public int BDepth => 8;
    public GameOfLifeEngine.InjectionMode InjectMode => _injectionMode;

    public void Configure(int requestedRows, int requestedDepth, double? aspectRatio = null)
    {
        _rows = Math.Clamp(requestedRows, 72, 2160);
        if (aspectRatio.HasValue && aspectRatio.Value > 0.01)
        {
            _aspectRatio = aspectRatio.Value;
        }

        _depth = Math.Clamp(requestedDepth, 3, 96);
        _columns = Math.Clamp((int)Math.Round(_rows * _aspectRatio), 32, 4096);
        _rows = Math.Max(72, Math.Min(2160, (int)Math.Round(_columns / _aspectRatio)));

        lock (_sync)
        {
            EnsureResources();
            ClearWorkingTextures();
        }
    }

    public void SetCellSize(int cellWidth, int cellHeight)
    {
        _cellWidth = Math.Clamp(cellWidth, 1, 4096);
        _cellHeight = Math.Clamp(cellHeight, 1, 4096);
    }

    public void SetBinningMode(GameOfLifeEngine.BinningMode mode)
    {
        _binningMode = mode;
    }

    public void SetInjectionMode(GameOfLifeEngine.InjectionMode mode)
    {
        _injectionMode = mode;
    }

    public void SetMode(GameOfLifeEngine.LifeMode mode)
    {
        _mode = mode;
    }

    public void Randomize()
    {
        lock (_sync)
        {
            EnsureResources();
            ClearWorkingTextures();
            _hasSnapshot = false;
        }
    }

    public void Step()
    {
        lock (_sync)
        {
            EnsureResources();
            if (_context == null || _sortPassShader == null || _snapshotTexture == null || _workTextureA == null)
            {
                return;
            }

            if (!_hasSnapshot)
            {
                return;
            }

            _context.CopyResource(_workTextureA, _snapshotTexture);
            _workAIsSource = true;
            DispatchSortPass(passParity: 0, sortAxis: 0);
            SwapWorkTextures();

            _publishedTextureDirty = true;
        }
    }

    public void FillColorBuffer(byte[] targetBuffer)
    {
        lock (_sync)
        {
            if (!EnsurePublishedTexture() || _context == null || _stagingTexture == null || _publishedTexture == null)
            {
                return;
            }

            _context.CopyResource(_stagingTexture, _publishedTexture);
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowSize = _columns * 4;
                for (int row = 0; row < _rows; row++)
                {
                    IntPtr sourcePtr = IntPtr.Add(mapped.DataPointer, checked((int)(row * mapped.RowPitch)));
                    Marshal.Copy(sourcePtr, targetBuffer, row * rowSize, rowSize);
                }
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
        }
    }

    public void InjectFrame(bool[,] frame)
    {
    }

    public void InjectRgbFrame(bool[,] red, bool[,] green, bool[,] blue)
    {
    }

    public bool TryInjectCompositeSurface(
        GpuCompositeSurface? compositeSurface,
        double min,
        double max,
        bool invertThreshold,
        GameOfLifeEngine.InjectionMode mode,
        double noiseProbability,
        int period,
        int pulseStep,
        bool invertInput,
        double hueShiftDegrees = 0.0)
    {
        if (compositeSurface == null ||
            compositeSurface.Width != _columns ||
            compositeSurface.Height != _rows)
        {
            return false;
        }

        lock (_sync)
        {
            EnsureResources();
            if (_context == null || _injectCompositeShader == null || _snapshotUav == null)
            {
                return false;
            }

            UploadParameters(passParity: 0, sortAxis: 0);
            _context.CSSetShader(_injectCompositeShader);
            _context.CSSetShaderResources(0, new[] { compositeSurface.ShaderResourceView });
            _context.CSSetUnorderedAccessViews(0, new[] { _snapshotUav });
            _context.CSSetConstantBuffers(0, new[] { _parameterBuffer! });
            DispatchGrid(_context, _columns, _rows);
            _context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
            _context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
            _context.CSSetShader(null);

            if (_workTextureA != null)
            {
                _context.CopyResource(_workTextureA, _snapshotTexture!);
                _workAIsSource = true;
            }

            _hasSnapshot = true;
            _publishedTextureDirty = true;
            return true;
        }
    }

    public bool TryGetSharedColorTexture(out IntPtr sharedHandle, out int width, out int height)
    {
        sharedHandle = IntPtr.Zero;
        width = 0;
        height = 0;

        lock (_sync)
        {
            if (!EnsurePublishedTexture())
            {
                return false;
            }

            sharedHandle = _publishedSharedHandle;
            width = _columns;
            height = _rows;
            return sharedHandle != IntPtr.Zero;
        }
    }

    public bool TryGetColorSurface(out GpuCompositeSurface? surface)
    {
        surface = null;

        lock (_sync)
        {
            if (!EnsurePublishedTexture() || _publishedTexture == null || _publishedSrv == null)
            {
                return false;
            }

            surface = new GpuCompositeSurface(
                _publishedTexture,
                _publishedSrv,
                _publishedSharedHandle,
                _columns,
                _rows);
            return true;
        }
    }

    public bool TryGetPresentationSurface(out GpuCompositeSurface? surface)
    {
        surface = null;

        lock (_sync)
        {
            if (!EnsurePublishedTexture() || _presentationTexture == null || _presentationSrv == null)
            {
                return false;
            }

            surface = new GpuCompositeSurface(
                _presentationTexture,
                _presentationSrv,
                IntPtr.Zero,
                _columns,
                _rows);
            return true;
        }
    }

    public void Dispose()
    {
        DisposeResources();
    }

    private void InitializeGpu()
    {
        _sharedDevice = GpuSharedDevice.GetOrCreate();
        _device = _sharedDevice.Device;
        _context = _sharedDevice.Context;
        _sync = _sharedDevice.SyncRoot;

        _injectCompositeShader = _device.CreateComputeShader(LoadShaderBytecode("Assets/GpuPixelSortInjectCompositeCS.cso"));
        _sortPassShader = _device.CreateComputeShader(LoadShaderBytecode("Assets/GpuPixelSortSortPassCS.cso"));
        _publishOutputShader = _device.CreateComputeShader(LoadShaderBytecode("Assets/GpuPixelSortPublishOutputCS.cso"));
        _publishPresentationShader = _device.CreateComputeShader(LoadShaderBytecode("Assets/GpuPixelSortPublishPresentationOutputCS.cso"));
        _parameterBuffer = _device.CreateBuffer(
            (uint)Marshal.SizeOf<PixelSortParameters>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);
    }

    private void EnsureResources()
    {
        if (_device == null)
        {
            throw new InvalidOperationException("GPU pixel-sort device was unavailable after initialization.");
        }

        if (_snapshotTexture != null &&
            _workTextureA != null &&
            _workTextureB != null &&
            _publishedTexture != null &&
            _presentationTexture != null &&
            _stagingTexture != null)
        {
            var snapshotDesc = _snapshotTexture.Description;
            if (snapshotDesc.Width == _columns && snapshotDesc.Height == _rows)
            {
                return;
            }
        }

        DisposeTextures();

        var storageDescription = new Texture2DDescription(
            Format.R8G8B8A8_UInt,
            (uint)_columns,
            (uint)_rows,
            1,
            1,
            BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);
        _snapshotTexture = _device.CreateTexture2D(storageDescription);
        _snapshotSrv = _device.CreateShaderResourceView(_snapshotTexture);
        _snapshotUav = _device.CreateUnorderedAccessView(_snapshotTexture);

        var workDescription = new Texture2DDescription(
            Format.R8G8B8A8_UInt,
            (uint)_columns,
            (uint)_rows,
            1,
            1,
            BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);
        _workTextureA = _device.CreateTexture2D(workDescription);
        _workTextureB = _device.CreateTexture2D(workDescription);
        _workSrvA = _device.CreateShaderResourceView(_workTextureA);
        _workSrvB = _device.CreateShaderResourceView(_workTextureB);
        _workUavA = _device.CreateUnorderedAccessView(_workTextureA);
        _workUavB = _device.CreateUnorderedAccessView(_workTextureB);

        var publishedDescription = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)_columns,
            (uint)_rows,
            1,
            1,
            BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.Shared);
        _publishedTexture = _device.CreateTexture2D(publishedDescription);
        _publishedSrv = _device.CreateShaderResourceView(_publishedTexture);
        _publishedUav = _device.CreateUnorderedAccessView(_publishedTexture);
        using (var publishedResource = _publishedTexture.QueryInterface<IDXGIResource>())
        {
            _publishedSharedHandle = publishedResource.SharedHandle;
        }

        var presentationDescription = new Texture2DDescription(
            Format.R8G8B8A8_UInt,
            (uint)_columns,
            (uint)_rows,
            1,
            1,
            BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);
        _presentationTexture = _device.CreateTexture2D(presentationDescription);
        _presentationSrv = _device.CreateShaderResourceView(_presentationTexture);
        _presentationUav = _device.CreateUnorderedAccessView(_presentationTexture);

        var stagingDescription = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)_columns,
            (uint)_rows,
            1,
            1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            1,
            0,
            ResourceOptionFlags.None);
        _stagingTexture = _device.CreateTexture2D(stagingDescription);
        ClearWorkingTextures();
    }

    private void DispatchSortPass(int passParity, int sortAxis)
    {
        if (_context == null || _parameterBuffer == null || _sortPassShader == null || CurrentWorkSrv == null || DestinationWorkUav == null)
        {
            return;
        }

        UploadParameters(passParity, sortAxis);
        _context.CSSetShader(_sortPassShader);
        _context.CSSetShaderResources(0, new[] { CurrentWorkSrv });
        _context.CSSetUnorderedAccessViews(0, new[] { DestinationWorkUav });
        _context.CSSetConstantBuffers(0, new[] { _parameterBuffer });
        DispatchSortGrid(_context);
        _context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
        _context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
        _context.CSSetShader(null);
    }

    private bool EnsurePublishedTexture()
    {
        EnsureResources();
        if (_context == null ||
            _publishedTexture == null ||
            _publishedUav == null ||
            _presentationTexture == null ||
            _presentationUav == null ||
            _publishOutputShader == null ||
            _publishPresentationShader == null ||
            _parameterBuffer == null ||
            CurrentWorkSrv == null)
        {
            return false;
        }

        if (!_publishedTextureDirty)
        {
            return true;
        }

        UploadParameters(passParity: 0, sortAxis: 0);
        _context.CSSetShader(_publishOutputShader);
        _context.CSSetShaderResources(0, new[] { CurrentWorkSrv });
        _context.CSSetUnorderedAccessViews(0, new[] { _publishedUav });
        _context.CSSetConstantBuffers(0, new[] { _parameterBuffer! });
        DispatchGrid(_context, _columns, _rows);
        _context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
        _context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
        _context.CSSetShader(null);

        _context.CSSetShader(_publishPresentationShader);
        _context.CSSetShaderResources(0, new[] { CurrentWorkSrv });
        _context.CSSetUnorderedAccessViews(0, new[] { _presentationUav });
        _context.CSSetConstantBuffers(0, new[] { _parameterBuffer! });
        DispatchGrid(_context, _columns, _rows);
        _context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
        _context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
        _context.CSSetShader(null);

        _publishedTextureDirty = false;
        return true;
    }

    private void UploadParameters(int passParity, int sortAxis)
    {
        if (_context == null || _parameterBuffer == null)
        {
            return;
        }

        var parameters = new PixelSortParameters
        {
            Width = (uint)_columns,
            Height = (uint)_rows,
            CellWidth = (uint)Math.Max(1, _cellWidth),
            CellHeight = (uint)Math.Max(1, _cellHeight),
            PassParity = (uint)Math.Max(0, passParity),
            SortAxis = (uint)Math.Max(0, sortAxis)
        };

        int size = Marshal.SizeOf<PixelSortParameters>();
        IntPtr native = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(parameters, native, false);
            _context.UpdateSubresource(_parameterBuffer, 0, null, native, (uint)size, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    private void ClearWorkingTextures()
    {
        if (_context == null)
        {
            return;
        }

        var clearValue = new Vortice.Mathematics.Int4(0, 0, 0, 255);
        _context.ClearUnorderedAccessView(_snapshotUav!, clearValue);
        _context.ClearUnorderedAccessView(_workUavA!, clearValue);
        _context.ClearUnorderedAccessView(_workUavB!, clearValue);
        _workAIsSource = true;
        _publishedTextureDirty = true;
    }

    private void SwapWorkTextures()
    {
        _workAIsSource = !_workAIsSource;
    }

    private static int CalculateMaxCellSpan(int span, int gridCount)
    {
        int cells = Math.Max(1, gridCount);
        int maxSpan = 1;
        for (int cell = 0; cell < cells; cell++)
        {
            int start = (cell * span) / cells;
            int end = ((cell + 1) * span) / cells;
            maxSpan = Math.Max(maxSpan, end - start);
        }

        return maxSpan;
    }

    private ID3D11Texture2D? CurrentWorkTexture => _workAIsSource ? _workTextureA : _workTextureB;

    private ID3D11ShaderResourceView? CurrentWorkSrv => _workAIsSource ? _workSrvA : _workSrvB;

    private ID3D11UnorderedAccessView? DestinationWorkUav => _workAIsSource ? _workUavB : _workUavA;

    private static void DispatchGrid(ID3D11DeviceContext1 context, int width, int height)
    {
        uint groupsX = (uint)((width + 7) / 8);
        uint groupsY = (uint)((height + 7) / 8);
        context.Dispatch(groupsX, groupsY, 1);
    }

    private void DispatchSortGrid(ID3D11DeviceContext1 context)
    {
        int dispatchColumns = Math.Max(1, (_columns + Math.Max(1, _cellWidth) - 1) / Math.Max(1, _cellWidth));
        int dispatchRows = Math.Max(1, (_rows + Math.Max(1, _cellHeight) - 1) / Math.Max(1, _cellHeight));
        context.Dispatch((uint)dispatchColumns, (uint)dispatchRows, 1);
    }

    private static byte[] LoadShaderBytecode(string relativePath)
    {
        var resource = Application.GetResourceStream(new Uri(relativePath, UriKind.Relative));
        if (resource == null)
        {
            throw new FileNotFoundException($"Shader resource not found: {relativePath}");
        }

        using var stream = resource.Stream;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private void DisposeResources()
    {
        DisposeTextures();
        _parameterBuffer?.Dispose();
        _parameterBuffer = null;
        _injectCompositeShader?.Dispose();
        _injectCompositeShader = null;
        _sortPassShader?.Dispose();
        _sortPassShader = null;
        _publishOutputShader?.Dispose();
        _publishOutputShader = null;
        _publishPresentationShader?.Dispose();
        _publishPresentationShader = null;
        _context = null;
        _device = null;
        _sharedDevice = null;
    }

    private void DisposeTextures()
    {
        _snapshotSrv?.Dispose();
        _snapshotSrv = null;
        _snapshotUav?.Dispose();
        _snapshotUav = null;
        _snapshotTexture?.Dispose();
        _snapshotTexture = null;

        _workSrvA?.Dispose();
        _workSrvA = null;
        _workSrvB?.Dispose();
        _workSrvB = null;
        _workUavA?.Dispose();
        _workUavA = null;
        _workUavB?.Dispose();
        _workUavB = null;
        _workTextureA?.Dispose();
        _workTextureA = null;
        _workTextureB?.Dispose();
        _workTextureB = null;

        _publishedSrv?.Dispose();
        _publishedSrv = null;
        _publishedUav?.Dispose();
        _publishedUav = null;
        _presentationSrv?.Dispose();
        _presentationSrv = null;
        _presentationUav?.Dispose();
        _presentationUav = null;
        _publishedTexture?.Dispose();
        _publishedTexture = null;
        _presentationTexture?.Dispose();
        _presentationTexture = null;
        _stagingTexture?.Dispose();
        _stagingTexture = null;

        if (_publishedSharedHandle != IntPtr.Zero)
        {
            CloseHandle(_publishedSharedHandle);
            _publishedSharedHandle = IntPtr.Zero;
        }

        _publishedTextureDirty = true;
    }
}
