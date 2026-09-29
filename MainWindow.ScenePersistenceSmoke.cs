using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace lifeviz;

public partial class MainWindow
{
    internal void RunScenePersistenceChecks(string directory)
    {
        if (!App.IsSmokeTestMode) throw new InvalidOperationException("Persistence checks require smoke mode.");
        _scenePersistenceTestPath = Path.Combine(directory, "session.json");
        _configReady = true;
        var group = CaptureSource.CreateGroup("Scene must survive shutdown");
        var plane = CaptureSource.CreateColorPlane(12, 34, 56, "Nested authored source");
        plane.Opacity = 0.42;
        group.Children.Add(plane);
        _sources.Add(group);
        var editor = new LayerEditorWindow(this);
        editor.VerifySceneRecoveryLayout(Path.Combine(AppContext.BaseDirectory, "smoke-scene-recovery-editor.png"));
        SaveConfig();
        FlushPendingConfigSave();
        string initial = File.ReadAllText(ConfigPath);

        // An OS-level exclusive lock induces a real writer failure. No edit is
        // made after releasing it: the failed revision itself must be retried.
        using (var lease = new FileStream(ConfigPath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            plane.Opacity = 0.65;
            SaveConfig();
            ConfigSaveTimer_Tick(null, EventArgs.Empty);
            if (!SpinWait.SpinUntil(() => _configWriteFailed, 3000))
                throw new InvalidOperationException("Locked-file save did not report failure.");
            SmokeTestRunner.RequireSceneCheck(File.ReadAllText(ConfigPath) == initial, "Failed write altered the last committed scene.");
        }
        ConfigSaveTimer_Tick(null, EventArgs.Empty);
        FlushPendingConfigSave();
        SmokeTestRunner.RequireSceneCheck(!_configWriteFailed && File.ReadAllText(ConfigPath) == SerializeCurrentConfig(),
            "The failed revision was not retried without another user edit.");

        RunMissingInputChecks(directory);
        _configReady = false;

        string protectedPath = Path.Combine(directory, "unreadable.json");
        File.WriteAllText(protectedPath, "{broken");
        _scenePersistenceTestPath = protectedPath;
        LoadConfig();
        SaveConfig();
        FlushPendingConfigSave();
        SmokeTestRunner.RequireSceneCheck(_configLoadBlocked && !_configReady && File.ReadAllText(protectedPath) == "{broken",
            "A failed load must not enable destructive autosave.");
        SmokeTestRunner.RequireSceneCheck(File.Exists(BlockedSceneRecoveryPath) &&
            File.ReadAllText(BlockedSceneRecoveryPath) == SerializeCurrentConfig(),
            "A session that cannot autosave must still keep its live scene in a recovery file.");
        _blockedRecoveryPath = null;

        string future = "{\"ConfigVersion\":999,\"Framerate\":60,\"Sources\":[]}";
        File.WriteAllText(protectedPath, future);
        File.WriteAllText(protectedPath + ".bak", initial);
        _configLoadBlocked = false;
        LoadConfig();
        SmokeTestRunner.RequireSceneCheck(_configLoadBlocked && File.ReadAllText(protectedPath) == future,
            "A newer schema must not fall back to an older backup and permit overwriting it.");

        _scenePersistenceTestPath = Path.Combine(directory, "session.json");
        _lastPersistedConfigJson = File.ReadAllText(ConfigPath);
        _configLoadBlocked = false;
        _configReady = true;
        _configWriteDelayForSmokeMilliseconds = 100;
        plane.Opacity = 0.75;
        QueueConfigWrite(SerializeCurrentConfig());
        SmokeTestRunner.RequireSceneCheck(SpinWait.SpinUntil(() => _inFlightConfigJson != null, 2000), "The shutdown test needs a real in-flight write.");
        plane.Opacity = 0.87;
        SaveConfig(); // Deliberately leave the 500 ms save pending.
        string expected = SerializeCurrentConfig();
        ShutdownResources();
        ShutdownResources();
        SmokeTestRunner.RequireSceneCheck(GetShutdownErrorMessage() == null && _sources.Count == 0, "Cleanup must still release source sessions.");
        SmokeTestRunner.RequireSceneCheck(File.ReadAllText(ConfigPath) == expected,
            "Shutdown serialized the cleared runtime sources instead of the authored scene.");
        SaveConfig();
        FlushPendingConfigSave();
        SmokeTestRunner.RequireSceneCheck(File.ReadAllText(ConfigPath) == expected, "Late teardown callbacks overwrote the shutdown snapshot.");
        var roundTrip = JsonSerializer.Deserialize<AppConfig>(expected)!;
        SmokeTestRunner.RequireSceneCheck(roundTrip.Sources.Count == 1 && roundTrip.Sources[0].Children[0].Opacity == 0.87,
            "Nested source settings did not survive shutdown.");
    }

    private void RunMissingInputChecks(string directory)
    {
        var authored = _sources.ToList();
        bool wasReady = _configReady;
        _sources.Clear();
        _missingSources.Clear();
        _configReady = false;
        string Json(AppConfig.SourceConfig config) => JsonSerializer.Serialize(config);
        var plane = new AppConfig.SourceConfig { Type = "ColorPlane", Color = "#102030", DisplayName = "Plane A" };
        var window = new AppConfig.SourceConfig { Type = "Window", WindowTitle = "Missing window " + Guid.NewGuid(), Opacity = 0.3 };
        var file = new AppConfig.SourceConfig { Type = "File", FilePath = Path.Combine(directory, "moved-away.mp4"), DisplayName = "Moved movie" };
        var nestedPlane = new AppConfig.SourceConfig { Type = "ColorPlane", Color = "#405060", DisplayName = "Plane B" };
        var group = new AppConfig.SourceConfig { Type = "Group", DisplayName = "Group G", Children = { file, nestedPlane } };
        var webcam = new AppConfig.SourceConfig { Type = "Webcam", WebcamId = "missing-camera-" + Guid.NewGuid(), DisplayName = "Gone camera" };
        RestoreSourceList(new[] { plane, window, group, webcam }, _sources,
            Array.Empty<WindowHandleInfo>(), Array.Empty<WebcamCaptureService.CameraInfo>());
        SmokeTestRunner.RequireSceneCheck(!_configLoadBlocked && MissingInputCount == 3 && _sources.Count == 2,
            "Missing inputs must be kept aside without pausing autosave.");
        var saved = BuildSourceConfigsWithMissingInputs();
        SmokeTestRunner.RequireSceneCheck(saved.Count == 4 && Json(saved[1]) == Json(window) && Json(saved[3]) == Json(webcam) &&
            saved[2].Children.Count == 2 && Json(saved[2].Children[0]) == Json(file) && saved[2].Children[1].DisplayName == "Plane B",
            "Missing inputs must be written back verbatim at their original positions, including inside groups.");

        // User edits: delete the missing window's anchor and add a layer; anchors fall back sensibly.
        _sources.RemoveAt(0);
        _sources.Add(CaptureSource.CreateColorPlane(1, 2, 3, "Plane C"));
        saved = BuildSourceConfigsWithMissingInputs();
        SmokeTestRunner.RequireSceneCheck(saved.Count == 4 && saved[0].Type == "Window" && saved[1].DisplayName == "Group G" &&
            saved[2].Type == "Webcam" && saved[3].DisplayName == "Plane C",
            "Missing inputs must survive edits to their neighbours exactly once.");

        // Deleting a group deletes the missing inputs it contained.
        _sources.RemoveAll(source => source.Type == CaptureSource.SourceType.Group);
        SmokeTestRunner.RequireSceneCheck(MissingInputCount == 2, "A deleted group must take its missing inputs with it.");

        // Autosave is live: the kept layers reach disk alongside new work.
        string sessionPath = ConfigPath;
        string? sessionPersisted = _lastPersistedConfigJson;
        _scenePersistenceTestPath = Path.Combine(directory, "missing-inputs.json");
        _lastPersistedConfigJson = null;
        _configReady = true;
        SaveConfig();
        FlushPendingConfigSave();
        var onDisk = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath))!;
        SmokeTestRunner.RequireSceneCheck(onDisk.Sources.Count(source => source.Type == "Window" && source.WindowTitle == window.WindowTitle) == 1 &&
            onDisk.Sources.Any(source => source.DisplayName == "Plane C"),
            "Autosave must keep writing while inputs are missing.");

        // An input that disappears mid-session is detached live but kept in the scene.
        var gone = CaptureSource.CreateWindow(new WindowHandleInfo(IntPtr.Zero, "Closed PiP " + Guid.NewGuid(), 64, 36));
        _sources.Insert(0, gone);
        PreserveAutomaticallyRemovedSources(_sources, new System.Collections.Generic.List<CaptureSource> { gone });
        _sources.Remove(gone);
        saved = BuildSourceConfigsWithMissingInputs();
        SmokeTestRunner.RequireSceneCheck(MissingInputCount == 3 && saved.Count(c => c.WindowTitle == gone.Window!.Title) == 1 &&
            saved.FindIndex(c => c.WindowTitle == gone.Window!.Title) < saved.FindIndex(c => c.DisplayName == "Plane C"),
            "An automatically detached input must stay in the saved scene at its position.");

        ForgetMissingInputs();
        FlushPendingConfigSave();
        onDisk = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath))!;
        SmokeTestRunner.RequireSceneCheck(MissingInputCount == 0 && onDisk.Sources.Count == 1 && onDisk.Sources[0].DisplayName == "Plane C",
            "Forget Missing Inputs must remove them from the saved scene.");

        // Genuinely blocked autosave: config.json is untouched, the live scene goes to a recovery file.
        string committed = File.ReadAllText(ConfigPath);
        _configLoadBlocked = true;
        _configLoadAttempted = true;
        _sources.Add(CaptureSource.CreateColorPlane(9, 9, 9, "Unsaved work"));
        SaveConfig();
        ConfigSaveTimer_Tick(null, EventArgs.Empty);
        FlushPendingConfigSave();
        SmokeTestRunner.RequireSceneCheck(File.ReadAllText(ConfigPath) == committed &&
            File.ReadAllText(BlockedSceneRecoveryPath).Contains("Unsaved work", StringComparison.Ordinal),
            "Blocked autosave must protect config.json and keep the live scene in a recovery file.");
        _blockedRecoveryPath = null;
        _configLoadBlocked = false;

        _sources.Clear();
        _sources.AddRange(authored);
        _scenePersistenceTestPath = sessionPath;
        _lastPersistedConfigJson = sessionPersisted;
        _configReady = wasReady;
    }
}
