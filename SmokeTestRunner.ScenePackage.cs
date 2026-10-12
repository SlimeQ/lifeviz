using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace lifeviz;

internal static partial class SmokeTestRunner
{
    private static int RunScenePackageSmokeTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-package-test-" + Guid.NewGuid().ToString("N"));
        string mediaA = Path.Combine(directory, "sender", "a");
        string mediaB = Path.Combine(directory, "sender", "b");
        Directory.CreateDirectory(mediaA);
        Directory.CreateDirectory(mediaB);
        string Make(string folder, string name, string text)
        {
            string path = Path.Combine(folder, name);
            File.WriteAllText(path, text);
            return path;
        }

        // Same file name in two folders must not collide inside the package.
        string clipA = Make(mediaA, "clip.mp4", "clip-a");
        string clipB = Make(mediaB, "clip.mp4", "clip-b");
        string still = Make(mediaA, "still.png", "still");
        string movie = Make(mediaA, "movie.mkv", "movie");
        string subtitles = Make(mediaA, "movie.srt", "1\n00:00:00,000 --> 00:00:01,000\nhi\n");
        string preset = Make(mediaB, "custom.milk", "preset");
        string missing = Path.Combine(mediaA, "gone.mp4");
        var movieId = Guid.NewGuid();
        var scene = new LayerConfigFile
        {
            Sources = new()
            {
                new() { Type = "File", DisplayName = "Still", FilePath = still },
                new() { Type = "File", DisplayName = "Starter", FilePath = DefaultScene.VideoReference },
                new() { Type = "File", DisplayName = "Tube", FilePath = "youtube:dQw4w9WgXcQ" },
                new() { Type = "File", DisplayName = "Gone", FilePath = missing },
                new()
                {
                    Type = "Group", DisplayName = "Group", Children = new()
                    {
                        new() { Type = "Window", DisplayName = "Browser", WindowTitle = "Browser" },
                        new() { Type = "VideoSequence", FilePaths = new() { clipA, clipB, clipA } }
                    }
                },
                new()
                {
                    Type = "AutoClip", FilePaths = new() { clipA, clipB },
                    AutoClipVideoOverrides = new() { new() { FilePath = clipB, BlendMode = "Normal" } }
                },
                new()
                {
                    Type = "MoviePlaylist",
                    MoviePlaylist = new() { Movies = new() { new() { Id = movieId, FilePath = movie, SubtitleMode = "Srt", SubtitlePath = subtitles } } }
                },
                new() { Type = "ProjectM", ProjectM = new() { Presets = new() { "Waveform/Spectrum/bundled.milk", preset } } },
                new() { Type = "Webcam", DisplayName = "Cam", WebcamId = "cam-1" }
            }
        };

        try
        {
            string original = JsonSerializer.Serialize(scene);
            var plan = ScenePackage.CreatePlan(scene);
            Require(JsonSerializer.Serialize(scene) == original, "Planning must not modify the editor's scene.");
            Require(plan.Media.Count == 6, $"Expected 6 unique media files, got {plan.Media.Count}.");
            Require(plan.Media.Select(file => file.EntryName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 6, "Package entry names must be unique.");
            Require(plan.NotIncluded.Any(note => note.Contains("gone.mp4")), "A missing file must be reported.");
            Require(plan.NotIncluded.Any(note => note.Contains("Browser")) && plan.NotIncluded.Any(note => note.Contains("Cam")), "Live inputs must be reported.");
            Require(plan.NotIncluded.Any(note => note.Contains("YouTube")), "YouTube links must be reported.");
            Require(ScenePackage.DescribeLiveInputs(scene).Count == 1, "Only window captures are live inputs to leave out.");
            var sharedCam = plan.Scene.Sources[8];
            Require(sharedCam.Type == "Webcam" && sharedCam.WebcamId == null && sharedCam.DisplayName == MainWindow.AnyWebcamName &&
                    scene.Sources[8].WebcamId == "cam-1", "Shared webcam layers must become Any camera without changing the sender's scene.");

            string package = Path.Combine(directory, "Party Scene" + ScenePackage.Extension);
            ScenePackage.Write(plan, package);
            Require(File.Exists(package) && !File.Exists(package + ".partial"), "Package must be committed without a partial file.");

            // The recipient does not have the sender's folders.
            Directory.Delete(Path.Combine(directory, "sender"), recursive: true);
            string importRoot = Path.Combine(directory, "recipient");
            var imported = ScenePackage.Import(package, importRoot);
            var sources = imported.Scene.Sources;
            Require(imported.MediaFolder.StartsWith(importRoot, StringComparison.OrdinalIgnoreCase), "Media must extract under the import root.");
            Require(File.ReadAllText(sources[0].FilePath!) == "still", "File layer media must be extracted and relinked.");
            Require(sources[1].FilePath == DefaultScene.VideoReference && sources[2].FilePath == "youtube:dQw4w9WgXcQ",
                "Bundled starter media and YouTube links must stay references.");
            Require(sources[3].FilePath == missing, "Missing media must keep its original reference.");
            var sequence = sources[4].Children[1].FilePaths;
            Require(sequence.Count == 3 && File.ReadAllText(sequence[0]) == "clip-a" && File.ReadAllText(sequence[1]) == "clip-b" && sequence[0] == sequence[2],
                "Sequence order and duplicate references must survive.");
            Require(sources[4].Children[0].Type == "Window", "Live inputs stay in the scene unless the sender leaves them out.");
            Require(sources[5].AutoClipVideoOverrides[0].FilePath == sources[5].FilePaths[1], "AutoClip overrides must follow their relinked file.");
            var autoClip = imported.Scene.ToEditorSources()[5];
            Require(autoClip.AutoClipVideoOverrides.Any(item => item.BlendMode == "Normal" && File.ReadAllText(item.FilePath) == "clip-b"),
                "AutoClip overrides must load in the editor after import.");
            var entry = sources[6].MoviePlaylist!.Movies[0];
            Require(entry.Id == movieId && File.ReadAllText(entry.FilePath) == "movie" && File.ReadAllText(entry.SubtitlePath!).Contains("hi"),
                "Movie and external subtitle must be relinked with the same playlist ID.");
            var presets = sources[7].ProjectM.Presets;
            Require(presets[0] == "Waveform/Spectrum/bundled.milk" && File.ReadAllText(presets[1]) == "preset",
                "Bundled presets stay library references; custom presets are bundled.");
            Require(imported.NotIncluded.Count == plan.NotIncluded.Count, "The recipient must see what the sender's package left out.");

            // Re-importing the same package reuses the extracted media (it may be playing).
            DateTime written = File.GetLastWriteTimeUtc(sources[0].FilePath!);
            using (File.Open(sources[0].FilePath!, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var again = ScenePackage.Import(package, importRoot);
                Require(again.MediaFolder == imported.MediaFolder && File.GetLastWriteTimeUtc(again.Scene.Sources[0].FilePath!) == written,
                    "Re-import must reuse an intact extraction.");
            }
            Require(Directory.GetDirectories(importRoot).Length == 1, "No staging folders may remain after import.");

            var omitted = ScenePackage.CreatePlan(scene, omitLiveInputs: true);
            Require(ScenePackage.DescribeLiveInputs(omitted.Scene).Count == 0 && omitted.Scene.Sources[4].Children.Count == 1 &&
                    !omitted.NotIncluded.Any(note => note.Contains("Browser")) && omitted.Scene.Sources.Any(source => source.Type == "Webcam"),
                "Leaving out window captures must remove nested ones too and keep webcam layers.");

            // A hostile package cannot write outside its extraction folder.
            string hostile = Path.Combine(directory, "hostile" + ScenePackage.Extension);
            using (var archive = ZipFile.Open(hostile, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("lifeviz-package.json").Open()))
                    writer.Write("{\"FormatVersion\":1,\"PackageId\":\"" + Guid.NewGuid() + "\",\"Name\":\"hostile\"}");
                using (var writer = new StreamWriter(archive.CreateEntry("scene.lifevizlayers.json").Open()))
                    writer.Write("{\"Version\":16,\"Sources\":[]}");
                using (var writer = new StreamWriter(archive.CreateEntry("media/../../escaped.txt").Open()))
                    writer.Write("escaped");
            }
            bool rejected = false;
            try { ScenePackage.Import(hostile, importRoot); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected && !File.Exists(Path.Combine(directory, "escaped.txt")), "Path traversal entries must be rejected.");

            Console.WriteLine("Scene package smoke passed: media bundling, name collisions, relinking (files, sequences, AutoClip overrides, movies, subtitles, presets), references kept, missing/live input reporting, window omission, portable webcams, re-import reuse, path traversal rejection.");
            Directory.Delete(directory, recursive: true);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Scene package smoke failed (artifacts kept in {directory}): {ex}");
            return 1;
        }

        static void Require(bool condition, string message) => RequireSceneCheck(condition, message);
    }
}
