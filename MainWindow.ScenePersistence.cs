using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace lifeviz;

public partial class MainWindow
{
    private bool _scenePersistenceNoticeShown;
    private string? _scenePersistenceTestPath;
    private readonly string _sceneSessionId = Guid.NewGuid().ToString("N");

    private void PreserveUncommittedScene()
    {
        string? json;
        lock (_configWriteSync) json = _pendingConfigJson ?? _inFlightConfigJson;
        if (json == null) return;
        try
        {
            string path = Path.Combine(SceneRecoveryDirectory, "unsaved-" + _sceneSessionId + ".json");
            SceneFileStore.Write(path, json);
            Logger.Warn($"The uncommitted scene was preserved at {path}.");
        }
        catch (Exception ex) { Logger.Error("Could not preserve the uncommitted scene revision.", ex); }
    }

    internal void BeginSceneReplacement()
    {
        FlushPendingConfigSave();
        if (_configReady && !_configLoadBlocked)
        {
            string checkpoint = Path.Combine(SceneRecoveryDirectory, DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'") + "-before-import.json");
            SceneFileStore.Write(checkpoint, SerializeCurrentConfig());
        }
        _configReady = false;
    }

    internal void CompleteSceneReplacement(bool complete)
    {
        _configLoadBlocked = !complete;
        _configReady = complete;
        if (complete) SaveConfig();
        else ReportScenePersistenceIssue("The scene could not be fully applied. Autosave is paused to protect the previous scene. Reconnect missing inputs or load another scene.");
    }

    internal void ResetToDefaultProject()
    {
        var starter = DefaultScene.Create(); // Validate the asset before replacing anything.
        BeginSceneReplacement();
        bool complete = false;
        try
        {
            // A fresh project must restart the demo, even if the previous scene
            // also used it. Retire the old session before creating the new one.
            ClearSources(persist: false);
            ApplyProjectSettingsFromEditor(starter.ToEditorProjectSettings());
            ApplyLayerEditorSources(starter.ToEditorSources());
            complete = _sources.Count == 2 &&
                _sources[0].Type == CaptureSource.SourceType.File &&
                _sources[1].Type == CaptureSource.SourceType.SimGroup &&
                _sources[1].SimulationLayers.Count == 1;
            if (!complete) throw new InvalidOperationException("The starter scene could not be fully applied.");
        }
        finally { CompleteSceneReplacement(complete); }
    }

    private static void ValidateAppConfigJson(string json)
    {
        SceneFileStore.ValidateSceneJson(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("Version", out _) ||
            !document.RootElement.TryGetProperty("Framerate", out _))
            throw new InvalidDataException("This is not a LifeViz autosave.");
        var config = JsonSerializer.Deserialize<AppConfig>(json)
            ?? throw new InvalidDataException("The scene is empty.");
        if (config.ConfigVersion > CurrentConfigVersion)
            throw new NotSupportedException("This scene was written by a newer version of LifeViz.");
    }

    private void ReportScenePersistenceIssue(string message)
    {
        Logger.Warn(message);
        _configSaveError = message;
        if (_scenePersistenceNoticeShown || App.SuppressErrorDialogs || App.IsSmokeTestMode) return;
        _scenePersistenceNoticeShown = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isShuttingDown)
                MessageBox.Show(this, message, "LifeViz scene recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
        }));
    }

    internal string SceneSaveStatus => _configLoadBlocked ? "Autosave paused: saved scene protected"
        : _configConflict ? "Autosave paused: another session changed the file"
        : _configWriteFailed ? "Autosave failed: retrying"
        : _configSaveDirty || _pendingConfigJson != null || _inFlightConfigJson != null ? "Autosave pending"
        : "Autosave up to date";

    internal string? SceneSaveError => _configSaveError;
    internal string SceneRecoveryDirectory => SceneFileStore.HistoryDirectory(ConfigPath);
    internal string EditorDraftRecoveryPath => Path.Combine(SceneRecoveryDirectory, "editor-draft-" + _sceneSessionId + ".json");
}
