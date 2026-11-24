using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace lifeviz;

internal static partial class SmokeTestRunner
{
    private static int RunPerformanceSimulationSmokeTest()
    {
        var window = new MainWindow();
        try { return window.RunPerformanceSimulationSmoke() ? 0 : 1; }
        finally { window.Close(); }
    }
}

public partial class MainWindow
{
    internal bool RunPerformanceSimulationSmoke()
    {
        static void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
        }

        static int Changes(byte[] a, byte[] b)
        {
            int count = 0;
            for (int i = 0; i < a.Length; i += 4)
                if (a[i] != b[i] || a[i+1] != b[i+1] || a[i+2] != b[i+2] || a[i+3] != b[i+3])
                    count++;
            return count;
        }

        ConfigureSimulationEngine(_engine, 144, 24, 16d / 9, false);
        _configuredRows = 144;
        _configuredDepth = 24;
        _currentAspectRatio = 16d / 9;
        string folder = Path.Combine(AppContext.BaseDirectory, "performance-sims-smoke");
        Directory.CreateDirectory(folder);
        var kinds = new[] { SimulationLayerType.Faultline, SimulationLayerType.MagnetChoir,
            SimulationLayerType.Stampede, SimulationLayerType.InkTrap };
        var hits = new List<byte[]>();

        foreach (var kind in kinds)
        {
            var spec = new SimulationLayerSpec { Id = Guid.NewGuid(), LayerType = kind,
                Name = kind.ToString(), BlendMode = BlendMode.Normal };
            ApplySimulationLayerSpecs(new List<SimulationLayerSpec> { spec });
            var layer = EnumerateSimulationLeafLayers(_simulationLayers).Single(s => s.Id == spec.Id);
            var backend = (GpuPixelSortBackend)layer.Engine!;
            Check(backend.IsPerformanceEffect && backend.Effect.ToString() == kind.ToString(), kind + " selected the wrong backend.");
            var config = JsonSerializer.Deserialize<AppConfig.SimulationLayerConfig>(
                JsonSerializer.Serialize(BuildSimulationLayerConfig(layer)))!;
            var roundTrip = CloneSimulationLayerSpec(NormalizeSimulationLayerSpec(config, 0, new HashSet<Guid>()));
            Check(roundTrip.LayerType == kind, kind + " was lost during scene round-trip.");

            int width = backend.Columns, height = backend.Rows;
            byte[]? buffer = null;
            var fixture = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = width / 8; x < width * 7 / 8; x++)
                {
                    int i = (y * width + x) * 4;
                    byte alpha = (byte)(y < height / 2 ? 128 : 255);
                    fixture[i] = (byte)(alpha * x / width);
                    fixture[i+1] = (byte)(alpha / 2);
                    fixture[i+2] = (byte)(alpha * y / height);
                    fixture[i+3] = alpha;
                }

            byte[] Read()
            {
                var bytes = new byte[fixture.Length];
                backend.FillColorBuffer(bytes);
                for (int i = 0; i < bytes.Length; i += 4)
                    Check(bytes[i] <= bytes[i+3] && bytes[i+1] <= bytes[i+3] && bytes[i+2] <= bytes[i+3],
                        kind + " produced non-premultiplied color.");
                return bytes;
            }

            byte[] Render(PerformanceSimulationFrame frame, byte[] pixels)
            {
                backend.Randomize();
                var source = CaptureSource.CreateFile("performance-sim-smoke", "Fixture", width, height);
                source.BlendMode = BlendMode.Normal;
                source.LastFrame = new SourceFrame(pixels, width, height, null, width, height);
                var composite = BuildCompositeFrame(new List<CaptureSource> { source }, ref buffer,
                    useEngineDimensions: true, animationTime: 0, includeCpuReadback: true);
                Check(composite?.GpuSurface != null &&
                    backend.TryInjectCompositeSurface(composite.GpuSurface, 0, 1, false,
                        GameOfLifeEngine.InjectionMode.Threshold, 0, 1, 0, false), kind + " GPU injection failed.");
                backend.SetPerformanceFrame(frame);
                backend.Step();
                return Read();
            }

            var still = new PerformanceSimulationFrame(0.5f, 0, 0, 0, 0, false);
            var kick = still with { Kick = 0.8f };
            var slam = still with { Kick = 1, Slam = 1 };
            byte[] baseline = Render(still, fixture), hit = Render(kick, fixture), burst = Render(slam, fixture);
            int minChanged = Math.Max(8, width * height / 150);
            Check(Changes(baseline, hit) > minChanged, kind + " ignored the kick or only changed a few pixels.");
            Check(Changes(hit, burst) > minChanged, kind + " did not distinguish a manual slam from a kick.");
            Check(hit.SequenceEqual(Render(kick, fixture)), kind + " deterministic replay diverged.");
            hits.Add(hit);

            if (kind is SimulationLayerType.MagnetChoir or SimulationLayerType.Stampede)
            {
                byte[] snare = Render(still with { Snare = 1 }, fixture);
                Check(Changes(baseline, snare) > minChanged && Changes(hit, snare) > minChanged,
                    kind + " did not move its geometry differently for snare and kick.");
            }
            if (kind == SimulationLayerType.InkTrap)
            {
                int alphaTotal(byte[] a) => a.Where((_, i) => i % 4 == 3).Sum(x => (int)x);
                Check(alphaTotal(hit) < alphaTotal(baseline), "Ink Trap failed to punch a true alpha aperture.");
            }

            byte[] clear = Render(still, new byte[fixture.Length]);
            if (kind == SimulationLayerType.Faultline)
                Check(clear.All(x => x == 0), "Faultline painted over a completely transparent source.");
            else
            {
                int alphaMin = 255, alphaMax = 0;
                for (int i = 3; i < clear.Length; i += 4)
                {
                    alphaMin = Math.Min(alphaMin, (int)clear[i]);
                    alphaMax = Math.Max(alphaMax, (int)clear[i]);
                }
                Check(alphaMin == 0 && alphaMax > 200, kind + " lost its synthetic shapes or negative space.");
            }
            Check(backend.AuxiliaryFieldBytes == 0 && backend.HistoryBytes == 0,
                kind + " unexpectedly allocated a field grid or history ring.");
            SaveFieldSmokeImage(baseline, width, height, Path.Combine(folder, $"{kind}-idle.png"));
            SaveFieldSmokeImage(hit, width, height, Path.Combine(folder, $"{kind}-kick.png"));
            SaveFieldSmokeImage(burst, width, height, Path.Combine(folder, $"{kind}-slam.png"));
            Logger.Info($"{kind}: scene round-trip, GPU event reactions, alpha and deterministic replay passed.");
        }

        for (int i = 0; i < hits.Count; i++)
            for (int j = i + 1; j < hits.Count; j++)
                Check(Changes(hits[i], hits[j]) > hits[i].Length / 80,
                    $"{kinds[i]} and {kinds[j]} rendered almost the same effect.");

        var state = new PerformanceSimulationState(ImageSimulationEffect.Faultline);
        var armed = state.Update(1d / 60, true, 4, 0, 0, 0, false, 0, 0);
        var held = state.Update(1d / 60, true, 5, 0.8, 0.9, 0.7, true, 0, 0);
        Check(held.Held && held.Time == armed.Time && held.Kick == 0, "Hold leaked queued audio into the visible state.");
        var released = state.Update(1d / 60, true, 5, 0.8, 0.9, 0.7, false, 1, 0);
        Check(!released.Held && released.Kick == 1 && released.Snare == 1 && released.Highs == 1,
            "Release did not flush queued audio accents.");
        var next = state.Update(1d / 60, true, 5, 0.8, 0.9, 0.7, false, 1, 0);
        Check(next.Kick < released.Kick && next.Snare < released.Snare && next.Highs < released.Highs,
            "The same Release edge was replayed.");
        return true;
    }
}
