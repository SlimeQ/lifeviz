using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace lifeviz;

internal interface IOfflineRenderProgress
{
    bool IsActive { get; }
    bool IsCancellationRequested { get; }
    void UpdateProgress(long completedFrames, long totalFrames, TimeSpan elapsed, TimeSpan? remaining);
    void Complete(string message, bool succeeded);
}

internal sealed record BakeRequest(string SceneJson, double DurationSeconds, int OutputFps);
internal sealed record BakeStatus(string State, string Message, long CompletedFrames = 0,
    long TotalFrames = 0, double ElapsedSeconds = 0, double? RemainingSeconds = null, string? OutputPath = null);

internal sealed class BakeJob : INotifyPropertyChanged
{
    public required string DirectoryPath { get; init; }
    public required string Name { get; init; }
    public BakeStatus Status { get; private set; } = new("Queued", "Waiting for the previous bake");
    public string Summary => $"{Name} — {Status.State}";
    public string Detail => Status.TotalFrames > 0
        ? $"{Status.CompletedFrames:N0} / {Status.TotalFrames:N0} frames • {Status.Message}"
        : Status.Message;
    public string OutputPath => Status.OutputPath ?? "";
    public double Percent => Status.TotalFrames > 0 ? 100.0 * Status.CompletedFrames / Status.TotalFrames : 0;
    public bool CanCancel => Status.State is "Queued" or "Starting" or "Rendering";
    public bool IsFinished => Status.State is "Completed" or "Cancelled" or "Failed";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(BakeStatus status)
    {
        Status = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

internal static class BackgroundBakeWorker
{
    public static string? DirectoryPath { get; private set; }
    public static bool IsWorker => DirectoryPath != null;
    public static bool CancellationRequested => IsWorker && File.Exists(Path.Combine(DirectoryPath!, "cancel"));
    public static string? ConfigPath => IsWorker ? Path.Combine(DirectoryPath!, "scene.json") : null;
    internal static string FormatClock(TimeSpan value) => $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}";

    public static void WriteJson<T>(string path, T value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, path, overwrite: true); return; }
            catch (IOException) when (attempt < 4) { Thread.Sleep(20 * (attempt + 1)); }
            catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(20 * (attempt + 1)); }
        }
    }

    public static ProcessStartInfo CreateStartInfo(string directory)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate LifeViz.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetTempPath()
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--background-bake");
        start.ArgumentList.Add(directory);
        return start;
    }

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != "--background-bake") return false;
        exitCode = 1;
        if (args.Length != 2) return true;
        DirectoryPath = Path.GetFullPath(args[1]);
        App.SuppressErrorDialogs = true;
        App.CapturePresentedFramesForValidation = false;
        try
        {
            var request = JsonSerializer.Deserialize<BakeRequest>(File.ReadAllText(Path.Combine(DirectoryPath, "request.json")))
                ?? throw new InvalidDataException("Missing bake request.");
            if (request.DurationSeconds is < 1 or > 86400 || !double.IsFinite(request.DurationSeconds) || request.OutputFps is < 1 or > 144)
                throw new InvalidDataException("Invalid bake duration or FPS.");
            File.WriteAllText(ConfigPath!, request.SceneJson);
            var progress = new WorkerProgress();
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) =>
            {
                progress.Complete($"Render failed: {e.Exception.Message}", false);
                app.Shutdown(1);
            };
            app.Startup += (_, _) =>
            {
                var window = new MainWindow
                {
                    ShowInTaskbar = false, ShowActivated = false, Opacity = 0,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000
                };
                window.Loaded += async (_, _) =>
                {
                    try { await window.RunBackgroundBakeAsync(progress, request); }
                    catch (Exception ex) { progress.Complete($"Render failed: {ex.Message}", false); }
                    finally { window.Close(); app.Shutdown(); }
                };
                window.Show();
            };
            app.Run();
            exitCode = progress.Succeeded ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteJson(Path.Combine(DirectoryPath, "status.json"), new BakeStatus("Failed", ex.Message));
        }
        return true;
    }

    internal sealed class WorkerProgress : IOfflineRenderProgress
    {
        private BakeStatus _status = new("Starting", "Preparing scene...");
        public bool Succeeded { get; private set; }
        public bool IsActive => false;
        public bool IsCancellationRequested => CancellationRequested;
        public string? OutputPath { get; set; }
        public void UpdateProgress(long completedFrames, long totalFrames, TimeSpan elapsed, TimeSpan? remaining)
        {
            _status = new BakeStatus("Rendering", remaining.HasValue
                ? $"Elapsed {FormatClock(elapsed)} • about {FormatClock(remaining.Value)} remaining"
                : $"Elapsed {FormatClock(elapsed)} • estimating remaining time...",
                completedFrames, totalFrames, elapsed.TotalSeconds, remaining?.TotalSeconds, OutputPath);
            Publish();
        }
        public void Complete(string message, bool succeeded)
        {
            Succeeded = succeeded;
            // Only the render loop knows whether cancellation finalized successfully.
            string state = succeeded ? "Completed" : message.StartsWith("Render cancelled.", StringComparison.Ordinal) ? "Cancelled" : "Failed";
            _status = _status with { State = state, Message = message, OutputPath = OutputPath };
            Publish();
        }
        private void Publish() => WriteJson(Path.Combine(DirectoryPath!, "status.json"), _status);
    }
}
