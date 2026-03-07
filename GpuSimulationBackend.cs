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
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

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
        public uint LifeMode;
        public float InjectHueRr;
        public float InjectHueRg;
        public float InjectHueRb;
        public float InjectHueGr;
        public float InjectHueGg;
        public float InjectHueGb;
        public float InjectHueBr;
        public float InjectHueBg;
        public float InjectHueBb;
        public float Padding0;
        public float Padding1;
        public float Padding2;
    }

    private readonly CpuSimulationBackend _fallback = new();
    private readonly Random _random = new();
    private object _sync = new();
    private GpuSharedDevice? _sharedDevice;

    private ID3D11Device1? _device;
    private ID3D11DeviceContext1? _context;
    private ID3D11ComputeShader? _injectShader;
    private ID3D11ComputeShader? _injectRgbShader;
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
    private ID3D11Texture2D? _rgbMaskTexture;
    private ID3D11ShaderResourceView? _rgbMaskSrv;

    private ID3D11Texture2D? _colorTexture;
    private ID3D11ShaderResourceView? _colorTextureView;
    private ID3D11UnorderedAccessView? _colorUav;
    private ID3D11Texture2D? _colorStagingTexture;
    private IntPtr _colorSharedHandle;

    private bool _gpuAvailable;
    private bool _historyAIsSource = true;
    private bool _colorTextureDirty = true;
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
    private float _injectHueRr = 1.0f;
    private float _injectHueRg;
    private float _injectHueRb;
    private float _injectHueGr;
    private float _injectHueGg = 1.0f;
    private float _injectHueGb;
    private float _injectHueBr;
    private float _injectHueBg;
    private float _injectHueBb = 1.0f;
    private byte[]? _maskBuffer;
    private byte[]? _rgbMaskBuffer;
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

    private bool UseGpu => _gpuAvailable;

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
        (_rDepth, _gDepth, _bDepth) = CalculateChannelDepths(_depth);
        _fallback.Configure(_rows, _depth, _aspectRatio);

        if (!UseGpu)
        {
            return;
        }

        lock (_sync)
        {
            EnsureResources();
            ClearHistory();
            _colorTextureDirty = true;
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
        if (_mode == mode)
        {
            return;
        }

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
                _colorTextureDirty = true;
            }
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
            _colorTextureDirty = true;
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
            _colorTextureDirty = true;
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
            if (!RenderColorTexture())
            {
                return;
            }
            _context!.CopyResource(_colorStagingTexture!, _colorTexture!);

            var mapped = _context.Map(_colorStagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
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
            _colorTextureDirty = true;
        }
    }

    public void InjectRgbFrame(bool[,] red, bool[,] green, bool[,] blue)
    {
        if (!UseGpu)
        {
            _fallback.InjectRgbFrame(red, green, blue);
            return;
        }

        if (red.GetLength(0) != _rows || red.GetLength(1) != _columns ||
            green.GetLength(0) != _rows || green.GetLength(1) != _columns ||
            blue.GetLength(0) != _rows || blue.GetLength(1) != _columns)
        {
            return;
        }

        lock (_sync)
        {
            EnsureResources();
            if (_context == null || _injectRgbShader == null || _rgbMaskTexture == null)
            {
                return;
            }

            _rgbMaskBuffer ??= new byte[_columns * _rows * 4];
            for (int row = 0; row < _rows; row++)
            {
                int rowOffset = row * _columns;
                int byteOffset = rowOffset * 4;
                for (int col = 0; col < _columns; col++)
                {
                    int maskIndex = byteOffset + (col * 4);
                    _rgbMaskBuffer[maskIndex] = red[row, col] ? (byte)1 : (byte)0;
                    _rgbMaskBuffer[maskIndex + 1] = green[row, col] ? (byte)1 : (byte)0;
                    _rgbMaskBuffer[maskIndex + 2] = blue[row, col] ? (byte)1 : (byte)0;
                    _rgbMaskBuffer[maskIndex + 3] = 255;
                }
            }

            UploadRgbMask(_rgbMaskBuffer);
            DispatchRgbInjectShader();
            SwapHistory();
            _colorTextureDirty = true;
        }
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
        bool invertInput,
        double hueShiftDegrees = 0.0)
    {
        if (!UseGpu)
        {
            if (App.IsSmokeTestMode)
            {
                Logger.Info($"GPU composite inject rejected: UseGpu=false, mode={_mode}, gpuAvailable={_gpuAvailable}.");
            }
            return false;
        }

        if (compositeSurface == null)
        {
            if (App.IsSmokeTestMode)
            {
                Logger.Info("GPU composite inject rejected: composite surface was null.");
            }
            return false;
        }

        if (compositeSurface.Width != _columns || compositeSurface.Height != _rows)
        {
            if (App.IsSmokeTestMode)
            {
                Logger.Info($"GPU composite inject rejected: surface {compositeSurface.Width}x{compositeSurface.Height} != engine {_columns}x{_rows}.");
            }
            return false;
        }

        lock (_sync)
        {
            EnsureResources();
            if (_context == null || _injectCompositeShader == null)
            {
                if (App.IsSmokeTestMode)
                {
                    Logger.Info($"GPU composite inject rejected: context null={_context == null}, injectCompositeShader null={_injectCompositeShader == null}.");
                }
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
            ConfigureInjectHueMatrix(_mode == GameOfLifeEngine.LifeMode.RgbChannels ? -hueShiftDegrees : 0.0);

            DispatchCompositeInjectShader(compositeSurface.ShaderResourceView);
            SwapHistory();
            _colorTextureDirty = true;
            return true;
        }
    }

    internal bool TryGetSharedColorTexture(out IntPtr sharedHandle, out int width, out int height)
    {
        sharedHandle = IntPtr.Zero;
        width = 0;
        height = 0;

        if (!UseGpu)
        {
            return false;
        }

        lock (_sync)
        {
            if (!RenderColorTexture() || _colorSharedHandle == IntPtr.Zero)
            {
                return false;
            }

            // Shared-resource visibility across devices requires command submission.
            _context!.Flush();
            sharedHandle = _colorSharedHandle;
            width = _columns;
            height = _rows;
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
            _injectRgbShader = WrapGpuInit("CreateComputeShader InjectRgbCS", () => _device.CreateComputeShader(LoadShaderBytecode("Assets/GpuSimulationInjectRgbCS.cso")));
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

        var rgbMaskDescription = new Texture2DDescription(
            Format.R8G8B8A8_UInt,
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
        _rgbMaskTexture = _device.CreateTexture2D(rgbMaskDescription);
        _rgbMaskSrv = _device.CreateShaderResourceView(_rgbMaskTexture);

        var colorDescription = new Texture2DDescription(
            Format.R8G8B8A8_UInt,
            (uint)_columns,
            (uint)_rows,
            1,
            1,
            BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.Shared);
        _colorTexture = _device.CreateTexture2D(colorDescription);
        _colorTextureView = _device.CreateShaderResourceView(_colorTexture);
        _colorUav = _device.CreateUnorderedAccessView(_colorTexture);
        using (var colorResource = _colorTexture.QueryInterface<IDXGIResource>())
        {
            _colorSharedHandle = colorResource.SharedHandle;
        }

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
        _rgbMaskBuffer = new byte[_columns * _rows * 4];
        _cpuReadbackBuffer = new byte[_columns * _rows * 4];
        _historyAIsSource = true;
        _colorTextureDirty = true;
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
        _colorTextureDirty = true;
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

    private void UploadRgbMask(byte[] mask)
    {
        if (_context == null || _rgbMaskTexture == null)
        {
            return;
        }

        GCHandle handle = GCHandle.Alloc(mask, GCHandleType.Pinned);
        try
        {
            _context.UpdateSubresource(_rgbMaskTexture, 0u, (Vortice.Mathematics.Box?)null, handle.AddrOfPinnedObject(), (uint)(_columns * 4), (uint)(_columns * _rows * 4));
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

    private void DispatchRgbInjectShader()
    {
        if (_context == null || _parameterBuffer == null || _injectRgbShader == null || _rgbMaskSrv == null)
        {
            return;
        }

        UploadParameters();
        _context.CSSetShader(_injectRgbShader);
        _context.CSSetShaderResources(0, new[] { SourceHistorySrv!, null!, _rgbMaskSrv });
        _context.CSSetUnorderedAccessViews(0, new[] { DestinationHistoryUav! });
        _context.CSSetConstantBuffers(0, new[] { _parameterBuffer });
        DispatchGrid(_context, _columns, _rows);
        _context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
        _context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null!, null! });
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
            InvertThreshold = _invertThreshold ? 1u : 0u,
            LifeMode = _mode == GameOfLifeEngine.LifeMode.RgbChannels ? 1u : 0u,
            InjectHueRr = _injectHueRr,
            InjectHueRg = _injectHueRg,
            InjectHueRb = _injectHueRb,
            InjectHueGr = _injectHueGr,
            InjectHueGg = _injectHueGg,
            InjectHueGb = _injectHueGb,
            InjectHueBr = _injectHueBr,
            InjectHueBg = _injectHueBg,
            InjectHueBb = _injectHueBb
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

    private bool RenderColorTexture()
    {
        EnsureResources();
        if (_context == null || _renderShader == null || _colorTexture == null || _colorStagingTexture == null || _colorUav == null)
        {
            return false;
        }

        if (!_colorTextureDirty)
        {
            return true;
        }

        UploadParameters();

        _context.CSSetShader(_renderShader);
        _context.CSSetShaderResources(0, new[] { SourceHistorySrv! });
        _context.CSSetUnorderedAccessViews(0, new[] { _colorUav });
        _context.CSSetConstantBuffers(0, new[] { _parameterBuffer! });
        DispatchGrid(_context, _columns, _rows);
        _context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
        _context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null! });
        _context.CSSetShader(null);
        _colorTextureDirty = false;
        return true;
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

    private void ConfigureInjectHueMatrix(double hueShiftDegrees)
    {
        BuildHueRotationMatrix(hueShiftDegrees,
            out double rr, out double rg, out double rb,
            out double gr, out double gg, out double gb,
            out double br, out double bg, out double bb);
        _injectHueRr = (float)rr;
        _injectHueRg = (float)rg;
        _injectHueRb = (float)rb;
        _injectHueGr = (float)gr;
        _injectHueGg = (float)gg;
        _injectHueGb = (float)gb;
        _injectHueBr = (float)br;
        _injectHueBg = (float)bg;
        _injectHueBb = (float)bb;
    }

    private static void BuildHueRotationMatrix(
        double hueShiftDegrees,
        out double rr, out double rg, out double rb,
        out double gr, out double gg, out double gb,
        out double br, out double bg, out double bb)
    {
        double radians = (Math.PI / 180.0) * hueShiftDegrees;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        rr = 0.299 + (0.701 * cos) + (0.168 * sin);
        rg = 0.587 - (0.587 * cos) + (0.330 * sin);
        rb = 0.114 - (0.114 * cos) - (0.497 * sin);

        gr = 0.299 - (0.299 * cos) - (0.328 * sin);
        gg = 0.587 + (0.413 * cos) + (0.035 * sin);
        gb = 0.114 - (0.114 * cos) + (0.292 * sin);

        br = 0.299 - (0.300 * cos) + (1.250 * sin);
        bg = 0.587 - (0.588 * cos) - (1.050 * sin);
        bb = 0.114 + (0.886 * cos) - (0.203 * sin);
    }

    private void DisposeResources()
    {
        DisposeHistoryResources();
        _parameterBuffer?.Dispose();
        _parameterBuffer = null;
        _injectShader?.Dispose();
        _injectShader = null;
        _injectRgbShader?.Dispose();
        _injectRgbShader = null;
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
        _rgbMaskSrv?.Dispose();
        _rgbMaskSrv = null;
        _rgbMaskTexture?.Dispose();
        _rgbMaskTexture = null;
        _colorTextureView?.Dispose();
        _colorTextureView = null;
        _colorUav?.Dispose();
        _colorUav = null;
        _colorTexture?.Dispose();
        _colorTexture = null;
        _colorStagingTexture?.Dispose();
        _colorStagingTexture = null;
        if (_colorSharedHandle != IntPtr.Zero)
        {
            CloseHandle(_colorSharedHandle);
            _colorSharedHandle = IntPtr.Zero;
        }
        _colorTextureDirty = true;
    }
}
