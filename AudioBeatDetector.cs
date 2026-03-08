using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using System.Runtime.InteropServices;
using WinRT;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace lifeviz;

internal sealed class AudioBeatDetector : IDisposable
{
    private const double MinInputGain = 0.0;
    private const double MaxInputGain = 2.0;
    private const int AudioDebugHistorySeconds = 30;
    private const int AudioDebugHistorySampleRate = 120;
    private const int WaveformHistorySize = AudioDebugHistorySeconds * AudioDebugHistorySampleRate;
    private const double SpectrumAnalysisRateHz = 60.0;
    private AudioGraph? _graph;
    private AudioDeviceInputNode? _inputNode;
    private AudioFrameOutputNode? _frameOutputNode;
    private MMDeviceEnumerator? _wasapiDeviceEnumerator;
    private MMDevice? _wasapiRenderDevice;
    private WasapiLoopbackCapture? _wasapiLoopbackCapture;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _waveformLock = new();
    private string? _currentDeviceId;
    private double _inputGain = 1.0;
    
    // Beat Detection State
    private readonly List<double> _energyHistory = new();
    private const int HistorySize = 60; // Longer history for better average
    
    private double _localEnergyAverage;
    private const double C = 1.18; // Threshold constant
    
    // BPM Calculation
    private readonly List<long> _beatTimestamps = new();
    private const int BeatHistorySize = 10;
    private double _detectedBpm = 120;
    private const double SignalFloorRms = 0.0025;
    private const double NormalizationFloorDb = -54.0;
    private const double NormalizationCeilingDb = -18.0;
    private const double EnvelopeNormalizationFloorDb = -42.0;
    private const double EnvelopeNormalizationCeilingDb = -6.0;
    private const double PeakNormalizationFloorDb = -42.0;
    private const double PeakNormalizationCeilingDb = -6.0;
    private const string RenderSelectionPrefix = "render:";
    private const string DefaultRenderSelectionId = "render:default";
    private const double EnergyBaselineFollow = 0.22;
    private const double TransientBaselineScale = 1.00;
    private const double TransientFloor = 0.015;
    private const double TransientBoost = 9.0;
    private double _energyBaseline;
    private readonly float[] _waveformHistory = new float[WaveformHistorySize];
    private readonly float[] _waveformMinHistory = new float[WaveformHistorySize];
    private readonly float[] _waveformMaxHistory = new float[WaveformHistorySize];
    private readonly float[] _envelopeHistory = new float[WaveformHistorySize];
    private readonly float[] _bassEnergyHistory = new float[WaveformHistorySize];
    private readonly float[] _mainFrequencyHistory = new float[WaveformHistorySize];
    private readonly float[] _bassFrequencyHistory = new float[WaveformHistorySize];
    private readonly float[] _midFrequencyHistory = new float[WaveformHistorySize];
    private readonly float[] _highFrequencyHistory = new float[WaveformHistorySize];
    private int _waveformHistoryWriteIndex;
    private int _waveformHistoryCount;
    private double _waveformHistoryAccumulator;
    private double _waveformBucketAbsMax;
    private double _waveformBucketMin = 1.0;
    private double _waveformBucketMax = -1.0;
    private double _spectrumAnalysisAccumulator;
    private volatile bool _enableSpectrumAnalysis;
    private volatile bool _enableDebugHistory;
    
    public DateTime LastBeatTime { get; private set; } = DateTime.MinValue;
    public long BeatCount { get; private set; }

    public double CurrentBpm => _detectedBpm;
    public bool IsBeat { get; private set; }
    public double CurrentEnergy { get; private set; }
    public double NormalizedEnergy { get; private set; }
    public double EnvelopeEnergy { get; private set; }
    public double PeakNormalizedEnergy { get; private set; }
    public double TransientEnergy { get; private set; }
    public double MainFrequency { get; private set; }
    public double BassFrequency { get; private set; }
    public double MidFrequency { get; private set; }
    public double HighFrequency { get; private set; }
    public double BassEnergy { get; private set; }
    public bool IsRunning => (_graph != null && _inputNode != null) || _wasapiLoopbackCapture != null;

    public double InputGain
    {
        get => _inputGain;
        set => _inputGain = Math.Clamp(value, MinInputGain, MaxInputGain);
    }

    private uint _sampleRate = 48000;

    public async Task<IReadOnlyList<AudioDeviceInfo>> EnumerateAudioDevices()
    {
        try
        {
            var devices = new List<AudioDeviceInfo>
            {
                new(DefaultRenderSelectionId, "System Output (Default)", AudioDeviceInfo.AudioDeviceKind.Render, isSystemDefault: true)
            };

            try
            {
                var renderDevices = await Task.Run(() =>
                {
                    using var renderEnumerator = new MMDeviceEnumerator();
                    return renderEnumerator
                        .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                        .Select(d => new AudioDeviceInfo(
                            BuildRenderSelectionId(d.ID),
                            $"Output: {d.FriendlyName}",
                            AudioDeviceInfo.AudioDeviceKind.Render))
                        .ToList();
                });
                devices.AddRange(renderDevices);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to enumerate render devices via WASAPI; falling back to WinRT. {ex.Message}");
                var renderDevices = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
                devices.AddRange(renderDevices.Select(d =>
                    new AudioDeviceInfo(BuildRenderSelectionId(d.Id), $"Output: {d.Name}", AudioDeviceInfo.AudioDeviceKind.Render)));
            }

            var captureDevices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            devices.AddRange(captureDevices.Select(d =>
                new AudioDeviceInfo(d.Id, $"Input: {d.Name}", AudioDeviceInfo.AudioDeviceKind.Capture)));

            return devices;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to enumerate audio devices", ex);
            return Array.Empty<AudioDeviceInfo>();
        }
    }

    public float[] GetWaveformHistory()
    {
        lock (_waveformLock)
        {
            var history = new float[_waveformHistoryCount];
            if (_waveformHistoryCount == 0)
            {
                return history;
            }

            int start = (_waveformHistoryWriteIndex - _waveformHistoryCount + WaveformHistorySize) % WaveformHistorySize;
            for (int i = 0; i < _waveformHistoryCount; i++)
            {
                history[i] = _waveformHistory[(start + i) % WaveformHistorySize];
            }

            return history;
        }
    }

    public (float[] minHistory, float[] maxHistory) GetWaveformRangeHistory()
    {
        lock (_waveformLock)
        {
            var minHistory = new float[_waveformHistoryCount];
            var maxHistory = new float[_waveformHistoryCount];
            if (_waveformHistoryCount == 0)
            {
                return (minHistory, maxHistory);
            }

            int start = (_waveformHistoryWriteIndex - _waveformHistoryCount + WaveformHistorySize) % WaveformHistorySize;
            for (int i = 0; i < _waveformHistoryCount; i++)
            {
                int index = (start + i) % WaveformHistorySize;
                minHistory[i] = _waveformMinHistory[index];
                maxHistory[i] = _waveformMaxHistory[index];
            }

            return (minHistory, maxHistory);
        }
    }

    public float[] GetEnvelopeHistory()
    {
        lock (_waveformLock)
        {
            var history = new float[_waveformHistoryCount];
            if (_waveformHistoryCount == 0)
            {
                return history;
            }

            int start = (_waveformHistoryWriteIndex - _waveformHistoryCount + WaveformHistorySize) % WaveformHistorySize;
            for (int i = 0; i < _waveformHistoryCount; i++)
            {
                history[i] = _envelopeHistory[(start + i) % WaveformHistorySize];
            }

            return history;
        }
    }

    public float[] GetBassEnergyHistory()
    {
        lock (_waveformLock)
        {
            var history = new float[_waveformHistoryCount];
            if (_waveformHistoryCount == 0)
            {
                return history;
            }

            int start = (_waveformHistoryWriteIndex - _waveformHistoryCount + WaveformHistorySize) % WaveformHistorySize;
            for (int i = 0; i < _waveformHistoryCount; i++)
            {
                history[i] = _bassEnergyHistory[(start + i) % WaveformHistorySize];
            }

            return history;
        }
    }

    public float[] GetMainFrequencyHistory()
    {
        lock (_waveformLock)
        {
            var history = new float[_waveformHistoryCount];
            if (_waveformHistoryCount == 0)
            {
                return history;
            }

            int start = (_waveformHistoryWriteIndex - _waveformHistoryCount + WaveformHistorySize) % WaveformHistorySize;
            for (int i = 0; i < _waveformHistoryCount; i++)
            {
                history[i] = _mainFrequencyHistory[(start + i) % WaveformHistorySize];
            }

            return history;
        }
    }

    public float[] GetBassFrequencyHistory()
    {
        lock (_waveformLock)
        {
            var history = new float[_waveformHistoryCount];
            if (_waveformHistoryCount == 0)
            {
                return history;
            }

            int start = (_waveformHistoryWriteIndex - _waveformHistoryCount + WaveformHistorySize) % WaveformHistorySize;
            for (int i = 0; i < _waveformHistoryCount; i++)
            {
                history[i] = _bassFrequencyHistory[(start + i) % WaveformHistorySize];
            }

            return history;
        }
    }

    public float[] GetMidFrequencyHistory()
    {
        lock (_waveformLock)
        {
            var history = new float[_waveformHistoryCount];
            if (_waveformHistoryCount == 0)
            {
                return history;
            }

            int start = (_waveformHistoryWriteIndex - _waveformHistoryCount + WaveformHistorySize) % WaveformHistorySize;
            for (int i = 0; i < _waveformHistoryCount; i++)
            {
                history[i] = _midFrequencyHistory[(start + i) % WaveformHistorySize];
            }

            return history;
        }
    }

    public float[] GetHighFrequencyHistory()
    {
        lock (_waveformLock)
        {
            var history = new float[_waveformHistoryCount];
            if (_waveformHistoryCount == 0)
            {
                return history;
            }

            int start = (_waveformHistoryWriteIndex - _waveformHistoryCount + WaveformHistorySize) % WaveformHistorySize;
            for (int i = 0; i < _waveformHistoryCount; i++)
            {
                history[i] = _highFrequencyHistory[(start + i) % WaveformHistorySize];
            }

            return history;
        }
    }

    public async Task InitializeAsync(string deviceId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_currentDeviceId == deviceId && _graph != null)
            {
                return;
            }
            if (_currentDeviceId == deviceId && _wasapiLoopbackCapture != null)
            {
                return;
            }

            StopInternal();
            ResetState();

            if (TryParseRenderSelectionId(deviceId, out string? renderDeviceId, out bool useSystemDefault))
            {
                StartWasapiLoopback(deviceId, renderDeviceId, useSystemDefault);
                return;
            }

            var settings = new AudioGraphSettings(AudioRenderCategory.Media);
            var result = await AudioGraph.CreateAsync(settings);
            if (result.Status != AudioGraphCreationStatus.Success)
            {
                Logger.Error($"AudioGraph creation failed: {result.Status}");
                return;
            }

            _graph = result.Graph;
            _sampleRate = _graph.EncodingProperties.SampleRate;
            _graph.QuantumStarted += Graph_QuantumStarted;

            var deviceInfo = await DeviceInformation.CreateFromIdAsync(deviceId);
            var deviceResult = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Media, _graph.EncodingProperties, deviceInfo);
            if (deviceResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                Logger.Error($"Audio device node creation failed ({deviceId}): {deviceResult.Status}");
                StopInternal();
                return;
            }

            _inputNode = deviceResult.DeviceInputNode;
            
            _frameOutputNode = _graph.CreateFrameOutputNode();
            _inputNode.AddOutgoingConnection(_frameOutputNode);

            _graph.Start();
            _currentDeviceId = deviceId;
            Logger.Info($"Audio beat detector initialized for input device: {deviceId}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to initialize audio beat detector for {deviceId}", ex);
            StopInternal();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void StopInternal()
    {
        if (_graph != null)
        {
            try
            {
                _graph.Stop();
            }
            catch (ObjectDisposedException)
            {
                // Ignore shutdown races.
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013))
            {
                // Object has been closed.
            }

            _graph.QuantumStarted -= Graph_QuantumStarted;
            try
            {
                _graph.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Ignore shutdown races.
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013))
            {
                // Object has been closed.
            }
            _graph = null;
        }
        if (_inputNode != null)
        {
            try
            {
                _inputNode.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Ignore shutdown races.
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013))
            {
                // Object has been closed.
            }
            _inputNode = null;
        }
        if (_frameOutputNode != null)
        {
            try
            {
                _frameOutputNode.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Ignore shutdown races.
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013))
            {
                // Object has been closed.
            }
            _frameOutputNode = null;
        }

        if (_wasapiLoopbackCapture != null)
        {
            try
            {
                _wasapiLoopbackCapture.DataAvailable -= WasapiLoopbackCapture_DataAvailable;
                _wasapiLoopbackCapture.RecordingStopped -= WasapiLoopbackCapture_RecordingStopped;
                _wasapiLoopbackCapture.StopRecording();
            }
            catch
            {
                // Ignore shutdown races.
            }
            _wasapiLoopbackCapture.Dispose();
            _wasapiLoopbackCapture = null;
        }
        _wasapiRenderDevice?.Dispose();
        _wasapiRenderDevice = null;
        _wasapiDeviceEnumerator?.Dispose();
        _wasapiDeviceEnumerator = null;

        _currentDeviceId = null;
    }

    private void ResetState()
    {
        _energyHistory.Clear();
        _beatTimestamps.Clear();
        _localEnergyAverage = 0;
        _detectedBpm = 120;
        LastBeatTime = DateTime.MinValue;
        IsBeat = false;
        CurrentEnergy = 0;
        NormalizedEnergy = 0;
        EnvelopeEnergy = 0;
        PeakNormalizedEnergy = 0;
        TransientEnergy = 0;
        MainFrequency = 0;
        BassFrequency = 0;
        MidFrequency = 0;
        HighFrequency = 0;
        _energyBaseline = 0;
        BeatCount = 0;
        lock (_waveformLock)
        {
            Array.Clear(_waveformHistory);
            Array.Clear(_waveformMinHistory);
            Array.Clear(_waveformMaxHistory);
            Array.Clear(_envelopeHistory);
            Array.Clear(_bassEnergyHistory);
            Array.Clear(_mainFrequencyHistory);
            Array.Clear(_bassFrequencyHistory);
            Array.Clear(_midFrequencyHistory);
            Array.Clear(_highFrequencyHistory);
            _waveformHistoryWriteIndex = 0;
            _waveformHistoryCount = 0;
            _waveformHistoryAccumulator = 0;
            _waveformBucketAbsMax = 0;
            _waveformBucketMin = 1.0;
            _waveformBucketMax = -1.0;
            _spectrumAnalysisAccumulator = 0;
        }
    }

    private unsafe void Graph_QuantumStarted(AudioGraph sender, object args)
    {
        var outputNode = _frameOutputNode;
        if (outputNode == null)
        {
            return;
        }

        try
        {
            using var frame = outputNode.GetFrame();
            using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
            using var reference = buffer.CreateReference();

            reference.As<IMemoryBufferByteAccess>().GetBuffer(out IntPtr dataPtr, out uint capacity);

            // Assuming float audio (default for AudioGraph)
            float* dataInFloat = (float*)dataPtr;
            int sampleCount = (int)capacity / sizeof(float);

            if (sampleCount == 0)
            {
                return;
            }

            var samples = new ReadOnlySpan<float>(dataInFloat, sampleCount);
            ProcessPcmSamples(samples);
        }
        catch (ObjectDisposedException)
        {
            // Audio graph shut down while a quantum callback was in-flight.
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013))
        {
            // Object has been closed - ignore during device swaps/shutdown.
        }
        catch (Exception ex)
        {
            Logger.Error("Audio beat detector frame read failed", ex);
        }
    }

    private void ProcessPcmSamples(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        bool enableSpectrumAnalysis = _enableSpectrumAnalysis;
        bool enableDebugHistory = _enableDebugHistory;
        double inputGain = _inputGain;
        double totalEnergy = 0;
        double totalAbsolute = 0;
        double peakAmplitude = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double scaledSample = Math.Clamp(samples[i] * inputGain, -1.0, 1.0);
            totalEnergy += scaledSample * scaledSample;
            totalAbsolute += Math.Abs(scaledSample);
            peakAmplitude = Math.Max(peakAmplitude, Math.Abs(scaledSample));
        }

        double rms = Math.Sqrt(totalEnergy / samples.Length);
        double meanAbsolute = totalAbsolute / samples.Length;
        bool hasSignal = rms >= SignalFloorRms;
        if (enableDebugHistory)
        {
            AppendWaveformHistory(samples, inputGain);
        }
        else
        {
            _waveformHistoryAccumulator = 0;
            _waveformBucketAbsMax = 0;
            _waveformBucketMin = 1.0;
            _waveformBucketMax = -1.0;
        }

        if (enableSpectrumAnalysis)
        {
            _spectrumAnalysisAccumulator += samples.Length;
        }
        else
        {
            _spectrumAnalysisAccumulator = 0;
        }

        double requiredSpectrumSamples = Math.Max(1.0, _sampleRate / SpectrumAnalysisRateHz);
        if (enableSpectrumAnalysis && hasSignal && samples.Length >= 256 && _spectrumAnalysisAccumulator >= requiredSpectrumSamples)
        {
            _spectrumAnalysisAccumulator -= requiredSpectrumSamples;
            int fftSize = 1024;
            while (fftSize > samples.Length) fftSize /= 2;

            if (fftSize >= 256)
            {
                var fftBuffer = new Complex[fftSize];
                int start = samples.Length - fftSize;
                for (int i = 0; i < fftSize; i++)
                {
                    double sample = Math.Clamp(samples[start + i] * inputGain, -1.0, 1.0);
                    double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (fftSize - 1)));
                    fftBuffer[i] = new Complex(sample * window, 0);
                }

                CalculateFFT(fftBuffer);
                AnalyzeSpectrum(fftBuffer, fftSize);
            }
        }
        else if (!enableSpectrumAnalysis || !hasSignal)
        {
            MainFrequency = 0;
            BassFrequency = 0;
            MidFrequency = 0;
            HighFrequency = 0;
            BassEnergy = 0;
        }

        ProcessEnergy(meanAbsolute, rms, peakAmplitude);
    }

    private void AppendWaveformHistory(ReadOnlySpan<float> samples, double inputGain)
    {
        if (samples.Length == 0)
        {
            return;
        }

        var appendedWaveform = new List<float>(Math.Max(4, samples.Length / 256));
        var appendedWaveformMin = new List<float>(Math.Max(4, samples.Length / 256));
        var appendedWaveformMax = new List<float>(Math.Max(4, samples.Length / 256));
        var appendedEnvelope = new List<float>(Math.Max(4, samples.Length / 256));
        var appendedBassEnergy = new List<float>(Math.Max(4, samples.Length / 256));
        var appendedMainFrequency = new List<float>(Math.Max(4, samples.Length / 256));
        var appendedBassFrequency = new List<float>(Math.Max(4, samples.Length / 256));
        var appendedMidFrequency = new List<float>(Math.Max(4, samples.Length / 256));
        var appendedHighFrequency = new List<float>(Math.Max(4, samples.Length / 256));
        for (int i = 0; i < samples.Length; i++)
        {
            double scaledSample = Math.Clamp(samples[i] * inputGain, -1.0, 1.0);
            _waveformBucketAbsMax = Math.Max(_waveformBucketAbsMax, Math.Abs(scaledSample));
            _waveformBucketMin = Math.Min(_waveformBucketMin, scaledSample);
            _waveformBucketMax = Math.Max(_waveformBucketMax, scaledSample);
            _waveformHistoryAccumulator += AudioDebugHistorySampleRate;
            if (_waveformHistoryAccumulator >= _sampleRate)
            {
                appendedWaveform.Add((float)scaledSample);
                appendedWaveformMin.Add((float)_waveformBucketMin);
                appendedWaveformMax.Add((float)_waveformBucketMax);
                float envelope = (float)_waveformBucketAbsMax;
                appendedEnvelope.Add(envelope);
                appendedBassEnergy.Add((float)BassEnergy);
                appendedMainFrequency.Add((float)MainFrequency);
                appendedBassFrequency.Add((float)BassFrequency);
                appendedMidFrequency.Add((float)MidFrequency);
                appendedHighFrequency.Add((float)HighFrequency);
                _waveformHistoryAccumulator -= _sampleRate;
                _waveformBucketAbsMax = 0;
                _waveformBucketMin = 1.0;
                _waveformBucketMax = -1.0;
            }
        }

        if (appendedWaveform.Count == 0)
        {
            return;
        }

        lock (_waveformLock)
        {
            for (int i = 0; i < appendedWaveform.Count; i++)
            {
                _waveformHistory[_waveformHistoryWriteIndex] = appendedWaveform[i];
                _waveformMinHistory[_waveformHistoryWriteIndex] = appendedWaveformMin[i];
                _waveformMaxHistory[_waveformHistoryWriteIndex] = appendedWaveformMax[i];
                _envelopeHistory[_waveformHistoryWriteIndex] = appendedEnvelope[i];
                _bassEnergyHistory[_waveformHistoryWriteIndex] = appendedBassEnergy[i];
                _mainFrequencyHistory[_waveformHistoryWriteIndex] = appendedMainFrequency[i];
                _bassFrequencyHistory[_waveformHistoryWriteIndex] = appendedBassFrequency[i];
                _midFrequencyHistory[_waveformHistoryWriteIndex] = appendedMidFrequency[i];
                _highFrequencyHistory[_waveformHistoryWriteIndex] = appendedHighFrequency[i];
                _waveformHistoryWriteIndex = (_waveformHistoryWriteIndex + 1) % WaveformHistorySize;
                if (_waveformHistoryCount < WaveformHistorySize)
                {
                    _waveformHistoryCount++;
                }
            }
        }
    }
    
    private void AnalyzeSpectrum(Complex[] fftBuffer, int fftSize)
    {
        double maxMagnitude = 0;
        int maxIndex = 0;
        double bassSum = 0;
        double bassMaxMagnitude = 0;
        int bassMaxIndex = 0;
        double midMaxMagnitude = 0;
        int midMaxIndex = 0;
        double highMaxMagnitude = 0;
        int highMaxIndex = 0;
        
        // Frequencies up to Nyquist (SampleRate / 2)
        // Bin resolution = SampleRate / fftSize
        double binRes = (double)_sampleRate / fftSize;
        
        // Define frequency bands for overlay/debug traces.
        int bassStartBin = (int)(20 / binRes);
        int bassEndBin = (int)(250 / binRes);
        int midStartBin = Math.Max(bassEndBin + 1, (int)(250 / binRes));
        int midEndBin = (int)(2000 / binRes);
        int highStartBin = Math.Max(midEndBin + 1, (int)(2000 / binRes));
        int highEndBin = (int)(8000 / binRes);
        
        // Only need to check first half (positive frequencies)
        int halfSize = fftSize / 2;
        
        for (int i = 1; i < halfSize; i++) // Skip DC component at 0
        {
            double magnitude = fftBuffer[i].Magnitude;
            if (magnitude > maxMagnitude)
            {
                maxMagnitude = magnitude;
                maxIndex = i;
            }
            
            if (i >= bassStartBin && i <= bassEndBin)
            {
                bassSum += magnitude;
            }

            if (i >= bassStartBin && i <= bassEndBin && magnitude > bassMaxMagnitude)
            {
                bassMaxMagnitude = magnitude;
                bassMaxIndex = i;
            }

            if (i >= midStartBin && i <= midEndBin && magnitude > midMaxMagnitude)
            {
                midMaxMagnitude = magnitude;
                midMaxIndex = i;
            }

            if (i >= highStartBin && i <= highEndBin && magnitude > highMaxMagnitude)
            {
                highMaxMagnitude = magnitude;
                highMaxIndex = i;
            }
        }
        
        MainFrequency = maxIndex * binRes;
        BassFrequency = bassMaxIndex > 0 ? bassMaxIndex * binRes : 0;
        MidFrequency = midMaxIndex > 0 ? midMaxIndex * binRes : 0;
        HighFrequency = highMaxIndex > 0 ? highMaxIndex * binRes : 0;
        
        int bassBins = bassEndBin - bassStartBin + 1;
        if (bassBins > 0)
        {
            // Calculate average magnitude in bass range and normalize it
            // For a 1024 FFT, magnitude can be up to ~256-512.
            // Let's normalize it so 1.0 is a very strong bass.
            double avgBassMag = bassSum / bassBins;
            BassEnergy = Math.Clamp(avgBassMag / 50.0, 0, 10); 
        }
        else
        {
            BassEnergy = 0;
        }
    }

    private static void CalculateFFT(Complex[] buffer)
    {
        int n = buffer.Length;
        int m = (int)Math.Log2(n);

        // Bit reversal
        int j = 0;
        for (int i = 0; i < n - 1; i++)
        {
            if (i < j)
            {
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }
            int k = n / 2;
            while (k <= j)
            {
                j -= k;
                k /= 2;
            }
            j += k;
        }

        // Butterfly
        for (int s = 1; s <= m; s++)
        {
            int m2 = 1 << s;
            int halfM2 = m2 / 2;
            Complex wm = Complex.FromPolarCoordinates(1, -Math.PI / halfM2);
            
            for (int k = 0; k < n; k += m2)
            {
                Complex w = 1;
                for (int sub = 0; sub < halfM2; sub++)
                {
                    Complex t = w * buffer[k + sub + halfM2];
                    Complex u = buffer[k + sub];
                    buffer[k + sub] = u + t;
                    buffer[k + sub + halfM2] = u - t;
                    w *= wm;
                }
            }
        }
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }

    private void ProcessEnergy(double meanAbsolute, double rms, double peakAmplitude)
    {
        CurrentEnergy = rms;
        NormalizedEnergy = NormalizeAmplitude(rms, NormalizationFloorDb, NormalizationCeilingDb);
        EnvelopeEnergy = NormalizeAmplitude(meanAbsolute, EnvelopeNormalizationFloorDb, EnvelopeNormalizationCeilingDb);
        PeakNormalizedEnergy = NormalizeAmplitude(peakAmplitude, PeakNormalizationFloorDb, PeakNormalizationCeilingDb);

        // Build a transient-focused envelope relative to a short local baseline so
        // sustained music settles close to zero while drum hits still spike clearly.
        _energyBaseline += (NormalizedEnergy - _energyBaseline) * EnergyBaselineFollow;
        double transient = NormalizedEnergy - (_energyBaseline * TransientBaselineScale) - TransientFloor;
        if (NormalizedEnergy < 0.025)
        {
            transient = 0;
        }
        TransientEnergy = Math.Clamp(transient * TransientBoost, 0, 1);

        // Maintain moving average of energy
        _energyHistory.Add(rms);
        if (_energyHistory.Count > HistorySize)
        {
            _energyHistory.RemoveAt(0);
        }

        _localEnergyAverage = _energyHistory.Average();
        // Simple variance/threshold logic could be added here
        
        // Simple Beat Detection: Instant energy > C * Average Energy
        // And wait some time (debounce)
        
        if (TransientEnergy > 0.06 &&
            rms > _localEnergyAverage * C &&
            (DateTime.UtcNow - LastBeatTime).TotalSeconds > 0.08) // Max 750 BPM
        {
            IsBeat = true;
            var now = DateTime.UtcNow;
            BeatCount++;
            
            // Calculate BPM
            if (LastBeatTime != DateTime.MinValue)
            {
                long ticks = now.Ticks - LastBeatTime.Ticks;
                _beatTimestamps.Add(ticks);
                if (_beatTimestamps.Count > BeatHistorySize)
                {
                    _beatTimestamps.RemoveAt(0);
                }
                
                if (_beatTimestamps.Count >= 2)
                {
                    double avgTicks = _beatTimestamps.Average();
                    double secondsPerBeat = TimeSpan.FromTicks((long)avgTicks).TotalSeconds;
                    if (secondsPerBeat > 0)
                    {
                        double rawBpm = 60.0 / secondsPerBeat;
                        // Smooth/Clamp
                        _detectedBpm = rawBpm; // Simple for now, can apply EWMA later
                    }
                }
            }
            
            LastBeatTime = now;
        }
        else
        {
            IsBeat = false;
        }
    }

    private static double NormalizeAmplitude(double amplitude, double floorDb, double ceilingDb)
    {
        double clampedAmplitude = Math.Max(amplitude, 0.000001);
        double db = 20.0 * Math.Log10(clampedAmplitude);
        double normalizationRange = ceilingDb - floorDb;
        if (normalizationRange <= 0.000001)
        {
            return 0;
        }

        return Math.Clamp((db - floorDb) / normalizationRange, 0, 1);
    }

    public void Stop()
    {
        _lock.Wait();
        try
        {
            StopInternal();
            ResetState();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Wait();
        try
        {
            StopInternal();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }

    internal sealed class AudioDeviceInfo
    {
        internal enum AudioDeviceKind
        {
            Capture,
            Render
        }

        public AudioDeviceInfo(string id, string name, AudioDeviceKind kind, bool isSystemDefault = false)
        {
            Id = id;
            Name = name;
            Kind = kind;
            IsSystemDefault = isSystemDefault;
        }

        public string Id { get; }
        public string Name { get; }
        public AudioDeviceKind Kind { get; }
        public bool IsSystemDefault { get; }
    }

    public void SetAnalysisRequirements(bool enableSpectrumAnalysis, bool enableDebugHistory)
    {
        bool normalizedSpectrum = enableSpectrumAnalysis || enableDebugHistory;
        bool clearDebugHistory = _enableDebugHistory && !enableDebugHistory;

        _enableSpectrumAnalysis = normalizedSpectrum;
        _enableDebugHistory = enableDebugHistory;

        if (!normalizedSpectrum)
        {
            MainFrequency = 0;
            BassFrequency = 0;
            MidFrequency = 0;
            HighFrequency = 0;
            BassEnergy = 0;
        }

        if (clearDebugHistory)
        {
            lock (_waveformLock)
            {
                Array.Clear(_waveformHistory);
                Array.Clear(_waveformMinHistory);
                Array.Clear(_waveformMaxHistory);
                Array.Clear(_envelopeHistory);
                Array.Clear(_bassEnergyHistory);
                Array.Clear(_mainFrequencyHistory);
                Array.Clear(_bassFrequencyHistory);
                Array.Clear(_midFrequencyHistory);
                Array.Clear(_highFrequencyHistory);
                _waveformHistoryWriteIndex = 0;
                _waveformHistoryCount = 0;
                _waveformHistoryAccumulator = 0;
                _waveformBucketAbsMax = 0;
                _waveformBucketMin = 1.0;
                _waveformBucketMax = -1.0;
            }
        }
    }

    private static string BuildRenderSelectionId(string deviceId) => $"{RenderSelectionPrefix}{deviceId}";

    internal static bool IsRenderSelectionId(string? selectedId) =>
        !string.IsNullOrWhiteSpace(selectedId) &&
        selectedId.StartsWith(RenderSelectionPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseRenderSelectionId(string selectedId, out string? renderDeviceId, out bool useSystemDefault)
    {
        renderDeviceId = null;
        useSystemDefault = false;
        if (!IsRenderSelectionId(selectedId))
        {
            return false;
        }

        string suffix = selectedId[RenderSelectionPrefix.Length..];
        if (string.Equals(suffix, "default", StringComparison.OrdinalIgnoreCase))
        {
            useSystemDefault = true;
            return true;
        }

        renderDeviceId = suffix;
        return !string.IsNullOrWhiteSpace(renderDeviceId);
    }

    private void StartWasapiLoopback(string selectionId, string? renderDeviceId, bool useSystemDefault)
    {
        _wasapiDeviceEnumerator = new MMDeviceEnumerator();
        _wasapiRenderDevice = useSystemDefault
            ? _wasapiDeviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : ResolveWasapiRenderDevice(renderDeviceId);

        _wasapiLoopbackCapture = new WasapiLoopbackCapture(_wasapiRenderDevice);
        _sampleRate = (uint)_wasapiLoopbackCapture.WaveFormat.SampleRate;
        _wasapiLoopbackCapture.DataAvailable += WasapiLoopbackCapture_DataAvailable;
        _wasapiLoopbackCapture.RecordingStopped += WasapiLoopbackCapture_RecordingStopped;
        _wasapiLoopbackCapture.StartRecording();
        _currentDeviceId = selectionId;
        Logger.Info($"Audio beat detector initialized for output loopback: {selectionId}");
    }

    private MMDevice ResolveWasapiRenderDevice(string? renderDeviceId)
    {
        if (_wasapiDeviceEnumerator == null || string.IsNullOrWhiteSpace(renderDeviceId))
        {
            throw new InvalidOperationException("Render device ID is missing.");
        }

        var devices = _wasapiDeviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            if (string.Equals(device.ID, renderDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        Logger.Warn($"Render device not found in WASAPI endpoint list: {renderDeviceId}. Falling back to default output.");
        return _wasapiDeviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private void WasapiLoopbackCapture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        var capture = _wasapiLoopbackCapture;
        if (capture == null || e.BytesRecorded <= 0)
        {
            return;
        }

        try
        {
            _sampleRate = (uint)capture.WaveFormat.SampleRate;
            int channels = Math.Max(1, capture.WaveFormat.Channels);
            var bufferSpan = e.Buffer.AsSpan(0, e.BytesRecorded);

            if (capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && capture.WaveFormat.BitsPerSample == 32)
            {
                var allSamples = MemoryMarshal.Cast<byte, float>(bufferSpan);
                ProcessInterleavedSamples(allSamples, channels);
                return;
            }

            if (capture.WaveFormat.BitsPerSample == 16)
            {
                var allSamples16 = MemoryMarshal.Cast<byte, short>(bufferSpan);
                ProcessInterleavedPcm16Samples(allSamples16, channels);
                return;
            }

            Logger.Warn($"Unsupported loopback audio format: {capture.WaveFormat.Encoding} {capture.WaveFormat.BitsPerSample}-bit");
        }
        catch (Exception ex)
        {
            Logger.Error("Loopback capture frame read failed", ex);
        }
    }

    private void ProcessInterleavedSamples(ReadOnlySpan<float> interleaved, int channels)
    {
        if (interleaved.Length == 0)
        {
            return;
        }

        if (channels <= 1)
        {
            ProcessPcmSamples(interleaved);
            return;
        }

        int frames = interleaved.Length / channels;
        if (frames <= 0)
        {
            return;
        }

        float[] monoBuffer = ArrayPool<float>.Shared.Rent(frames);
        try
        {
            for (int frame = 0; frame < frames; frame++)
            {
                int baseIndex = frame * channels;
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    sum += interleaved[baseIndex + ch];
                }
                monoBuffer[frame] = sum / channels;
            }

            ProcessPcmSamples(monoBuffer.AsSpan(0, frames));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(monoBuffer);
        }
    }

    private void ProcessInterleavedPcm16Samples(ReadOnlySpan<short> interleaved, int channels)
    {
        if (interleaved.Length == 0)
        {
            return;
        }

        int frames = interleaved.Length / Math.Max(1, channels);
        if (frames <= 0)
        {
            return;
        }

        float[] monoBuffer = ArrayPool<float>.Shared.Rent(frames);
        try
        {
            if (channels <= 1)
            {
                for (int i = 0; i < frames; i++)
                {
                    monoBuffer[i] = interleaved[i] / 32768f;
                }
            }
            else
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    int baseIndex = frame * channels;
                    int sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += interleaved[baseIndex + ch];
                    }
                    monoBuffer[frame] = (sum / (float)channels) / 32768f;
                }
            }

            ProcessPcmSamples(monoBuffer.AsSpan(0, frames));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(monoBuffer);
        }
    }

    private void WasapiLoopbackCapture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Logger.Error("Output loopback capture stopped unexpectedly", e.Exception);
        }
    }
}
