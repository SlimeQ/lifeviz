using System;

namespace lifeviz;

internal static class PerformanceSimulationStateSmoke
{
    internal static int Run()
    {
        static void Check(bool okay, string message)
        {
            if (!okay) throw new InvalidOperationException(message);
        }

        var fx = new PerformanceSimulationState(ImageSimulationEffect.Faultline);
        PerformanceSimulationFrame Step(double dt, bool audio = true, long beat = 0,
            double low = 0, double mid = 0, double high = 0, bool hold = false, long release = 0, long slam = 0)
            => fx.Update(dt, audio, beat, low, mid, high, hold, release, slam);

        Check(Step(0.01, beat: 42, low: 1).Kick == 0, "Stale beat replayed on attach.");
        Check(Step(0.01, beat: 43, low: 1).Kick == 1, "Bass-driven beat did not hit.");
        Check(Step(0.01, beat: 43, low: 1).Kick < 1, "One beat was replayed across frames.");
        Step(0.25); Step(0.25); Step(0.25);
        var midOnly = Step(0.01, mid: 1);
        Check(midOnly.Snare == 1 && midOnly.Kick == 0 && midOnly.Highs == 0, "Mid-only onset fired another band.");
        var highOnly = Step(0.01, high: 1);
        Check(highOnly.Highs == 1 && highOnly.Snare < 1 && highOnly.Kick == 0, "High-only accent failed.");
        var hold1 = Step(0.1, low: 1, hold: true);
        var hold2 = Step(0.1, hold: true);
        Check(hold1 == hold2 && hold2.Held, "Hold did not freeze simulation state.");
        Check(Step(0.1, release: 3).Kick == 1, "Release did not fire the accumulated kick.");
        Check(Step(0.1, release: 3).Kick < 1, "Release fired twice.");
        Check(Step(0.1, audio: false, slam: 4).Slam == 1, "Slam needs no audio.");
        Check(Step(0.1, audio: false, slam: 4).Slam < 1, "Slam fired twice.");

        static PerformanceSimulationFrame AtRate(int hz)
        {
            var state = new PerformanceSimulationState(ImageSimulationEffect.Stampede);
            state.Update(0, true, 0, 0, 0, 0, false, 0, 0);
            state.Update(0, true, 1, 1, 0, 0, false, 0, 0);
            PerformanceSimulationFrame output = default;
            for (int i = 0; i < hz / 2; i++)
                output = state.Update(1d / hz, true, 1, 0, 0, 0, false, 0, 0);
            return output;
        }
        Check(Math.Abs(AtRate(30).Kick - AtRate(120).Kick) < 0.002, "Kick timing depends on frame rate.");
        return 0;
    }
}
