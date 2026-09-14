using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace lifeviz;

public partial class MainWindow
{
    private static CaptureSource CreateProjectMSource(ProjectMSettings settings, string? name = null) => CaptureSource.CreateProjectM(settings, name);

    private sealed partial class CaptureSource
    {
        public static CaptureSource CreateProjectM(ProjectMSettings settings, string? name) => new(
            SourceType.ProjectM, null, null, null, name ?? "MilkDrop / projectM", null, null)
        {
            ProjectM = settings.Clone(), BlendMode = BlendMode.Normal, FitMode = FitMode.Stretch,
            AddedUtc = DateTime.UtcNow
        };
    }

    private MenuItem BuildAddProjectMMenuItem(CaptureSource? parent)
    {
        var item = new MenuItem { Header = "Add MilkDrop / projectM" };
        item.Click += (_, _) =>
        {
            AddProjectMFromEditor(parent?.Id);
            NotifyLayerEditorSourcesChanged();
        };
        return item;
    }

    internal void AddProjectMFromEditor(Guid? parentId)
    {
        var list = ResolveTargetList(parentId);
        if (list == null) return;
        RunWithoutLayerEditorRefresh(() =>
        {
            list.Add(CreateProjectMSource(ProjectMLibrary.Defaults()));
            RenderFrame(); SaveConfig(); RebuildSourcesMenu();
        });
    }

    private void AddProjectMContextControls(MenuItem menu, CaptureSource source)
    {
        var settings = new MenuItem { Header = "Presets & Playback..." };
        settings.Click += (_, _) =>
        {
            var dialog = new ProjectMSettingsWindow(source.ProjectM, () => GetProjectMStatus(source.Id), action => ControlProjectM(source.Id, action)) { Owner = this };
            if (dialog.ShowDialog() == true) UpdateProjectMFromEditor(source.Id, dialog.Result);
        };
        menu.Items.Add(settings);
        foreach (var entry in new[] { ("Previous Preset", -1), ("Next Preset", 1), ("Retry / Restart Playlist", 0) })
        {
            var item = new MenuItem { Header = entry.Item1 };
            item.Click += (_, _) => ControlProjectM(source.Id, entry.Item2);
            menu.Items.Add(item);
        }
    }

    internal string GetProjectMStatus(Guid id) => FindSourceById(id)?.ProjectMPlayback?.Status ?? "Waiting for layer playback.";
    internal void ControlProjectM(Guid id, int action)
    {
        var source = FindSourceById(id);
        if (source?.Type != CaptureSource.SourceType.ProjectM) return;
        source.ProjectMPlayback ??= new ProjectMPlayback();
        source.ProjectMPlayback.Configure(source.ProjectM);
        if (action == 0) source.ProjectMPlayback.Reset();
        else source.ProjectMPlayback.Move(action, _audioBeatDetector.BeatCount);
        RenderFrame();
    }

    internal void UpdateProjectMFromEditor(Guid id, ProjectMSettings settings)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(id);
            if (source?.Type != CaptureSource.SourceType.ProjectM) return;
            source.ProjectM = settings.Clone();
            source.ProjectMPlayback?.Configure(source.ProjectM);
            RenderFrame(); SaveConfig(); RebuildSourcesMenu();
        });
    }

    private void CaptureProjectMSource(CaptureSource source, double sceneTime)
    {
        source.ProjectMPlayback ??= new ProjectMPlayback();
        if (!source.IsInitialized)
        {
            source.ProjectMPlayback.Configure(source.ProjectM);
            source.IsInitialized = true;
        }
        var engine = GetReferenceSimulationEngine();
        byte[]? pixels = source.ProjectMPlayback.Render(engine.Columns, engine.Rows, sceneTime, _isOfflineRendering, _audioBeatDetector);
        if (pixels != null && pixels.Length != engine.Columns * engine.Rows * 4) pixels = null;
        source.LastFrame = pixels == null ? null : new SourceFrame(pixels, engine.Columns, engine.Rows, null,
            engine.Columns, engine.Rows, source.ProjectMPlayback.FrameToken);
        source.FirstFrameReceived = pixels != null;
        source.HasError = pixels == null;
    }

    private static void PauseProjectMSources(CaptureSource source, double time)
    {
        source.ProjectMPlayback?.Pause(time);
        foreach (var child in source.Children) PauseProjectMSources(child, time);
    }
}
