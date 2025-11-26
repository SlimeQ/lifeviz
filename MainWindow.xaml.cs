using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Windows.Threading;
using Windows.Devices.Enumeration;

namespace lifeviz;

public partial class MainWindow : Window
{
    private const int DefaultColumns = 128;
    private const int DefaultDepth = 24;
    private const double DefaultAspectRatio = 16d / 9d;
    private const double DefaultFps = 60;

    private readonly GameOfLifeEngine _engine = new();
    private readonly DispatcherTimer _timer;
    private readonly WindowCaptureService _windowCapture = new();
    private readonly WebcamCaptureService _webcamCapture = new();
    private readonly BlendEffect _blendEffect = new();
    private int _configuredColumns = DefaultColumns;
    private int _configuredDepth = DefaultDepth;
    private IReadOnlyList<WindowHandleInfo> _cachedWindows = Array.Empty<WindowHandleInfo>();
    private IReadOnlyList<WebcamCaptureService.CameraInfo> _cachedCameras = Array.Empty<WebcamCaptureService.CameraInfo>();
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
    private IntPtr _windowHandle;
    private bool _passthroughEnabled;
    private bool _preserveResolution;
    private BlendMode _blendMode = BlendMode.Additive;
    private double _lifeOpacity = 1.0;
    private bool _invertComposite;
    private bool _showFps;
    private readonly Stopwatch _fpsStopwatch = new();
    private int _fpsFrames;
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

    public MainWindow()
    {
        Logger.Initialize();
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / DefaultFps)
        };
        _timer.Tick += (_, _) => OnTick();

        Loaded += (_, _) =>
        {
            LoadConfig();
            InitializeVisualizer();
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
            Logger.Shutdown();
        };
    }

    private void InitializeVisualizer()
    {
        _currentAspectRatio = DefaultAspectRatio;
        _engine.Configure(_configuredColumns, _configuredDepth, _currentAspectRatio);
        _engine.SetMode(_lifeMode);
        _engine.SetBinningMode(_binningMode);
        _engine.SetInjectionMode(_injectionMode);
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / _currentFps);
        _engine.Randomize();
        UpdateDisplaySurface(force: true);
        InitializeEffect();
        UpdateFpsOverlay();
        _timer.Start();
    }

    private void OnTick()
    {
        if (!_isPaused)
        {
            InjectCaptureFrames();
            _engine.Step();
        }

        RenderFrame();
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

        for (int row = 0; row < height; row++)
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
        }

        if (_invertComposite)
        {
            InvertBuffer(_pixelBuffer);
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _pixelBuffer, stride, 0);
        UpdateUnderlayBitmap(requiredLength);
        UpdateEffectInput();
        UpdateFpsOverlay();

        _fpsFrames++;
        if (!_fpsStopwatch.IsRunning)
        {
            _fpsStopwatch.Start();
        }
        else if (_fpsStopwatch.ElapsedMilliseconds >= 500)
        {
            double seconds = _fpsStopwatch.Elapsed.TotalSeconds;
            if (seconds > 0)
            {
                _displayFps = _fpsFrames / seconds;
            }
            _fpsFrames = 0;
            _fpsStopwatch.Restart();
        }
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

    private void PresetColumns_Click(object sender, RoutedEventArgs e)
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

    private void SetColumns_Click(object sender, RoutedEventArgs e)
    {
        int? requested = PromptForInteger("Columns", _engine.Columns, 32, 512);
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

    private void ApplyDimensions(int? columns, int? depth, double? aspectOverride = null, bool persist = true)
    {
        int nextColumns = columns ?? _engine.Columns;
        int nextDepth = depth ?? _engine.Depth;
        double nextAspect = aspectOverride ?? _currentAspectRatio;
        _currentAspectRatio = nextAspect;

        bool wasPaused = _isPaused;
        _isPaused = true;

        _configuredColumns = Math.Clamp(nextColumns, 32, 512);
        _configuredDepth = Math.Clamp(nextDepth, 3, 96);
        _engine.Configure(_configuredColumns, _configuredDepth, _currentAspectRatio);
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
        if (LifeOpacitySlider != null)
        {
            LifeOpacitySlider.Value = _lifeOpacity;
        }
        if (InvertCompositeMenuItem != null)
        {
            InvertCompositeMenuItem.IsChecked = _invertComposite;
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
    }

    private void PopulateSourcesMenu()
    {
        if (SourcesMenu == null)
        {
            return;
        }

        SourcesMenu.Items.Clear();

        var addWindowMenu = new MenuItem { Header = "Add Window Source" };
        _cachedWindows = _windowCapture.EnumerateWindows(_windowHandle);
        Logger.Info($"Enumerated windows: count={_cachedWindows.Count}");
        if (_cachedWindows.Count == 0)
        {
            addWindowMenu.Items.Add(new MenuItem
            {
                Header = "No windows detected",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var window in _cachedWindows)
            {
                bool alreadyAdded = _sources.Any(s => s.Type == CaptureSource.SourceType.Window && s.Window != null && s.Window.Handle == window.Handle);
                var item = new MenuItem
                {
                    Header = window.Title,
                    Tag = window,
                    IsCheckable = true,
                    IsChecked = alreadyAdded
                };
                item.Click += AddWindowSourceMenuItem_Click;
                addWindowMenu.Items.Add(item);
            }
        }

        var addWebcamMenu = new MenuItem { Header = "Add Webcam Source" };
        _cachedCameras = _webcamCapture.EnumerateCameras();
        Logger.Info($"Enumerated webcams: count={_cachedCameras.Count}");
        if (_cachedCameras.Count == 0)
        {
            addWebcamMenu.Items.Add(new MenuItem
            {
                Header = "No webcams detected",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var camera in _cachedCameras)
            {
                bool alreadyAdded = _sources.Any(s => s.Type == CaptureSource.SourceType.Webcam && string.Equals(s.WebcamId, camera.Id, StringComparison.OrdinalIgnoreCase));
                var item = new MenuItem
                {
                    Header = camera.Name,
                    Tag = camera,
                    IsCheckable = true,
                    IsChecked = alreadyAdded
                };
                item.Click += AddWebcamSourceMenuItem_Click;
                addWebcamMenu.Items.Add(item);
            }
        }

        SourcesMenu.Items.Add(addWindowMenu);
        SourcesMenu.Items.Add(addWebcamMenu);
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
            var source = _sources[i];
            string label = source.Type == CaptureSource.SourceType.Webcam
                ? $"{i + 1}. Camera: {source.DisplayName}"
                : $"{i + 1}. {source.DisplayName}";
            var sourceItem = new MenuItem { Header = label, Tag = source };

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

            var primaryItem = new MenuItem
            {
                Header = "Make Primary (adopt aspect)",
                IsEnabled = i != 0
            };
            primaryItem.Click += (_, _) => MakePrimarySource(source);

            var moveUpItem = new MenuItem
            {
                Header = "Move Up",
                IsEnabled = i > 0
            };
            moveUpItem.Click += (_, _) => MoveSource(source, -1);

            var moveDownItem = new MenuItem
            {
                Header = "Move Down",
                IsEnabled = i < _sources.Count - 1
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
            sourceItem.Items.Add(primaryItem);
            sourceItem.Items.Add(moveUpItem);
            sourceItem.Items.Add(moveDownItem);
            sourceItem.Items.Add(mirrorItem);
            sourceItem.Items.Add(opacityItem);
            sourceItem.Items.Add(new Separator());
            sourceItem.Items.Add(removeItem);

            SourcesMenu.Items.Add(sourceItem);
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

    private void AddWindowSourceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: WindowHandleInfo info })
        {
            return;
        }

        Logger.Info($"Adding window source: {info.Title}");
        AddOrPromoteWindowSource(info);
    }

    private void AddWebcamSourceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: WebcamCaptureService.CameraInfo camera })
        {
            return;
        }

        Logger.Info($"Adding webcam source: {camera.Name}");
        AddOrPromoteWebcamSource(camera);
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

    private void AddOrPromoteWindowSource(WindowHandleInfo info)
    {
        var existing = _sources.FirstOrDefault(s => s.Type == CaptureSource.SourceType.Window && s.Window != null && s.Window.Handle == info.Handle);
        if (existing != null)
        {
            existing.Window = info;
            Logger.Info($"Updated existing window source: {info.Title}");
        }
        else
        {
            bool hadSources = _sources.Count > 0;
            _sources.Add(CaptureSource.CreateWindow(info));
            Logger.Info($"Inserted new window source (appended): {info.Title}");

            if (!hadSources)
            {
                _currentAspectRatio = info.AspectRatio;
                ApplyDimensions(null, null, _currentAspectRatio, persist: false);
            }
        }

        if (_sources.Count == 1)
        {
            _currentAspectRatio = info.AspectRatio;
            ApplyDimensions(null, null, _currentAspectRatio, persist: false);
        }

        RenderFrame();
        SaveConfig();
    }

    private void AddOrPromoteWebcamSource(WebcamCaptureService.CameraInfo camera)
    {
        var existing = _sources.FirstOrDefault(s => s.Type == CaptureSource.SourceType.Webcam && string.Equals(s.WebcamId, camera.Id, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Logger.Info($"Updated existing webcam source: {camera.Name}");
        }
        else
        {
            bool hadSources = _sources.Count > 0;
            _sources.Add(CaptureSource.CreateWebcam(camera.Id, camera.Name));
            Logger.Info($"Inserted new webcam source (appended): {camera.Name}");

            if (!hadSources && _sources.Count > 0)
            {
                _currentAspectRatio = _sources[0].AspectRatio;
                ApplyDimensions(null, null, _currentAspectRatio, persist: false);
            }
        }

        if (_sources.Count == 1)
        {
            _currentAspectRatio = _sources[0].AspectRatio;
            ApplyDimensions(null, null, _currentAspectRatio, persist: false);
        }

        RenderFrame();
        SaveConfig();
        _webcamErrorShown = false;
    }

    private void MakePrimarySource(CaptureSource source)
    {
        if (!_sources.Contains(source))
        {
            return;
        }

        _sources.Remove(source);
        _sources.Insert(0, source);
        _currentAspectRatio = source.AspectRatio;
        Logger.Info($"Primary source set: {source.DisplayName} ({source.Type})");
        ApplyDimensions(null, null, _currentAspectRatio);
        SaveConfig();
    }

    private void MoveSource(CaptureSource source, int delta)
    {
        int index = _sources.IndexOf(source);
        if (index < 0)
        {
            return;
        }

        int next = Math.Clamp(index + delta, 0, _sources.Count - 1);
        if (next == index)
        {
            return;
        }

        _sources.RemoveAt(index);
        _sources.Insert(next, source);

        if (next == 0)
        {
            _currentAspectRatio = source.AspectRatio;
            ApplyDimensions(null, null, _currentAspectRatio);
        }
        SaveConfig();
    }

    private void RemoveSource(CaptureSource source)
    {
        int index = _sources.IndexOf(source);
        if (index < 0)
        {
            return;
        }

        _sources.RemoveAt(index);
        Logger.Info($"Removed source: {source.DisplayName} ({source.Type})");

        if (_sources.Count == 0)
        {
            ClearSources();
            return;
        }

        if (_sources.All(s => s.Type != CaptureSource.SourceType.Webcam))
        {
            _webcamCapture.Reset();
        }

        if (index == 0)
        {
            _currentAspectRatio = _sources[0].AspectRatio;
            ApplyDimensions(null, null, _currentAspectRatio);
        }
        else
        {
            RenderFrame();
        }
        SaveConfig();
    }

    private void ClearSources()
    {
        bool hadSources = _sources.Count > 0;
        _sources.Clear();
        _lastCompositeFrame = null;
        _preserveResolution = false;
        _passthroughEnabled = false;
        _webcamCapture.Reset();
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
        if (Math.Abs(_currentAspectRatio - DefaultAspectRatio) > 0.0001)
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

        double thresholdHint = Math.Min(_captureThresholdMin, _captureThresholdMax);
        var removed = new List<CaptureSource>();
        var primaryBeforeRemoval = _sources.Count > 0 ? _sources[0] : null;

        foreach (var source in _sources.ToList())
        {
            SourceFrame? frame = null;
            try
            {
                if (source.Type == CaptureSource.SourceType.Window && source.Window != null)
                {
                    var windowFrame = _windowCapture.CaptureFrame(source.Window, _engine.Columns, _engine.Rows, thresholdHint);
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
                    var webcamFrame = _webcamCapture.CaptureFrame(source.WebcamId, _engine.Columns, _engine.Rows);
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
                if (isWebcam)
                {
                    source.MissedFrames++;
                    var age = DateTime.UtcNow - source.AddedUtc;
                    if (source.MissedFrames <= 90 || age < TimeSpan.FromSeconds(3))
                    {
                        if (source.MissedFrames % 30 == 0)
                        {
                            Logger.Warn($"Waiting for webcam frames from {source.DisplayName}; missed {source.MissedFrames} so far.");
                        }
                        continue;
                    }
                    Logger.Warn($"Webcam frames never arrived; removing source {source.DisplayName} after {source.MissedFrames} misses.");
                    removed.Add(source);
                    continue;
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
            bool primaryRemoved = primaryBeforeRemoval != null && removed.Contains(primaryBeforeRemoval);
            foreach (var source in removed)
            {
                _sources.Remove(source);
            }

            if (_sources.All(s => s.Type != CaptureSource.SourceType.Webcam))
            {
                _webcamCapture.Reset();
            }

            if (_sources.Count == 0)
            {
                ClearSources();
                return;
            }

            if (primaryRemoved)
            {
                _currentAspectRatio = _sources[0].AspectRatio;
                ApplyDimensions(null, null, _currentAspectRatio);
                Logger.Info($"Primary source removed; new primary is {_sources[0].DisplayName} ({_sources[0].Type})");
            }
        }

        var composite = BuildCompositeFrame();
        if (composite == null)
        {
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

    private CompositeFrame? BuildCompositeFrame()
    {
        int downscaledWidth = _engine.Columns;
        int downscaledHeight = _engine.Rows;
        int downscaledLength = downscaledWidth * downscaledHeight * 4;

        if (_compositeDownscaledBuffer == null || _compositeDownscaledBuffer.Length != downscaledLength)
        {
            _compositeDownscaledBuffer = new byte[downscaledLength];
        }

        int targetWidth = _preserveResolution && _sources.Count > 0
            ? (_sources[0].LastFrame?.SourceWidth > 0
                ? _sources[0].LastFrame!.SourceWidth
                : _sources[0].FallbackWidth ?? downscaledWidth)
            : downscaledWidth;
        int targetHeight = _preserveResolution && _sources.Count > 0
            ? (_sources[0].LastFrame?.SourceHeight > 0
                ? _sources[0].LastFrame!.SourceHeight
                : _sources[0].FallbackHeight ?? downscaledHeight)
            : downscaledHeight;

        if (targetWidth <= 0 || targetHeight <= 0)
        {
            targetWidth = downscaledWidth;
            targetHeight = downscaledHeight;
        }

        byte[]? highResBuffer = null;
        if (_preserveResolution)
        {
            int highResLength = targetWidth * targetHeight * 4;
            if (_compositeHighResBuffer == null || _compositeHighResBuffer.Length != highResLength)
            {
                _compositeHighResBuffer = new byte[highResLength];
            }

            highResBuffer = _compositeHighResBuffer;
        }

        bool wroteDownscaled = false;
        bool wroteHighRes = false;
        bool primedDownscaled = false;
        bool primedHighRes = false;

        foreach (var source in _sources)
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
                CopyIntoBuffer(_compositeDownscaledBuffer, downscaledWidth, downscaledHeight,
                    frame.Downscaled, frame.DownscaledWidth, frame.DownscaledHeight, source.Opacity, source.Mirror && source.Type == CaptureSource.SourceType.Webcam);
                primedDownscaled = true;
                wroteDownscaled = true;
            }
            else
            {
                CompositeIntoBuffer(_compositeDownscaledBuffer, downscaledWidth, downscaledHeight,
                    frame.Downscaled, frame.DownscaledWidth, frame.DownscaledHeight, source.BlendMode, source.Opacity, source.Mirror && source.Type == CaptureSource.SourceType.Webcam);
                wroteDownscaled = true;
            }

            if (highResBuffer != null && frame.Source != null)
            {
                var sourceBuffer = frame.Source;
                int sourceWidth = frame.SourceWidth;
                int sourceHeight = frame.SourceHeight;

                if (!primedHighRes)
                {
                    CopyIntoBuffer(highResBuffer, targetWidth, targetHeight, sourceBuffer, sourceWidth, sourceHeight, source.Opacity, source.Mirror && source.Type == CaptureSource.SourceType.Webcam);
                    primedHighRes = true;
                    wroteHighRes = true;
                }
                else
                {
                    CompositeIntoBuffer(highResBuffer, targetWidth, targetHeight, sourceBuffer, sourceWidth, sourceHeight, source.BlendMode, source.Opacity, source.Mirror && source.Type == CaptureSource.SourceType.Webcam);
                    wroteHighRes = true;
                }
            }
        }

        if (!wroteDownscaled)
        {
            return null;
        }

        return new CompositeFrame(_compositeDownscaledBuffer, downscaledWidth, downscaledHeight,
            wroteHighRes ? highResBuffer : null, targetWidth, targetHeight);
    }

    private void CopyIntoBuffer(byte[] destination, int destWidth, int destHeight, byte[] source, int sourceWidth, int sourceHeight, double opacity, bool mirror)
    {
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        int destStride = destWidth * 4;
        int sourceStride = sourceWidth * 4;
        var destSpan = new Span<byte>(destination);
        var sourceSpan = new ReadOnlySpan<byte>(source);

        double scaleX = sourceWidth / (double)destWidth;
        double scaleY = sourceHeight / (double)destHeight;

        for (int row = 0; row < destHeight; row++)
        {
            int srcY = Math.Min(sourceHeight - 1, (int)Math.Floor(row * scaleY));
            int destRowOffset = row * destStride;
            int srcRowOffset = srcY * sourceStride;
            for (int col = 0; col < destWidth; col++)
            {
                int sampleX = Math.Min(sourceWidth - 1, (int)Math.Floor(col * scaleX));
                int srcX = mirror ? (sourceWidth - 1 - sampleX) : sampleX;
                int destIndex = destRowOffset + (col * 4);
                int srcIndex = srcRowOffset + (srcX * 4);

                byte sb = sourceSpan[srcIndex];
                byte sg = sourceSpan[srcIndex + 1];
                byte sr = sourceSpan[srcIndex + 2];

                destSpan[destIndex] = ClampToByte((int)(sb * opacity));
                destSpan[destIndex + 1] = ClampToByte((int)(sg * opacity));
                destSpan[destIndex + 2] = ClampToByte((int)(sr * opacity));
                destSpan[destIndex + 3] = 255;
            }
        }
    }

    private void CompositeIntoBuffer(byte[] destination, int destWidth, int destHeight, byte[] source, int sourceWidth, int sourceHeight, BlendMode mode, double opacity, bool mirror)
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
        var destSpan = new Span<byte>(destination);
        var sourceSpan = new ReadOnlySpan<byte>(source);

        if (destWidth == sourceWidth && destHeight == sourceHeight)
        {
            for (int row = 0; row < destHeight; row++)
            {
                int destRowOffset = row * destStride;
                int srcRowOffset = row * sourceStride;
                for (int col = 0; col < destWidth; col++)
                {
                    int destIndex = destRowOffset + (col * 4);
                    int sampleX = mirror ? (sourceWidth - 1 - col) : col;
                    int srcIndex = srcRowOffset + (sampleX * 4);
                    BlendInto(destSpan, sourceSpan, destIndex, srcIndex, mode, opacity);
                }
            }
            return;
        }

        double scaleX = sourceWidth / (double)destWidth;
        double scaleY = sourceHeight / (double)destHeight;
        for (int row = 0; row < destHeight; row++)
        {
            int srcY = Math.Min(sourceHeight - 1, (int)Math.Floor(row * scaleY));
            int destRowOffset = row * destStride;
            int srcRowOffset = srcY * sourceStride;
            for (int col = 0; col < destWidth; col++)
            {
                int sampleX = Math.Min(sourceWidth - 1, (int)Math.Floor(col * scaleX));
                int srcX = mirror ? (sourceWidth - 1 - sampleX) : sampleX;
                int destIndex = destRowOffset + (col * 4);
                int srcIndex = srcRowOffset + (srcX * 4);
                BlendInto(destSpan, sourceSpan, destIndex, srcIndex, mode, opacity);
            }
        }
    }

    private static void BlendInto(Span<byte> destination, ReadOnlySpan<byte> source, int destIndex, int srcIndex, BlendMode mode, double opacity)
    {
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        byte db = destination[destIndex];
        byte dg = destination[destIndex + 1];
        byte dr = destination[destIndex + 2];

        byte sb = source[srcIndex];
        byte sg = source[srcIndex + 1];
        byte sr = source[srcIndex + 2];

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

        for (int row = 0; row < _engine.Rows; row++)
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
        }
    }

    private void InitializeEffect()
    {
        _overlayBrush = new ImageBrush
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center
        };
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

        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = (byte)(255 - buffer[i]);         // B
            buffer[i + 1] = (byte)(255 - buffer[i + 1]); // G
            buffer[i + 2] = (byte)(255 - buffer[i + 2]); // R
        }
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

    private void SetFramerate(double fps)
    {
        fps = Math.Clamp(fps, 5, 120);
        _currentFps = fps;
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / _currentFps);
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
                bool isChecked = header.StartsWith("15", StringComparison.OrdinalIgnoreCase) && Math.Abs(_currentFps - 15) < 0.1
                                 || header.StartsWith("30", StringComparison.OrdinalIgnoreCase) && Math.Abs(_currentFps - 30) < 0.1
                                 || header.StartsWith("60", StringComparison.OrdinalIgnoreCase) && Math.Abs(_currentFps - 60) < 0.1;
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
        for (int row = 0; row < rows; row++)
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
        }

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
        for (int row = 0; row < rows; row++)
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
        }

        return (rMask, gMask, bMask);
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
            _currentFps = Math.Clamp(config.Framerate, 5, 120);
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
            _configuredColumns = Math.Clamp(config.Columns, 32, 512);
            _configuredDepth = Math.Clamp(config.Depth, 3, 96);
            _passthroughEnabled = config.Passthrough;
            if (Enum.TryParse<BlendMode>(config.BlendMode, out var blendMode))
            {
                _blendMode = blendMode;
            }

            RestoreSources(config.Sources);
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
                Framerate = _currentFps,
                LifeMode = _lifeMode.ToString(),
                BinningMode = _binningMode.ToString(),
                InjectionMode = _injectionMode.ToString(),
                PreserveResolution = _preserveResolution,
                InjectionNoise = _injectionNoise,
                LifeOpacity = _lifeOpacity,
                InvertComposite = _invertComposite,
                ShowFps = _showFps,
                Columns = _configuredColumns,
                Depth = _configuredDepth,
                Passthrough = _passthroughEnabled,
                BlendMode = _blendMode.ToString(),
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

    private List<AppConfig.SourceConfig> BuildSourceConfigs()
    {
        var configs = new List<AppConfig.SourceConfig>(_sources.Count);
        foreach (var source in _sources)
        {
            configs.Add(new AppConfig.SourceConfig
            {
                Type = source.Type.ToString(),
                WindowTitle = source.Window?.Title,
                WebcamId = source.WebcamId,
                DisplayName = source.DisplayName,
                BlendMode = source.BlendMode.ToString(),
                Opacity = source.Opacity,
                Mirror = source.Mirror
            });
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

        foreach (var config in configs)
        {
            if (!Enum.TryParse<CaptureSource.SourceType>(config.Type, true, out var type))
            {
                continue;
            }

            CaptureSource? restored = null;
            switch (type)
            {
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
            }

            if (restored == null)
            {
                continue;
            }

            ApplySourceSettings(restored, config);
            _sources.Add(restored);
        }

        if (_sources.Count > 0)
        {
            _currentAspectRatio = _sources[0].AspectRatio;
            ApplyDimensions(null, null, _currentAspectRatio, persist: false);
        }
    }

    private static void ApplySourceSettings(CaptureSource source, AppConfig.SourceConfig config)
    {
        if (Enum.TryParse<BlendMode>(config.BlendMode, true, out var blend))
        {
            source.BlendMode = blend;
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
        public int Columns { get; set; } = DefaultColumns;
        public int Depth { get; set; } = DefaultDepth;
        public bool Passthrough { get; set; }
        public string BlendMode { get; set; } = MainWindow.BlendMode.Additive.ToString();
        public List<SourceConfig> Sources { get; set; } = new();

        public sealed class SourceConfig
        {
            public string Type { get; set; } = CaptureSource.SourceType.Window.ToString();
            public string? WindowTitle { get; set; }
            public string? WebcamId { get; set; }
            public string? DisplayName { get; set; }
            public string BlendMode { get; set; } = MainWindow.BlendMode.Normal.ToString();
            public double Opacity { get; set; } = 1.0;
            public bool Mirror { get; set; }
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
            Webcam
        }

        private CaptureSource(SourceType type, WindowHandleInfo? window, string? webcamId, string displayName)
        {
            Type = type;
            Window = window;
            WebcamId = webcamId;
            DisplayName = displayName;
        }

        public static CaptureSource CreateWindow(WindowHandleInfo window) =>
            new(SourceType.Window, window, null, window.Title) { AddedUtc = DateTime.UtcNow };

        public static CaptureSource CreateWebcam(string webcamId, string name) =>
            new(SourceType.Webcam, null, webcamId, name) { AddedUtc = DateTime.UtcNow };

        public SourceType Type { get; }
        public WindowHandleInfo? Window { get; set; }
        public string? WebcamId { get; }
        public string DisplayName { get; }
        public BlendMode BlendMode { get; set; } = BlendMode.Normal;
        public SourceFrame? LastFrame { get; set; }
        public bool HasError { get; set; }
        public int MissedFrames { get; set; }
        public bool FirstFrameReceived { get; set; }
        public DateTime AddedUtc { get; set; }
        public double Opacity { get; set; } = 1.0;
        public bool Mirror { get; set; }

        public double AspectRatio
        {
            get
            {
                if (LastFrame != null && LastFrame.SourceHeight > 0)
                {
                    return Math.Max(0.05, LastFrame.SourceWidth / (double)LastFrame.SourceHeight);
                }

                if (Window != null)
                {
                    return Window.AspectRatio;
                }

                return DefaultAspectRatio;
            }
        }

        public int? FallbackWidth => Type == SourceType.Window ? Window?.Width : LastFrame?.SourceWidth;
        public int? FallbackHeight => Type == SourceType.Window ? Window?.Height : LastFrame?.SourceHeight;
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
