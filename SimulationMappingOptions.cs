namespace lifeviz;

internal static class SimulationMappingOptions
{
    internal static bool Supports(LayerEditorSimulationLayerType type, string output)
    {
        if (!Enum.TryParse<SimulationReactiveOutput>(output, out var value)) return false;
        if (value is SimulationReactiveOutput.Opacity or SimulationReactiveOutput.Framerate
            or SimulationReactiveOutput.HueShift or SimulationReactiveOutput.HueSpeed) return true;
        return type switch
        {
            LayerEditorSimulationLayerType.Life => value is SimulationReactiveOutput.InjectionNoise or SimulationReactiveOutput.ThresholdMin or SimulationReactiveOutput.ThresholdMax,
            LayerEditorSimulationLayerType.PixelSort => value is SimulationReactiveOutput.PixelSortCellWidth or SimulationReactiveOutput.PixelSortCellHeight,
            LayerEditorSimulationLayerType.Datamosh => value is SimulationReactiveOutput.DatamoshFeedback or SimulationReactiveOutput.DatamoshDisplacement,
            LayerEditorSimulationLayerType.FluidInk => value == SimulationReactiveOutput.FluidFlow,
            LayerEditorSimulationLayerType.TimeDisplacement => value == SimulationReactiveOutput.TimeSpread,
            LayerEditorSimulationLayerType.ReactionDiffusion => value == SimulationReactiveOutput.ReactionSeed,
            LayerEditorSimulationLayerType.FeedbackKaleidoscope => value is SimulationReactiveOutput.KaleidoscopeFeedback or SimulationReactiveOutput.KaleidoscopeZoom or SimulationReactiveOutput.KaleidoscopeRotation,
            _ => false
        };
    }
}
