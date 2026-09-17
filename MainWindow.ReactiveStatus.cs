using System;
using System.Linq;

namespace lifeviz;

public partial class MainWindow
{
    internal string GetSimulationMappingStatus(Guid layerId, Guid mappingId)
    {
        var layer = FindSimulationNode(layerId);
        var mapping = layer?.ReactiveMappings.FirstOrDefault(m => m.Id == mappingId);
        if (layer == null || mapping == null) return "Not applied";
        for (var ancestor=layer; ancestor!=null; ancestor=ancestor.Parent)
            if (!ancestor.Enabled) return "Simulation or parent group disabled";
        if (!HasReactiveAudioInput()) return "Choose an Audio Source to drive this mapping";
        if (!_audioBeatDetector.HasFreshReactiveSamples) return "Waiting for audio samples";
        if (layer.LayerType == SimulationLayerType.Life &&
            ((layer.LifeMode == GameOfLifeEngine.LifeMode.Bitwise && mapping.Output is SimulationReactiveOutput.ThresholdMin or SimulationReactiveOutput.ThresholdMax) ||
             (layer.LifeMode == GameOfLifeEngine.LifeMode.NaiveGrayscale && mapping.Output is SimulationReactiveOutput.HueShift or SimulationReactiveOutput.HueSpeed)))
            return "Not used in this Life mode";
        double response = layer.ReactiveEnvelopes.TryGetValue(mappingId, out var envelope) ? envelope.Value : 0;
        double effective = mapping.Output switch
        {
            SimulationReactiveOutput.Opacity => layer.EffectiveLifeOpacity,
            SimulationReactiveOutput.Framerate => layer.EffectiveSimulationTargetFps,
            SimulationReactiveOutput.HueShift => CurrentRgbHueShiftDegrees(layer),
            SimulationReactiveOutput.HueSpeed => layer.EffectiveRgbHueShiftSpeedDegreesPerSecond,
            SimulationReactiveOutput.InjectionNoise => layer.EffectiveInjectionNoise,
            SimulationReactiveOutput.ThresholdMin => layer.EffectiveThresholdMin,
            SimulationReactiveOutput.ThresholdMax => layer.EffectiveThresholdMax,
            SimulationReactiveOutput.PixelSortCellWidth => layer.EffectivePixelSortCellWidth,
            SimulationReactiveOutput.PixelSortCellHeight => layer.EffectivePixelSortCellHeight,
            SimulationReactiveOutput.DatamoshFeedback => layer.EffectiveDatamoshFeedback,
            SimulationReactiveOutput.DatamoshDisplacement => layer.EffectiveDatamoshDisplacement,
            _ => typeof(SimulationEffectSettings).GetProperty(mapping.Output.ToString())?.GetValue(layer.EffectiveEffects) is double value ? value : 0
        };
        string valueText = mapping.Output switch
        {
            SimulationReactiveOutput.Framerate => $"{effective:0.#} fps",
            SimulationReactiveOutput.HueShift => $"{effective:0.#}°",
            SimulationReactiveOutput.HueSpeed => $"{effective:0.#}°/s",
            SimulationReactiveOutput.ReactionFeed or SimulationReactiveOutput.ReactionKill or SimulationReactiveOutput.KaleidoscopeZoom => $"{effective:0.0000}",
            SimulationReactiveOutput.PixelSortCellWidth or SimulationReactiveOutput.PixelSortCellHeight or SimulationReactiveOutput.KaleidoscopeFolds or SimulationReactiveOutput.TimeScale or SimulationReactiveOutput.KaleidoscopeRotation => $"{effective:0.##}",
            _ => $"{effective:P0}"
        };
        double input=GetReactiveInputValue(mapping.Input);
        string clipping=input>=Math.Max(mapping.ThresholdMin,mapping.ThresholdMax) ? " · Input at Max" : "";
        string blend=mapping.Output==SimulationReactiveOutput.Opacity && layer.BlendMode==BlendMode.Subtractive ? " · Subtractive darkens" : "";
        return $"Input {input:P0} → Response {response:P0} · Live output {valueText}{clipping}{blend}";
    }
}
