using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace lifeviz;

public partial class MainWindow
{
    internal async Task VerifyBackgroundBakeShutdownAsync()
    {
        int confirmations = 0;
        _confirmBakeExitForSmoke = () => { confirmations++; return false; };
        if (!ConfirmBakeExit() || confirmations != 0)
            throw new InvalidOperationException("An idle/completed queue prompted for exit.");
        var active = EnqueueBake(TimeSpan.FromHours(1), 30);
        var waiting = EnqueueBake(TimeSpan.FromSeconds(1), 30);
        // Closing before the worker launches must also protect queued work.
        Close();
        if (confirmations != 1 || _bakeQueueClosing || _isShuttingDown || waiting.Status.State != "Queued")
            throw new InvalidOperationException("Declining exit did not preserve queued bakes.");
        var timeout = Stopwatch.StartNew();
        while (active.Status.CompletedFrames == 0 && !active.IsFinished && timeout.Elapsed < TimeSpan.FromSeconds(45))
            await Task.Delay(50);
        if (active.Status.CompletedFrames == 0) throw new InvalidOperationException("Shutdown fixture did not start rendering.");
        long framesBeforeClose = active.Status.CompletedFrames;
        Close();
        if (confirmations != 2 || _bakeQueueClosing || _isShuttingDown ||
            File.Exists(Path.Combine(active.DirectoryPath, "cancel")) || waiting.Status.State != "Queued")
            throw new InvalidOperationException("Declining exit cancelled an active bake or its queue.");
        timeout.Restart();
        while (active.Status.CompletedFrames <= framesBeforeClose && !active.IsFinished && timeout.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(50);
        if (active.Status.CompletedFrames <= framesBeforeClose)
            throw new InvalidOperationException("The bake stopped progressing after declining exit.");
        _confirmBakeExitForSmoke = () => { confirmations++; return true; };
        Close();
        await _bakeQueueTask!.WaitAsync(TimeSpan.FromSeconds(45));
        await Task.Yield();
        if (confirmations != 3 || !_isShuttingDown || _bakeProcess != null || active.Status.State != "Cancelled" ||
            waiting.Status.State != "Cancelled" || !File.Exists(active.OutputPath))
            throw new InvalidOperationException("Closing the app did not cancel the queue and finalize active partial output.");
    }

    internal async Task<BakeJob[]> RunBackgroundBakeQueueSmokeAsync(string directory, string firstImage, string secondImage)
    {
        ConfigureProfilingSmokeScene(144, smokeVideoPath: firstImage);
        _recordingOutputFolder = Path.Combine(directory, "first");
        _recordingQuality = RecordingQuality.LosslessCompatible;
        var first = EnqueueBake(TimeSpan.FromSeconds(2), 30);
        // Reproduce the old access-denied failure, then leave both old status
        // paths permanently unwritable for the real worker's entire render.
        string oldStatusPath = Path.Combine(first.DirectoryPath, "status.json");
        Directory.CreateDirectory(oldStatusPath);
        bool reproducedStatusFailure = false;
        try { BackgroundBakeWorker.WriteJson(oldStatusPath, new BakeStatus("Rendering", "repro")); }
        catch (UnauthorizedAccessException) { reproducedStatusFailure = true; }
        catch (IOException) { reproducedStatusFailure = true; }
        if (!reproducedStatusFailure) throw new InvalidOperationException("Old progress-file failure was not reproduced.");
        File.Delete(oldStatusPath + ".tmp");
        Directory.CreateDirectory(oldStatusPath + ".tmp");

        ConfigureProfilingSmokeScene(144, smokeVideoPath: secondImage);
        _recordingOutputFolder = Path.Combine(directory, "second");
        var removed = EnqueueBake(TimeSpan.FromSeconds(1), 30);
        CancelBake(removed);
        var second = EnqueueBake(TimeSpan.FromSeconds(1), 60);
        string obstruction = Path.Combine(directory, "not-a-folder");
        File.WriteAllText(obstruction, "blocks folder creation");
        _recordingOutputFolder = obstruction;
        var failure = EnqueueBake(TimeSpan.FromSeconds(1), 30);
        _recordingOutputFolder = Path.Combine(directory, "cancelled");
        var cancelled = EnqueueBake(TimeSpan.FromHours(1), 30);
        _recordingOutputFolder = Path.Combine(directory, "after-cancel");
        var last = EnqueueBake(TimeSpan.FromSeconds(1), 30);
        var simGroup = CaptureSource.CreateSimulationGroup("Baked Pixel Sort");
        simGroup.SimulationLayers.Add(new SimulationLayerSpec
        {
            Id = Guid.NewGuid(), LayerType = SimulationLayerType.PixelSort,
            BlendMode = BlendMode.Normal, Enabled = true
        });
        _sources.Add(simGroup);
        ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
        var simulation = EnqueueBake(TimeSpan.FromSeconds(1), 30);
        string finalScene = SerializeCurrentConfig();
        int heartbeats = 0;
        int ticksDuringRender = 0;
        bool capturedQueue = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            heartbeats++;
            if (_bakeJobs.Any(j => j.Status.State == "Rendering")) ticksDuringRender++;
            if (!capturedQueue && ticksDuringRender > 0 && _bakeQueueWindow is { } queueWindow)
            {
                queueWindow.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)queueWindow.ActualWidth,
                    (int)queueWindow.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(queueWindow);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var imageFile = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-bake-queue.png"));
                encoder.Save(imageFile);
                capturedQueue = true;
            }
            if (cancelled.Status.State == "Rendering" && cancelled.Status.CompletedFrames > 0) CancelBake(cancelled);
        };
        timer.Start();
        try
        {
            // Opening, closing, and reopening the nonmodal queue must not stop work.
            ShowBakeQueue();
            _bakeQueueWindow!.Close();
            ShowBakeQueue();
            await _bakeQueueTask!.WaitAsync(TimeSpan.FromMinutes(3));
            if (heartbeats < 5 || ticksDuringRender < 2 || _isOfflineRendering || _isRecording || !_renderLoopAttached)
                throw new InvalidOperationException("The editor did not remain live during background baking.");
            if (SerializeCurrentConfig() != finalScene)
                throw new InvalidOperationException("Baking changed the authored editor scene/settings.");
            if (first.Status.State != "Completed" || second.Status.State != "Completed" || last.Status.State != "Completed" || simulation.Status.State != "Completed" ||
                failure.Status.State != "Failed" || removed.Status.State != "Cancelled" || cancelled.Status.State != "Cancelled")
                throw new InvalidOperationException(string.Join("\n", _bakeJobs.Select(j => j.Summary + ": " + j.Detail)));
            if (_bakeJobs.Any(j => !j.HasDiagnostics && Directory.Exists(j.DirectoryPath)))
                throw new InvalidOperationException("Finished bake scratch directories were not released.");
            if (!failure.HasDiagnostics || !File.Exists(Path.Combine(failure.DiagnosticsPath, "request.json")) ||
                !File.ReadAllText(Path.Combine(failure.DiagnosticsPath, "worker.log")).Contains("not-a-folder", StringComparison.Ordinal))
                throw new InvalidOperationException("Failed bake diagnostics did not retain the request and actual error.");
            Directory.Delete(failure.DiagnosticsPath, recursive: true); // Isolated test fixture only.
            if (_recordingOutputFolder != Path.Combine(directory, "after-cancel"))
                throw new InvalidOperationException("Bake worker changed live output preferences.");
            Logger.Info($"Background bake queue: {heartbeats} editor heartbeats, {ticksDuringRender} during active rendering; snapshot/FIFO/failure/cancel passed, including permanently blocked legacy status paths and retained failure diagnostics.");
            return new[] { first, second, cancelled, last, simulation };
        }
        finally
        {
            timer.Stop();
            _bakeQueueWindow?.Close();
            if (_bakeQueueTask is { IsCompleted: false })
            {
                AbortBackgroundBakes();
                await _bakeQueueTask;
            }
        }
    }
}
