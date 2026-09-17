using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace lifeviz;

public partial class MainWindow
{
    internal bool RunAudioResponseSmoke()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        AudioResponseSmoke.RunAnalysis();
        _configuredRows = 144; _configuredDepth = 24; _currentAspectRatio = 16d/9;
        _currentSimulationTargetFps = 60;
        ConfigureSimulationEngine(_engine,144,24,16d/9,false);
        foreach (var type in Enum.GetValues<SimulationLayerType>())
        {
            var id = Guid.NewGuid();
            ApplySimulationLayerSpecs(new List<SimulationLayerSpec> { new() { Id=id, LayerType=type, LifeOpacity=1, Name=type.ToString() } });
            var layer = EnumerateSimulationLeafLayers(_simulationLayers).Single(l => l.Id==id);
            var editorType = Enum.Parse<LayerEditorSimulationLayerType>(type.ToString());
            var outputs = Enum.GetValues<SimulationReactiveOutput>().Where(o => SimulationMappingOptions.Supports(editorType,o.ToString())).ToArray();
            double Read(SimulationReactiveOutput output) => output switch
            {
                SimulationReactiveOutput.Opacity => layer.EffectiveLifeOpacity,
                SimulationReactiveOutput.Framerate => layer.EffectiveSimulationTargetFps,
                SimulationReactiveOutput.HueShift => layer.ReactiveHueShiftDegrees,
                SimulationReactiveOutput.HueSpeed => layer.EffectiveRgbHueShiftSpeedDegreesPerSecond,
                SimulationReactiveOutput.InjectionNoise => layer.EffectiveInjectionNoise,
                SimulationReactiveOutput.ThresholdMin => layer.EffectiveThresholdMin,
                SimulationReactiveOutput.ThresholdMax => layer.EffectiveThresholdMax,
                SimulationReactiveOutput.PixelSortCellWidth => layer.EffectivePixelSortCellWidth,
                SimulationReactiveOutput.PixelSortCellHeight => layer.EffectivePixelSortCellHeight,
                SimulationReactiveOutput.DatamoshFeedback => layer.EffectiveDatamoshFeedback,
                SimulationReactiveOutput.DatamoshDisplacement => layer.EffectiveDatamoshDisplacement,
                _ => (double)typeof(SimulationEffectSettings).GetProperty(output.ToString())!.GetValue(layer.EffectiveEffects)!
            };
            foreach (var output in outputs)
            {
                var mapping = new SimulationReactiveMapping { Input=SimulationReactiveInput.Bass, Output=output, Amount=SimulationReactivity.MaximumAmount(output)*0.25, AttackMs=7, ReleaseMs=115 };
                layer.ReactiveMappings = new() { mapping }; layer.ReactiveEnvelopes.Clear();
                _selectedAudioDeviceId="smoke";
                _audioBeatDetector.SetSmokeReactiveState(0,0,0,0,0,0,0,0); ApplySimulationLayerReactiveState(); double quiet=Read(output);
                _audioBeatDetector.SetSmokeReactiveState(1,1,1,1,1,1,1,1); ApplySimulationLayerReactiveState(1/60d); double loud=Read(output);
                mapping.Amount=-mapping.Amount; ApplySimulationLayerReactiveState(); double reverse=Read(output);
                Check(double.IsFinite(loud) && double.IsFinite(reverse) && Math.Abs(loud-reverse)>0.00001, $"{type}/{output} did not respond in both directions.");
                var config = JsonSerializer.Deserialize<AppConfig.SimulationLayerConfig>(JsonSerializer.Serialize(BuildSimulationLayerConfig(layer)))!;
                var copy = CloneSimulationLayerSpec(NormalizeSimulationLayerSpec(config,0,new HashSet<Guid>())).ReactiveMappings.Single();
                Check(copy.Amount==mapping.Amount && copy.AttackMs==7 && copy.ReleaseMs==115, "Mapping snapshot lost signed strength/response.");
                _selectedAudioDeviceId=null; ApplySimulationLayerReactiveState();
                Check(layer.ReactiveEnvelopes.Count==0, "Audio disconnection retained envelope state.");
            }
            var presetLayer = new LayerEditorSimulationLayer { LayerType=editorType };
            var preset = SimulationReactivePresets.Create(presetLayer);
            Check(preset.Count>=2 && preset.All(m => SimulationMappingOptions.Supports(editorType,m.Output)), "Preset has unsupported outputs.");
            var clone = preset[0];
            Check(clone.AttackMs==5 && clone.ReleaseMs==80, "Preset response is not initialized.");
            Logger.Info($"Audio response: {type}, {outputs.Length} supported mappings checked for signed travel, envelope propagation and persistence.");
        }
        return true;
    }
}
