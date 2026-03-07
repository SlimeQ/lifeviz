using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.Wpf;

namespace lifeviz;

internal sealed class GpuPresentationBackend : IDisposable
{
    private const int MaxSimulationLayers = 8;

    private enum PresentationBlendMode
    {
        Additive,
        Normal,
        Multiply,
        Screen,
        Overlay,
        Lighten,
        Darken,
        Subtractive
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositeShaderParameters
    {
        public int UseOverlay;
        public int BlendMode;
        public float Padding0;
        public float Padding1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FinalCompositeShaderParameters
    {
        public int LayerCount;
        public int UseUnderlay;
        public int UseSignedAddSubPassthrough;
        public int UseMixedAddSubPassthrough;

        public int InvertComposite;
        public float SimulationBaseline;
        public float SurfaceWidth;
        public float SurfaceHeight;

        public int BlendMode0;
        public int BlendMode1;
        public int BlendMode2;
        public int BlendMode3;

        public int BlendMode4;
        public int BlendMode5;
        public int BlendMode6;
        public int BlendMode7;

        public float Opacity0;
        public float Opacity1;
        public float Opacity2;
        public float Opacity3;

        public float Opacity4;
        public float Opacity5;
        public float Opacity6;
        public float Opacity7;

        public float HueCos0;
        public float HueCos1;
        public float HueCos2;
        public float HueCos3;

        public float HueCos4;
        public float HueCos5;
        public float HueCos6;
        public float HueCos7;

        public float HueSin0;
        public float HueSin1;
        public float HueSin2;
        public float HueSin3;

        public float HueSin4;
        public float HueSin5;
        public float HueSin6;
        public float HueSin7;
    }

    private readonly Grid _host;
    private readonly Image _fallbackImage;
    private readonly DrawingSurface _drawingSurface;
    private readonly object _sync = new();

    private byte[]? _pixelBuffer;
    private byte[]? _simulationUploadBuffer;
    private byte[]? _underlayUploadBuffer;

    private ID3D11Texture2D? _simulationTexture;
    private ID3D11Texture2D? _underlayTexture;
    private ID3D11ShaderResourceView? _simulationTextureView;
    private ID3D11ShaderResourceView? _underlayTextureView;
    private ID3D11VertexShader? _compositeVertexShader;
    private ID3D11PixelShader? _compositePixelShader;
    private ID3D11PixelShader? _finalCompositePixelShader;
    private ID3D11SamplerState? _pointSamplerState;
    private ID3D11Buffer? _compositeParametersBuffer;
    private ID3D11Buffer? _finalCompositeParametersBuffer;
    private readonly byte[][] _simulationLayerUploadBuffers = new byte[MaxSimulationLayers][];
    private readonly ID3D11Texture2D?[] _simulationLayerTextures = new ID3D11Texture2D?[MaxSimulationLayers];
    private readonly ID3D11ShaderResourceView?[] _simulationLayerTextureViews = new ID3D11ShaderResourceView?[MaxSimulationLayers];
    private readonly ID3D11Texture2D?[] _sharedSimulationLayerTextures = new ID3D11Texture2D?[MaxSimulationLayers];
    private readonly ID3D11ShaderResourceView?[] _sharedSimulationLayerTextureViews = new ID3D11ShaderResourceView?[MaxSimulationLayers];
    private readonly IntPtr[] _simulationLayerSharedHandles = new IntPtr[MaxSimulationLayers];
    private readonly IntPtr[] _openedSimulationLayerSharedHandles = new IntPtr[MaxSimulationLayers];
    private readonly bool[] _simulationLayerUsesSharedTexture = new bool[MaxSimulationLayers];
    private ID3D11Texture2D? _sharedUnderlayTexture;
    private ID3D11ShaderResourceView? _sharedUnderlayTextureView;
    private IntPtr _underlaySharedHandle;
    private IntPtr _openedUnderlaySharedHandle;
    private bool _underlayUsesSharedTexture;
    private int _simulationLayerCount;
    private bool _finalCompositePending;
    private bool _useFinalComposite;
    private FinalCompositeShaderParameters _finalCompositeParameters;

    private bool _simulationPending;
    private bool _underlayPending;
    private bool _useOverlay;
    private PresentationBlendMode _overlayBlendMode = PresentationBlendMode.Additive;
    private bool _gpuCompositePipelineReady;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private int _drawCount;
    private int _gpuCompositeDrawCount;
    private int _cpuFallbackDrawCount;
    private bool _disposed;

    public GpuPresentationBackend(Grid host, Image fallbackImage)
    {
        _host = host;
        _fallbackImage = fallbackImage;
        _drawingSurface = new DrawingSurface
        {
            AlwaysRefresh = false,
            Focusable = false,
            IsHitTestVisible = false,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };

        RenderOptions.SetBitmapScalingMode(_drawingSurface, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_drawingSurface, EdgeMode.Aliased);

        _drawingSurface.LoadContent += DrawingSurface_OnLoadContent;
        _drawingSurface.Draw += DrawingSurface_OnDraw;
        _drawingSurface.UnloadContent += DrawingSurface_OnUnloadContent;

        _host.Children.Clear();
        _host.Children.Add(_drawingSurface);
    }

    public int PixelWidth => _surfaceWidth;

    public int PixelHeight => _surfaceHeight;

    internal bool IsCompositePipelineReady => _gpuCompositePipelineReady;

    internal int DrawCount => _drawCount;

    internal int GpuCompositeDrawCount => _gpuCompositeDrawCount;

    internal int CpuFallbackDrawCount => _cpuFallbackDrawCount;

    internal static int CompositePipelineInitializationCount => _compositePipelineInitializationCount;

    private static int _compositePipelineInitializationCount;

    internal static void ResetSmokeCounters()
    {
        Interlocked.Exchange(ref _compositePipelineInitializationCount, 0);
    }

    public bool SupportsGpuSimulationComposition => true;

    public byte[]? EnsureSurface(int width, int height, bool force)
    {
        if (width <= 0 || height <= 0)
        {
            return _pixelBuffer;
        }

        bool needsResize = force || width != _surfaceWidth || height != _surfaceHeight;
        int requiredLength = width * height * 4;

        if (_pixelBuffer == null || _pixelBuffer.Length != requiredLength)
        {
            _pixelBuffer = new byte[requiredLength];
        }

        if (_simulationUploadBuffer == null || _simulationUploadBuffer.Length != requiredLength)
        {
            _simulationUploadBuffer = new byte[requiredLength];
        }

        if (_underlayUploadBuffer == null || _underlayUploadBuffer.Length != requiredLength)
        {
            _underlayUploadBuffer = new byte[requiredLength];
        }

        if (needsResize)
        {
            lock (_sync)
            {
                _surfaceWidth = width;
                _surfaceHeight = height;
                _simulationPending = false;
                _underlayPending = false;
                _finalCompositePending = false;
                _useFinalComposite = false;
                _simulationLayerCount = 0;
                DisposeTextureResources();
            }

            _drawingSurface.Width = width;
            _drawingSurface.Height = height;
        }

        return _pixelBuffer;
    }

    public void PresentFrame(byte[] pixelBuffer, int stride)
    {
        if (_surfaceWidth <= 0 || _surfaceHeight <= 0 || _simulationUploadBuffer == null)
        {
            return;
        }

        int requiredLength = stride * _surfaceHeight;
        if (_simulationUploadBuffer.Length != requiredLength)
        {
            _simulationUploadBuffer = new byte[requiredLength];
        }

        lock (_sync)
        {
            Buffer.BlockCopy(pixelBuffer, 0, _simulationUploadBuffer, 0, requiredLength);
            _simulationPending = true;
            _useFinalComposite = false;
        }

        _drawingSurface.Invalidate();
    }

    public void PresentUnderlay(byte[]? underlayBuffer, int stride)
    {
        if (underlayBuffer == null || _surfaceHeight <= 0 || _underlayUploadBuffer == null)
        {
            return;
        }

        int requiredLength = stride * _surfaceHeight;
        if (_underlayUploadBuffer.Length != requiredLength)
        {
            _underlayUploadBuffer = new byte[requiredLength];
        }

        lock (_sync)
        {
            Buffer.BlockCopy(underlayBuffer, 0, _underlayUploadBuffer, 0, requiredLength);
            _underlayPending = true;
        }

        _drawingSurface.Invalidate();
    }

    public void UpdateEffectState(bool useOverlay, double blendModeValue)
    {
        lock (_sync)
        {
            _useOverlay = useOverlay;
            _overlayBlendMode = ToBlendMode(blendModeValue);
        }

        _drawingSurface.Invalidate();
    }

    public bool PresentSimulationComposition(
        IReadOnlyList<SimulationPresentationLayerData> layers,
        byte[]? underlayBuffer,
        GpuCompositeSurface? underlaySurface,
        int simulationBaseline,
        bool useSignedAddSubPassthrough,
        bool useMixedAddSubPassthroughModel,
        bool invertComposite)
    {
        if (layers.Count == 0 || layers.Count > MaxSimulationLayers || _surfaceWidth <= 0 || _surfaceHeight <= 0)
        {
            return false;
        }

        int requiredLength = _surfaceWidth * _surfaceHeight * 4;
        lock (_sync)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                if (layer.Buffer != null && layer.Buffer.Length >= requiredLength)
                {
                    _simulationLayerUploadBuffers[i] ??= new byte[requiredLength];
                    if (_simulationLayerUploadBuffers[i].Length != requiredLength)
                    {
                        _simulationLayerUploadBuffers[i] = new byte[requiredLength];
                    }

                    Buffer.BlockCopy(layer.Buffer, 0, _simulationLayerUploadBuffers[i], 0, requiredLength);
                }

                if (layer.SharedTextureHandle != IntPtr.Zero &&
                    layer.Width == _surfaceWidth &&
                    layer.Height == _surfaceHeight)
                {
                    _simulationLayerUsesSharedTexture[i] = true;
                    _simulationLayerSharedHandles[i] = layer.SharedTextureHandle;
                    continue;
                }

                if (layer.Buffer == null || layer.Buffer.Length < requiredLength)
                {
                    return false;
                }

                _simulationLayerUsesSharedTexture[i] = false;
                _simulationLayerSharedHandles[i] = IntPtr.Zero;
                DisposeSharedSimulationLayerResource(i);
                if (layer.Buffer == null || layer.Buffer.Length < requiredLength)
                {
                    return false;
                }
            }

            for (int i = layers.Count; i < MaxSimulationLayers; i++)
            {
                _simulationLayerUsesSharedTexture[i] = false;
                _simulationLayerSharedHandles[i] = IntPtr.Zero;
                DisposeSharedSimulationLayerResource(i);
            }

            bool useSharedUnderlay = underlaySurface != null &&
                underlaySurface.SharedTextureHandle != IntPtr.Zero &&
                underlaySurface.Width == _surfaceWidth &&
                underlaySurface.Height == _surfaceHeight;
            if (useSharedUnderlay)
            {
                _underlayUsesSharedTexture = true;
                _underlaySharedHandle = underlaySurface!.SharedTextureHandle;
                _underlayPending = false;
            }
            else if (underlayBuffer != null)
            {
                if (underlayBuffer.Length < requiredLength)
                {
                    return false;
                }

                _underlayUploadBuffer ??= new byte[requiredLength];
                if (_underlayUploadBuffer.Length != requiredLength)
                {
                    _underlayUploadBuffer = new byte[requiredLength];
                }
                Buffer.BlockCopy(underlayBuffer, 0, _underlayUploadBuffer, 0, requiredLength);
                _underlayUsesSharedTexture = false;
                _underlaySharedHandle = IntPtr.Zero;
                DisposeSharedUnderlayResource();
                _underlayPending = true;
            }
            else
            {
                _underlayUsesSharedTexture = false;
                _underlaySharedHandle = IntPtr.Zero;
                DisposeSharedUnderlayResource();
                _underlayPending = false;
            }

            _simulationLayerCount = layers.Count;
            _finalCompositeParameters = BuildFinalCompositeParameters(
                layers,
                useSharedUnderlay || underlayBuffer != null,
                simulationBaseline,
                useSignedAddSubPassthrough,
                useMixedAddSubPassthroughModel,
                invertComposite,
                _surfaceWidth,
                _surfaceHeight);
            _finalCompositePending = true;
            _useFinalComposite = true;
            _simulationPending = false;
        }

        _drawingSurface.Invalidate();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _drawingSurface.LoadContent -= DrawingSurface_OnLoadContent;
        _drawingSurface.Draw -= DrawingSurface_OnDraw;
        _drawingSurface.UnloadContent -= DrawingSurface_OnUnloadContent;

        DisposeDeviceResources();

        if (_host.Children.Contains(_drawingSurface))
        {
            _host.Children.Clear();
            _host.Children.Add(_fallbackImage);
        }
    }

    private void DrawingSurface_OnLoadContent(object? sender, DrawingSurfaceEventArgs e)
    {
        lock (_sync)
        {
            EnsureTextureResources(e.Device);
            EnsureCompositePipeline(e.Device);
        }
    }

    private void DrawingSurface_OnDraw(object? sender, DrawEventArgs e)
    {
        lock (_sync)
        {
            if (_surfaceWidth <= 0 || _surfaceHeight <= 0 || _simulationUploadBuffer == null)
            {
                return;
            }

            EnsureTextureResources(e.Device);
            EnsureCompositePipeline(e.Device);

            if (_simulationTexture == null || e.Surface.ColorTexture == null)
            {
                return;
            }

            if (_simulationPending)
            {
                UploadTexture(e.Context, _simulationTexture, _simulationUploadBuffer, _surfaceWidth, _surfaceHeight);
                _simulationPending = false;
            }

            if (_underlayPending &&
                _underlayTexture != null &&
                _underlayUploadBuffer != null)
            {
                UploadTexture(e.Context, _underlayTexture, _underlayUploadBuffer, _surfaceWidth, _surfaceHeight);
                _underlayPending = false;
            }

            if (_useFinalComposite)
            {
                for (int i = 0; i < _simulationLayerCount; i++)
                {
                    if (_simulationLayerUsesSharedTexture[i])
                    {
                        EnsureSharedSimulationLayerResource(e.Device, i);
                    }
                }
                if (_underlayUsesSharedTexture)
                {
                    EnsureSharedUnderlayResource(e.Device);
                }
            }

            if (_useFinalComposite &&
                _finalCompositePixelShader != null &&
                _finalCompositeParametersBuffer != null &&
                _pointSamplerState != null &&
                AreSimulationLayerTexturesReady())
            {
                if (_finalCompositePending)
                {
                    for (int i = 0; i < _simulationLayerCount; i++)
                    {
                        if (_simulationLayerUsesSharedTexture[i])
                        {
                            EnsureSharedSimulationLayerResource(e.Device, i);
                        }
                        else if (_simulationLayerTextures[i] != null && _simulationLayerUploadBuffers[i] != null)
                        {
                            UploadTexture(e.Context, _simulationLayerTextures[i]!, _simulationLayerUploadBuffers[i], _surfaceWidth, _surfaceHeight);
                        }
                    }
                    if (_underlayUsesSharedTexture)
                    {
                        EnsureSharedUnderlayResource(e.Device);
                    }

                    _finalCompositePending = false;
                }

                RenderFinalCompositePass(e);
                _drawCount++;
                _gpuCompositeDrawCount++;
            }
            else if (_gpuCompositePipelineReady &&
                _compositeVertexShader != null &&
                _compositePixelShader != null &&
                _pointSamplerState != null &&
                _simulationTextureView != null &&
                _compositeParametersBuffer != null)
            {
                RenderCompositePass(e);
                _drawCount++;
                _gpuCompositeDrawCount++;
            }
            else
            {
                byte[]? fallbackBuffer = _simulationUploadBuffer;
                if (fallbackBuffer != null &&
                    _useOverlay &&
                    _underlayUploadBuffer != null &&
                    _underlayUploadBuffer.Length == fallbackBuffer.Length)
                {
                    ApplyOverlayBlend(fallbackBuffer, _underlayUploadBuffer, _overlayBlendMode, _surfaceWidth, _surfaceHeight);
                    UploadTexture(e.Context, _simulationTexture, fallbackBuffer, _surfaceWidth, _surfaceHeight);
                }

                e.Context.CopyResource(e.Surface.ColorTexture, _simulationTexture);
                _drawCount++;
                _cpuFallbackDrawCount++;
            }
        }
    }

    private void DrawingSurface_OnUnloadContent(object? sender, DrawingSurfaceEventArgs e)
    {
        lock (_sync)
        {
            DisposeDeviceResources();
        }
    }

    private void RenderCompositePass(DrawEventArgs e)
    {
        if (_simulationTextureView == null ||
            _compositeParametersBuffer == null ||
            _pointSamplerState == null ||
            _compositeVertexShader == null ||
            _compositePixelShader == null ||
            e.Surface.ColorTextureView == null)
        {
            return;
        }

        var parameters = new CompositeShaderParameters
        {
            UseOverlay = _useOverlay && _underlayTextureView != null ? 1 : 0,
            BlendMode = (int)_overlayBlendMode
        };
        UploadConstants(e.Context, _compositeParametersBuffer, parameters);

        e.Context.OMSetRenderTargets(e.Surface.ColorTextureView, null);
        e.Context.RSSetViewports(new[] { new Viewport(0, 0, _surfaceWidth, _surfaceHeight, 0.0f, 1.0f) });
        e.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        e.Context.IASetInputLayout(null);
        e.Context.VSSetShader(_compositeVertexShader);
        e.Context.PSSetShader(_compositePixelShader);
        e.Context.PSSetSamplers(0, new[] { _pointSamplerState });
        if (_underlayTextureView != null)
        {
            e.Context.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { _simulationTextureView, _underlayTextureView });
        }
        else
        {
            e.Context.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { _simulationTextureView });
        }
        e.Context.PSSetConstantBuffers(0, new[] { _compositeParametersBuffer });
        e.Context.Draw(3, 0);
    }

    private void RenderFinalCompositePass(DrawEventArgs e)
    {
        if (_compositeVertexShader == null ||
            _finalCompositePixelShader == null ||
            _finalCompositeParametersBuffer == null ||
            _pointSamplerState == null ||
            e.Surface.ColorTextureView == null)
        {
            return;
        }

        UploadConstants(e.Context, _finalCompositeParametersBuffer, _finalCompositeParameters);

        e.Context.OMSetRenderTargets(e.Surface.ColorTextureView, null);
        e.Context.RSSetViewports(new[] { new Viewport(0, 0, _surfaceWidth, _surfaceHeight, 0.0f, 1.0f) });
        e.Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        e.Context.IASetInputLayout(null);
        e.Context.VSSetShader(_compositeVertexShader);
        e.Context.PSSetShader(_finalCompositePixelShader);
        e.Context.PSSetSamplers(0, new[] { _pointSamplerState });

        var resources = new ID3D11ShaderResourceView[MaxSimulationLayers + 1];
        for (int i = 0; i < MaxSimulationLayers; i++)
        {
            resources[i] = (_simulationLayerUsesSharedTexture[i]
                ? _sharedSimulationLayerTextureViews[i]
                : _simulationLayerTextureViews[i])!;
        }
        resources[MaxSimulationLayers] = (_underlayUsesSharedTexture
            ? _sharedUnderlayTextureView
            : _underlayTextureView)!;
        e.Context.PSSetShaderResources(0, resources);
        e.Context.PSSetConstantBuffers(0, new[] { _finalCompositeParametersBuffer });
        e.Context.Draw(3, 0);
    }

    private void EnsureTextureResources(ID3D11Device1 device)
    {
        if (_surfaceWidth <= 0 || _surfaceHeight <= 0)
        {
            return;
        }

        if (!IsTextureValid(_simulationTexture) || !IsTextureValid(_underlayTexture))
        {
            DisposeTextureResources();

            var textureDescription = new Texture2DDescription(
                Format.B8G8R8A8_UNorm,
                (uint)_surfaceWidth,
                (uint)_surfaceHeight,
                1,
                1,
                BindFlags.ShaderResource,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                1,
                0,
                ResourceOptionFlags.None);

            _simulationTexture = device.CreateTexture2D(textureDescription);
            _underlayTexture = device.CreateTexture2D(textureDescription);
            _simulationTextureView = device.CreateShaderResourceView(_simulationTexture);
            _underlayTextureView = device.CreateShaderResourceView(_underlayTexture);
            for (int i = 0; i < MaxSimulationLayers; i++)
            {
                _simulationLayerTextures[i] = device.CreateTexture2D(textureDescription);
                _simulationLayerTextureViews[i] = device.CreateShaderResourceView(_simulationLayerTextures[i]!);
            }
        }
    }

    private void EnsureSharedSimulationLayerResource(ID3D11Device1 device, int index)
    {
        if (index < 0 || index >= MaxSimulationLayers)
        {
            return;
        }

        if (!_simulationLayerUsesSharedTexture[index] || _simulationLayerSharedHandles[index] == IntPtr.Zero)
        {
            DisposeSharedSimulationLayerResource(index);
            return;
        }

        if (_sharedSimulationLayerTextureViews[index] != null &&
            _sharedSimulationLayerTextures[index] != null &&
            _openedSimulationLayerSharedHandles[index] == _simulationLayerSharedHandles[index])
        {
            return;
        }

        DisposeSharedSimulationLayerResource(index);

        try
        {
            _sharedSimulationLayerTextures[index] = device.OpenSharedResource<ID3D11Texture2D>(_simulationLayerSharedHandles[index]);
            _sharedSimulationLayerTextureViews[index] = device.CreateShaderResourceView(_sharedSimulationLayerTextures[index]!);
            _openedSimulationLayerSharedHandles[index] = _simulationLayerSharedHandles[index];
        }
        catch (Exception ex)
        {
            DisposeSharedSimulationLayerResource(index);
            _simulationLayerUsesSharedTexture[index] = false;
            Logger.Warn($"Failed to open shared simulation layer resource {index}; falling back to CPU-uploaded layer buffer. {ex.Message}");
        }
    }

    private bool IsTextureValid(ID3D11Texture2D? texture)
    {
        if (texture == null)
        {
            return false;
        }

        var description = texture.Description;
        return description.Width == _surfaceWidth &&
               description.Height == _surfaceHeight &&
               description.Format == Format.B8G8R8A8_UNorm;
    }

    private void EnsureCompositePipeline(ID3D11Device1 device)
    {
        if (_gpuCompositePipelineReady)
        {
            return;
        }

        try
        {
            byte[] vertexShaderBytes = LoadShaderBytecode("Assets/GpuCompositeVS.cso");
            byte[] pixelShaderBytes = LoadShaderBytecode("Assets/GpuCompositePS.cso");
            byte[] finalCompositePixelShaderBytes = LoadShaderBytecode("Assets/GpuFinalCompositePS.cso");

            _compositeVertexShader = device.CreateVertexShader(vertexShaderBytes);
            _compositePixelShader = device.CreatePixelShader(pixelShaderBytes);
            _finalCompositePixelShader = device.CreatePixelShader(finalCompositePixelShaderBytes);
            _pointSamplerState = device.CreateSamplerState(new SamplerDescription(
                Filter.MinMagMipPoint,
                TextureAddressMode.Clamp,
                TextureAddressMode.Clamp,
                TextureAddressMode.Clamp,
                0.0f,
                1,
                ComparisonFunction.Never,
                0.0f,
                float.MaxValue));
            _compositeParametersBuffer = device.CreateBuffer(
                (uint)Marshal.SizeOf<CompositeShaderParameters>(),
                BindFlags.ConstantBuffer,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.None,
                0);
            _finalCompositeParametersBuffer = device.CreateBuffer(
                (uint)Marshal.SizeOf<FinalCompositeShaderParameters>(),
                BindFlags.ConstantBuffer,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.None,
                0);

            _gpuCompositePipelineReady = true;
            Interlocked.Increment(ref _compositePipelineInitializationCount);
            Logger.Info("GPU composite pass initialized.");
        }
        catch (Exception ex)
        {
            if (!_gpuCompositePipelineReady)
            {
                Logger.Warn($"GPU composite pass unavailable, using CPU fallback blend. {ex.Message}");
            }
            DisposeCompositePipeline();
        }
    }

    private bool AreSimulationLayerTexturesReady()
    {
        if (_simulationLayerCount <= 0 || _simulationLayerCount > MaxSimulationLayers)
        {
            return false;
        }

        if (_finalCompositeParameters.UseUnderlay != 0)
        {
            if (_underlayUsesSharedTexture)
            {
                if (_sharedUnderlayTextureView == null)
                {
                    return false;
                }
            }
            else if (_underlayTextureView == null)
            {
                return false;
            }
        }

        for (int i = 0; i < _simulationLayerCount; i++)
        {
            if (_simulationLayerUsesSharedTexture[i])
            {
                if (_sharedSimulationLayerTextureViews[i] == null)
                {
                    return false;
                }
            }
            else if (_simulationLayerTextures[i] == null || _simulationLayerTextureViews[i] == null || _simulationLayerUploadBuffers[i] == null)
            {
                return false;
            }
        }

        return true;
    }

    private void EnsureSharedUnderlayResource(ID3D11Device1 device)
    {
        if (!_underlayUsesSharedTexture || _underlaySharedHandle == IntPtr.Zero)
        {
            DisposeSharedUnderlayResource();
            return;
        }

        if (_sharedUnderlayTextureView != null &&
            _sharedUnderlayTexture != null &&
            _openedUnderlaySharedHandle == _underlaySharedHandle)
        {
            return;
        }

        DisposeSharedUnderlayResource();
        _sharedUnderlayTexture = device.OpenSharedResource1<ID3D11Texture2D>(_underlaySharedHandle);
        _sharedUnderlayTextureView = device.CreateShaderResourceView(_sharedUnderlayTexture);
        _openedUnderlaySharedHandle = _underlaySharedHandle;
    }

    private static FinalCompositeShaderParameters BuildFinalCompositeParameters(
        IReadOnlyList<SimulationPresentationLayerData> layers,
        bool useUnderlay,
        int simulationBaseline,
        bool useSignedAddSubPassthrough,
        bool useMixedAddSubPassthroughModel,
        bool invertComposite,
        int width,
        int height)
    {
        var parameters = new FinalCompositeShaderParameters
        {
            LayerCount = layers.Count,
            UseUnderlay = useUnderlay ? 1 : 0,
            UseSignedAddSubPassthrough = useSignedAddSubPassthrough ? 1 : 0,
            UseMixedAddSubPassthrough = useMixedAddSubPassthroughModel ? 1 : 0,
            InvertComposite = invertComposite ? 1 : 0,
            SimulationBaseline = Math.Clamp(simulationBaseline / 255.0f, 0.0f, 1.0f),
            SurfaceWidth = width,
            SurfaceHeight = height
        };

        for (int i = 0; i < layers.Count && i < MaxSimulationLayers; i++)
        {
            int blendMode = layers[i].BlendMode;
            float opacity = Math.Clamp(layers[i].Opacity, 0.0f, 1.0f);
            float hueRadians = Math.Clamp(layers[i].HueShiftDegrees, -360.0f, 360.0f) * (MathF.PI / 180.0f);
            float hueCos = MathF.Cos(hueRadians);
            float hueSin = MathF.Sin(hueRadians);
            switch (i)
            {
                case 0:
                    parameters.BlendMode0 = blendMode;
                    parameters.Opacity0 = opacity;
                    parameters.HueCos0 = hueCos;
                    parameters.HueSin0 = hueSin;
                    break;
                case 1:
                    parameters.BlendMode1 = blendMode;
                    parameters.Opacity1 = opacity;
                    parameters.HueCos1 = hueCos;
                    parameters.HueSin1 = hueSin;
                    break;
                case 2:
                    parameters.BlendMode2 = blendMode;
                    parameters.Opacity2 = opacity;
                    parameters.HueCos2 = hueCos;
                    parameters.HueSin2 = hueSin;
                    break;
                case 3:
                    parameters.BlendMode3 = blendMode;
                    parameters.Opacity3 = opacity;
                    parameters.HueCos3 = hueCos;
                    parameters.HueSin3 = hueSin;
                    break;
                case 4:
                    parameters.BlendMode4 = blendMode;
                    parameters.Opacity4 = opacity;
                    parameters.HueCos4 = hueCos;
                    parameters.HueSin4 = hueSin;
                    break;
                case 5:
                    parameters.BlendMode5 = blendMode;
                    parameters.Opacity5 = opacity;
                    parameters.HueCos5 = hueCos;
                    parameters.HueSin5 = hueSin;
                    break;
                case 6:
                    parameters.BlendMode6 = blendMode;
                    parameters.Opacity6 = opacity;
                    parameters.HueCos6 = hueCos;
                    parameters.HueSin6 = hueSin;
                    break;
                case 7:
                    parameters.BlendMode7 = blendMode;
                    parameters.Opacity7 = opacity;
                    parameters.HueCos7 = hueCos;
                    parameters.HueSin7 = hueSin;
                    break;
            }
        }

        return parameters;
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

    private static void UploadTexture(ID3D11DeviceContext1 context, ID3D11Texture2D texture, byte[] data, int width, int height)
    {
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            context.UpdateSubresource(
                texture,
                0,
                null,
                handle.AddrOfPinnedObject(),
                (uint)(width * 4),
                (uint)(width * height * 4));
        }
        finally
        {
            handle.Free();
        }
    }

    private static void UploadConstants<T>(ID3D11DeviceContext1 context, ID3D11Buffer buffer, T parameters) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr native = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(parameters, native, false);
            context.UpdateSubresource(buffer, 0, null, native, (uint)size, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    private void DisposeDeviceResources()
    {
        DisposeTextureResources();
        DisposeCompositePipeline();
    }

    private void DisposeTextureResources()
    {
        DisposeSharedUnderlayResource();
        _simulationTextureView?.Dispose();
        _simulationTextureView = null;
        _underlayTextureView?.Dispose();
        _underlayTextureView = null;
        _simulationTexture?.Dispose();
        _simulationTexture = null;
        _underlayTexture?.Dispose();
        _underlayTexture = null;
        for (int i = 0; i < MaxSimulationLayers; i++)
        {
            DisposeSharedSimulationLayerResource(i);
            _simulationLayerTextureViews[i]?.Dispose();
            _simulationLayerTextureViews[i] = null;
            _simulationLayerTextures[i]?.Dispose();
            _simulationLayerTextures[i] = null;
        }
    }

    private void DisposeSharedUnderlayResource()
    {
        _sharedUnderlayTextureView?.Dispose();
        _sharedUnderlayTextureView = null;
        _sharedUnderlayTexture?.Dispose();
        _sharedUnderlayTexture = null;
        _openedUnderlaySharedHandle = IntPtr.Zero;
    }

    private void DisposeSharedSimulationLayerResource(int index)
    {
        _sharedSimulationLayerTextureViews[index]?.Dispose();
        _sharedSimulationLayerTextureViews[index] = null;
        _sharedSimulationLayerTextures[index]?.Dispose();
        _sharedSimulationLayerTextures[index] = null;
        _openedSimulationLayerSharedHandles[index] = IntPtr.Zero;
    }

    private void DisposeCompositePipeline()
    {
        _gpuCompositePipelineReady = false;
        _pointSamplerState?.Dispose();
        _pointSamplerState = null;
        _compositeParametersBuffer?.Dispose();
        _compositeParametersBuffer = null;
        _finalCompositeParametersBuffer?.Dispose();
        _finalCompositeParametersBuffer = null;
        _compositeVertexShader?.Dispose();
        _compositeVertexShader = null;
        _compositePixelShader?.Dispose();
        _compositePixelShader = null;
        _finalCompositePixelShader?.Dispose();
        _finalCompositePixelShader = null;
    }

    private static void ApplyOverlayBlend(byte[] targetBuffer, byte[] overlayBuffer, PresentationBlendMode blendMode, int width, int height)
    {
        int stride = width * 4;
        for (int row = 0; row < height; row++)
        {
            int rowOffset = row * stride;
            for (int col = 0; col < width; col++)
            {
                int index = rowOffset + (col * 4);
                BlendInto(
                    targetBuffer,
                    index,
                    overlayBuffer[index],
                    overlayBuffer[index + 1],
                    overlayBuffer[index + 2],
                    overlayBuffer[index + 3],
                    blendMode,
                    1.0);
            }
        }
    }

    private static PresentationBlendMode ToBlendMode(double blendModeValue)
    {
        int mode = (int)Math.Round(blendModeValue);
        return mode switch
        {
            0 => PresentationBlendMode.Additive,
            1 => PresentationBlendMode.Normal,
            2 => PresentationBlendMode.Multiply,
            3 => PresentationBlendMode.Screen,
            4 => PresentationBlendMode.Overlay,
            5 => PresentationBlendMode.Lighten,
            6 => PresentationBlendMode.Darken,
            7 => PresentationBlendMode.Subtractive,
            _ => PresentationBlendMode.Additive
        };
    }

    private static void BlendInto(byte[] destination, int destIndex, byte sb, byte sg, byte sr, byte sa, PresentationBlendMode mode, double opacity)
    {
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        byte db = destination[destIndex];
        byte dg = destination[destIndex + 1];
        byte dr = destination[destIndex + 2];

        int b;
        int g;
        int r;

        switch (mode)
        {
            case PresentationBlendMode.Additive:
                b = db + sb;
                g = dg + sg;
                r = dr + sr;
                break;
            case PresentationBlendMode.Normal:
            {
                double alpha = (sa / 255.0) * opacity;
                destination[destIndex] = ClampToByte((int)(db + ((sb - db) * alpha)));
                destination[destIndex + 1] = ClampToByte((int)(dg + ((sg - dg) * alpha)));
                destination[destIndex + 2] = ClampToByte((int)(dr + ((sr - dr) * alpha)));
                destination[destIndex + 3] = 255;
                return;
            }
            case PresentationBlendMode.Multiply:
                b = db * sb / 255;
                g = dg * sg / 255;
                r = dr * sr / 255;
                break;
            case PresentationBlendMode.Screen:
                b = 255 - ((255 - db) * (255 - sb) / 255);
                g = 255 - ((255 - dg) * (255 - sg) / 255);
                r = 255 - ((255 - dr) * (255 - sr) / 255);
                break;
            case PresentationBlendMode.Overlay:
                b = db < 128 ? (2 * db * sb) / 255 : 255 - (2 * (255 - db) * (255 - sb) / 255);
                g = dg < 128 ? (2 * dg * sg) / 255 : 255 - (2 * (255 - dg) * (255 - sg) / 255);
                r = dr < 128 ? (2 * dr * sr) / 255 : 255 - (2 * (255 - dr) * (255 - sr) / 255);
                break;
            case PresentationBlendMode.Lighten:
                b = Math.Max(db, sb);
                g = Math.Max(dg, sg);
                r = Math.Max(dr, sr);
                break;
            case PresentationBlendMode.Darken:
                b = Math.Min(db, sb);
                g = Math.Min(dg, sg);
                r = Math.Min(dr, sr);
                break;
            case PresentationBlendMode.Subtractive:
                b = db - sb;
                g = dg - sg;
                r = dr - sr;
                break;
            default:
                b = sb;
                g = sg;
                r = sr;
                break;
        }

        destination[destIndex] = ClampToByte((int)(db + ((b - db) * opacity)));
        destination[destIndex + 1] = ClampToByte((int)(dg + ((g - dg) * opacity)));
        destination[destIndex + 2] = ClampToByte((int)(dr + ((r - dr) * opacity)));
        destination[destIndex + 3] = 255;
    }

    private static byte ClampToByte(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
