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
    PixelSortCellHeight
}

internal sealed class SimulationReactiveMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SimulationReactiveInput Input { get; set; } = SimulationReactiveInput.Level;
    public SimulationReactiveOutput Output { get; set; } = SimulationReactiveOutput.Opacity;
    public double Amount { get; set; } = 1.0;
    public double ThresholdMin { get; set; }
    public double ThresholdMax { get; set; } = 1.0;

    public SimulationReactiveMapping Clone()
    {
        return new SimulationReactiveMapping
        {
            Id = Id,
            Input = Input,
            Output = Output,
            Amount = Amount,
            ThresholdMin = ThresholdMin,
            ThresholdMax = ThresholdMax
        };
    }
}

internal static class SimulationReactivity
{
    public static readonly IReadOnlyList<SimulationReactiveMapping> EmptyMappings = Array.Empty<SimulationReactiveMapping>();

    public static double ClampAmount(SimulationReactiveOutput output, double amount)
    {
        return output switch
        {
            SimulationReactiveOutput.HueShift => Math.Clamp(amount, 0, 360),
            SimulationReactiveOutput.HueSpeed => Math.Clamp(amount, 0, 180),
            SimulationReactiveOutput.PixelSortCellWidth => Math.Clamp(amount, 0, 50),
            SimulationReactiveOutput.PixelSortCellHeight => Math.Clamp(amount, 0, 50),
            _ => Math.Clamp(amount, 0, 1)
        };
    }

    public static bool RequiresSpectrum(SimulationReactiveInput input)
    {
        return input != SimulationReactiveInput.Level;
    }
}
