using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace lifeviz;

public partial class MainWindow
{
    internal bool RunFieldEffectsSmoke()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        _configuredRows = 144; _configuredDepth = 24; _currentAspectRatio = 16d / 9;
        ConfigureSimulationEngine(_engine, 144, 24, 16d / 9, false);
        string folder = Path.Combine(AppContext.BaseDirectory, "field-effects-smoke");
        Directory.CreateDirectory(folder);
        foreach (var kind in new[] { SimulationLayerType.FluidInk, SimulationLayerType.TimeDisplacement, SimulationLayerType.ReactionDiffusion })
        {
            var id = Guid.NewGuid();
            var settings = new SimulationEffectSettings { FluidFlow = 0.55, TimeSpread = 0.7, ReactionSeed = 0.7 };
            var target = kind switch { SimulationLayerType.FluidInk => SimulationReactiveOutput.FluidFlow, SimulationLayerType.TimeDisplacement => SimulationReactiveOutput.TimeSpread, _ => SimulationReactiveOutput.ReactionSeed };
            var spec = new SimulationLayerSpec { Id = id, LayerType = kind, Name = kind.ToString(), BlendMode = BlendMode.Normal, Effects = settings,
                ReactiveMappings = new() { new() { Input = SimulationReactiveInput.Bass, Output = target, Amount = 0.2 } } };
            ApplySimulationLayerSpecs(new List<SimulationLayerSpec> { spec });
            var layer = EnumerateSimulationLeafLayers(_simulationLayers).Single(l => l.Id == id);
            var backend = (GpuPixelSortBackend)layer.Engine!;
            Check(backend.Effect.ToString() == kind.ToString(), "Wrong GPU effect selected.");
            _selectedAudioDeviceId = "smoke";
            _audioBeatDetector.SetSmokeReactiveState(0.5, 0.5, 0, 0, 0, 0, 0, 0);
            ApplySimulationLayerReactiveState();
            double reactive = kind switch { SimulationLayerType.FluidInk => layer.EffectiveEffects.FluidFlow, SimulationLayerType.TimeDisplacement => layer.EffectiveEffects.TimeSpread, _ => layer.EffectiveEffects.ReactionSeed };
            double authored = kind == SimulationLayerType.FluidInk ? 0.55 : 0.7;
            Check(Math.Abs(reactive - authored - 0.1) < 1e-6, "Audio did not reach field settings.");
            _selectedAudioDeviceId = null; ApplySimulationLayerReactiveState();
            Check(layer.EffectiveEffects.FluidFlow == settings.FluidFlow && layer.EffectiveEffects.TimeSpread == settings.TimeSpread && layer.EffectiveEffects.ReactionSeed == settings.ReactionSeed, "Audio reset accumulated into authored settings.");
            var config = JsonSerializer.Deserialize<AppConfig.SimulationLayerConfig>(JsonSerializer.Serialize(BuildSimulationLayerConfig(layer)))!;
            var restored = CloneSimulationLayerSpec(NormalizeSimulationLayerSpec(config, 0, new HashSet<Guid>()));
            Check(restored.LayerType == kind && JsonSerializer.Serialize(restored.Effects) == JsonSerializer.Serialize(settings), "Autosave lost field settings.");
            restored.Effects.FluidFlow = 0;
            Check(layer.Effects.FluidFlow == 0.55, "Cloned settings share mutable state.");
            int width = backend.Columns, height = backend.Rows;
            byte[]? compositeBuffer = null;
            byte[] Inject(byte[] pixels)
            {
                var source = CaptureSource.CreateFile("field-smoke", "Field fixture", width, height);
                source.BlendMode = BlendMode.Normal;
                source.LastFrame = new SourceFrame(pixels, width, height, null, width, height);
                var composite = BuildCompositeFrame(new List<CaptureSource> { source }, ref compositeBuffer, useEngineDimensions: true, animationTime: 0, includeCpuReadback: true);
                Check(composite?.GpuSurface != null && backend.TryInjectCompositeSurface(composite.GpuSurface, 0, 1, false, GameOfLifeEngine.InjectionMode.Threshold, 0, 1, 0, false), "Could not inject field fixture.");
                return composite!.Downscaled.ToArray();
            }
            byte[] Read() { byte[] result = new byte[width * height * 4]; backend.FillColorBuffer(result); return result; }
            byte[] Pattern(int frame)
            {
                byte[] pixels = new byte[width * height * 4];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    double wave = 0.5 + 0.5 * Math.Sin(x * 0.07 + y * 0.09 + frame * 0.06);
                    bool disc = Math.Pow(x - width * (0.5 + 0.25 * Math.Sin(frame * 0.035)), 2) + Math.Pow(y - height * 0.5, 2) < height * height * 0.045;
                    pixels[i] = (byte)(disc ? 245 : 20 + wave * 80);
                    pixels[i + 1] = (byte)(disc ? 170 : 30 + y * 150 / height);
                    pixels[i + 2] = (byte)(disc ? 60 : 40 + wave * 190);
                    pixels[i + 3] = 255;
                }
                return pixels;
            }
            byte[]? first = null, final = null;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (int run = 0; run < 2; run++)
            {
                backend.Randomize(); backend.SetEffectSettings(settings);
                for (int frame = 0; frame < 180; frame++)
                {
                    byte[] input = Inject(Pattern(frame)); backend.Step();
                    if (frame is 0 or 59 or 179)
                    {
                        byte[] output = Read();
                        if (frame == 0 && run == 0) first = output;
                        if (frame == 179)
                        {
                            Check(!output.SequenceEqual(input) && !output.SequenceEqual(first!), "Effect did not evolve or only passed through input.");
                            if (run == 0) final = output;
                            else Check(output.SequenceEqual(final!), "Reset/replay was not deterministic.");
                        }
                        if (run == 0) SaveFieldSmokeImage(output, width, height, Path.Combine(folder, $"{kind}-{frame:000}.png"));
                    }
                }
            }
            Logger.Info($"{kind}: 360 injected/stepped frames in {timer.Elapsed.TotalSeconds:F2}s; deterministic replay passed.");
            // Fluid must also evolve with a still source; reaction must grow after seeding is stopped.
            if (kind != SimulationLayerType.TimeDisplacement)
            {
                if (kind == SimulationLayerType.ReactionDiffusion) { settings.ReactionSeed = 0; backend.SetEffectSettings(settings); }
                Inject(Pattern(179)); byte[] before = Read();
                for (int i = 0; i < 30; i++) backend.Step();
                Check(!Read().SequenceEqual(before), "Field stopped evolving on static input.");
            }
            if (kind != SimulationLayerType.ReactionDiffusion)
            {
                settings.FluidPersistence = 0; settings.TimeSpread = 0; backend.SetEffectSettings(settings);
                byte[] input = Inject(Pattern(20)); backend.Step();
                Check(Read().SequenceEqual(input), "Zero persistence/spread must return exact full-resolution input.");
            }
            backend.Randomize(); Inject(new byte[width * height * 4]);
            for (int i = 0; i < 4; i++) backend.Step();
            Check(Read().All(b => b == 0), "Transparent input produced visible pixels.");
            byte[] partial = Pattern(0);
            for (int i = 0; i < partial.Length; i += 4) { partial[i] /= 2; partial[i+1] /= 2; partial[i+2] /= 2; partial[i+3] = 128; }
            Inject(partial); backend.Step(); byte[] alpha = Read();
            for (int i = 0; i < alpha.Length; i += 4) Check(alpha[i] <= alpha[i+3] && alpha[i+1] <= alpha[i+3] && alpha[i+2] <= alpha[i+3], "Premultiplied alpha invariant broken.");
            Check(TryBuildSimulationPresentationLayer(layer, 1, out var presentation) && presentation.PublishesStandaloneOutput && presentation.Surface != null, "Effect did not publish a standalone GPU surface.");
            EnsureEngineColorBuffer(layer);
            byte[] bgra = Read();
            Check(layer.ColorBuffer![0] == bgra[2] && layer.ColorBuffer[2] == bgra[0], "CPU/recording channel conversion failed.");
            if (kind == SimulationLayerType.TimeDisplacement)
            {
                backend.Configure(2160, 24, 16d / 9);
                Check(backend.HistoryBytes <= 64L * 1024 * 1024, "4K history exceeded memory budget.");
            }
            backend.Configure(144, 24, 16d / 9); backend.SetEffectSettings(new());
            byte[] clean = Inject(Pattern(0)); backend.Step();
            if (kind != SimulationLayerType.ReactionDiffusion) Check(Read().SequenceEqual(clean), "Reconfigure retained stale history.");
        }
        var invalid = new SimulationEffectSettings { FluidFlow = double.NaN, ReactionKill = double.PositiveInfinity, TimeScale = -20 };
        Check(invalid.FluidFlow == 0 && invalid.ReactionKill == 0.03 && invalid.TimeScale == 0.5, "Invalid settings were not normalized.");
        Logger.Info("Field effect GPU, reactivity, persistence, alpha, publication, reset and memory checks passed.");
        return true;
    }

    private static void SaveFieldSmokeImage(byte[] data, int width, int height, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, data, width * 4)));
        using var file = File.Create(path); encoder.Save(file);
    }
}
