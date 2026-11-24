using System;

namespace lifeviz;

// Presentation time and event envelopes, independent of the simulation's GPU step count.
internal readonly record struct PerformanceSimulationFrame(float Time, float Kick, float Snare, float Highs, float Slam, bool Held);

internal sealed class PerformanceSimulationState
{
    public PerformanceSimulationState(ImageSimulationEffect effect, long lastRelease = 0, long lastSlam = 0)
    {
        Effect = effect;
        _lastRelease = lastRelease;
        _lastSlam = lastSlam;
    }
    public ImageSimulationEffect Effect { get; }
    private bool _hadAudio, _bassArmed, _midArmed, _highArmed;
    private double _time, _kick, _snare, _highs, _slam, _heldBass;
    private double _pendingKick, _pendingSnare, _pendingHighs;
    private long _lastBeat, _lastRelease, _lastSlam;

    public PerformanceSimulationFrame Update(double delta, bool audio, long beat, double bass, double mid, double high,
        bool held, long releaseSerial, long slamSerial)
    {
        double dt = double.IsFinite(delta) ? Math.Clamp(delta, 0, 0.25) : 0;
        bool released = releaseSerial > _lastRelease;
        bool slammed = slamSerial > _lastSlam;
        if (released) _lastRelease = releaseSerial;
        if (slammed) _lastSlam = slamSerial;
        bool newBeat = audio && _hadAudio && beat > _lastBeat;
        double kickEvent = 0, snareEvent = 0, highEvent = 0;
        if (audio)
        {
            if (_hadAudio)
            {
                if (BandOnset(ref _bassArmed, bass, 0.32, 0.57) ||
                    (newBeat && bass > 0.28 && bass >= mid * 0.82))
                    kickEvent = 1;
                if (BandOnset(ref _midArmed, mid, 0.29, 0.52)) snareEvent = 1;
                if (BandOnset(ref _highArmed, high, 0.30, 0.54)) highEvent = 1;
            }
            else
            {
                // Observe current levels on connection; never replay stale beats.
                _bassArmed = bass < 0.52;
                _midArmed = mid < 0.48;
                _highArmed = high < 0.50;
            }
        }
        _lastBeat = beat;
        _hadAudio = audio;

        if (held && !released && !slammed)
        {
            _pendingKick = Math.Max(_pendingKick, kickEvent);
            _pendingSnare = Math.Max(_pendingSnare, snareEvent);
            _pendingHighs = Math.Max(_pendingHighs, highEvent);
            return Snapshot(true);
        }

        // Envelopes encode age, giving frame-rate-independent GPU wave fronts.
        _time += dt;
        _kick = Math.Max(kickEvent, Math.Max(0, _kick - dt / 0.70));
        _snare = Math.Max(snareEvent, Math.Max(0, _snare - dt / 0.55));
        _highs = Math.Max(highEvent, Math.Max(0, _highs - dt / 0.28));
        _slam = Math.Max(0, _slam - dt / 0.85);
        _heldBass = audio ? bass : 0;
        if (released)
        {
            _kick = Math.Max(_kick, _pendingKick == 0 ? 1 : _pendingKick);
            _snare = Math.Max(_snare, _pendingSnare);
            _highs = Math.Max(_highs, _pendingHighs);
        }
        if (slammed) _slam = _kick = 1;
        // An aborted hold (disconnect, disable, target change) drops queued hits.
        _pendingKick = _pendingSnare = _pendingHighs = 0;
        return Snapshot(false);
    }

    private PerformanceSimulationFrame Snapshot(bool held)
    {
        double kick = Effect == ImageSimulationEffect.MagnetChoir
            ? Math.Max(_kick, Math.Clamp(_heldBass, 0, 1))
            : _kick;
        return new((float)_time, (float)kick, (float)_snare, (float)_highs, (float)_slam, held);
    }

    private static bool BandOnset(ref bool armed, double value, double low, double high)
    {
        if (value <= low) armed = true;
        if (value < high || !armed) return false;
        armed = false;
        return true;
    }
}
