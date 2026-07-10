using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using WinRT;

namespace lifeviz;

internal sealed class AudioRecordingCapture : IDisposable
{
    private const string RenderSelectionPrefix = "render:";
    private readonly object _writerLock = new();
    private readonly string _path;
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _renderDevice;
    private WasapiLoopbackCapture? _loopbackCapture;
    private AudioGraph? _graph;
    private AudioDeviceInputNode? _inputNode;
    private AudioFrameOutputNode? _frameOutputNode;
    private WaveFileWriter? _writer;
    private long _bytesWritten;
    private bool _disposed;

    private AudioRecordingCapture(string path)
    {
        _path = path;
    }

    public bool HasAudio => _bytesWritten > 0;

    public static AudioRecordingCapture Start(string deviceId, string path)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Audio device id is required.", nameof(deviceId));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var capture = new AudioRecordingCapture(path);
        try
        {
            if (TryParseRenderSelectionId(deviceId, out string? renderDeviceId, out bool useSystemDefault))
            {
                capture.StartLoopback(renderDeviceId, useSystemDefault);
            }
            else
            {
                capture.StartInputGraphAsync(deviceId).GetAwaiter().GetResult();
            }

            return capture;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_loopbackCapture != null)
        {
            try
            {
                _loopbackCapture.DataAvailable -= LoopbackCapture_DataAvailable;
                _loopbackCapture.RecordingStopped -= Capture_RecordingStopped;
                _loopbackCapture.StopRecording();
            }
            catch
            {
                // Ignore shutdown races.
            }

            _loopbackCapture.Dispose();
            _loopbackCapture = null;
        }

        if (_graph != null)
        {
            try
            {
                _graph.Stop();
                _graph.QuantumStarted -= Graph_QuantumStarted;
            }
            catch
            {
                // Ignore shutdown races.
            }
        }

        _frameOutputNode?.Dispose();
        _frameOutputNode = null;
        _inputNode?.Dispose();
        _inputNode = null;
        _graph?.Dispose();
        _graph = null;

        _renderDevice?.Dispose();
        _renderDevice = null;
        _deviceEnumerator?.Dispose();
        _deviceEnumerator = null;

        lock (_writerLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void StartLoopback(string? renderDeviceId, bool useSystemDefault)
    {
        _deviceEnumerator = new MMDeviceEnumerator();
        _renderDevice = useSystemDefault
            ? _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : ResolveRenderDevice(renderDeviceId);

        _loopbackCapture = new WasapiLoopbackCapture(_renderDevice);
        _writer = new WaveFileWriter(_path, _loopbackCapture.WaveFormat);
        _loopbackCapture.DataAvailable += LoopbackCapture_DataAvailable;
        _loopbackCapture.RecordingStopped += Capture_RecordingStopped;
        _loopbackCapture.StartRecording();
    }

    private async Task StartInputGraphAsync(string deviceId)
    {
        var settings = new AudioGraphSettings(AudioRenderCategory.Media);
        var result = await AudioGraph.CreateAsync(settings);
        if (result.Status != AudioGraphCreationStatus.Success)
        {
            throw new InvalidOperationException($"AudioGraph creation failed: {result.Status}");
        }

        _graph = result.Graph;
        _graph.QuantumStarted += Graph_QuantumStarted;

        var deviceInfo = await DeviceInformation.CreateFromIdAsync(deviceId);
        var deviceResult = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Media, _graph.EncodingProperties, deviceInfo);
        if (deviceResult.Status != AudioDeviceNodeCreationStatus.Success)
        {
            throw new InvalidOperationException($"Audio input node creation failed: {deviceResult.Status}");
        }

        _inputNode = deviceResult.DeviceInputNode;
        _frameOutputNode = _graph.CreateFrameOutputNode();
        _inputNode.AddOutgoingConnection(_frameOutputNode);

        int sampleRate = (int)Math.Max(1, _graph.EncodingProperties.SampleRate);
        int channels = (int)Math.Max(1, _graph.EncodingProperties.ChannelCount);
        _writer = new WaveFileWriter(_path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));

        _graph.Start();
    }

    private MMDevice ResolveRenderDevice(string? renderDeviceId)
    {
        if (_deviceEnumerator == null || string.IsNullOrWhiteSpace(renderDeviceId))
        {
            throw new InvalidOperationException("Render device id is missing.");
        }

        foreach (var device in _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (string.Equals(device.ID, renderDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        Logger.Warn($"Recording render device not found: {renderDeviceId}. Falling back to default output.");
        return _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private void LoopbackCapture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        lock (_writerLock)
        {
            if (_writer == null)
            {
                return;
            }

            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _bytesWritten += e.BytesRecorded;
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
            if (capacity == 0 || capacity > int.MaxValue)
            {
                return;
            }

            int byteCount = (int)capacity;
            byte[] copy = new byte[byteCount];
            Marshal.Copy(dataPtr, copy, 0, byteCount);

            lock (_writerLock)
            {
                if (_writer == null)
                {
                    return;
                }

                _writer.Write(copy, 0, byteCount);
                _bytesWritten += byteCount;
            }
        }
        catch (ObjectDisposedException)
        {
            // Graph is stopping.
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80000013))
        {
            // Object has been closed.
        }
        catch (Exception ex)
        {
            Logger.Error("Recording audio input frame read failed.", ex);
        }
    }

    private static bool TryParseRenderSelectionId(string selectedId, out string? renderDeviceId, out bool useSystemDefault)
    {
        renderDeviceId = null;
        useSystemDefault = false;
        if (!selectedId.StartsWith(RenderSelectionPrefix, StringComparison.OrdinalIgnoreCase))
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

    private static void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Logger.Error("Recording audio capture stopped unexpectedly.", e.Exception);
        }
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }
}
