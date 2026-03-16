using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace lifeviz;

internal sealed class LayerConfigFile
{
    public int Version { get; set; } = 7;
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
        var sourceList = sources.ToList();
        bool hasEmbeddedSimulationGroups = EnumerateSources(sourceList).Any(source => source.Kind == LayerEditorSourceKind.SimGroup);
        var file = new LayerConfigFile
        {
            ProjectSettings = new LayerConfigProjectSettings
            {
                Height = Math.Max(1, projectSettings.Height),
                Depth = Math.Max(1, projectSettings.Depth),
                Framerate = projectSettings.Framerate,
                LifeOpacity = Math.Clamp(projectSettings.LifeOpacity, 0, 1),
                // Legacy global hue lives on project settings for backward compatibility only.
                RgbHueShiftDegrees = 0,
                RgbHueShiftSpeedDegreesPerSecond = 0,
                InvertComposite = projectSettings.InvertComposite,
                Passthrough = projectSettings.Passthrough,
                CompositeBlendMode = string.IsNullOrWhiteSpace(projectSettings.CompositeBlendMode) ? "Additive" : projectSettings.CompositeBlendMode
            },
            SimulationLayers = hasEmbeddedSimulationGroups
                ? new List<LayerConfigSimulationLayer>()
                : simulationLayers.Select(FromEditorSimulationLayer).ToList()
        };
        if (!hasEmbeddedSimulationGroups && file.SimulationLayers.Count == 0)
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
                    LifeMode = "NaiveGrayscale",
                    BinningMode = "Fill",
                    InjectionNoise = 0,
                    LifeOpacity = 1.0,
                    RgbHueShiftDegrees = 0,
                    RgbHueShiftSpeedDegreesPerSecond = 0,
                    AudioFrequencyHueShiftDegrees = 0,
                    ReactiveMappings = new List<LayerConfigReactiveMapping>(),
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
                    LifeMode = "NaiveGrayscale",
                    BinningMode = "Fill",
                    InjectionNoise = 0,
                    LifeOpacity = 1.0,
                    RgbHueShiftDegrees = 0,
                    RgbHueShiftSpeedDegreesPerSecond = 0,
                    AudioFrequencyHueShiftDegrees = 0,
                    ReactiveMappings = new List<LayerConfigReactiveMapping>(),
                    ThresholdMin = 0.35,
                    ThresholdMax = 0.75,
                    InvertThreshold = false
                }
            };
        }
        foreach (var source in sourceList)
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

        if (!list.Any(source => source.Kind == LayerEditorSourceKind.SimGroup) &&
            SimulationLayers != null &&
            SimulationLayers.Count > 0)
        {
            var simGroup = new LayerEditorSource
            {
                Id = Guid.NewGuid(),
                Kind = LayerEditorSourceKind.SimGroup,
                DisplayName = "Simulation",
                Enabled = true,
                BlendMode = "Additive",
                FitMode = "Fill",
                Opacity = 1.0,
                KeyColorHex = "#000000",
                KeyTolerance = 0.1
            };

        foreach (var simulationLayer in ToEditorSimulationLayers())
        {
            simGroup.SimulationLayers.Add(simulationLayer);
        }

            list.Add(simGroup);
        }

        return list;
    }

    public List<LayerEditorSimulationLayer> ToEditorSimulationLayers()
    {
        if (SimulationLayers != null && SimulationLayers.Count > 0)
        {
            return FlattenSimulationLayers(SimulationLayers)
                .Select(layer => ToEditorSimulationLayer(layer, null))
                .ToList();
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
            Enabled = source.Enabled,
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

        foreach (var simulationLayer in source.SimulationLayers)
        {
            config.SimulationLayers.Add(FromEditorSimulationLayer(simulationLayer));
        }

        return config;
    }

    private static LayerConfigSimulationLayer FromEditorSimulationLayer(LayerEditorSimulationLayer layer)
    {
        var config = new LayerConfigSimulationLayer
        {
            Id = layer.Id,
            Kind = layer.Kind.ToString(),
            Name = string.IsNullOrWhiteSpace(layer.Name)
                ? (layer.IsGroup ? "Sim Group" : "Simulation Layer")
                : layer.Name,
            Enabled = layer.Enabled,
            InputFunction = string.IsNullOrWhiteSpace(layer.InputFunction) ? "Direct" : layer.InputFunction,
            BlendMode = string.IsNullOrWhiteSpace(layer.BlendMode) ? "Subtractive" : layer.BlendMode,
            InjectionMode = string.IsNullOrWhiteSpace(layer.InjectionMode) ? "Threshold" : layer.InjectionMode,
            LifeMode = string.IsNullOrWhiteSpace(layer.LifeMode) ? "NaiveGrayscale" : layer.LifeMode,
            BinningMode = string.IsNullOrWhiteSpace(layer.BinningMode) ? "Fill" : layer.BinningMode,
            InjectionNoise = Math.Clamp(layer.InjectionNoise, 0, 1),
            LifeOpacity = Math.Clamp(layer.LifeOpacity, 0, 1),
            RgbHueShiftDegrees = layer.RgbHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = layer.RgbHueShiftSpeedDegreesPerSecond,
            AudioFrequencyHueShiftDegrees = 0,
            ReactiveMappings = layer.ReactiveMappings
                .Select(mapping => new LayerConfigReactiveMapping
                {
                    Id = mapping.Id,
                    Input = string.IsNullOrWhiteSpace(mapping.Input) ? nameof(SimulationReactiveInput.Level) : mapping.Input,
                    Output = string.IsNullOrWhiteSpace(mapping.Output) ? nameof(SimulationReactiveOutput.Opacity) : mapping.Output,
                    Amount = mapping.Amount
                })
                .ToList(),
            ThresholdMin = Math.Clamp(layer.ThresholdMin, 0, 1),
            ThresholdMax = Math.Clamp(layer.ThresholdMax, 0, 1),
            InvertThreshold = layer.InvertThreshold
        };

        foreach (var child in layer.Children)
        {
            config.Children.Add(FromEditorSimulationLayer(child));
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
            Enabled = config.Enabled,
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

        foreach (var simulationLayer in FlattenSimulationLayers(config.SimulationLayers))
        {
            model.SimulationLayers.Add(ToEditorSimulationLayer(simulationLayer, null));
        }

        return model;
    }

    private static LayerEditorSimulationLayer ToEditorSimulationLayer(LayerConfigSimulationLayer layer, LayerEditorSimulationLayer? parent)
    {
        var reactiveMappings = (layer.ReactiveMappings ?? new List<LayerConfigReactiveMapping>())
            .Select(mapping => new LayerEditorSimulationReactiveMapping
            {
                Id = mapping.Id == Guid.Empty ? Guid.NewGuid() : mapping.Id,
                Input = string.IsNullOrWhiteSpace(mapping.Input) ? nameof(SimulationReactiveInput.Level) : mapping.Input,
                Output = string.IsNullOrWhiteSpace(mapping.Output) ? nameof(SimulationReactiveOutput.Opacity) : mapping.Output,
                Amount = SimulationReactivity.ClampAmount(
                    ParseReactiveOutputOrDefault(mapping.Output, SimulationReactiveOutput.Opacity),
                    mapping.Amount)
            })
            .ToList();

        if (reactiveMappings.Count == 0 && layer.AudioFrequencyHueShiftDegrees > 0.001)
        {
            reactiveMappings.Add(new LayerEditorSimulationReactiveMapping
            {
                Id = Guid.NewGuid(),
                Input = nameof(SimulationReactiveInput.Frequency),
                Output = nameof(SimulationReactiveOutput.HueShift),
                Amount = Math.Clamp(layer.AudioFrequencyHueShiftDegrees, 0, 360)
            });
        }

        var model = new LayerEditorSimulationLayer
        {
            Id = layer.Id == Guid.Empty ? Guid.NewGuid() : layer.Id,
            Kind = ParseSimulationItemKind(layer.Kind),
            Name = string.IsNullOrWhiteSpace(layer.Name)
                ? (string.Equals(layer.Kind, nameof(LayerEditorSimulationItemKind.Group), StringComparison.OrdinalIgnoreCase)
                    ? "Sim Group"
                    : "Simulation Layer")
                : layer.Name,
            Enabled = layer.Enabled,
            InputFunction = string.IsNullOrWhiteSpace(layer.InputFunction) ? "Direct" : layer.InputFunction,
            BlendMode = string.IsNullOrWhiteSpace(layer.BlendMode) ? "Subtractive" : layer.BlendMode,
            InjectionMode = string.IsNullOrWhiteSpace(layer.InjectionMode) ? "Threshold" : layer.InjectionMode,
            LifeMode = string.IsNullOrWhiteSpace(layer.LifeMode) ? "NaiveGrayscale" : layer.LifeMode,
            BinningMode = string.IsNullOrWhiteSpace(layer.BinningMode) ? "Fill" : layer.BinningMode,
            InjectionNoise = Math.Clamp(layer.InjectionNoise, 0, 1),
            LifeOpacity = Math.Clamp(layer.LifeOpacity, 0, 1),
            RgbHueShiftDegrees = layer.RgbHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = layer.RgbHueShiftSpeedDegreesPerSecond,
            AudioFrequencyHueShiftDegrees = 0,
            ReactiveMappings = new ObservableCollection<LayerEditorSimulationReactiveMapping>(reactiveMappings),
            ThresholdMin = Math.Clamp(layer.ThresholdMin, 0, 1),
            ThresholdMax = Math.Clamp(layer.ThresholdMax, 0, 1),
            InvertThreshold = layer.InvertThreshold,
            Parent = parent
        };

        foreach (var child in layer.Children)
        {
            model.Children.Add(ToEditorSimulationLayer(child, model));
        }

        return model;
    }

    private static IEnumerable<LayerEditorSource> EnumerateSources(IEnumerable<LayerEditorSource> roots)
    {
        foreach (var source in roots)
        {
            yield return source;
            foreach (var child in EnumerateSources(source.Children))
            {
                yield return child;
            }
        }
    }

    private static List<LayerConfigSimulationLayer> FlattenSimulationLayers(IEnumerable<LayerConfigSimulationLayer> layers)
    {
        var flattened = new List<LayerConfigSimulationLayer>();
        foreach (var layer in layers)
        {
            FlattenSimulationLayer(layer, flattened, ancestorEnabled: true);
        }

        return flattened;
    }

    private static void FlattenSimulationLayer(LayerConfigSimulationLayer layer, ICollection<LayerConfigSimulationLayer> target, bool ancestorEnabled)
    {
        bool enabled = ancestorEnabled && layer.Enabled;
        if (ParseSimulationItemKind(layer.Kind) == LayerEditorSimulationItemKind.Group)
        {
            foreach (var child in layer.Children)
            {
                FlattenSimulationLayer(child, target, enabled);
            }

            return;
        }

        var flattened = new LayerConfigSimulationLayer
        {
            Id = layer.Id,
            Kind = nameof(LayerEditorSimulationItemKind.Layer),
            Name = layer.Name,
            Enabled = enabled,
            InputFunction = layer.InputFunction,
            BlendMode = layer.BlendMode,
            InjectionMode = layer.InjectionMode,
            LifeMode = layer.LifeMode,
            BinningMode = layer.BinningMode,
            InjectionNoise = layer.InjectionNoise,
            LifeOpacity = layer.LifeOpacity,
            RgbHueShiftDegrees = layer.RgbHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = layer.RgbHueShiftSpeedDegreesPerSecond,
            AudioFrequencyHueShiftDegrees = layer.AudioFrequencyHueShiftDegrees,
            ReactiveMappings = layer.ReactiveMappings.Select(mapping => new LayerConfigReactiveMapping
            {
                Id = mapping.Id,
                Input = mapping.Input,
                Output = mapping.Output,
                Amount = mapping.Amount
            }).ToList(),
            ThresholdMin = layer.ThresholdMin,
            ThresholdMax = layer.ThresholdMax,
            InvertThreshold = layer.InvertThreshold
        };
        target.Add(flattened);
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
                LifeMode = string.IsNullOrWhiteSpace(ProjectSettings?.LifeMode) ? "NaiveGrayscale" : ProjectSettings.LifeMode,
                BinningMode = string.IsNullOrWhiteSpace(ProjectSettings?.BinningMode) ? "Fill" : ProjectSettings.BinningMode,
                InjectionNoise = Math.Clamp(ProjectSettings?.InjectionNoise ?? 0, 0, 1),
                LifeOpacity = 1.0,
                RgbHueShiftDegrees = ProjectSettings?.RgbHueShiftDegrees ?? 0,
                RgbHueShiftSpeedDegreesPerSecond = ProjectSettings?.RgbHueShiftSpeedDegreesPerSecond ?? 0,
                AudioFrequencyHueShiftDegrees = 0,
                ReactiveMappings = new ObservableCollection<LayerEditorSimulationReactiveMapping>(),
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
                LifeMode = string.IsNullOrWhiteSpace(ProjectSettings?.LifeMode) ? "NaiveGrayscale" : ProjectSettings.LifeMode,
                BinningMode = string.IsNullOrWhiteSpace(ProjectSettings?.BinningMode) ? "Fill" : ProjectSettings.BinningMode,
                InjectionNoise = Math.Clamp(ProjectSettings?.InjectionNoise ?? 0, 0, 1),
                LifeOpacity = 1.0,
                RgbHueShiftDegrees = ProjectSettings?.RgbHueShiftDegrees ?? 0,
                RgbHueShiftSpeedDegreesPerSecond = ProjectSettings?.RgbHueShiftSpeedDegreesPerSecond ?? 0,
                AudioFrequencyHueShiftDegrees = 0,
                ReactiveMappings = new ObservableCollection<LayerEditorSimulationReactiveMapping>(),
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

    private static SimulationReactiveOutput ParseReactiveOutputOrDefault(string? value, SimulationReactiveOutput fallback)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Enum.TryParse<SimulationReactiveOutput>(value, true, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static LayerEditorSimulationItemKind ParseSimulationItemKind(string? value)
    {
        return Enum.TryParse<LayerEditorSimulationItemKind>(value, true, out var parsed)
            ? parsed
            : LayerEditorSimulationItemKind.Layer;
    }
}

internal sealed class LayerConfigSource
{
    public string? Type { get; set; }
    public bool Enabled { get; set; } = true;
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
    public List<LayerConfigSimulationLayer> SimulationLayers { get; set; } = new();
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
    public string Kind { get; set; } = nameof(LayerEditorSimulationItemKind.Layer);
    public string Name { get; set; } = "Simulation Layer";
    public bool Enabled { get; set; } = true;
    public string InputFunction { get; set; } = "Direct";
    public string BlendMode { get; set; } = "Subtractive";
    public string InjectionMode { get; set; } = "Threshold";
    public string LifeMode { get; set; } = "NaiveGrayscale";
    public string BinningMode { get; set; } = "Fill";
    public double InjectionNoise { get; set; }
    public double LifeOpacity { get; set; } = 1.0;
    public double RgbHueShiftDegrees { get; set; }
    public double RgbHueShiftSpeedDegreesPerSecond { get; set; }
    public double AudioFrequencyHueShiftDegrees { get; set; }
    public List<LayerConfigReactiveMapping> ReactiveMappings { get; set; } = new();
    public double ThresholdMin { get; set; } = 0.35;
    public double ThresholdMax { get; set; } = 0.75;
    public bool InvertThreshold { get; set; }
    public List<LayerConfigSimulationLayer> Children { get; set; } = new();
}

internal sealed class LayerConfigReactiveMapping
{
    public Guid Id { get; set; }
    public string Input { get; set; } = nameof(SimulationReactiveInput.Level);
    public string Output { get; set; } = nameof(SimulationReactiveOutput.Opacity);
    public double Amount { get; set; } = 1.0;
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
