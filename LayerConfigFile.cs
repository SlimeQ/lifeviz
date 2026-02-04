using System;
using System.Collections.Generic;

namespace lifeviz;

internal sealed class LayerConfigFile
{
    public int Version { get; set; } = 1;
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    public List<LayerConfigSource> Sources { get; set; } = new();

    public static LayerConfigFile FromEditorSources(IEnumerable<LayerEditorSource> sources)
    {
        var file = new LayerConfigFile();
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
    public double BeatsPerCycle { get; set; } = 1.0;
}
