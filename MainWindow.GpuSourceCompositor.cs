using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace lifeviz;

public partial class MainWindow
{
    private sealed class GpuSourceCompositor : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct SourceCompositeParameters
        {
            public float DestWidth;
            public float DestHeight;
            public float SourceWidth;
            public float SourceHeight;

            public float ScaleX;
            public float ScaleY;
            public float OffsetX;
            public float OffsetY;

            public float ScaledWidth;
            public float ScaledHeight;
            public float Opacity;
            public float Tolerance;

            public float M11;
            public float M12;
            public float M21;
            public float M22;

            public float TransformOffsetX;
            public float TransformOffsetY;
            public float KeyB;
            public float KeyG;

            public float KeyR;
            public uint FitMode;
            public uint BlendMode;
            public uint Flags;
        }

        private const uint FlagMirror = 1u << 0;
        private const uint FlagUseAlpha = 1u << 1;
        private const uint FlagKeyEnabled = 1u << 2;
        private const uint FlagFirstLayer = 1u << 3;

        private readonly MainWindow _owner;
        private object _sync = new();
        private GpuSharedDevice? _sharedDevice;

        private ID3D11Device1? _device;
        private ID3D11DeviceContext1? _context;
        private ID3D11VertexShader? _vertexShader;
        private ID3D11PixelShader? _pixelShader;
        private ID3D11Buffer? _parameterBuffer;
        private ID3D11SamplerState? _linearClampSampler;
        private ID3D11SamplerState? _linearWrapSampler;

        private ID3D11Texture2D? _sourceTexture;
        private ID3D11ShaderResourceView? _sourceSrv;
        private int _sourceWidth;
        private int _sourceHeight;

        private ID3D11Texture2D? _compositeTextureA;
        private ID3D11Texture2D? _compositeTextureB;
        private ID3D11ShaderResourceView? _compositeSrvA;
        private ID3D11ShaderResourceView? _compositeSrvB;
        private ID3D11RenderTargetView? _compositeRtvA;
        private ID3D11RenderTargetView? _compositeRtvB;
        private IntPtr _compositeSharedHandleA;
        private IntPtr _compositeSharedHandleB;
        private ID3D11Texture2D? _stagingTexture;
        private int _destWidth;
        private int _destHeight;

        private bool _gpuAvailable;

        private static int _compositePassCount;
        private static long _buildCount;
        private static long _uploadTicks;
        private static long _drawTicks;
        private static long _readbackTicks;
        internal static int CompositePassCount => _compositePassCount;

        internal static void ResetSmokeCounters()
        {
            Interlocked.Exchange(ref _compositePassCount, 0);
            Interlocked.Exchange(ref _buildCount, 0);
            Interlocked.Exchange(ref _uploadTicks, 0);
            Interlocked.Exchange(ref _drawTicks, 0);
            Interlocked.Exchange(ref _readbackTicks, 0);
        }

        internal static (int passCount, long buildCount, double uploadMs, double drawMs, double readbackMs) GetSmokeStats()
        {
            double tickScale = 1000.0 / Stopwatch.Frequency;
            return (
                _compositePassCount,
                _buildCount,
                _uploadTicks * tickScale,
                _drawTicks * tickScale,
                _readbackTicks * tickScale);
        }

        public GpuSourceCompositor(MainWindow owner)
        {
            _owner = owner;
            TryInitializeGpu();
        }

        public bool IsAvailable => _gpuAvailable;

        public CompositeFrame? BuildCompositeFrame(List<CaptureSource> sources, ref byte[]? downscaledBuffer, bool useEngineDimensions, double animationTime, bool includeCpuReadback = true)
        {
            if (!_gpuAvailable || sources.Count == 0)
            {
                return null;
            }

            if (!_owner.TryGetDownscaledDimensions(sources, useEngineDimensions, out int downscaledWidth, out int downscaledHeight))
            {
                return null;
            }

            int downscaledLength = downscaledWidth * downscaledHeight * 4;
            if (includeCpuReadback && (downscaledBuffer == null || downscaledBuffer.Length != downscaledLength))
            {
                downscaledBuffer = new byte[downscaledLength];
            }

            lock (_sync)
            {
                EnsureCompositeResources(downscaledWidth, downscaledHeight);
                if (_context == null ||
                    _vertexShader == null ||
                    _pixelShader == null ||
                    _parameterBuffer == null ||
                    _linearClampSampler == null ||
                    _linearWrapSampler == null ||
                    _compositeTextureA == null ||
                    _compositeTextureB == null ||
                    _compositeSrvA == null ||
                    _compositeSrvB == null ||
                    _compositeRtvA == null ||
                    _compositeRtvB == null ||
                    _stagingTexture == null)
                {
                    return null;
                }

                _context.ClearRenderTargetView(_compositeRtvA, new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));
                _context.ClearRenderTargetView(_compositeRtvB, new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));

                bool wroteDownscaled = false;
                bool currentIsA = true;

                foreach (var source in sources)
                {
                    var frame = source.LastFrame;
                    if (frame == null)
                    {
                        continue;
                    }

                    if (source.Type == CaptureSource.SourceType.Window && source.Window != null)
                    {
                        source.Window = source.Window.WithDimensions(frame.SourceWidth, frame.SourceHeight);
                    }

                    var downscaledTransform = _owner.BuildAnimationTransform(source, downscaledWidth, downscaledHeight, animationTime);
                    double animationOpacity = _owner.BuildAnimationOpacity(source, animationTime);
                    double effectiveOpacity = Math.Clamp(source.Opacity * animationOpacity, 0.0, 1.0);
                    var keying = new KeyingSettings(
                        source.KeyEnabled && source.BlendMode == BlendMode.Normal,
                        source.BlendMode == BlendMode.Normal,
                        source.KeyColorR,
                        source.KeyColorG,
                        source.KeyColorB,
                        source.KeyTolerance);

                    int sourceWidth = frame.Source != null ? frame.SourceWidth : frame.DownscaledWidth;
                    int sourceHeight = frame.Source != null ? frame.SourceHeight : frame.DownscaledHeight;
                    EnsureSourceResource(sourceWidth, sourceHeight);
                    if (_sourceTexture == null || _sourceSrv == null)
                    {
                        continue;
                    }
                    byte[] sourcePixels = frame.Source ?? frame.Downscaled;

                    long uploadStart = Stopwatch.GetTimestamp();
                    UploadTexture(_context, _sourceTexture, sourcePixels, sourceWidth, sourceHeight);
                    Interlocked.Add(ref _uploadTicks, Stopwatch.GetTimestamp() - uploadStart);

                    long drawStart = Stopwatch.GetTimestamp();
                    DrawSourceIntoComposite(
                        sourceWidth,
                        sourceHeight,
                        downscaledWidth,
                        downscaledHeight,
                        source.BlendMode,
                        effectiveOpacity,
                        source.Mirror && source.Type == CaptureSource.SourceType.Webcam,
                        source.FitMode,
                        downscaledTransform,
                        keying,
                        isFirstLayer: !wroteDownscaled,
                        currentIsA);
                    Interlocked.Add(ref _drawTicks, Stopwatch.GetTimestamp() - drawStart);

                    currentIsA = !currentIsA;
                    wroteDownscaled = true;
                    Interlocked.Increment(ref _compositePassCount);
                }

                if (!wroteDownscaled)
                {
                    return null;
                }

                ID3D11Texture2D currentTexture = currentIsA ? _compositeTextureA : _compositeTextureB;
                ID3D11ShaderResourceView currentSrv = currentIsA ? _compositeSrvA : _compositeSrvB;
                _context.OMSetRenderTargets(new ID3D11RenderTargetView[] { null! }, null);
                if (includeCpuReadback && downscaledBuffer != null)
                {
                    long readbackStart = Stopwatch.GetTimestamp();
                    _context.CopyResource(_stagingTexture, currentTexture);
                    var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        int rowSize = downscaledWidth * 4;
                        for (int row = 0; row < downscaledHeight; row++)
                        {
                            IntPtr sourcePtr = IntPtr.Add(mapped.DataPointer, checked((int)(row * mapped.RowPitch)));
                            Marshal.Copy(sourcePtr, downscaledBuffer, row * rowSize, rowSize);
                        }
                    }
                    finally
                    {
                        _context.Unmap(_stagingTexture, 0);
                    }
                    Interlocked.Add(ref _readbackTicks, Stopwatch.GetTimestamp() - readbackStart);
                }
                Interlocked.Increment(ref _buildCount);

                IntPtr currentSharedHandle = currentIsA ? _compositeSharedHandleA : _compositeSharedHandleB;
                return new CompositeFrame(
                    includeCpuReadback && downscaledBuffer != null ? downscaledBuffer : Array.Empty<byte>(),
                    downscaledWidth,
                    downscaledHeight,
                    new GpuCompositeSurface(currentTexture, currentSrv, currentSharedHandle, downscaledWidth, downscaledHeight));
            }
        }

        public void Dispose()
        {
            DisposeCompositeResources();
            DisposeSourceResources();
            _linearClampSampler?.Dispose();
            _linearClampSampler = null;
            _linearWrapSampler?.Dispose();
            _linearWrapSampler = null;
            _parameterBuffer?.Dispose();
            _parameterBuffer = null;
            _vertexShader?.Dispose();
            _vertexShader = null;
            _pixelShader?.Dispose();
            _pixelShader = null;
            _context = null;
            _device = null;
            _sharedDevice = null;
        }

        private void TryInitializeGpu()
        {
            try
            {
                _sharedDevice = GpuSharedDevice.GetOrCreate();
                Logger.Info($"GPU source compositor device created at feature level {_sharedDevice.FeatureLevel}.");
                _device = _sharedDevice.Device;
                _context = _sharedDevice.Context;
                _sync = _sharedDevice.SyncRoot;

                _vertexShader = _device.CreateVertexShader(LoadShaderBytecode("Assets/GpuSourceCompositeVS.cso"));
                _pixelShader = _device.CreatePixelShader(LoadShaderBytecode("Assets/GpuSourceCompositePS.cso"));
                _parameterBuffer = _device.CreateBuffer(
                    (uint)Marshal.SizeOf<SourceCompositeParameters>(),
                    BindFlags.ConstantBuffer,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    ResourceOptionFlags.None,
                    0);
                _linearClampSampler = _device.CreateSamplerState(new SamplerDescription(
                    Filter.MinMagMipLinear,
                    TextureAddressMode.Clamp,
                    TextureAddressMode.Clamp,
                    TextureAddressMode.Clamp,
                    0.0f,
                    1,
                    ComparisonFunction.Never,
                    0.0f,
                    float.MaxValue));
                _linearWrapSampler = _device.CreateSamplerState(new SamplerDescription(
                    Filter.MinMagMipLinear,
                    TextureAddressMode.Wrap,
                    TextureAddressMode.Wrap,
                    TextureAddressMode.Wrap,
                    0.0f,
                    1,
                    ComparisonFunction.Never,
                    0.0f,
                    float.MaxValue));
                _gpuAvailable = true;
                Logger.Info("GPU source compositor initialized.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"GPU source compositor unavailable, using CPU composite fallback. {ex.Message}");
                _gpuAvailable = false;
                Dispose();
            }
        }

        private void EnsureSourceResource(int width, int height)
        {
            if (_device == null || width <= 0 || height <= 0)
            {
                return;
            }

            if (_sourceTexture != null && _sourceWidth == width && _sourceHeight == height)
            {
                return;
            }

            DisposeSourceResources();
            _sourceWidth = width;
            _sourceHeight = height;

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
            _sourceTexture = _device.CreateTexture2D(description);
            _sourceSrv = _device.CreateShaderResourceView(_sourceTexture);
        }

        private void EnsureCompositeResources(int width, int height)
        {
            if (_device == null || width <= 0 || height <= 0)
            {
                return;
            }

            if (_compositeTextureA != null && _destWidth == width && _destHeight == height)
            {
                return;
            }

            DisposeCompositeResources();
            _destWidth = width;
            _destHeight = height;

            var compositeDescription = new Texture2DDescription(
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
            _compositeTextureA = _device.CreateTexture2D(compositeDescription);
            _compositeTextureB = _device.CreateTexture2D(compositeDescription);
            _compositeSrvA = _device.CreateShaderResourceView(_compositeTextureA);
            _compositeSrvB = _device.CreateShaderResourceView(_compositeTextureB);
            _compositeRtvA = _device.CreateRenderTargetView(_compositeTextureA);
            _compositeRtvB = _device.CreateRenderTargetView(_compositeTextureB);
            using (var compositeResourceA = _compositeTextureA.QueryInterface<IDXGIResource1>())
            {
                _compositeSharedHandleA = compositeResourceA.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read, null);
            }
            using (var compositeResourceB = _compositeTextureB.QueryInterface<IDXGIResource1>())
            {
                _compositeSharedHandleB = compositeResourceB.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read, null);
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

        private void DrawSourceIntoComposite(
            int sourceWidth,
            int sourceHeight,
            int destWidth,
            int destHeight,
            BlendMode blendMode,
            double opacity,
            bool mirror,
            FitMode fitMode,
            Transform2D transform,
            in KeyingSettings keying,
            bool isFirstLayer,
            bool currentIsA)
        {
            if (_context == null ||
                _vertexShader == null ||
                _pixelShader == null ||
                _parameterBuffer == null ||
                _linearClampSampler == null ||
                _linearWrapSampler == null ||
                _sourceSrv == null ||
                _compositeSrvA == null ||
                _compositeSrvB == null ||
                _compositeRtvA == null ||
                _compositeRtvB == null)
            {
                return;
            }

            var mapping = ImageFit.GetMapping(fitMode, sourceWidth, sourceHeight, destWidth, destHeight);
            uint flags = 0;
            if (mirror)
            {
                flags |= FlagMirror;
            }

            if (keying.UseAlpha)
            {
                flags |= FlagUseAlpha;
            }

            if (keying.Enabled)
            {
                flags |= FlagKeyEnabled;
            }

            if (isFirstLayer)
            {
                flags |= FlagFirstLayer;
            }

            var parameters = new SourceCompositeParameters
            {
                DestWidth = destWidth,
                DestHeight = destHeight,
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                ScaleX = (float)mapping.ScaleX,
                ScaleY = (float)mapping.ScaleY,
                OffsetX = (float)mapping.OffsetX,
                OffsetY = (float)mapping.OffsetY,
                ScaledWidth = (float)mapping.ScaledWidth,
                ScaledHeight = (float)mapping.ScaledHeight,
                Opacity = (float)Math.Clamp(opacity, 0.0, 1.0),
                Tolerance = (float)Math.Clamp(keying.Tolerance, 0.0, 1.0),
                M11 = (float)transform.M11,
                M12 = (float)transform.M12,
                M21 = (float)transform.M21,
                M22 = (float)transform.M22,
                TransformOffsetX = (float)transform.OffsetX,
                TransformOffsetY = (float)transform.OffsetY,
                KeyB = keying.B / 255.0f,
                KeyG = keying.G / 255.0f,
                KeyR = keying.R / 255.0f,
                FitMode = (uint)mapping.Mode,
                BlendMode = (uint)blendMode,
                Flags = flags
            };

            ID3D11ShaderResourceView currentCompositeSrv = currentIsA ? _compositeSrvA : _compositeSrvB;
            ID3D11RenderTargetView targetRtv = currentIsA ? _compositeRtvB : _compositeRtvA;
            UpdateConstants(_context, _parameterBuffer, parameters);

            _context.OMSetRenderTargets(targetRtv, null);
            _context.RSSetViewports(new[] { new Vortice.Mathematics.Viewport(0, 0, destWidth, destHeight, 0.0f, 1.0f) });
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.IASetInputLayout(null);
            _context.VSSetShader(_vertexShader);
            _context.PSSetShader(_pixelShader);
            _context.PSSetShaderResources(0, new[] { currentCompositeSrv, _sourceSrv });
            _context.PSSetSamplers(0, new[] { _linearClampSampler, _linearWrapSampler });
            _context.PSSetConstantBuffers(0, new[] { _parameterBuffer });
            _context.Draw(3, 0);
            _context.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
            _context.PSSetShader(null);
            _context.VSSetShader(null);
            _context.OMSetRenderTargets(new ID3D11RenderTargetView[] { null! }, null);
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

        private static void UpdateConstants(ID3D11DeviceContext1 context, ID3D11Buffer buffer, SourceCompositeParameters parameters)
        {
            int size = Marshal.SizeOf<SourceCompositeParameters>();
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

        private static byte[] LoadShaderBytecode(string relativePath)
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri(relativePath, UriKind.Relative));
            if (resource == null)
            {
                throw new FileNotFoundException($"Shader resource not found: {relativePath}");
            }

            using var stream = resource.Stream;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        private void DisposeSourceResources()
        {
            _sourceSrv?.Dispose();
            _sourceSrv = null;
            _sourceTexture?.Dispose();
            _sourceTexture = null;
            _sourceWidth = 0;
            _sourceHeight = 0;
        }

        private void DisposeCompositeResources()
        {
            if (_compositeSharedHandleA != IntPtr.Zero)
            {
                CloseHandle(_compositeSharedHandleA);
                _compositeSharedHandleA = IntPtr.Zero;
            }
            if (_compositeSharedHandleB != IntPtr.Zero)
            {
                CloseHandle(_compositeSharedHandleB);
                _compositeSharedHandleB = IntPtr.Zero;
            }
            _compositeSrvA?.Dispose();
            _compositeSrvA = null;
            _compositeSrvB?.Dispose();
            _compositeSrvB = null;
            _compositeRtvA?.Dispose();
            _compositeRtvA = null;
            _compositeRtvB?.Dispose();
            _compositeRtvB = null;
            _compositeTextureA?.Dispose();
            _compositeTextureA = null;
            _compositeTextureB?.Dispose();
            _compositeTextureB = null;
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _destWidth = 0;
            _destHeight = 0;
        }
    }
}
