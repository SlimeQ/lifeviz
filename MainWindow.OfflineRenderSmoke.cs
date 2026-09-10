using System.IO;
using System.Windows.Threading;

namespace lifeviz;

public partial class MainWindow
{
    internal async Task<string> RunFixedDurationForSmokeAsync(string directory, bool cancel)
    {
        ConfigureProfilingSmokeScene(144);
        string? previousFolder = _recordingOutputFolder;
        RecordingQuality previousQuality = _recordingQuality;
        int previousInteractionCount = _uiInteractionSuspendCount;
        var dialog = new OfflineRenderWindow(60);
        try
        {
            _recordingOutputFolder = directory;
            _recordingQuality = RecordingQuality.LosslessCompatible;
            // An open editor interaction must not skip virtual-clock frames.
            _uiInteractionSuspendCount = 1;
            if (cancel)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => _offlineRenderCancellation?.Cancel()));
            }
            await RunOfflineRenderAsync(dialog, TimeSpan.FromSeconds(3), 30);
            if (_isOfflineRendering || _isRecording || _offlineRenderCancellation != null || _lastRecordingFrame != null)
                throw new InvalidOperationException("Offline render state was not released.");
            return Directory.GetFiles(directory, "*.mp4").Single();
        }
        finally
        {
            _recordingOutputFolder = previousFolder;
            _recordingQuality = previousQuality;
            _uiInteractionSuspendCount = previousInteractionCount;
            dialog.Close();
        }
    }
}
