using System;

namespace lifeviz;

// One causal envelope per authored mapping, after its threshold window.
internal sealed class ReactiveEnvelope
{
    public double Value { get; private set; }
    public double Process(double target, double seconds, double attackMs, double releaseMs)
    {
        target = double.IsFinite(target) ? Math.Clamp(target, 0, 1) : 0;
        double milliseconds = target > Value ? attackMs : releaseMs;
        if (seconds <= 0 || milliseconds <= 0) return Value = target;
        double mix = 1 - Math.Exp(-Math.Clamp(seconds, 0, 1) * 1000 / milliseconds);
        Value += (target - Value) * mix;
        if (Value < 0.00001) Value = 0;
        return Value;
    }
}
