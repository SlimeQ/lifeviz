using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace lifeviz;

public partial class MainWindow
{
    internal void RunProjectMSmoke()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        string folder = Path.Combine(AppContext.BaseDirectory, "projectm-smoke");
        Directory.CreateDirectory(folder);
        var settings = ProjectMLibrary.Defaults();
        Check(ProjectMLibrary.Presets.Count > 9000 && settings.Presets.Count >= 2, "The bundled preset collection is incomplete.");
        settings.Order = "Ordered"; settings.Advance = "Hold";
        _audioBeatDetector.BeginOfflineInput();
        _isOfflineRendering = true;
        foreach (var old in _sources.ToArray()) CleanupSource(old);
        _sources.Clear();
        var source = CreateProjectMSource(settings);
        var group = CaptureSource.CreateGroup("MilkDrop smoke group"); group.BlendMode = BlendMode.Normal;
        group.Children.Add(source); _sources.Add(group);
        byte[]? first = null;
        var tone = new float[1600];
        try
        {
            for (int frame = 0; frame < 45; frame++)
            {
                double time = frame / 30.0;
                for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.65 * Math.Sin((frame * tone.Length + i) * Math.PI * 2 * 110 / 48000));
                _audioBeatDetector.ProcessOfflineSamples(tone, time);
                CaptureSourceList(_sources, time);
                Check(source.LastFrame != null, source.ProjectMPlayback?.Status ?? "No projectM frame.");
                if (frame == 10) first = source.LastFrame!.Downscaled.ToArray();
            }
            byte[] latest = source.LastFrame!.Downscaled;
            Check(first != null && !first.SequenceEqual(latest), "ProjectM frames did not animate under a fixed offline clock.");
            Check(latest.Where((_, i) => i % 4 != 3).Any(b => b > 5), "ProjectM output was black.");
            var engine = GetReferenceSimulationEngine();
            SaveProjectMSmokeImage(latest, engine.Columns, engine.Rows, Path.Combine(folder, "native-output.png"));
            byte[]? buffer = null;
            ResetGpuSourceCompositeSmokeCounters();
            var composite = BuildCompositeFrame(_sources, ref buffer, true, 1.5, includeCpuReadback: true);
            Check(composite != null && GetGpuSourceCompositePassCount() > 0, "ProjectM did not pass through group/GPU compositing.");
            Check(composite!.Downscaled.Any(b => b != 0), "The projectM group composite was empty.");
            var configs = BuildSourceConfigs();
            var restoredConfig = JsonSerializer.Deserialize<AppConfig.SourceConfig>(JsonSerializer.Serialize(configs[0]))!;
            Check(restoredConfig.Children[0].ProjectM.Presets.SequenceEqual(settings.Presets), "Autosave lost the playlist.");
            Check(source.IsAspectNeutral && group.IsAspectNeutral, "A procedural projectM layer must not change scene aspect.");
            source.Opacity = 0.5; source.Scale = 0.7;
            var transformed = BuildCompositeFrame(_sources, ref buffer, true, 1.5, includeCpuReadback: true);
            Check(transformed != null, "Transformed projectM layer failed to composite.");
            source.Enabled = false; long token = source.ProjectMPlayback!.FrameToken;
            CaptureSourceList(_sources, 2); Check(source.ProjectMPlayback.FrameToken == token, "Disabled projectM layer kept rendering.");
            source.Enabled = true;
            source.ProjectMPlayback.Move(1, _audioBeatDetector.BeatCount);
            CaptureSourceList(_sources, 2.1);
            Check(source.ProjectMPlayback.Status.Contains(settings.Presets[1]), "Manual preset advance failed.");
            // Exercise another instance and a resize independently of the scene engine.
            using var renderer = new ProjectMRenderer();
            Check(renderer.Load(settings.Presets[0], 0, 0, true) == null, "Second projectM instance failed to load a preset.");
            var silent = new float[1024];
            Check(renderer.Render(96, 64, 0, silent).Length == 96 * 64 * 4, "Native initial dimensions failed.");
            Check(renderer.Render(160, 90, 1.0 / 30, silent).Length == 160 * 90 * 4, "Native resize failed.");
            Check(renderer.Load(settings.Presets[1], 0.1, 0.25, false) == null, "Smooth preset transition failed to start.");
            for (int f = 0; f < 12; f++) renderer.Render(160, 90, 0.1 + f / 30.0, silent);
            source.ProjectM = new ProjectMSettings { Presets = new() { "missing-smoke-preset.milk" } };
            source.ProjectMPlayback.Configure(source.ProjectM);
            bool missingFailedBake = false;
            try { CaptureSourceList(_sources, 3); }
            catch (InvalidOperationException ex) { missingFailedBake = ex.Message.Contains("No playable presets"); }
            Check(missingFailedBake, "A bake with no playable presets must report failure.");
            source.ProjectMPlayback.Configure(settings); source.ProjectMPlayback.Reset();
            CaptureSourceList(_sources, 4);
            Check(source.ProjectMPlayback.Status.StartsWith("Playing:"), "Preset retry failed to recover.");
            _isOfflineRendering = false;
            source.ProjectMPlayback.Configure(new ProjectMSettings { Presets = new() { "missing-smoke-preset.milk" } });
            CaptureSourceList(_sources, 5);
            Check(source.ProjectMPlayback.Status.StartsWith("No playable presets"), "Live missing presets must report an error without terminating playback.");
            var dialog = new ProjectMSettingsWindow(settings);
            dialog.PopulateLibraryForSmoke();
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new Size(1040, 700)); content.Arrange(new Rect(0, 0, 1040, 700)); content.UpdateLayout();
            var image = new RenderTargetBitmap(1040, 700, 96, 96, PixelFormats.Pbgra32); image.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(Path.Combine(folder, "playlist-controls.png"))) encoder.Save(file);
            dialog.Close();
        }
        finally
        {
            CleanupSource(group); _sources.Clear(); _isOfflineRendering = false; _audioBeatDetector.EndOfflineInput();
        }
    }

    private static void SaveProjectMSmokeImage(byte[] pixels, int width, int height, string path)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
