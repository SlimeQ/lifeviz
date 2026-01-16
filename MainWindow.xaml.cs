using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Windows.Devices.Enumeration;

namespace lifeviz;

public partial class MainWindow : Window
{
    private const int DefaultRows = 144;
    private const int MinRows = 72;
    private const int MaxRows = 2160;
    private const int DefaultDepth = 24;
    private const double DefaultAspectRatio = 16d / 9d;
    private const double DefaultFps = 60;

    private readonly GameOfLifeEngine _engine = new();
    private readonly WindowCaptureService _windowCapture = new();
    private readonly WebcamCaptureService _webcamCapture = new();
    private readonly FileCaptureService _fileCapture = new();
    private readonly AudioBeatDetector _audioBeatDetector = new();
    private readonly BlendEffect _blendEffect = new();
    private int _configuredRows = DefaultRows;
    private int _configuredDepth = DefaultDepth;
    private int? _pendingLegacyColumns;
    private bool _suppressWindowResize;
    private Size _lastWindowSize;
    private Size _lastClientSize;
    private IReadOnlyList<WindowHandleInfo> _cachedWindows = Array.Empty<WindowHandleInfo>();
    private IReadOnlyList<WebcamCaptureService.CameraInfo> _cachedCameras = Array.Empty<WebcamCaptureService.CameraInfo>();
    private IReadOnlyList<AudioBeatDetector.AudioDeviceInfo> _cachedAudioDevices = Array.Empty<AudioBeatDetector.AudioDeviceInfo>();
    private readonly List<CaptureSource> _sources = new();
    private WriteableBitmap? _bitmap;
    private byte[]? _pixelBuffer;
    private WriteableBitmap? _underlayBitmap;
    private ImageBrush? _overlayBrush;
    private ImageBrush? _inputBrush;
    private byte[]? _engineColorBuffer;
    private byte[]? _compositeDownscaledBuffer;
    private byte[]? _compositeHighResBuffer;
    private byte[]? _invertScratchBuffer;
    private CompositeFrame? _lastCompositeFrame;
    private int _displayWidth;
    private int _displayHeight;
    private int[] _rowMap = Array.Empty<int>();
    private int[] _colMap = Array.Empty<int>();
    private bool _isPaused;
    private double _currentAspectRatio = DefaultAspectRatio;
    private bool _aspectRatioLocked;
    private double _lockedAspectRatio = DefaultAspectRatio;
    private IntPtr _windowHandle;
    private bool _passthroughEnabled;
    private bool _preserveResolution;
    private BlendMode _blendMode = BlendMode.Additive;
    private double _lifeOpacity = 1.0;
    private bool _invertComposite;
    private bool _showFps;
    private readonly Stopwatch _simulationFpsStopwatch = new();
    private int _simulationFrames;
    private double _displayFps;
    private GameOfLifeEngine.LifeMode _lifeMode = GameOfLifeEngine.LifeMode.NaiveGrayscale;
    private GameOfLifeEngine.BinningMode _binningMode = GameOfLifeEngine.BinningMode.Fill;
    private GameOfLifeEngine.InjectionMode _injectionMode = GameOfLifeEngine.InjectionMode.Threshold;
    private double _currentFps = DefaultFps;
    private double _captureThresholdMin = 0.35;
    private double _captureThresholdMax = 0.75;
    private bool _invertThreshold;
    private double _injectionNoise = 0.0;
    private int _pulseStep;
    private bool _webcamErrorShown;
    private bool _configReady;
    private bool _isFullscreen;
    private bool _pendingFullscreen;
    private WindowState _previousWindowState = WindowState.Normal;
    private WindowStyle _previousWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _previousResizeMode = ResizeMode.CanResize;
    private bool _previousTopmost;
    private Rect _previousBounds;
    private readonly Stopwatch _stepStopwatch = new();
    private readonly Stopwatch _lifetimeStopwatch = new();
    private double _timeSinceLastStep;
    private bool _fpsOscillationEnabled;
    private bool _audioSyncEnabled;
    private string? _selectedAudioDeviceId;
    private double _oscillationBpm = 140;
    private double _oscillationMinFps = 30;
    private double _oscillationMaxFps = 60;

    public MainWindow()
    {
        Logger.Initialize();
        InitializeComponent();

        Loaded += (_, _) =>
        {
            LoadConfig();
            InitializeVisualizer();
            if (_pendingFullscreen)
            {
                EnterFullscreen(applyConfig: true);
            }
            _lastWindowSize = new Size(ActualWidth, ActualHeight);
            _lastClientSize = new Size(Root.ActualWidth, Root.ActualHeight);
            Logger.Info("Main window loaded and visualizer initialized.");
        };
        SourceInitialized += (_, _) =>
        {
            var helper = new WindowInteropHelper(this);
            _windowHandle = helper.Handle;
        };
        Closed += (_, _) =>
        {
            _webcamCapture.Reset();
            _fileCapture.Dispose();
            _audioBeatDetector.Dispose();
            Logger.Shutdown();
        };
    }

    private void InitializeVisualizer()
    {
        _currentAspectRatio = _aspectRatioLocked
            ? _lockedAspectRatio
            : (_sources.Count > 0 ? _sources[0].AspectRatio : DefaultAspectRatio);
        _engine.Configure(_configuredRows, _configuredDepth, _currentAspectRatio);
        SnapWindowToAspect(preserveHeight: true);
        _engine.SetMode(_lifeMode);
        _engine.SetBinningMode(_binningMode);
        _engine.SetInjectionMode(_injectionMode);
        _engine.Randomize();
        UpdateDisplaySurface(force: true);
        InitializeEffect();
        UpdateFpsOverlay();
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        _stepStopwatch.Start();
        _lifetimeStopwatch.Start();
    }

    private double _oscillationPhase;
    // We need to track previous time to calculate delta time for phase.
    private double _lastRenderTime;

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        double now = _lifetimeStopwatch.Elapsed.TotalSeconds;
        double dt = now - _lastRenderTime;
        _lastRenderTime = now;

        // --- FPS Modulation ---
        if (_fpsOscillationEnabled)
        {
            double bpm = _oscillationBpm;
            if (_audioSyncEnabled)
            {
                bpm = _audioBeatDetector.CurrentBpm;
            }

            // Clamp BPM to reasonable range to avoid crazy frequencies
            bpm = Math.Clamp(bpm, 10, 300);
            
            double frequency = bpm / 60.0;
            
            if (_audioSyncEnabled)
            {
                 // Sync phase to beat: Peak at beat.
                 // Sin wave peaks at 0.25 (PI/2).
                 // So we want Phase = 0.25 when TimeSinceBeat = 0.
                 double timeSinceBeat = (DateTime.UtcNow - _audioBeatDetector.LastBeatTime).TotalSeconds;
                 
                 // Handle potentially large numbers or negative drift
                 if (timeSinceBeat < 0) timeSinceBeat = 0;

                 _oscillationPhase = (timeSinceBeat * frequency + 0.25) % 1.0;
            }
            else
            {
                // Clamp dt to avoid huge jumps if simulation lags
                double safeDt = Math.Min(dt, 0.1); 
            
                _oscillationPhase += frequency * safeDt;
                _oscillationPhase %= 1.0; // Safe wrapping
                if (_oscillationPhase < 0) _oscillationPhase += 1.0;
            }

            double factor = (Math.Sin(2 * Math.PI * _oscillationPhase) + 1.0) / 2.0;
            double targetFps = _oscillationMinFps + (_oscillationMaxFps - _oscillationMinFps) * factor;
            
            _currentFps = Math.Clamp(targetFps, 1, 144);
        }
        else
        {
             // Ensure we stay at config FPS if disabled (redundant safety)
             if (Math.Abs(_currentFps - _currentFpsFromConfig) > 0.1)
             {
                 _currentFps = _currentFpsFromConfig;
             }
        }

        // --- Simulation Step ---
        double elapsed = _stepStopwatch.Elapsed.TotalSeconds;
        _stepStopwatch.Restart();
        _timeSinceLastStep += elapsed;

        double desiredInterval = 1.0 / _currentFps;

        if (_timeSinceLastStep >= desiredInterval)
        {
            if (!_isPaused)
            {
                InjectCaptureFrames();
                _engine.Step();
                _simulationFrames++;
            }
            _timeSinceLastStep -= desiredInterval;
        }

        // --- Rendering Step ---
        RenderFrame();

        // --- FPS Counter Update ---
        if (!_simulationFpsStopwatch.IsRunning)
        {
            _simulationFpsStopwatch.Start();
        }
        else if (_simulationFpsStopwatch.ElapsedMilliseconds >= 500)
        {
            double seconds = _simulationFpsStopwatch.Elapsed.TotalSeconds;
            if (seconds > 0)
            {
                _displayFps = _simulationFrames / seconds;
            }
            _simulationFrames = 0;
            _simulationFpsStopwatch.Restart();
        }
        UpdateFpsOverlay();
    }

    private void RebuildSurface() => UpdateDisplaySurface(force: true);

    private void RenderFrame()
    {
        if (_bitmap == null || _pixelBuffer == null)
        {
            return;
        }

        int width = _bitmap.PixelWidth;
        int height = _bitmap.PixelHeight;
        int stride = width * 4;
        int requiredLength = stride * height;

        if (_pixelBuffer.Length != requiredLength)
        {
            _pixelBuffer = new byte[requiredLength];
        }

        int engineRows = _engine.Rows;
        int engineCols = _engine.Columns;
        BuildMappings(width, height, engineCols, engineRows);

        EnsureEngineColorBuffer();
        var engineColorBuffer = _engineColorBuffer;
        if (engineColorBuffer == null)
        {
            return;
        }

        Parallel.For(0, height, row =>
        {
            int sourceRow = _rowMap[row];
            for (int col = 0; col < width; col++)
            {
                int sourceCol = _colMap[col];
                int sourceIndex = (sourceRow * engineCols + sourceCol) * 4;
                byte r = engineColorBuffer[sourceIndex];
                byte g = engineColorBuffer[sourceIndex + 1];
                byte b = engineColorBuffer[sourceIndex + 2];
                int index = (row * stride) + (col * 4);
                _pixelBuffer[index] = b;
                _pixelBuffer[index + 1] = g;
                _pixelBuffer[index + 2] = r;
                _pixelBuffer[index + 3] = 255;
            }
        });

        if (_invertComposite)
        {
            InvertBuffer(_pixelBuffer);
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _pixelBuffer, stride, 0);
        UpdateUnderlayBitmap(requiredLength);
        UpdateEffectInput();
    }

    private void TogglePause_Click(object sender, RoutedEventArgs e)
    {
        _isPaused = !_isPaused;
        PauseMenuItem.Header = _isPaused ? "Resume Simulation" : "Pause Simulation";
    }

    private void Randomize_Click(object sender, RoutedEventArgs e)
    {
        _engine.Randomize();
        RenderFrame();
    }

    private void PresetHeight_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Header: string header } && int.TryParse(header, out int value))
        {
            ApplyDimensions(value, null);
        }
    }

    private void PresetDepth_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Header: string header } && int.TryParse(header, out int value))
        {
            ApplyDimensions(null, value);
        }
    }

    private void SetHeight_Click(object sender, RoutedEventArgs e)
    {
        int? requested = PromptForInteger("Height", _engine.Rows, MinRows, MaxRows);
        if (requested.HasValue)
        {
            ApplyDimensions(requested.Value, null);
        }
    }

    private void SetDepth_Click(object sender, RoutedEventArgs e)
    {
        int? requested = PromptForInteger("Depth", _engine.Depth, 3, 96);
        if (requested.HasValue)
        {
            ApplyDimensions(null, requested.Value);
        }
    }

    private void ApplyDimensions(int? rows, int? depth, double? aspectOverride = null, bool persist = true)
    {
        int nextRows = rows ?? _configuredRows;
        int nextDepth = depth ?? _engine.Depth;
        double nextAspect = aspectOverride ?? _currentAspectRatio;
        if (_aspectRatioLocked)
        {
            nextAspect = _lockedAspectRatio;
        }
        _currentAspectRatio = nextAspect;

        bool wasPaused = _isPaused;
        _isPaused = true;

        _configuredRows = Math.Clamp(nextRows, MinRows, MaxRows);
        _configuredDepth = Math.Clamp(nextDepth, 3, 96);
        _engine.Configure(_configuredRows, _configuredDepth, _currentAspectRatio);
        _configuredRows = _engine.Rows;
        SnapWindowToAspect(preserveHeight: true);
        RebuildSurface();

        _isPaused = wasPaused;
        PauseMenuItem.Header = _isPaused ? "Resume Simulation" : "Pause Simulation";
        if (persist)
        {
            SaveConfig();
        }
    }

    private int? PromptForInteger(string label, int current, int min, int max)
    {
        var dialog = new Window
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(16, 16, 16)),
            Foreground = Brushes.White,
            ShowInTaskbar = false,
            Title = label
        };

        var layout = new StackPanel
        {
            Margin = new Thickness(16),
            Width = 260
        };

        var message = new TextBlock
        {
            Text = $"Enter {label} ({min}-{max})",
            Margin = new Thickness(0, 0, 0, 8)
        };

        var input = new TextBox
        {
            Text = current.ToString(CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var error = new TextBlock
        {
            Foreground = Brushes.IndianRed,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        int? result = null;

        var okButton = new Button
        {
            Content = "OK",
            IsDefault = true,
            Width = 70,
            Margin = new Thickness(0, 0, 8, 0)
        };
        okButton.Click += (_, _) =>
        {
            if (int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                parsed = Math.Clamp(parsed, min, max);
                result = parsed;
                dialog.DialogResult = true;
            }
            else
            {
                error.Text = "Please enter a number.";
                error.Visibility = Visibility.Visible;
            }
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Width = 70
        };

        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        layout.Children.Add(message);
        layout.Children.Add(input);
        layout.Children.Add(error);
        layout.Children.Add(buttons);

        dialog.Content = layout;

        bool? dialogResult = dialog.ShowDialog();
        return dialogResult == true ? result : null;
    }

    private void RootContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        PopulateSourcesMenu();
        PopulateAudioMenu();
        if (PassthroughMenuItem != null)
        {
            PassthroughMenuItem.IsChecked = _passthroughEnabled;
            PassthroughMenuItem.IsEnabled = _sources.Count > 0;
        }
        if (PreserveResolutionMenuItem != null)
        {
            PreserveResolutionMenuItem.IsChecked = _preserveResolution;
            PreserveResolutionMenuItem.IsEnabled = _sources.Count > 0;
        }

        if (BlendModeMenu != null)
        {
            BlendModeMenu.IsEnabled = _sources.Count > 0;
            UpdateBlendModeMenuChecks();
        }
        if (FullscreenMenuItem != null)
        {
            FullscreenMenuItem.IsChecked = _isFullscreen;
        }
        if (LifeOpacitySlider != null)
        {
            LifeOpacitySlider.Value = _lifeOpacity;
        }
        if (InvertCompositeMenuItem != null)
        {
            InvertCompositeMenuItem.IsChecked = _invertComposite;
        }
        if (AspectRatioLockMenuItem != null)
        {
            AspectRatioLockMenuItem.IsChecked = _aspectRatioLocked;
        }
        if (ShowFpsMenuItem != null)
        {
            ShowFpsMenuItem.IsChecked = _showFps;
        }

        UpdateFramerateMenuChecks();
        UpdateLifeModeMenuChecks();
        UpdateBinningModeMenuChecks();
        UpdateInjectionModeMenuChecks();
        if (ThresholdMinSlider != null && ThresholdMaxSlider != null)
        {
            ThresholdMinSlider.Value = _captureThresholdMin;
            ThresholdMaxSlider.Value = _captureThresholdMax;
        }
        if (NoiseSlider != null)
        {
            NoiseSlider.Value = _injectionNoise;
        }
        if (InvertThresholdCheckBox != null)
        {
            InvertThresholdCheckBox.IsChecked = _invertThreshold;
        }

        if (FpsOscillationCheckBox != null)
        {
            FpsOscillationCheckBox.IsChecked = _fpsOscillationEnabled;
        }
        if (AudioSyncCheckBox != null)
        {
            AudioSyncCheckBox.IsChecked = _audioSyncEnabled;
            AudioSyncCheckBox.IsEnabled = _fpsOscillationEnabled;
        }
        if (BpmSlider != null && BpmValueText != null)
        {
            BpmSlider.Value = _oscillationBpm;
            BpmValueText.Text = $"{_oscillationBpm:F0}";
            BpmSlider.IsEnabled = !_audioSyncEnabled;
        }
        if (MinFpsSlider != null && MinFpsValueText != null)
        {
            MinFpsSlider.Value = _oscillationMinFps;
            MinFpsValueText.Text = $"{_oscillationMinFps:F0}";
        }
        if (MaxFpsSlider != null && MaxFpsValueText != null)
        {
            MaxFpsSlider.Value = _oscillationMaxFps;
            MaxFpsValueText.Text = $"{_oscillationMaxFps:F0}";
        }
    }

    private async void PopulateAudioMenu()
    {
        if (AudioSourceMenu == null) return;
        AudioSourceMenu.Items.Clear();

        try
        {
            _cachedAudioDevices = await _audioBeatDetector.EnumerateAudioDevices();
        }
        catch
        {
            // Fallback
        }

        if (_cachedAudioDevices.Count == 0)
        {
            AudioSourceMenu.Items.Add(new MenuItem { Header = "No audio devices found", IsEnabled = false });
            return;
        }

        foreach (var device in _cachedAudioDevices)
        {
            var item = new MenuItem
            {
                Header = device.Name,
                Tag = device,
                IsCheckable = true,
                IsChecked = _selectedAudioDeviceId == device.Id
            };
            item.Click += AudioDeviceMenuItem_Click;
            AudioSourceMenu.Items.Add(item);
        }
    }

    private async void AudioDeviceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AudioBeatDetector.AudioDeviceInfo device }) return;

        if (_selectedAudioDeviceId == device.Id) return;

        _selectedAudioDeviceId = device.Id;
        await _audioBeatDetector.InitializeAsync(device.Id);
        SaveConfig();
    }

    private void PopulateSourcesMenu()
    {
        if (SourcesMenu == null)
        {
            return;
        }

        SourcesMenu.Items.Clear();

        _cachedWindows = _windowCapture.EnumerateWindows(_windowHandle);
        Logger.Info($"Enumerated windows: count={_cachedWindows.Count}");
        _cachedCameras = _webcamCapture.EnumerateCameras();
        Logger.Info($"Enumerated webcams: count={_cachedCameras.Count}");

        SourcesMenu.Items.Add(BuildAddLayerGroupMenuItem(null));
        SourcesMenu.Items.Add(BuildAddWindowMenuItem(null));
        SourcesMenu.Items.Add(BuildAddWebcamMenuItem(null));
        SourcesMenu.Items.Add(BuildAddFileMenuItem(null));
        SourcesMenu.Items.Add(new Separator());

        if (_sources.Count == 0)
        {
            SourcesMenu.Items.Add(new MenuItem
            {
                Header = "No active sources",
                IsEnabled = false
            });
            return;
        }

        for (int i = 0; i < _sources.Count; i++)
        {
            SourcesMenu.Items.Add(BuildSourceMenuItem(_sources[i], _sources, i, isTopLevel: true));
        }

        SourcesMenu.Items.Add(new Separator());
        var clearItem = new MenuItem
        {
            Header = "Remove All Sources",
            IsEnabled = _sources.Count > 0
        };
        clearItem.Click += (_, _) => ClearSources();
        SourcesMenu.Items.Add(clearItem);
    }

    private sealed class WindowAddTarget
    {
        public WindowAddTarget(WindowHandleInfo info, CaptureSource? parentGroup)
        {
            Info = info;
            ParentGroup = parentGroup;
        }

        public WindowHandleInfo Info { get; }
        public CaptureSource? ParentGroup { get; }
    }

    private sealed class WebcamAddTarget
    {
        public WebcamAddTarget(WebcamCaptureService.CameraInfo camera, CaptureSource? parentGroup)
        {
            Camera = camera;
            ParentGroup = parentGroup;
        }

        public WebcamCaptureService.CameraInfo Camera { get; }
        public CaptureSource? ParentGroup { get; }
    }

    private MenuItem BuildAddLayerGroupMenuItem(CaptureSource? parentGroup)
    {
        var addGroupItem = new MenuItem { Header = "Add Layer Group", Tag = parentGroup };
        addGroupItem.Click += AddLayerGroupMenuItem_Click;
        return addGroupItem;
    }

    private MenuItem BuildAddFileMenuItem(CaptureSource? parentGroup)
    {
        var addFileItem = new MenuItem { Header = "Add File Source...", Tag = parentGroup };
        addFileItem.Click += AddFileSourceMenuItem_Click;
        return addFileItem;
    }

    private MenuItem BuildAddWindowMenuItem(CaptureSource? parentGroup)
    {
        var addWindowMenu = new MenuItem { Header = "Add Window Source" };
        if (_cachedWindows.Count == 0)
        {
            addWindowMenu.Items.Add(new MenuItem
            {
                Header = "No windows detected",
                IsEnabled = false
            });
            return addWindowMenu;
        }

        foreach (var window in _cachedWindows)
        {
            bool alreadyAdded = ContainsWindowSource(window.Handle);
            var item = new MenuItem
            {
                Header = window.Title,
                Tag = new WindowAddTarget(window, parentGroup),
                IsCheckable = true,
                IsChecked = alreadyAdded
            };
            item.Click += AddWindowSourceMenuItem_Click;
            addWindowMenu.Items.Add(item);
        }

        return addWindowMenu;
    }

    private MenuItem BuildAddWebcamMenuItem(CaptureSource? parentGroup)
    {
        var addWebcamMenu = new MenuItem { Header = "Add Webcam Source" };
        if (_cachedCameras.Count == 0)
        {
            addWebcamMenu.Items.Add(new MenuItem
            {
                Header = "No webcams detected",
                IsEnabled = false
            });
            return addWebcamMenu;
        }

        foreach (var camera in _cachedCameras)
        {
            bool alreadyAdded = ContainsWebcamSource(camera.Id);
            var item = new MenuItem
            {
                Header = camera.Name,
                Tag = new WebcamAddTarget(camera, parentGroup),
                IsCheckable = true,
                IsChecked = alreadyAdded
            };
            item.Click += AddWebcamSourceMenuItem_Click;
            addWebcamMenu.Items.Add(item);
        }

        return addWebcamMenu;
    }

    private MenuItem BuildSourceMenuItem(CaptureSource source, List<CaptureSource> siblings, int index, bool isTopLevel)
    {
        string label = BuildSourceLabel(source, index, isTopLevel);
        var sourceItem = new MenuItem { Header = label, Tag = source };

        if (source.Type == CaptureSource.SourceType.Group)
        {
            for (int i = 0; i < source.Children.Count; i++)
            {
                sourceItem.Items.Add(BuildSourceMenuItem(source.Children[i], source.Children, i, isTopLevel: false));
            }

            if (source.Children.Count > 0)
            {
                sourceItem.Items.Add(new Separator());
            }

            sourceItem.Items.Add(BuildAddLayerGroupMenuItem(source));
            sourceItem.Items.Add(BuildAddWindowMenuItem(source));
            sourceItem.Items.Add(BuildAddWebcamMenuItem(source));
            sourceItem.Items.Add(BuildAddFileMenuItem(source));
            sourceItem.Items.Add(new Separator());
        }

        var blendMenu = new MenuItem { Header = "Blend Mode" };
        foreach (var mode in Enum.GetValues(typeof(BlendMode)).Cast<BlendMode>())
        {
            var blendItem = new MenuItem
            {
                Header = mode.ToString(),
                IsCheckable = true,
                IsChecked = source.BlendMode == mode,
                Tag = source
            };
            blendItem.Click += SourceBlendModeItem_Click;
            blendMenu.Items.Add(blendItem);
        }

        var fitMenu = new MenuItem { Header = "Fit Mode" };
        foreach (var fit in Enum.GetValues(typeof(FitMode)).Cast<FitMode>())
        {
            var fitItem = new MenuItem
            {
                Header = fit.ToString(),
                IsCheckable = true,
                IsChecked = source.FitMode == fit,
                Tag = source
            };
            fitItem.Click += SourceFitModeItem_Click;
            fitMenu.Items.Add(fitItem);
        }

        var primaryItem = new MenuItem
        {
            Header = "Make Primary (adopt aspect)",
            IsEnabled = index != 0
        };
        primaryItem.Click += (_, _) => MakePrimarySource(source);

        var moveUpItem = new MenuItem
        {
            Header = "Move Up",
            IsEnabled = index > 0
        };
        moveUpItem.Click += (_, _) => MoveSource(source, -1);

        var moveDownItem = new MenuItem
        {
            Header = "Move Down",
            IsEnabled = index < siblings.Count - 1
        };
        moveDownItem.Click += (_, _) => MoveSource(source, 1);

        var mirrorItem = new MenuItem
        {
            Header = "Mirror Webcam",
            IsCheckable = true,
            IsChecked = source.Mirror,
            IsEnabled = source.Type == CaptureSource.SourceType.Webcam
        };
        mirrorItem.Click += (_, _) =>
        {
            source.Mirror = !source.Mirror;
            Logger.Info($"Mirror toggled for {source.DisplayName}: {source.Mirror}");
            RenderFrame();
            SaveConfig();
        };

        var opacityItem = new MenuItem
        {
            Header = "Opacity",
            StaysOpenOnClick = true
        };
        var opacityValueItem = new MenuItem
        {
            Header = $"{source.Opacity:P0}",
            IsEnabled = false
        };
        var opacitySlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = Math.Clamp(source.Opacity, 0, 1),
            Width = 140,
            SmallChange = 0.05,
            LargeChange = 0.1,
            Margin = new Thickness(12, 4, 12, 8)
        };
        opacitySlider.ValueChanged += (_, args) =>
        {
            source.Opacity = Math.Clamp(args.NewValue, 0, 1);
            Logger.Info($"Source opacity changed: {source.DisplayName} ({source.Type}) = {source.Opacity:F2}");
            opacityValueItem.Header = $"{source.Opacity:P0}";
            RenderFrame();
            SaveConfig();
        };
        opacityItem.Items.Add(opacityValueItem);
        opacityItem.Items.Add(opacitySlider);

        var removeItem = new MenuItem
        {
            Header = "Remove"
        };
        removeItem.Click += (_, _) => RemoveSource(source);

        sourceItem.Items.Add(blendMenu);
        sourceItem.Items.Add(fitMenu);
        sourceItem.Items.Add(primaryItem);
        sourceItem.Items.Add(moveUpItem);
        sourceItem.Items.Add(moveDownItem);
        sourceItem.Items.Add(mirrorItem);
        sourceItem.Items.Add(opacityItem);
        sourceItem.Items.Add(new Separator());
        sourceItem.Items.Add(removeItem);

        return sourceItem;
    }

    private static string BuildSourceLabel(CaptureSource source, int index, bool isTopLevel)
    {
        string prefix = isTopLevel ? $"{index + 1}. " : string.Empty;
        return source.Type switch
        {
            CaptureSource.SourceType.Webcam => $"{prefix}Camera: {source.DisplayName}",
            CaptureSource.SourceType.File => $"{prefix}File: {source.DisplayName}",
            CaptureSource.SourceType.Group => $"{prefix}Group: {source.DisplayName}",
            _ => $"{prefix}{source.DisplayName}"
        };
    }

    private void AddWindowSourceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: WindowAddTarget target })
        {
            return;
        }

        Logger.Info($"Adding window source: {target.Info.Title}");
        AddOrPromoteWindowSource(target.Info, target.ParentGroup?.Children ?? _sources);
    }

    private void AddWebcamSourceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: WebcamAddTarget target })
        {
            return;
        }

        Logger.Info($"Adding webcam source: {target.Camera.Name}");
        AddOrPromoteWebcamSource(target.Camera, target.ParentGroup?.Children ?? _sources);
    }

    private void AddFileSourceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CaptureSource? parentGroup = null;
        if (sender is MenuItem { Tag: CaptureSource group })
        {
            parentGroup = group;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select Image, GIF, or Video",
            Filter = "Media Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg|Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Video Files|*.mp4;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg|All Files|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddOrPromoteFileSource(dialog.FileName, parentGroup?.Children ?? _sources);
        }
    }

    private void SourceBlendModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Header: string header, Tag: CaptureSource source })
        {
            return;
        }

        if (Enum.TryParse<BlendMode>(header, ignoreCase: true, out var mode))
        {
            source.BlendMode = mode;
            PopulateSourcesMenu();
            RenderFrame();
            SaveConfig();
        }
    }

    private void SourceFitModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Header: string header, Tag: CaptureSource source })
        {
            return;
        }

        if (Enum.TryParse<FitMode>(header, ignoreCase: true, out var fitMode))
        {
            source.FitMode = fitMode;
            PopulateSourcesMenu();
            RenderFrame();
            SaveConfig();
        }
    }

    private void AddLayerGroupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CaptureSource? parentGroup = null;
        if (sender is MenuItem { Tag: CaptureSource group })
        {
            parentGroup = group;
        }

        AddLayerGroup(parentGroup?.Children ?? _sources);
    }

    private void AddOrPromoteWindowSource(WindowHandleInfo info, List<CaptureSource> targetList)
    {
        var existing = FindSource(s => s.Type == CaptureSource.SourceType.Window && s.Window != null && s.Window.Handle == info.Handle);
        if (existing != null)
        {
            existing.Window = info;
            Logger.Info($"Updated existing window source: {info.Title}");
        }
        else
        {
            targetList.Add(CaptureSource.CreateWindow(info));
            Logger.Info($"Inserted new window source (appended): {info.Title}");
        }

        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
    }

    private void AddOrPromoteWebcamSource(WebcamCaptureService.CameraInfo camera, List<CaptureSource> targetList)
    {
        var existing = FindSource(s => s.Type == CaptureSource.SourceType.Webcam && string.Equals(s.WebcamId, camera.Id, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Logger.Info($"Updated existing webcam source: {camera.Name}");
        }
        else
        {
            targetList.Add(CaptureSource.CreateWebcam(camera.Id, camera.Name));
            Logger.Info($"Inserted new webcam source (appended): {camera.Name}");
        }

        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
        _webcamErrorShown = false;
    }

    private void AddOrPromoteFileSource(string path, List<CaptureSource> targetList)
    {
        if (!_fileCapture.TryGetOrAdd(path, out var info, out var error))
        {
            string message = error ?? "Unsupported file format.";
            MessageBox.Show(this, $"Failed to load file source:\n{message}", "File Source Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            Logger.Warn($"Failed to add file source: {path}. {message}");
            return;
        }

        var existing = FindSource(s => s.Type == CaptureSource.SourceType.File && s.FilePath != null &&
            string.Equals(s.FilePath, info.Path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Logger.Info($"File source already active: {info.Path}");
        }
        else
        {
            targetList.Add(CaptureSource.CreateFile(info.Path, info.DisplayName, info.Width, info.Height));
            Logger.Info($"Inserted new file source (appended): {info.Path}");
        }

        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
    }

    private void AddLayerGroup(List<CaptureSource> targetList)
    {
        targetList.Add(CaptureSource.CreateGroup());
        Logger.Info("Inserted new layer group.");
        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
    }

    private IEnumerable<CaptureSource> EnumerateSources(IEnumerable<CaptureSource> sources)
    {
        foreach (var source in sources)
        {
            yield return source;
            if (source.Type == CaptureSource.SourceType.Group)
            {
                foreach (var child in EnumerateSources(source.Children))
                {
                    yield return child;
                }
            }
        }
    }

    private CaptureSource? FindSource(Func<CaptureSource, bool> predicate) =>
        EnumerateSources(_sources).FirstOrDefault(predicate);

    private bool ContainsWindowSource(IntPtr handle) =>
        FindSource(s => s.Type == CaptureSource.SourceType.Window && s.Window != null && s.Window.Handle == handle) != null;

    private bool ContainsWebcamSource(string webcamId) =>
        FindSource(s => s.Type == CaptureSource.SourceType.Webcam && string.Equals(s.WebcamId, webcamId, StringComparison.OrdinalIgnoreCase)) != null;

    private static List<CaptureSource>? FindParentList(List<CaptureSource> sources, CaptureSource target)
    {
        if (sources.Contains(target))
        {
            return sources;
        }

        foreach (var source in sources)
        {
            if (source.Type == CaptureSource.SourceType.Group)
            {
                var parent = FindParentList(source.Children, target);
                if (parent != null)
                {
                    return parent;
                }
            }
        }

        return null;
    }

    private void CleanupSource(CaptureSource source)
    {
        if (source.Type == CaptureSource.SourceType.Group)
        {
            foreach (var child in source.Children.ToList())
            {
                CleanupSource(child);
            }
            source.Children.Clear();
            source.CompositeDownscaledBuffer = null;
            source.CompositeHighResBuffer = null;
            return;
        }

        if (source.Type == CaptureSource.SourceType.Webcam && source.WebcamId != null)
        {
            _webcamCapture.Reset(source.WebcamId);
            source.IsInitialized = false;
        }
        else if (source.Type == CaptureSource.SourceType.Window && source.Window != null)
        {
            _windowCapture.RemoveCache(source.Window.Handle);
        }
        else if (source.Type == CaptureSource.SourceType.File && source.FilePath != null)
        {
            _fileCapture.Remove(source.FilePath);
        }
    }

    private bool UpdatePrimaryAspectIfNeeded()
    {
        if (_aspectRatioLocked)
        {
            return false;
        }

        double target = _sources.Count > 0 ? _sources[0].AspectRatio : DefaultAspectRatio;
        if (Math.Abs(target - _currentAspectRatio) > 0.0001)
        {
            ApplyDimensions(null, null, target, persist: false);
            return true;
        }

        return false;
    }

    private void MakePrimarySource(CaptureSource source)
    {
        var parentList = FindParentList(_sources, source);
        if (parentList == null || parentList.Count == 0 || parentList[0] == source)
        {
            return;
        }

        parentList.Remove(source);
        parentList.Insert(0, source);
        Logger.Info($"Primary source set: {source.DisplayName} ({source.Type})");
        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
    }

    private void MoveSource(CaptureSource source, int delta)
    {
        var parentList = FindParentList(_sources, source);
        if (parentList == null)
        {
            return;
        }

        int index = parentList.IndexOf(source);
        if (index < 0)
        {
            return;
        }

        int next = Math.Clamp(index + delta, 0, parentList.Count - 1);
        if (next == index)
        {
            return;
        }

        parentList.RemoveAt(index);
        parentList.Insert(next, source);

        UpdatePrimaryAspectIfNeeded();
        SaveConfig();
    }

    private void RemoveSource(CaptureSource source)
    {
        var parentList = FindParentList(_sources, source);
        if (parentList == null)
        {
            return;
        }

        parentList.Remove(source);
        Logger.Info($"Removed source: {source.DisplayName} ({source.Type})");
        CleanupSource(source);

        if (_sources.Count == 0)
        {
            ClearSources();
            return;
        }

        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
    }

    private void ClearSources()
    {
        bool hadSources = _sources.Count > 0;

        foreach (var source in _sources.ToList())
        {
            CleanupSource(source);
        }

        _fileCapture.Clear();
        
        _sources.Clear();
        _lastCompositeFrame = null;
        _preserveResolution = false;
        _passthroughEnabled = false;
        
        Logger.Info("Cleared all sources; reset webcam capture.");
        if (PassthroughMenuItem != null)
        {
            PassthroughMenuItem.IsChecked = false;
        }
        if (PreserveResolutionMenuItem != null)
        {
            PreserveResolutionMenuItem.IsChecked = false;
        }
        UpdateDisplaySurface(force: true);
        if (_aspectRatioLocked)
        {
            _currentAspectRatio = _lockedAspectRatio;
            ApplyDimensions(null, null, _currentAspectRatio);
        }
        else if (Math.Abs(_currentAspectRatio - DefaultAspectRatio) > 0.0001)
        {
            _currentAspectRatio = DefaultAspectRatio;
            ApplyDimensions(null, null, _currentAspectRatio);
        }
        else if (hadSources)
        {
            RenderFrame();
        }
        SaveConfig();
    }

    private void InjectCaptureFrames()
    {
        if (_sources.Count == 0)
        {
            _lastCompositeFrame = null;
            return;
        }

        bool removedAny = CaptureSourceList(_sources);
        if (_sources.Count == 0)
        {
            ClearSources();
            return;
        }

        if (removedAny && UpdatePrimaryAspectIfNeeded())
        {
            _lastCompositeFrame = null;
            return;
        }

        var composite = BuildCompositeFrame(_sources, ref _compositeDownscaledBuffer, ref _compositeHighResBuffer, useEngineDimensions: true);
        if (composite == null)
        {
            _lastCompositeFrame = null;
            return;
        }

        _lastCompositeFrame = composite;
        UpdateDisplaySurface();

        if (_lifeMode == GameOfLifeEngine.LifeMode.NaiveGrayscale)
        {
            var grayMask = BuildLuminanceMask(composite.Downscaled, composite.DownscaledWidth, composite.DownscaledHeight, _captureThresholdMin, _captureThresholdMax, _invertThreshold, _injectionMode, _injectionNoise, _engine.Depth, _pulseStep);
            _engine.InjectFrame(grayMask);
        }
        else
        {
            var (rMask, gMask, bMask) = BuildChannelMasks(composite.Downscaled, composite.DownscaledWidth, composite.DownscaledHeight, _captureThresholdMin, _captureThresholdMax, _invertThreshold, _injectionMode, _injectionNoise, _engine.RDepth, _engine.GDepth, _engine.BDepth, _pulseStep);
            _engine.InjectRgbFrame(rMask, gMask, bMask);
        }

        _pulseStep++;
    }

    private bool CaptureSourceList(List<CaptureSource> sources)
    {
        bool removedAny = false;
        var removed = new List<CaptureSource>();

        foreach (var source in sources.ToList())
        {
            if (source.Type == CaptureSource.SourceType.Group)
            {
                if (source.Children.Count > 0)
                {
                    removedAny |= CaptureSourceList(source.Children);
                }

                var groupDownscaled = source.CompositeDownscaledBuffer;
                var groupHighRes = source.CompositeHighResBuffer;
                var groupComposite = BuildCompositeFrame(source.Children, ref groupDownscaled, ref groupHighRes, useEngineDimensions: false);
                source.CompositeDownscaledBuffer = groupDownscaled;
                source.CompositeHighResBuffer = groupHighRes;
                if (groupComposite != null)
                {
                    source.LastFrame = new SourceFrame(groupComposite.Downscaled, groupComposite.DownscaledWidth, groupComposite.DownscaledHeight,
                        groupComposite.HighRes, groupComposite.HighResWidth, groupComposite.HighResHeight);
                    source.HasError = false;
                    source.MissedFrames = 0;
                    if (!source.FirstFrameReceived)
                    {
                        source.FirstFrameReceived = true;
                        Logger.Info($"Layer group composite ready: {source.DisplayName}");
                    }
                }
                else
                {
                    source.LastFrame = null;
                }

                continue;
            }

            if (!source.IsInitialized && source.Type == CaptureSource.SourceType.Webcam && source.WebcamId != null)
            {
                var initialized = _webcamCapture.EnsureInitialized(source.WebcamId);
                source.IsInitialized = initialized;
                if (!initialized)
                {
                    Logger.Warn($"Failed to initialize {source.DisplayName}, it will be skipped.");
                    continue;
                }
            }

            SourceFrame? frame = null;
            try
            {
                if (source.Type == CaptureSource.SourceType.Window && source.Window != null)
                {
                    var windowFrame = _windowCapture.CaptureFrame(source.Window, _engine.Columns, _engine.Rows, source.FitMode, _preserveResolution);
                    if (windowFrame != null)
                    {
                        frame = new SourceFrame(windowFrame.OverlayDownscaled, windowFrame.DownscaledWidth, windowFrame.DownscaledHeight,
                            windowFrame.OverlaySource, windowFrame.SourceWidth, windowFrame.SourceHeight);
                        source.Window = source.Window.WithDimensions(windowFrame.SourceWidth, windowFrame.SourceHeight);
                        source.HasError = false;
                        source.MissedFrames = 0;
                        if (!source.FirstFrameReceived)
                        {
                            source.FirstFrameReceived = true;
                            Logger.Info($"Window frame acquired for {source.DisplayName}: {windowFrame.SourceWidth}x{windowFrame.SourceHeight}");
                        }
                    }
                }
                else if (source.Type == CaptureSource.SourceType.Webcam && !string.IsNullOrWhiteSpace(source.WebcamId))
                {
                    var webcamFrame = _webcamCapture.CaptureFrame(source.WebcamId, _engine.Columns, _engine.Rows, source.FitMode, _preserveResolution);
                    if (webcamFrame != null)
                    {
                        frame = new SourceFrame(webcamFrame.OverlayDownscaled, webcamFrame.DownscaledWidth, webcamFrame.DownscaledHeight,
                            webcamFrame.OverlaySource, webcamFrame.SourceWidth, webcamFrame.SourceHeight);
                        source.HasError = false;
                        source.MissedFrames = 0;
                        if (!source.FirstFrameReceived)
                        {
                            source.FirstFrameReceived = true;
                            Logger.Info($"Webcam frame acquired for {source.DisplayName}: {webcamFrame.SourceWidth}x{webcamFrame.SourceHeight}");
                        }
                    }
                }
                else if (source.Type == CaptureSource.SourceType.File && !string.IsNullOrWhiteSpace(source.FilePath))
                {
                    var fileFrame = _fileCapture.CaptureFrame(source.FilePath, _engine.Columns, _engine.Rows, source.FitMode, _preserveResolution);
                    if (fileFrame.HasValue)
                    {
                        var value = fileFrame.Value;
                        frame = new SourceFrame(value.OverlayDownscaled, value.DownscaledWidth, value.DownscaledHeight,
                            value.OverlaySource, value.SourceWidth, value.SourceHeight);
                        source.UpdateFileDimensions(value.SourceWidth, value.SourceHeight);
                        source.HasError = false;
                        source.MissedFrames = 0;
                        if (!source.FirstFrameReceived)
                        {
                            source.FirstFrameReceived = true;
                            Logger.Info($"File frame acquired for {source.DisplayName}: {value.SourceWidth}x{value.SourceHeight}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                source.HasError = true;
                if (!_webcamErrorShown && source.Type == CaptureSource.SourceType.Webcam)
                {
                    _webcamErrorShown = true;
                    MessageBox.Show(this, $"Failed to read from webcam \"{source.DisplayName}\": {ex.Message}", "Webcam Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Logger.Error($"Webcam error for {source.DisplayName}", ex);
                }
                else
                {
                    Logger.Error($"Capture error for source {source.DisplayName} ({source.Type})", ex);
                }
            }

            if (frame == null)
            {
                bool isWebcam = source.Type == CaptureSource.SourceType.Webcam;
                if (isWebcam && source.WebcamId != null)
                {
                    source.MissedFrames++;
                    var age = DateTime.UtcNow - source.AddedUtc;
                    if (!source.FirstFrameReceived && !source.RetryInitializationAttempted && source.MissedFrames >= 90)
                    {
                        Logger.Warn($"Webcam frames missing; retrying initialization for {source.DisplayName} after {source.MissedFrames} misses.");
                        _webcamCapture.Reset(source.WebcamId);
                        source.IsInitialized = false;
                        source.RetryInitializationAttempted = true;
                        source.MissedFrames = 0;
                        source.AddedUtc = DateTime.UtcNow;
                        continue;
                    }

                    if (source.MissedFrames <= 240 || age < TimeSpan.FromSeconds(12))
                    {
                        if (source.MissedFrames > 0 && source.MissedFrames % 60 == 0)
                        {
                            Logger.Warn($"Waiting for webcam frames from {source.DisplayName}; missed {source.MissedFrames} so far.");
                        }
                        continue;
                    }
                    Logger.Warn($"Webcam frames never arrived; removing source {source.DisplayName} after {source.MissedFrames} misses.");
                    removed.Add(source);
                    continue;
                }

                if (source.Type == CaptureSource.SourceType.Window)
                {
                    source.MissedFrames++;
                    var age = DateTime.UtcNow - source.AddedUtc;
                    if (!source.FirstFrameReceived && age < TimeSpan.FromSeconds(10))
                    {
                        continue;
                    }
                }

                if (source.Type == CaptureSource.SourceType.File)
                {
                    source.MissedFrames++;
                    var age = DateTime.UtcNow - source.AddedUtc;
                    if (!source.FirstFrameReceived && age < TimeSpan.FromSeconds(5))
                    {
                        continue;
                    }
                    if (source.MissedFrames <= 180 && age < TimeSpan.FromSeconds(10))
                    {
                        continue;
                    }
                }

                Logger.Warn($"Source frame missing; removing source {source.DisplayName} ({source.Type})");
                removed.Add(source);
                continue;
            }

            source.HasError = false;
            source.LastFrame = frame;
        }

        if (removed.Count > 0)
        {
            foreach (var source in removed)
            {
                sources.Remove(source);
                CleanupSource(source);
            }
            removedAny = true;
        }

        return removedAny;
    }

    private void TogglePassthrough_Click(object sender, RoutedEventArgs e)
    {
        _passthroughEnabled = !_passthroughEnabled;
        if (PassthroughMenuItem != null)
        {
            PassthroughMenuItem.IsChecked = _passthroughEnabled;
        }
        RenderFrame();
        SaveConfig();
    }

    private void ToggleAspectRatioLock_Click(object sender, RoutedEventArgs e)
    {
        _aspectRatioLocked = !_aspectRatioLocked;
        if (_aspectRatioLocked)
        {
            if (_lockedAspectRatio <= 0)
            {
                _lockedAspectRatio = DefaultAspectRatio;
            }
            ApplyDimensions(null, null, _lockedAspectRatio);
        }
        else
        {
            double target = _sources.Count > 0 ? _sources[0].AspectRatio : DefaultAspectRatio;
            ApplyDimensions(null, null, target);
        }

        if (AspectRatioLockMenuItem != null)
        {
            AspectRatioLockMenuItem.IsChecked = _aspectRatioLocked;
        }
        SaveConfig();
    }

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private void EnterFullscreen(bool applyConfig = false)
    {
        if (_isFullscreen)
        {
            return;
        }

        _previousWindowState = WindowState;
        _previousWindowStyle = WindowStyle;
        _previousResizeMode = ResizeMode;
        _previousTopmost = Topmost;
        _previousBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;

        var monitorBounds = GetCurrentMonitorBounds();
        Left = monitorBounds.Left;
        Top = monitorBounds.Top;
        Width = monitorBounds.Width;
        Height = monitorBounds.Height;
        _isFullscreen = true;
        if (!applyConfig)
        {
            SaveConfig();
        }
        UpdateFullscreenMenuItem();
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            return;
        }

        WindowState = WindowState.Normal;
        WindowStyle = _previousWindowStyle;
        ResizeMode = _previousResizeMode;
        Topmost = _previousTopmost;
        if (!_previousBounds.IsEmpty)
        {
            Left = _previousBounds.Left;
            Top = _previousBounds.Top;
            Width = _previousBounds.Width;
            Height = _previousBounds.Height;
        }

        var targetState = _previousWindowState == WindowState.Minimized ? WindowState.Normal : _previousWindowState;
        WindowState = targetState;
        _isFullscreen = false;
        SnapWindowToAspect(preserveHeight: true);
        SaveConfig();
        UpdateFullscreenMenuItem();
    }

    private Rect GetCurrentMonitorBounds()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
        double scaleY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;
        const int MonitorDefaultToNearest = 2;
        if (_windowHandle != IntPtr.Zero)
        {
            IntPtr monitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var info = new MonitorInfo
                {
                    cbSize = Marshal.SizeOf<MonitorInfo>()
                };
                if (GetMonitorInfo(monitor, ref info))
                {
                    return new Rect(
                        info.rcMonitor.left / scaleX,
                        info.rcMonitor.top / scaleY,
                        (info.rcMonitor.right - info.rcMonitor.left) / scaleX,
                        (info.rcMonitor.bottom - info.rcMonitor.top) / scaleY);
                }
            }
        }

        return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private void UpdateFullscreenMenuItem()
    {
        if (FullscreenMenuItem != null)
        {
            FullscreenMenuItem.IsChecked = _isFullscreen;
        }
    }

    private void ThresholdSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _captureThresholdMin = ThresholdMinSlider?.Value ?? _captureThresholdMin;
        _captureThresholdMax = ThresholdMaxSlider?.Value ?? _captureThresholdMax;
        SaveConfig();
    }

    private void NoiseSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _injectionNoise = Math.Clamp(e.NewValue, 0, 1);
        SaveConfig();
    }

    private void InvertThresholdCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        _invertThreshold = InvertThresholdCheckBox?.IsChecked == true;
        SaveConfig();
    }

    private static double ComputePwmSignal(double value, double min, double max, bool invert)
    {
        min = Math.Clamp(min, 0, 1);
        max = Math.Clamp(max, 0, 1);
        if (min > max)
        {
            (min, max) = (max, min);
        }

        if (value < min || value > max)
        {
            return 0;
        }
        double norm = (max > min) ? (value - min) / (max - min) : 1.0;
        norm = Math.Clamp(norm, 0, 1);
        if (invert)
        {
            return norm >= 0.5 ? 1.0 : 0.0;
        }
        return norm;
    }

    private static bool PulseWidthAlive(double value, int period, int pulseStep)
    {
        period = Math.Max(1, period);
        value = Math.Clamp(value, 0, 1);
        int aliveCount = (int)Math.Round(value * period);
        if (aliveCount <= 0)
        {
            return false;
        }
        if (aliveCount >= period)
        {
            return true;
        }

        int phase = pulseStep % period;
        // Evenly distribute alive slots across the period.
        // Inspired by Bresenham: alive if scaled phase wraps under aliveCount.
        return (phase * aliveCount) % period < aliveCount;
    }

    private static bool EvaluateThresholdValue(double value, double min, double max, bool invert)
    {
        min = Math.Clamp(min, 0, 1);
        max = Math.Clamp(max, 0, 1);
        if (min > max)
        {
            (min, max) = (max, min);
        }

        if (value < min) return false;
        if (value > max) return true;
        if (invert)
        {
            double mid = (min + max) / 2.0;
            return value >= mid;
        }
        return true;
    }

    private void FramerateItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Header: string header })
        {
            return;
        }

        if (header.StartsWith("15", StringComparison.OrdinalIgnoreCase))
        {
            SetFramerate(15);
        }
        else if (header.StartsWith("30", StringComparison.OrdinalIgnoreCase))
        {
            SetFramerate(30);
        }
        else if (header.StartsWith("60", StringComparison.OrdinalIgnoreCase))
        {
            SetFramerate(60);
        }
        else if (header.StartsWith("144", StringComparison.OrdinalIgnoreCase))
        {
            SetFramerate(144);
        }
    }

    private void TogglePreserveResolution_Click(object sender, RoutedEventArgs e)
    {
        _preserveResolution = !_preserveResolution;
        if (PreserveResolutionMenuItem != null)
        {
            PreserveResolutionMenuItem.IsChecked = _preserveResolution;
        }
        UpdateDisplaySurface(force: true);
        RenderFrame();
        SaveConfig();
    }

    private void BlendModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Header: string header })
        {
            return;
        }

        if (Enum.TryParse<BlendMode>(header, ignoreCase: true, out var mode))
        {
            _blendMode = mode;
            UpdateBlendModeMenuChecks();
            RenderFrame();
            SaveConfig();
        }
    }

    private void UpdateBlendModeMenuChecks()
    {
        if (BlendModeMenu == null)
        {
            return;
        }

        foreach (var item in BlendModeMenu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Header is string header &&
                Enum.TryParse<BlendMode>(header, ignoreCase: true, out var mode))
            {
                menuItem.IsCheckable = true;
                menuItem.IsChecked = mode == _blendMode;
            }
        }
    }

    private enum BlendMode
    {
        Additive,
        Normal,
        Multiply,
        Screen,
        Overlay,
        Lighten,
        Darken,
        Subtractive
    }

    private void UpdateDisplaySurface(bool force = false)
    {
        int targetWidth = _engine.Columns;
        int targetHeight = _engine.Rows;

        if (_preserveResolution && _sources.Count > 0)
        {
            if (_lastCompositeFrame?.HighRes != null && _lastCompositeFrame.HighResWidth > 0 && _lastCompositeFrame.HighResHeight > 0)
            {
                targetWidth = _lastCompositeFrame.HighResWidth;
                targetHeight = _lastCompositeFrame.HighResHeight;
            }
            else
            {
                var primary = _sources[0];
                targetWidth = primary.LastFrame?.SourceWidth ?? primary.FallbackWidth ?? _engine.Columns;
                targetHeight = primary.LastFrame?.SourceHeight ?? primary.FallbackHeight ?? _engine.Rows;
            }
        }

        if (targetWidth <= 0 || targetHeight <= 0)
        {
            targetWidth = _engine.Columns;
            targetHeight = _engine.Rows;
        }

        bool needsBitmap = _bitmap == null || force || _bitmap.PixelWidth != targetWidth || _bitmap.PixelHeight != targetHeight;
        if (needsBitmap)
        {
            _bitmap = new WriteableBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Bgra32, null);
            _pixelBuffer = new byte[targetWidth * targetHeight * 4];
            GameImage.Source = _bitmap;
        }
        else if (_pixelBuffer != null && _pixelBuffer.Length != targetWidth * targetHeight * 4)
        {
            _pixelBuffer = new byte[targetWidth * targetHeight * 4];
        }

        _displayWidth = targetWidth;
        _displayHeight = targetHeight;

        GameImage.Width = targetWidth;
        GameImage.Height = targetHeight;

        if (_underlayBitmap == null || _underlayBitmap.PixelWidth != targetWidth || _underlayBitmap.PixelHeight != targetHeight || force)
        {
            _underlayBitmap = new WriteableBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Bgra32, null);
            if (_overlayBrush != null)
            {
                _overlayBrush.ImageSource = _underlayBitmap;
            }
        }

        _rowMap = _displayHeight == _rowMap.Length ? _rowMap : new int[_displayHeight];
        _colMap = _displayWidth == _colMap.Length ? _colMap : new int[_displayWidth];
    }

    private void SnapWindowToAspect(bool preserveHeight)
    {
        if (_isFullscreen)
        {
            return;
        }

        double aspect = _currentAspectRatio > 0 ? _currentAspectRatio : DefaultAspectRatio;
        double clientWidth = Root.ActualWidth > 0 ? Root.ActualWidth : ActualWidth;
        double clientHeight = Root.ActualHeight > 0 ? Root.ActualHeight : ActualHeight;
        if (clientWidth <= 0 || clientHeight <= 0)
        {
            return;
        }

        double chromeWidth = Math.Max(0, ActualWidth - clientWidth);
        double chromeHeight = Math.Max(0, ActualHeight - clientHeight);

        _suppressWindowResize = true;
        if (preserveHeight)
        {
            double targetClientWidth = Math.Max(1, clientHeight * aspect);
            Width = targetClientWidth + chromeWidth;
        }
        else
        {
            double targetClientHeight = Math.Max(1, clientWidth / aspect);
            Height = targetClientHeight + chromeHeight;
        }
        _suppressWindowResize = false;
        _lastWindowSize = new Size(ActualWidth, ActualHeight);
        _lastClientSize = new Size(Root.ActualWidth, Root.ActualHeight);
    }

    private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_suppressWindowResize || _isFullscreen)
        {
            _lastWindowSize = new Size(ActualWidth, ActualHeight);
            _lastClientSize = new Size(Root.ActualWidth, Root.ActualHeight);
            return;
        }

        double aspect = _currentAspectRatio > 0 ? _currentAspectRatio : DefaultAspectRatio;
        double clientWidth = Root.ActualWidth > 0 ? Root.ActualWidth : e.NewSize.Width;
        double clientHeight = Root.ActualHeight > 0 ? Root.ActualHeight : e.NewSize.Height;
        if (clientWidth <= 0 || clientHeight <= 0)
        {
            return;
        }

        double deltaWidth = Math.Abs(clientWidth - _lastClientSize.Width);
        double deltaHeight = Math.Abs(clientHeight - _lastClientSize.Height);
        bool preserveHeight = deltaHeight >= deltaWidth;

        double chromeWidth = Math.Max(0, e.NewSize.Width - clientWidth);
        double chromeHeight = Math.Max(0, e.NewSize.Height - clientHeight);

        _suppressWindowResize = true;
        if (preserveHeight)
        {
            double targetClientWidth = Math.Max(1, clientHeight * aspect);
            Width = targetClientWidth + chromeWidth;
        }
        else
        {
            double targetClientHeight = Math.Max(1, clientWidth / aspect);
            Height = targetClientHeight + chromeHeight;
        }
        _suppressWindowResize = false;
        _lastWindowSize = new Size(ActualWidth, ActualHeight);
        _lastClientSize = new Size(Root.ActualWidth, Root.ActualHeight);
    }

    private static bool TryGetPrimaryDimensions(List<CaptureSource> sources, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (sources.Count == 0)
        {
            return false;
        }

        var primary = sources[0];
        if (primary.LastFrame != null && primary.LastFrame.SourceWidth > 0 && primary.LastFrame.SourceHeight > 0)
        {
            width = primary.LastFrame.SourceWidth;
            height = primary.LastFrame.SourceHeight;
            return true;
        }

        if (primary.FallbackWidth.HasValue && primary.FallbackHeight.HasValue && primary.FallbackWidth > 0 && primary.FallbackHeight > 0)
        {
            width = primary.FallbackWidth.Value;
            height = primary.FallbackHeight.Value;
            return true;
        }

        return false;
    }

    private bool TryGetDownscaledDimensions(List<CaptureSource> sources, bool useEngineDimensions, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (useEngineDimensions || sources.Count == 0)
        {
            width = _engine.Columns;
            height = _engine.Rows;
            return width > 0 && height > 0;
        }

        int maxWidth = _engine.Columns;
        int maxHeight = _engine.Rows;
        if (maxWidth <= 0 || maxHeight <= 0)
        {
            return false;
        }

        double aspect = sources[0].AspectRatio;
        if (aspect <= 0 || double.IsNaN(aspect) || double.IsInfinity(aspect))
        {
            width = maxWidth;
            height = maxHeight;
            return true;
        }

        double fitWidth = maxHeight * aspect;
        double fitHeight = maxWidth / aspect;
        if (fitWidth <= maxWidth)
        {
            width = (int)Math.Round(fitWidth);
            height = maxHeight;
        }
        else
        {
            width = maxWidth;
            height = (int)Math.Round(fitHeight);
        }

        width = Math.Clamp(width, 1, maxWidth);
        height = Math.Clamp(height, 1, maxHeight);
        return true;
    }

    private CompositeFrame? BuildCompositeFrame(List<CaptureSource> sources, ref byte[]? downscaledBuffer, ref byte[]? highResBuffer, bool useEngineDimensions)
    {
        if (sources.Count == 0)
        {
            return null;
        }

        if (!TryGetDownscaledDimensions(sources, useEngineDimensions, out int downscaledWidth, out int downscaledHeight))
        {
            return null;
        }
        int downscaledLength = downscaledWidth * downscaledHeight * 4;

        if (downscaledBuffer == null || downscaledBuffer.Length != downscaledLength)
        {
            downscaledBuffer = new byte[downscaledLength];
        }

        int targetWidth = downscaledWidth;
        int targetHeight = downscaledHeight;
        if (_preserveResolution && TryGetPrimaryDimensions(sources, out int primaryWidth, out int primaryHeight))
        {
            targetWidth = primaryWidth;
            targetHeight = primaryHeight;
        }

        if (targetWidth <= 0 || targetHeight <= 0)
        {
            targetWidth = downscaledWidth;
            targetHeight = downscaledHeight;
        }

        byte[]? highRes = null;
        if (_preserveResolution)
        {
            int highResLength = targetWidth * targetHeight * 4;
            if (highResBuffer == null || highResBuffer.Length != highResLength)
            {
                highResBuffer = new byte[highResLength];
            }

            highRes = highResBuffer;
        }

        bool wroteDownscaled = false;
        bool wroteHighRes = false;
        bool primedDownscaled = false;
        bool primedHighRes = false;

        foreach (var source in sources)
        {
            var frame = source.LastFrame;
            if (frame == null)
            {
                continue;
            }

            if (source.Type == CaptureSource.SourceType.Window && source.Window != null)
            {
                source.Window = source.Window.WithDimensions(frame.SourceWidth, frame.SourceHeight);
            }

            if (!primedDownscaled)
            {
                CopyIntoBuffer(downscaledBuffer, downscaledWidth, downscaledHeight,
                    frame.Downscaled, frame.DownscaledWidth, frame.DownscaledHeight, source.Opacity, source.Mirror && source.Type == CaptureSource.SourceType.Webcam, source.FitMode);
                primedDownscaled = true;
                wroteDownscaled = true;
            }
            else
            {
                CompositeIntoBuffer(downscaledBuffer, downscaledWidth, downscaledHeight,
                    frame.Downscaled, frame.DownscaledWidth, frame.DownscaledHeight, source.BlendMode, source.Opacity, source.Mirror && source.Type == CaptureSource.SourceType.Webcam, source.FitMode);
                wroteDownscaled = true;
            }

            if (highRes != null && frame.Source != null)
            {
                var sourceBuffer = frame.Source;
                int sourceWidth = frame.SourceWidth;
                int sourceHeight = frame.SourceHeight;

                if (!primedHighRes)
                {
                    CopyIntoBuffer(highRes, targetWidth, targetHeight, sourceBuffer, sourceWidth, sourceHeight, source.Opacity, source.Mirror && source.Type == CaptureSource.SourceType.Webcam, source.FitMode);
                    primedHighRes = true;
                    wroteHighRes = true;
                }
                else
                {
                    CompositeIntoBuffer(highRes, targetWidth, targetHeight, sourceBuffer, sourceWidth, sourceHeight, source.BlendMode, source.Opacity, source.Mirror && source.Type == CaptureSource.SourceType.Webcam, source.FitMode);
                    wroteHighRes = true;
                }
            }
        }

        if (!wroteDownscaled)
        {
            return null;
        }

        return new CompositeFrame(downscaledBuffer, downscaledWidth, downscaledHeight,
            wroteHighRes ? highRes : null, targetWidth, targetHeight);
    }

    private void CopyIntoBuffer(byte[] destination, int destWidth, int destHeight, byte[] source, int sourceWidth, int sourceHeight, double opacity, bool mirror, FitMode fitMode)
    {
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        int destStride = destWidth * 4;
        int sourceStride = sourceWidth * 4;
        var mapping = ImageFit.GetMapping(fitMode, sourceWidth, sourceHeight, destWidth, destHeight);

        Parallel.For(0, destHeight, row =>
        {
            int destRowOffset = row * destStride;
            for (int col = 0; col < destWidth; col++)
            {
                int destIndex = destRowOffset + (col * 4);
                byte sb = 0;
                byte sg = 0;
                byte sr = 0;
                if (ImageFit.TryMapPixel(mapping, col, row, out int srcX, out int srcY))
                {
                    if (mirror)
                    {
                        srcX = sourceWidth - 1 - srcX;
                    }
                    int srcIndex = (srcY * sourceStride) + (srcX * 4);
                    sb = source[srcIndex];
                    sg = source[srcIndex + 1];
                    sr = source[srcIndex + 2];
                }

                destination[destIndex] = ClampToByte((int)(sb * opacity));
                destination[destIndex + 1] = ClampToByte((int)(sg * opacity));
                destination[destIndex + 2] = ClampToByte((int)(sr * opacity));
                destination[destIndex + 3] = 255;
            }
        });
    }

    private void CompositeIntoBuffer(byte[] destination, int destWidth, int destHeight, byte[] source, int sourceWidth, int sourceHeight, BlendMode mode, double opacity, bool mirror, FitMode fitMode)
    {
        if (destination == null || source == null || destWidth <= 0 || destHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return;
        }

        int destLength = destWidth * destHeight * 4;
        int sourceLength = sourceWidth * sourceHeight * 4;
        if (destination.Length < destLength || source.Length < sourceLength)
        {
            return;
        }

        int destStride = destWidth * 4;
        int sourceStride = sourceWidth * 4;

        if (destWidth == sourceWidth && destHeight == sourceHeight)
        {
            Parallel.For(0, destHeight, row =>
            {
                int destRowOffset = row * destStride;
                int srcRowOffset = row * sourceStride;
                for (int col = 0; col < destWidth; col++)
                {
                    int destIndex = destRowOffset + (col * 4);
                    int sampleX = mirror ? (sourceWidth - 1 - col) : col;
                    int srcIndex = srcRowOffset + (sampleX * 4);
                    byte sb = source[srcIndex];
                    byte sg = source[srcIndex + 1];
                    byte sr = source[srcIndex + 2];
                    BlendInto(destination, destIndex, sb, sg, sr, mode, opacity);
                }
            });
            return;
        }

        var mapping = ImageFit.GetMapping(fitMode, sourceWidth, sourceHeight, destWidth, destHeight);
        Parallel.For(0, destHeight, row =>
        {
            int destRowOffset = row * destStride;
            for (int col = 0; col < destWidth; col++)
            {
                int destIndex = destRowOffset + (col * 4);
                byte sb = 0;
                byte sg = 0;
                byte sr = 0;
                if (ImageFit.TryMapPixel(mapping, col, row, out int srcX, out int srcY))
                {
                    if (mirror)
                    {
                        srcX = sourceWidth - 1 - srcX;
                    }
                    int srcIndex = (srcY * sourceStride) + (srcX * 4);
                    sb = source[srcIndex];
                    sg = source[srcIndex + 1];
                    sr = source[srcIndex + 2];
                }
                BlendInto(destination, destIndex, sb, sg, sr, mode, opacity);
            }
        });
    }

    private static void BlendInto(byte[] destination, int destIndex, byte sb, byte sg, byte sr, BlendMode mode, double opacity)
    {
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        byte db = destination[destIndex];
        byte dg = destination[destIndex + 1];
        byte dr = destination[destIndex + 2];

        int b;
        int g;
        int r;

        switch (mode)
        {
            case BlendMode.Additive:
                b = db + sb;
                g = dg + sg;
                r = dr + sr;
                break;
            case BlendMode.Multiply:
                b = db * sb / 255;
                g = dg * sg / 255;
                r = dr * sr / 255;
                break;
            case BlendMode.Screen:
                b = 255 - ((255 - db) * (255 - sb) / 255);
                g = 255 - ((255 - dg) * (255 - sg) / 255);
                r = 255 - ((255 - dr) * (255 - sr) / 255);
                break;
            case BlendMode.Overlay:
                b = db < 128 ? (2 * db * sb) / 255 : 255 - (2 * (255 - db) * (255 - sb) / 255);
                g = dg < 128 ? (2 * dg * sg) / 255 : 255 - (2 * (255 - dg) * (255 - sg) / 255);
                r = dr < 128 ? (2 * dr * sr) / 255 : 255 - (2 * (255 - dr) * (255 - sr) / 255);
                break;
            case BlendMode.Lighten:
                b = Math.Max(db, sb);
                g = Math.Max(dg, sg);
                r = Math.Max(dr, sr);
                break;
            case BlendMode.Darken:
                b = Math.Min(db, sb);
                g = Math.Min(dg, sg);
                r = Math.Min(dr, sr);
                break;
            case BlendMode.Subtractive:
                b = db - sb;
                g = dg - sg;
                r = dr - sr;
                break;
            case BlendMode.Normal:
            default:
                b = sb;
                g = sg;
                r = sr;
                break;
        }

        // Apply opacity as a lerp between destination and blended result.
        destination[destIndex] = ClampToByte((int)(db + (b - db) * opacity));
        destination[destIndex + 1] = ClampToByte((int)(dg + (g - dg) * opacity));
        destination[destIndex + 2] = ClampToByte((int)(dr + (r - dr) * opacity));
        destination[destIndex + 3] = 255;
    }

    private static byte ClampToByte(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    private void EnsureEngineColorBuffer()
    {
        int size = _engine.Columns * _engine.Rows * 4;
        if (_engineColorBuffer == null || _engineColorBuffer.Length != size)
        {
            _engineColorBuffer = new byte[size];
        }

        Parallel.For(0, _engine.Rows, row =>
        {
            int rowOffset = row * _engine.Columns * 4;
            for (int col = 0; col < _engine.Columns; col++)
            {
                var (r, g, b) = _engine.GetColor(row, col);
                int index = rowOffset + (col * 4);
                _engineColorBuffer[index] = r;
                _engineColorBuffer[index + 1] = g;
                _engineColorBuffer[index + 2] = b;
                _engineColorBuffer[index + 3] = 255;
            }
        });
    }

    private void InitializeEffect()
    {
        _overlayBrush = new ImageBrush
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center
        };
        if (_underlayBitmap != null)
        {
            _overlayBrush.ImageSource = _underlayBitmap;
        }
        _blendEffect.Overlay = _overlayBrush;
        _inputBrush = new ImageBrush(_bitmap)
        {
            Stretch = Stretch.Fill,
            Opacity = _lifeOpacity
        };
        _blendEffect.Input = _inputBrush;
        GameImage.Effect = _blendEffect;
        UpdateEffectInput();
    }

    private void UpdateUnderlayBitmap(int requiredLength)
    {
        if (_underlayBitmap == null)
        {
            return;
        }

        var composite = _lastCompositeFrame;
        bool hasOverlay = _passthroughEnabled && composite != null;
        if (!hasOverlay)
        {
            return;
        }

        int width = _underlayBitmap.PixelWidth;
        int height = _underlayBitmap.PixelHeight;
        byte[]? buffer = null;
        int stride = width * 4;

        if (_preserveResolution && composite?.HighRes is { Length: > 0 } highRes &&
            composite.HighResWidth == width && composite.HighResHeight == height)
        {
            buffer = highRes;
            stride = composite.HighResWidth * 4;
        }
        else if (composite != null && composite.Downscaled.Length >= requiredLength &&
                 composite.DownscaledWidth == width && composite.DownscaledHeight == height)
        {
            buffer = composite.Downscaled;
        }

        if (buffer == null || buffer.Length < stride * height)
        {
            return;
        }

        if (_invertComposite)
        {
            if (_invertScratchBuffer == null || _invertScratchBuffer.Length != buffer.Length)
            {
                _invertScratchBuffer = new byte[buffer.Length];
            }
            Buffer.BlockCopy(buffer, 0, _invertScratchBuffer, 0, buffer.Length);
            buffer = _invertScratchBuffer;
            InvertBuffer(buffer);
        }

        _underlayBitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, stride, 0);
    }

    private void UpdateEffectInput()
    {
        _blendEffect.UseOverlay = _passthroughEnabled && _lastCompositeFrame != null ? 1.0 : 0.0;
        _blendEffect.Mode = _blendMode switch
        {
            BlendMode.Additive => 0.0,
            BlendMode.Normal => 1.0,
            BlendMode.Multiply => 2.0,
            BlendMode.Screen => 3.0,
            BlendMode.Overlay => 4.0,
            BlendMode.Lighten => 5.0,
            BlendMode.Darken => 6.0,
            BlendMode.Subtractive => 7.0,
            _ => 0.0
        };
        if (_inputBrush != null && _bitmap != null)
        {
            _inputBrush.ImageSource = _bitmap;
            _inputBrush.Opacity = _lifeOpacity;
        }
    }

    private void UpdateFpsOverlay()
    {
        if (FpsText == null)
        {
            return;
        }

        if (_showFps)
        {
            FpsText.Text = $"{_displayFps:0.0} fps";
            FpsText.Visibility = Visibility.Visible;
        }
        else
        {
            FpsText.Visibility = Visibility.Collapsed;
        }
    }

    private void InvertBuffer(byte[] buffer)
    {
        if (buffer == null)
        {
            return;
        }

        Parallel.For(0, buffer.Length / 4, i =>
        {
            int index = i * 4;
            buffer[index] = (byte)(255 - buffer[index]);
            buffer[index + 1] = (byte)(255 - buffer[index + 1]);
            buffer[index + 2] = (byte)(255 - buffer[index + 2]);
        });
    }

    private void BinningModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Header: string header })
        {
            return;
        }

        if (header.StartsWith("Fill", StringComparison.OrdinalIgnoreCase))
        {
            SetBinningMode(GameOfLifeEngine.BinningMode.Fill);
        }
        else if (header.StartsWith("Binary", StringComparison.OrdinalIgnoreCase))
        {
            SetBinningMode(GameOfLifeEngine.BinningMode.Binary);
        }
    }

    private void SetBinningMode(GameOfLifeEngine.BinningMode mode)
    {
        if (_binningMode == mode)
        {
            return;
        }

        _binningMode = mode;
        _engine.SetBinningMode(mode);
        UpdateBinningModeMenuChecks();
        RenderFrame();
        SaveConfig();
    }

    private void InjectionModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Header: string header })
        {
            return;
        }

        if (header.StartsWith("Threshold", StringComparison.OrdinalIgnoreCase))
        {
            SetInjectionMode(GameOfLifeEngine.InjectionMode.Threshold);
        }
        else if (header.StartsWith("Random", StringComparison.OrdinalIgnoreCase))
        {
            SetInjectionMode(GameOfLifeEngine.InjectionMode.RandomPulse);
        }
        else if (header.StartsWith("Pulse", StringComparison.OrdinalIgnoreCase))
        {
            SetInjectionMode(GameOfLifeEngine.InjectionMode.PulseWidthModulation);
        }
    }

    private void SetInjectionMode(GameOfLifeEngine.InjectionMode mode)
    {
        if (_injectionMode == mode)
        {
            return;
        }

        _injectionMode = mode;
        _engine.SetInjectionMode(mode);
        _pulseStep = 0;
        UpdateInjectionModeMenuChecks();
        RenderFrame();
        SaveConfig();
    }

    private void UpdateInjectionModeMenuChecks()
    {
        if (InjectionModeMenu == null)
        {
            return;
        }

        foreach (var item in InjectionModeMenu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Header is string header)
            {
                bool isThreshold = header.StartsWith("Threshold", StringComparison.OrdinalIgnoreCase);
                bool isRandom = header.StartsWith("Random", StringComparison.OrdinalIgnoreCase);
                bool isPwm = header.StartsWith("Pulse Width", StringComparison.OrdinalIgnoreCase);
                menuItem.IsCheckable = true;
                menuItem.IsChecked = (isThreshold && _injectionMode == GameOfLifeEngine.InjectionMode.Threshold) ||
                                     (isRandom && _injectionMode == GameOfLifeEngine.InjectionMode.RandomPulse) ||
                                     (isPwm && _injectionMode == GameOfLifeEngine.InjectionMode.PulseWidthModulation);
            }
        }
    }

    private void UpdateBinningModeMenuChecks()
    {
        if (BinningModeMenu == null)
        {
            return;
        }

        foreach (var item in BinningModeMenu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Header is string header)
            {
                bool isFill = header.StartsWith("Fill", StringComparison.OrdinalIgnoreCase);
                bool isBinary = header.StartsWith("Binary", StringComparison.OrdinalIgnoreCase);
                menuItem.IsCheckable = true;
                menuItem.IsChecked = (isFill && _binningMode == GameOfLifeEngine.BinningMode.Fill) ||
                                     (isBinary && _binningMode == GameOfLifeEngine.BinningMode.Binary);
            }
        }
    }

    private double _currentFpsFromConfig = DefaultFps;

    private void SetFramerate(double fps)
    {
        fps = Math.Clamp(fps, 5, 144);
        _currentFpsFromConfig = fps;
        if (!_fpsOscillationEnabled)
        {
            _currentFps = fps;
        }
        UpdateFramerateMenuChecks();
        SaveConfig();
    }

    private void UpdateFramerateMenuChecks()
    {
        if (FramerateMenu == null)
        {
            return;
        }

        foreach (var item in FramerateMenu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Header is string header)
            {
                bool isChecked = header.StartsWith("15", StringComparison.OrdinalIgnoreCase) && Math.Abs(_currentFpsFromConfig - 15) < 0.1
                                 || header.StartsWith("30", StringComparison.OrdinalIgnoreCase) && Math.Abs(_currentFpsFromConfig - 30) < 0.1
                                 || header.StartsWith("60", StringComparison.OrdinalIgnoreCase) && Math.Abs(_currentFpsFromConfig - 60) < 0.1
                                 || header.StartsWith("144", StringComparison.OrdinalIgnoreCase) && Math.Abs(_currentFpsFromConfig - 144) < 0.1;
                menuItem.IsCheckable = true;
                menuItem.IsChecked = isChecked;
            }
        }
    }

    private void LifeOpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _lifeOpacity = Math.Clamp(e.NewValue, 0, 1);
        Logger.Info($"Life opacity set to {_lifeOpacity:F2}");
        UpdateEffectInput();
        RenderFrame();
        SaveConfig();
    }

    private void InvertComposite_Click(object sender, RoutedEventArgs e)
    {
        _invertComposite = !_invertComposite;
        if (InvertCompositeMenuItem != null)
        {
            InvertCompositeMenuItem.IsChecked = _invertComposite;
        }
        Logger.Info($"Invert composite toggled: {_invertComposite}");
        RenderFrame();
        SaveConfig();
    }

    private void ToggleFps_Click(object sender, RoutedEventArgs e)
    {
        _showFps = !_showFps;
        if (ShowFpsMenuItem != null)
        {
            ShowFpsMenuItem.IsChecked = _showFps;
        }
        UpdateFpsOverlay();
        SaveConfig();
    }

    private void LifeModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Header: string header })
        {
            return;
        }

        if (header.StartsWith("Naive", StringComparison.OrdinalIgnoreCase))
        {
            SetLifeMode(GameOfLifeEngine.LifeMode.NaiveGrayscale);
        }
        else if (header.StartsWith("RGB", StringComparison.OrdinalIgnoreCase))
        {
            SetLifeMode(GameOfLifeEngine.LifeMode.RgbChannels);
        }
    }

    private void SetLifeMode(GameOfLifeEngine.LifeMode mode)
    {
        if (_lifeMode == mode)
        {
            return;
        }

        _lifeMode = mode;
        _engine.SetMode(mode);
        _pulseStep = 0;
        UpdateDisplaySurface(force: true);
        RenderFrame();
        SaveConfig();
    }

    private void UpdateLifeModeMenuChecks()
    {
        if (LifeModeMenu == null)
        {
            return;
        }

        foreach (var item in LifeModeMenu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Header is string header)
            {
                bool isNaive = header.StartsWith("Naive", StringComparison.OrdinalIgnoreCase);
                bool isRgb = header.StartsWith("RGB", StringComparison.OrdinalIgnoreCase);
                menuItem.IsCheckable = true;
                menuItem.IsChecked = (isNaive && _lifeMode == GameOfLifeEngine.LifeMode.NaiveGrayscale) ||
                                     (isRgb && _lifeMode == GameOfLifeEngine.LifeMode.RgbChannels);
            }
        }
    }

    private bool[,] BuildLuminanceMask(byte[] buffer, int width, int height, double min, double max, bool invert, GameOfLifeEngine.InjectionMode mode, double noiseProbability, int period, int pulseStep)
    {
        min = Math.Clamp(min, 0, 1);
        max = Math.Clamp(max, 0, 1);
        noiseProbability = Math.Clamp(noiseProbability, 0, 1);
        period = Math.Max(1, period);
        int rows = Math.Max(0, height);
        int cols = Math.Max(0, width);
        var mask = new bool[rows, cols];

        if (rows == 0 || cols == 0 || buffer.Length < rows * cols * 4)
        {
            return mask;
        }

        int stride = cols * 4;
        Parallel.For(0, rows, row =>
        {
            int rowOffset = row * stride;
            for (int col = 0; col < cols; col++)
            {
                int index = rowOffset + (col * 4);
                byte b = buffer[index];
                byte g = buffer[index + 1];
                byte r = buffer[index + 2];

                bool noiseFail = noiseProbability > 0 && Random.Shared.NextDouble() < noiseProbability;
                double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                bool alive = false;
                if (mode == GameOfLifeEngine.InjectionMode.RandomPulse)
                {
                    double gate = ComputePwmSignal(luminance, min, max, invert);
                    alive = gate > 0 && Random.Shared.NextDouble() < luminance;
                }
                else if (mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation)
                {
                    double gate = ComputePwmSignal(luminance, min, max, invert);
                    alive = gate > 0 && PulseWidthAlive(gate, period, pulseStep);
                }
                else
                {
                    alive = EvaluateThresholdValue(luminance, min, max, invert);
                }
                mask[row, col] = !noiseFail && alive;
            }
        });

        return mask;
    }

    private (bool[,] r, bool[,] g, bool[,] b) BuildChannelMasks(byte[] buffer, int width, int height, double min, double max, bool invert, GameOfLifeEngine.InjectionMode mode, double noiseProbability, int rPeriod, int gPeriod, int bPeriod, int pulseStep)
    {
        min = Math.Clamp(min, 0, 1);
        max = Math.Clamp(max, 0, 1);
        noiseProbability = Math.Clamp(noiseProbability, 0, 1);
        rPeriod = Math.Max(1, rPeriod);
        gPeriod = Math.Max(1, gPeriod);
        bPeriod = Math.Max(1, bPeriod);
        int rows = Math.Max(0, height);
        int cols = Math.Max(0, width);
        var rMask = new bool[rows, cols];
        var gMask = new bool[rows, cols];
        var bMask = new bool[rows, cols];

        if (rows == 0 || cols == 0 || buffer.Length < rows * cols * 4)
        {
            return (rMask, gMask, bMask);
        }

        int stride = cols * 4;
        Parallel.For(0, rows, row =>
        {
            int rowOffset = row * stride;
            for (int col = 0; col < cols; col++)
            {
                int index = rowOffset + (col * 4);
                byte b = buffer[index];
                byte g = buffer[index + 1];
                byte r = buffer[index + 2];

                double randomGate = Random.Shared.NextDouble();
                bool noiseFail = noiseProbability > 0 && randomGate < noiseProbability;
                double nr = r / 255.0;
                double ng = g / 255.0;
                double nb = b / 255.0;

                double rGate = ComputePwmSignal(nr, min, max, invert);
                double gGate = ComputePwmSignal(ng, min, max, invert);
                double bGate = ComputePwmSignal(nb, min, max, invert);

                bool rAlive = mode == GameOfLifeEngine.InjectionMode.RandomPulse
                    ? rGate > 0 && randomGate < nr
                    : mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation
                        ? rGate > 0 && PulseWidthAlive(nr, rPeriod, pulseStep)
                    : EvaluateThresholdValue(nr, min, max, invert);
                bool gAlive = mode == GameOfLifeEngine.InjectionMode.RandomPulse
                    ? gGate > 0 && randomGate < ng
                    : mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation
                        ? gGate > 0 && PulseWidthAlive(ng, gPeriod, pulseStep)
                    : EvaluateThresholdValue(ng, min, max, invert);
                bool bAlive = mode == GameOfLifeEngine.InjectionMode.RandomPulse
                    ? bGate > 0 && randomGate < nb
                    : mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation
                        ? bGate > 0 && PulseWidthAlive(nb, bPeriod, pulseStep)
                    : EvaluateThresholdValue(nb, min, max, invert);

                rMask[row, col] = !noiseFail && rAlive;
                gMask[row, col] = !noiseFail && gAlive;
                bMask[row, col] = !noiseFail && bAlive;
            }
        });

        return (rMask, gMask, bMask);
    }

    private void FpsOscillation_OnChecked(object sender, RoutedEventArgs e)
    {
        bool wasEnabled = _fpsOscillationEnabled;
        _fpsOscillationEnabled = FpsOscillationCheckBox?.IsChecked == true;
        
        if (wasEnabled && !_fpsOscillationEnabled)
        {
            // Reset to fixed framerate
            SetFramerate(_currentFpsFromConfig); 
        }

        if (AudioSyncCheckBox != null)
        {
            AudioSyncCheckBox.IsEnabled = _fpsOscillationEnabled;
        }
        SaveConfig();
    }

    private void AudioSync_OnChecked(object sender, RoutedEventArgs e)
    {
        _audioSyncEnabled = AudioSyncCheckBox?.IsChecked == true;
        if (BpmSlider != null)
        {
            BpmSlider.IsEnabled = !_audioSyncEnabled;
        }
        SaveConfig();
    }

    private void BpmSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _oscillationBpm = e.NewValue;
        if (BpmValueText != null)
        {
            BpmValueText.Text = $"{_oscillationBpm:F0}";
        }
        SaveConfig();
    }

    private void OscillationRange_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MinFpsSlider == null || MaxFpsSlider == null) return;

        _oscillationMinFps = MinFpsSlider.Value;
        _oscillationMaxFps = MaxFpsSlider.Value;
        
        if (MinFpsValueText != null) MinFpsValueText.Text = $"{_oscillationMinFps:F0}";
        if (MaxFpsValueText != null) MaxFpsValueText.Text = $"{_oscillationMaxFps:F0}";

        SaveConfig();
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return;
            }

            string json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json);
            if (config == null)
            {
                return;
            }

            _captureThresholdMin = Math.Clamp(config.CaptureThresholdMin, 0, 1);
            _captureThresholdMax = Math.Clamp(config.CaptureThresholdMax, 0, 1);
            _invertThreshold = config.InvertThreshold;
            _currentFpsFromConfig = Math.Clamp(config.Framerate, 5, 144);
            _currentFps = _currentFpsFromConfig;
            _lifeOpacity = Math.Clamp(config.LifeOpacity, 0, 1);
            if (Enum.TryParse<GameOfLifeEngine.LifeMode>(config.LifeMode, out var lifeMode))
            {
                _lifeMode = lifeMode;
            }
            if (Enum.TryParse<GameOfLifeEngine.BinningMode>(config.BinningMode, out var binMode))
            {
                _binningMode = binMode;
            }
            _preserveResolution = config.PreserveResolution;
            _injectionNoise = Math.Clamp(config.InjectionNoise, 0, 1);
            if (Enum.TryParse<GameOfLifeEngine.InjectionMode>(config.InjectionMode, out var injMode))
            {
                _injectionMode = injMode;
            }
            _invertComposite = config.InvertComposite;
            _showFps = config.ShowFps;
            if (config.Height > 0)
            {
                _configuredRows = Math.Clamp(config.Height, MinRows, MaxRows);
            }
            else if (config.Columns > 0)
            {
                _pendingLegacyColumns = config.Columns;
                _configuredRows = DefaultRows;
            }
            else
            {
                _configuredRows = DefaultRows;
            }
            _configuredDepth = Math.Clamp(config.Depth, 3, 96);
            _passthroughEnabled = config.Passthrough;
            _fpsOscillationEnabled = config.OscillationEnabled;
            _oscillationBpm = Math.Clamp(config.OscillationBpm, 40, 240);
            _oscillationMinFps = Math.Clamp(config.OscillationMinFps, 1, 144);
            _oscillationMaxFps = Math.Clamp(config.OscillationMaxFps, 1, 144);
            _audioSyncEnabled = config.AudioSyncEnabled;
            _selectedAudioDeviceId = config.AudioDeviceId;
            _aspectRatioLocked = config.AspectRatioLocked;
            _lockedAspectRatio = config.LockedAspectRatio > 0 ? config.LockedAspectRatio : DefaultAspectRatio;

            if (!string.IsNullOrWhiteSpace(_selectedAudioDeviceId))
            {
                 _ = _audioBeatDetector.InitializeAsync(_selectedAudioDeviceId);
            }

            if (Enum.TryParse<BlendMode>(config.BlendMode, out var blendMode))
            {
                _blendMode = blendMode;
            }

            _pendingFullscreen = config.Fullscreen;
            RestoreSources(config.Sources);
            if (_pendingLegacyColumns.HasValue)
            {
                double targetAspect = _aspectRatioLocked ? _lockedAspectRatio
                    : (_sources.Count > 0 ? _sources[0].AspectRatio : DefaultAspectRatio);
                _configuredRows = Math.Clamp((int)Math.Round(_pendingLegacyColumns.Value / targetAspect), MinRows, MaxRows);
                _pendingLegacyColumns = null;
            }
        }
        catch
        {
            // Ignore config load errors.
        }
        finally
        {
            // Allow saves after the first load attempt so startup events don't clobber existing config.
            _configReady = true;
        }
    }

    private void SaveConfig()
    {
        if (!_configReady)
        {
            return;
        }

        try
        {
            var config = new AppConfig
            {
                CaptureThresholdMin = _captureThresholdMin,
                CaptureThresholdMax = _captureThresholdMax,
                InvertThreshold = _invertThreshold,
                Framerate = _currentFpsFromConfig,
                LifeMode = _lifeMode.ToString(),
                BinningMode = _binningMode.ToString(),
                InjectionMode = _injectionMode.ToString(),
                PreserveResolution = _preserveResolution,
                InjectionNoise = _injectionNoise,
                LifeOpacity = _lifeOpacity,
                InvertComposite = _invertComposite,
                ShowFps = _showFps,
                Height = _configuredRows,
                Depth = _configuredDepth,
                Passthrough = _passthroughEnabled,
                OscillationEnabled = _fpsOscillationEnabled,
                OscillationBpm = _oscillationBpm,
                OscillationMinFps = _oscillationMinFps,
                OscillationMaxFps = _oscillationMaxFps,
                AudioSyncEnabled = _audioSyncEnabled,
                AudioDeviceId = _selectedAudioDeviceId,
                BlendMode = _blendMode.ToString(),
                Fullscreen = _isFullscreen,
                AspectRatioLocked = _aspectRatioLocked,
                LockedAspectRatio = _lockedAspectRatio,
                Sources = BuildSourceConfigs()
            };

            string directory = Path.GetDirectoryName(ConfigPath) ?? string.Empty;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Ignore config save errors.
        }
    }

    private List<AppConfig.SourceConfig> BuildSourceConfigs() => BuildSourceConfigs(_sources);

    private List<AppConfig.SourceConfig> BuildSourceConfigs(List<CaptureSource> sources)
    {
        var configs = new List<AppConfig.SourceConfig>(sources.Count);
        foreach (var source in sources)
        {
            var config = new AppConfig.SourceConfig
            {
                Type = source.Type.ToString(),
                WindowTitle = source.Window?.Title,
                WebcamId = source.WebcamId,
                FilePath = source.FilePath,
                DisplayName = source.DisplayName,
                BlendMode = source.BlendMode.ToString(),
                FitMode = source.FitMode.ToString(),
                Opacity = source.Opacity,
                Mirror = source.Mirror
            };

            if (source.Type == CaptureSource.SourceType.Group && source.Children.Count > 0)
            {
                config.Children = BuildSourceConfigs(source.Children);
            }

            configs.Add(config);
        }

        return configs;
    }

    private void RestoreSources(IReadOnlyList<AppConfig.SourceConfig>? configs)
    {
        if (configs == null || configs.Count == 0)
        {
            return;
        }

        var windows = _windowCapture.EnumerateWindows(_windowHandle);
        var webcams = _webcamCapture.EnumerateCameras();
        RestoreSourceList(configs, _sources, windows, webcams);

        double targetAspect = _aspectRatioLocked ? _lockedAspectRatio
            : (_sources.Count > 0 ? _sources[0].AspectRatio : DefaultAspectRatio);

        if (_pendingLegacyColumns.HasValue)
        {
            _configuredRows = Math.Clamp((int)Math.Round(_pendingLegacyColumns.Value / targetAspect), MinRows, MaxRows);
            _pendingLegacyColumns = null;
        }

        if (_sources.Count > 0)
        {
            _currentAspectRatio = targetAspect;
            ApplyDimensions(_configuredRows, null, _currentAspectRatio, persist: false);
        }
    }

    private void RestoreSourceList(IReadOnlyList<AppConfig.SourceConfig> configs, List<CaptureSource> targetList,
        IReadOnlyList<WindowHandleInfo> windows, IReadOnlyList<WebcamCaptureService.CameraInfo> webcams)
    {
        foreach (var config in configs)
        {
            if (!Enum.TryParse<CaptureSource.SourceType>(config.Type, true, out var type))
            {
                continue;
            }

            CaptureSource? restored = null;
            switch (type)
            {
                case CaptureSource.SourceType.Group:
                    restored = CaptureSource.CreateGroup(string.IsNullOrWhiteSpace(config.DisplayName) ? null : config.DisplayName);
                    break;

                case CaptureSource.SourceType.Window:
                    if (string.IsNullOrWhiteSpace(config.WindowTitle))
                    {
                        break;
                    }

                    var window = windows.FirstOrDefault(w =>
                        string.Equals(w.Title, config.WindowTitle, StringComparison.OrdinalIgnoreCase));
                    if (window != null)
                    {
                        restored = CaptureSource.CreateWindow(window);
                    }
                    break;

                case CaptureSource.SourceType.Webcam:
                    var camera = webcams.FirstOrDefault(c =>
                        (!string.IsNullOrWhiteSpace(config.WebcamId) && string.Equals(c.Id, config.WebcamId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(config.DisplayName) && string.Equals(c.Name, config.DisplayName, StringComparison.OrdinalIgnoreCase)));
                    if (!string.IsNullOrWhiteSpace(camera.Id))
                    {
                        restored = CaptureSource.CreateWebcam(camera.Id, camera.Name);
                    }
                    break;

                case CaptureSource.SourceType.File:
                    if (string.IsNullOrWhiteSpace(config.FilePath))
                    {
                        break;
                    }

                    if (_fileCapture.TryGetOrAdd(config.FilePath, out var info, out _))
                    {
                        restored = CaptureSource.CreateFile(info.Path, info.DisplayName, info.Width, info.Height);
                    }
                    break;
            }

            if (restored == null)
            {
                continue;
            }

            ApplySourceSettings(restored, config);
            targetList.Add(restored);

            if (type == CaptureSource.SourceType.Group && config.Children.Count > 0)
            {
                RestoreSourceList(config.Children, restored.Children, windows, webcams);
            }
        }
    }

    private static void ApplySourceSettings(CaptureSource source, AppConfig.SourceConfig config)
    {
        if (Enum.TryParse<BlendMode>(config.BlendMode, true, out var blend))
        {
            source.BlendMode = blend;
        }

        if (Enum.TryParse<FitMode>(config.FitMode, true, out var fitMode))
        {
            source.FitMode = fitMode;
        }

        source.Opacity = Math.Clamp(config.Opacity, 0, 1);
        source.Mirror = config.Mirror;
    }

    private string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lifeviz", "config.json");

    private sealed class AppConfig
    {
        public double CaptureThresholdMin { get; set; } = 0.35;
        public double CaptureThresholdMax { get; set; } = 0.75;
        public bool InvertThreshold { get; set; }
        public double Framerate { get; set; } = DefaultFps;
        public string LifeMode { get; set; } = GameOfLifeEngine.LifeMode.NaiveGrayscale.ToString();
        public string BinningMode { get; set; } = GameOfLifeEngine.BinningMode.Fill.ToString();
        public string InjectionMode { get; set; } = GameOfLifeEngine.InjectionMode.Threshold.ToString();
        public bool PreserveResolution { get; set; }
        public double InjectionNoise { get; set; } = 0.0;
        public double LifeOpacity { get; set; } = 1.0;
        public bool InvertComposite { get; set; }
        public bool ShowFps { get; set; }
        public int Height { get; set; } = DefaultRows;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Columns { get; set; }
        public int Depth { get; set; } = DefaultDepth;
        public bool Passthrough { get; set; }
        public bool OscillationEnabled { get; set; }
        public double OscillationBpm { get; set; } = 140;
        public double OscillationMinFps { get; set; } = 30;
        public double OscillationMaxFps { get; set; } = 60;
        public bool AudioSyncEnabled { get; set; }
        public string? AudioDeviceId { get; set; }
        public string BlendMode { get; set; } = MainWindow.BlendMode.Additive.ToString();
        public bool Fullscreen { get; set; }
        public bool AspectRatioLocked { get; set; }
        public double LockedAspectRatio { get; set; } = DefaultAspectRatio;
        public List<SourceConfig> Sources { get; set; } = new();

        public sealed class SourceConfig
        {
            public string Type { get; set; } = CaptureSource.SourceType.Window.ToString();
            public string? WindowTitle { get; set; }
            public string? WebcamId { get; set; }
            public string? FilePath { get; set; }
            public string? DisplayName { get; set; }
            public string BlendMode { get; set; } = MainWindow.BlendMode.Normal.ToString();
            public string FitMode { get; set; } = lifeviz.FitMode.Fit.ToString();
            public double Opacity { get; set; } = 1.0;
            public bool Mirror { get; set; }
            public List<SourceConfig> Children { get; set; } = new();
        }
    }

    private void BuildMappings(int width, int height, int engineCols, int engineRows)
    {
        for (int row = 0; row < height; row++)
        {
            _rowMap[row] = Math.Min(engineRows - 1, (int)((row / (double)height) * engineRows));
        }

        for (int col = 0; col < width; col++)
        {
            _colMap[col] = Math.Min(engineCols - 1, (int)((col / (double)width) * engineCols));
        }
    }

    private sealed class CaptureSource
    {
        public enum SourceType
        {
            Window,
            Webcam,
            File,
            Group
        }

        private CaptureSource(SourceType type, WindowHandleInfo? window, string? webcamId, string? filePath, string displayName, int? fileWidth, int? fileHeight)
        {
            Type = type;
            Window = window;
            WebcamId = webcamId;
            FilePath = filePath;
            DisplayName = displayName;
            FileWidth = fileWidth;
            FileHeight = fileHeight;
        }

        public static CaptureSource CreateWindow(WindowHandleInfo window) =>
            new(SourceType.Window, window, null, null, window.Title, null, null) { AddedUtc = DateTime.UtcNow };

        public static CaptureSource CreateWebcam(string webcamId, string name) =>
            new(SourceType.Webcam, null, webcamId, null, name, null, null) { AddedUtc = DateTime.UtcNow };

        public static CaptureSource CreateFile(string filePath, string displayName, int width, int height)
        {
            int? fileWidth = width > 0 ? width : null;
            int? fileHeight = height > 0 ? height : null;
            return new(SourceType.File, null, null, filePath, displayName, fileWidth, fileHeight) { AddedUtc = DateTime.UtcNow };
        }

        public static CaptureSource CreateGroup(string? displayName = null) =>
            new(SourceType.Group, null, null, null, displayName ?? "Layer Group", null, null) { AddedUtc = DateTime.UtcNow };

        public SourceType Type { get; }
        public WindowHandleInfo? Window { get; set; }
        public string? WebcamId { get; }
        public string? FilePath { get; }
        public string DisplayName { get; }
        public List<CaptureSource> Children { get; } = new();
        public BlendMode BlendMode { get; set; } = BlendMode.Normal;
        public FitMode FitMode { get; set; } = FitMode.Fit;
        public SourceFrame? LastFrame { get; set; }
        public bool HasError { get; set; }
        public int MissedFrames { get; set; }
        public bool FirstFrameReceived { get; set; }
        public DateTime AddedUtc { get; set; }
        public double Opacity { get; set; } = 1.0;
        public bool Mirror { get; set; }
        public bool RetryInitializationAttempted { get; set; }

        public bool IsInitialized { get; set; }
        public int? FileWidth { get; private set; }
        public int? FileHeight { get; private set; }
        public byte[]? CompositeDownscaledBuffer { get; set; }
        public byte[]? CompositeHighResBuffer { get; set; }

        public double AspectRatio
        {
            get
            {
                if (Type == SourceType.Group)
                {
                    if (Children.Count > 0)
                    {
                        return Children[0].AspectRatio;
                    }
                    return DefaultAspectRatio;
                }

                if (LastFrame != null && LastFrame.SourceHeight > 0)
                {
                    return Math.Max(0.05, LastFrame.SourceWidth / (double)LastFrame.SourceHeight);
                }

                if (Window != null)
                {
                    return Window.AspectRatio;
                }

                if (Type == SourceType.File && FileWidth.HasValue && FileHeight.HasValue && FileHeight > 0)
                {
                    return Math.Max(0.05, FileWidth.Value / (double)FileHeight.Value);
                }

                return DefaultAspectRatio;
            }
        }

        public int? FallbackWidth => Type switch
        {
            SourceType.Window => Window?.Width,
            SourceType.File => FileWidth,
            SourceType.Group => Children.Count > 0 ? Children[0].FallbackWidth : null,
            _ => LastFrame?.SourceWidth
        };

        public int? FallbackHeight => Type switch
        {
            SourceType.Window => Window?.Height,
            SourceType.File => FileHeight,
            SourceType.Group => Children.Count > 0 ? Children[0].FallbackHeight : null,
            _ => LastFrame?.SourceHeight
        };

        public void UpdateFileDimensions(int width, int height)
        {
            if (width > 0 && height > 0)
            {
                FileWidth = width;
                FileHeight = height;
            }
        }
    }

    private sealed class SourceFrame
    {
        public SourceFrame(byte[] downscaled, int downscaledWidth, int downscaledHeight, byte[]? source, int sourceWidth, int sourceHeight)
        {
            Downscaled = downscaled;
            DownscaledWidth = downscaledWidth;
            DownscaledHeight = downscaledHeight;
            Source = source;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
        }

        public byte[] Downscaled { get; }
        public int DownscaledWidth { get; }
        public int DownscaledHeight { get; }
        public byte[]? Source { get; }
        public int SourceWidth { get; }
        public int SourceHeight { get; }
    }

    private sealed class CompositeFrame
    {
        public CompositeFrame(byte[] downscaled, int downscaledWidth, int downscaledHeight, byte[]? highRes, int highResWidth, int highResHeight)
        {
            Downscaled = downscaled;
            DownscaledWidth = downscaledWidth;
            DownscaledHeight = downscaledHeight;
            HighRes = highRes;
            HighResWidth = highResWidth;
            HighResHeight = highResHeight;
        }

        public byte[] Downscaled { get; }
        public int DownscaledWidth { get; }
        public int DownscaledHeight { get; }
        public byte[]? HighRes { get; }
        public int HighResWidth { get; }
        public int HighResHeight { get; }
    }
}
