using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace lifeviz;

// A webcam layer is a persistent slot for a camera, not a camera handle. It keeps its
// place and settings when its camera is unplugged, missing on another PC, or fails to
// open; it shows nothing until a device is available, and the camera can be swapped
// without rebuilding the layer. Previously such layers were dropped (or kept aside as
// hidden missing inputs), which made scenes with webcams fragile and unshareable.
public partial class MainWindow
{
    internal const string AnyWebcamName = "Any camera";
    private static readonly TimeSpan WebcamProbeWhileWaiting = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WebcamProbeWhileConnected = TimeSpan.FromSeconds(10);
    private DateTime _lastWebcamProbeUtc = DateTime.MinValue;
    private bool _webcamProbeRunning;

    // Smoke tests substitute a fake camera list; production enumerates real devices.
    internal Func<IReadOnlyList<WebcamCaptureService.CameraInfo>>? WebcamEnumeratorOverride { get; set; }

    private sealed partial class CaptureSource
    {
        // The device this layer is reading on this PC; null while its camera is unavailable.
        public string? ActiveWebcamId { get; set; }
        public string? ActiveWebcamName { get; set; }
        public DateTime WebcamRetryUtc { get; set; }
        public int WebcamFailures { get; set; }
        public bool IsAnyWebcam => Type == SourceType.Webcam && string.IsNullOrWhiteSpace(WebcamId);

        public void SetWebcamDevice(string? webcamId, string name)
        {
            WebcamId = string.IsNullOrWhiteSpace(webcamId) ? null : webcamId;
            DisplayName = string.IsNullOrWhiteSpace(webcamId) ? AnyWebcamName : name;
        }
    }

    internal IReadOnlyList<WebcamCaptureService.CameraInfo> EnumerateWebcams() =>
        WebcamEnumeratorOverride?.Invoke() ?? _webcamCapture.EnumerateCameras();

    private CaptureSource CreateWebcamSource(string? webcamId, string? name, IReadOnlyList<WebcamCaptureService.CameraInfo> cameras)
    {
        bool any = string.IsNullOrWhiteSpace(webcamId);
        var source = CaptureSource.CreateWebcam(null, any ? AnyWebcamName : name ?? "Webcam");
        source.SetWebcamDevice(webcamId, string.IsNullOrWhiteSpace(name) ? "Webcam" : name);
        ResolveWebcamDevice(source, cameras, TakenWebcamIds(source));
        return source;
    }

    private HashSet<string> TakenWebcamIds(CaptureSource except) =>
        EnumerateSources(_sources)
            .Where(s => s != except && s.Type == CaptureSource.SourceType.Webcam && s.ActiveWebcamId != null)
            .Select(s => s.ActiveWebcamId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Specific cameras match by ID, then by name (IDs differ between PCs and USB ports);
    // a name match adopts the local ID. "Any camera" takes the first camera no other layer uses.
    private static WebcamCaptureService.CameraInfo? FindWebcamFor(CaptureSource source,
        IReadOnlyList<WebcamCaptureService.CameraInfo> cameras, ISet<string> taken)
    {
        var free = cameras.Where(c => !string.IsNullOrWhiteSpace(c.Id) && !taken.Contains(c.Id)).ToList();
        if (source.IsAnyWebcam) return free.Count > 0 ? free[0] : null;
        foreach (var camera in free)
            if (string.Equals(camera.Id, source.WebcamId, StringComparison.OrdinalIgnoreCase)) return camera;
        foreach (var camera in free)
            if (string.Equals(camera.Name, source.DisplayName, StringComparison.OrdinalIgnoreCase)) return camera;
        return null;
    }

    // Returns true when the layer's device or saved camera changed.
    private bool ResolveWebcamDevice(CaptureSource source, IReadOnlyList<WebcamCaptureService.CameraInfo> cameras, ISet<string> taken)
    {
        if (source.ActiveWebcamId != null)
        {
            if (cameras.Any(c => string.Equals(c.Id, source.ActiveWebcamId, StringComparison.OrdinalIgnoreCase))) return false;
            Logger.Warn($"Camera for webcam layer {source.DisplayName} disconnected; keeping the layer until it returns.");
            ReleaseWebcamDevice(source);
            source.ActiveWebcamId = null;
            source.ActiveWebcamName = null;
            source.LastFrame = null;
            return true;
        }

        if (DateTime.UtcNow < source.WebcamRetryUtc) return false;
        if (FindWebcamFor(source, cameras, taken) is not { } camera) return false;
        bool adopted = !source.IsAnyWebcam && !string.Equals(source.WebcamId, camera.Id, StringComparison.OrdinalIgnoreCase);
        if (adopted)
        {
            Logger.Info($"Webcam layer {source.DisplayName} matched camera by name; using its ID on this PC.");
            source.SetWebcamDevice(camera.Id, camera.Name);
        }
        source.ActiveWebcamId = camera.Id;
        source.ActiveWebcamName = camera.Name;
        source.IsInitialized = false;
        source.MissedFrames = 0;
        source.FirstFrameReceived = false;
        source.RetryInitializationAttempted = false;
        source.AddedUtc = DateTime.UtcNow;
        Logger.Info($"Webcam layer {source.DisplayName} connected to {camera.Name}.");
        return true;
    }

    private bool ResolveWebcamDevices(IReadOnlyList<WebcamCaptureService.CameraInfo> cameras)
    {
        var webcams = EnumerateSources(_sources).Where(s => s.Type == CaptureSource.SourceType.Webcam).ToList();
        bool changed = false;
        // Release vanished devices first, then serve specific choices before "Any camera".
        foreach (var source in webcams.Where(s => s.ActiveWebcamId != null))
            changed |= ResolveWebcamDevice(source, cameras, TakenWebcamIds(source));
        foreach (var source in webcams.Where(s => s.ActiveWebcamId == null).OrderBy(s => s.IsAnyWebcam))
            changed |= ResolveWebcamDevice(source, cameras, TakenWebcamIds(source));
        return changed;
    }

    // Enumeration runs off the render loop. It is frequent while a layer waits for a camera
    // and occasional otherwise, to notice unplugged devices.
    private void RequestWebcamProbe()
    {
        if (_webcamProbeRunning || _isShuttingDown) return;
        var elapsed = DateTime.UtcNow - _lastWebcamProbeUtc;
        if (elapsed < WebcamProbeWhileWaiting) return;
        if (elapsed < WebcamProbeWhileConnected &&
            !EnumerateSources(_sources).Any(s => s.Type == CaptureSource.SourceType.Webcam && s.Enabled && s.ActiveWebcamId == null))
            return;
        _webcamProbeRunning = true;
        _lastWebcamProbeUtc = DateTime.UtcNow;
        Task.Run(EnumerateWebcams).ContinueWith(task => Dispatcher.BeginInvoke(new Action(() =>
        {
            _webcamProbeRunning = false;
            _lastWebcamProbeUtc = DateTime.UtcNow;
            if (_isShuttingDown || task.IsFaulted) return;
            _cachedCameras = task.Result;
            if (!ResolveWebcamDevices(task.Result)) return;
            SaveConfig();
            RebuildSourcesMenu();
            NotifyLayerEditorSourcesChanged();
        })));
    }

    private void ReleaseWebcamDevice(CaptureSource source)
    {
        string? id = source.ActiveWebcamId;
        source.IsInitialized = false;
        if (id == null) return;
        bool shared = EnumerateSources(_sources).Any(s => s != source && s.Type == CaptureSource.SourceType.Webcam &&
            string.Equals(s.ActiveWebcamId, id, StringComparison.OrdinalIgnoreCase));
        if (!shared) _webcamCapture.Reset(id);
    }

    // A camera that fails stays in the scene; it is retried with backoff instead of removed.
    private void DetachWebcamDevice(CaptureSource source, string reason)
    {
        source.WebcamFailures++;
        var delay = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(4, source.WebcamFailures - 1))));
        Logger.Warn($"Webcam layer {source.DisplayName}: {reason}. Keeping the layer and retrying in {delay.TotalSeconds:0} s.");
        ReleaseWebcamDevice(source);
        source.ActiveWebcamId = null;
        source.ActiveWebcamName = null;
        source.LastFrame = null;
        source.WebcamRetryUtc = DateTime.UtcNow + delay;
    }

    private bool TrySetWebcamDevice(CaptureSource source, string? webcamId, string? name,
        IReadOnlyList<WebcamCaptureService.CameraInfo> cameras, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(webcamId) && EnumerateSources(_sources).Any(s => s != source &&
                s.Type == CaptureSource.SourceType.Webcam &&
                (string.Equals(s.WebcamId, webcamId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(s.ActiveWebcamId, webcamId, StringComparison.OrdinalIgnoreCase))))
        {
            error = "That camera is already used by another webcam layer. Choose a different camera.";
            return false;
        }

        ReleaseWebcamDevice(source);
        source.ActiveWebcamId = null;
        source.ActiveWebcamName = null;
        source.LastFrame = null;
        source.WebcamFailures = 0;
        source.WebcamRetryUtc = DateTime.MinValue;
        string label = !string.IsNullOrWhiteSpace(name) ? name :
            cameras.FirstOrDefault(c => string.Equals(c.Id, webcamId, StringComparison.OrdinalIgnoreCase)).Name ?? "Webcam";
        source.SetWebcamDevice(webcamId, label);
        ResolveWebcamDevice(source, cameras, TakenWebcamIds(source));
        return true;
    }

    // Enumerates off the UI thread, then reconnects any waiting layers on it.
    internal async Task<IReadOnlyList<WebcamCaptureService.CameraInfo>> RefreshWebcamDevicesAsync()
    {
        var cameras = await Task.Run(EnumerateWebcams);
        if (_isShuttingDown) return cameras;
        _cachedCameras = cameras;
        _lastWebcamProbeUtc = DateTime.UtcNow;
        foreach (var source in EnumerateSources(_sources).Where(s => s.Type == CaptureSource.SourceType.Webcam))
            source.WebcamRetryUtc = DateTime.MinValue; // An explicit refresh retries failed cameras now.
        if (ResolveWebcamDevices(cameras))
        {
            SaveConfig();
            RebuildSourcesMenu();
            NotifyLayerEditorSourcesChanged();
        }
        return cameras;
    }

    internal bool SetWebcamDeviceFromEditor(Guid sourceId, string? webcamId, string? name, out string? error)
    {
        var source = FindSource(s => s.Id == sourceId);
        if (source?.Type != CaptureSource.SourceType.Webcam)
        {
            error = "Select a webcam layer to change its camera.";
            return false;
        }

        var cameras = EnumerateWebcams();
        _cachedCameras = cameras;
        if (!TrySetWebcamDevice(source, webcamId, name, cameras, out error)) return false;
        RenderFrame();
        SaveConfig();
        RebuildSourcesMenu();
        return true;
    }

    private static string DescribeWebcamStatus(CaptureSource source)
    {
        if (source.ActiveWebcamId != null) return $"Using {source.ActiveWebcamName ?? "camera"}.";
        if (DateTime.UtcNow < source.WebcamRetryUtc) return "The camera could not be opened. Retrying shortly; you can choose another camera.";
        return source.IsAnyWebcam
            ? "No free camera is connected. The layer shows nothing until one is plugged in."
            : $"\"{source.DisplayName}\" is not connected. The layer shows nothing until it is plugged in, or choose another camera.";
    }

    // Camera choices for a layer: Any camera, every connected camera, and the saved camera when absent.
    internal static List<LayerEditorOption> BuildWebcamOptions(IReadOnlyList<WebcamCaptureService.CameraInfo> cameras, string? webcamId, string? name)
    {
        var options = new List<LayerEditorOption> { new(string.Empty, AnyWebcamName + " (first available)") };
        options.AddRange(cameras.Where(c => !string.IsNullOrWhiteSpace(c.Id)).Select(c => new LayerEditorOption(c.Id, c.Name)));
        if (!string.IsNullOrWhiteSpace(webcamId) && !options.Any(o => string.Equals(o.Value, webcamId, StringComparison.OrdinalIgnoreCase)))
            options.Add(new LayerEditorOption(webcamId, $"{(string.IsNullOrWhiteSpace(name) ? "Saved camera" : name)} (not connected)"));
        return options;
    }

    private MenuItem BuildWebcamDeviceMenu(CaptureSource source)
    {
        var menu = new MenuItem { Header = "Camera" };
        foreach (var option in BuildWebcamOptions(_cachedCameras, source.WebcamId, source.DisplayName))
        {
            var item = new MenuItem
            {
                Header = option.Label,
                IsCheckable = true,
                IsChecked = string.Equals(option.Value, source.WebcamId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            };
            string name = _cachedCameras.FirstOrDefault(c => string.Equals(c.Id, option.Value, StringComparison.OrdinalIgnoreCase)).Name ?? source.DisplayName;
            item.Click += (_, _) =>
            {
                if (!SetWebcamDeviceFromEditor(source.Id, option.Value, name, out var error))
                    ShowSceneNotice(error ?? "Could not switch camera.", "Camera");
                NotifyLayerEditorSourcesChanged();
            };
            menu.Items.Add(item);
        }
        return menu;
    }
}
