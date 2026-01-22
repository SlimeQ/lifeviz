using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using WinRT;
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
    
    public DateTime LastBeatTime { get; private set; } = DateTime.MinValue;
    public long BeatCount { get; private set; }

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
            ResetState();

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
        BeatCount = 0;
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

            double totalEnergy = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                float sample = dataInFloat[i];
                totalEnergy += sample * sample;
            }

            double rms = Math.Sqrt(totalEnergy / sampleCount);
            ProcessEnergy(rms);
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
        
        if (rms > _localEnergyAverage * C && (DateTime.UtcNow - LastBeatTime).TotalSeconds > 0.25) // Max 240 BPM
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
        public AudioDeviceInfo(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
    }
}
