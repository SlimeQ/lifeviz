using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace lifeviz;

internal static class DefaultScene
{
    // Persist a stable reference, never a versioned ClickOnce install path.
    internal const string VideoReference = "lifeviz-asset:starter-loop";
    internal static string VideoPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Starter", "lifeviz-loop.mp4");

    [return: NotNullIfNotNull(nameof(path))]
    internal static string? ResolveMediaPath(string? path) =>
        string.Equals(path, VideoReference, StringComparison.OrdinalIgnoreCase) ? VideoPath : path;

    [return: NotNullIfNotNull(nameof(path))]
    internal static string? PortableMediaPath(string? path) =>
        string.Equals(path, VideoPath, StringComparison.OrdinalIgnoreCase) ? VideoReference : path;

    internal static LayerConfigFile Create()
    {
        if (!File.Exists(VideoPath))
            throw new FileNotFoundException("The bundled starter video is missing. Reinstall LifeViz to restore it.", VideoPath);

        return new LayerConfigFile
        {
            ProjectSettings = new LayerConfigProjectSettings
            {
                Height = 480, Depth = 12, Framerate = 30,
                LifeOpacity = 1, Passthrough = true, CompositeBlendMode = "Additive"
            },
            Sources = new()
            {
                new()
                {
                    Type = "File", DisplayName = "LifeViz Demo", FilePath = VideoReference,
                    BlendMode = "Normal", FitMode = "Fit", VideoAudioEnabled = false
                },
                new()
                {
                    Type = "SimGroup", DisplayName = "Simulation", BlendMode = "Additive",
                    SimulationLayers = new()
                    {
                        new()
                        {
                            Id = Guid.NewGuid(), Name = "Life Sim", LayerType = "Life",
                            BlendMode = "Additive", LifeOpacity = 0.3,
                            LifeMode = "NaiveGrayscale", BinningMode = "Fill",
                            InjectionMode = "Threshold", ThresholdMin = 0.45, ThresholdMax = 0.9
                        }
                    }
                }
            }
        };
    }
}
