using System;
using System.Diagnostics;

namespace lifeviz;

internal sealed partial class AudioBeatDetector
{
    private readonly object _projectMAudioLock = new();
    private readonly float[] _projectMAudio = new float[1024];
    private int _projectMAudioOffset;
    private long _projectMAudioTimestamp;

    private void CaptureProjectMPcm(ReadOnlySpan<float> samples, double gain)
    {
        lock (_projectMAudioLock)
        {
            // All selected inputs converge here, including silent video-stack audio and offline samples.
            foreach (float sample in samples)
            {
                _projectMAudio[_projectMAudioOffset] = float.IsFinite(sample) ? (float)Math.Clamp(sample * gain, -1, 1) : 0;
                _projectMAudioOffset = (_projectMAudioOffset + 1) % _projectMAudio.Length;
            }
            _projectMAudioTimestamp = Stopwatch.GetTimestamp();
        }
    }

    internal void CopyProjectMPcm(float[] destination, bool offline)
    {
        lock (_projectMAudioLock)
        {
            Array.Clear(destination);
            // Loopback capture can stop publishing when the endpoint becomes silent.
            if (_projectMAudioTimestamp == 0 || (!offline && Stopwatch.GetElapsedTime(_projectMAudioTimestamp).TotalSeconds > 0.25)) return;
            int count = Math.Min(destination.Length, _projectMAudio.Length);
            for (int i = 0; i < count; i++)
                destination[destination.Length - count + i] = _projectMAudio[(_projectMAudioOffset + _projectMAudio.Length - count + i) % _projectMAudio.Length];
        }
    }

    private void ResetProjectMPcm()
    {
        lock (_projectMAudioLock)
        {
            Array.Clear(_projectMAudio); _projectMAudioOffset = 0; _projectMAudioTimestamp = 0;
        }
    }
}
