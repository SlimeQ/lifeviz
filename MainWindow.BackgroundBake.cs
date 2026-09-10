using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace lifeviz;

public partial class MainWindow
{
    private readonly ObservableCollection<BakeJob> _bakeJobs = new();
    private BakeQueueWindow? _bakeQueueWindow;
    private Task? _bakeQueueTask;
    private Process? _bakeProcess;
    private FfmpegProcessManager.KillOnCloseJob? _bakeProcessOwner;
    private bool _bakeQueueClosing;

    private void ShowBakeQueue()
    {
        if (_bakeQueueWindow != null) { _bakeQueueWindow.Activate(); return; }
        var window = new BakeQueueWindow(_bakeJobs, CancelBake) { Owner = this };
        _bakeQueueWindow = window;
        window.Closed += (_, _) => _bakeQueueWindow = null;
        window.Show();
    }

    private void BakeQueueMenuItem_Click(object sender, RoutedEventArgs e) => ShowBakeQueue();

    private BakeJob EnqueueBake(TimeSpan duration, int fps)
    {
        if (_bakeQueueClosing) throw new InvalidOperationException("LifeViz is closing.");
        // Freeze output settings and actual aspect ratio as well as authored layers.
        var scene = JsonNode.Parse(SerializeCurrentConfig())!.AsObject();
        scene["RecordingOutputFolder"] = GetRecordingOutputFolder();
        scene["AspectRatioLocked"] = true;
        scene["LockedAspectRatio"] = _displayHeight > 0 ? _displayWidth / (double)_displayHeight : _currentAspectRatio;
        scene["Fullscreen"] = false;
        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-bakes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            BackgroundBakeWorker.WriteJson(Path.Combine(directory, "request.json"), new BakeRequest(scene.ToJsonString(), duration.TotalSeconds, fps));
        }
        catch { Directory.Delete(directory, recursive: true); throw; }
        var job = new BakeJob { DirectoryPath = directory, Name = $"Bake {_bakeJobs.Count + 1} • {BackgroundBakeWorker.FormatClock(duration)} at {fps} FPS" };
        _bakeJobs.Add(job);
        if (_bakeQueueTask == null || _bakeQueueTask.IsCompleted) _bakeQueueTask = ProcessBakeQueueAsync();
        return job;
    }

    private void CancelBake(BakeJob job)
    {
        if (!job.CanCancel) return;
        if (job.Status.State == "Queued")
        {
            job.Update(new BakeStatus("Cancelled", "Removed from queue"));
            CleanupBakeFiles(job);
        }
        else
        {
            try
            {
                File.WriteAllText(Path.Combine(job.DirectoryPath, "cancel"), "cancel");
                job.Update(job.Status with { State = "Cancelling", Message = "Finishing the current frame and saving partial output..." });
            }
            catch (Exception ex) { job.Update(job.Status with { Message = $"Could not request cancellation: {ex.Message}" }); }
        }
    }

    private async Task ProcessBakeQueueAsync()
    {
        // Always yield before launching: enqueue must return before completion can race the task field.
        await Task.Yield();
        while (!_bakeQueueClosing && _bakeJobs.FirstOrDefault(j => j.Status.State == "Queued") is { } job)
        {
            job.Update(new BakeStatus("Starting", "Preparing a separate renderer..."));
            try
            {
                var process = Process.Start(BackgroundBakeWorker.CreateStartInfo(job.DirectoryPath))
                    ?? throw new InvalidOperationException("Could not start the bake worker.");
                _bakeProcess = process;
                _bakeProcessOwner ??= FfmpegProcessManager.KillOnCloseJob.TryCreate(out _);
                if (_bakeProcessOwner != null && !_bakeProcessOwner.TryAssign(process, out string? ownershipError))
                    Logger.Warn($"Bake worker job containment unavailable: {ownershipError}");
                while (!process.HasExited)
                {
                    ReadBakeStatus(job);
                    await Task.Delay(500);
                }
                ReadBakeStatus(job);
                if (!job.IsFinished)
                    job.Update(job.Status with { State = "Failed", Message = $"Bake worker exited unexpectedly (code {process.ExitCode}). Check partial output before using it." });
            }
            catch (Exception ex)
            {
                // A monitor failure must not leave an untracked worker running
                // alongside the next job in this otherwise serial queue.
                try
                {
                    if (_bakeProcess is { HasExited: false } worker)
                    {
                        worker.Kill(entireProcessTree: true);
                        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    }
                }
                catch (Exception stopError)
                {
                    _bakeQueueClosing = true;
                    foreach (var queued in _bakeJobs.Where(j => j.Status.State == "Queued").ToArray()) CancelBake(queued);
                    Logger.Warn($"Bake worker cleanup failed; further bakes stopped: {stopError.Message}");
                }
                job.Update(job.Status with { State = "Failed", Message = ex.Message });
            }
            finally
            {
                _bakeProcess?.Dispose();
                _bakeProcess = null;
                CleanupBakeFiles(job);
            }
        }
    }

    private static void ReadBakeStatus(BakeJob job)
    {
        try
        {
            string path = Path.Combine(job.DirectoryPath, "status.json");
            if (!File.Exists(path)) return;
            // Let the worker atomically replace this path while we read the old handle.
            // File.ReadAllText's default sharing can deny replacement on Windows.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonSerializer.Deserialize<BakeStatus>(file) is { } status)
            {
                if (job.Status.State == "Cancelling" && status.State == "Rendering")
                    status = status with { State = "Cancelling", Message = job.Status.Message };
                job.Update(status);
            }
        }
        catch (IOException) { } // Atomic replacement can briefly share-lock the status file.
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }

    private static void CleanupBakeFiles(BakeJob job)
    {
        try { Directory.Delete(job.DirectoryPath, recursive: true); }
        catch (Exception ex) { Logger.Warn($"Could not remove bake temporary files: {ex.Message}"); }
    }

    private async void BackgroundBakeWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_bakeQueueTask == null || _bakeQueueTask.IsCompleted) return;
        e.Cancel = true;
        if (_bakeQueueClosing) return;
        _bakeQueueClosing = true;
        foreach (var job in _bakeJobs.Where(j => j.CanCancel).ToArray()) CancelBake(job);
        await _bakeQueueTask;
        Close();
    }

    private void AbortBackgroundBakes()
    {
        _bakeQueueClosing = true;
        try { if (_bakeProcess is { HasExited: false }) _bakeProcess.Kill(entireProcessTree: true); }
        catch (Exception ex) { Logger.Warn($"Could not stop bake worker during shutdown: {ex.Message}"); }
        _bakeProcessOwner?.Dispose();
        _bakeProcessOwner = null;
    }

    internal async Task RunBackgroundBakeAsync(BackgroundBakeWorker.WorkerProgress progress, BakeRequest request)
    {
        var (loaded, _) = LoadConfig();
        if (!loaded || _configLoadBlocked) throw new InvalidDataException("The captured bake scene could not be loaded.");
        _pendingFullscreen = false;
        InitializeVisualizer();
        DetachRenderLoop();
        await RunOfflineRenderAsync(progress, TimeSpan.FromSeconds(request.DurationSeconds), request.OutputFps);
    }
}
