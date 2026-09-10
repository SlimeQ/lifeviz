using System;
using System.IO;
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

        // A missing input must not turn a partial runtime scene into a saved edit.
        _configReady = false;
        RestoreSourceList(new[] { new AppConfig.SourceConfig { Type = "Window", WindowTitle = "Missing-" + Guid.NewGuid() } },
            _sources, Array.Empty<WindowHandleInfo>(), Array.Empty<WebcamCaptureService.CameraInfo>());
        SmokeTestRunner.RequireSceneCheck(_configLoadBlocked, "Missing inputs did not protect the original scene.");
        _configLoadBlocked = false;

        string protectedPath = Path.Combine(directory, "unreadable.json");
        File.WriteAllText(protectedPath, "{broken");
        _scenePersistenceTestPath = protectedPath;
        LoadConfig();
        SaveConfig();
        FlushPendingConfigSave();
        SmokeTestRunner.RequireSceneCheck(_configLoadBlocked && !_configReady && File.ReadAllText(protectedPath) == "{broken",
            "A failed load must not enable destructive autosave.");

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
}
