namespace lifeviz;

public partial class MainWindow
{
    // Baking needs GPU source composition and a CPU encoder buffer, but no
    // D3D9/WPF presentation bridge or visible swap chain.
    private sealed class BakeRenderBackend : IRenderBackend
    {
        private readonly GpuSourceCompositor _gpu;
        private readonly CpuSourceCompositor _cpu;
        private byte[]? _buffer;
        public BakeRenderBackend(MainWindow owner)
        {
            _gpu = new GpuSourceCompositor(owner);
            _cpu = new CpuSourceCompositor(owner);
        }
        public int PixelWidth { get; private set; }
        public int PixelHeight { get; private set; }
        public int PresentationDrawCount => 0;
        public bool PrefersNativeSourceFrames => _gpu.IsAvailable;
        public bool SupportsGpuSimulationComposition => false;
        public byte[]? EnsureSurface(int width, int height, bool force)
        {
            if (_buffer == null || width != PixelWidth || height != PixelHeight)
                _buffer = new byte[checked(width * height * 4)];
            PixelWidth = width;
            PixelHeight = height;
            return _buffer;
        }
        public CompositeFrame? BuildCompositeFrame(List<CaptureSource> sources, ref byte[]? downscaledBuffer,
            bool useEngineDimensions, double animationTime, bool includeCpuReadback = true)
        {
            if (_gpu.IsAvailable)
            {
                var frame = _gpu.BuildCompositeFrame(sources, ref downscaledBuffer, useEngineDimensions, animationTime, includeCpuReadback);
                if (frame != null || sources.Count == 0) return frame;
            }
            return _cpu.BuildCompositeFrame(sources, ref downscaledBuffer, useEngineDimensions, animationTime);
        }
        public void PresentFrame(byte[] pixelBuffer, int stride) { }
        public void PresentUnderlay(byte[]? underlayBuffer, int stride) { }
        public void UpdateEffectState(bool useOverlay, double blendModeValue) { }
        public bool PresentSimulationComposition(IReadOnlyList<SimulationPresentationLayerData> layers,
            byte[]? underlayBuffer, GpuCompositeSurface? underlaySurface, int simulationBaseline,
            bool useSignedAddSubPassthrough, bool useMixedAddSubPassthroughModel, bool invertComposite) => false;
        public byte[]? GetPresentedFrameCopyForSmoke() => null;
        public void RequestPresentationRedrawForSmoke() { }
        public void Dispose() { _gpu.Dispose(); _buffer = null; }
    }
}
