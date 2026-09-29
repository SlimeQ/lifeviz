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
            RunLiveProjectMNonBlockingSmoke(source, settings);
            source.ProjectMPlayback.Configure(new ProjectMSettings { Presets = new() { "missing-smoke-preset.milk" } });
            CaptureSourceList(_sources, 5);
            Check(source.ProjectMPlayback.Status.StartsWith("No playable presets"), "Live missing presets must report an error without terminating playback.");
            var dialog = new ProjectMSettingsWindow(settings, previewAudio: samples => Array.Fill(samples, 0.25f));
            dialog.PopulateLibraryForSmoke();
            dialog.RunPreviewSmoke();
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new Size(1240, 700)); content.Arrange(new Rect(0, 0, 1240, 700)); content.UpdateLayout();
            var image = new RenderTargetBitmap(1240, 700, 96, 96, PixelFormats.Pbgra32); image.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(Path.Combine(folder, "playlist-controls.png"))) encoder.Save(file);
            content.Measure(new Size(960, 590)); content.Arrange(new Rect(0, 0, 960, 590)); content.UpdateLayout();
            var compactImage = new RenderTargetBitmap(960, 590, 96, 96, PixelFormats.Pbgra32); compactImage.Render(content);
            var compactEncoder = new PngBitmapEncoder(); compactEncoder.Frames.Add(BitmapFrame.Create(compactImage));
            using (var file = File.Create(Path.Combine(folder, "playlist-controls-compact.png"))) compactEncoder.Save(file);
            dialog.ValidatePreviewTimerForSmoke();
            dialog.Close();
            dialog.ValidatePreviewClosedForSmoke();
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

    // Live projectM work (renderer creation, preset shader compiles, frames) runs on a render
    // thread. The frame loop must keep ticking quickly while presets load and switch.
    private void RunLiveProjectMNonBlockingSmoke(CaptureSource source, ProjectMSettings settings)
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var playback = source.ProjectMPlayback!;
        playback.Configure(settings); playback.Reset();
        double time = 20, worst = 0, worstGap = 0;
        long lastFreshToken = 0, lastFreshAt = 0;
        string lastPhase = "";
        long nextTick = System.Diagnostics.Stopwatch.GetTimestamp();
        bool Tick()
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            CaptureSourceList(_sources, time += 1.0 / 30);
            worst = Math.Max(worst, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (playback.FrameToken != lastFreshToken)
            {
                double gap = System.Diagnostics.Stopwatch.GetElapsedTime(lastFreshAt).TotalMilliseconds;
                if (lastFreshToken != 0 && gap > 120) Logger.Info($"projectM smoke gap {gap:F0} ms: {lastPhase} -> {playback.Phase}");
                if (lastFreshToken != 0) worstGap = Math.Max(worstGap, gap);
                lastFreshToken = playback.FrameToken; lastFreshAt = System.Diagnostics.Stopwatch.GetTimestamp();
                lastPhase = playback.Phase;
            }
            // Tick in real time at 30 fps like the app, so scene time and render-thread time agree.
            nextTick += System.Diagnostics.Stopwatch.Frequency / 30;
            long wait = nextTick - System.Diagnostics.Stopwatch.GetTimestamp();
            if (wait > 0) System.Threading.Thread.Sleep(TimeSpan.FromSeconds(wait / (double)System.Diagnostics.Stopwatch.Frequency));
            else nextTick = System.Diagnostics.Stopwatch.GetTimestamp();
            return true;
        }
        bool TickUntil(Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline) { Tick(); if (done()) return true; }
            return false;
        }
        Check(TickUntil(() => source.LastFrame != null && playback.Status.StartsWith("Playing:")),
            $"Live projectM never produced a frame: {playback.Status}");
        long token = playback.FrameToken;
        Check(TickUntil(() => playback.FrameToken > token + 5), "Live projectM stopped delivering frames.");
        Check(worst < 250, $"Live projectM blocked the frame loop for {worst:F0} ms while starting the renderer.");

        // Presets that draw on their own from a clean start (verified with silent audio) switch via a
        // standby renderer and crossfade; the layer must keep animating through each switch.
        string[] drawing =
        {
            "Fractal/Nested Circle/NeW Adam Master Mashup FX 2 Geiss - Reaction Diffusion 34 + Swelling Spiral  + Liquid Fire  + Geiss an28 --- Isosceles edit.milk",
            "Geometric/Honeycomb/suksma - yin - 360 - Organic circuits - hate shade.milk",
            "Particles/Points Trails/271 nz+ m19.milk"
        };
        // Only warps the image it inherits; from a clean renderer it stays black, so it switches in place.
        const string feedbackOnly = "Waveform/Spectrum/couldn't not.milk";
        foreach (string preset in drawing.Append(feedbackOnly))
            Check(ProjectMLibrary.Presets.Contains(preset), $"Smoke preset missing from the bundled library: {preset}");
        var switching = new ProjectMSettings { Presets = new(drawing) { feedbackOnly }, Order = "Ordered", Advance = "Hold", TransitionSeconds = 1 };
        playback.Configure(switching); playback.Reset();
        Check(TickUntil(() => playback.Status.Contains(drawing[0]) && playback.FrameToken > 0 && !playback.IsSwitchingPresets),
            $"Live projectM did not start the switching playlist: {playback.Status}");
        double worstCrossfadeGap = 0;
        for (int i = 1; i <= drawing.Length; i++)
        {
            // Let the current preset reach steady playback first; its own first frames are not the switch.
            long settled = playback.FrameToken;
            Check(TickUntil(() => playback.FrameToken > settled + 15), "Live projectM stopped delivering frames before a switch.");
            long before = playback.FrameToken, inPlaceBefore = playback.InPlaceSwitches, crossfadeBefore = playback.CrossfadeFrames;
            worstGap = 0;
            playback.Move(1, _audioBeatDetector.BeatCount);
            string expected = i < drawing.Length ? drawing[i] : feedbackOnly;
            Check(TickUntil(() => !playback.IsSwitchingPresets && playback.Status.Contains(expected) && playback.FrameToken > before + 10),
                $"Live projectM did not complete the switch to {expected}: {playback.Status}");
            if (i < drawing.Length)
            {
                Check(playback.InPlaceSwitches == inPlaceBefore && playback.CrossfadeFrames > crossfadeBefore + 2,
                    $"Switching to {expected} did not crossfade from a standby renderer.");
                worstCrossfadeGap = Math.Max(worstCrossfadeGap, worstGap);
            }
            else
            {
                // A warp-only preset either warps the reused standby's image or, if that is black,
                // switches in place with projectM's transition. Either way the layer must not go black.
                Check(source.LastFrame != null && ProjectMPlayback.MeanLuminance(source.LastFrame.Downscaled) >= 3,
                    $"The feedback-only preset left the layer black ({playback.InPlaceSwitches - inPlaceBefore} in-place switches).");
            }
        }
        Check(worst < 250, $"Live projectM blocked the frame loop for {worst:F0} ms while loading presets.");
        Check(worstCrossfadeGap < 250, $"The projectM layer froze for {worstCrossfadeGap:F0} ms during a crossfaded preset switch.");
        Logger.Info($"projectM live playback stayed off the UI thread: worst frame-loop tick {worst:F1} ms; crossfaded switches kept the layer moving (longest frame gap {worstCrossfadeGap:F0} ms, {playback.CrossfadeFrames} blended frames); feedback-only preset stayed visible ({playback.InPlaceSwitches} in-place switches).");
    }
}
