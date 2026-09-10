using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace lifeviz;

public partial class MainWindow
{
    internal void RunFileReplacementChecks(string directory)
    {
        if (!App.IsSmokeTestMode) throw new InvalidOperationException("File replacement checks require smoke mode.");
        _scenePersistenceTestPath = Path.Combine(directory, "session.json");
        _configReady = true;
        string first = WriteImage("first.png", 4, 4, 255, 0);
        string second = WriteImage("second.png", 8, 4, 0, 255);
        string corrupt = Path.Combine(directory, "corrupt.png");
        File.WriteAllText(corrupt, "not an image");

        var group = CaptureSource.CreateGroup("Keep parent");
        var sibling = CaptureSource.CreateColorPlane(1, 2, 3, "Keep sibling");
        _sources.Add(group);
        group.Children.Add(sibling);
        AddFileSourceFromEditor(first, group.Id);
        var source = group.Children.Last();
        var model = BuildLayerEditorSources()[0].Children.Last();
        model.Enabled = false;
        model.BlendMode = "Normal";
        model.FitMode = "Fit";
        model.Opacity = 0.43;
        model.Scale = 1.7;
        model.Mirror = true;
        model.KeyEnabled = true;
        model.KeyColorHex = "#12AB34";
        model.KeyTolerance = 0.21;
        model.VideoAudioEnabled = true;
        model.VideoAudioVolume = 0.36;
        model.VisibilityGroup = "Keep visibility";
        model.Animations.Add(new LayerEditorAnimation { Type = "Rotate", StartAngleDegrees = 37 });
        ApplySourceModel(source, model);
        source.SetDisplayName("Old layer name");
        source.VideoPlaybackPaused = true;
        var animation = source.Animations.Single();
        var expected = Snapshot();
        expected.Sources[0].Children[1].FilePath = second;
        expected.Sources[0].Children[1].DisplayName = "second.png";

        Require(ReplaceFileSourceFromEditor(source.Id, second, out var error), error ?? "Live replacement failed.");
        Require(ReferenceEquals(group.Children[1], source) && ReferenceEquals(group.Children[0], sibling) &&
            ReferenceEquals(source.Animations.Single(), animation) && source.VideoPlaybackPaused,
            "Live replacement recreated or reordered the layer or reset its animation/transport.");
        Require(JsonSerializer.Serialize(Snapshot().Sources) == JsonSerializer.Serialize(expected.Sources), "Replacement changed authored settings.");
        Require(source.FileWidth == 8 && source.FileHeight == 4 && source.LastFrame == null,
            "Replacement retained old dimensions/frame.");
        var frame = _fileCapture.CaptureFrame(second, 8, 4, FitMode.Stretch);
        Require(frame != null && frame.Value.OverlayDownscaled[2] == 0 && frame.Value.OverlayDownscaled[1] == 255,
            "Replacement did not decode the new image.");
        source.SetDisplayName("Stale name from an earlier replacement");
        Require(ReplaceFileSourceFromEditor(source.Id, second, out _) && source.DisplayName == "second.png",
            "Choosing the current file must refresh its name.");
        foreach (string badPath in new[] { Path.Combine(directory, "missing.png"), corrupt })
        {
            Require(!ReplaceFileSourceFromEditor(source.Id, badPath, out error) && !string.IsNullOrWhiteSpace(error),
                "Invalid replacement was accepted.");
            Require(source.FilePath == second && JsonSerializer.Serialize(Snapshot().Sources) == JsonSerializer.Serialize(expected.Sources),
                "Failed replacement changed the layer.");
        }

        // Draft edits must stay isolated until Apply and then reuse the same runtime layer.
        var draft = BuildLayerEditorSources();
        draft[0].Children[1].ReplaceFile(first);
        Require(draft[0].Children[1].DisplayName == "first.png" && draft[0].Children[1].TreeLabel.Contains("first.png"),
            "Draft replacement did not update the layer label.");
        var savedDraft = LayerConfigFile.FromEditorSources(draft, Array.Empty<LayerEditorSimulationLayer>(), GetProjectSettingsForEditor());
        Require(LayerConfigFile.Parse(JsonSerializer.Serialize(savedDraft)).ToEditorSources()[0].Children[1].DisplayName == "first.png",
            "Draft save lost the new layer name.");
        Require(source.FilePath == second && source.DisplayName == "second.png", "Draft file edit changed the live source before Apply.");
        ApplyLayerEditorSources(draft);
        expected.Sources[0].Children[1].FilePath = first;
        expected.Sources[0].Children[1].DisplayName = "first.png";
        Require(ReferenceEquals(group.Children[1], source) && source.FilePath == first &&
            JsonSerializer.Serialize(Snapshot().Sources) == JsonSerializer.Serialize(expected.Sources), "Draft apply lost the file edit or settings.");
        source.SetDisplayName("Stale name before draft reselect");
        draft = BuildLayerEditorSources();
        draft[0].Children[1].ReplaceFile(first);
        ApplyLayerEditorSources(draft);
        Require(source.DisplayName == "first.png", "Draft Apply did not refresh the name when reselecting the current file.");
        SaveConfig();
        FlushPendingConfigSave();
        using var saved = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var savedSource = saved.RootElement.GetProperty("Sources")[0].GetProperty("Children")[1];
        Require(savedSource.GetProperty("FilePath").GetString() == first && savedSource.GetProperty("DisplayName").GetString() == "first.png",
            "App config did not persist the replacement path and name.");
        var roundTrip = LayerConfigFile.Parse(JsonSerializer.Serialize(Snapshot())).ToEditorSources();
        Require(roundTrip[0].Children[1].FilePath == first && roundTrip[0].Children[1].DisplayName == "first.png" && roundTrip[0].Children[1].Scale == 1.7 &&
            roundTrip[0].Children[1].Animations.Single().StartAngleDegrees == 37, "Scene export/import lost replacement settings.");

        LayerConfigFile Snapshot() => LayerConfigFile.FromEditorSources(BuildLayerEditorSources(), Array.Empty<LayerEditorSimulationLayer>(), GetProjectSettingsForEditor());
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        string WriteImage(string name, int width, int height, byte red, byte green)
        {
            byte[] pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 1] = green;
                pixels[i + 2] = red;
                pixels[i + 3] = 255;
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
            string path = Path.Combine(directory, name);
            using var stream = File.Create(path);
            encoder.Save(stream);
            return path;
        }
    }
}
