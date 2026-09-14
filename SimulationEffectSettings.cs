using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace lifeviz;

// Numeric values are shared with GpuFieldEffects.hlsl.
internal enum ImageSimulationEffect { PixelSort = 0, Datamosh = 1, FluidInk = 2, TimeDisplacement = 3, ReactionDiffusion = 4 }

// Shared by editor, autosave, scene projects and bake snapshots. Each owner gets a clone.
internal sealed class SimulationEffectSettings : INotifyPropertyChanged
{
    private double _fluidFlow = 0.45, _fluidPersistence = 0.94, _fluidSwirl = 0.45;
    private double _timeSpread = 0.65, _timeScale = 3, _timeMotion = 0.3;
    private double _reactionFeed = 0.0367, _reactionKill = 0.0649, _reactionSeed = 0.35;
    public double FluidFlow { get => _fluidFlow; set => Set(ref _fluidFlow, value, 0, 1); }
    public double FluidPersistence { get => _fluidPersistence; set => Set(ref _fluidPersistence, value, 0, 0.995); }
    public double FluidSwirl { get => _fluidSwirl; set => Set(ref _fluidSwirl, value, 0, 1); }
    public double TimeSpread { get => _timeSpread; set => Set(ref _timeSpread, value, 0, 1); }
    public double TimeScale { get => _timeScale; set => Set(ref _timeScale, value, 0.5, 12); }
    public double TimeMotion { get => _timeMotion; set => Set(ref _timeMotion, value, 0, 1); }
    public double ReactionFeed { get => _reactionFeed; set => Set(ref _reactionFeed, value, 0.01, 0.08); }
    public double ReactionKill { get => _reactionKill; set => Set(ref _reactionKill, value, 0.03, 0.075); }
    public double ReactionSeed { get => _reactionSeed; set => Set(ref _reactionSeed, value, 0, 1); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set(ref double field, double value, double min, double max, [CallerMemberName] string? name = null)
    {
        double normalized = double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
        if (field == normalized) return;
        field = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public SimulationEffectSettings Clone() { var result = new SimulationEffectSettings(); result.CopyFrom(this); return result; }
    public void CopyFrom(SimulationEffectSettings source)
    {
        FluidFlow = source.FluidFlow; FluidPersistence = source.FluidPersistence; FluidSwirl = source.FluidSwirl;
        TimeSpread = source.TimeSpread; TimeScale = source.TimeScale; TimeMotion = source.TimeMotion;
        ReactionFeed = source.ReactionFeed; ReactionKill = source.ReactionKill; ReactionSeed = source.ReactionSeed;
    }
    public static string DisplayName(string type) => type switch
    {
        "FluidInk" => "Fluid Ink", "TimeDisplacement" => "Time Displacement",
        "ReactionDiffusion" => "Reaction–Diffusion", "Datamosh" => "Datamosh",
        "PixelSort" => "Pixel Sort", _ => "Life Sim"
    };
}
