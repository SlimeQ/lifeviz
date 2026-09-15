using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace lifeviz;

// Numeric values are shared with GpuFieldEffects.hlsl.
internal enum ImageSimulationEffect { PixelSort = 0, Datamosh = 1, FluidInk = 2, TimeDisplacement = 3, ReactionDiffusion = 4, FeedbackKaleidoscope = 5, ParticleErosion = 6, RippleField = 7, ChromaticMemory = 8, ContourCurrent = 9 }

// Shared by editor, autosave, scene projects and bake snapshots. Each owner gets a clone.
internal sealed class SimulationEffectSettings : INotifyPropertyChanged
{
    private double _fluidFlow = 0.45, _fluidPersistence = 0.94, _fluidSwirl = 0.45;
    private double _timeSpread = 0.65, _timeScale = 3, _timeMotion = 0.3;
    private double _reactionFeed = 0.0367, _reactionKill = 0.0649, _reactionSeed = 0.35;
    private double _kaleidoscopeFeedback = 0.82, _kaleidoscopeZoom = 1.015, _kaleidoscopeRotation = 1;
    private double _kaleidoscopeFolds = 6, _kaleidoscopeCenterX = 0.5, _kaleidoscopeCenterY = 0.5;
    public double KaleidoscopeFeedback { get => _kaleidoscopeFeedback; set => Set(ref _kaleidoscopeFeedback, value, 0, 0.98); }
    public double KaleidoscopeZoom { get => _kaleidoscopeZoom; set => Set(ref _kaleidoscopeZoom, value, 0.9, 1.1); }
    public double KaleidoscopeRotation { get => _kaleidoscopeRotation; set => Set(ref _kaleidoscopeRotation, value, -10, 10); }
    public double KaleidoscopeFolds { get => _kaleidoscopeFolds; set => Set(ref _kaleidoscopeFolds, Math.Round(value), 2, 16); }
    public double KaleidoscopeCenterX { get => _kaleidoscopeCenterX; set => Set(ref _kaleidoscopeCenterX, value, 0, 1); }
    public double KaleidoscopeCenterY { get => _kaleidoscopeCenterY; set => Set(ref _kaleidoscopeCenterY, value, 0, 1); }
    public double FluidFlow { get => _fluidFlow; set => Set(ref _fluidFlow, value, 0, 1); }
    public double FluidPersistence { get => _fluidPersistence; set => Set(ref _fluidPersistence, value, 0, 0.995); }
    public double FluidSwirl { get => _fluidSwirl; set => Set(ref _fluidSwirl, value, 0, 1); }
    public double TimeSpread { get => _timeSpread; set => Set(ref _timeSpread, value, 0, 1); }
    public double TimeScale { get => _timeScale; set => Set(ref _timeScale, value, 0.5, 12); }
    public double TimeMotion { get => _timeMotion; set => Set(ref _timeMotion, value, 0, 1); }
    public double ReactionFeed { get => _reactionFeed; set => Set(ref _reactionFeed, value, 0.01, 0.08); }
    public double ReactionKill { get => _reactionKill; set => Set(ref _reactionKill, value, 0.03, 0.075); }
    public double ReactionSeed { get => _reactionSeed; set => Set(ref _reactionSeed, value, 0, 1); }
    private double _particleEmission = 0.45;
    public double ParticleEmission { get => _particleEmission; set => Set(ref _particleEmission, value, 0, 1); }
    private double _particleGravity = 0.45;
    public double ParticleGravity { get => _particleGravity; set => Set(ref _particleGravity, value, -1, 1); }
    private double _particleTurbulence = 0.55;
    public double ParticleTurbulence { get => _particleTurbulence; set => Set(ref _particleTurbulence, value, 0, 1); }
    private double _particlePersistence = 0.94;
    public double ParticlePersistence { get => _particlePersistence; set => Set(ref _particlePersistence, value, 0, 0.99); }
    private double _rippleImpulse = 0.55;
    public double RippleImpulse { get => _rippleImpulse; set => Set(ref _rippleImpulse, value, 0, 1); }
    private double _rippleSpeed = 0.45;
    public double RippleSpeed { get => _rippleSpeed; set => Set(ref _rippleSpeed, value, 0, 1); }
    private double _rippleDamping = 0.985;
    public double RippleDamping { get => _rippleDamping; set => Set(ref _rippleDamping, value, 0.9, 0.999); }
    private double _rippleRefraction = 0.65;
    public double RippleRefraction { get => _rippleRefraction; set => Set(ref _rippleRefraction, value, 0, 1); }
    private double _chromaticRed = 0.92;
    public double ChromaticRed { get => _chromaticRed; set => Set(ref _chromaticRed, value, 0, 0.99); }
    private double _chromaticGreen = 0.8;
    public double ChromaticGreen { get => _chromaticGreen; set => Set(ref _chromaticGreen, value, 0, 0.99); }
    private double _chromaticBlue = 0.65;
    public double ChromaticBlue { get => _chromaticBlue; set => Set(ref _chromaticBlue, value, 0, 0.99); }
    private double _chromaticDrift = 0.35;
    public double ChromaticDrift { get => _chromaticDrift; set => Set(ref _chromaticDrift, value, 0, 1); }
    private double _contourFlow = 0.5;
    public double ContourFlow { get => _contourFlow; set => Set(ref _contourFlow, value, 0, 1); }
    private double _contourThickness = 0.4;
    public double ContourThickness { get => _contourThickness; set => Set(ref _contourThickness, value, 0, 1); }
    private double _contourPersistence = 0.94;
    public double ContourPersistence { get => _contourPersistence; set => Set(ref _contourPersistence, value, 0, 0.99); }
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
        ParticleEmission = source.ParticleEmission;
        ParticleGravity = source.ParticleGravity;
        ParticleTurbulence = source.ParticleTurbulence;
        ParticlePersistence = source.ParticlePersistence;
        RippleImpulse = source.RippleImpulse;
        RippleSpeed = source.RippleSpeed;
        RippleDamping = source.RippleDamping;
        RippleRefraction = source.RippleRefraction;
        ChromaticRed = source.ChromaticRed;
        ChromaticGreen = source.ChromaticGreen;
        ChromaticBlue = source.ChromaticBlue;
        ChromaticDrift = source.ChromaticDrift;
        ContourFlow = source.ContourFlow;
        ContourThickness = source.ContourThickness;
        ContourPersistence = source.ContourPersistence;
        KaleidoscopeFeedback = source.KaleidoscopeFeedback; KaleidoscopeZoom = source.KaleidoscopeZoom;
        KaleidoscopeRotation = source.KaleidoscopeRotation; KaleidoscopeFolds = source.KaleidoscopeFolds;
        KaleidoscopeCenterX = source.KaleidoscopeCenterX; KaleidoscopeCenterY = source.KaleidoscopeCenterY;
        FluidFlow = source.FluidFlow; FluidPersistence = source.FluidPersistence; FluidSwirl = source.FluidSwirl;
        TimeSpread = source.TimeSpread; TimeScale = source.TimeScale; TimeMotion = source.TimeMotion;
        ReactionFeed = source.ReactionFeed; ReactionKill = source.ReactionKill; ReactionSeed = source.ReactionSeed;
    }
    public static string DisplayName(string type) => type switch
    {
        "ParticleErosion" => "Particle Erosion",
        "RippleField" => "Ripple Field",
        "ChromaticMemory" => "Chromatic Memory",
        "ContourCurrent" => "Contour Current",
        "FeedbackKaleidoscope" => "Feedback Kaleidoscope",
        "FluidInk" => "Fluid Ink", "TimeDisplacement" => "Time Displacement",
        "ReactionDiffusion" => "Reaction–Diffusion", "Datamosh" => "Datamosh",
        "PixelSort" => "Pixel Sort", _ => "Life Sim"
    };
}
