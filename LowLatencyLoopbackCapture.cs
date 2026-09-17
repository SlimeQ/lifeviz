using NAudio.CoreAudioApi;

namespace lifeviz;

// Event-driven shared-mode loopback on supported Windows 11 endpoints.
internal sealed class LowLatencyLoopbackCapture : WasapiCapture
{
    public LowLatencyLoopbackCapture(MMDevice device) : base(device, true, 20) { }
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
        base.GetAudioClientStreamFlags() | AudioClientStreamFlags.Loopback;
}
