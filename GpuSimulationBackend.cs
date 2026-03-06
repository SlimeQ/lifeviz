using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace lifeviz;

internal sealed class GpuSimulationBackend : ISimulationBackend
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SimulationParameters
    {
        public uint Width;
        public uint Height;
        public uint Depth;
        public uint BinningMode;
        public uint RDepth;
        public uint GDepth;
        public uint BDepth;
        public uint InjectionMode;
        public float ThresholdMin;
        public float ThresholdMax;
        public float NoiseProbability;
        public float InvertInput;
        public uint PulsePeriod;
        public uint PulseStep;
        public uint InvertThreshold;
        public uint Padding;
    }

    private readonly CpuSimulationBackend _fallback = new();
    private readonly Random _random = new();
    private object _sync = new();
    private GpuSharedDevice? _sharedDevice;

    private ID3D11Device1? _device;
    private ID3D11DeviceContext1? _context;
    private ID3D11ComputeShader? _injectShader;
    private ID3D11ComputeShader? _injectCompositeShader;
    private ID3D11ComputeShader? _stepShader;
    private ID3D11ComputeShader? _renderShader;
    private ID3D11Buffer? _parameterBuffer;

    private ID3D11Texture2D? _historyTextureA;
    private ID3D11Texture2D? _historyTextureB;
    private ID3D11ShaderResourceView? _historySrvA;
    private ID3D11ShaderResourceView? _historySrvB;
    private ID3D11UnorderedAccessView? _historyUavA;
    private ID3D11UnorderedAccessView? _historyUavB;

    private ID3D11Texture2D? _maskTexture;
    private ID3D11ShaderResourceView? _maskSrv;

    private ID3D11Texture2D? _colorTexture;
    private ID3D11UnorderedAccessView? _colorUav;
    private ID3D11Texture2D? _colorStagingTexture;

    private bool _gpuAvailable;
    private bool _historyAIsSource = true;
    private int _columns = 256;
    private int _rows = 144;
    private int _depth = 24;
    private int _rDepth = 8;
    private int _gDepth = 8;
    private int _bDepth = 8;
    private double _aspectRatio = 16d / 9d;
    private GameOfLifeEngine.LifeMode _mode = GameOfLifeEngine.LifeMode.NaiveGrayscale;
    private GameOfLifeEngine.BinningMode _binningMode = GameOfLifeEngine.BinningMode.Fill;
    private GameOfLifeEngine.InjectionMode _injectionMode = GameOfLifeEngine.InjectionMode.Threshold;
    private double _thresholdMin = 0.35;
    private double _thresholdMax = 0.75;
    private double _noiseProbability;
    private bool _invertInput;
    private bool _invertThreshold;
    private int _pulsePeriod = 24;
    private int _pulseStep;
    private byte[]? _maskBuffer;
    private byte[]? _cpuReadbackBuffer;

    public GpuSimulationBackend()
    {
        TryInitializeGpu();
    }

    public int Columns => UseGpu ? _columns : _fallback.Columns;
    public int Rows => UseGpu ? _rows : _fallback.Rows;
    public int Depth => UseGpu ? _depth : _fallback.Depth;
    public double AspectRatio => UseGpu ? _aspectRatio : _fallback.AspectRatio;
    public GameOfLifeEngine.LifeMode Mode => UseGpu ? _mode : _fallback.Mode;
    public GameOfLifeEngine.BinningMode BinMode => UseGpu ? _binningMode : _fallback.BinMode;
    public int RDepth => UseGpu ? _rDepth : _fallback.RDepth;
    public int GDepth => UseGpu ? _gDepth : _fallback.GDepth;
    public int BDepth => UseGpu ? _bDepth : _fallback.BDepth;
    public GameOfLifeEngine.InjectionMode InjectMode => UseGpu ? _injectionMode : _fallback.InjectMode;
    public IReadOnlyList<bool[,]> Frames => UseGpu ? Array.Empty<bool[,]>() : _fallback.Frames;
    internal bool IsGpuAvailable => _gpuAvailable;
    internal bool IsGpuActive => UseGpu;

    private bool UseGpu => _gpuAvailable && _mode == GameOfLifeEngine.LifeMode.NaiveGrayscale;

    public void Configure(int requestedRows, int requestedDepth, double? aspectRatio = null)
    {
        if (!UseGpu)
        {
            _fallback.Configure(requestedRows, requestedDepth, aspectRatio);
            return;
        }

        _rows = Math.Clamp(requestedRows, 72, 2160);
        if (aspectRatio.HasValue && aspectRatio.Value > 0.01)
        {
            _aspectRatio = aspectRatio.Value;
        }

        _depth = Math.Clamp(requestedDepth, 3, 96);
        _columns = Math.Clamp((int)Math.Round(_rows * _aspectRatio), 32, 4096);
        _rows = Math.Max(72, Math.Min(2160, (int)Math.Round(_columns / _aspectRatio)));
        (_rDepth, _gDepth, _bDepth) = CalculateChannelDepths(_depth);

        lock (_sync)
        {
            EnsureResources();
            ClearHistory();
        }
    }

    public void SetBinningMode(GameOfLifeEngine.BinningMode mode)
    {
        _binningMode = mode;
        _fallback.SetBinningMode(mode);
    }

    public void SetInjectionMode(GameOfLifeEngine.InjectionMode mode)
    {
        _injectionMode = mode;
        _fallback.SetInjectionMode(mode);
    }

    public void SetMode(GameOfLifeEngine.LifeMode mode)
    {
        _mode = mode;
        _fallback.Configure(_rows, _depth, _aspectRatio);
        _fallback.SetBinningMode(_binningMode);
        _fallback.SetInjectionMode(_injectionMode);
        _fallback.SetMode(mode);
        if (UseGpu)
        {
            lock (_sync)
            {
                EnsureResources();
                ClearHistory();
            }
            Randomize();
        }
    }

    public void Randomize()
    {
        if (!UseGpu)
        {
            _fallback.Randomize();
            return;
        }

        lock (_sync)
        {
            EnsureResources();
            if (_historyTextureA == null || _historyTextureB == null || _context == null)
            {
                return;
            }

            int sliceLength = _columns * _rows;
            byte[] randomSlice = new byte[sliceLength];
            for (int slice = 0; slice < _depth; slice++)
            {
                for (int i = 0; i < sliceLength; i++)
                {
                    randomSlice[i] = _random.NextDouble() < 0.35 ? (byte)1 : (byte)0;
                }
                UploadSlice(_context, _historyTextureA, slice, randomSlice, _columns, _rows);
                UploadSlice(_context, _historyTextureB, slice, randomSlice, _columns, _rows);
            }

            _historyAIsSource = true;
        }
    }

    public void Step()
    {
        if (!UseGpu)
        {
            _fallback.Step();
            return;
        }

        lock (_sync)
        {
            EnsureResources();
            if (_context == null || _stepShader == null)
            {
                return;
            }

            DispatchHistoryShader(_stepShader, hasMask: false);
            SwapHistory();
        }
    }

    public (byte r, byte g, byte b) GetColor(int row, int col)
    {
        if (!UseGpu)
        {
            return _fallback.GetColor(row, col);
        }

        int requiredLength = _columns * _rows * 4;
        _cpuReadbackBuffer ??= new byte[requiredLength];
        FillColorBuffer(_cpuReadbackBuffer);
        int index = (row * _columns + col) * 4;
        return (_cpuReadbackBuffer[index], _cpuReadbackBuffer[index + 1], _cpuReadbackBuffer[index + 2]);
    }

    public void FillColorBuffer(byte[] targetBuffer)
    {
        if (!UseGpu)
        {
            _fallback.FillColorBuffer(targetBuffer);
            return;
        }

        lock (_sync)
        {
            EnsureResources();
            if (_context == null || _renderShader == null || _colorTexture == null || _colorStagingTexture == null || _colorUav == null)
            {
                return;
            }

            UploadParameters();

            _context.CSSetShader(_renderShader);
            _context.CSSetShaderResources(0, new[] { SourceHistorySrv! });
            _context.CSSetUnorderedAccessViews(0, new[] { _colorUav });
            _context.CSSetConstantBuffers(0, new[] { _parameterBuffer! });
            DispatchGrid(_context, _columns, _rows);
            _context.CopyResource(_colorStagingTexture, _colorTexture);

            var mapped = _context.Map(_colorStagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
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
                _context.Unmap(_colorStagingTexture, 0);
                _context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
                _context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
                _context.CSSetShader(null);
            }
        }
    }

    public void InjectFrame(bool[,] frame)
    {
        if (!UseGpu)
        {
            _fallback.InjectFrame(frame);
            return;
        }

        if (frame.GetLength(0) != _rows || frame.GetLength(1) != _columns)
        {
            return;
        }

        lock (_sync)
        {
            EnsureResources();
            if (_context == null || _injectShader == null || _maskTexture == null)
            {
                return;
            }

            _maskBuffer ??= new byte[_columns * _rows];
            for (int row = 0; row < _rows; row++)
            {
                int rowOffset = row * _columns;
                for (int col = 0; col < _columns; col++)
                {
                    _maskBuffer[rowOffset + col] = frame[row, col] ? (byte)1 : (byte)0;
                }
            }

            UploadMask(_maskBuffer);
            DispatchHistoryShader(_injectShader, hasMask: true);
            SwapHistory();
        }
    }

    public void InjectRgbFrame(bool[,] red, bool[,] green, bool[,] blue)
    {
        if (!UseGpu)
        {
            _fallback.InjectRgbFrame(red, green, blue);
            return;
        }

        var merged = new bool[_rows, _columns];
        for (int row = 0; row < _rows; row++)
        {
            for (int col = 0; col < _columns; col++)
            {
                merged[row, col] = red[row, col] || green[row, col] || blue[row, col];
            }
        }

        InjectFrame(merged);
    }

    internal bool TryInjectCompositeSurface(
        GpuCompositeSurface? compositeSurface,
        double min,
        double max,
        bool invertThreshold,
        GameOfLifeEngine.InjectionMode mode,
        double noiseProbability,
        int period,
        int pulseStep,
        bool invertInput)
    {
        if (!UseGpu || compositeSurface == null || compositeSurface.Width != _columns || compositeSurface.Height != _rows)
        {
            return false;
        }

        lock (_sync)
        {
            EnsureResources();
            if (_context == null || _injectCompositeShader == null)
            {
                return false;
            }

            _thresholdMin = Math.Clamp(min, 0, 1);
            _thresholdMax = Math.Clamp(max, 0, 1);
            if (_thresholdMin > _thresholdMax)
            {
                (_thresholdMin, _thresholdMax) = (_thresholdMax, _thresholdMin);
            }

            _invertThreshold = invertThreshold;
            _injectionMode = mode;
            _noiseProbability = Math.Clamp(noiseProbability, 0, 1);
            _pulsePeriod = Math.Max(1, period);
            _pulseStep = Math.Max(0, pulseStep);
            _invertInput = invertInput;

            DispatchCompositeInjectShader(compositeSurface.ShaderResourceView);
            SwapHistory();
            return true;
        }
    }

    public void Dispose()
    {
        DisposeResources();
        _fallback.Dispose();
    }

    private void TryInitializeGpu()
    {
        try
        {
            _sharedDevice = GpuSharedDevice.GetOrCreate();
            Logger.Info($"GPU simulation device created at feature level {_sharedDevice.FeatureLevel}.");

            _device = _sharedDevice.Device;
            _context = _sharedDevice.Context;
            _sync = _sharedDevice.SyncRoot;

            _injectShader = WrapGpuInit("CreateComputeShader InjectCS", () => _device.CreateComputeShader(LoadShaderBytecode("Assets/GpuSimulationInjectCS.cso")));
            _injectCompositeShader = WrapGpuInit("CreateComputeShader InjectCompositeCS", () => _device.CreateComputeShader(LoadShaderBytecode("Assets/GpuSimulationInjectCompositeCS.cso")));
            _stepShader = WrapGpuInit("CreateComputeShader StepCS", () => _device.CreateComputeShader(LoadShaderBytecode("Assets/GpuSimulationStepCS.cso")));
            _renderShader = WrapGpuInit("CreateComputeShader RenderCS", () => _device.CreateComputeShader(LoadShaderBytecode("Assets/GpuSimulationRenderCS.cso")));
            _parameterBuffer = WrapGpuInit("CreateBuffer ConstantBuffer", () => _device.CreateBuffer(
                (uint)Marshal.SizeOf<SimulationParameters>(),
                BindFlags.ConstantBuffer,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.None,
                0));
            _gpuAvailable = true;
            Logger.Info("GPU simulation backend initialized.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"GPU simulation backend unavailable, using CPU simulation fallback. {ex.Message}");
            _gpuAvailable = false;
            DisposeResources();
        }
    }

    private static T WrapGpuInit<T>(string step, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"GPU simulation init failed during {step}.", ex);
        }
    }

    private void EnsureResources()
    {
        if (!UseGpu || _device == null)
        {
            return;
        }

        if (HistoryResourcesMatch())
        {
            return;
        }

        DisposeHistoryResources();

        var historyDescription = new Texture2DDescription(
            Format.R8_UInt,
            (uint)_columns,
            (uint)_rows,
            (uint)_depth,
            1,
            BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);
        _historyTextureA = _device.CreateTexture2D(historyDescription);
        _historyTextureB = _device.CreateTexture2D(historyDescription);
        var historySrvDescription = new ShaderResourceViewDescription(
            _historyTextureA,
            ShaderResourceViewDimension.Texture2DArray,
            Format.R8_UInt,
            0,
            1,
            0,
            (uint)_depth);
        var historyUavDescription = new UnorderedAccessViewDescription(
            _historyTextureA,
            UnorderedAccessViewDimension.Texture2DArray,
            Format.R8_UInt,
            0,
            0,
            (uint)_depth);
        _historySrvA = _device.CreateShaderResourceView(_historyTextureA, historySrvDescription);
        _historySrvB = _device.CreateShaderResourceView(_historyTextureB, historySrvDescription);
        _historyUavA = _device.CreateUnorderedAccessView(_historyTextureA, historyUavDescription);
        _historyUavB = _device.CreateUnorderedAccessView(_historyTextureB, historyUavDescription);

        var maskDescription = new Texture2DDescription(
            Format.R8_UInt,
            (uint)_columns,
            (uint)_rows,
            1,
            1,
            BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);
        _maskTexture = _device.CreateTexture2D(maskDescription);
        _maskSrv = _device.CreateShaderResourceView(_maskTexture);

        var colorDescription = new Texture2DDescription(
            Format.R8G8B8A8_UInt,
            (uint)_columns,
            (uint)_rows,
            1,
            1,
            BindFlags.UnorderedAccess,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);
        _colorTexture = _device.CreateTexture2D(colorDescription);
        _colorUav = _device.CreateUnorderedAccessView(_colorTexture);

        var stagingDescription = new Texture2DDescription(
            Format.R8G8B8A8_UInt,
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
        _colorStagingTexture = _device.CreateTexture2D(stagingDescription);

        _maskBuffer = new byte[_columns * _rows];
        _cpuReadbackBuffer = new byte[_columns * _rows * 4];
        _historyAIsSource = true;
    }

    private bool HistoryResourcesMatch()
    {
        if (_historyTextureA == null || _historyTextureB == null || _colorTexture == null || _colorStagingTexture == null)
        {
            return false;
        }

        var historyDesc = _historyTextureA.Description;
        var colorDesc = _colorTexture.Description;
        return historyDesc.Width == _columns &&
               historyDesc.Height == _rows &&
               historyDesc.ArraySize == _depth &&
               colorDesc.Width == _columns &&
               colorDesc.Height == _rows;
    }

    private void ClearHistory()
    {
        if (_context == null || _historyTextureA == null || _historyTextureB == null)
        {
            return;
        }

        byte[] zeroSlice = new byte[_columns * _rows];
        for (int slice = 0; slice < _depth; slice++)
        {
            UploadSlice(_context, _historyTextureA, slice, zeroSlice, _columns, _rows);
            UploadSlice(_context, _historyTextureB, slice, zeroSlice, _columns, _rows);
        }

        _historyAIsSource = true;
    }

    private void UploadMask(byte[] mask)
    {
        if (_context == null || _maskTexture == null)
        {
            return;
        }

        GCHandle handle = GCHandle.Alloc(mask, GCHandleType.Pinned);
        try
        {
            _context.UpdateSubresource(_maskTexture, 0u, (Vortice.Mathematics.Box?)null, handle.AddrOfPinnedObject(), (uint)_columns, (uint)(_columns * _rows));
        }
        finally
        {
            handle.Free();
        }
    }

    private void DispatchHistoryShader(ID3D11ComputeShader shader, bool hasMask)
    {
        if (_context == null || _parameterBuffer == null)
        {
            return;
        }

        UploadParameters();
        _context.CSSetShader(shader);
        if (hasMask)
        {
            _context.CSSetShaderResources(0, new[] { SourceHistorySrv!, _maskSrv! });
        }
        else
        {
            _context.CSSetShaderResources(0, new[] { SourceHistorySrv! });
        }
        _context.CSSetUnorderedAccessViews(0, new[] { DestinationHistoryUav! });
        _context.CSSetConstantBuffers(0, new[] { _parameterBuffer });
        DispatchGrid(_context, _columns, _rows);
        _context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
        _context.CSSetShaderResources(0, hasMask
            ? new ID3D11ShaderResourceView[] { null!, null! }
            : new ID3D11ShaderResourceView[] { null! });
        _context.CSSetShader(null);
    }

    private void DispatchCompositeInjectShader(ID3D11ShaderResourceView compositeSrv)
    {
        if (_context == null || _parameterBuffer == null || _injectCompositeShader == null)
        {
            return;
        }

        UploadParameters();
        _context.CSSetShader(_injectCompositeShader);
        _context.CSSetShaderResources(0, new[] { SourceHistorySrv!, compositeSrv });
        _context.CSSetUnorderedAccessViews(0, new[] { DestinationHistoryUav! });
        _context.CSSetConstantBuffers(0, new[] { _parameterBuffer });
        DispatchGrid(_context, _columns, _rows);
        _context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
        _context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
        _context.CSSetShader(null);
    }

    private void UploadParameters()
    {
        if (_context == null || _parameterBuffer == null)
        {
            return;
        }

        var parameters = new SimulationParameters
        {
            Width = (uint)_columns,
            Height = (uint)_rows,
            Depth = (uint)_depth,
            BinningMode = _binningMode == GameOfLifeEngine.BinningMode.Binary ? 1u : 0u,
            RDepth = (uint)_rDepth,
            GDepth = (uint)_gDepth,
            BDepth = (uint)_bDepth,
            InjectionMode = (uint)_injectionMode,
            ThresholdMin = (float)_thresholdMin,
            ThresholdMax = (float)_thresholdMax,
            NoiseProbability = (float)_noiseProbability,
            InvertInput = _invertInput ? 1.0f : 0.0f,
            PulsePeriod = (uint)Math.Max(1, _pulsePeriod),
            PulseStep = (uint)Math.Max(0, _pulseStep),
            InvertThreshold = _invertThreshold ? 1u : 0u
        };

        int size = Marshal.SizeOf<SimulationParameters>();
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

    private static void DispatchGrid(ID3D11DeviceContext1 context, int width, int height)
    {
        uint groupsX = (uint)((width + 7) / 8);
        uint groupsY = (uint)((height + 7) / 8);
        context.Dispatch(groupsX, groupsY, 1);
    }

    private void SwapHistory() => _historyAIsSource = !_historyAIsSource;

    private ID3D11ShaderResourceView? SourceHistorySrv => _historyAIsSource ? _historySrvA : _historySrvB;

    private ID3D11UnorderedAccessView? DestinationHistoryUav => _historyAIsSource ? _historyUavB : _historyUavA;

    private static void UploadSlice(ID3D11DeviceContext1 context, ID3D11Texture2D texture, int slice, byte[] data, int width, int height)
    {
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            context.UpdateSubresource(texture, (uint)slice, (Vortice.Mathematics.Box?)null, handle.AddrOfPinnedObject(), (uint)width, (uint)(width * height));
        }
        finally
        {
            handle.Free();
        }
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

    private static (int r, int g, int b) CalculateChannelDepths(int depth)
    {
        int baseSlice = depth / 3;
        int remainder = depth % 3;
        int r = baseSlice + (remainder > 0 ? 1 : 0);
        int g = baseSlice + (remainder > 1 ? 1 : 0);
        int b = depth - r - g;
        return (r, g, b);
    }

    private void DisposeResources()
    {
        DisposeHistoryResources();
        _parameterBuffer?.Dispose();
        _parameterBuffer = null;
        _injectShader?.Dispose();
        _injectShader = null;
        _injectCompositeShader?.Dispose();
        _injectCompositeShader = null;
        _stepShader?.Dispose();
        _stepShader = null;
        _renderShader?.Dispose();
        _renderShader = null;
        _context = null;
        _device = null;
        _sharedDevice = null;
    }

    private void DisposeHistoryResources()
    {
        _historySrvA?.Dispose();
        _historySrvA = null;
        _historySrvB?.Dispose();
        _historySrvB = null;
        _historyUavA?.Dispose();
        _historyUavA = null;
        _historyUavB?.Dispose();
        _historyUavB = null;
        _historyTextureA?.Dispose();
        _historyTextureA = null;
        _historyTextureB?.Dispose();
        _historyTextureB = null;
        _maskSrv?.Dispose();
        _maskSrv = null;
        _maskTexture?.Dispose();
        _maskTexture = null;
        _colorUav?.Dispose();
        _colorUav = null;
        _colorTexture?.Dispose();
        _colorTexture = null;
        _colorStagingTexture?.Dispose();
        _colorStagingTexture = null;
    }
}
