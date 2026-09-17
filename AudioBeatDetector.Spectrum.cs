using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace lifeviz;

internal sealed partial class AudioBeatDetector
{
    private readonly double[] _spectrumSamples = new double[1024];
    private int _spectrumWriteIndex;
    private long _lastPcmTimestamp;
    private bool _smokeInputActive;
    internal bool HasFreshReactiveSamples => _offlineInputActive || _smokeInputActive ||
        (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastPcmTimestamp)) / (double)Stopwatch.Frequency < 0.1;

    private void ProcessSpectrumSamples(ReadOnlySpan<float> samples, double gain, bool enabled)
    {
        if (!enabled)
        {
            Array.Clear(_spectrumSamples); _spectrumWriteIndex = 0; _spectrumAnalysisAccumulator = 0;
            ClearSpectrumLevels(); return;
        }
        double hop = Math.Max(1, _sampleRate / SpectrumAnalysisRateHz);
        foreach (float sample in samples)
        {
            _spectrumSamples[_spectrumWriteIndex] = Math.Clamp(sample * gain, -1, 1);
            _spectrumWriteIndex = (_spectrumWriteIndex + 1) % _spectrumSamples.Length;
            if (++_spectrumAnalysisAccumulator < hop) continue;
            _spectrumAnalysisAccumulator -= hop;
            double power = 0;
            for (int i = 0; i < _fftBuffer.Length; i++)
            {
                double value = _spectrumSamples[(_spectrumWriteIndex + i) % _spectrumSamples.Length];
                power += value * value;
                double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (_fftBuffer.Length - 1)));
                _fftBuffer[i] = new Complex(value * window, 0);
            }
            if (power / _fftBuffer.Length < SignalFloorRms * SignalFloorRms)
            {
                ClearSpectrumLevels(); continue;
            }
            CalculateFFT(_fftBuffer, _fftBuffer.Length);
            AnalyzeSpectrum(_fftBuffer, _fftBuffer.Length);
        }
    }

    private void ClearSpectrumLevels()
    {
        MainFrequency = BassFrequency = MidFrequency = HighFrequency = BassEnergy = 0;
        BassNormalizedLevel = MidNormalizedLevel = HighNormalizedLevel = 0;
        MainFrequencyNormalized = BassFrequencyNormalized = MidFrequencyNormalized = HighFrequencyNormalized = 0;
    }
}
