using System;
using System.Threading;

namespace lifeviz;

/// <summary>
/// Latest tempo/phase measurement from <see cref="BeatTracker"/>. Times are in the
/// tracker's analysis timeline (live: Stopwatch seconds, offline: render timeline).
/// </summary>
internal sealed record BeatEstimate(double Bpm, double BeatTimeSeconds, double Confidence, bool Locked)
{
    public static readonly BeatEstimate None = new(0, 0, 0, false);
}

/// <summary>
/// Audio-thread tempo and beat-phase estimator. Builds a fixed-rate onset-strength
/// envelope from PCM, finds the tempo by autocorrelation folded into a one-octave
/// range (so 70/140 ambiguity always resolves the same way), and finds the beat
/// phase with a comb filter over recent onsets. It publishes immutable
/// <see cref="BeatEstimate"/> snapshots; <see cref="BeatClock"/> follows them smoothly.
/// </summary>
internal sealed class BeatTracker
{
    public const double EnvelopeRate = 100.0;
    public const double DefaultMinBpm = 90.0;
    private const double TempoWindowSeconds = 8.0;
    private const int PhaseWindowBeats = 8;
    // Long enough to score four beats at the slowest tempo (4 x 0.67 s at 90 BPM).
    private const double MinAnalysisSeconds = 4.0;
    private const int EstimateEveryEnvelopeSamples = 10;
    private const double LowPassHz = 150.0;
    private const double FloorDb = -60.0;
    private const double LockConfidence = 0.16;
    private const double UnlockConfidence = 0.08;
    private const double UnlockAfterSeconds = 2.0;
    private const double TempoSwitchSeconds = 3.0;
    private const double TempoSwitchScoreRatio = 1.25;
    private const double RelatedTempoSwitchSeconds = 8.0;
    private const double RelatedTempoSwitchScoreRatio = 1.6;
    private const double RelatedTempoSwitchConfidence = 0.35;

    private readonly float[] _envelope = new float[(int)(TempoWindowSeconds * EnvelopeRate)];
    private readonly double[] _window = new double[(int)(TempoWindowSeconds * EnvelopeRate)];
    private readonly double[] _autocorrelation = new double[(int)(TempoWindowSeconds * EnvelopeRate)];
    private long _envelopeCount;
    private double _lastEnvelopeTime;

    private double _lowState1;
    private double _lowState2;
    private double _hopLowEnergy;
    private double _hopFullEnergy;
    private int _hopFill;
    private double _previousLowDb = FloorDb;
    private double _previousFullDb = FloorDb;

    private double _smoothedPeriod; // envelope samples per beat
    private double _pendingPeriod;
    private double _pendingSinceSeconds;
    private bool _locked;
    private double _lowConfidenceSinceSeconds = double.NaN;
    private double _minBpm = DefaultMinBpm;
    private volatile BeatEstimate _estimate = BeatEstimate.None;

    public BeatEstimate Estimate => _estimate;

    /// <summary>Lower bound of the detected tempo range; the range spans one octave.</summary>
    public double MinBpm
    {
        get => Volatile.Read(ref _minBpm);
        set => Volatile.Write(ref _minBpm, Math.Clamp(value, 40, 150));
    }

    public double MaxBpm => MinBpm * 2.0;

    public void Reset()
    {
        Array.Clear(_envelope);
        _envelopeCount = 0;
        _lastEnvelopeTime = 0;
        _lowState1 = _lowState2 = 0;
        _hopLowEnergy = _hopFullEnergy = 0;
        _hopFill = 0;
        _previousLowDb = _previousFullDb = FloorDb;
        _smoothedPeriod = 0;
        _pendingPeriod = 0;
        _pendingSinceSeconds = 0;
        _locked = false;
        _lowConfidenceSinceSeconds = double.NaN;
        _estimate = BeatEstimate.None;
    }

    /// <summary>Feeds mono PCM whose last sample was captured at <paramref name="endTimeSeconds"/>.</summary>
    public void ProcessSamples(ReadOnlySpan<float> samples, double sampleRate, double gain, double endTimeSeconds)
    {
        if (samples.Length == 0 || sampleRate <= 0)
        {
            return;
        }

        int hopSize = Math.Max(1, (int)Math.Round(sampleRate / EnvelopeRate));
        double lowAlpha = 1.0 - Math.Exp(-2.0 * Math.PI * LowPassHz / sampleRate);
        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples[i] * gain;
            _lowState1 += (x - _lowState1) * lowAlpha;
            _lowState2 += (_lowState1 - _lowState2) * lowAlpha;
            _hopLowEnergy += _lowState2 * _lowState2;
            _hopFullEnergy += x * x;
            if (++_hopFill < hopSize)
            {
                continue;
            }

            // Flux compares this hop with the previous one, so the change it measures
            // sits on the boundary between them: stamp the sample at the hop start.
            double hopEndTime = endTimeSeconds - (samples.Length - 1 - i) / sampleRate;
            EmitEnvelopeSample(hopEndTime - hopSize / sampleRate);
        }
    }

    private void EmitEnvelopeSample(double timeSeconds)
    {
        double lowDb = ToDb(_hopLowEnergy / _hopFill);
        double fullDb = ToDb(_hopFullEnergy / _hopFill);
        _hopLowEnergy = _hopFullEnergy = 0;
        _hopFill = 0;

        // Half-wave rectified log-energy flux; kick-heavy low band weighted highest.
        double flux = Math.Max(0, lowDb - _previousLowDb) + 0.5 * Math.Max(0, fullDb - _previousFullDb);
        _previousLowDb = lowDb;
        _previousFullDb = fullDb;

        _envelope[(int)(_envelopeCount % _envelope.Length)] = (float)flux;
        _envelopeCount++;
        _lastEnvelopeTime = timeSeconds;

        if (_envelopeCount % EstimateEveryEnvelopeSamples == 0 &&
            _envelopeCount >= MinAnalysisSeconds * EnvelopeRate)
        {
            UpdateEstimate();
        }
    }

    private static double ToDb(double meanSquare) =>
        Math.Max(FloorDb, 10.0 * Math.Log10(meanSquare + 1e-12));

    private void UpdateEstimate()
    {
        int n = (int)Math.Min(_envelopeCount, _envelope.Length);
        long first = _envelopeCount - n;
        double mean = 0;
        for (int i = 0; i < n; i++)
        {
            // Light [1 2 1] smoothing makes autocorrelation/comb peaks less spiky.
            long index = first + i;
            double previous = i > 0 ? _envelope[(int)((index - 1) % _envelope.Length)] : _envelope[(int)(index % _envelope.Length)];
            double next = i < n - 1 ? _envelope[(int)((index + 1) % _envelope.Length)] : _envelope[(int)(index % _envelope.Length)];
            double value = 0.25 * previous + 0.5 * _envelope[(int)(index % _envelope.Length)] + 0.25 * next;
            _window[i] = value;
            mean += value;
        }

        mean /= n;
        double energy = 0;
        for (int i = 0; i < n; i++)
        {
            energy += (_window[i] - mean) * (_window[i] - mean);
        }

        double now = _lastEnvelopeTime;
        if (energy / n < 1e-4)
        {
            // Silence or a perfectly static signal: hold the last tempo, report no confidence.
            PublishUnconfident(now);
            return;
        }

        double minBpm = MinBpm;
        double maxBpm = minBpm * 2.0;
        int lagMin = Math.Max(2, (int)Math.Floor(60.0 * EnvelopeRate / maxBpm));
        int lagMax = (int)Math.Ceiling(60.0 * EnvelopeRate / minBpm);
        int maxLag = Math.Min(n - 1, lagMax * 4 + 2);
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = lag; i < n; i++)
            {
                sum += (_window[i] - mean) * (_window[i - lag] - mean);
            }

            _autocorrelation[lag] = sum / (n - lag);
        }

        double zeroLag = Math.Max(1e-9, _autocorrelation[0]);
        int bestLag = -1;
        double bestScore = double.MinValue;
        for (int lag = lagMin; lag <= lagMax; lag++)
        {
            double score = HarmonicScore(lag, maxLag);
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        if (bestLag < 0)
        {
            PublishUnconfident(now);
            return;
        }

        // Hysteresis: once locked, stay on the current tempo unless another one
        // explains the music clearly better. Breakdowns and fills often score a
        // related tempo (4/3, 3/2) slightly higher for a few seconds.
        if (_locked && _smoothedPeriod > 0)
        {
            int currentLag = (int)Math.Round(_smoothedPeriod);
            int currentBest = -1;
            double currentScore = double.MinValue;
            for (int lag = Math.Max(lagMin, currentLag - 1); lag <= Math.Min(lagMax, currentLag + 1); lag++)
            {
                double score = HarmonicScore(lag, maxLag);
                if (score > currentScore)
                {
                    currentScore = score;
                    currentBest = lag;
                }
            }

            double requiredRatio = IsMetricallyRelated(bestLag / _smoothedPeriod)
                ? RelatedTempoSwitchScoreRatio
                : TempoSwitchScoreRatio;
            if (currentBest > 0 && currentScore > 0 && bestScore < currentScore * requiredRatio)
            {
                bestLag = currentBest;
                bestScore = currentScore;
            }
        }

        double period = RefinePeriod(bestLag, maxLag);
        double periodBpm = 60.0 * EnvelopeRate / period;
        if (periodBpm < minBpm || periodBpm >= maxBpm)
        {
            period = 60.0 * EnvelopeRate / Math.Clamp(periodBpm, minBpm, maxBpm - 0.01);
        }

        double confidence = Math.Clamp(bestScore / zeroLag, 0, 1);
        UpdateLockState(confidence, now);
        UpdateSmoothedPeriod(period, confidence, now);
        if (_smoothedPeriod <= 0)
        {
            PublishUnconfident(now);
            return;
        }

        double beatTime = EstimateLastBeatTime(_smoothedPeriod, n);
        _estimate = new BeatEstimate(60.0 * EnvelopeRate / _smoothedPeriod, beatTime, confidence, _locked);
    }

    /// <summary>
    /// Scores a beat period by its first four multiples. A real beat correlates at
    /// every whole number of beats (the bar especially), while a syncopation artefact
    /// such as the 1.5-beat kick spacing of a breakbeat only correlates at some.
    /// </summary>
    private double HarmonicScore(int lag, int maxLag)
    {
        double score = 0;
        int terms = 0;
        for (int multiple = 1; multiple <= 4; multiple++)
        {
            if (lag * multiple > maxLag)
            {
                break;
            }

            score += LocalPeak(lag * multiple, multiple / 2, maxLag);
            terms++;
        }

        return terms > 0 ? score / terms : double.MinValue;
    }

    private double LocalPeak(int centre, int radius, int maxLag)
    {
        double best = double.MinValue;
        for (int lag = Math.Max(1, centre - radius); lag <= Math.Min(maxLag, centre + radius); lag++)
        {
            best = Math.Max(best, _autocorrelation[lag]);
        }

        return best;
    }

    /// <summary>
    /// The base lag is only accurate to ±0.5 envelope samples (~±1% tempo), so refine
    /// it with the 2x and 4x multiples, whose peaks pin the period down 2-4x tighter.
    /// </summary>
    private double RefinePeriod(int bestLag, int maxLag)
    {
        double weighted = 0;
        double weights = 0;
        foreach (int multiple in new[] { 1, 2, 3, 4 })
        {
            int centre = bestLag * multiple;
            if (centre + 1 > maxLag)
            {
                continue;
            }

            int peak = centre;
            for (int lag = Math.Max(1, centre - multiple); lag <= Math.Min(maxLag - 1, centre + multiple); lag++)
            {
                if (_autocorrelation[lag] > _autocorrelation[peak])
                {
                    peak = lag;
                }
            }

            double strength = _autocorrelation[peak];
            if (strength <= 0 || peak <= 1)
            {
                continue;
            }

            double refined = peak + ParabolicOffset(_autocorrelation[peak - 1], strength, _autocorrelation[peak + 1]);
            double weight = multiple * strength;
            weighted += refined / multiple * weight;
            weights += weight;
        }

        return weights > 0 ? weighted / weights : bestLag;
    }

    private static double ParabolicOffset(double left, double centre, double right)
    {
        double denominator = left - 2 * centre + right;
        if (Math.Abs(denominator) < 1e-12)
        {
            return 0;
        }

        return Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
    }

    private void UpdateLockState(double confidence, double now)
    {
        if (confidence >= LockConfidence)
        {
            _locked = true;
            _lowConfidenceSinceSeconds = double.NaN;
            return;
        }

        if (confidence >= UnlockConfidence || !_locked)
        {
            _lowConfidenceSinceSeconds = double.NaN;
            return;
        }

        if (double.IsNaN(_lowConfidenceSinceSeconds))
        {
            _lowConfidenceSinceSeconds = now;
        }
        else if (now - _lowConfidenceSinceSeconds >= UnlockAfterSeconds)
        {
            _locked = false;
        }
    }

    private void UpdateSmoothedPeriod(double period, double confidence, double now)
    {
        if (_smoothedPeriod <= 0 || !_locked)
        {
            // Not locked yet: follow the measurement directly, no switching hysteresis.
            _smoothedPeriod = confidence >= LockConfidence ? period : 0;
            _pendingPeriod = 0;
            return;
        }

        double ratio = period / _smoothedPeriod;
        if (Math.Abs(ratio - 1.0) <= 0.03)
        {
            // Same tempo: average out lag quantisation noise.
            _smoothedPeriod += (period - _smoothedPeriod) * (0.1 + 0.2 * confidence);
            _pendingPeriod = 0;
            return;
        }

        // A genuinely different tempo has to persist before we jump to it, so one
        // ambiguous window (fills, breakdowns) cannot yank every synced layer.
        if (_pendingPeriod <= 0 || Math.Abs(period / _pendingPeriod - 1.0) > 0.03)
        {
            _pendingPeriod = period;
            _pendingSinceSeconds = now;
            return;
        }

        _pendingPeriod += (period - _pendingPeriod) * 0.3;
        bool related = IsMetricallyRelated(_pendingPeriod / _smoothedPeriod);
        if (related && confidence < RelatedTempoSwitchConfidence)
        {
            // Syncopated sections (breaks, halftime, triplet percussion) read as 3:4,
            // 4:3, 2:3... of the real tempo; only a strong, sustained reading may switch.
            _pendingSinceSeconds = now;
            return;
        }

        double holdSeconds = related ? RelatedTempoSwitchSeconds : TempoSwitchSeconds;
        if (now - _pendingSinceSeconds >= holdSeconds && confidence >= LockConfidence)
        {
            _smoothedPeriod = _pendingPeriod;
            _pendingPeriod = 0;
        }
    }

    private static bool IsMetricallyRelated(double ratio)
    {
        foreach (double related in RelatedRatios)
        {
            if (Math.Abs(ratio / related - 1.0) < 0.025)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly double[] RelatedRatios =
    {
        3.0 / 2.0, 2.0 / 3.0, 4.0 / 3.0, 3.0 / 4.0, 5.0 / 4.0, 4.0 / 5.0, 6.0 / 5.0, 5.0 / 6.0
    };

    /// <summary>
    /// Comb filter: try every phase offset within one period and sum the onset
    /// envelope at the implied beat times over the last two bars. The best offset is
    /// the most recent beat.
    /// </summary>
    private double EstimateLastBeatTime(double period, int n)
    {
        // Sum over whole bars with equal weight. A partial or decaying window sees a
        // different mix of kick/snare/hat beats as it slides through each bar, which
        // makes syncopated patterns wobble by tens of milliseconds.
        int beats = PhaseWindowBeats;
        while (beats > BeatClock.BeatsPerBar && (beats * period) >= n - 1)
        {
            beats -= BeatClock.BeatsPerBar;
        }
        double bestOffset = 0;
        double bestSum = double.MinValue;
        int steps = (int)Math.Ceiling(period);
        Span<double> sums = steps <= 512 ? stackalloc double[steps] : new double[steps];
        for (int offset = 0; offset < steps; offset++)
        {
            double sum = 0;
            for (int k = 0; k < beats; k++)
            {
                double position = n - 1 - offset - k * period;
                if (position < 1)
                {
                    break;
                }

                sum += Interpolate(position, n);
            }

            sums[offset] = sum;
            if (sum > bestSum)
            {
                bestSum = sum;
                bestOffset = offset;
            }
        }

        int best = (int)bestOffset;
        if (steps >= 3)
        {
            double left = sums[(best - 1 + steps) % steps];
            double right = sums[(best + 1) % steps];
            bestOffset += ParabolicOffset(left, sums[best], right);
        }

        return _lastEnvelopeTime - bestOffset / EnvelopeRate;
    }

    private double Interpolate(double position, int n)
    {
        int index = (int)Math.Floor(position);
        double fraction = position - index;
        double a = _window[Math.Clamp(index, 0, n - 1)];
        double b = _window[Math.Clamp(index + 1, 0, n - 1)];
        return a + (b - a) * fraction;
    }

    private void PublishUnconfident(double now)
    {
        UpdateLockState(0, now);
        BeatEstimate previous = _estimate;
        _estimate = previous with { Confidence = 0, Locked = _locked };
    }
}

/// <summary>
/// Render-thread musical clock. Its beat position only ever moves forward and changes
/// rate smoothly: it free-runs at the current tempo and, when following audio, slews
/// its phase toward the tracker's estimate instead of jumping. Beat 0 of a bar is
/// wherever the user last resynced the downbeat.
/// </summary>
internal sealed class BeatClock
{
    public const int BeatsPerBar = 4;
    private const double PhaseCorrectionSeconds = 0.6;
    private const double MaxPhaseCorrection = 0.25;
    private const double TempoGlideSeconds = 0.35;
    private const double MaxStepSeconds = 1.0;
    private const double LargePhaseError = 0.2;
    private const double LargePhaseErrorHoldSeconds = 1.5;
    private double _largeErrorSince = double.NaN;

    private sealed record State(double Now, double Position, double BeatsPerSecond, bool AudioLocked);

    private volatile State _state = new(0, 0, 140.0 / 60.0, false);
    private double _downbeatOffset;
    private bool _initialized;
    private bool _hasPhaseLock;

    public double Bpm => _state.BeatsPerSecond * 60.0;
    public bool AudioLocked => _state.AudioLocked;

    public void Reset(double now = 0)
    {
        State state = _state;
        _state = new State(now, 0, state.BeatsPerSecond, false);
        Volatile.Write(ref _downbeatOffset, 0);
        _initialized = false;
        _hasPhaseLock = false;
        _largeErrorSince = double.NaN;
    }

    /// <summary>Advances the clock to <paramref name="now"/>. Call once per render tick.</summary>
    /// <param name="fallbackBpm">Tempo used when no audio estimate is being followed.</param>
    /// <param name="estimate">Tracker estimate to follow, or null for a free-running manual clock.</param>
    /// <param name="analysisNow">Current time in the tracker's timeline.</param>
    public void Update(double now, double fallbackBpm, BeatEstimate? estimate, double analysisNow)
    {
        State state = _state;
        double dt = now - state.Now;
        if (!_initialized || dt < 0 || double.IsNaN(dt))
        {
            // First tick or a timeline switch (e.g. offline render start): rebase, no jump.
            dt = 0;
            _initialized = true;
        }
        else if (dt > MaxStepSeconds)
        {
            // Long stall: don't extrapolate blindly; re-acquire audio phase directly.
            dt = 0;
            _hasPhaseLock = false;
        }
        double bps = state.BeatsPerSecond;
        double position = state.Position;
        bool audioLocked = estimate is { Locked: true, Bpm: > 0 };
        if (audioLocked)
        {
            double targetBps = estimate!.Bpm / 60.0;
            bps += (targetBps - bps) * Math.Min(1.0, dt / TempoGlideSeconds);
            double targetPhase = Fraction((analysisNow - estimate.BeatTimeSeconds) * targetBps);
            if (!_hasPhaseLock)
            {
                // First lock: jump forward to the measured phase.
                bps = targetBps;
                double snapped = Math.Floor(position) + targetPhase;
                position = snapped < position ? snapped + 1.0 : snapped;
                _hasPhaseLock = true;
            }
            else
            {
                // Advance first so the error compares like with like (both at `now`);
                // comparing last tick's position would settle one frame early.
                position += bps * dt;
                double error = WrapHalf(targetPhase - Fraction(position));
                if (Math.Abs(error) > LargePhaseError)
                {
                    // Syncopated sections can flip the estimate onto an off-beat for a
                    // moment; only chase a large phase error once it has persisted.
                    if (double.IsNaN(_largeErrorSince))
                    {
                        _largeErrorSince = now;
                    }

                    if (now - _largeErrorSince < LargePhaseErrorHoldSeconds)
                    {
                        error = 0;
                    }
                }
                else
                {
                    _largeErrorSince = double.NaN;
                }

                double correction = Math.Clamp(error / PhaseCorrectionSeconds, -MaxPhaseCorrection * bps, MaxPhaseCorrection * bps);
                position += correction * dt;
            }
        }
        else if (estimate != null && _hasPhaseLock)
        {
            // Following audio but the tracker lost confidence: hold tempo and phase.
            position += bps * dt;
        }
        else
        {
            bps = Math.Clamp(fallbackBpm, 10, 300) / 60.0;
            position += bps * dt;
            if (estimate == null)
            {
                _hasPhaseLock = false;
            }
        }

        _state = new State(now, position, bps, audioLocked);
    }

    /// <summary>Beat position at <paramref name="time"/>, extrapolated from the last tick.</summary>
    public double GetPosition(double time)
    {
        State state = _state;
        double extrapolated = state.Position + (time - state.Now) * state.BeatsPerSecond;
        return Math.Max(0, extrapolated);
    }

    /// <summary>Beat position measured from the last resynced downbeat (beat 0 = bar start).</summary>
    public double GetBarAlignedPosition(double time) => GetPosition(time) - Volatile.Read(ref _downbeatOffset);

    /// <summary>Marks the nearest beat as beat 1 of a bar.</summary>
    public void ResyncDownbeat()
    {
        Volatile.Write(ref _downbeatOffset, Math.Round(_state.Position));
    }

    internal static double Fraction(double value) => value - Math.Floor(value);

    private static double WrapHalf(double value)
    {
        value = Fraction(value + 0.5) - 0.5;
        return value;
    }
}
