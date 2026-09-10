using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace lifeviz;

public partial class MainWindow
{
    internal bool RunAutoClipTakeoverSmoke(string videoPath)
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        videoPath = Path.GetFullPath(videoPath);
        string secondPath = Path.Combine(Path.GetTempPath(), $"lifeviz-takeover-{Guid.NewGuid():N}{Path.GetExtension(videoPath)}");
        File.Copy(videoPath, secondPath);
        try
        {
            using var session = new FileCaptureService.AutoClipSession(new[] { videoPath, secondPath }, 0.2, 0.2, 0.2, 0.2);
            session.SetLoopSelectedFile(true); // Whole-file mode must override timed looping.
            session.SetPlaybackOptions(startWithDelay: true, playInOrder: true, playWholeFile: true);
            session.SetOfflineRenderMode(true, 30);

            var background = CaptureSource.CreateColorPlane(10, 20, 30, "Fractal stand-in");
            var movie = CaptureSource.CreateColorPlane(90, 0, 0, "Movie stand-in");
            movie.BlendMode = BlendMode.Additive;
            movie.VisibilityGroup = "show";
            var shortClip = CaptureSource.CreateColorPlane(0, 80, 0, "Short clip stand-in");
            shortClip.VisibilityGroup = "SHOW";
            shortClip.Opacity = 0.5;
            var longClip = CaptureSource.CreateAutoClip(session, 0.2, 0.2, 0.2, 0.2);
            longClip.VisibilityGroup = "show";
            longClip.AutoClipTakeover = true;
            longClip.AutoClipStartWithDelay = true;
            longClip.AutoClipPlayInOrder = true;
            longClip.AutoClipPlayWholeFile = true;
            longClip.AutoClipFadeSeconds = 0.1;
            longClip.BlendMode = BlendMode.Normal;
            longClip.FitMode = FitMode.Stretch;
            var logo = CaptureSource.CreateColorPlane(0, 0, 40, "Logo stand-in");
            logo.Opacity = 0.25;
            var stack = new List<CaptureSource> { background, movie, shortClip, longClip, logo };
            var cpu = new CpuSourceCompositor(this);
            var phases = new List<string>();
            bool sawPeak = false, sawFade = false, sawReturn = false;
            double largestRemaining = 0;
            byte[]? firstGap = null;
            byte[]? cpuBuffer = null, gpuBuffer = null;
            int gpuPassesBefore = GetGpuSourceCompositePassCount();

            // The fixture is a one-second video: sample five complete clip/gap cycles.
            for (int tick = 0; tick < 190; tick++)
            {
                var decoded = session.CaptureFrame(16, 16, FitMode.Stretch, includeSource: false);
                if (decoded.HasValue)
                {
                    // A fully transparent normal overlay must still take over: the
                    // untouched background, not the movie, shows through its holes.
                    longClip.LastFrame = new SourceFrame(new byte[16 * 16 * 4], 16, 16, null, 16, 16);
                }
                else longClip.LastFrame = null;

                if (decoded.HasValue && (phases.Count == 0 || phases[^1] != session.CurrentFramePath))
                    phases.Add(session.CurrentFramePath!);
                largestRemaining = Math.Max(largestRemaining, session.GetCurrentPhaseRemainingForSmoke() ?? 0);
                UpdateVisibilityGroups(stack);
                double envelope = session.GetVisualOpacity(longClip.AutoClipFadeSeconds);
                Require(Math.Abs(movie.VisibilityOpacity - (1 - envelope)) < 0.00001, "Movie must follow the inverse clip envelope.");
                Require(movie.VisibilityOpacity == shortClip.VisibilityOpacity, "Case-insensitive group members must switch together.");
                Require(background.VisibilityOpacity == 1 && logo.VisibilityOpacity == 1, "Independent background/logo were suppressed.");
                Require(longClip.VisibilityOpacity == 1, "Takeover must not suppress itself.");
                Require(movie.Enabled && shortClip.Enabled, "Takeover must not disable playback.");

                var cpuFrame = cpu.BuildCompositeFrame(stack, ref cpuBuffer, useEngineDimensions: false, animationTime: 0);
                var gpuFrame = BuildCompositeFrame(stack, ref gpuBuffer, useEngineDimensions: false, animationTime: 0, includeCpuReadback: true);
                Require(cpuFrame != null && gpuFrame != null, "Missing composite.");
                int center = ((cpuFrame!.DownscaledHeight / 2) * cpuFrame.DownscaledWidth + cpuFrame.DownscaledWidth / 2) * 4;
                int gpuCenter = ((gpuFrame!.DownscaledHeight / 2) * gpuFrame.DownscaledWidth + gpuFrame.DownscaledWidth / 2) * 4;
                for (int channel = 0; channel < 4; channel++)
                    Require(Math.Abs(cpuFrame.Downscaled[center + channel] - gpuFrame.Downscaled[gpuCenter + channel]) <= 2, "CPU/GPU takeover pixels disagree.");

                if (tick < 6) Require(envelope == 0 && !decoded.HasValue, "Initial delay did not hold the first clip.");
                if (envelope == 0 && firstGap == null) firstGap = cpuFrame.Downscaled.Skip(center).Take(4).ToArray();
                if (envelope >= 0.999)
                {
                    sawPeak = true;
                    // Transparent takeover + independent logo over the original background.
                    Require(cpuFrame.Downscaled[center + 2] <= 8 && cpuFrame.Downscaled[center + 1] <= 16, "Movie/short clip leaked through takeover transparency.");

                    longClip.Enabled = false;
                    UpdateVisibilityGroups(stack);
                    Require(movie.VisibilityOpacity == 1, "Disabling takeover must restore fallback.");
                    longClip.Enabled = true;
                    longClip.HasError = true;
                    UpdateVisibilityGroups(stack);
                    Require(movie.VisibilityOpacity == 1, "Failed takeover must restore fallback.");
                    longClip.HasError = false;
                    var isolated = new List<CaptureSource> { movie };
                    UpdateVisibilityGroups(isolated);
                    Require(movie.VisibilityOpacity == 1, "Visibility groups must stay within their sibling stack.");
                }
                sawFade |= envelope > 0.01 && envelope < 0.99;
                if (sawPeak && envelope == 0)
                {
                    sawReturn = true;
                    Require(firstGap!.SequenceEqual(cpuFrame.Downscaled.Skip(center).Take(4)), "Fallback did not return to its authored compositing.");
                }
            }
            Require(sawPeak && sawFade && sawReturn, "Did not observe fade, takeover, and return.");
            Require(largestRemaining >= 0.9, "Whole-file mode used the 0.2-second clip window.");
            Require(phases.Count >= 4 && phases.Select((path, i) => path == (i % 2 == 0 ? videoPath : secondPath)).All(value => value), "List order did not cycle through both files.");
            Require(GetGpuSourceCompositePassCount() > gpuPassesBefore, "GPU validation fell back to CPU.");

            var serialized = JsonSerializer.Serialize(BuildSourceConfigs(stack));
            var saved = JsonSerializer.Deserialize<List<AppConfig.SourceConfig>>(serialized)!;
            var restored = CaptureSource.CreateAutoClip(session, 0.2, 0.2, 0.2, 0.2);
            ApplySourceSettings(restored, saved[3]);
            Require(restored.VisibilityGroup == "show" && restored.AutoClipTakeover && restored.AutoClipStartWithDelay && restored.AutoClipPlayInOrder && restored.AutoClipPlayWholeFile, "App config lost playback/visibility options.");
            var editorModels = BuildLayerEditorSources(stack, null);
            var file = LayerConfigFile.FromEditorSources(editorModels, Array.Empty<LayerEditorSimulationLayer>(), new LayerEditorProjectSettings());
            var roundTrip = JsonSerializer.Deserialize<LayerConfigFile>(JsonSerializer.Serialize(file))!.ToEditorSources();
            var model = roundTrip[3];
            Require(model.VisibilityGroup == "show" && model.AutoClipTakeover && model.AutoClipStartWithDelay && model.AutoClipPlayInOrder && model.AutoClipPlayWholeFile, "Scene export/import lost playback/visibility options.");
            ApplySourceModel(restored, model);
            Require(restored.AutoClipTakeover && restored.AutoClipPlayInOrder && restored.AutoClipPlayWholeFile, "Editor apply lost playback options.");
            var editor = new LayerEditorWindow(this);
            try
            {
                editor.ValidateAutoClipControlsForSmoke(model, Path.Combine(Path.GetDirectoryName(videoPath)!, "takeover-editor.png"));
            }
            finally { editor.Close(); }
            var legacy = JsonSerializer.Deserialize<LayerConfigFile>("{\"Sources\":[{\"Type\":\"AutoClip\"}]}")!.ToEditorSources()[0];
            Require(!legacy.AutoClipTakeover && !legacy.AutoClipStartWithDelay && !legacy.AutoClipPlayInOrder && !legacy.AutoClipPlayWholeFile && legacy.VisibilityGroup == "", "Legacy defaults changed.");
            Logger.Info($"AutoClip takeover smoke passed: cycles={phases.Count}, wholeFileSeconds={largestRemaining:0.00}, CPU/GPU, transparent holes, persistence, isolation, initial gap, fades and restoration.");
            return true;
        }
        finally
        {
            File.Delete(secondPath);
        }
    }
}
