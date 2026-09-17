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
            LayerEditorSimulationLayerType.FluidInk => value is SimulationReactiveOutput.FluidFlow or SimulationReactiveOutput.FluidPersistence or SimulationReactiveOutput.FluidSwirl,
            LayerEditorSimulationLayerType.TimeDisplacement => value is SimulationReactiveOutput.TimeSpread or SimulationReactiveOutput.TimeScale or SimulationReactiveOutput.TimeMotion,
            LayerEditorSimulationLayerType.ReactionDiffusion => value is SimulationReactiveOutput.ReactionSeed or SimulationReactiveOutput.ReactionFeed or SimulationReactiveOutput.ReactionKill,
            LayerEditorSimulationLayerType.FeedbackKaleidoscope => value is SimulationReactiveOutput.KaleidoscopeFeedback or SimulationReactiveOutput.KaleidoscopeZoom or SimulationReactiveOutput.KaleidoscopeRotation or SimulationReactiveOutput.KaleidoscopeFolds or SimulationReactiveOutput.KaleidoscopeCenterX or SimulationReactiveOutput.KaleidoscopeCenterY,
            LayerEditorSimulationLayerType.ParticleErosion => value is SimulationReactiveOutput.ParticleEmission or SimulationReactiveOutput.ParticleTurbulence or SimulationReactiveOutput.ParticleGravity or SimulationReactiveOutput.ParticlePersistence,
            LayerEditorSimulationLayerType.RippleField => value is SimulationReactiveOutput.RippleImpulse or SimulationReactiveOutput.RippleRefraction or SimulationReactiveOutput.RippleSpeed or SimulationReactiveOutput.RippleDamping,
            LayerEditorSimulationLayerType.ChromaticMemory => value is SimulationReactiveOutput.ChromaticRed or SimulationReactiveOutput.ChromaticGreen or SimulationReactiveOutput.ChromaticBlue or SimulationReactiveOutput.ChromaticDrift,
            LayerEditorSimulationLayerType.ContourCurrent => value is SimulationReactiveOutput.ContourFlow or SimulationReactiveOutput.ContourThickness or SimulationReactiveOutput.ContourPersistence,
            _ => false
        };
    }
}
