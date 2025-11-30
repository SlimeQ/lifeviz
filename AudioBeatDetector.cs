using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace lifeviz;

internal sealed class AudioBeatDetector : IDisposable
{
    private AudioGraph? _graph;
    private AudioDeviceInputNode? _inputNode;
    private AudioFrameOutputNode? _frameOutputNode;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _currentDeviceId;
    
    // Beat Detection State
    private readonly List<double> _energyHistory = new();
    private const int HistorySize = 43; // ~1 second at 60fps-ish polling? No, audio runs faster.
    // Actually, we process audio frames. 
    
    private double _localEnergyAverage;
    private const double C = 1.3; // Threshold constant
    
    // BPM Calculation
    private readonly List<long> _beatTimestamps = new();
    private const int BeatHistorySize = 10;
    private double _detectedBpm = 120;
    private DateTime _lastBeatTime = DateTime.MinValue;

    public double CurrentBpm => _detectedBpm;
    public bool IsBeat { get; private set; }
    public double CurrentEnergy { get; private set; }

    public async Task<IReadOnlyList<AudioDeviceInfo>> EnumerateAudioDevices()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            return devices.Select(d => new AudioDeviceInfo(d.Id, d.Name)).ToList();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to enumerate audio devices", ex);
            return Array.Empty<AudioDeviceInfo>();
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

            StopInternal();

            var settings = new AudioGraphSettings(AudioRenderCategory.Media);
            var result = await AudioGraph.CreateAsync(settings);
            if (result.Status != AudioGraphCreationStatus.Success)
            {
                Logger.Error($"AudioGraph creation failed: {result.Status}");
                return;
            }

            _graph = result.Graph;
            _graph.QuantumStarted += Graph_QuantumStarted;

            var deviceResult = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Media, _graph.EncodingProperties, await DeviceInformation.CreateFromIdAsync(deviceId));
            if (deviceResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                Logger.Error($"Audio device node creation failed: {deviceResult.Status}");
                StopInternal();
                return;
            }

            _inputNode = deviceResult.DeviceInputNode;
            
            _frameOutputNode = _graph.CreateFrameOutputNode();
            _inputNode.AddOutgoingConnection(_frameOutputNode);

            _graph.Start();
            _currentDeviceId = deviceId;
            Logger.Info($"Audio beat detector initialized for device: {deviceId}");
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
            _graph.Stop();
            _graph.QuantumStarted -= Graph_QuantumStarted;
            _graph.Dispose();
            _graph = null;
        }
        _inputNode?.Dispose();
        _inputNode = null;
        _frameOutputNode?.Dispose();
        _frameOutputNode = null;
        _currentDeviceId = null;
    }

    private unsafe void Graph_QuantumStarted(AudioGraph sender, object args)
    {
        if (_frameOutputNode == null) return;

        using var frame = _frameOutputNode.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        ((IMemoryBufferByteAccess)reference).GetBuffer(out IntPtr dataPtr, out uint capacity);
        
        // Assuming float audio (default for AudioGraph)
        float* dataInFloat = (float*)dataPtr;
        int sampleCount = (int)capacity / sizeof(float);
        
        if (sampleCount == 0) return;

        double totalEnergy = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            float sample = dataInFloat[i];
            totalEnergy += sample * sample;
        }
        
        double rms = Math.Sqrt(totalEnergy / sampleCount);
        ProcessEnergy(rms);
    }
    
    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }

    private void ProcessEnergy(double rms)
    {
        CurrentEnergy = rms;

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
        
        if (rms > _localEnergyAverage * C && (DateTime.UtcNow - _lastBeatTime).TotalSeconds > 0.25) // Max 240 BPM
        {
            IsBeat = true;
            var now = DateTime.UtcNow;
            
            // Calculate BPM
            if (_lastBeatTime != DateTime.MinValue)
            {
                long ticks = now.Ticks - _lastBeatTime.Ticks;
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
            
            _lastBeatTime = now;
        }
        else
        {
            IsBeat = false;
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
        public AudioDeviceInfo(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
    }
}
