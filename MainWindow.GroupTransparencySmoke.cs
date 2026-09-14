using System;
using System.Collections.Generic;
using System.Linq;

namespace lifeviz;

public partial class MainWindow
{
    internal bool RunGroupTransparencySmoke(bool datamosh = false)
    {
        if (!_renderLoopAttached) InitializeVisualizer();
        _sources.Clear();
        ClearSimulationLayers();
        _passthroughEnabled = false;
        _invertComposite = false;
        _isPaused = false;
        _effectiveLifeOpacity = 1;
        ApplyDimensions(144, 24, DefaultAspectRatio, persist: false);
        int width = GetReferenceSimulationEngine().Columns;
        int height = GetReferenceSimulationEngine().Rows;
        byte[] pixels = new byte[width * height * 4];
        byte[] alphas = { 0, 64, 128, 255, 255 };
        for (int i = 0; i < width * height; i++)
        {
            int band = (i % width) * 5 / width;
            pixels[i * 4 + 2] = band == 4 ? (byte)0 : (byte)255;
            pixels[i * 4 + 3] = alphas[band];
        }

        var media = CaptureSource.CreateFile("group-alpha-fixture", "Alpha fixture", width, height);
        media.BlendMode = BlendMode.Normal;
        media.FitMode = FitMode.Stretch;
        media.Opacity = 0.75;
        media.LastFrame = new SourceFrame(pixels, width, height, null, width, height);
        var sim = CaptureSource.CreateSimulationGroup("Alpha Pixel Sort");
        sim.BlendMode = BlendMode.Normal;
        sim.SimulationLayers.Add(new SimulationLayerSpec
        {
            Id = Guid.NewGuid(), Kind = LayerEditorSimulationItemKind.Layer,
            LayerType = datamosh ? SimulationLayerType.Datamosh : SimulationLayerType.PixelSort,
            Name = datamosh ? "Datamosh" : "Pixel Sort", Enabled = true,
            BlendMode = BlendMode.Normal, LifeOpacity = 1,
            PixelSortCellWidth = 1, PixelSortCellHeight = 1
        });
        _sources.Add(media);
        _sources.Add(sim);
        ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
        ConfigureSimulationLayerEngines(_configuredRows, _configuredDepth, _currentAspectRatio, randomize: false);
        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers)) layer.TimeSinceLastStep = 1;

        bool injected = false;
        int stepped = 0;
        byte[]? gpuBuffer = null;
        var gpu = BuildInlineCompositeFrameGpu(_sources, ref gpuBuffer, true, 0, 1,
            ref injected, ref stepped, includeCpuReadback: true);
        if (gpu?.GpuSurface == null || gpu.Downscaled.Length != pixels.Length)
            throw new InvalidOperationException("Alpha smoke requires the real GPU inline path.");
        byte[] gpuPixels = gpu.Downscaled.ToArray();
        _isPaused = true;
        byte[]? cpuBuffer = null;
        var cpu = BuildInlineCompositeFrameCpu(_sources, ref cpuBuffer, true, 0, 0, ref injected, ref stepped);
        if (cpu == null) throw new InvalidOperationException("CPU inline alpha output unavailable.");

        void Check(byte[] actual, double groupOpacity, bool overBlue, string phase)
        {
            for (int i = 0; i < width * height; i++)
            {
                double alpha = pixels[i * 4 + 3] / 255.0 * media.Opacity * groupOpacity;
                double[] expected = { overBlue ? 255 * (1 - alpha) : 0, 0,
                    pixels[i * 4 + 2] * alpha, overBlue ? 255 : alpha * 255 };
                for (int c = 0; c < 4; c++)
                    if (Math.Abs(actual[i * 4 + c] - expected[c]) > 3)
                        throw new InvalidOperationException($"{phase}: pixel {i}, channel {c}: expected {expected[c]:0.##}, got {actual[i * 4 + c]}.");
            }
        }

        Check(gpuPixels, 1, false, "GPU sim alpha");
        Check(cpu.Downscaled, 1, false, "CPU sim alpha");
        var background = CaptureSource.CreateFile("alpha-blue", "Blue", width, height);
        background.BlendMode = BlendMode.Normal;
        background.LastFrame = new SourceFrame(BuildSmokeSolidBgra(width, height, 255, 0, 0), width, height, null, width, height);
        // A resolved Sim Group and a Layer Group must both consume premultiplied
        // output correctly, including when the group itself is the first layer.
        foreach (var group in new[] { sim, CaptureSource.CreateGroup("Alpha container") })
        {
            group.BlendMode = BlendMode.Normal;
            group.FitMode = FitMode.Stretch;
            group.Opacity = 0.6;
            group.LastFrame = new SourceFrame(gpuPixels, width, height, null, width, height);
            foreach (bool overBlue in new[] { false, true })
            {
                var sources = overBlue ? new List<CaptureSource> { background, group } : new List<CaptureSource> { group };
                byte[]? result = null;
                using var gpuSource = new GpuSourceCompositor(this);
                var gpuBlend = gpuSource.BuildCompositeFrame(sources, ref result, true, 0, includeCpuReadback: true);
                if (gpuBlend?.GpuSurface == null) throw new InvalidOperationException("GPU group alpha blend unavailable.");
                Check(gpuBlend.Downscaled, group.Opacity, overBlue, "GPU group Normal");
                result = null;
                var cpuBlend = _inlineSourceCompositor.BuildCompositeFrame(sources, ref result, true, 0);
                if (cpuBlend == null) throw new InvalidOperationException("CPU group alpha blend unavailable.");
                Check(cpuBlend.Downscaled, group.Opacity, overBlue, "CPU group Normal");
            }
        }
        // A transparent image border also exercises Fill/Stretch's clamped edge sampling.
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
                if (row < 4 || col < 4 || row >= height - 4 || col >= width - 4)
                    pixels[(row * width + col) * 4 + 3] = 0;
        media.Animations.Add(new LayerAnimation { Type = AnimationType.DvdBounce, DvdScale = 0.25, Loop = AnimationLoop.PingPong });
        var dvdGroup = CaptureSource.CreateGroup("DVD Bounce group");
        dvdGroup.BlendMode = BlendMode.Normal;
        dvdGroup.FitMode = FitMode.Stretch;
        foreach (var fit in new[] { FitMode.Fit, FitMode.Fill, FitMode.Stretch })
        foreach (double time in new[] { 0.0, 1.7, 6.3 })
        {
            media.FitMode = fit;
            using var childGpu = new GpuSourceCompositor(this);
            using var outerGpu = new GpuSourceCompositor(this);
            foreach (bool useGpu in new[] { false, true })
            {
                CompositeFrame Compose(List<CaptureSource> sources, bool child)
                {
                    byte[]? buffer = null;
                    return (useGpu
                        ? (child ? childGpu : outerGpu).BuildCompositeFrame(sources, ref buffer, true, time, includeCpuReadback: true)
                        : _inlineSourceCompositor.BuildCompositeFrame(sources, ref buffer, true, time))
                        ?? throw new InvalidOperationException("DVD Bounce composite unavailable.");
                }
                var child = Compose(new List<CaptureSource> { media }, true);
                dvdGroup.LastFrame = new SourceFrame(child.Downscaled, width, height, null, width, height);
                byte[] grouped = Compose(new List<CaptureSource> { background, dvdGroup }, false).Downscaled.ToArray();
                byte[] direct = Compose(new List<CaptureSource> { background, media }, false).Downscaled;
                int revealedBlue = 0;
                int visibleImage = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    for (int c = 0; c < 4; c++)
                        if (Math.Abs(grouped[i + c] - direct[i + c]) > 3)
                            throw new InvalidOperationException($"DVD Bounce grouped/direct mismatch: gpu={useGpu}, time={time}, byte={i+c}.");
                    if (grouped[i] == 255 && grouped[i + 2] == 0) revealedBlue++;
                    if (grouped[i + 2] > 100) visibleImage++;
                }
                if (revealedBlue < width * height * 0.9 || visibleImage < 20)
                    throw new InvalidOperationException($"DVD Bounce coverage wrong: blue={revealedBlue}, visible={visibleImage}.");
            }
        }
        Logger.Info("Group transparency smoke passed: transparent/partial/opaque black pixels, Pixel Sort, CPU/GPU, first-layer and Normal over blue, media/group opacity, moving DVD Bounce grouped/direct parity.");
        return true;
    }
}
