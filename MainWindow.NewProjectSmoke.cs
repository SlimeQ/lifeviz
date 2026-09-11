using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace lifeviz;

public partial class MainWindow
{
    internal void RunNewProjectSmoke()
    {
        if (!App.IsSmokeTestMode) throw new InvalidOperationException("New-project checks require smoke mode.");
        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-new-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _scenePersistenceTestPath = Path.Combine(directory, "config.json");
        _lastPersistedConfigJson = null;
        _configReady = true;
        _configLoadBlocked = false;
        _recordingOutputFolder = Path.Combine(directory, "recordings-preference");
        string recordingPreference = _recordingOutputFolder;
        try
        {
            var editor = new LayerEditorWindow(this);
            try
            {
                editor.VerifyNewProjectLayout(Path.Combine(AppContext.BaseDirectory, "smoke-new-project-editor.png"));
                foreach (bool liveMode in new[] { true, false })
                {
                    var authored = new LayerEditorSource
                    {
                        Id = Guid.NewGuid(), Kind = LayerEditorSourceKind.ColorPlane,
                        DisplayName = "Recover this authored scene", ColorHex = "#AC1234", BlendMode = "Normal"
                    };
                    ApplyLayerEditorSources(new[] { authored });
                    ApplyProjectSettingsFromEditor(new LayerEditorProjectSettings { Height = 144, Depth = 24, Framerate = 60 });
                    editor.RefreshFromSourcesIfLive();
                    editor.SetLiveModeForSmoke(liveMode);
                    if (!liveMode) editor.PrepareNewProjectDraftForSmoke();
                    editor.CreateNewProject();
                    FlushPendingConfigSave();
                    SmokeTestRunner.RequireSceneCheck(HasUsableDefaultScene() && _configuredRows == 480 &&
                        _configuredDepth == 12 && _currentFpsFromConfig == 30 && _passthroughEnabled,
                        "New Project did not restore the exact starter stack and project settings.");
                    SmokeTestRunner.RequireSceneCheck(_recordingOutputFolder == recordingPreference,
                        "New Project reset the recording-folder preference.");
                    SmokeTestRunner.RequireSceneCheck(Directory.GetFiles(SceneRecoveryDirectory, "*-before-import.json")
                        .Any(path => File.ReadAllText(path).Contains("Recover this authored scene")),
                        "New Project did not checkpoint the previous scene.");
                    if (!liveMode)
                        SmokeTestRunner.RequireSceneCheck(File.Exists(EditorDraftRecoveryPath) &&
                            LayerConfigFile.Parse(File.ReadAllText(EditorDraftRecoveryPath)).Sources[0].DisplayName == "Unapplied New Project draft",
                            "New Project lost the unapplied draft.");
                    using var config = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                    SmokeTestRunner.RequireSceneCheck(config.RootElement.GetProperty("Sources")[0].GetProperty("FilePath").GetString() == DefaultScene.VideoReference,
                        "Autosave embedded a version-specific install path.");
                    var project = LayerConfigFile.FromEditorSources(BuildLayerEditorSources(), Array.Empty<LayerEditorSimulationLayer>(), GetProjectSettingsForEditor());
                    string json = JsonSerializer.Serialize(project);
                    var restored = LayerConfigFile.Parse(json).ToEditorSources();
                    SmokeTestRunner.RequireSceneCheck(json.Contains(DefaultScene.VideoReference) && restored[0].FilePath == DefaultScene.VideoPath,
                        "Export/import did not retain a portable built-in video reference.");
                    // Repeated New Project must retire and restart the same file source.
                    Guid previousId = _sources[0].Id;
                    editor.CreateNewProject();
                    SmokeTestRunner.RequireSceneCheck(_sources[0].Id != previousId && HasUsableDefaultScene(),
                        "Repeated New Project reused the previous runtime source.");
                }
                editor.VerifyNewProjectLayout(Path.Combine(AppContext.BaseDirectory, "smoke-new-project-editor.png"));
            }
            finally { editor.Close(); }
            FlushPendingConfigSave();
            // Reload via the actual autosave path, as on the next launch/update.
            ClearSources(persist: false);
            var loaded = LoadConfig();
            SmokeTestRunner.RequireSceneCheck(loaded.loaded && HasUsableDefaultScene(), "Starter autosave could not reload.");
            VerifyStarterPlaybackForSmoke();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "starter.lifevizlayers.json"),
                JsonSerializer.Serialize(DefaultScene.Create(), new JsonSerializerOptions { WriteIndented = true }));
            Logger.Info("New Project smoke passed: live/deferred reset, recovery, preferences, portable export, repeated reset, and autosave reload.");
        }
        finally
        {
            FlushPendingConfigSave();
            _configReady = false;
            _configSaveTimer.Stop();
            Directory.Delete(directory, recursive: true);
        }
    }

    private void VerifyStarterPlaybackForSmoke()
    {
        var clock = Stopwatch.StartNew();
        double previousPosition = 0;
        bool looped = false;
        long seamToken = -1;
        bool advancedAfterLoop = false;
        while (clock.Elapsed < TimeSpan.FromSeconds(9))
        {
            InjectCaptureFrames(injectLayers: true);
            RenderFrame();
            var engine = GetReferenceSimulationEngine();
            var frame = _fileCapture.CaptureFrame(DefaultScene.VideoPath, engine.Columns, engine.Rows, FitMode.Fit, includeSource: false);
            if (_fileCapture.TryGetVideoPlaybackState(DefaultScene.VideoPath, out var playback))
            {
                if (previousPosition > 5 && playback.PositionSeconds < 1)
                {
                    looped = true;
                    seamToken = frame?.FrameToken ?? -1;
                }
                previousPosition = playback.PositionSeconds;
            }
            if (looped && frame.HasValue && frame.Value.FrameToken > seamToken && BufferHasNonBlackPixel(frame.Value.OverlayDownscaled))
                advancedAfterLoop = true;
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(20);
        }
        var composite = _lastCompositeFrame;
        SmokeTestRunner.RequireSceneCheck(looped && advancedAfterLoop && composite != null &&
            BufferHasNonBlackPixel(composite.Downscaled), "Starter did not present the scene and continue decoding after the loop seam.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(composite!.DownscaledWidth, composite.DownscaledHeight,
            96, 96, PixelFormats.Bgra32, null, composite.Downscaled, composite.DownscaledWidth * 4)));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-starter-scene.png"));
        encoder.Save(stream);
    }
}
