using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace lifeviz;

internal static partial class SmokeTestRunner
{
    private static int RunProjectMSmokeTest()
    {
        ValidateProjectMPlaylist();
        bool previous = App.IsDiagnosticTestMode;
        App.IsDiagnosticTestMode = true;
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Exception? error = null;
        app.Startup += (_, _) =>
        {
            try
            {
                var window = new MainWindow();
                window.RunProjectMSmoke();
                Logger.Info("projectM smoke passed: native frames, audio, transitions, resize, layer/group compositing, playlists, persistence and error recovery.");
            }
            catch (Exception ex) { error = ex; Logger.Error($"projectM smoke failed: {ex}"); }
            app.Shutdown(error == null ? 0 : 1);
        };
        try { app.Run(); if (error != null) throw error; return 0; }
        finally { App.IsDiagnosticTestMode = previous; }
    }

    private static void ValidateProjectMPlaylist()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var settings = new ProjectMSettings { Presets = new() { "a.milk", "b.milk", "c.milk" }, Order = "Ordered", DurationSeconds = 2, TransitionSeconds = 0 };
        var list = new ProjectMPlaylist(); list.Configure(settings);
        Check(list.Tick(0, 100) && list.Current == "a.milk", "Ordered playlist must start at the first entry.");
        Check(!list.Move(-1, 0, 100) && list.Current == "a.milk", "Previous must not select a random/new preset before any history exists.");
        Check(!list.Tick(1, 101) && list.Tick(2, 102) && list.Current == "b.milk", "Timed preset boundary failed.");
        list.Move(-1, 2.1, 102); Check(list.Current == "a.milk", "Previous must navigate playback history.");
        list.Move(1, 2.2, 102); Check(list.Current == "b.milk", "Next must navigate forward in history.");
        settings.DurationSeconds = 5; list.Configure(settings);
        Check(!list.Tick(3, 103) && list.Current == "b.milk", "Timing edits must preserve the current preset and elapsed time.");
        settings.Advance = "Beats"; settings.BeatsPerPreset = 2; settings.MinimumSeconds = 3; list.Configure(settings);
        Check(!list.Tick(4, 105) && list.Tick(6, 106), "Beat advance must honor the minimum duration.");
        string? current = list.Current;
        Check(!list.Tick(7, 1) && list.Current == current, "Audio input resets must not look like new beats.");
        settings.Advance = "TimedOnBeat"; settings.DurationSeconds = 2; list.Configure(settings);
        Check(!list.Tick(9, 1) && list.Tick(10, 2), "Timed-on-beat must wait for a fresh beat.");
        settings.Advance = "Hold"; list.Configure(settings); Check(!list.Tick(9999, 9999), "Hold must suppress automatic changes.");
        settings.Order = "Shuffle"; settings.Advance = "Timed"; list.Configure(settings); list.Reset();
        var played = new List<string>();
        for (int i = 0; i < 12; i++) { list.Move(1, i, i); played.Add(list.Current!); }
        for (int i = 0; i < 12; i += 3) Check(played.Skip(i).Take(3).Distinct().Count() == 3, "Shuffle repeated within a cycle.");
        Check(Enumerable.Range(1, played.Count - 1).All(i => played[i] != played[i - 1]), "Shuffle immediately repeated across cycles.");
        list.Reset();
        for (int i = 0; i < played.Count; i++) { list.Move(1, i, i); Check(list.Current == played[i], "Shuffle seed must reproduce preset order for bakes."); }
        var model = new LayerEditorSource { Kind = LayerEditorSourceKind.ProjectM, ProjectM = settings, DisplayName = "MilkDrop test" };
        var saved = LayerConfigFile.FromEditorSources(new[] { model }, Array.Empty<LayerEditorSimulationLayer>(), new LayerEditorProjectSettings());
        var restored = LayerConfigFile.Parse(JsonSerializer.Serialize(saved)).ToEditorSources().Single(s => s.IsProjectM);
        Check(JsonSerializer.Serialize(restored.ProjectM) == JsonSerializer.Serialize(settings), "ProjectM settings did not survive scene serialization.");
        restored.ProjectM.Presets.Clear(); Check(settings.Presets.Count == 3, "Scene snapshots must not share mutable playlists.");
        var empty = new ProjectMPlaylist(); empty.Configure(new()); Check(!empty.Tick(0, 0), "An empty playlist must be safe.");
        Check(new ProjectMSettings { DurationSeconds = double.NaN, TransitionSeconds = double.PositiveInfinity }.Clone().DurationSeconds == 30, "Invalid settings must normalize.");
    }
}
