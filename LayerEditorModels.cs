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

    public double BeatsPerCycle
    {
        get => _beatsPerCycle;
        set => SetField(ref _beatsPerCycle, value);
    }

    public bool IsTranslate => string.Equals(Type, "Translate", StringComparison.OrdinalIgnoreCase);
    public bool IsRotate => string.Equals(Type, "Rotate", StringComparison.OrdinalIgnoreCase);
    public bool IsDvd => string.Equals(Type, "DvdBounce", StringComparison.OrdinalIgnoreCase);
    public bool IsBeatShake => string.Equals(Type, "BeatShake", StringComparison.OrdinalIgnoreCase);

    public LayerEditorSource? Parent { get; set; }

    public string DisplayName => Type switch
    {
        "ZoomIn" => "Zoom In",
        "DvdBounce" => "DVD Bounce",
        "BeatShake" => "Beat Shake",
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
    private bool _mirror;
    private string _pendingTranslateDirection = "Right";
    private string _pendingRotationDirection = "Clockwise";

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
        set => SetField(ref _blendMode, value);
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

    public bool Mirror
    {
        get => _mirror;
        set => SetField(ref _mirror, value);
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

    public bool IsGroup => Kind == LayerEditorSourceKind.Group;
    public bool IsWebcam => Kind == LayerEditorSourceKind.Webcam;
    public bool IsWindow => Kind == LayerEditorSourceKind.Window;
    public bool IsVideo =>
        Kind == LayerEditorSourceKind.VideoSequence ||
        (Kind == LayerEditorSourceKind.File && !string.IsNullOrWhiteSpace(FilePath) && 
            (FileCaptureService.IsVideoPath(FilePath) || FilePath.StartsWith("youtube:")));

    public string KindLabel => Kind switch
    {
        LayerEditorSourceKind.Webcam => "Camera",
        LayerEditorSourceKind.File => "File",
        LayerEditorSourceKind.VideoSequence => "Video Sequence",
        LayerEditorSourceKind.Group => "Group",
        LayerEditorSourceKind.Window => "Window",
        _ => "Source"
    };

    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName) ? KindLabel : $"{KindLabel}: {DisplayName}";

    public string Details => Kind switch
    {
        LayerEditorSourceKind.Window => string.IsNullOrWhiteSpace(WindowTitle) ? "Window source" : WindowTitle,
        LayerEditorSourceKind.Webcam => string.IsNullOrWhiteSpace(WebcamId) ? "Webcam source" : $"Id: {WebcamId}",
        LayerEditorSourceKind.File => string.IsNullOrWhiteSpace(FilePath) ? "File source" : FilePath,
        LayerEditorSourceKind.VideoSequence => FilePaths.Count > 0 ? $"{FilePaths.Count} files" : "Video sequence",
        LayerEditorSourceKind.Group => "Layer group",
        _ => string.Empty
    };

    public IReadOnlyList<LayerEditorOption> BlendModeOptions => LayerEditorOptions.BlendModes;
    public IReadOnlyList<LayerEditorOption> FitModeOptions => LayerEditorOptions.FitModes;
    public IReadOnlyList<LayerEditorOption> AnimationTypeOptions => LayerEditorOptions.AnimationTypes;
    public IReadOnlyList<LayerEditorOption> TranslateDirectionOptions => LayerEditorOptions.TranslateDirections;
    public IReadOnlyList<LayerEditorOption> RotationDirectionOptions => LayerEditorOptions.RotationDirections;
}

internal sealed class LayerEditorViewModel : LayerEditorNotify
{
    private bool _liveMode = true;
    private ObservableCollection<LayerEditorSource> _sources = new();

    public bool LiveMode
    {
        get => _liveMode;
        set => SetField(ref _liveMode, value);
    }

    public ObservableCollection<LayerEditorSource> Sources
    {
        get => _sources;
        set => SetField(ref _sources, value);
    }
}
