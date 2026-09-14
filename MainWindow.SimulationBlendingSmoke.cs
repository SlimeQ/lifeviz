using System;
using System.Collections.Generic;
using System.Linq;

namespace lifeviz;

public partial class MainWindow
{
    internal bool RunSimulationBlendingSmoke()
    {
        if (!_renderLoopAttached) InitializeVisualizer();
        _sources.Clear();
        ClearSimulationLayers();
        _effectiveLifeOpacity = _lifeOpacity = 1;
        _isPaused = false;
        ApplyDimensions(144, 24, DefaultAspectRatio, persist: false);
        int width = GetReferenceSimulationEngine().Columns;
        int height = GetReferenceSimulationEngine().Rows;
        byte[] lowerPixels = BuildSmokeSolidBgra(width, height, 40, 80, 120);
        var lower = CaptureSource.CreateFile("blend-fixture", "Lower stack", width, height);
        lower.BlendMode = BlendMode.Normal;
        lower.LastFrame = new SourceFrame(lowerPixels, width, height, null, width, height);
        var group = CaptureSource.CreateSimulationGroup("Blend fixture");
        group.BlendMode = BlendMode.Normal;
        group.FitMode = FitMode.Stretch;
        SimulationLayerSpec Sim(string name, int cell = 1) => new()
        {
            Id = Guid.NewGuid(), Kind = LayerEditorSimulationItemKind.Layer,
            LayerType = SimulationLayerType.PixelSort, Name = name, Enabled = true,
            BlendMode = BlendMode.Normal, LifeOpacity = 1,
            PixelSortCellWidth = cell, PixelSortCellHeight = 1
        };
        group.SimulationLayers.Add(Sim("Sorted", 8));
        group.SimulationLayers.Add(Sim("Unmodified"));
        _sources.Add(lower);
        _sources.Add(group);

        void ResetEngines()
        {
            ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
            ConfigureSimulationLayerEngines(_configuredRows, _configuredDepth, _currentAspectRatio, randomize: false);
            foreach (var leaf in EnumerateSimulationLeafLayers(_simulationLayers)) leaf.TimeSinceLastStep = 1;
            _isPaused = false;
        }
        byte[] Render(bool gpu, int steps = 0)
        {
            byte[]? buffer = null;
            bool injected = false;
            int stepped = 0;
            var frame = gpu
                ? BuildInlineCompositeFrameGpu(_sources, ref buffer, true, 0, steps,
                    ref injected, ref stepped, includeCpuReadback: true)
                : BuildInlineCompositeFrameCpu(_sources, ref buffer, true, 0, steps, ref injected, ref stepped);
            if (frame == null || (gpu && frame.GpuSurface == null))
                throw new InvalidOperationException("Simulation blend fixture requires a real composite.");
            return frame.Downscaled.ToArray();
        }
        void Equal(byte[] actual, byte[] expected, string phase, int tolerance = 2)
        {
            if (actual.Length != expected.Length) throw new InvalidOperationException(phase + " length mismatch");
            for (int i = 0; i < actual.Length; i++)
                if (Math.Abs(actual[i] - expected[i]) > tolerance)
                    throw new InvalidOperationException($"{phase}: byte {i}, expected {expected[i]}, got {actual[i]}.");
        }
        byte[] Read(SimulationLayerState layer)
        {
            var pixels = new byte[width * height * 4];
            layer.Engine!.FillColorBuffer(pixels);
            return pixels; // Pixel Sort readback is BGRA.
        }
        void Seed(SimulationLayerState layer, byte[] pixels)
        {
            var input = _gpuSimulationGroupCompositor.UploadInputSurface(pixels, width, height)!;
            if (!TryInjectLayerFromGpuSurface(layer, input)) throw new InvalidOperationException("Seed injection failed.");
            layer.Engine!.Step();
        }
        // A sorted first sibling must not become the input to the second sibling.
        for (int i = 0; i < lowerPixels.Length; i += 4)
            lowerPixels[i] = lowerPixels[i + 1] = lowerPixels[i + 2] = (byte)(240 - ((i / 4) % 8) * 25);
        foreach (bool gpu in new[] { true, false })
        {
            ResetEngines();
            Render(gpu, 1);
            var leaves = FindSimulationNode(group.Id)!.Children;
            Equal(Read(leaves[1]), lowerPixels, $"Shared input GPU={gpu}");
            if (Read(leaves[0]).AsSpan().SequenceEqual(lowerPixels))
                throw new InvalidOperationException("Shared-input fixture must change the first simulation output.");
        }

        lowerPixels = BuildSmokeSolidBgra(width, height, 40, 80, 120);
        lower.LastFrame = new SourceFrame(lowerPixels, width, height, null, width, height);
        group.SimulationLayers[0] = Sim("First");
        ResetEngines();
        _isPaused = true;
        var children = FindSimulationNode(group.Id)!.Children;
        byte[] first = BuildSmokeSolidBgra(width, height, 60, 90, 120);
        byte[] second = BuildSmokeSolidBgra(width, height, 20, 60, 100);
        Seed(children[0], first);
        Seed(children[1], second);
        double Blend(double d, double v, BlendMode mode) => mode switch
        {
            BlendMode.Additive => d + v,
            BlendMode.Subtractive => d - v,
            BlendMode.Multiply => d * v / 255,
            BlendMode.Screen => 255 - (255 - d) * (255 - v) / 255,
            BlendMode.Overlay => d < 128 ? 2 * d * v / 255 : 255 - 2 * (255 - d) * (255 - v) / 255,
            BlendMode.Lighten => Math.Max(d, v),
            BlendMode.Darken => Math.Min(d, v),
            _ => v
        };
        foreach (var simMode in Enum.GetValues<BlendMode>())
        foreach (var groupMode in Enum.GetValues<BlendMode>())
        {
            children[1].BlendMode = simMode;
            children[1].EffectiveLifeOpacity = 0.6;
            group.BlendMode = groupMode;
            group.Opacity = 0.5;
            byte[] expected = new byte[first.Length];
            for (int i = 0; i < expected.Length; i += 4)
            {
                for (int c = 0; c < 3; c++)
                {
                    double resolved = Math.Clamp(first[c] + (Blend(first[c], second[c], simMode) - first[c]) * 0.6, 0, 255);
                    expected[i + c] = (byte)Math.Round(Math.Clamp(lowerPixels[c] + (Blend(lowerPixels[c], resolved, groupMode) - lowerPixels[c]) * 0.5, 0, 255));
                }
                expected[i + 3] = 255;
            }
            Equal(Render(true), expected, $"GPU {simMode}/{groupMode}");
            Equal(Render(false), expected, $"CPU {simMode}/{groupMode}");
        }
        // Clamping must happen after each operation; add/sub cannot be regrouped.
        group.Opacity = 1;
        group.BlendMode = BlendMode.Normal;
        children[1].EffectiveLifeOpacity = 1;
        children[1].BlendMode = BlendMode.Subtractive;
        byte[] forward = Render(true);
        children.Reverse();
        byte[] reverse = Render(true);
        if (forward.AsSpan().SequenceEqual(reverse)) throw new InvalidOperationException("Simulation order was ignored.");
        Equal(Render(false), reverse, "Reordered CPU/GPU");
        children.Reverse();

        // Saturate between operations: (200 + 100) - 100 = 155,
        // while (200 - 100) + 100 = 200.
        group.SimulationLayers.Add(Sim("Third"));
        ResetEngines();
        _isPaused = true;
        children = FindSimulationNode(group.Id)!.Children;
        Seed(children[0], BuildSmokeSolidBgra(width, height, 200, 200, 200));
        Seed(children[1], BuildSmokeSolidBgra(width, height, 100, 100, 100));
        Seed(children[2], BuildSmokeSolidBgra(width, height, 100, 100, 100));
        children[1].BlendMode = BlendMode.Additive;
        children[2].BlendMode = BlendMode.Subtractive;
        foreach (bool gpu in new[] { true, false })
            Equal(Render(gpu), BuildSmokeSolidBgra(width, height, 155, 155, 155), "Ordered clamp");
        (children[1], children[2]) = (children[2], children[1]);
        foreach (bool gpu in new[] { true, false })
            Equal(Render(gpu), BuildSmokeSolidBgra(width, height, 200, 200, 200), "Reordered clamp");
        group.SimulationLayers.RemoveAt(2);

        // Top group's 1x1 simulation must read the fully blended lower group,
        // including that group's opacity, on every new frame.
        var top = CaptureSource.CreateSimulationGroup("Top group");
        top.BlendMode = BlendMode.Normal;
        top.SimulationLayers.Add(Sim("Read lower stack"));
        _sources.Add(top);
        foreach (bool gpu in new[] { true, false })
        {
            ResetEngines();
            group.BlendMode = BlendMode.Multiply;
            group.Opacity = 0.5;
            for (int frame = 0; frame < 3; frame++)
            {
                lowerPixels = BuildSmokeSolidBgra(width, height, (byte)(30 + frame * 30), 80, 120);
                lower.LastFrame = new SourceFrame(lowerPixels, width, height, null, width, height);
                foreach (var leaf in EnumerateSimulationLeafLayers(_simulationLayers)) leaf.TimeSinceLastStep = 1;
                byte[] result = Render(gpu, 1);
                byte[] expected = lowerPixels.ToArray();
                for (int i = 0; i < expected.Length; i += 4)
                    for (int c = 0; c < 3; c++)
                        expected[i + c] = (byte)Math.Round((lowerPixels[i + c] + lowerPixels[i + c] * lowerPixels[i + c] / 255.0) * 0.5);
                Equal(result, expected, $"Stacked groups GPU={gpu} frame={frame}");
                Equal(Read(FindSimulationNode(top.Id)!.Children[0]), expected, "Top group input");
            }
        }
        // More than eight outputs uses local CPU resolution without reinjecting
        // earlier groups. Empty and disabled groups remain stack no-ops.
        _sources.Remove(top);
        group.BlendMode = BlendMode.Normal;
        group.Opacity = 1;
        while (group.SimulationLayers.Count < 9) group.SimulationLayers.Add(Sim("Extra"));
        ResetEngines();
        Equal(Render(true, 1), lowerPixels, "Nine outputs");
        Equal(Render(false), lowerPixels, "Nine outputs CPU");
        group.SimulationLayers.Clear();
        ResetEngines();
        Equal(Render(true, 1), lowerPixels, "Empty group");
        group.Enabled = false;
        Equal(Render(false), lowerPixels, "Disabled group");
        group.Enabled = true;
        group.SimulationLayers.Add(Sim("Persisted simulation"));
        ResetEngines();
        var editor = new LayerEditorWindow(this);
        try { editor.VerifySimulationBlendControls(group.Id); }
        finally { editor.Close(); }
        var saved = LayerConfigFile.FromEditorSources(BuildLayerEditorSources(),
            Array.Empty<LayerEditorSimulationLayer>(), GetProjectSettingsForEditor());
        var restored = LayerConfigFile.Parse(System.Text.Json.JsonSerializer.Serialize(saved)).ToEditorSources().Last();
        if (restored.BlendMode != "Screen" || Math.Abs(restored.Opacity - 0.4) > 0.001 ||
            restored.SimulationLayers[0].BlendMode != "Normal")
            throw new InvalidOperationException("Independent group/simulation settings failed scene roundtrip.");
        Logger.Info("Simulation blending smoke passed: shared CPU/GPU input, all 64 blend combinations, ordering, opacity, stacked groups, nine outputs, empty/disabled groups.");
        return true;
    }
}
