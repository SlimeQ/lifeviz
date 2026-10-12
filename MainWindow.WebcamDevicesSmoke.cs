using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace lifeviz;

public partial class MainWindow
{
    // Uses a fake camera list; the only real device call is opening a bogus camera ID,
    // which must fail without dropping the layer.
    internal void RunWebcamDeviceChecks(string directory)
    {
        if (!App.IsSmokeTestMode) throw new InvalidOperationException("Webcam device checks require smoke mode.");
        _scenePersistenceTestPath = Path.Combine(directory, "session.json");
        _configReady = true;
        var cameras = new List<WebcamCaptureService.CameraInfo>();
        WebcamEnumeratorOverride = () => cameras.ToList();
        // Background probes would resolve against stale lists mid-test; resolution is driven explicitly.
        _lastWebcamProbeUtc = DateTime.MaxValue;
        var camA = new WebcamCaptureService.CameraInfo("fake-cam-a-" + Guid.NewGuid(), "Smoke Cam A");
        var camB = new WebcamCaptureService.CameraInfo("fake-cam-b-" + Guid.NewGuid(), "Smoke Cam B");

        // A saved camera that is not connected keeps its layer, settings and saved identity.
        var saved = new AppConfig.SourceConfig { Type = "Webcam", WebcamId = "id-from-another-pc", DisplayName = "Smoke Cam A", Opacity = 0.4, Mirror = true };
        RestoreSourceList(new[] { saved }, _sources, Array.Empty<WindowHandleInfo>(), cameras);
        var layer = _sources.Single();
        Require(layer.Type == CaptureSource.SourceType.Webcam && layer.ActiveWebcamId == null && layer.Opacity == 0.4 && layer.Mirror &&
                MissingInputCount == 0, "A webcam layer without its camera must stay in the scene, not become a missing input.");
        var model = BuildLayerEditorSources().Single();
        Require(model.IsWebcam && model.WebcamId == "id-from-another-pc" && model.WebcamStatus.Contains("not connected"),
            "The editor must show the waiting layer and its status.");
        var config = BuildSourceConfigs(_sources).Single();
        Require(config.WebcamId == "id-from-another-pc" && config.DisplayName == "Smoke Cam A", "Autosave must keep the saved camera.");

        // Capturing never removes it.
        for (int i = 0; i < 400; i++)
            Require(!CaptureSourceList(_sources, i / 60.0) && _sources.Contains(layer), "The capture loop removed a waiting webcam layer.");

        // Plugging in the same model on this PC: match by name and adopt the local ID.
        cameras.Add(camB);
        cameras.Add(camA);
        Require(ResolveWebcamDevices(cameras) && layer.ActiveWebcamId == camA.Id && layer.WebcamId == camA.Id,
            "A camera with the saved name must be adopted.");
        Require(BuildSourceConfigs(_sources).Single().WebcamId == camA.Id, "The adopted local camera ID must be saved.");

        // Opening fails (fake device): the layer stays and retries later instead of being removed.
        Require(!CaptureSourceList(_sources, 10) && _sources.Contains(layer) && layer.ActiveWebcamId == null && layer.WebcamRetryUtc > DateTime.UtcNow,
            "A camera that fails to open must leave the layer waiting with a retry.");
        Require(!ResolveWebcamDevices(cameras) && layer.ActiveWebcamId == null, "A failed camera must back off before retrying.");
        layer.WebcamRetryUtc = DateTime.MinValue;
        Require(ResolveWebcamDevices(cameras) && layer.ActiveWebcamId == camA.Id, "The camera must reconnect after the backoff.");

        // "Any camera" takes a free camera, never one a specific layer uses.
        var any = CreateWebcamSource(null, null, cameras);
        _sources.Add(any);
        Require(any.IsAnyWebcam && any.DisplayName == AnyWebcamName && any.ActiveWebcamId == camB.Id,
            "Any camera must use the first camera no other layer uses.");
        Require(BuildSourceConfigs(_sources)[1].WebcamId == null, "Any camera must save without a device ID.");

        // Unplugging releases the device; the layer waits and reconnects when it returns.
        cameras.Remove(camB);
        Require(ResolveWebcamDevices(cameras) && any.ActiveWebcamId == null && _sources.Contains(any), "Unplugging must keep the layer.");
        cameras.Add(camB);
        Require(ResolveWebcamDevices(cameras) && any.ActiveWebcamId == camB.Id, "A replugged camera must reconnect.");

        // Live swap keeps the layer and its settings; a camera in use elsewhere is refused.
        var anyAnimations = any.Animations.Count;
        any.Opacity = 0.7;
        Require(!SetWebcamDeviceFromEditor(any.Id, camA.Id, camA.Name, out var error) && error != null && any.ActiveWebcamId == camB.Id,
            "Choosing a camera another layer uses must be refused without changing the layer.");
        Require(SetWebcamDeviceFromEditor(layer.Id, null, null, out error) && layer.IsAnyWebcam && layer.DisplayName == AnyWebcamName,
            error ?? "Switching to Any camera failed.");
        Require(layer.ActiveWebcamId == camA.Id, "Any camera must pick up the freed camera.");
        Require(SetWebcamDeviceFromEditor(any.Id, "unplugged-cam", "Unplugged Cam", out error) && ReferenceEquals(_sources[1], any) &&
                any.Opacity == 0.7 && any.Animations.Count == anyAnimations && any.ActiveWebcamId == null && any.DisplayName == "Unplugged Cam",
            error ?? "Swapping to a disconnected camera must keep the layer and settings.");
        var options = BuildWebcamOptions(cameras, any.WebcamId, any.DisplayName);
        Require(options[0].Value == "" && options.Any(o => o.Value == camA.Id) && options.Any(o => o.Value == "unplugged-cam" && o.Label.Contains("not connected")),
            "The camera list must offer Any camera, connected cameras and the saved missing camera.");

        // Draft Apply switches devices on the same runtime layer.
        var draft = BuildLayerEditorSources();
        draft[1].WebcamId = camB.Id;
        draft[1].DisplayName = camB.Name;
        ApplyLayerEditorSources(draft);
        Require(ReferenceEquals(_sources[1], any) && any.WebcamId == camB.Id && any.ActiveWebcamId == camB.Id && any.Opacity == 0.7,
            "Draft Apply must switch the camera on the existing layer.");

        // Project export/import and a fresh restore keep both layers.
        var exported = LayerConfigFile.FromEditorSources(BuildLayerEditorSources(), Array.Empty<LayerEditorSimulationLayer>(), GetProjectSettingsForEditor());
        var reloaded = LayerConfigFile.Parse(JsonSerializer.Serialize(exported)).ToEditorSources().Where(s => s.IsWebcam).ToList();
        Require(reloaded.Count == 2 && reloaded[0].WebcamId == null && reloaded[1].WebcamId == camB.Id, "Project export lost webcam choices.");
        var configs = BuildSourceConfigs(_sources);
        ClearSources(persist: false);
        RestoreSourceList(configs, _sources, Array.Empty<WindowHandleInfo>(), Array.Empty<WebcamCaptureService.CameraInfo>());
        Require(_sources.Count == 2 && _sources.All(s => s.Type == CaptureSource.SourceType.Webcam && s.ActiveWebcamId == null),
            "Restoring with no cameras connected must keep every webcam layer.");

        // Editor camera picker in Live Mode. Opening the picker refreshes devices:
        // the specific layer takes Cam B and Any takes Cam A; then Cam C is chosen.
        var camC = new WebcamCaptureService.CameraInfo("fake-cam-c-" + Guid.NewGuid(), "Smoke Cam C");
        cameras.Add(camC);
        var editor = new LayerEditorWindow(this) { ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
        try
        {
            _layerEditorWindow = editor;
            editor.SetLiveModeForSmoke(true);
            editor.Show();
            editor.RunWebcamPickerSmoke(_sources[1].Id, camC.Id, Path.Combine(AppContext.BaseDirectory, "smoke-webcam-picker.png"));
            Require(_sources[1].WebcamId == camC.Id && _sources[1].ActiveWebcamId == camC.Id && _sources[0].ActiveWebcamId == camA.Id,
                "The editor camera picker did not switch the live layer.");
        }
        finally
        {
            _layerEditorWindow = null;
            editor.Close();
        }

        ClearSources(persist: false);
        WebcamEnumeratorOverride = null;

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    // Opt-in hardware check: frames from a real camera, then the same camera through "Any camera".
    internal string RunLiveWebcamCheck()
    {
        var cameras = EnumerateWebcams();
        if (cameras.Count == 0) return "skipped (no camera connected)";
        var layer = CreateWebcamSource(cameras[0].Id, cameras[0].Name, cameras);
        _sources.Add(layer);
        try
        {
            WaitForFrames("the selected camera");
            Require(SetWebcamDeviceFromEditor(layer.Id, null, null, out var error) && layer.IsAnyWebcam, error ?? "Switching to Any camera failed.");
            WaitForFrames("Any camera");
            return $"frames from {cameras[0].Name}, before and after switching to Any camera";
        }
        finally
        {
            ClearSources(persist: false);
        }

        void WaitForFrames(string label)
        {
            layer.FirstFrameReceived = false;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                CaptureSourceList(_sources, 0);
                if (layer.FirstFrameReceived && layer.LastFrame != null && _sources.Contains(layer)) return;
                System.Threading.Thread.Sleep(30);
            }
            throw new InvalidOperationException($"No frames arrived from {label} ({layer.ActiveWebcamName ?? "no device"}).");
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
