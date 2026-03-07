using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace lifeviz;

internal sealed class SimulationPresentationLayerData
{
    public byte[]? Buffer { get; init; }
    public IntPtr SharedTextureHandle { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public required int BlendMode { get; init; }
    public required float Opacity { get; init; }
    public float HueShiftDegrees { get; init; }
}

public partial class MainWindow
{
    private interface IRenderBackend : IDisposable
    {
        int PixelWidth { get; }

        int PixelHeight { get; }

        byte[]? EnsureSurface(int width, int height, bool force);

        CompositeFrame? BuildCompositeFrame(List<CaptureSource> sources, ref byte[]? downscaledBuffer, bool useEngineDimensions, double animationTime, bool includeCpuReadback = true);

        void PresentFrame(byte[] pixelBuffer, int stride);

        void PresentUnderlay(byte[]? underlayBuffer, int stride);

        void UpdateEffectState(bool useOverlay, double blendModeValue);

        bool PrefersNativeSourceFrames { get; }

        bool SupportsGpuSimulationComposition { get; }

        bool PresentSimulationComposition(
            IReadOnlyList<SimulationPresentationLayerData> layers,
            byte[]? underlayBuffer,
            GpuCompositeSurface? underlaySurface,
            int simulationBaseline,
            bool useSignedAddSubPassthrough,
            bool useMixedAddSubPassthroughModel,
            bool invertComposite);
    }

    private sealed class NullRenderBackend : IRenderBackend
    {
        public int PixelWidth => 0;

        public int PixelHeight => 0;

        public byte[]? EnsureSurface(int width, int height, bool force) => null;

        public CompositeFrame? BuildCompositeFrame(List<CaptureSource> sources, ref byte[]? downscaledBuffer, bool useEngineDimensions, double animationTime, bool includeCpuReadback = true)
            => null;

        public void PresentFrame(byte[] pixelBuffer, int stride)
        {
        }

        public void PresentUnderlay(byte[]? underlayBuffer, int stride)
        {
        }

        public void UpdateEffectState(bool useOverlay, double blendModeValue)
        {
        }

        public bool PrefersNativeSourceFrames => false;

        public bool SupportsGpuSimulationComposition => false;

        public bool PresentSimulationComposition(
            IReadOnlyList<SimulationPresentationLayerData> layers,
            byte[]? underlayBuffer,
            GpuCompositeSurface? underlaySurface,
            int simulationBaseline,
            bool useSignedAddSubPassthrough,
            bool useMixedAddSubPassthroughModel,
            bool invertComposite) => false;

        public void Dispose()
        {
        }
    }

    private sealed class CpuRenderBackend : IRenderBackend
    {
        private readonly WpfPresentationBackend _presentationBackend;
        private readonly CpuSourceCompositor _sourceCompositor;

        public CpuRenderBackend(MainWindow owner, Image targetImage)
        {
            _presentationBackend = new WpfPresentationBackend(targetImage);
            _sourceCompositor = new CpuSourceCompositor(owner);
        }

        public int PixelWidth => _presentationBackend.PixelWidth;

        public int PixelHeight => _presentationBackend.PixelHeight;

        public byte[]? EnsureSurface(int width, int height, bool force) => _presentationBackend.EnsureSurface(width, height, force);

        public CompositeFrame? BuildCompositeFrame(List<CaptureSource> sources, ref byte[]? downscaledBuffer, bool useEngineDimensions, double animationTime, bool includeCpuReadback = true)
            => _sourceCompositor.BuildCompositeFrame(sources, ref downscaledBuffer, useEngineDimensions, animationTime);

        public void PresentFrame(byte[] pixelBuffer, int stride) => _presentationBackend.PresentFrame(pixelBuffer, stride);

        public void PresentUnderlay(byte[]? underlayBuffer, int stride) => _presentationBackend.PresentUnderlay(underlayBuffer, stride);

        public void UpdateEffectState(bool useOverlay, double blendModeValue) => _presentationBackend.UpdateEffectState(useOverlay, blendModeValue);

        public bool PrefersNativeSourceFrames => false;

        public bool SupportsGpuSimulationComposition => false;

        public bool PresentSimulationComposition(
            IReadOnlyList<SimulationPresentationLayerData> layers,
            byte[]? underlayBuffer,
            GpuCompositeSurface? underlaySurface,
            int simulationBaseline,
            bool useSignedAddSubPassthrough,
            bool useMixedAddSubPassthroughModel,
            bool invertComposite) => false;

        public void Dispose() => _presentationBackend.Dispose();
    }

    private sealed class GpuRenderBackend : IRenderBackend
    {
        private readonly GpuPresentationBackend _presentationBackend;
        private readonly CpuSourceCompositor _cpuSourceCompositor;
        private readonly GpuSourceCompositor _gpuSourceCompositor;
        private bool _useGpuSourceCompositor;

        public GpuRenderBackend(MainWindow owner, Grid renderHost, Image fallbackImage)
        {
            _presentationBackend = new GpuPresentationBackend(renderHost, fallbackImage);
            _cpuSourceCompositor = new CpuSourceCompositor(owner);
            _gpuSourceCompositor = new GpuSourceCompositor(owner);
            _useGpuSourceCompositor = _gpuSourceCompositor.IsAvailable;
        }

        public int PixelWidth => _presentationBackend.PixelWidth;

        public int PixelHeight => _presentationBackend.PixelHeight;

        public byte[]? EnsureSurface(int width, int height, bool force) => _presentationBackend.EnsureSurface(width, height, force);

        public CompositeFrame? BuildCompositeFrame(List<CaptureSource> sources, ref byte[]? downscaledBuffer, bool useEngineDimensions, double animationTime, bool includeCpuReadback = true)
        {
            if (_useGpuSourceCompositor)
            {
                try
                {
                    var composite = _gpuSourceCompositor.BuildCompositeFrame(sources, ref downscaledBuffer, useEngineDimensions, animationTime, includeCpuReadback);
                    if (composite != null || sources.Count == 0)
                    {
                        return composite;
                    }
                }
                catch (Exception ex)
                {
                    _useGpuSourceCompositor = false;
                    Logger.Warn($"GPU source compositor failed, falling back to CPU composite path. {ex.Message}");
                }
            }

            return _cpuSourceCompositor.BuildCompositeFrame(sources, ref downscaledBuffer, useEngineDimensions, animationTime);
        }

        public void PresentFrame(byte[] pixelBuffer, int stride) => _presentationBackend.PresentFrame(pixelBuffer, stride);

        public void PresentUnderlay(byte[]? underlayBuffer, int stride) => _presentationBackend.PresentUnderlay(underlayBuffer, stride);

        public void UpdateEffectState(bool useOverlay, double blendModeValue) => _presentationBackend.UpdateEffectState(useOverlay, blendModeValue);

        public bool PrefersNativeSourceFrames => _useGpuSourceCompositor;

        public bool SupportsGpuSimulationComposition => _presentationBackend.SupportsGpuSimulationComposition;

        public bool PresentSimulationComposition(
            IReadOnlyList<SimulationPresentationLayerData> layers,
            byte[]? underlayBuffer,
            GpuCompositeSurface? underlaySurface,
            int simulationBaseline,
            bool useSignedAddSubPassthrough,
            bool useMixedAddSubPassthroughModel,
            bool invertComposite)
            => _presentationBackend.PresentSimulationComposition(
                layers,
                underlayBuffer,
                underlaySurface,
                simulationBaseline,
                useSignedAddSubPassthrough,
                useMixedAddSubPassthroughModel,
                invertComposite);

        public void Dispose()
        {
            _gpuSourceCompositor.Dispose();
            _presentationBackend.Dispose();
        }
    }

    private static IRenderBackend CreateRenderBackend(MainWindow owner, Grid renderHost, Image fallbackImage)
    {
        try
        {
            Logger.Info("Initializing GPU render backend.");
            return new GpuRenderBackend(owner, renderHost, fallbackImage);
        }
        catch (Exception ex)
        {
            Logger.Warn($"GPU render backend unavailable, falling back to CPU presentation. {ex.Message}");
            return new CpuRenderBackend(owner, fallbackImage);
        }
    }
}
