using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace lifeviz;

internal static partial class SmokeTestRunner
{
    private static int RunScenePersistenceSmokeTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-persistence-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        App.IsDiagnosticTestMode = true;
        var app = new App();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Exception? failure = null;
        app.Startup += (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                RunSceneFileStoreChecks(directory);
                window = new MainWindow();
                window.RunScenePersistenceChecks(directory);
                Console.WriteLine("Scene persistence smoke passed: atomic replacement, backups, corruption recovery, conflict protection, retry, empty scene, protected load, future schema protection, draft recovery, project import, shutdown snapshot.");
            }
            catch (Exception ex) { failure = ex; Console.WriteLine(ex); }
            finally
            {
                window?.ShutdownResources();
                app.Shutdown(failure == null ? 0 : 1);
            }
        };
        int result = app.Run();
        // Keep failed artifacts for diagnosis; successful test fixtures are isolated
        // under a freshly created, known temp directory.
        if (failure == null) Directory.Delete(directory, recursive: true);
        return failure == null ? result : 1;
    }

    private static void RunSceneFileStoreChecks(string directory)
    {
        string path = Path.Combine(directory, "store.json");
        string first = "{\"Sources\":[{\"Type\":\"Group\",\"Children\":[{\"Type\":\"ColorPlane\",\"Color\":\"#ABCDEF\"}]}]}";
        string empty = "{\"Sources\":[]}";
        SceneFileStore.Write(path, first, checkForConflict: true);
        SceneFileStore.Write(path, empty, first, checkForConflict: true);
        RequireSceneCheck(File.ReadAllText(path + ".bak") == first, "Replacement must back up the complete preceding scene.");
        RequireSceneCheck(File.ReadAllText(path) == empty, "An intentional empty scene must persist.");
        RequireSceneCheck(SceneFileStore.ReadRecoverable(path, SceneFileStore.ValidateSceneJson).json == empty,
            "An intentional empty scene must not trigger recovery.");
        bool conflict = false;
        try { SceneFileStore.Write(path, first, first, checkForConflict: true); }
        catch (SceneSaveConflictException) { conflict = true; }
        RequireSceneCheck(conflict && File.ReadAllText(path) == empty, "A stale writer must not overwrite a newer scene.");
        bool rejected = false;
        try { SceneFileStore.Write(path, "{}"); }
        catch (InvalidDataException) { rejected = true; }
        RequireSceneCheck(rejected && File.ReadAllText(path) == empty, "Invalid output must leave the primary untouched.");
        File.WriteAllText(path, "{truncated");
        var recovered = SceneFileStore.ReadRecoverable(path, SceneFileStore.ValidateSceneJson);
        RequireSceneCheck(recovered.json == first && recovered.source == path + ".bak", "A truncated primary must recover from its backup.");
        File.WriteAllText(path + ".bak", "broken too");
        RequireSceneCheck(SceneFileStore.ReadRecoverable(path, SceneFileStore.ValidateSceneJson).json == first,
            "History must recover when both primary and backup are corrupt.");
        SceneFileStore.Write(path, first, "{truncated", checkForConflict: true);
        RequireSceneCheck(Directory.GetFiles(SceneFileStore.HistoryDirectory(path), "*.invalid").Length == 1,
            "Repair must preserve the damaged predecessor.");
        RequireSceneCheck(Directory.GetFiles(directory, "*.tmp").Length == 0, "No temporary replacement files should remain.");
        for (int i = 0; i < SceneFileStore.HistoryLimit + 5; i++)
            SceneFileStore.Write(path, "{\"Sources\":[],\"Revision\":" + i + "}");
        RequireSceneCheck(Directory.GetFiles(SceneFileStore.HistoryDirectory(path), "*.json").Length == SceneFileStore.HistoryLimit,
            "Automatic history must remain bounded.");
        var project = LayerConfigFile.Parse("{\"ConfigVersion\":1,\"Height\":1080,\"Framerate\":30,\"BlendMode\":\"Lighten\",\"Sources\":[{\"Type\":\"ColorPlane\",\"Color\":\"#ABCDEF\"}]}");
        RequireSceneCheck(project.ProjectSettings.Height == 1080 && project.ProjectSettings.CompositeBlendMode == "Lighten" &&
                          project.ToEditorSources().Any(source => source.ColorHex == "#ABCDEF"), "Autosave import must retain project settings and source content.");
    }

    internal static void RequireSceneCheck(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
