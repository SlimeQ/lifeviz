using System;
using System.Collections.ObjectModel;

namespace lifeviz;

internal static class SimulationReactivePresets
{
    public static ObservableCollection<LayerEditorSimulationReactiveMapping> Create(LayerEditorSimulationLayer layer)
    {
        var result = new ObservableCollection<LayerEditorSimulationReactiveMapping>();
        void Add(string input, string output, double amount) => result.Add(new()
        {
            Id = Guid.NewGuid(), Input = input, Output = output, Amount = amount,
            ThresholdMin = 0.08, ThresholdMax = 0.85, AttackMs = 5, ReleaseMs = 80
        });
        // Choose the direction with room to move. Presets do not change the base controls.
        double Travel(double value, double min = 0, double max = 1) =>
            (max - value >= value - min ? max - value : min - value) * 0.85;
        var s = layer.Effects;
        switch (layer.LayerType)
        {
            case LayerEditorSimulationLayerType.Life:
                if (layer.LifeMode == "Bitwise") { Add("Bass", "Opacity", 0.65); Add("Mid", "HueSpeed", 120); break; }
                Add("Bass", "ThresholdMin", -layer.ThresholdMin * 0.85);
                Add("Mid", layer.LifeMode == "NaiveGrayscale" ? "Opacity" : "HueSpeed", layer.LifeMode == "NaiveGrayscale" ? 0.65 : 120); break;
            case LayerEditorSimulationLayerType.PixelSort:
                Add("Bass", "PixelSortCellWidth", 32); Add("Mid", "PixelSortCellHeight", 20); break;
            case LayerEditorSimulationLayerType.Datamosh:
                Add("Bass", "DatamoshFeedback", Travel(layer.DatamoshFeedback, 0, 0.98));
                Add("Mid", "DatamoshDisplacement", Travel(layer.DatamoshDisplacement)); break;
            case LayerEditorSimulationLayerType.FluidInk:
                Add("Bass", "FluidFlow", Travel(s.FluidFlow)); Add("Mid", "FluidSwirl", Travel(s.FluidSwirl)); break;
            case LayerEditorSimulationLayerType.TimeDisplacement:
                Add("Bass", "TimeSpread", Travel(s.TimeSpread)); Add("Mid", "TimeMotion", Travel(s.TimeMotion)); break;
            case LayerEditorSimulationLayerType.ReactionDiffusion:
                Add("Bass", "ReactionSeed", Travel(s.ReactionSeed)); Add("Mid", "ReactionFeed", 0.012); break;
            case LayerEditorSimulationLayerType.FeedbackKaleidoscope:
                Add("Bass", "KaleidoscopeZoom", s.KaleidoscopeZoom <= 1.04 ? 0.06 : -0.06);
                Add("Mid", "KaleidoscopeRotation", s.KaleidoscopeRotation <= 4 ? 5 : -5); break;
            case LayerEditorSimulationLayerType.ParticleErosion:
                Add("Bass", "ParticleEmission", Travel(s.ParticleEmission)); Add("Mid", "ParticleTurbulence", Travel(s.ParticleTurbulence)); break;
            case LayerEditorSimulationLayerType.RippleField:
                Add("Bass", "RippleImpulse", Travel(s.RippleImpulse)); Add("Mid", "RippleRefraction", Travel(s.RippleRefraction)); break;
            case LayerEditorSimulationLayerType.ChromaticMemory:
                Add("Bass", "ChromaticRed", -s.ChromaticRed * 0.75); Add("Mid", "ChromaticGreen", -s.ChromaticGreen * 0.75);
                Add("High", "ChromaticBlue", -s.ChromaticBlue * 0.75); break;
            case LayerEditorSimulationLayerType.ContourCurrent:
                Add("Bass", "ContourFlow", Travel(s.ContourFlow)); Add("High", "ContourPersistence", -s.ContourPersistence * 0.6); break;
        }
        return result;
    }
}
