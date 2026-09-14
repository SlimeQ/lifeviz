using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace lifeviz;

public partial class MainWindow
{
    internal bool RunDatamoshSmoke()
    {
        var id = Guid.NewGuid();
        var spec = new SimulationLayerSpec
        {
            Id = id, LayerType = SimulationLayerType.Datamosh, Name = "Datamosh test",
            BlendMode = BlendMode.Normal, DatamoshFeedback = 0.15, DatamoshBlockSize = 17,
            ReactiveMappings = new List<SimulationReactiveMapping>
            {
                new() { Input = SimulationReactiveInput.Bass, Output = SimulationReactiveOutput.DatamoshFeedback, Amount = 0.8 },
                new() { Input = SimulationReactiveInput.Mid, Output = SimulationReactiveOutput.DatamoshDisplacement, Amount = 0.6 }
            }
        };
        ApplySimulationLayerSpecs(new List<SimulationLayerSpec> { spec });
        var layer = EnumerateSimulationLeafLayers(_simulationLayers).Single(l => l.Id == id);
        if (layer.Engine is not GpuPixelSortBackend backend || !backend.IsDatamosh) return false;

        string? previousAudioDevice = _selectedAudioDeviceId;
        try
        {
            _selectedAudioDeviceId = "smoke";
            _audioBeatDetector.SetSmokeReactiveState(0.5, 0.75, 0.5, 0, 0, 0, 0, 0);
            ApplySimulationLayerReactiveState();
            bool audio = Math.Abs(layer.EffectiveDatamoshFeedback - 0.75) < 0.00001 &&
                         Math.Abs(layer.EffectiveDatamoshDisplacement - 0.3) < 0.00001;
            _selectedAudioDeviceId = null;
            ApplySimulationLayerReactiveState();
            audio &= layer.EffectiveDatamoshFeedback == 0.15 && layer.EffectiveDatamoshDisplacement == 0;

            var config = JsonSerializer.Deserialize<AppConfig.SimulationLayerConfig>(
                JsonSerializer.Serialize(BuildSimulationLayerConfig(layer)))!;
            var restored = NormalizeSimulationLayerSpec(config, 0, new HashSet<Guid>());
            var cloned = CloneSimulationLayerSpec(restored);
            bool persistence = cloned.LayerType == SimulationLayerType.Datamosh &&
                cloned.DatamoshFeedback == 0.15 && cloned.DatamoshBlockSize == 17 &&
                cloned.ReactiveMappings.Count == 2;

            int width = backend.Columns, height = backend.Rows;
            byte[] pattern = BuildSmokePixelSortPatternBgra(width, height);
            byte[] blue = new byte[pattern.Length];
            for (int i = 0; i < blue.Length; i += 4) { blue[i] = 230; blue[i + 1] = 15; blue[i + 2] = 35; blue[i + 3] = 255; }
            byte[]? compositeBuffer = null;
            byte[] Inject(byte[] pixels)
            {
                var source = CaptureSource.CreateFile("datamosh-smoke", "Datamosh", width, height);
                source.BlendMode = BlendMode.Normal;
                source.LastFrame = new SourceFrame(pixels, width, height, null, width, height);
                var composite = BuildCompositeFrame(new List<CaptureSource> { source }, ref compositeBuffer,
                    useEngineDimensions: true, animationTime: 0, includeCpuReadback: true);
                if (composite?.GpuSurface == null || !backend.TryInjectCompositeSurface(composite.GpuSurface,
                    0, 1, false, GameOfLifeEngine.InjectionMode.Threshold, 0, 1, 0, false))
                    throw new InvalidOperationException("Datamosh smoke input failed.");
                return composite.Downscaled.ToArray();
            }
            byte[] Read()
            {
                byte[] pixels = new byte[width * height * 4];
                backend.FillColorBuffer(pixels);
                return pixels;
            }
            backend.SetDatamoshSettings(0.95, 0.8, 17);
            byte[] first = Inject(pattern);
            backend.Step();
            bool seeded = Read().SequenceEqual(first);
            byte[] next = Inject(blue);
            backend.Step();
            byte[] moshed = Read();
            bool history = !moshed.SequenceEqual(next) && !moshed.SequenceEqual(first);

            // Displacement must change spatial output, even on an unchanged source.
            backend.Randomize();
            backend.SetDatamoshSettings(0.95, 0, 17);
            Inject(pattern); backend.Step(); backend.Step();
            byte[] stationary = Read();
            backend.SetDatamoshSettings(0.95, 1, 17);
            backend.Step();
            bool displaced = !Read().SequenceEqual(stationary);

            backend.Randomize();
            backend.SetDatamoshSettings(0.95, 0.8, 17);
            Inject(pattern); backend.Step(); Inject(blue); backend.Step();
            bool deterministic = Read().SequenceEqual(moshed);
            backend.SetDatamoshSettings(0, 1, 17);
            backend.Step();
            bool bypass = Read().SequenceEqual(next);
            backend.SetDatamoshSettings(0.95, 1, 17);
            backend.Configure(height, 24, (double)width / height);
            Inject(blue); backend.Step();
            bool reset = Read().SequenceEqual(next);

            // Retain alpha and ignore invisible RGB while old pixels decay.
            backend.Randomize();
            Inject(blue); backend.Step();
            byte[] transparent = new byte[blue.Length];
            Inject(transparent); backend.Step();
            byte[] faded = Read();
            bool alpha = faded.Where((_, i) => i % 4 == 3).All(a => a > 0 && a < 255);
            for (int i = 0; i < faded.Length; i += 4)
                alpha &= Math.Abs(faded[i] - 230.0 * faded[i + 3] / 255.0) <= 1;

            EnsureEngineColorBuffer(layer);
            byte[] bgra = Read();
            bool recordingColor = layer.ColorBuffer != null &&
                layer.ColorBuffer[0] == bgra[2] && layer.ColorBuffer[2] == bgra[0];
            bool presentation = TryBuildSimulationPresentationLayer(layer, 1, out var output) &&
                                output.PublishesStandaloneOutput && output.Surface != null;
            Logger.Info($"Datamosh smoke: audio={audio}, persistence={persistence}, seeded={seeded}, history={history}, displacement={displaced}, deterministic={deterministic}, bypass={bypass}, reset={reset}, alpha={alpha}, recordingColor={recordingColor}, presentation={presentation}.");
            return audio && persistence && seeded && history && displaced && deterministic && bypass && reset && alpha && recordingColor && presentation;
        }
        finally { _selectedAudioDeviceId = previousAudioDevice; }
    }
}
