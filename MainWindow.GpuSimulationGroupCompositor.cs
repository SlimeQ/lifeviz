using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace lifeviz;

public partial class MainWindow
{
    private sealed class GpuSimulationGroupCompositor : IDisposable
    {
        private const int MaxSimulationLayers = 8;

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

        private readonly object _sync;
        private ID3D11Device1? _device;
        private ID3D11DeviceContext1? _context;
        private ID3D11VertexShader? _vertexShader;
        private ID3D11PixelShader? _pixelShader;
        private ID3D11SamplerState? _pointSamplerState;
        private ID3D11Buffer? _parametersBuffer;

        private readonly ID3D11Texture2D?[] _uploadTextures = new ID3D11Texture2D?[MaxSimulationLayers + 1];
        private readonly ID3D11ShaderResourceView?[] _uploadTextureViews = new ID3D11ShaderResourceView?[MaxSimulationLayers + 1];
        private readonly int[] _uploadTextureWidths = new int[MaxSimulationLayers + 1];
        private readonly int[] _uploadTextureHeights = new int[MaxSimulationLayers + 1];

        private ID3D11Texture2D? _outputTexture;
        private ID3D11ShaderResourceView? _outputTextureView;
        private ID3D11RenderTargetView? _outputRenderTargetView;
        private ID3D11Texture2D? _stagingTexture;
        private IntPtr _outputSharedHandle;
        private int _outputWidth;
        private int _outputHeight;
        private bool _gpuAvailable;

        public GpuSimulationGroupCompositor()
        {
            TryInitializeGpu();
            _sync = _gpuAvailable ? GpuSharedDevice.GetOrCreate().SyncRoot : new object();
        }

        public bool IsAvailable => _gpuAvailable;

        public CompositeFrame? Compose(
            IReadOnlyList<SimulationPresentationLayerData> layers,
            GpuCompositeSurface? underlaySurface,
            byte[]? underlayBuffer,
            int underlayWidth,
            int underlayHeight,
            int simulationBaseline,
            bool useSignedAddSubPassthrough,
            bool useMixedAddSubPassthroughModel,
            bool invertComposite,
            int width,
            int height,
            ref byte[]? downscaledBuffer,
            bool includeCpuReadback)
        {
            if (!_gpuAvailable ||
                layers.Count == 0 ||
                layers.Count > MaxSimulationLayers ||
                width <= 0 ||
                height <= 0)
            {
                return null;
            }

            int requiredLength = width * height * 4;
            if (includeCpuReadback && (downscaledBuffer == null || downscaledBuffer.Length != requiredLength))
            {
                downscaledBuffer = new byte[requiredLength];
            }

            lock (_sync)
            {
                EnsureOutputResources(width, height);
                if (_context == null ||
                    _vertexShader == null ||
                    _pixelShader == null ||
                    _pointSamplerState == null ||
                    _parametersBuffer == null ||
                    _outputTexture == null ||
                    _outputTextureView == null ||
                    _outputRenderTargetView == null)
                {
                    return null;
                }

                var resources = new ID3D11ShaderResourceView[MaxSimulationLayers + 1];
                for (int i = 0; i < layers.Count; i++)
                {
                    var resourceView = ResolveLayerResource(layers[i], i);
                    if (resourceView == null)
                    {
                        return null;
                    }

                    resources[i] = resourceView;
                }

                ID3D11ShaderResourceView? underlayResource = ResolveUnderlayResource(underlaySurface, underlayBuffer, underlayWidth, underlayHeight);
                resources[MaxSimulationLayers] = underlayResource ?? null!;

                var parameters = BuildFinalCompositeParameters(
                    layers,
                    resources[MaxSimulationLayers] != null,
                    simulationBaseline,
                    useSignedAddSubPassthrough,
                    useMixedAddSubPassthroughModel,
                    invertComposite,
                    width,
                    height);
                UploadConstants(_context, _parametersBuffer, parameters);

                _context.ClearRenderTargetView(_outputRenderTargetView, new Color4(0f, 0f, 0f, 1f));
                _context.OMSetRenderTargets(_outputRenderTargetView, null);
                _context.RSSetViewports(new[] { new Viewport(0, 0, width, height, 0.0f, 1.0f) });
                _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _context.IASetInputLayout(null);
                _context.VSSetShader(_vertexShader);
                _context.PSSetShader(_pixelShader);
                _context.PSSetSamplers(0, new[] { _pointSamplerState });
                _context.PSSetShaderResources(0, resources);
                _context.PSSetConstantBuffers(0, new[] { _parametersBuffer });
                _context.Draw(3, 0);
                _context.PSSetShaderResources(0, new ID3D11ShaderResourceView[MaxSimulationLayers + 1]);
                _context.PSSetShader(null);
                _context.VSSetShader(null);
                _context.OMSetRenderTargets(new ID3D11RenderTargetView[] { null! }, null);

                var surface = new GpuCompositeSurface(_outputTexture, _outputTextureView, _outputSharedHandle, width, height);
                if (includeCpuReadback && downscaledBuffer != null)
                {
                    ReadbackSurface(surface, downscaledBuffer, width, height);
                }

                return new CompositeFrame(includeCpuReadback && downscaledBuffer != null ? downscaledBuffer : Array.Empty<byte>(), width, height, surface);
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < _uploadTextures.Length; i++)
            {
                _uploadTextureViews[i]?.Dispose();
                _uploadTextureViews[i] = null;
                _uploadTextures[i]?.Dispose();
                _uploadTextures[i] = null;
                _uploadTextureWidths[i] = 0;
                _uploadTextureHeights[i] = 0;
            }

            if (_outputSharedHandle != IntPtr.Zero)
            {
                CloseHandle(_outputSharedHandle);
                _outputSharedHandle = IntPtr.Zero;
            }

            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _outputRenderTargetView?.Dispose();
            _outputRenderTargetView = null;
            _outputTextureView?.Dispose();
            _outputTextureView = null;
            _outputTexture?.Dispose();
            _outputTexture = null;
            _parametersBuffer?.Dispose();
            _parametersBuffer = null;
            _pointSamplerState?.Dispose();
            _pointSamplerState = null;
            _pixelShader?.Dispose();
            _pixelShader = null;
            _vertexShader?.Dispose();
            _vertexShader = null;
            _context = null;
            _device = null;
            _outputWidth = 0;
            _outputHeight = 0;
            _gpuAvailable = false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private void TryInitializeGpu()
        {
            try
            {
                var sharedDevice = GpuSharedDevice.GetOrCreate();
                _device = sharedDevice.Device;
                _context = sharedDevice.Context;
                _vertexShader = _device.CreateVertexShader(LoadShaderBytecode("Assets/GpuCompositeVS.cso"));
                _pixelShader = _device.CreatePixelShader(LoadShaderBytecode("Assets/GpuFinalCompositePS.cso"));
                _parametersBuffer = _device.CreateBuffer(
                    (uint)Marshal.SizeOf<FinalCompositeShaderParameters>(),
                    BindFlags.ConstantBuffer,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    ResourceOptionFlags.None,
                    0);
                _pointSamplerState = _device.CreateSamplerState(new SamplerDescription(
                    Filter.MinMagMipPoint,
                    TextureAddressMode.Clamp,
                    TextureAddressMode.Clamp,
                    TextureAddressMode.Clamp,
                    0.0f,
                    1,
                    ComparisonFunction.Never,
                    0.0f,
                    float.MaxValue));
                _gpuAvailable = true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"GPU sim-group compositor unavailable, using CPU inline fallback. {ex.Message}");
                _gpuAvailable = false;
                Dispose();
            }
        }

        private void EnsureOutputResources(int width, int height)
        {
            if (_device == null || width <= 0 || height <= 0)
            {
                return;
            }

            if (_outputTexture != null && _outputWidth == width && _outputHeight == height)
            {
                return;
            }

            if (_outputSharedHandle != IntPtr.Zero)
            {
                CloseHandle(_outputSharedHandle);
                _outputSharedHandle = IntPtr.Zero;
            }

            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _outputRenderTargetView?.Dispose();
            _outputRenderTargetView = null;
            _outputTextureView?.Dispose();
            _outputTextureView = null;
            _outputTexture?.Dispose();
            _outputTexture = null;

            _outputWidth = width;
            _outputHeight = height;

            var outputDescription = new Texture2DDescription(
                Format.B8G8R8A8_UNorm,
                (uint)width,
                (uint)height,
                1,
                1,
                BindFlags.ShaderResource | BindFlags.RenderTarget,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                1,
                0,
                ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle);
            _outputTexture = _device.CreateTexture2D(outputDescription);
            _outputTextureView = _device.CreateShaderResourceView(_outputTexture);
            _outputRenderTargetView = _device.CreateRenderTargetView(_outputTexture);
            using (var resource = _outputTexture.QueryInterface<IDXGIResource1>())
            {
                _outputSharedHandle = resource.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read, null);
            }

            var stagingDescription = new Texture2DDescription(
                Format.B8G8R8A8_UNorm,
                (uint)width,
                (uint)height,
                1,
                1,
                BindFlags.None,
                ResourceUsage.Staging,
                CpuAccessFlags.Read,
                1,
                0,
                ResourceOptionFlags.None);
            _stagingTexture = _device.CreateTexture2D(stagingDescription);
        }

        private ID3D11ShaderResourceView? ResolveLayerResource(SimulationPresentationLayerData layer, int index)
        {
            if (layer.Surface?.ShaderResourceView != null)
            {
                return layer.Surface.ShaderResourceView;
            }

            if (layer.Buffer == null || layer.Buffer.Length < layer.Width * layer.Height * 4)
            {
                return null;
            }

            return ResolveUploadedResource(index, layer.Buffer, layer.Width, layer.Height);
        }

        private ID3D11ShaderResourceView? ResolveUnderlayResource(
            GpuCompositeSurface? underlaySurface,
            byte[]? underlayBuffer,
            int underlayWidth,
            int underlayHeight)
        {
            if (underlaySurface?.ShaderResourceView != null)
            {
                return underlaySurface.ShaderResourceView;
            }

            if (underlayBuffer == null ||
                underlayWidth <= 0 ||
                underlayHeight <= 0 ||
                underlayBuffer.Length < underlayWidth * underlayHeight * 4)
            {
                return null;
            }

            return ResolveUploadedResource(MaxSimulationLayers, underlayBuffer, underlayWidth, underlayHeight);
        }

        private ID3D11ShaderResourceView? ResolveUploadedResource(int index, byte[] data, int width, int height)
        {
            if (_device == null || _context == null || width <= 0 || height <= 0)
            {
                return null;
            }

            if (_uploadTextures[index] == null ||
                _uploadTextureWidths[index] != width ||
                _uploadTextureHeights[index] != height)
            {
                _uploadTextureViews[index]?.Dispose();
                _uploadTextureViews[index] = null;
                _uploadTextures[index]?.Dispose();
                _uploadTextures[index] = null;

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
                    ResourceOptionFlags.None);
                _uploadTextures[index] = _device.CreateTexture2D(description);
                _uploadTextureViews[index] = _device.CreateShaderResourceView(_uploadTextures[index]!);
                _uploadTextureWidths[index] = width;
                _uploadTextureHeights[index] = height;
            }

            UploadTexture(_context, _uploadTextures[index]!, data, width, height);
            return _uploadTextureViews[index];
        }

        private void ReadbackSurface(GpuCompositeSurface surface, byte[] targetBuffer, int width, int height)
        {
            if (_context == null || _stagingTexture == null)
            {
                return;
            }

            _context.CopyResource(_stagingTexture, surface.Texture);
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowSize = width * 4;
                for (int row = 0; row < height; row++)
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
                context.UpdateSubresource(texture, 0, null, handle.AddrOfPinnedObject(), (uint)(width * 4), (uint)(width * height * 4));
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
    }
}
