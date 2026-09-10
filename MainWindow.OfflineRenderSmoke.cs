using System.IO;

namespace lifeviz;

public partial class MainWindow
{
    internal async Task<string> RunFixedDurationForSmokeAsync(string directory, bool cancel)
    {
        ConfigureProfilingSmokeScene(144);
        string? previousFolder = _recordingOutputFolder;
        RecordingQuality previousQuality = _recordingQuality;
        int previousInteractionCount = _uiInteractionSuspendCount;
        var progress = new SmokeOfflineProgress(cancel);
        try
        {
            _recordingOutputFolder = directory;
            _recordingQuality = RecordingQuality.LosslessCompatible;
            // An open editor interaction must not skip virtual-clock frames.
            _uiInteractionSuspendCount = 1;
            await RunOfflineRenderAsync(progress, TimeSpan.FromSeconds(3), 30);
            if (_isOfflineRendering || _isRecording || _offlineRenderCancellation != null || _lastRecordingFrame != null)
                throw new InvalidOperationException("Offline render state was not released.");
            return Directory.GetFiles(directory, "*.mp4").Single();
        }
        finally
        {
            _recordingOutputFolder = previousFolder;
            _recordingQuality = previousQuality;
            _uiInteractionSuspendCount = previousInteractionCount;
        }
    }

    // Cancel at the first reported frame, independent of dispatcher scheduling.
    // A queued dispatcher callback may run after several frames under load.
    private sealed class SmokeOfflineProgress(bool cancel) : IOfflineRenderProgress
    {
        public bool IsActive => false;
        public bool IsCancellationRequested { get; private set; }
        public void UpdateProgress(long completedFrames, long totalFrames, TimeSpan elapsed, TimeSpan? remaining)
        {
            if (cancel && completedFrames > 0) IsCancellationRequested = true;
        }
        public void Complete(string message, bool succeeded) { }
    }
}
