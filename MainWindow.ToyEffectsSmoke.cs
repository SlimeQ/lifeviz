using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace lifeviz;

public partial class MainWindow
{
    internal bool RunToyEffectsSmoke()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        _configuredRows = 144; _configuredDepth = 24; _currentAspectRatio = 16d / 9;
        ConfigureSimulationEngine(_engine, 144, 24, 16d / 9, false);
        string folder = Path.Combine(AppContext.BaseDirectory, "toy-effects-smoke"); Directory.CreateDirectory(folder);
        var cases = new[]
        {
            (SimulationLayerType.ParticleErosion, "Particle", new[] { SimulationReactiveOutput.ParticleEmission, SimulationReactiveOutput.ParticleTurbulence }),
            (SimulationLayerType.RippleField, "Ripple", new[] { SimulationReactiveOutput.RippleImpulse, SimulationReactiveOutput.RippleRefraction }),
            (SimulationLayerType.ChromaticMemory, "Chromatic", new[] { SimulationReactiveOutput.ChromaticRed, SimulationReactiveOutput.ChromaticGreen, SimulationReactiveOutput.ChromaticBlue }),
            (SimulationLayerType.ContourCurrent, "Contour", new[] { SimulationReactiveOutput.ContourFlow, SimulationReactiveOutput.ContourThickness })
        };
        foreach (var (kind, prefix, targets) in cases)
        {
            var settings = new SimulationEffectSettings();
            var spec = new SimulationLayerSpec { Id = Guid.NewGuid(), LayerType = kind, Name = kind.ToString(), BlendMode = BlendMode.Normal, Effects = settings,
                ReactiveMappings = targets.Select(t => new SimulationReactiveMapping { Input = SimulationReactiveInput.Bass, Output = t, Amount = 0.04 }).ToList() };
            ApplySimulationLayerSpecs(new List<SimulationLayerSpec> { spec });
            var layer = EnumerateSimulationLeafLayers(_simulationLayers).Single(s => s.Id == spec.Id);
            var backend = (GpuPixelSortBackend)layer.Engine!;
            Check(backend.Effect.ToString() == kind.ToString(), "Wrong simulation backend.");
            _selectedAudioDeviceId = "smoke"; _audioBeatDetector.SetSmokeReactiveState(0.5, 0.5, 0, 0, 0, 0, 0, 0);
            ApplySimulationLayerReactiveState();
            foreach (var target in targets)
            {
                var property = typeof(SimulationEffectSettings).GetProperty(target.ToString())!;
                Check(Math.Abs((double)property.GetValue(layer.EffectiveEffects)! - (double)property.GetValue(settings)! - 0.02) < 1e-6, "Audio did not reach " + target);
            }
            _selectedAudioDeviceId = null; ApplySimulationLayerReactiveState();
            Check(JsonSerializer.Serialize(layer.EffectiveEffects) == JsonSerializer.Serialize(settings), "Audio changed authored settings.");
            var config = JsonSerializer.Deserialize<AppConfig.SimulationLayerConfig>(JsonSerializer.Serialize(BuildSimulationLayerConfig(layer)))!;
            var saved = CloneSimulationLayerSpec(NormalizeSimulationLayerSpec(config, 0, new HashSet<Guid>()));
            Check(saved.LayerType == kind && JsonSerializer.Serialize(saved.Effects) == JsonSerializer.Serialize(settings), "Autosave lost controls.");
            saved.Effects.ParticleEmission = 0; Check(layer.Effects.ParticleEmission == 0.45, "Clone changed original settings.");
            int width = backend.Columns, height = backend.Rows;
            byte[]? buffer = null;
            byte[] Inject(byte[] pixels)
            {
                var source = CaptureSource.CreateFile("toys-smoke", "Fixture", width, height);
                source.BlendMode = BlendMode.Normal; source.LastFrame = new SourceFrame(pixels, width, height, null, width, height);
                var composite = BuildCompositeFrame(new List<CaptureSource> { source }, ref buffer, useEngineDimensions: true, animationTime: 0, includeCpuReadback: true);
                Check(composite?.GpuSurface != null && backend.TryInjectCompositeSurface(composite.GpuSurface, 0, 1, false, GameOfLifeEngine.InjectionMode.Threshold, 0, 1, 0, false), "GPU injection failed.");
                return composite!.Downscaled.ToArray();
            }
            byte[] Read() { var pixels = new byte[width * height * 4]; backend.FillColorBuffer(pixels); return pixels; }
            byte[] Pattern(int frame)
            {
                var pixels = new byte[width * height * 4];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    double wave = Math.Sin(x * 0.13 + y * 0.09 + frame * 0.08);
                    bool disc = Math.Pow(x - width * (0.5 + 0.25 * Math.Sin(frame * 0.055)), 2) + Math.Pow(y - height * 0.5, 2) < height * height * 0.035;
                    pixels[i] = (byte)(disc ? 240 : 80 + 60 * wave);
                    pixels[i+1] = (byte)(disc ? 220 : 30 + y * 140 / height);
                    pixels[i+2] = (byte)(disc ? 50 : 130 + 90 * wave); pixels[i+3] = 255;
                }
                return pixels;
            }
            byte[]? final = null;
            for (int run = 0; run < 2; run++)
            {
                backend.Randomize(); backend.SetEffectSettings(settings);
                for (int frame = 0; frame < 120; frame++)
                {
                    var input = Inject(Pattern(frame)); backend.Step();
                    if (run == 0 && frame is 0 or 30 or 119) SaveFieldSmokeImage(Read(), width, height, Path.Combine(folder, $"{kind}-{frame:000}.png"));
                    if (frame == 119)
                    {
                        var output = Read(); Check(!output.SequenceEqual(input), kind + " only passed through its source.");
                        if (run == 0) final = output; else Check(output.SequenceEqual(final!), kind + " replay diverged.");
                    }
                }
            }
            byte[] Render(SimulationEffectSettings controls)
            {
                backend.Randomize(); backend.SetEffectSettings(controls);
                for (int frame = 0; frame < 30; frame++) { Inject(Pattern(frame)); backend.Step(); }
                return Read();
            }
            byte[] baseline = Render(settings);
            for (int step = 0; step < 24; step++) backend.Step();
            Check(!Read().SequenceEqual(baseline), kind + " stopped evolving when source motion stopped.");
            foreach (var property in typeof(SimulationEffectSettings).GetProperties().Where(p => p.Name.StartsWith(prefix)))
            {
                var altered = settings.Clone(); property.SetValue(altered, 0d);
                Check(!Render(altered).SequenceEqual(baseline), property.Name + " has no visual effect.");
            }
            var neutral = settings.Clone(); neutral.RippleRefraction = 0; neutral.ChromaticRed = neutral.ChromaticGreen = neutral.ChromaticBlue = 0;
            backend.SetEffectSettings(neutral); var original = Inject(Pattern(10)); backend.Step();
            if (kind is SimulationLayerType.RippleField or SimulationLayerType.ChromaticMemory)
                Check(Read().SequenceEqual(original), "Neutral settings did not restore source pixels.");
            backend.Randomize(); backend.SetEffectSettings(settings); Inject(new byte[width * height * 4]);
            for (int i = 0; i < 8; i++) backend.Step();
            Check(Read().All(b => b == 0), "Transparent source leaked color.");
            byte[] partial = Pattern(0);
            for (int i = 0; i < partial.Length; i += 4) { partial[i] /= 2; partial[i+1] /= 2; partial[i+2] /= 2; partial[i+3] = 128; }
            Inject(partial); for (int step = 0; step < 12; step++) backend.Step();
            byte[] alpha = Read();
            for (int i = 0; i < alpha.Length; i += 4) Check(alpha[i] <= alpha[i+3] && alpha[i+1] <= alpha[i+3] && alpha[i+2] <= alpha[i+3], "Premultiplied alpha violated.");
            Check(TryBuildSimulationPresentationLayer(layer, 1, out var presentation) && presentation.Surface != null && presentation.PublishesStandaloneOutput, "GPU publication failed.");
            EnsureEngineColorBuffer(layer); var bgra = Read(); Check(layer.ColorBuffer![0] == bgra[2] && layer.ColorBuffer[2] == bgra[0], "Recording channel order incorrect.");
            if (kind == SimulationLayerType.ChromaticMemory)
            {
                backend.Randomize(); var solid = new byte[width*height*4];
                for (int i = 0; i < solid.Length; i += 4) { solid[i] = 100; solid[i+1] = 150; solid[i+2] = 200; solid[i+3] = 255; }
                Inject(solid); backend.Step(); Inject(new byte[solid.Length]); backend.Step(); var ghost = Read(); int center = (height/2*width+width/2)*4;
                Check(Math.Abs(ghost[center]-65) <= 1 && Math.Abs(ghost[center+1]-120) <= 1 && Math.Abs(ghost[center+2]-184) <= 1, "RGB memories were swapped or did not retain independent channel history.");
            }
            if (kind == SimulationLayerType.RippleField)
            {
                backend.Configure(2160, 24, 16d/9);
                Check(backend.AuxiliaryFieldBytes > 0 && backend.AuxiliaryFieldBytes <= 8L*1024*1024, "Ripple field exceeded its memory budget.");
            }
            else Check(backend.AuxiliaryFieldBytes == 0 && backend.HistoryBytes == 0, "Image-only effect allocated extra grids or history rings.");
            if (kind != SimulationLayerType.RippleField)
            {
                Inject(new byte[width*height*4]);
                for (int step = 0; step < 256; step++) backend.Step();
                Check(Read().All(b => b == 0), kind + " left permanent quantized ghosts after removing input.");
            }
            backend.Configure(144, 24, 16d/9); backend.SetEffectSettings(settings); Inject(Pattern(0)); backend.Step(); var reset = Read();
            backend.Randomize(); Inject(Pattern(0)); backend.Step(); Check(reset.SequenceEqual(Read()), "Resize retained stale state.");
            Logger.Info($"{kind}: GPU, every control, audio, alpha, publication, deterministic replay and reset passed.");
        }
        return true;
    }
}
