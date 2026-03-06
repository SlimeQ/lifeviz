using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace lifeviz;

internal enum LayerEditorSourceKind
{
    Window,
    Webcam,
    File,
    VideoSequence,
    Group,
    Youtube
}

internal sealed class LayerEditorOption
{
    public LayerEditorOption(string value, string label)
    {
        Value = value;
        Label = label;
    }

    public string Value { get; }
    public string Label { get; }
}

internal static class LayerEditorOptions
{
    public static readonly IReadOnlyList<int> SimulationHeightPresets = new[]
    {
        144, 240, 480, 720, 1080, 1440, 2160
    };

    public static readonly IReadOnlyList<LayerEditorOption> SimulationLifeModes = new[]
    {
        new LayerEditorOption("NaiveGrayscale", "Naive Grayscale"),
        new LayerEditorOption("RgbChannels", "RGB Channel Bins")
    };

    public static readonly IReadOnlyList<LayerEditorOption> SimulationBinningModes = new[]
    {
        new LayerEditorOption("Fill", "Fill"),
        new LayerEditorOption("Binary", "Binary")
    };

    public static readonly IReadOnlyList<LayerEditorOption> BlendModes = new[]
    {
        new LayerEditorOption("Additive", "Additive"),
        new LayerEditorOption("Normal", "Normal"),
        new LayerEditorOption("Multiply", "Multiply"),
        new LayerEditorOption("Screen", "Screen"),
        new LayerEditorOption("Overlay", "Overlay"),
        new LayerEditorOption("Lighten", "Lighten"),
        new LayerEditorOption("Darken", "Darken"),
        new LayerEditorOption("Subtractive", "Subtractive")
    };

    public static readonly IReadOnlyList<LayerEditorOption> FitModes = new[]
    {
        new LayerEditorOption("Fit", "Fit"),
        new LayerEditorOption("Fill", "Fill"),
        new LayerEditorOption("Stretch", "Stretch"),
        new LayerEditorOption("Center", "Center"),
        new LayerEditorOption("Tile", "Tile"),
        new LayerEditorOption("Span", "Span")
    };

    public static readonly IReadOnlyList<LayerEditorOption> AnimationTypes = new[]
    {
        new LayerEditorOption("ZoomIn", "Zoom In"),
        new LayerEditorOption("Translate", "Translate"),
        new LayerEditorOption("Rotate", "Rotate"),
        new LayerEditorOption("DvdBounce", "DVD Bounce"),
        new LayerEditorOption("BeatShake", "Beat Shake"),
        new LayerEditorOption("AudioGranular", "Audio Granular"),
        new LayerEditorOption("Fade", "Fade")
    };

    public static readonly IReadOnlyList<LayerEditorOption> AnimationLoops = new[]
    {
        new LayerEditorOption("Forward", "Forward"),
        new LayerEditorOption("PingPong", "Reverse")
    };

    public static readonly IReadOnlyList<LayerEditorOption> AnimationSpeeds = new[]
    {
        new LayerEditorOption("Eighth", "1/8x"),
        new LayerEditorOption("Quarter", "1/4x"),
        new LayerEditorOption("Half", "1/2x"),
        new LayerEditorOption("Normal", "1x"),
        new LayerEditorOption("Double", "2x"),
        new LayerEditorOption("Quadruple", "4x"),
        new LayerEditorOption("Octuple", "8x")
    };

    public static readonly IReadOnlyList<LayerEditorOption> TranslateDirections = new[]
    {
        new LayerEditorOption("Up", "Up"),
        new LayerEditorOption("Down", "Down"),
        new LayerEditorOption("Left", "Left"),
        new LayerEditorOption("Right", "Right")
    };

    public static readonly IReadOnlyList<LayerEditorOption> RotationDirections = new[]
    {
        new LayerEditorOption("Clockwise", "Clockwise"),
        new LayerEditorOption("CounterClockwise", "Counterclockwise")
    };

    public static readonly IReadOnlyList<LayerEditorOption> SimulationInputFunctions = new[]
    {
        new LayerEditorOption("Direct", "Direct (y = x)"),
        new LayerEditorOption("Inverse", "Inverse (y = 1 - x)")
    };

    public static readonly IReadOnlyList<LayerEditorOption> SimulationInjectionModes = new[]
    {
        new LayerEditorOption("Threshold", "Threshold"),
        new LayerEditorOption("RandomPulse", "Random Pulse"),
        new LayerEditorOption("PulseWidthModulation", "Pulse Width Modulation")
    };
}

internal abstract class LayerEditorNotify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class LayerEditorAnimation : LayerEditorNotify
{
    private Guid _id;
    private string _type = "ZoomIn";
    private string _loop = "Forward";
    private string _speed = "Normal";
    private string _translateDirection = "Right";
    private string _rotationDirection = "Clockwise";
    private double _rotationDegrees = 12.0;
    private double _dvdScale = 0.2;
    private double _beatShakeIntensity = 1.0;
    private double _audioGranularLowGain = 1.0;
    private double _audioGranularMidGain = 1.0;
    private double _audioGranularHighGain = 1.0;
    private double _beatsPerCycle = 1.0;

    public Guid Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }


    public string Type
    {
        get => _type;
        set
        {
            if (SetField(ref _type, value))
            {
                OnPropertyChanged(nameof(IsTranslate));
                OnPropertyChanged(nameof(IsRotate));
                OnPropertyChanged(nameof(IsDvd));
                OnPropertyChanged(nameof(IsBeatShake));
                OnPropertyChanged(nameof(IsAudioGranular));
                OnPropertyChanged(nameof(IsIntensityVisible));
                OnPropertyChanged(nameof(IsAudioGranularEqVisible));
                OnPropertyChanged(nameof(IntensityMax));
                OnPropertyChanged(nameof(IntensityLargeChange));
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(CycleMax));
                OnPropertyChanged(nameof(CycleLargeChange));
            }
        }
    }

    public string Loop
    {
        get => _loop;
        set => SetField(ref _loop, value);
    }

    public string Speed
    {
        get => _speed;
        set => SetField(ref _speed, value);
    }

    public string TranslateDirection
    {
        get => _translateDirection;
        set => SetField(ref _translateDirection, value);
    }

    public string RotationDirection
    {
        get => _rotationDirection;
        set => SetField(ref _rotationDirection, value);
    }

    public double RotationDegrees
    {
        get => _rotationDegrees;
        set => SetField(ref _rotationDegrees, value);
    }

    public double DvdScale
    {
        get => _dvdScale;
        set => SetField(ref _dvdScale, value);
    }

    public double BeatShakeIntensity
    {
        get => _beatShakeIntensity;
        set => SetField(ref _beatShakeIntensity, value);
    }

    public double AudioGranularLowGain
    {
        get => _audioGranularLowGain;
        set => SetField(ref _audioGranularLowGain, value);
    }

    public double AudioGranularMidGain
    {
        get => _audioGranularMidGain;
        set => SetField(ref _audioGranularMidGain, value);
    }

    public double AudioGranularHighGain
    {
        get => _audioGranularHighGain;
        set => SetField(ref _audioGranularHighGain, value);
    }

    public double BeatsPerCycle
    {
        get => _beatsPerCycle;
        set => SetField(ref _beatsPerCycle, value);
    }

    public bool IsTranslate => string.Equals(Type, "Translate", StringComparison.OrdinalIgnoreCase);
    public bool IsRotate => string.Equals(Type, "Rotate", StringComparison.OrdinalIgnoreCase);
    public bool IsDvd => string.Equals(Type, "DvdBounce", StringComparison.OrdinalIgnoreCase);
    public bool IsBeatShake => string.Equals(Type, "BeatShake", StringComparison.OrdinalIgnoreCase);
    public bool IsAudioGranular => string.Equals(Type, "AudioGranular", StringComparison.OrdinalIgnoreCase);

    public bool IsIntensityVisible => IsBeatShake || IsAudioGranular;
    public bool IsAudioGranularEqVisible => IsAudioGranular;
    public double IntensityMax => IsAudioGranular ? 10.0 : 2.0;
    public double IntensityLargeChange => IsAudioGranular ? 1.0 : 0.2;

    public LayerEditorSource? Parent { get; set; }

    public string DisplayName => Type switch
    {
        "ZoomIn" => "Zoom In",
        "DvdBounce" => "DVD Bounce",
        "BeatShake" => "Beat Shake",
        "AudioGranular" => "Audio Granular",
        _ => Type
    };

    public double CycleMax => IsDvd ? 128 : 4096;
    public double CycleLargeChange => IsDvd ? 8 : 32;

    public IReadOnlyList<LayerEditorOption> LoopOptions => LayerEditorOptions.AnimationLoops;
    public IReadOnlyList<LayerEditorOption> SpeedOptions => LayerEditorOptions.AnimationSpeeds;
    public IReadOnlyList<LayerEditorOption> TranslateDirectionOptions => LayerEditorOptions.TranslateDirections;
    public IReadOnlyList<LayerEditorOption> RotationDirectionOptions => LayerEditorOptions.RotationDirections;
}

internal sealed class LayerEditorSource : LayerEditorNotify
{
    private Guid _id;
    private LayerEditorSourceKind _kind;
    private string _displayName = string.Empty;
    private string? _windowTitle;
    private IntPtr? _windowHandle;
    private string? _webcamId;
    private string? _filePath;
    private string _blendMode = "Additive";
    private string _fitMode = "Fill";
    private double _opacity = 1.0;
    private bool _videoAudioEnabled;
    private double _videoAudioVolume = 1.0;
    private bool _videoPlaybackPaused;
    private double _videoPlaybackPosition;
    private double _videoPlaybackPositionSeconds;
    private double _videoPlaybackDurationSeconds;
    private bool _mirror;
    private bool _keyEnabled;
    private string _keyColorHex = "#000000";
    private double _keyTolerance = 0.1;
    private string _pendingTranslateDirection = "Right";
    private string _pendingRotationDirection = "Clockwise";
    private bool _isExpanded = true;
    private bool _isSelected;

    public Guid Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public LayerEditorSourceKind Kind
    {
        get => _kind;
        set
        {
            if (SetField(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsGroup));
                OnPropertyChanged(nameof(IsWebcam));
                OnPropertyChanged(nameof(IsWindow));
                OnPropertyChanged(nameof(IsVideo));
                OnPropertyChanged(nameof(KindLabel));
                OnPropertyChanged(nameof(DisplayLabel));
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetField(ref _displayName, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
                OnPropertyChanged(nameof(TreeLabel));
            }
        }
    }

    public string? WindowTitle
    {
        get => _windowTitle;
        set
        {
            if (SetField(ref _windowTitle, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public IntPtr? WindowHandle
    {
        get => _windowHandle;
        set => SetField(ref _windowHandle, value);
    }

    public string? WebcamId
    {
        get => _webcamId;
        set
        {
            if (SetField(ref _webcamId, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public string? FilePath
    {
        get => _filePath;
        set
        {
            if (SetField(ref _filePath, value))
            {
                OnPropertyChanged(nameof(IsVideo));
                OnPropertyChanged(nameof(DisplayLabel));
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public List<string> FilePaths { get; } = new();

    public string BlendMode
    {
        get => _blendMode;
        set
        {
            if (SetField(ref _blendMode, value))
            {
                OnPropertyChanged(nameof(IsNormalBlend));
            }
        }
    }

    public string FitMode
    {
        get => _fitMode;
        set => SetField(ref _fitMode, value);
    }

    public double Opacity
    {
        get => _opacity;
        set => SetField(ref _opacity, value);
    }

    public bool VideoAudioEnabled
    {
        get => _videoAudioEnabled;
        set => SetField(ref _videoAudioEnabled, value);
    }

    public double VideoAudioVolume
    {
        get => _videoAudioVolume;
        set => SetField(ref _videoAudioVolume, value);
    }

    public bool VideoPlaybackPaused
    {
        get => _videoPlaybackPaused;
        set
        {
            if (SetField(ref _videoPlaybackPaused, value))
            {
                OnPropertyChanged(nameof(VideoPlaybackToggleLabel));
            }
        }
    }

    public double VideoPlaybackPosition
    {
        get => _videoPlaybackPosition;
        set => SetField(ref _videoPlaybackPosition, value);
    }

    public double VideoPlaybackPositionSeconds
    {
        get => _videoPlaybackPositionSeconds;
        set
        {
            if (SetField(ref _videoPlaybackPositionSeconds, value))
            {
                OnPropertyChanged(nameof(VideoPlaybackTimeLabel));
            }
        }
    }

    public double VideoPlaybackDurationSeconds
    {
        get => _videoPlaybackDurationSeconds;
        set
        {
            if (SetField(ref _videoPlaybackDurationSeconds, value))
            {
                OnPropertyChanged(nameof(VideoPlaybackTimeLabel));
                OnPropertyChanged(nameof(VideoSeekAvailable));
            }
        }
    }

    public bool Mirror
    {
        get => _mirror;
        set => SetField(ref _mirror, value);
    }

    public bool KeyEnabled
    {
        get => _keyEnabled;
        set => SetField(ref _keyEnabled, value);
    }

    public string KeyColorHex
    {
        get => _keyColorHex;
        set => SetField(ref _keyColorHex, value);
    }

    public double KeyTolerance
    {
        get => _keyTolerance;
        set => SetField(ref _keyTolerance, value);
    }

    public string PendingTranslateDirection
    {
        get => _pendingTranslateDirection;
        set => SetField(ref _pendingTranslateDirection, value);
    }

    public string PendingRotationDirection
    {
        get => _pendingRotationDirection;
        set => SetField(ref _pendingRotationDirection, value);
    }

    public LayerEditorSource? Parent { get; set; }

    public ObservableCollection<LayerEditorSource> Children { get; } = new();

    public ObservableCollection<LayerEditorAnimation> Animations { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool IsGroup => Kind == LayerEditorSourceKind.Group;
    public bool IsWebcam => Kind == LayerEditorSourceKind.Webcam;
    public bool IsWindow => Kind == LayerEditorSourceKind.Window;
    public bool IsNormalBlend => string.Equals(BlendMode, "Normal", StringComparison.OrdinalIgnoreCase);
    public bool IsVideo =>
        Kind == LayerEditorSourceKind.VideoSequence ||
        Kind == LayerEditorSourceKind.Youtube ||
        (Kind == LayerEditorSourceKind.File && !string.IsNullOrWhiteSpace(FilePath) && 
            (FileCaptureService.IsVideoPath(FilePath) || FilePath.StartsWith("youtube:")));
    public bool VideoSeekAvailable => VideoPlaybackDurationSeconds > 0.001;
    public string VideoPlaybackToggleLabel => VideoPlaybackPaused ? "Play" : "Pause";
    public string VideoPlaybackTimeLabel => $"{FormatPlaybackTime(VideoPlaybackPositionSeconds)} / {FormatPlaybackTime(VideoPlaybackDurationSeconds)}";

    public string KindLabel => Kind switch
    {
        LayerEditorSourceKind.Webcam => "Camera",
        LayerEditorSourceKind.File => "File",
        LayerEditorSourceKind.Youtube => "YouTube",
        LayerEditorSourceKind.VideoSequence => "Video Sequence",
        LayerEditorSourceKind.Group => "Group",
        LayerEditorSourceKind.Window => "Window",
        _ => "Source"
    };

    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName) ? KindLabel : $"{KindLabel}: {DisplayName}";

    public string TreeLabel => string.IsNullOrWhiteSpace(DisplayName) ? KindLabel : DisplayName;

    public string Details => Kind switch
    {
        LayerEditorSourceKind.Window => string.IsNullOrWhiteSpace(WindowTitle) ? "Window source" : WindowTitle,
        LayerEditorSourceKind.Webcam => string.IsNullOrWhiteSpace(WebcamId) ? "Webcam source" : $"Id: {WebcamId}",
        LayerEditorSourceKind.File => string.IsNullOrWhiteSpace(FilePath) ? "File source" : FilePath,
        LayerEditorSourceKind.Youtube => string.IsNullOrWhiteSpace(FilePath) ? "YouTube source" : FilePath,
        LayerEditorSourceKind.VideoSequence => FilePaths.Count > 0 ? $"{FilePaths.Count} files" : "Video sequence",
        LayerEditorSourceKind.Group => "Layer group",
        _ => string.Empty
    };

    public IReadOnlyList<LayerEditorOption> BlendModeOptions => LayerEditorOptions.BlendModes;
    public IReadOnlyList<LayerEditorOption> FitModeOptions => LayerEditorOptions.FitModes;
    public IReadOnlyList<LayerEditorOption> AnimationTypeOptions => LayerEditorOptions.AnimationTypes;
    public IReadOnlyList<LayerEditorOption> TranslateDirectionOptions => LayerEditorOptions.TranslateDirections;
    public IReadOnlyList<LayerEditorOption> RotationDirectionOptions => LayerEditorOptions.RotationDirections;

    private static string FormatPlaybackTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return "0:00";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalHours >= 1)
        {
            return duration.ToString(@"h\:mm\:ss");
        }

        return duration.ToString(@"m\:ss");
    }
}

internal sealed class LayerEditorViewModel : LayerEditorNotify
{
    private bool _liveMode = true;
    private bool _sourceAudioMasterEnabled = true;
    private double _sourceAudioMasterVolume = 1.0;
    private int _simulationHeight = 144;
    private int _simulationDepth = 24;
    private double _simulationFramerate = 60.0;
    private double _globalSimulationLifeOpacity = 1.0;
    private ObservableCollection<LayerEditorSimulationLayer> _simulationLayers = new();
    private LayerEditorSimulationLayer? _selectedSimulationLayer;
    private ObservableCollection<LayerEditorSource> _sources = new();
    private LayerEditorSource? _selectedSource;

    public bool LiveMode
    {
        get => _liveMode;
        set => SetField(ref _liveMode, value);
    }

    public bool SourceAudioMasterEnabled
    {
        get => _sourceAudioMasterEnabled;
        set => SetField(ref _sourceAudioMasterEnabled, value);
    }

    public double SourceAudioMasterVolume
    {
        get => _sourceAudioMasterVolume;
        set => SetField(ref _sourceAudioMasterVolume, value);
    }

    public int SimulationHeight
    {
        get => _simulationHeight;
        set => SetField(ref _simulationHeight, value);
    }

    public int SimulationDepth
    {
        get => _simulationDepth;
        set => SetField(ref _simulationDepth, value);
    }

    public double SimulationFramerate
    {
        get => _simulationFramerate;
        set => SetField(ref _simulationFramerate, value);
    }

    public double GlobalSimulationLifeOpacity
    {
        get => _globalSimulationLifeOpacity;
        set => SetField(ref _globalSimulationLifeOpacity, value);
    }

    public ObservableCollection<LayerEditorSimulationLayer> SimulationLayers
    {
        get => _simulationLayers;
        set => SetField(ref _simulationLayers, value);
    }

    public LayerEditorSimulationLayer? SelectedSimulationLayer
    {
        get => _selectedSimulationLayer;
        set => SetField(ref _selectedSimulationLayer, value);
    }

    public ObservableCollection<LayerEditorSource> Sources
    {
        get => _sources;
        set => SetField(ref _sources, value);
    }

    public LayerEditorSource? SelectedSource
    {
        get => _selectedSource;
        set => SetField(ref _selectedSource, value);
    }

    public IReadOnlyList<int> SimulationHeightOptions => LayerEditorOptions.SimulationHeightPresets;
    public IReadOnlyList<LayerEditorOption> BlendModeOptions => LayerEditorOptions.BlendModes;
}

internal sealed class LayerEditorProjectSettings
{
    public int Height { get; set; } = 144;
    public int Depth { get; set; } = 24;
    public double Framerate { get; set; } = 60;
    public double LifeOpacity { get; set; } = 1.0;
    public double RgbHueShiftDegrees { get; set; }
    public double RgbHueShiftSpeedDegreesPerSecond { get; set; }
    public bool InvertComposite { get; set; }
    public bool Passthrough { get; set; }
    public string CompositeBlendMode { get; set; } = "Additive";
}

internal sealed class LayerEditorSimulationLayer : LayerEditorNotify
{
    private Guid _id;
    private string _name = "Simulation Layer";
    private bool _enabled = true;
    private string _inputFunction = "Direct";
    private string _blendMode = "Subtractive";
    private string _injectionMode = "Threshold";
    private string _lifeMode = "NaiveGrayscale";
    private string _binningMode = "Fill";
    private double _injectionNoise;
    private double _lifeOpacity = 1.0;
    private double _thresholdMin = 0.35;
    private double _thresholdMax = 0.75;
    private bool _invertThreshold;
    private bool _isExpanded = true;
    private bool _isSelected;

    public Guid Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value))
            {
                OnPropertyChanged(nameof(TreeLabel));
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetField(ref _enabled, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public string InputFunction
    {
        get => _inputFunction;
        set
        {
            if (SetField(ref _inputFunction, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public string BlendMode
    {
        get => _blendMode;
        set
        {
            if (SetField(ref _blendMode, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public string InjectionMode
    {
        get => _injectionMode;
        set
        {
            if (SetField(ref _injectionMode, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public string LifeMode
    {
        get => _lifeMode;
        set
        {
            if (SetField(ref _lifeMode, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public string BinningMode
    {
        get => _binningMode;
        set
        {
            if (SetField(ref _binningMode, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public double InjectionNoise
    {
        get => _injectionNoise;
        set
        {
            if (SetField(ref _injectionNoise, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public double LifeOpacity
    {
        get => _lifeOpacity;
        set
        {
            if (SetField(ref _lifeOpacity, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public double ThresholdMin
    {
        get => _thresholdMin;
        set
        {
            if (SetField(ref _thresholdMin, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public double ThresholdMax
    {
        get => _thresholdMax;
        set
        {
            if (SetField(ref _thresholdMax, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public bool InvertThreshold
    {
        get => _invertThreshold;
        set
        {
            if (SetField(ref _invertThreshold, value))
            {
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string TreeLabel => string.IsNullOrWhiteSpace(Name) ? "Simulation Layer" : Name;

    public string Details => $"{(Enabled ? "Enabled" : "Disabled")} | {InputFunction} | {BlendMode} | {LifeMode} | {BinningMode} | Noise {InjectionNoise:P0} | Opacity {LifeOpacity:P0} | {InjectionMode} | Th {ThresholdMin:P0}-{ThresholdMax:P0}{(InvertThreshold ? " inv" : string.Empty)}";

    public IReadOnlyList<LayerEditorOption> BlendModeOptions => LayerEditorOptions.BlendModes;
    public IReadOnlyList<LayerEditorOption> InputFunctionOptions => LayerEditorOptions.SimulationInputFunctions;
    public IReadOnlyList<LayerEditorOption> InjectionModeOptions => LayerEditorOptions.SimulationInjectionModes;
    public IReadOnlyList<LayerEditorOption> LifeModeOptions => LayerEditorOptions.SimulationLifeModes;
    public IReadOnlyList<LayerEditorOption> BinningModeOptions => LayerEditorOptions.SimulationBinningModes;
}
