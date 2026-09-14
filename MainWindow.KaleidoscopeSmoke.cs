using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace lifeviz;

public partial class MainWindow
{
    internal bool RunKaleidoscopeSmoke()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        _configuredRows = 144; _configuredDepth = 24; _currentAspectRatio = 16d / 9;
        ConfigureSimulationEngine(_engine, 144, 24, 16d / 9, false);
        var settings = new SimulationEffectSettings { KaleidoscopeFeedback = 0.6, KaleidoscopeZoom = 1.02, KaleidoscopeRotation = -1.25, KaleidoscopeFolds = 7, KaleidoscopeCenterX = 0.47, KaleidoscopeCenterY = 0.56 };
        var spec = new SimulationLayerSpec { Id = Guid.NewGuid(), LayerType = SimulationLayerType.FeedbackKaleidoscope, Name = "Kaleidoscope test", BlendMode = BlendMode.Normal, Effects = settings,
            ReactiveMappings = new()
            {
                new() { Input = SimulationReactiveInput.Bass, Output = SimulationReactiveOutput.KaleidoscopeFeedback, Amount = 0.2 },
                new() { Input = SimulationReactiveInput.Bass, Output = SimulationReactiveOutput.KaleidoscopeZoom, Amount = 0.05 },
                new() { Input = SimulationReactiveInput.Bass, Output = SimulationReactiveOutput.KaleidoscopeRotation, Amount = 2 }
            } };
        ApplySimulationLayerSpecs(new List<SimulationLayerSpec> { spec });
        var layer = EnumerateSimulationLeafLayers(_simulationLayers).Single(s => s.Id == spec.Id);
        var backend = (GpuPixelSortBackend)layer.Engine!;
        Check(backend.Effect == ImageSimulationEffect.FeedbackKaleidoscope, "Wrong backend.");
        _selectedAudioDeviceId = "smoke"; _audioBeatDetector.SetSmokeReactiveState(0.5, 0.5, 0, 0, 0, 0, 0, 0);
        ApplySimulationLayerReactiveState();
        Check(Math.Abs(layer.EffectiveEffects.KaleidoscopeFeedback - 0.7) < 1e-6 && Math.Abs(layer.EffectiveEffects.KaleidoscopeZoom - 1.045) < 1e-6 && Math.Abs(layer.EffectiveEffects.KaleidoscopeRotation + 0.25) < 1e-6, "Audio did not reach kaleidoscope parameters.");
        _selectedAudioDeviceId = null; ApplySimulationLayerReactiveState();
        Check(JsonSerializer.Serialize(layer.EffectiveEffects) == JsonSerializer.Serialize(settings), "Audio accumulated into base settings.");
        var saved = JsonSerializer.Deserialize<AppConfig.SimulationLayerConfig>(JsonSerializer.Serialize(BuildSimulationLayerConfig(layer)))!;
        var restored = CloneSimulationLayerSpec(NormalizeSimulationLayerSpec(saved, 0, new HashSet<Guid>()));
        Check(restored.LayerType == spec.LayerType && JsonSerializer.Serialize(restored.Effects) == JsonSerializer.Serialize(settings) && restored.ReactiveMappings.Count == 3, "Autosave/clone lost kaleidoscope settings.");
        restored.Effects.KaleidoscopeZoom = 0.9;
        Check(layer.Effects.KaleidoscopeZoom == 1.02, "Snapshot shares mutable settings.");
        int width = backend.Columns, height = backend.Rows;
        byte[]? buffer = null;
        void Inject(byte[] pixels)
        {
            var source = CaptureSource.CreateFile("kaleidoscope-smoke", "Fixture", width, height);
            source.BlendMode = BlendMode.Normal; source.LastFrame = new SourceFrame(pixels, width, height, null, width, height);
            var composite = BuildCompositeFrame(new List<CaptureSource> { source }, ref buffer, useEngineDimensions: true, animationTime: 0, includeCpuReadback: true);
            Check(composite?.GpuSurface != null && backend.TryInjectCompositeSurface(composite.GpuSurface, 0, 1, false, GameOfLifeEngine.InjectionMode.Threshold, 0, 1, 0, false), "GPU injection failed.");
        }
        byte[] Read() { var pixels = new byte[width * height * 4]; backend.FillColorBuffer(pixels); return pixels; }
        byte[] pattern = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            pattern[i] = (byte)(128 + 120 * Math.Sin(x * 0.08 + y * 0.05));
            pattern[i+1] = (byte)(128 + 120 * Math.Sin(x * 0.02 - y * 0.14));
            pattern[i+2] = (byte)(128 + 120 * Math.Sin(x * 0.13 + y * 0.12));
            pattern[i+3] = 255;
        }
        string folder = Path.Combine(AppContext.BaseDirectory, "kaleidoscope-smoke"); Directory.CreateDirectory(folder);
        byte[]? last = null;
        for (int run = 0; run < 2; run++)
        {
            backend.Randomize(); backend.SetEffectSettings(settings); Inject(pattern);
            byte[]? first = null;
            for (int frame = 0; frame < 120; frame++)
            {
                backend.Step();
                if (frame == 0) first = Read();
                if (run == 0 && frame is 0 or 30 or 119) SaveFieldSmokeImage(Read(), width, height, Path.Combine(folder, $"frame-{frame:000}.png"));
            }
            var output = Read();
            Check(!output.SequenceEqual(first!) && !output.SequenceEqual(pattern), "Feedback did not evolve with a still source.");
            if (run == 0) last = output; else Check(output.SequenceEqual(last!), "Replay was not deterministic.");
        }
        // Each authored geometric control must affect the actual pixels.
        byte[] Render(SimulationEffectSettings controls)
        {
            backend.Randomize(); backend.SetEffectSettings(controls); Inject(pattern);
            for (int i = 0; i < 8; i++) backend.Step(); return Read();
        }
        byte[] baseline = Render(settings);
        foreach (Action<SimulationEffectSettings> change in new Action<SimulationEffectSettings>[]
        {
            s => s.KaleidoscopeFeedback = 0, s => s.KaleidoscopeZoom = 0.95, s => s.KaleidoscopeRotation = 3,
            s => s.KaleidoscopeFolds = 4, s => s.KaleidoscopeCenterX = 0.3, s => s.KaleidoscopeCenterY = 0.3
        })
        {
            var variant = settings.Clone(); change(variant); Check(!Render(variant).SequenceEqual(baseline), "A kaleidoscope control had no visual effect.");
        }
        var symmetry = new SimulationEffectSettings { KaleidoscopeFeedback = 0, KaleidoscopeFolds = 4 };
        byte[] folded = Render(symmetry);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) for (int c = 0; c < 4; c++)
            Check(Math.Abs(folded[(y*width+x)*4+c] - folded[(y*width+width-1-x)*4+c]) <= 1, "Centered four-fold output is not mirrored.");
        backend.SetEffectSettings(symmetry); backend.Step(); Check(Read().SequenceEqual(folded), "Zero feedback retained old frames.");
        backend.Randomize(); backend.SetEffectSettings(settings); Inject(pattern); backend.Step();
        Inject(new byte[pattern.Length]); backend.Step(); var faded = Read();
        Check(faded.Where((_, i) => i % 4 == 3).All(a => a > 0 && a < 255), "Transparent input did not fade retained coverage.");
        for (int i = 0; i < faded.Length; i += 4) Check(faded[i] <= faded[i+3] && faded[i+1] <= faded[i+3] && faded[i+2] <= faded[i+3], "Alpha no longer premultiplies color.");
        backend.Randomize(); Inject(new byte[pattern.Length]); backend.Step(); Check(Read().All(b => b == 0), "Reset leaked previous image.");
        Check(TryBuildSimulationPresentationLayer(layer, 1, out var presentation) && presentation.Surface != null && presentation.PublishesStandaloneOutput, "GPU publication failed.");
        Inject(pattern); backend.Step(); EnsureEngineColorBuffer(layer); var bgra = Read();
        Check(layer.ColorBuffer![0] == bgra[2] && layer.ColorBuffer[2] == bgra[0], "Recording channel conversion failed.");
        backend.Configure(144, 24, 16d / 9); backend.SetEffectSettings(settings); Inject(pattern); backend.Step();
        var afterConfigure = Read(); backend.Randomize(); Inject(pattern); backend.Step(); Check(Read().SequenceEqual(afterConfigure), "Dimension reset kept stale history.");
        Check(backend.HistoryBytes == 0, "Kaleidoscope should not allocate a temporal ring.");
        Logger.Info("Kaleidoscope GPU smoke passed: geometric controls, mirror symmetry, feedback, audio, snapshots, alpha, reset/replay and publication.");
        return true;
    }
}
