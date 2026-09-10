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
        source.SetDisplayName("Keep my layer name");
        source.VideoPlaybackPaused = true;
        var animation = source.Animations.Single();
        var expected = Snapshot();
        expected.Sources[0].Children[1].FilePath = second;

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
        Require(ReplaceFileSourceFromEditor(source.Id, second, out _), "Choosing the current file must be harmless.");
        foreach (string badPath in new[] { Path.Combine(directory, "missing.png"), corrupt })
        {
            Require(!ReplaceFileSourceFromEditor(source.Id, badPath, out error) && !string.IsNullOrWhiteSpace(error),
                "Invalid replacement was accepted.");
            Require(source.FilePath == second && JsonSerializer.Serialize(Snapshot().Sources) == JsonSerializer.Serialize(expected.Sources),
                "Failed replacement changed the layer.");
        }

        // Draft edits must stay isolated until Apply and then reuse the same runtime layer.
        var draft = BuildLayerEditorSources();
        draft[0].Children[1].FilePath = first;
        Require(source.FilePath == second, "Draft file edit changed the live source before Apply.");
        ApplyLayerEditorSources(draft);
        expected.Sources[0].Children[1].FilePath = first;
        Require(ReferenceEquals(group.Children[1], source) && source.FilePath == first &&
            JsonSerializer.Serialize(Snapshot().Sources) == JsonSerializer.Serialize(expected.Sources), "Draft apply lost the file edit or settings.");
        SaveConfig();
        FlushPendingConfigSave();
        using var saved = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Require(saved.RootElement.GetProperty("Sources")[0].GetProperty("Children")[1].GetProperty("FilePath").GetString() == first,
            "App config did not persist the replacement.");
        var roundTrip = LayerConfigFile.Parse(JsonSerializer.Serialize(Snapshot())).ToEditorSources();
        Require(roundTrip[0].Children[1].FilePath == first && roundTrip[0].Children[1].Scale == 1.7 &&
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
