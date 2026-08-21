using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace lifeviz;

public partial class MainWindow
{
    private MenuItem BuildAddColorPlaneMenuItem(CaptureSource? parentGroup)
    {
        var item = new MenuItem { Header = "Add Color Plane...", Tag = parentGroup };
        item.Click += AddColorPlaneMenuItem_Click;
        return item;
    }

    private void AddColorPlaneMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var parentGroup = (sender as MenuItem)?.Tag as CaptureSource;
        var dialog = new TextInputDialog(
            "Add Color Plane",
            "Enter a solid color (#RRGGBB or R,G,B):",
            "#000000")
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!TryNormalizeHexColor(dialog.InputText, out string normalized))
        {
            MessageBox.Show(this, "Invalid color value. Use #RRGGBB or R,G,B.", "Color Plane",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AddColorPlaneSource(normalized, parentGroup?.Children ?? _sources);
    }

    private MenuItem BuildColorPlaneColorMenuItem(CaptureSource source)
    {
        var item = new MenuItem
        {
            Header = $"Color... ({FormatHexColor(source.ColorPlaneR, source.ColorPlaneG, source.ColorPlaneB)})"
        };
        item.Click += (_, _) =>
        {
            string current = FormatHexColor(source.ColorPlaneR, source.ColorPlaneG, source.ColorPlaneB);
            var dialog = new TextInputDialog(
                "Color Plane",
                "Enter a solid color (#RRGGBB or R,G,B):",
                current)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            if (!TryNormalizeHexColor(dialog.InputText, out string normalized) ||
                !TrySetColorPlaneColor(source, normalized))
            {
                MessageBox.Show(this, "Invalid color value. Use #RRGGBB or R,G,B.", "Color Plane",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RenderFrame();
            SaveConfig();
            RebuildSourcesMenu();
        };
        return item;
    }

    private void AddColorPlaneSource(string color, List<CaptureSource> targetList)
    {
        if (!TryParseHexColor(color, out byte r, out byte g, out byte b))
        {
            return;
        }

        targetList.Add(CaptureSource.CreateColorPlane(r, g, b));
        Logger.Info($"Inserted new color plane: {FormatHexColor(r, g, b)}.");
        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
        RebuildSourcesMenu();
    }

    internal void AddColorPlaneFromEditor(string color, Guid? parentId)
    {
        var targetList = ResolveTargetList(parentId);
        if (targetList == null)
        {
            return;
        }

        RunWithoutLayerEditorRefresh(() => AddColorPlaneSource(color, targetList));
    }

    internal void UpdateColorPlaneColor(Guid sourceId, string color)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source?.Type != CaptureSource.SourceType.ColorPlane || !TrySetColorPlaneColor(source, color))
            {
                return;
            }

            RenderFrame();
            SaveConfig();
            RebuildSourcesMenu();
        });
    }

    private static bool TrySetColorPlaneColor(CaptureSource source, string? color)
    {
        if (source.Type != CaptureSource.SourceType.ColorPlane ||
            !TryParseHexColor(color, out byte r, out byte g, out byte b))
        {
            return false;
        }

        source.SetColorPlaneColor(r, g, b);
        return true;
    }

    internal static bool TryNormalizeHexColor(string? value, out string normalized)
    {
        if (TryParseHexColor(value, out byte r, out byte g, out byte b))
        {
            normalized = FormatHexColor(r, g, b);
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    internal (bool ok, string detail) RunColorPlaneSmoke()
    {
        _sources.Clear();

        var plane = CaptureSource.CreateColorPlane(0x12, 0x34, 0x56);
        _sources.Add(plane);
        plane.LastFrame = null;
        bool removed = CaptureSourceList(_sources, animationTime: 0.0);
        bool captureOk = !removed && _sources.Count == 1 && plane.LastFrame != null;

        byte[]? cpuBuffer = null;
        var cpuComposite = _inlineSourceCompositor.BuildCompositeFrame(
            _sources,
            ref cpuBuffer,
            useEngineDimensions: true,
            animationTime: 0.0);
        bool cpuOk = IsSolidColorComposite(cpuComposite, 0x12, 0x34, 0x56);

        ResetGpuSourceCompositeSmokeCounters();
        byte[]? runtimeBuffer = null;
        var runtimeComposite = BuildCompositeFrame(
            _sources,
            ref runtimeBuffer,
            useEngineDimensions: true,
            animationTime: 0.0,
            includeCpuReadback: true);
        bool gpuPathOk = GetGpuSourceCompositePassCount() > 0;
        bool runtimeOk = gpuPathOk && IsSolidColorComposite(runtimeComposite, 0x12, 0x34, 0x56);

        bool updateOk = TrySetColorPlaneColor(plane, "#A1B2C3") &&
                        plane.ColorPlaneR == 0xA1 &&
                        plane.ColorPlaneG == 0xB2 &&
                        plane.ColorPlaneB == 0xC3 &&
                        !TrySetColorPlaneColor(plane, "not-a-color") &&
                        plane.ColorPlaneR == 0xA1 &&
                        plane.ColorPlaneG == 0xB2 &&
                        plane.ColorPlaneB == 0xC3;

        var portrait = CaptureSource.CreateFile("portrait-smoke.png", "Portrait", 100, 200);
        var neutralGroup = CaptureSource.CreateGroup("Neutral");
        neutralGroup.Children.Add(CaptureSource.CreateColorPlane(0, 0, 0));
        var mixedGroup = CaptureSource.CreateGroup("Mixed");
        mixedGroup.Children.Add(CaptureSource.CreateColorPlane(0, 0, 0));
        mixedGroup.Children.Add(portrait);
        bool aspectOk = neutralGroup.IsAspectNeutral &&
                        !mixedGroup.IsAspectNeutral &&
                        Math.Abs(mixedGroup.AspectRatio - 0.5) < 0.0001 &&
                        Math.Abs(ResolveSourceStackAspectRatio(
                            new List<CaptureSource> { CaptureSource.CreateColorPlane(0, 0, 0), portrait },
                            DefaultAspectRatio) - 0.5) < 0.0001 &&
                        Math.Abs(ResolveSourceStackAspectRatio(
                            new List<CaptureSource> { CaptureSource.CreateColorPlane(0, 0, 0) },
                            DefaultAspectRatio) - DefaultAspectRatio) < 0.0001;

        _sources.Clear();
        _sources.Add(portrait);
        _sources.Add(plane);
        MakePrimarySource(plane);
        aspectOk &= ReferenceEquals(_sources[0], portrait);

        var editorGroup = new LayerEditorSource
        {
            Id = Guid.NewGuid(),
            Kind = LayerEditorSourceKind.Group,
            DisplayName = "Color Group"
        };
        editorGroup.Children.Add(new LayerEditorSource
        {
            Id = Guid.NewGuid(),
            Kind = LayerEditorSourceKind.ColorPlane,
            DisplayName = "Color Plane",
            ColorHex = "#A1B2C3",
            BlendMode = "Normal",
            FitMode = "Stretch",
            Opacity = 1.0,
            Scale = 1.0,
            Parent = editorGroup
        });
        var layerConfig = LayerConfigFile.FromEditorSources(
            new[] { editorGroup },
            Array.Empty<LayerEditorSimulationLayer>(),
            GetProjectSettingsForEditor());
        var layerRoundTrip = layerConfig.ToEditorSources();
        var roundTripPlane = layerRoundTrip
            .SelectMany(source => source.IsGroup
                ? source.Children.AsEnumerable()
                : Enumerable.Repeat(source, 1))
            .FirstOrDefault(source => source.IsColorPlane);
        bool layerConfigOk = layerConfig.Version == 11 &&
                             roundTripPlane?.ColorHex == "#A1B2C3" &&
                             roundTripPlane.BlendMode == "Normal" &&
                             roundTripPlane.FitMode == "Stretch";

        var runtimeGroup = CaptureSource.CreateGroup("Color Group");
        runtimeGroup.Children.Add(CaptureSource.CreateColorPlane(0xA1, 0xB2, 0xC3));
        _sources.Clear();
        _sources.Add(runtimeGroup);
        var appConfig = BuildSourceConfigs();
        _sources.Clear();
        RestoreSourceList(
            appConfig,
            _sources,
            Array.Empty<WindowHandleInfo>(),
            Array.Empty<WebcamCaptureService.CameraInfo>());
        var restoredPlane = _sources.SingleOrDefault()?.Children.SingleOrDefault();
        bool appConfigOk = appConfig.Count == 1 &&
                           appConfig[0].Children.Count == 1 &&
                           appConfig[0].Children[0].Color == "#A1B2C3" &&
                           restoredPlane?.Type == CaptureSource.SourceType.ColorPlane &&
                           restoredPlane.ColorPlaneR == 0xA1 &&
                           restoredPlane.ColorPlaneG == 0xB2 &&
                           restoredPlane.ColorPlaneB == 0xC3;

        bool groupRemoved = CaptureSourceList(_sources, animationTime: 0.0);
        var groupFrame = _sources.SingleOrDefault()?.LastFrame;
        bool groupRenderOk = !groupRemoved && groupFrame != null && IsSolidColorComposite(
            new CompositeFrame(groupFrame.Downscaled, groupFrame.DownscaledWidth, groupFrame.DownscaledHeight),
            0xA1,
            0xB2,
            0xC3);

        var deferredPlane = new LayerEditorSource
        {
            Id = Guid.NewGuid(),
            Kind = LayerEditorSourceKind.ColorPlane,
            DisplayName = "Deferred Plane",
            ColorHex = "#0A0B0C",
            BlendMode = "Normal",
            FitMode = "Stretch",
            Opacity = 1.0,
            Scale = 1.0
        };
        ApplyLayerEditorSources(new[] { deferredPlane });
        var appliedPlane = _sources.SingleOrDefault();
        bool deferredApplyOk = appliedPlane?.Type == CaptureSource.SourceType.ColorPlane &&
                               appliedPlane.ColorPlaneR == 0x0A &&
                               appliedPlane.ColorPlaneG == 0x0B &&
                               appliedPlane.ColorPlaneB == 0x0C;

        _sources.Clear();
        bool ok = captureOk && cpuOk && runtimeOk && updateOk && aspectOk && layerConfigOk &&
                  appConfigOk && groupRenderOk && deferredApplyOk;
        string detail = $"capture={captureOk}, cpu={cpuOk}, gpu={gpuPathOk}, runtime={runtimeOk}, update={updateOk}, " +
                        $"aspect={aspectOk}, layerConfig={layerConfigOk}, appConfig={appConfigOk}, " +
                        $"group={groupRenderOk}, deferredApply={deferredApplyOk}";
        Logger.Info($"Color-plane smoke: {detail}.");
        return (ok, detail);
    }

    private static bool IsSolidColorComposite(CompositeFrame? frame, byte r, byte g, byte b)
    {
        if (frame == null || frame.DownscaledWidth <= 0 || frame.DownscaledHeight <= 0)
        {
            return false;
        }

        int required = frame.DownscaledWidth * frame.DownscaledHeight * 4;
        if (frame.Downscaled.Length < required)
        {
            return false;
        }

        int[] samples = { 0, (required / 2) & ~3, required - 4 };
        return samples.All(index =>
            frame.Downscaled[index] == b &&
            frame.Downscaled[index + 1] == g &&
            frame.Downscaled[index + 2] == r &&
            frame.Downscaled[index + 3] == 255);
    }
}
