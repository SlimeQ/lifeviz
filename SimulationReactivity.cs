using System;
using System.Collections.Generic;

namespace lifeviz;

internal enum SimulationReactiveInput
{
    Level,
    Bass,
    Mid,
    High,
    Frequency,
    BassFrequency,
    MidFrequency,
    HighFrequency
}

internal enum SimulationReactiveOutput
{
    Opacity,
    Framerate,
    HueShift,
    HueSpeed,
    InjectionNoise,
    ThresholdMin,
    ThresholdMax,
    PixelSortCellWidth,
    PixelSortCellHeight,
    DatamoshFeedback,
    DatamoshDisplacement,
    FluidFlow,
    TimeSpread,
    ReactionSeed,
    KaleidoscopeFeedback,
    KaleidoscopeZoom,
    KaleidoscopeRotation,
    ParticleEmission,
    ParticleTurbulence,
    RippleImpulse,
    RippleRefraction,
    ChromaticRed,
    ChromaticGreen,
    ChromaticBlue,
    ContourFlow,
    ContourThickness,
    FluidPersistence,
    FluidSwirl,
    TimeScale,
    TimeMotion,
    ReactionFeed,
    ReactionKill,
    KaleidoscopeFolds,
    KaleidoscopeCenterX,
    KaleidoscopeCenterY,
    ParticleGravity,
    ParticlePersistence,
    RippleSpeed,
    RippleDamping,
    ChromaticDrift,
    ContourPersistence
}

internal sealed class SimulationReactiveMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SimulationReactiveInput Input { get; set; } = SimulationReactiveInput.Level;
    public SimulationReactiveOutput Output { get; set; } = SimulationReactiveOutput.Opacity;
    public double Amount { get; set; } = 1.0;
    public double ThresholdMin { get; set; }
    public double ThresholdMax { get; set; } = 1.0;
    public double AttackMs { get; set; } = 5;
    public double ReleaseMs { get; set; } = 80;

    public SimulationReactiveMapping Clone()
    {
        return new SimulationReactiveMapping
        {
            Id = Id,
            Input = Input,
            Output = Output,
            Amount = Amount,
            ThresholdMin = ThresholdMin,
            ThresholdMax = ThresholdMax,
            AttackMs = AttackMs,
            ReleaseMs = ReleaseMs
        };
    }
}

internal static class SimulationReactivity
{
    public static readonly IReadOnlyList<SimulationReactiveMapping> EmptyMappings = Array.Empty<SimulationReactiveMapping>();

    public static double ClampAttack(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 150) : 5;
    public static double ClampRelease(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 500) : 80;

    public static double MaximumAmount(SimulationReactiveOutput output) => output switch
    {
        SimulationReactiveOutput.KaleidoscopeZoom => 0.1,
        SimulationReactiveOutput.KaleidoscopeRotation => 10,
        SimulationReactiveOutput.HueShift => 360,
        SimulationReactiveOutput.HueSpeed => 180,
        SimulationReactiveOutput.PixelSortCellWidth or SimulationReactiveOutput.PixelSortCellHeight => 50,
        SimulationReactiveOutput.TimeScale => 11.5,
        SimulationReactiveOutput.ReactionFeed => 0.07,
        SimulationReactiveOutput.ReactionKill => 0.045,
        SimulationReactiveOutput.KaleidoscopeFolds => 14,
        SimulationReactiveOutput.ParticleGravity => 2,
        SimulationReactiveOutput.RippleDamping => 0.099,
        _ => 1
    };

    public static double ClampAmount(SimulationReactiveOutput output, double amount) =>
        Math.Clamp(double.IsFinite(amount) ? amount : 0, -MaximumAmount(output), MaximumAmount(output));

    public static bool RequiresSpectrum(SimulationReactiveInput input)
    {
        return input != SimulationReactiveInput.Level;
    }
}
