using System;
using System.Collections.Generic;
using System.Linq;

namespace lifeviz;

internal sealed class LayerConfigFile
{
    public int Version { get; set; } = 4;
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    public LayerConfigProjectSettings ProjectSettings { get; set; } = new();
    public List<LayerConfigSimulationLayer> SimulationLayers { get; set; } = new();
    public bool PositiveLayerEnabled { get; set; } = true;
    public string PositiveLayerBlendMode { get; set; } = "Additive";
    public bool NegativeLayerEnabled { get; set; } = true;
    public string NegativeLayerBlendMode { get; set; } = "Subtractive";
    public List<string> SimulationLayerOrder { get; set; } = new() { "Positive", "Negative" };
    public List<LayerConfigSource> Sources { get; set; } = new();

    public static LayerConfigFile FromEditorSources(
        IEnumerable<LayerEditorSource> sources,
        IEnumerable<LayerEditorSimulationLayer> simulationLayers,
        LayerEditorProjectSettings projectSettings)
    {
        var file = new LayerConfigFile
        {
            ProjectSettings = new LayerConfigProjectSettings
            {
                Height = Math.Max(1, projectSettings.Height),
                Depth = Math.Max(1, projectSettings.Depth),
                Framerate = projectSettings.Framerate,
                LifeMode = string.IsNullOrWhiteSpace(projectSettings.LifeMode) ? "NaiveGrayscale" : projectSettings.LifeMode,
                BinningMode = string.IsNullOrWhiteSpace(projectSettings.BinningMode) ? "Fill" : projectSettings.BinningMode,
                InjectionMode = string.IsNullOrWhiteSpace(projectSettings.InjectionMode) ? "Threshold" : projectSettings.InjectionMode,
                InjectionNoise = Math.Clamp(projectSettings.InjectionNoise, 0, 1),
                LifeOpacity = Math.Clamp(projectSettings.LifeOpacity, 0, 1),
                RgbHueShiftDegrees = projectSettings.RgbHueShiftDegrees,
                RgbHueShiftSpeedDegreesPerSecond = projectSettings.RgbHueShiftSpeedDegreesPerSecond,
                InvertComposite = projectSettings.InvertComposite,
                Passthrough = projectSettings.Passthrough,
                CompositeBlendMode = string.IsNullOrWhiteSpace(projectSettings.CompositeBlendMode) ? "Additive" : projectSettings.CompositeBlendMode
            },
            SimulationLayers = simulationLayers.Select(layer => new LayerConfigSimulationLayer
            {
                Id = layer.Id,
                Name = string.IsNullOrWhiteSpace(layer.Name) ? "Simulation Layer" : layer.Name,
                Enabled = layer.Enabled,
                InputFunction = string.IsNullOrWhiteSpace(layer.InputFunction) ? "Direct" : layer.InputFunction,
                BlendMode = string.IsNullOrWhiteSpace(layer.BlendMode) ? "Subtractive" : layer.BlendMode,
                InjectionMode = string.IsNullOrWhiteSpace(layer.InjectionMode) ? "Threshold" : layer.InjectionMode,
                ThresholdMin = Math.Clamp(layer.ThresholdMin, 0, 1),
                ThresholdMax = Math.Clamp(layer.ThresholdMax, 0, 1),
                InvertThreshold = layer.InvertThreshold
            }).ToList()
        };
        if (file.SimulationLayers.Count == 0)
        {
            file.SimulationLayers = new List<LayerConfigSimulationLayer>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Positive",
                    Enabled = true,
                    InputFunction = "Direct",
                    BlendMode = "Additive",
                    InjectionMode = "Threshold",
                    ThresholdMin = 0.35,
                    ThresholdMax = 0.75,
                    InvertThreshold = false
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Negative",
                    Enabled = true,
                    InputFunction = "Inverse",
                    BlendMode = "Subtractive",
                    InjectionMode = "Threshold",
                    ThresholdMin = 0.35,
                    ThresholdMax = 0.75,
                    InvertThreshold = false
                }
            };
        }
        foreach (var source in sources)
        {
            file.Sources.Add(FromEditorSource(source));
        }
        return file;
    }

    public List<LayerEditorSource> ToEditorSources()
    {
        var list = new List<LayerEditorSource>(Sources.Count);
        foreach (var source in Sources)
        {
            list.Add(ToEditorSource(source, null));
        }
        return list;
    }

    public List<LayerEditorSimulationLayer> ToEditorSimulationLayers()
    {
        if (SimulationLayers != null && SimulationLayers.Count > 0)
        {
            return SimulationLayers.Select(layer => new LayerEditorSimulationLayer
            {
                Id = layer.Id == Guid.Empty ? Guid.NewGuid() : layer.Id,
                Name = string.IsNullOrWhiteSpace(layer.Name) ? "Simulation Layer" : layer.Name,
                Enabled = layer.Enabled,
                InputFunction = string.IsNullOrWhiteSpace(layer.InputFunction) ? "Direct" : layer.InputFunction,
                BlendMode = string.IsNullOrWhiteSpace(layer.BlendMode) ? "Subtractive" : layer.BlendMode,
                InjectionMode = string.IsNullOrWhiteSpace(layer.InjectionMode) ? "Threshold" : layer.InjectionMode,
                ThresholdMin = Math.Clamp(layer.ThresholdMin, 0, 1),
                ThresholdMax = Math.Clamp(layer.ThresholdMax, 0, 1),
                InvertThreshold = layer.InvertThreshold
            }).ToList();
        }

        return BuildLegacyEditorSimulationLayers();
    }

    public LayerEditorProjectSettings ToEditorProjectSettings()
    {
        var settings = ProjectSettings ?? new LayerConfigProjectSettings();
        return new LayerEditorProjectSettings
        {
            Height = Math.Max(1, settings.Height),
            Depth = Math.Max(1, settings.Depth),
            Framerate = settings.Framerate,
            LifeMode = string.IsNullOrWhiteSpace(settings.LifeMode) ? "NaiveGrayscale" : settings.LifeMode,
            BinningMode = string.IsNullOrWhiteSpace(settings.BinningMode) ? "Fill" : settings.BinningMode,
            InjectionMode = string.IsNullOrWhiteSpace(settings.InjectionMode) ? "Threshold" : settings.InjectionMode,
            InjectionNoise = Math.Clamp(settings.InjectionNoise, 0, 1),
            LifeOpacity = Math.Clamp(settings.LifeOpacity, 0, 1),
            RgbHueShiftDegrees = settings.RgbHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = settings.RgbHueShiftSpeedDegreesPerSecond,
            InvertComposite = settings.InvertComposite,
            Passthrough = settings.Passthrough,
            CompositeBlendMode = string.IsNullOrWhiteSpace(settings.CompositeBlendMode) ? "Additive" : settings.CompositeBlendMode
        };
    }

    private static LayerConfigSource FromEditorSource(LayerEditorSource source)
    {
        var config = new LayerConfigSource
        {
            Type = source.Kind.ToString(),
            WindowTitle = source.WindowTitle,
            WebcamId = source.WebcamId,
            FilePath = source.FilePath,
            DisplayName = source.DisplayName,
            BlendMode = source.BlendMode,
            FitMode = source.FitMode,
            Opacity = source.Opacity,
            VideoAudioEnabled = source.VideoAudioEnabled,
            VideoAudioVolume = source.VideoAudioVolume,
            Mirror = source.Mirror,
            KeyEnabled = source.KeyEnabled,
            KeyColor = source.KeyColorHex,
            KeyTolerance = source.KeyTolerance
        };

        if (source.FilePaths.Count > 0)
        {
            config.FilePaths = new List<string>(source.FilePaths);
        }

        foreach (var animation in source.Animations)
        {
            config.Animations.Add(new LayerConfigAnimation
            {
                Type = animation.Type,
                Loop = animation.Loop,
                Speed = animation.Speed,
                TranslateDirection = animation.TranslateDirection,
                RotationDirection = animation.RotationDirection,
                RotationDegrees = animation.RotationDegrees,
                DvdScale = animation.DvdScale,
                BeatShakeIntensity = animation.BeatShakeIntensity,
                AudioGranularLowGain = animation.AudioGranularLowGain,
                AudioGranularMidGain = animation.AudioGranularMidGain,
                AudioGranularHighGain = animation.AudioGranularHighGain,
                BeatsPerCycle = animation.BeatsPerCycle
            });
        }

        foreach (var child in source.Children)
        {
            config.Children.Add(FromEditorSource(child));
        }

        return config;
    }

    private static LayerEditorSource ToEditorSource(LayerConfigSource config, LayerEditorSource? parent)
    {
        var kind = ResolveKind(config);
        var model = new LayerEditorSource
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            DisplayName = config.DisplayName ?? string.Empty,
            WindowTitle = config.WindowTitle,
            WebcamId = config.WebcamId,
            FilePath = config.FilePath,
            BlendMode = string.IsNullOrWhiteSpace(config.BlendMode) ? "Additive" : config.BlendMode,
            FitMode = string.IsNullOrWhiteSpace(config.FitMode) ? "Fill" : config.FitMode,
            Opacity = Math.Clamp(config.Opacity, 0, 1),
            VideoAudioEnabled = config.VideoAudioEnabled,
            VideoAudioVolume = Math.Clamp(config.VideoAudioVolume, 0, 1),
            Mirror = config.Mirror,
            KeyEnabled = config.KeyEnabled,
            KeyColorHex = string.IsNullOrWhiteSpace(config.KeyColor) ? "#000000" : config.KeyColor,
            KeyTolerance = Math.Clamp(config.KeyTolerance, 0, 1),
            Parent = parent
        };

        if (config.FilePaths.Count > 0)
        {
            model.FilePaths.AddRange(config.FilePaths);
        }

        if (kind == LayerEditorSourceKind.Window &&
            string.IsNullOrWhiteSpace(model.WindowTitle) &&
            !string.IsNullOrWhiteSpace(model.DisplayName))
        {
            model.WindowTitle = model.DisplayName;
        }

        foreach (var animation in config.Animations)
        {
            model.Animations.Add(new LayerEditorAnimation
            {
                Id = Guid.NewGuid(),
                Type = string.IsNullOrWhiteSpace(animation.Type) ? "ZoomIn" : animation.Type,
                Loop = string.IsNullOrWhiteSpace(animation.Loop) ? "Forward" : animation.Loop,
                Speed = string.IsNullOrWhiteSpace(animation.Speed) ? "Normal" : animation.Speed,
                TranslateDirection = string.IsNullOrWhiteSpace(animation.TranslateDirection) ? "Right" : animation.TranslateDirection,
                RotationDirection = string.IsNullOrWhiteSpace(animation.RotationDirection) ? "Clockwise" : animation.RotationDirection,
                RotationDegrees = animation.RotationDegrees,
                DvdScale = animation.DvdScale,
                BeatShakeIntensity = animation.BeatShakeIntensity,
                AudioGranularLowGain = animation.AudioGranularLowGain,
                AudioGranularMidGain = animation.AudioGranularMidGain,
                AudioGranularHighGain = animation.AudioGranularHighGain,
                BeatsPerCycle = animation.BeatsPerCycle,
                Parent = model
            });
        }

        foreach (var child in config.Children)
        {
            model.Children.Add(ToEditorSource(child, model));
        }

        return model;
    }

    private static LayerEditorSourceKind ResolveKind(LayerConfigSource config)
    {
        if (Enum.TryParse<LayerEditorSourceKind>(config.Type, true, out var parsed))
        {
            return parsed;
        }

        if (!string.IsNullOrWhiteSpace(config.FilePath) &&
            config.FilePath.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase))
        {
            return LayerEditorSourceKind.Youtube;
        }

        return LayerEditorSourceKind.File;
    }

    private List<LayerEditorSimulationLayer> BuildLegacyEditorSimulationLayers()
    {
        string positiveBlend = string.IsNullOrWhiteSpace(PositiveLayerBlendMode) ? "Additive" : PositiveLayerBlendMode;
        string negativeBlend = string.IsNullOrWhiteSpace(NegativeLayerBlendMode) ? "Subtractive" : NegativeLayerBlendMode;
        var order = BuildLegacyOrder(SimulationLayerOrder);
        var byKey = new Dictionary<string, LayerEditorSimulationLayer>(StringComparer.OrdinalIgnoreCase)
        {
            ["Positive"] = new LayerEditorSimulationLayer
            {
                Id = Guid.NewGuid(),
                Name = "Positive",
                Enabled = PositiveLayerEnabled,
                InputFunction = "Direct",
                BlendMode = positiveBlend,
                InjectionMode = string.IsNullOrWhiteSpace(ProjectSettings?.InjectionMode) ? "Threshold" : ProjectSettings.InjectionMode,
                ThresholdMin = 0.35,
                ThresholdMax = 0.75,
                InvertThreshold = false
            },
            ["Negative"] = new LayerEditorSimulationLayer
            {
                Id = Guid.NewGuid(),
                Name = "Negative",
                Enabled = NegativeLayerEnabled,
                InputFunction = "Inverse",
                BlendMode = negativeBlend,
                InjectionMode = string.IsNullOrWhiteSpace(ProjectSettings?.InjectionMode) ? "Threshold" : ProjectSettings.InjectionMode,
                ThresholdMin = 0.35,
                ThresholdMax = 0.75,
                InvertThreshold = false
            }
        };

        var list = new List<LayerEditorSimulationLayer>(2);
        foreach (var key in order)
        {
            if (byKey.TryGetValue(key, out var layer))
            {
                list.Add(layer);
            }
        }

        if (list.Count == 0)
        {
            list.AddRange(byKey.Values);
        }

        return list;
    }

    private static List<string> BuildLegacyOrder(IReadOnlyList<string>? order)
    {
        var normalized = new List<string>();
        if (order != null)
        {
            foreach (var value in order)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (value.Equals("Positive", StringComparison.OrdinalIgnoreCase) &&
                    !normalized.Contains("Positive", StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add("Positive");
                }
                else if (value.Equals("Negative", StringComparison.OrdinalIgnoreCase) &&
                         !normalized.Contains("Negative", StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add("Negative");
                }
            }
        }

        if (!normalized.Contains("Positive", StringComparer.OrdinalIgnoreCase))
        {
            normalized.Add("Positive");
        }

        if (!normalized.Contains("Negative", StringComparer.OrdinalIgnoreCase))
        {
            normalized.Add("Negative");
        }

        return normalized;
    }
}

internal sealed class LayerConfigSource
{
    public string? Type { get; set; }
    public string? WindowTitle { get; set; }
    public string? WebcamId { get; set; }
    public string? FilePath { get; set; }
    public List<string> FilePaths { get; set; } = new();
    public string? DisplayName { get; set; }
    public string? BlendMode { get; set; }
    public string? FitMode { get; set; }
    public double Opacity { get; set; } = 1.0;
    public bool VideoAudioEnabled { get; set; }
    public double VideoAudioVolume { get; set; } = 1.0;
    public bool Mirror { get; set; }
    public bool KeyEnabled { get; set; }
    public string? KeyColor { get; set; }
    public double KeyTolerance { get; set; } = 0.1;
    public List<LayerConfigAnimation> Animations { get; set; } = new();
    public List<LayerConfigSource> Children { get; set; } = new();
}

internal sealed class LayerConfigAnimation
{
    public string? Type { get; set; }
    public string? Loop { get; set; }
    public string? Speed { get; set; }
    public string? TranslateDirection { get; set; }
    public string? RotationDirection { get; set; }
    public double RotationDegrees { get; set; } = 12.0;
    public double DvdScale { get; set; } = 0.2;
    public double BeatShakeIntensity { get; set; } = 1.0;
    public double AudioGranularLowGain { get; set; } = 1.0;
    public double AudioGranularMidGain { get; set; } = 1.0;
    public double AudioGranularHighGain { get; set; } = 1.0;
    public double BeatsPerCycle { get; set; } = 1.0;
}

internal sealed class LayerConfigSimulationLayer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Simulation Layer";
    public bool Enabled { get; set; } = true;
    public string InputFunction { get; set; } = "Direct";
    public string BlendMode { get; set; } = "Subtractive";
    public string InjectionMode { get; set; } = "Threshold";
    public double ThresholdMin { get; set; } = 0.35;
    public double ThresholdMax { get; set; } = 0.75;
    public bool InvertThreshold { get; set; }
}

internal sealed class LayerConfigProjectSettings
{
    public int Height { get; set; } = 144;
    public int Depth { get; set; } = 24;
    public double Framerate { get; set; } = 60;
    public string LifeMode { get; set; } = "NaiveGrayscale";
    public string BinningMode { get; set; } = "Fill";
    public string InjectionMode { get; set; } = "Threshold";
    public double InjectionNoise { get; set; }
    public double LifeOpacity { get; set; } = 1.0;
    public double RgbHueShiftDegrees { get; set; }
    public double RgbHueShiftSpeedDegreesPerSecond { get; set; }
    public bool InvertComposite { get; set; }
    public bool Passthrough { get; set; }
    public string CompositeBlendMode { get; set; } = "Additive";
}
