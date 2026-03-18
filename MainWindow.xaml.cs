using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Threading;
using System.Windows.Shell;
using Windows.Devices.Enumeration;

namespace lifeviz;

public partial class MainWindow : Window
{
    private enum ChromeResizeDirection
    {
        None,
        Left,
        Right,
        Bottom,
        BottomLeft,
        BottomRight
    }

    private const int DefaultRows = 144;
    private const int MinRows = 72;
    private const int MaxRows = 2160;
    private const int DefaultDepth = 24;
    private const double DefaultAspectRatio = 16d / 9d;
    private const double DefaultFps = 60;
    private const double DefaultAnimationBpm = 140;
    private const double AnimationZoomScale = 0.2;
    private const double AnimationTranslateFactor = 0.1;
    private const double AnimationRotateDegrees = 12;
    private const double AnimationDvdScale = 0.2;
    private const double AnimationDvdCycleBeats = 4.0;
    private const double AnimationDvdAspectFactor = 1.3;
    private const double AnimationBeatShakeFactor = 0.03;
    private const double AnimationBeatShakeFrequency = 24.0;
    private const double AnimationBeatShakeWindowBeats = 0.25;
    private const double MaxBeatShakeIntensity = 2.0;
    private const double MaxAudioGranularIntensity = 10.0;
    private const double DefaultAudioGranularEqBandGain = 1.0;
    private const double MaxAudioGranularEqBandGain = 3.0;
    private const double DefaultKeyTolerance = 0.1;
    private const double DefaultAudioReactiveEnergyGain = 16.0;
    private const double DefaultAudioReactiveFpsBoost = 1.0;
    private const double DefaultAudioReactiveFpsMinPercent = 0.10;
    private const double DefaultAudioReactiveLifeOpacityMinScalar = 0.25;
    private const int DefaultAudioReactiveLevelSeedMaxBursts = 24;
    private const double DefaultAudioInputGain = 1.0;
    private const double DefaultAudioOutputGain = 1.0;
    private const double MinAudioInputGain = 0.0;
    private const double MaxAudioInputGain = 2.0;
    private const int AudioDebugHistorySeconds = 30;
    private const int AudioDebugHistorySampleRate = 120;
    private const int AudioDebugHistorySize = AudioDebugHistorySeconds * AudioDebugHistorySampleRate;
    private const double DebugTimingOverlayRefreshRateHz = 6.0;
    private const double DebugAudioOverlayRefreshRateHz = 3.0;
    private const int DebugOverlayMaxPoints = 640;
    private const double FrameTimingOverlayMaxMs = 50.0;
    private const int DefaultAudioReactiveSeedsPerBeat = 2;
    private const double DefaultAudioReactiveSeedCooldownMs = 180.0;
    private const int MaxAudioReactiveSeedBurstsPerStep = 64;
    private const int MaxSimulationStepsPerRender = 8;
    private const double MaxRgbHueShiftSpeedDegreesPerSecond = 180.0;
    private const int DefaultDecoderThreadLimit = 0;
    private const int DefaultVideoDecodeFpsLimit = 0;
    private const double MinReactiveHueFrequencyHz = 27.5;
    private const double MaxReactiveHueFrequencyHz = 4186.01;
    private const double MaxColorDistance = 441.6729559300637;
    private const double UiInteractionThrottleFps = 20.0;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmMouseMove = 0x0200;
    private const string GitHubRepoOwner = "SlimeQ";
    private const string GitHubRepoName = "lifeviz";
    private const string GitHubReleaseAssetName = "lifeviz_installer.exe";
    private static readonly string StartupRecoveryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lifeviz", "startup-recovery.flag");
    private static readonly Uri GitHubLatestReleaseUri = new($"https://api.github.com/repos/{GitHubRepoOwner}/{GitHubRepoName}/releases/latest");
    private static readonly JsonSerializerOptions GitHubJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly (int rowOffset, int colOffset)[] GliderPattern =
    {
        (0, 1), (1, 2), (2, 0), (2, 1), (2, 2)
    };
    private static readonly (int rowOffset, int colOffset)[] RPentominoPattern =
    {
        (0, 1), (0, 2), (1, 0), (1, 1), (2, 1)
    };

    private readonly ISimulationBackend _engine;
    private readonly List<SimulationLayerState> _simulationLayers = new();
    private readonly List<ISimulationBackend> _retiredSimulationBackends = new();
    private readonly WindowCaptureService _windowCapture = new();
    private readonly WebcamCaptureService _webcamCapture = new();
    private readonly FileCaptureService _fileCapture = new();
    private readonly AudioBeatDetector _audioBeatDetector = new();
    private IRenderBackend _renderBackend = new NullRenderBackend();
    private LayerEditorWindow? _layerEditorWindow;
    private bool _suppressLayerEditorRefresh;
    private int _configuredRows = DefaultRows;
    private int _configuredDepth = DefaultDepth;
    private int? _pendingLegacyColumns;
    private bool _suppressWindowResize;
    private Size _lastWindowSize;
    private Size _lastClientSize;
    private bool _isRecording;
    private RecordingSession? _recordingSession;
    private Stopwatch? _recordingStopwatch;
    private TimeSpan _recordingFrameInterval;
    private TimeSpan _nextRecordingFrameTime;
    private int _recordingWidth;
    private int _recordingHeight;
    private int _recordingSourceWidth;
    private int _recordingSourceHeight;
    private int _recordingDisplayWidth;
    private int _recordingDisplayHeight;
    private int _recordingScale = 1;
    private RecordingQuality _recordingQuality = RecordingQuality.High;
    private string? _recordingPath;
    private ImageSource? _recordingOverlayIcon;
    private IReadOnlyList<WindowHandleInfo> _cachedWindows = Array.Empty<WindowHandleInfo>();
    private IReadOnlyList<WebcamCaptureService.CameraInfo> _cachedCameras = Array.Empty<WebcamCaptureService.CameraInfo>();
    private IReadOnlyList<AudioBeatDetector.AudioDeviceInfo> _cachedAudioDevices = Array.Empty<AudioBeatDetector.AudioDeviceInfo>();
    private readonly List<CaptureSource> _sources = new();
    private byte[]? _pixelBuffer;
    private byte[]? _compositeDownscaledBuffer;
    private byte[]? _invertScratchBuffer;
    private bool _suppressSmokeIntermediateSimGroupReadback;
    private CompositeFrame? _lastCompositeFrame;
    private int _displayWidth;
    private int _displayHeight;
    private int[] _rowMap = Array.Empty<int>();
    private int[] _colMap = Array.Empty<int>();
    private bool _isShuttingDown;
    private Exception? _shutdownException;
    private bool _renderLoopAttached;
    private int _uiInteractionSuspendCount;
    private bool _isPaused;
    private double _currentAspectRatio = DefaultAspectRatio;
    private bool _aspectRatioLocked;
    private double _lockedAspectRatio = DefaultAspectRatio;
    private IntPtr _windowHandle;
    private bool _passthroughEnabled;
    private bool _passthroughCompositedInPixelBuffer;
    private BlendMode _blendMode = BlendMode.Additive;
    private double _lifeOpacity = 1.0;
    private double _rgbHueShiftDegrees;
    private double _rgbHueShiftSpeedDegreesPerSecond;
    private bool _suppressThresholdControlEvents;
    private bool _invertComposite;
    private bool _showFps;
    private readonly Stopwatch _simulationFpsStopwatch = new();
    private int _simulationFrames;
    private int _renderFrames;
    private double _renderDisplayFps;
    private double _presentationDisplayFps;
    private double _simulationDisplayFps;
    private int _lastSimulationStepsThisFrame;
    private int _lastPresentationDrawCount;
    private GameOfLifeEngine.LifeMode _lifeMode = GameOfLifeEngine.LifeMode.NaiveGrayscale;
    private GameOfLifeEngine.BinningMode _binningMode = GameOfLifeEngine.BinningMode.Fill;
    private GameOfLifeEngine.InjectionMode _injectionMode = GameOfLifeEngine.InjectionMode.Threshold;
    private double _currentFps = DefaultFps;
    private double _currentSimulationTargetFps = DefaultFps;
    private double _animationBpm = DefaultAnimationBpm;
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
    private WindowStyle _previousWindowStyle = WindowStyle.None;
    private ResizeMode _previousResizeMode = ResizeMode.NoResize;
    private bool _previousTopmost;
    private Rect _previousBounds;
    private readonly Stopwatch _stepStopwatch = new();
    private readonly Stopwatch _lifetimeStopwatch = new();
    private Thread? _framePumpThread;
    private readonly AutoResetEvent _framePumpWakeEvent = new(false);
    private int _frameCallbackQueued;
    private long _nextFramePumpTimestamp;
    private int _framePumpStopRequested;
    private readonly FrameProfiler _frameProfiler = new();
    private readonly UiThreadLatencyProbe _uiThreadLatencyProbe;
    private readonly Dictionary<string, RollingMetricWindow> _liveMetrics = new(StringComparer.Ordinal);
    private double _timeSinceLastStep;
    private double _lastUiInteractionRenderTime;
    private bool _fpsOscillationEnabled;
    private bool _audioSyncEnabled;
    private bool _animationAudioSyncEnabled;
    private bool _sourceAudioMasterEnabled = true;
    private double _sourceAudioMasterVolume = 1.0;
    private string? _selectedAudioDeviceId;
    private double _oscillationBpm = 140;
    private double _oscillationMinFps = 30;
    private double _oscillationMaxFps = 60;
    private bool _updateInProgress;
    private double _smoothedEnergy;
    private double _fastAudioLevel;
    private double _smoothedLevelEnergy;
    private double _smoothedBass;
    private double _smoothedFreq;
    private readonly Queue<double> _frameGapHistory = new();
    private double _frameGapHistoryAccumulator;
    private double _lastTimingOverlayUpdateTime = double.NegativeInfinity;
    private double _lastAudioOverlayUpdateTime = double.NegativeInfinity;
    private readonly Random _audioReactiveRandom = new();
    private bool _audioReactiveEnabled;
    private bool _audioReactiveLevelToFpsEnabled = true;
    private bool _audioReactiveLevelToLifeOpacityEnabled;
    private bool _audioReactiveLevelSeedEnabled = true;
    private bool _audioReactiveBeatSeedEnabled = true;
    private double _audioInputGainCapture = DefaultAudioInputGain;
    private double _audioInputGainRender = DefaultAudioOutputGain;
    private double _audioInputGain = DefaultAudioInputGain;
    private double _audioReactiveEnergyGain = DefaultAudioReactiveEnergyGain;
    private double _audioReactiveFpsBoost = DefaultAudioReactiveFpsBoost;
    private double _audioReactiveFpsMinPercent = DefaultAudioReactiveFpsMinPercent;
    private double _audioReactiveLifeOpacityMinScalar = DefaultAudioReactiveLifeOpacityMinScalar;
    private int _audioReactiveLevelSeedMaxBursts = DefaultAudioReactiveLevelSeedMaxBursts;
    private int _audioReactiveSeedsPerBeat = DefaultAudioReactiveSeedsPerBeat;
    private double _audioReactiveSeedCooldownMs = DefaultAudioReactiveSeedCooldownMs;
    private AudioReactiveSeedPattern _audioReactiveSeedPattern = AudioReactiveSeedPattern.Glider;
    private double _audioReactiveFpsMultiplier = 1.0;
    private int _audioReactiveLevelSeedBurstsLastStep;
    private int _audioReactiveBeatSeedBurstsLastStep;
    private long _lastAudioReactiveBeatCount;
    private DateTime _lastAudioReactiveSeedUtc = DateTime.MinValue;
    private double _effectiveLifeOpacity = 1.0;
    private long _lastProfileFrameTimestamp;
    private bool _liveProfileExportInProgress;
    private bool _lowContentionMode;
    private int _decoderThreadLimit = DefaultDecoderThreadLimit;
    private int _videoDecodeFpsLimit = DefaultVideoDecodeFpsLimit;
    private bool _highResolutionTimerEnabled;
    private bool _isChromeDragging;
    private Point _chromeDragStartScreen;
    private Point _chromeDragStartWindow;
    private bool _isChromeResizing;
    private ChromeResizeDirection _chromeResizeDirection;
    private Point _chromeResizeStartScreen;
    private Rect _chromeResizeStartBounds;
    private readonly bool _startupRecoveryTriggered;
    private readonly CpuSourceCompositor _inlineSourceCompositor;
    private readonly GpuSourceCompositor _inlineGpuSourceCompositor;
    private readonly GpuSimulationGroupCompositor _gpuSimulationGroupCompositor;
    private readonly GpuPresentationSurfaceSnapshotter _inlineGpuPresentationSnapshotter;
    private int _pendingInlineSimulationStepsThisFrame;
    private DateTime _lastInlinePresentationFallbackLogUtc = DateTime.MinValue;
    private int _inlineGpuPresentationFallbackCount;

    public MainWindow()
    {
        Logger.Initialize();
        _engine = new GpuSimulationBackend();
        _inlineSourceCompositor = new CpuSourceCompositor(this);
        _inlineGpuSourceCompositor = new GpuSourceCompositor(this);
        _gpuSimulationGroupCompositor = new GpuSimulationGroupCompositor();
        _inlineGpuPresentationSnapshotter = new GpuPresentationSurfaceSnapshotter();
        _startupRecoveryTriggered = TryConsumeStartupRecoveryFlag();
        MarkStartupPending();

        _uiThreadLatencyProbe = new UiThreadLatencyProbe(Dispatcher, _frameProfiler);
        InitializeComponent();
        _renderBackend = CreateRenderBackend(this, RenderSurfaceHost, GameImage);
        bool allowFullSmokeStartup = App.IsSmokeTestMode && App.LoadUserConfigInSmokeTest;
        if (!App.IsSmokeTestMode || allowFullSmokeStartup)
        {
            ApplyAudioInputGainForSelection();
        }

        Loaded += (_, _) =>
        {
            if (!App.IsSmokeTestMode || allowFullSmokeStartup)
            {
                LoadConfig();
                InitializeVisualizer();
                if (_pendingFullscreen)
                {
                    EnterFullscreen(applyConfig: true);
                }
            }

            ApplyPerformancePreferences();
            _lastWindowSize = new Size(ActualWidth, ActualHeight);
            _lastClientSize = new Size(Root.ActualWidth, Root.ActualHeight);
            UpdateChromeUi();
            MarkStartupComplete();
            Logger.Info(App.IsSmokeTestMode
                ? "Main window loaded in smoke test mode."
                : "Main window loaded and visualizer initialized.");
        };
        SourceInitialized += (_, _) =>
        {
            var helper = new WindowInteropHelper(this);
            _windowHandle = helper.Handle;
            if (HwndSource.FromHwnd(_windowHandle) is { } source)
            {
                source.AddHook(MainWindow_WndProc);
            }

            UpdateChromeUi();
        };
        Closed += (_, _) =>
        {
            if (_isShuttingDown)
            {
                return;
            }

            _isShuttingDown = true;
            DetachRenderLoop();
            _uiThreadLatencyProbe.Stop();

            if (_layerEditorWindow != null)
            {
                _layerEditorWindow.PrepareForOwnerShutdown();
                _layerEditorWindow.Close();
                _layerEditorWindow = null;
            }

            try
            {
                StopRecording(showMessage: false);
                var renderBackend = _renderBackend;
                _renderBackend = new NullRenderBackend();
                _pixelBuffer = null;
                renderBackend.Dispose();
                _inlineGpuSourceCompositor.Dispose();
                _gpuSimulationGroupCompositor.Dispose();
                _inlineGpuPresentationSnapshotter.Dispose();
                foreach (var backend in EnumerateSimulationLeafLayers(_simulationLayers).Select(layer => layer.Engine).OfType<ISimulationBackend>().Distinct())
                {
                    backend.Dispose();
                }
                foreach (var backend in _retiredSimulationBackends.Distinct())
                {
                    backend.Dispose();
                }
                _retiredSimulationBackends.Clear();
                _webcamCapture.Reset();
                _fileCapture.Dispose();
                _audioBeatDetector.Dispose();
            }
            catch (Exception ex)
            {
                _shutdownException = ex;
                Logger.Error("Main window shutdown failed.", ex);
            }
            finally
            {
                MarkStartupComplete();
                DisableHighResolutionTimer();
            }

            Logger.Shutdown();
        };
    }

    internal string? GetShutdownErrorMessage() => _shutdownException?.ToString();

    private void UpdateChromeUi()
    {
        bool showChrome = !_isFullscreen;
        if (ChromeBar != null)
        {
            ChromeBar.Visibility = showChrome ? Visibility.Visible : Visibility.Collapsed;
        }

        if (LeftResizeGrip != null)
        {
            LeftResizeGrip.Visibility = showChrome ? Visibility.Visible : Visibility.Collapsed;
        }
        if (RightResizeGrip != null)
        {
            RightResizeGrip.Visibility = showChrome ? Visibility.Visible : Visibility.Collapsed;
        }
        if (BottomResizeGrip != null)
        {
            BottomResizeGrip.Visibility = showChrome ? Visibility.Visible : Visibility.Collapsed;
        }
        if (BottomLeftResizeGrip != null)
        {
            BottomLeftResizeGrip.Visibility = showChrome ? Visibility.Visible : Visibility.Collapsed;
        }
        if (BottomRightResizeGrip != null)
        {
            BottomRightResizeGrip.Visibility = showChrome ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ChromeBar_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryBeginWindowDragOrToggleFullscreen(e))
        {
            return;
        }
    }

    private void BackgroundDragSurface_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryBeginWindowDragOrToggleFullscreen(e))
        {
            return;
        }
    }

    private bool TryBeginWindowDragOrToggleFullscreen(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return false;
        }

        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (FindAncestor<Button>(source) != null ||
            IsResizeGripSource(source))
        {
            return false;
        }

        if (e.ClickCount >= 2)
        {
            ToggleFullscreen_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return true;
        }

        if (_isFullscreen)
        {
            return false;
        }

        _isChromeDragging = true;
        _chromeDragStartScreen = GetScreenDipPosition(e);
        _chromeDragStartWindow = new Point(Left, Top);
        Mouse.Capture(this);
        e.Handled = true;
        return true;
    }

    private static bool IsResizeGripSource(DependencyObject? source)
    {
        var element = FindAncestor<FrameworkElement>(source);
        if (element == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(element.Name) && element.Name.EndsWith("ResizeGrip", StringComparison.Ordinal))
        {
            return true;
        }

        return TryParseResizeDirection(element.Tag as string, out var direction) &&
               direction != ChromeResizeDirection.None;
    }

    private void ChromeBar_OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var screen = GetScreenDipPosition(e);
        OpenRootContextMenuAtScreenPoint(screen.X, screen.Y);
        e.Handled = true;
    }

    private void ResizeGrip_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isFullscreen || e.ChangedButton != MouseButton.Left || sender is not FrameworkElement grip)
        {
            return;
        }

        if (!TryParseResizeDirection(grip.Tag as string, out var direction) || direction == ChromeResizeDirection.None)
        {
            return;
        }

        _isChromeResizing = true;
        _chromeResizeDirection = direction;
        _chromeResizeStartScreen = GetScreenDipPosition(e);
        _chromeResizeStartBounds = new Rect(Left, Top, Width, Height);
        Mouse.Capture(this);
        e.Handled = true;
    }

    private void WindowChrome_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isChromeDragging)
        {
            Point screen = GetScreenDipPosition(e);
            Vector delta = screen - _chromeDragStartScreen;
            Left = _chromeDragStartWindow.X + delta.X;
            Top = _chromeDragStartWindow.Y + delta.Y;
            e.Handled = true;
            return;
        }

        if (_isChromeResizing)
        {
            Point screen = GetScreenDipPosition(e);
            ApplyChromeResize(screen);
            e.Handled = true;
        }
    }

    private void WindowChrome_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isChromeDragging && !_isChromeResizing)
        {
            return;
        }

        _isChromeDragging = false;
        _isChromeResizing = false;
        _chromeResizeDirection = ChromeResizeDirection.None;
        if (Mouse.Captured == this)
        {
            Mouse.Capture(null);
        }

        _lastWindowSize = new Size(ActualWidth, ActualHeight);
        _lastClientSize = new Size(Root.ActualWidth, Root.ActualHeight);

        e.Handled = true;
    }

    private void ChromeMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ChromeMenuButton == null)
        {
            return;
        }

        var anchor = ChromeMenuButton.PointToScreen(new Point(0, ChromeMenuButton.ActualHeight));
        OpenRootContextMenuAtScreenPoint(anchor.X, anchor.Y);
    }

    private void ChromeMinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void ChromeCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyChromeResize(Point screenDip)
    {
        const double MinWindowWidth = 360.0;
        const double MinWindowHeight = 220.0;
        double aspect = _currentAspectRatio > 0 ? _currentAspectRatio : DefaultAspectRatio;

        Vector delta = screenDip - _chromeResizeStartScreen;
        double left = _chromeResizeStartBounds.Left;
        double top = _chromeResizeStartBounds.Top;
        double width = _chromeResizeStartBounds.Width;
        double height = _chromeResizeStartBounds.Height;

        switch (_chromeResizeDirection)
        {
            case ChromeResizeDirection.Left:
                left = _chromeResizeStartBounds.Left + delta.X;
                width = _chromeResizeStartBounds.Width - delta.X;
                break;
            case ChromeResizeDirection.Right:
                width = _chromeResizeStartBounds.Width + delta.X;
                break;
            case ChromeResizeDirection.Bottom:
                height = _chromeResizeStartBounds.Height + delta.Y;
                break;
            case ChromeResizeDirection.BottomLeft:
                left = _chromeResizeStartBounds.Left + delta.X;
                width = _chromeResizeStartBounds.Width - delta.X;
                height = _chromeResizeStartBounds.Height + delta.Y;
                break;
            case ChromeResizeDirection.BottomRight:
                width = _chromeResizeStartBounds.Width + delta.X;
                height = _chromeResizeStartBounds.Height + delta.Y;
                break;
        }

        if (width < MinWindowWidth)
        {
            if (_chromeResizeDirection == ChromeResizeDirection.Left || _chromeResizeDirection == ChromeResizeDirection.BottomLeft)
            {
                left -= MinWindowWidth - width;
            }
            width = MinWindowWidth;
        }

        if (height < MinWindowHeight)
        {
            height = MinWindowHeight;
        }

        bool horizontalResize = _chromeResizeDirection == ChromeResizeDirection.Left ||
                                _chromeResizeDirection == ChromeResizeDirection.Right ||
                                _chromeResizeDirection == ChromeResizeDirection.BottomLeft ||
                                _chromeResizeDirection == ChromeResizeDirection.BottomRight;
        bool verticalResize = _chromeResizeDirection == ChromeResizeDirection.Bottom ||
                              _chromeResizeDirection == ChromeResizeDirection.BottomLeft ||
                              _chromeResizeDirection == ChromeResizeDirection.BottomRight;

        if (horizontalResize && !verticalResize)
        {
            height = Math.Max(MinWindowHeight, width / aspect);
        }
        else if (verticalResize && !horizontalResize)
        {
            width = Math.Max(MinWindowWidth, height * aspect);
        }
        else if (horizontalResize && verticalResize)
        {
            bool preserveWidth = Math.Abs(delta.X) >= Math.Abs(delta.Y);
            if (preserveWidth)
            {
                height = Math.Max(MinWindowHeight, width / aspect);
            }
            else
            {
                width = Math.Max(MinWindowWidth, height * aspect);
            }
        }

        if ((_chromeResizeDirection == ChromeResizeDirection.Left || _chromeResizeDirection == ChromeResizeDirection.BottomLeft) &&
            Math.Abs(left + width - _chromeResizeStartBounds.Right) > 0.001)
        {
            left = _chromeResizeStartBounds.Right - width;
        }

        _suppressWindowResize = true;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        _suppressWindowResize = false;
    }

    private Point GetScreenDipPosition(MouseEventArgs e)
    {
        Point screen = PointToScreen(e.GetPosition(this));
        if (PresentationSource.FromVisual(this) is HwndSource source && source.CompositionTarget != null)
        {
            return source.CompositionTarget.TransformFromDevice.Transform(screen);
        }

        return screen;
    }

    private static bool TryParseResizeDirection(string? value, out ChromeResizeDirection direction)
    {
        return Enum.TryParse(value, true, out direction);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private long BeginProfileStamp() => Stopwatch.GetTimestamp();

    private void EndProfileStamp(string metricName, long startTimestamp)
    {
        if (startTimestamp == 0)
        {
            return;
        }

        double elapsedMs = FrameProfiler.ElapsedMilliseconds(startTimestamp);
        if (_frameProfiler.IsActive)
        {
            _frameProfiler.RecordSample(metricName, elapsedMs);
        }

        RecordLiveMetric(metricName, elapsedMs);
    }

    private void RecordLiveMetric(string metricName, double value)
    {
        if (string.IsNullOrWhiteSpace(metricName) || double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }

        if (!_liveMetrics.TryGetValue(metricName, out var window))
        {
            window = new RollingMetricWindow(180);
            _liveMetrics.Add(metricName, window);
        }

        window.Add(value);
    }

    private double GetLiveMetricAverage(string metricName)
    {
        return _liveMetrics.TryGetValue(metricName, out var window)
            ? window.Average
            : 0.0;
    }

    internal void StartProfilingSession(string sessionName)
    {
        _frameProfiler.Start(sessionName);
        _lastProfileFrameTimestamp = 0;
        _lastPresentationDrawCount = _renderBackend.PresentationDrawCount;
        _uiThreadLatencyProbe.Start(TimeSpan.FromMilliseconds(50));
    }

    private void ApplyPerformancePreferences()
    {
        if (_lowContentionMode)
        {
            DisableHighResolutionTimer();
        }
        else
        {
            EnableHighResolutionTimer();
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = _lowContentionMode
                ? ProcessPriorityClass.BelowNormal
                : ProcessPriorityClass.Normal;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to apply process priority preference: {ex.Message}");
        }

        try
        {
            _fileCapture.SetPerformanceSettings(_lowContentionMode, _decoderThreadLimit, _videoDecodeFpsLimit);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to apply file-capture performance settings: {ex.Message}");
        }

        try
        {
            if (_framePumpThread != null && _framePumpThread.IsAlive)
            {
                _framePumpThread.Priority = _lowContentionMode ? ThreadPriority.BelowNormal : ThreadPriority.Normal;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to apply frame pump priority preference: {ex.Message}");
        }
    }

    private void UpdatePerformanceMenuState()
    {
        if (LowContentionModeMenuItem != null)
        {
            LowContentionModeMenuItem.IsChecked = _lowContentionMode;
        }

        if (DecoderThreadsMenu == null)
        {
            return;
        }

        string selectedTag = _decoderThreadLimit.ToString(CultureInfo.InvariantCulture);
        foreach (var item in DecoderThreadsMenu.Items)
        {
            if (item is MenuItem menuItem)
            {
                string? tag = menuItem.Tag as string;
                menuItem.IsChecked = string.Equals(tag, selectedTag, StringComparison.Ordinal);
            }
        }

        if (VideoDecodeFpsMenu == null)
        {
            return;
        }

        string selectedVideoDecodeTag = _videoDecodeFpsLimit.ToString(CultureInfo.InvariantCulture);
        foreach (var item in VideoDecodeFpsMenu.Items)
        {
            if (item is MenuItem menuItem)
            {
                string? tag = menuItem.Tag as string;
                menuItem.IsChecked = string.Equals(tag, selectedVideoDecodeTag, StringComparison.Ordinal);
            }
        }
    }

    internal (FrameProfileReport report, string path) StopProfilingSessionAndExport()
    {
        string outputDirectory = App.IsSmokeTestMode || App.IsDiagnosticTestMode
            ? Path.Combine(AppContext.BaseDirectory, "profiles")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "lifeviz",
                "profiles");
        return StopProfilingSessionAndExport(outputDirectory);
    }

    internal (FrameProfileReport report, string path) StopProfilingSessionAndExport(string outputDirectory)
    {
        _uiThreadLatencyProbe.Stop();
        var report = _frameProfiler.Stop();
        string path = _frameProfiler.Export(report, outputDirectory);
        return (report, path);
    }

    internal (int configuredRows, int engineRows, int engineColumns, int surfaceWidth, int surfaceHeight, int layerCount, bool allLayerRowsMatch, bool allLayerColumnsMatch) RunDimensionChangeSmoke(int rows, int depth)
    {
        ApplyProjectSettingsFromEditor(new LayerEditorProjectSettings
        {
            Height = rows,
            Depth = depth,
            Framerate = _currentFpsFromConfig,
            LifeOpacity = _lifeOpacity,
            RgbHueShiftDegrees = _rgbHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = _rgbHueShiftSpeedDegreesPerSecond,
            InvertComposite = _invertComposite,
            Passthrough = _passthroughEnabled,
            CompositeBlendMode = _blendMode.ToString()
        });

        var engine = GetReferenceSimulationEngine();
        var leaves = EnumerateSimulationLeafLayers(_simulationLayers).ToArray();
        bool allLayerRowsMatch = leaves.All(layer => layer.Engine?.Rows == engine.Rows);
        bool allLayerColumnsMatch = leaves.All(layer => layer.Engine?.Columns == engine.Columns);
        return (_configuredRows, engine.Rows, engine.Columns, _renderBackend.PixelWidth, _renderBackend.PixelHeight, leaves.Length, allLayerRowsMatch, allLayerColumnsMatch);
    }

    internal (int configuredRows, int engineRows, int engineColumns, int surfaceWidth, int surfaceHeight, int layerCount, bool allLayerRowsMatch, bool allLayerColumnsMatch) RunSceneEditorDimensionSmoke(int rows, bool liveMode)
    {
        OpenLayerEditor();
        var editor = _layerEditorWindow ?? throw new InvalidOperationException("Scene Editor was not available.");
        editor.SetLiveModeForSmoke(liveMode);
        editor.ApplySimulationHeightForSmoke(rows, applyImmediately: !liveMode);

        var engine = GetReferenceSimulationEngine();
        var leaves = EnumerateSimulationLeafLayers(_simulationLayers).ToArray();
        bool allLayerRowsMatch = leaves.All(layer => layer.Engine?.Rows == engine.Rows);
        bool allLayerColumnsMatch = leaves.All(layer => layer.Engine?.Columns == engine.Columns);
        return (_configuredRows, engine.Rows, engine.Columns, _renderBackend.PixelWidth, _renderBackend.PixelHeight, leaves.Length, allLayerRowsMatch, allLayerColumnsMatch);
    }

    internal bool TryGetSimulationLayerThresholdMinForSmoke(Guid layerId, out double thresholdMin)
    {
        var layer = EnumerateSimulationLeafLayers(_simulationLayers).FirstOrDefault(candidate => candidate.Id == layerId);
        if (layer == null)
        {
            thresholdMin = 0;
            return false;
        }

        thresholdMin = layer.ThresholdMin;
        return true;
    }

    internal bool TryGetSimulationLayerRuntimeInfoForSmoke(Guid layerId, out string layerType, out int pixelSortCellWidth, out int pixelSortCellHeight)
    {
        var layer = EnumerateSimulationLeafLayers(_simulationLayers).FirstOrDefault(candidate => candidate.Id == layerId);
        if (layer == null)
        {
            layerType = string.Empty;
            pixelSortCellWidth = 0;
            pixelSortCellHeight = 0;
            return false;
        }

        layerType = layer.LayerType.ToString();
        pixelSortCellWidth = layer.PixelSortCellWidth;
        pixelSortCellHeight = layer.PixelSortCellHeight;
        return true;
    }

    internal void ApplyCurrentSceneBisectVariantForSmoke(string variant)
    {
        switch (variant)
        {
            case "baseline":
                break;
            case "no-audio":
                ClearAudioDeviceSelection();
                _audioSyncEnabled = false;
                _animationAudioSyncEnabled = false;
                _audioReactiveEnabled = false;
                _audioReactiveLevelToFpsEnabled = false;
                _audioReactiveLevelToLifeOpacityEnabled = false;
                _audioReactiveLevelSeedEnabled = false;
                _audioReactiveBeatSeedEnabled = false;
                UpdateAudioReactiveMenuState();
                break;
            case "no-video":
                foreach (var source in EnumerateSources(_sources))
                {
                    if (!IsVideoSource(source))
                    {
                        continue;
                    }

                    source.Enabled = false;
                    SetSourceVideoPlaybackPaused(source, paused: true);
                }
                break;
            case "no-sim-groups":
                foreach (var source in EnumerateSources(_sources))
                {
                    if (source.Type == CaptureSource.SourceType.SimGroup)
                    {
                        source.Enabled = false;
                    }
                }
                ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
                break;
            case "first-static-only":
            {
                CaptureSource? kept = EnumerateSources(_sources)
                    .FirstOrDefault(source => source.Type != CaptureSource.SourceType.SimGroup && !IsVideoSource(source));
                foreach (var source in EnumerateSources(_sources))
                {
                    bool keep = kept != null && ReferenceEquals(source, kept);
                    source.Enabled = keep;
                    if (!keep && IsVideoSource(source))
                    {
                        SetSourceVideoPlaybackPaused(source, paused: true);
                    }
                }

                foreach (var source in EnumerateSources(_sources))
                {
                    if (source.Type == CaptureSource.SourceType.SimGroup)
                    {
                        source.Enabled = false;
                    }
                }
                ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown current-scene bisect variant '{variant}'.");
        }

        RenderFrame();
    }

    internal (int totalLayers, int enabledLayers) GetSimulationLayerCountsForSmoke()
    {
        var leaves = EnumerateSimulationLeafLayers(_simulationLayers).ToArray();
        return (leaves.Length, leaves.Count(layer => layer.Enabled));
    }

    internal bool RunLegacySimulationGroupSourceMigrationSmoke()
    {
        _sources.Clear();

        var specs = new List<SimulationLayerSpec>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Kind = LayerEditorSimulationItemKind.Group,
                Name = "Outer",
                Enabled = true,
                Children = new List<SimulationLayerSpec>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Kind = LayerEditorSimulationItemKind.Group,
                        Name = "Inner Disabled",
                        Enabled = false,
                        Children = new List<SimulationLayerSpec>
                        {
                            new()
                            {
                                Id = Guid.NewGuid(),
                                Kind = LayerEditorSimulationItemKind.Layer,
                                Name = "Disabled Child",
                                Enabled = true,
                                InputFunction = SimulationInputFunction.Direct,
                                BlendMode = BlendMode.Additive,
                                InjectionMode = GameOfLifeEngine.InjectionMode.Threshold,
                                LifeMode = GameOfLifeEngine.LifeMode.NaiveGrayscale,
                                BinningMode = GameOfLifeEngine.BinningMode.Fill,
                                ThresholdMin = 0.22,
                                ThresholdMax = 0.66
                            }
                        }
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Kind = LayerEditorSimulationItemKind.Layer,
                        Name = "Enabled Child",
                        Enabled = true,
                        InputFunction = SimulationInputFunction.Inverse,
                        BlendMode = BlendMode.Subtractive,
                        InjectionMode = GameOfLifeEngine.InjectionMode.Threshold,
                        LifeMode = GameOfLifeEngine.LifeMode.NaiveGrayscale,
                        BinningMode = GameOfLifeEngine.BinningMode.Fill,
                        ThresholdMin = 0.31,
                        ThresholdMax = 0.74
                    }
                }
            }
        };

        if (!TryAddLegacySimulationGroupSource(specs))
        {
            return false;
        }

        var source = _sources.SingleOrDefault(candidate => candidate.Type == CaptureSource.SourceType.SimGroup);
        if (source == null || source.SimulationLayers.Count != 2)
        {
            return false;
        }

        bool flattened = source.SimulationLayers.All(layer => layer.Kind == LayerEditorSimulationItemKind.Layer);
        var disabledChild = source.SimulationLayers.FirstOrDefault(layer => layer.Name == "Disabled Child");
        var enabledChild = source.SimulationLayers.FirstOrDefault(layer => layer.Name == "Enabled Child");
        return flattened &&
               disabledChild is { Enabled: false } &&
               enabledChild is { Enabled: true } &&
               Math.Abs(enabledChild.ThresholdMin - 0.31) < 0.0001;
    }

    internal bool RunSimGroupRemovalClearsRuntimeSmoke()
    {
        _sources.Clear();
        ClearSimulationLayers();

        var source = CaptureSource.CreateSimulationGroup("Simulation");
        foreach (var spec in BuildDefaultSimulationLayerSpecs())
        {
            source.SimulationLayers.Add(spec);
        }

        _sources.Add(source);
        ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
        bool hadRuntimeLayers = EnumerateSimulationLeafLayers(_simulationLayers).Any();

        _sources.Clear();
        ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
        bool clearedRuntimeLayers = !EnumerateSimulationLeafLayers(_simulationLayers).Any();

        Logger.Info(
            $"Sim-group removal smoke: hadRuntimeLayers={hadRuntimeLayers}, clearedRuntimeLayers={clearedRuntimeLayers}.");
        return hadRuntimeLayers && clearedRuntimeLayers;
    }

    internal bool RunNoSimGroupRendersCompositeSmoke()
    {
        if (!_renderLoopAttached)
        {
            InitializeVisualizer();
        }

        _sources.Clear();
        ClearSimulationLayers();
        _passthroughEnabled = false;
        _passthroughCompositedInPixelBuffer = false;

        ApplyDimensions(144, 24, DefaultAspectRatio, persist: false);
        int width = GetReferenceSimulationEngine().Columns;
        int height = GetReferenceSimulationEngine().Rows;
        var source = CaptureSource.CreateFile("no-sim-group", "No Sim Group", width, height);
        source.LastFrame = new SourceFrame(BuildSmokeGradientBgra(width, height), width, height, null, width, height);
        source.BlendMode = BlendMode.Additive;
        source.Opacity = 1.0;
        _sources.Add(source);
        UpdatePrimaryAspectIfNeeded();

        _compositeDownscaledBuffer = null;
        _lastCompositeFrame = null;
        InjectCaptureFrames();
        int priorPresentationDrawCount = _renderBackend.PresentationDrawCount;
        RenderFrame();

        bool outputVisible = BufferHasNonBlackPixel(_pixelBuffer);
        bool gpuPresentedComposite = _renderBackend.PresentationDrawCount > priorPresentationDrawCount &&
                                     _lastCompositeFrame?.GpuSurface != null;
        bool compositedUnderlay = _passthroughCompositedInPixelBuffer && _lastCompositeFrame != null;
        bool runtimeLayersEmpty = !EnumerateSimulationLeafLayers(_simulationLayers).Any();
        Logger.Info(
            $"No-sim-group composite smoke: outputVisible={outputVisible}, gpuPresentedComposite={gpuPresentedComposite}, " +
            $"compositedUnderlay={compositedUnderlay}, runtimeLayersEmpty={runtimeLayersEmpty}.");
        return (outputVisible || gpuPresentedComposite || compositedUnderlay) && runtimeLayersEmpty;
    }

    internal bool RunDisabledSimGroupRendersCompositeSmoke()
    {
        if (!_renderLoopAttached)
        {
            InitializeVisualizer();
        }

        _sources.Clear();
        ClearSimulationLayers();
        _passthroughEnabled = false;
        _passthroughCompositedInPixelBuffer = false;

        ApplyDimensions(144, 24, DefaultAspectRatio, persist: false);
        int width = GetReferenceSimulationEngine().Columns;
        int height = GetReferenceSimulationEngine().Rows;

        var source = CaptureSource.CreateFile("disabled-sim-group", "Disabled Sim Group", width, height);
        source.LastFrame = new SourceFrame(BuildSmokeGradientBgra(width, height), width, height, null, width, height);
        source.BlendMode = BlendMode.Additive;
        source.Opacity = 1.0;

        var simulationGroup = CaptureSource.CreateSimulationGroup("Disabled Sim Group");
        foreach (var spec in BuildDefaultSimulationLayerSpecs())
        {
            simulationGroup.SimulationLayers.Add(spec);
        }

        _sources.Add(source);
        _sources.Add(simulationGroup);
        ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            layer.Enabled = false;
        }

        UpdatePrimaryAspectIfNeeded();
        _compositeDownscaledBuffer = null;
        _lastCompositeFrame = null;
        InjectCaptureFrames();
        RenderFrame();

        bool hasRuntimeLayers = EnumerateSimulationLeafLayers(_simulationLayers).Any();
        bool anyEnabledLayers = EnumerateSimulationLeafLayers(_simulationLayers).Any(layer => layer.Enabled);
        bool compositeVisible = BufferHasNonBlackPixel(_lastCompositeFrame?.Downscaled);
        bool outputMatchesComposite =
            _pixelBuffer != null &&
            _lastCompositeFrame != null &&
            _pixelBuffer.Length == _lastCompositeFrame.Downscaled.Length &&
            _pixelBuffer.AsSpan().SequenceEqual(_lastCompositeFrame.Downscaled);
        bool gpuPresentedComposite = _lastCompositeFrame?.GpuSurface != null && !_passthroughCompositedInPixelBuffer;

        Logger.Info(
            $"Disabled-sim-group composite smoke: hasRuntimeLayers={hasRuntimeLayers}, anyEnabledLayers={anyEnabledLayers}, " +
            $"compositeVisible={compositeVisible}, outputMatchesComposite={outputMatchesComposite}, gpuPresentedComposite={gpuPresentedComposite}.");
        return hasRuntimeLayers && !anyEnabledLayers && compositeVisible && (outputMatchesComposite || gpuPresentedComposite);
    }

    internal bool RunSimGroupStackOrderSmoke()
    {
        if (!_renderLoopAttached)
        {
            InitializeVisualizer();
        }

        _sources.Clear();
        ClearSimulationLayers();
        _passthroughEnabled = false;
        _passthroughCompositedInPixelBuffer = false;

        ApplyDimensions(144, 24, DefaultAspectRatio, persist: false);
        int width = GetReferenceSimulationEngine().Columns;
        int height = GetReferenceSimulationEngine().Rows;

        var graySource = CaptureSource.CreateFile("stack-order-gray", "Gray", width, height);
        graySource.LastFrame = new SourceFrame(BuildSmokeSolidBgra(width, height, 128, 128, 128), width, height, null, width, height);
        graySource.BlendMode = BlendMode.Additive;

        var whiteSource = CaptureSource.CreateFile("stack-order-white", "White", width, height);
        whiteSource.LastFrame = new SourceFrame(BuildSmokeSolidBgra(width, height, 255, 255, 255), width, height, null, width, height);
        whiteSource.BlendMode = BlendMode.Additive;

        var simulationGroup = CaptureSource.CreateSimulationGroup("Stack Order");
        simulationGroup.SimulationLayers.Add(new SimulationLayerSpec
        {
            Id = Guid.NewGuid(),
            Kind = LayerEditorSimulationItemKind.Layer,
            Name = "Positive",
            Enabled = true,
            InputFunction = SimulationInputFunction.Direct,
            BlendMode = BlendMode.Additive,
            InjectionMode = GameOfLifeEngine.InjectionMode.Threshold,
            LifeMode = GameOfLifeEngine.LifeMode.NaiveGrayscale,
            BinningMode = GameOfLifeEngine.BinningMode.Fill,
            InjectionNoise = 0.0,
            LifeOpacity = 1.0,
            ThresholdMin = 0.40,
            ThresholdMax = 0.60,
            InvertThreshold = false
        });

        byte[]? firstOutput = null;
        byte[]? secondOutput = null;
        bool firstGpuBacked = false;
        bool secondGpuBacked = false;

        void RenderCurrentOrder()
        {
            ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
            ConfigureSimulationLayerEngines(_configuredRows, _configuredDepth, _currentAspectRatio, randomize: false);
            foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
            {
                layer.TimeSinceLastStep = 1.0;
            }

            _pendingInlineSimulationStepsThisFrame = 1;
            InjectCaptureFrames(injectLayers: true);
            _pendingInlineSimulationStepsThisFrame = 0;
        }

        _sources.Add(graySource);
        _sources.Add(simulationGroup);
        _sources.Add(whiteSource);
        RenderCurrentOrder();
        firstGpuBacked = _lastCompositeFrame?.GpuSurface != null;
        if (simulationGroup.LastFrame?.Downscaled != null)
        {
            firstOutput = simulationGroup.LastFrame.Downscaled.ToArray();
        }

        _sources.Clear();
        _sources.Add(whiteSource);
        _sources.Add(simulationGroup);
        _sources.Add(graySource);
        RenderCurrentOrder();
        secondGpuBacked = _lastCompositeFrame?.GpuSurface != null;
        if (simulationGroup.LastFrame?.Downscaled != null)
        {
            secondOutput = simulationGroup.LastFrame.Downscaled.ToArray();
        }

        bool changed = firstOutput != null &&
                       secondOutput != null &&
                       !firstOutput.AsSpan().SequenceEqual(secondOutput);
        bool gpuBacked = firstGpuBacked && secondGpuBacked;
        Logger.Info($"Sim-group stack-order smoke: outputChanged={changed}, gpuBacked={gpuBacked}.");
        return changed && gpuBacked;
    }

    internal bool RunSimGroupInlineHueSmoke()
    {
        if (!_renderLoopAttached)
        {
            InitializeVisualizer();
        }

        _sources.Clear();
        ClearSimulationLayers();
        _passthroughEnabled = false;
        _passthroughCompositedInPixelBuffer = false;

        ApplyDimensions(144, 24, DefaultAspectRatio, persist: false);
        int width = GetReferenceSimulationEngine().Columns;
        int height = GetReferenceSimulationEngine().Rows;

        var source = CaptureSource.CreateFile("sim-group-inline-hue-source", "Inline Hue Source", width, height);
        source.LastFrame = new SourceFrame(BuildSmokeSolidBgra(width, height, 0, 0, 255), width, height, null, width, height);
        source.BlendMode = BlendMode.Additive;
        source.Opacity = 1.0;

        var simulationGroup = CaptureSource.CreateSimulationGroup("Inline Hue Group");

        SimulationLayerSpec BuildHueSpec(double hueShiftDegrees) => new()
        {
            Id = Guid.NewGuid(),
            Kind = LayerEditorSimulationItemKind.Layer,
            Name = "Hue Test",
            Enabled = true,
            InputFunction = SimulationInputFunction.Direct,
            BlendMode = BlendMode.Additive,
            InjectionMode = GameOfLifeEngine.InjectionMode.Threshold,
            LifeMode = GameOfLifeEngine.LifeMode.RgbChannels,
            BinningMode = GameOfLifeEngine.BinningMode.Fill,
            InjectionNoise = 0.0,
            LifeOpacity = 1.0,
            ThresholdMin = 0.1,
            ThresholdMax = 1.0,
            InvertThreshold = false,
            RgbHueShiftDegrees = hueShiftDegrees
        };
        simulationGroup.SimulationLayers.Add(BuildHueSpec(0.0));

        byte[]? zeroHueOutput = null;
        byte[]? shiftedHueOutput = null;
        bool zeroHueGpuBacked = false;
        bool shiftedHueGpuBacked = false;

        void RenderCurrentHue()
        {
            ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
            ConfigureSimulationLayerEngines(_configuredRows, _configuredDepth, _currentAspectRatio, randomize: false);
            foreach (var runtimeLayer in EnumerateSimulationLeafLayers(_simulationLayers))
            {
                runtimeLayer.TimeSinceLastStep = 1.0;
            }

            _pendingInlineSimulationStepsThisFrame = 1;
            InjectCaptureFrames(injectLayers: true);
            _pendingInlineSimulationStepsThisFrame = 0;
        }

        _sources.Add(source);
        _sources.Add(simulationGroup);

        RenderCurrentHue();
        zeroHueGpuBacked = _lastCompositeFrame?.GpuSurface != null;
        if (simulationGroup.LastFrame?.Downscaled != null)
        {
            zeroHueOutput = simulationGroup.LastFrame.Downscaled.ToArray();
        }

        simulationGroup.SimulationLayers[0] = BuildHueSpec(120.0);
        RenderCurrentHue();
        shiftedHueGpuBacked = _lastCompositeFrame?.GpuSurface != null;
        if (simulationGroup.LastFrame?.Downscaled != null)
        {
            shiftedHueOutput = simulationGroup.LastFrame.Downscaled.ToArray();
        }

        bool changed = zeroHueOutput != null &&
                       shiftedHueOutput != null &&
                       !zeroHueOutput.AsSpan().SequenceEqual(shiftedHueOutput);
        bool gpuBacked = zeroHueGpuBacked && shiftedHueGpuBacked;
        Logger.Info($"Sim-group inline-hue smoke: outputChanged={changed}, gpuBacked={gpuBacked}.");
        return changed && gpuBacked;
    }

    private void ResetInlinePresentationDiagnosticsForSmoke()
    {
        _inlineGpuPresentationFallbackCount = 0;
        _lastInlinePresentationFallbackLogUtc = DateTime.MinValue;
        GpuPresentationSurfaceSnapshotter.ResetSmokeCounters();
    }

    private int GetInlinePresentationFallbackCountForSmoke() => _inlineGpuPresentationFallbackCount;

    private (int snapshotCount, int distinctHandleCount) GetInlinePresentationSnapshotStatsForSmoke()
        => GpuPresentationSurfaceSnapshotter.GetSmokeStats();

    internal bool RunSimGroupInlinePresentationFreshnessSmoke()
    {
        if (_renderLoopAttached)
        {
            DetachRenderLoop();
        }

        Logger.Info("Sim-group inline-presentation smoke: starting dynamic phase.");
        bool dynamicOk = RunSimGroupInlinePresentationDynamicPhase();
        Logger.Info($"Sim-group inline-presentation smoke: dynamic phase complete ({dynamicOk}). Starting static phase.");
        bool staticOk = RunSimGroupInlinePresentationStaticPhase();
        Logger.Info($"Sim-group inline-presentation smoke: static phase complete ({staticOk}). Starting redraw-pressure phase.");
        bool redrawOk = RunSimGroupInlinePresentationRedrawPressurePhase();
        Logger.Info($"Sim-group inline-presentation smoke: redraw-pressure phase complete ({redrawOk}). Starting file-backed hover phase.");
        bool fileBackedOk = RunSimGroupInlinePresentationFileBackedHoverPhase();
        Logger.Info($"Sim-group inline-presentation smoke: file-backed hover phase complete ({fileBackedOk}). Starting exact repro hover phase.");
        bool exactReproOk = RunSimGroupInlinePresentationExactReproHoverPhase();
        Logger.Info($"Sim-group inline-presentation smoke: exact repro hover phase complete ({exactReproOk}). Starting real cursor hover phase.");
        bool realCursorOk = RunSimGroupInlinePresentationRealCursorHoverPhase();
        Logger.Info($"Sim-group inline-presentation combined smoke: dynamicOk={dynamicOk}, staticOk={staticOk}, redrawOk={redrawOk}, fileBackedOk={fileBackedOk}, exactReproOk={exactReproOk}, realCursorOk={realCursorOk}.");
        return dynamicOk && staticOk && redrawOk && fileBackedOk && exactReproOk && realCursorOk;
    }

    private bool RunSimGroupInlinePresentationDynamicPhase()
    {
        if (_renderBackend is NullRenderBackend)
        {
            InitializeVisualizer();
        }

        SetupInlinePixelSortPresentationSceneForSmoke(
            out int width,
            out int height,
            out var background,
            out var simulationGroup,
            out var overlay,
            overlayOpacity: 0.88);

        byte[][] backgroundFrames =
        {
            BuildSmokeRainbowBgra(width, height, 0.00),
            BuildSmokeRainbowBgra(width, height, 0.18),
            BuildSmokeRainbowBgra(width, height, 0.37),
            BuildSmokeRainbowBgra(width, height, 0.56)
        };
        byte[][] overlayFrames =
        {
            BuildSmokePortraitBgra(width, height, 0),
            BuildSmokePortraitBgra(width, height, 1),
            BuildSmokePortraitBgra(width, height, 2),
            BuildSmokePortraitBgra(width, height, 3)
        };

        ResetInlinePresentationDiagnosticsForSmoke();
        int baselineFallbacks = GetInlinePresentationFallbackCountForSmoke();
        int drawAdvances = 0;
        int compositeChanges = 0;
        int presentedChanges = 0;
        int compositePresentedMismatches = 0;
        byte[]? previousComposite = null;
        byte[]? previousPresented = null;
        int iterations = 144;

        for (int i = 0; i < iterations; i++)
        {
            if (i > 0 && i % 24 == 0)
            {
                Logger.Info($"Sim-group inline-presentation dynamic smoke progress: iteration={i}/{iterations}.");
            }

            background.LastFrame = new SourceFrame(backgroundFrames[i % backgroundFrames.Length], width, height, null, width, height);
            overlay.LastFrame = new SourceFrame(overlayFrames[(i / 2) % overlayFrames.Length], width, height, null, width, height);
            PrimeInlineSmokeLayerStepping(simulationGroup);

            int priorDrawCount = _renderBackend.PresentationDrawCount;
            _pendingInlineSimulationStepsThisFrame = 1;
            InjectCaptureFrames(injectLayers: true);
            _pendingInlineSimulationStepsThisFrame = 0;
            RenderFrame();

            if (WaitForPresentationDrawForSmoke(priorDrawCount))
            {
                drawAdvances++;
            }

            var composite = _lastCompositeFrame?.Downscaled;
            var presented = _renderBackend.GetPresentedFrameCopyForSmoke();
            if (composite != null &&
                presented != null &&
                !BuffersEqual(composite, presented))
            {
                compositePresentedMismatches++;
            }

            if (!BuffersEqual(previousComposite, composite))
            {
                compositeChanges++;
            }

            if (!BuffersEqual(previousPresented, presented))
            {
                presentedChanges++;
            }

            previousComposite = composite?.ToArray();
            previousPresented = presented;
        }

        int fallbackCount = GetInlinePresentationFallbackCountForSmoke() - baselineFallbacks;
        int minDrawAdvances = (int)Math.Ceiling(iterations * 0.50);
        int minChanges = (int)Math.Ceiling(iterations * 0.50);
        bool ok = drawAdvances >= minDrawAdvances &&
                  compositeChanges >= minChanges &&
                  presentedChanges >= minChanges &&
                  fallbackCount == 0;
        Logger.Info(
            $"Sim-group inline-presentation dynamic smoke: drawAdvances={drawAdvances}/{iterations}, " +
            $"compositeChanges={compositeChanges}, presentedChanges={presentedChanges}, " +
            $"mismatches={compositePresentedMismatches}, fallbacks={fallbackCount}, " +
            $"minDrawAdvances={minDrawAdvances}, minChanges={minChanges}.");
        return ok;
    }

    private bool RunSimGroupInlinePresentationStaticPhase()
    {
        if (_renderBackend is NullRenderBackend)
        {
            InitializeVisualizer();
        }

        SetupInlinePixelSortPresentationSceneForSmoke(
            out int width,
            out int height,
            out var background,
            out var simulationGroup,
            out var overlay,
            overlayOpacity: 0.88);

        byte[] backgroundFrame = BuildSmokeRainbowBgra(width, height, 0.12);
        byte[] overlayFrame = BuildSmokePortraitBgra(width, height, 0);

        ResetInlinePresentationDiagnosticsForSmoke();
        int baselineFallbacks = GetInlinePresentationFallbackCountForSmoke();
        int drawAdvances = 0;
        int compositeChanges = 0;
        int presentedChanges = 0;
        int compositePresentedMismatches = 0;
        byte[]? baselineComposite = null;
        byte[]? baselinePresented = null;
        var compositeSignatures = new HashSet<ulong>();
        var presentedSignatures = new HashSet<ulong>();
        int iterations = 240;

        for (int i = 0; i < iterations; i++)
        {
            if (i > 0 && i % 40 == 0)
            {
                Logger.Info($"Sim-group inline-presentation static smoke progress: iteration={i}/{iterations}.");
            }

            background.LastFrame = new SourceFrame(backgroundFrame, width, height, null, width, height);
            overlay.LastFrame = new SourceFrame(overlayFrame, width, height, null, width, height);
            PrimeInlineSmokeLayerStepping(simulationGroup);

            int priorDrawCount = _renderBackend.PresentationDrawCount;
            _pendingInlineSimulationStepsThisFrame = 1;
            InjectCaptureFrames(injectLayers: true);
            _pendingInlineSimulationStepsThisFrame = 0;
            RenderFrame();

            if (WaitForPresentationDrawForSmoke(priorDrawCount))
            {
                drawAdvances++;
            }

            var composite = _lastCompositeFrame?.Downscaled;
            var presented = _renderBackend.GetPresentedFrameCopyForSmoke();
            ulong? compositeSignature = ComputeSmokeBufferSignature(composite);
            ulong? presentedSignature = ComputeSmokeBufferSignature(presented);
            if (compositeSignature.HasValue)
            {
                compositeSignatures.Add(compositeSignature.Value);
            }

            if (presentedSignature.HasValue)
            {
                presentedSignatures.Add(presentedSignature.Value);
            }

            if (composite != null &&
                presented != null &&
                !BuffersEqual(composite, presented))
            {
                compositePresentedMismatches++;
            }

            if (i == 0)
            {
                baselineComposite = composite?.ToArray();
                baselinePresented = presented;
                continue;
            }

            if (!BuffersEqual(baselineComposite, composite))
            {
                compositeChanges++;
            }

            if (!BuffersEqual(baselinePresented, presented))
            {
                presentedChanges++;
            }
        }

        int fallbackCount = GetInlinePresentationFallbackCountForSmoke() - baselineFallbacks;
        int minDrawAdvances = (int)Math.Ceiling(iterations * 0.75);
        bool ok = drawAdvances >= minDrawAdvances &&
                  compositeChanges == 0 &&
                  presentedChanges == 0 &&
                  compositeSignatures.Count == 1 &&
                  presentedSignatures.Count == 1 &&
                  fallbackCount == 0;
        Logger.Info(
            $"Sim-group inline-presentation static smoke: drawAdvances={drawAdvances}/{iterations}, " +
            $"compositeChanges={compositeChanges}, presentedChanges={presentedChanges}, " +
            $"mismatches={compositePresentedMismatches}, compositeSignatures={compositeSignatures.Count}, " +
            $"presentedSignatures={presentedSignatures.Count}, fallbacks={fallbackCount}.");
        return ok;
    }

    private bool RunSimGroupInlinePresentationRedrawPressurePhase()
    {
        SetupInlinePixelSortPresentationSceneForSmoke(
            out int width,
            out int height,
            out var background,
            out _,
            out var overlay,
            overlayOpacity: 0.88);

        byte[] backgroundFrame = BuildSmokeRainbowBgra(width, height, 0.12);
        byte[] overlayFrame = BuildSmokePortraitBgra(width, height, 0);
        background.LastFrame = new SourceFrame(backgroundFrame, width, height, null, width, height);
        overlay.LastFrame = new SourceFrame(overlayFrame, width, height, null, width, height);

        ResetInlinePresentationDiagnosticsForSmoke();
        int warmupDraws = 12;
        for (int i = 0; i < warmupDraws; i++)
        {
            int priorWarmupDrawCount = _renderBackend.PresentationDrawCount;
            _renderBackend.RequestPresentationRedrawForSmoke();
            RenderFrame();
            WaitForPresentationDrawForSmoke(priorWarmupDrawCount, maxIdlePasses: 16);
        }

        int baselineFallbacks = GetInlinePresentationFallbackCountForSmoke();
        int drawAdvances = 0;
        int presentedChanges = 0;
        var presentedSignatures = new HashSet<ulong>();
        byte[]? baselinePresented = null;
        int iterations = 180;

        for (int i = 0; i < iterations; i++)
        {
            if (i > 0 && i % 30 == 0)
            {
                Logger.Info($"Sim-group inline-presentation redraw-pressure smoke progress: iteration={i}/{iterations}.");
            }

            ApplyChromeHoverStressForSmoke(i);
            int priorDrawCount = _renderBackend.PresentationDrawCount;
            _renderBackend.RequestPresentationRedrawForSmoke();
            RenderFrame();
            if (WaitForPresentationDrawForSmoke(priorDrawCount, maxIdlePasses: 16))
            {
                drawAdvances++;
            }

            var presented = _renderBackend.GetPresentedFrameCopyForSmoke();
            ulong? signature = ComputeSmokeBufferSignature(presented);
            if (signature.HasValue)
            {
                presentedSignatures.Add(signature.Value);
            }

            if (i == 0)
            {
                baselinePresented = presented;
                continue;
            }

            if (!BuffersEqual(baselinePresented, presented))
            {
                presentedChanges++;
            }
        }

        int fallbackCount = GetInlinePresentationFallbackCountForSmoke() - baselineFallbacks;
        int minDrawAdvances = (int)Math.Ceiling(iterations * 0.80);
        bool ok = drawAdvances >= minDrawAdvances &&
                  presentedChanges == 0 &&
                  presentedSignatures.Count == 1 &&
                  fallbackCount == 0;
        Logger.Info(
            $"Sim-group inline-presentation redraw-pressure smoke: drawAdvances={drawAdvances}/{iterations}, " +
            $"presentedChanges={presentedChanges}, presentedSignatures={presentedSignatures.Count}, fallbacks={fallbackCount}.");
        return ok;
    }

    private bool RunSimGroupInlinePresentationFileBackedHoverPhase()
    {
        SetupInlinePixelSortPresentationSceneForSmoke(
            out int width,
            out int height,
            out var background,
            out _,
            out var overlay,
            overlayOpacity: 0.88,
            rows: 480,
            useFileBackedSources: true);

        DateTime deadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while ((background.LastFrame == null || overlay.LastFrame == null) && DateTime.UtcNow < deadlineUtc)
        {
            int priorDrawCount = _renderBackend.PresentationDrawCount;
            _pendingInlineSimulationStepsThisFrame = 1;
            InjectCaptureFrames(injectLayers: true);
            _pendingInlineSimulationStepsThisFrame = 0;
            RenderFrame();
            WaitForPresentationDrawForSmoke(priorDrawCount, maxIdlePasses: 24);
        }

        if (background.LastFrame == null || overlay.LastFrame == null)
        {
            Logger.Warn("Sim-group inline-presentation file-backed hover smoke failed to load file-backed source frames.");
            return false;
        }

        var baseline = CollectInlinePresentationMetricsForSmoke(frames: 90, applyHoverStress: false, refreshSources: true);
        var hover = CollectInlinePresentationMetricsForSmoke(frames: 90, applyHoverStress: true, refreshSources: true);
        double baselineP95 = ComputePercentile(baseline.Deltas, 0.95);
        double hoverP95 = ComputePercentile(hover.Deltas, 0.95);
        double baselineAverage = baseline.Deltas.Count > 0 ? baseline.Deltas.Average() : 0.0;
        double hoverAverage = hover.Deltas.Count > 0 ? hover.Deltas.Average() : 0.0;

        bool ok = baseline.EmptyFrames == 0 &&
                  hover.EmptyFrames == 0 &&
                  baseline.DrawAdvances >= 60 &&
                  hover.DrawAdvances >= 75 &&
                  hoverP95 <= Math.Max(baselineP95 * 1.15, baselineP95 + 2.0) &&
                  hoverAverage <= Math.Max(baselineAverage * 1.15, baselineAverage + 1.0);

        Logger.Info(
            $"Sim-group inline-presentation file-backed hover smoke: baselineDraws={baseline.DrawAdvances}/90, " +
            $"hoverDraws={hover.DrawAdvances}/90, baselineAvg={baselineAverage:F2}, hoverAvg={hoverAverage:F2}, " +
            $"baselineP95={baselineP95:F2}, hoverP95={hoverP95:F2}, baselineEmpty={baseline.EmptyFrames}, hoverEmpty={hover.EmptyFrames}.");
        return ok;
    }

    private bool RunSimGroupInlinePresentationExactReproHoverPhase()
    {
        if (!TryGetInlinePixelSortExactReproPathsForSmoke(out string backgroundPath, out string overlayPath))
        {
            Logger.Warn("Sim-group inline-presentation exact repro hover smoke skipped because exact repro assets were unavailable.");
            return false;
        }

        SetupInlinePixelSortPresentationSceneForSmoke(
            out int width,
            out int height,
            out var background,
            out _,
            out var overlay,
            overlayOpacity: 1.0,
            rows: 480,
            useFileBackedSources: true,
            backgroundFilePathOverride: backgroundPath,
            overlayFilePathOverride: overlayPath,
            backgroundBlendModeOverride: BlendMode.Additive,
            overlayFitModeOverride: FitMode.Fit);

        DateTime deadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while ((background.LastFrame == null || overlay.LastFrame == null) && DateTime.UtcNow < deadlineUtc)
        {
            int priorDrawCount = _renderBackend.PresentationDrawCount;
            _pendingInlineSimulationStepsThisFrame = 1;
            InjectCaptureFrames(injectLayers: true);
            _pendingInlineSimulationStepsThisFrame = 0;
            RenderFrame();
            WaitForPresentationDrawForSmoke(priorDrawCount, maxIdlePasses: 24);
        }

        if (background.LastFrame == null || overlay.LastFrame == null)
        {
            Logger.Warn("Sim-group inline-presentation exact repro hover smoke failed to load exact repro source frames.");
            return false;
        }

        int baselineFrames = 90;
        int hoverFrames = 180;
        int baselineDrawAdvances = 0;
        int hoverDrawAdvances = 0;
        var baselinePresentedSignatures = new HashSet<ulong>();
        var hoverPresentedSignatures = new HashSet<ulong>();
        var hoverCompositeSignatures = new HashSet<ulong>();
        byte[]? baselinePresented = null;
        byte[]? baselineComposite = null;
        int hoverPresentedChanges = 0;
        int hoverCompositeChanges = 0;

        for (int i = 0; i < baselineFrames; i++)
        {
            int priorDrawCount = _renderBackend.PresentationDrawCount;
            _pendingInlineSimulationStepsThisFrame = 1;
            InjectCaptureFrames(injectLayers: true);
            _pendingInlineSimulationStepsThisFrame = 0;
            RenderFrame();
            if (WaitForPresentationDrawForSmoke(priorDrawCount, maxIdlePasses: 24))
            {
                baselineDrawAdvances++;
            }

            byte[]? presented = _renderBackend.GetPresentedFrameCopyForSmoke();
            ulong? presentedSignature = ComputeSmokeBufferSignature(presented);
            if (presentedSignature.HasValue)
            {
                baselinePresentedSignatures.Add(presentedSignature.Value);
            }

            if (i == 0)
            {
                baselinePresented = presented;
                baselineComposite = _lastCompositeFrame?.Downscaled?.ToArray();
            }
        }

        for (int i = 0; i < hoverFrames; i++)
        {
            ApplyChromeHoverStressForSmoke(i);
            int priorDrawCount = _renderBackend.PresentationDrawCount;
            _renderBackend.RequestPresentationRedrawForSmoke();
            _pendingInlineSimulationStepsThisFrame = 1;
            InjectCaptureFrames(injectLayers: true);
            _pendingInlineSimulationStepsThisFrame = 0;
            RenderFrame();
            if (WaitForPresentationDrawForSmoke(priorDrawCount, maxIdlePasses: 24))
            {
                hoverDrawAdvances++;
            }

            byte[]? presented = _renderBackend.GetPresentedFrameCopyForSmoke();
            byte[]? composite = _lastCompositeFrame?.Downscaled;
            ulong? presentedSignature = ComputeSmokeBufferSignature(presented);
            ulong? compositeSignature = ComputeSmokeBufferSignature(composite);
            if (presentedSignature.HasValue)
            {
                hoverPresentedSignatures.Add(presentedSignature.Value);
            }

            if (compositeSignature.HasValue)
            {
                hoverCompositeSignatures.Add(compositeSignature.Value);
            }

            if (!BuffersEqual(baselinePresented, presented))
            {
                hoverPresentedChanges++;
            }

            if (!BuffersEqual(baselineComposite, composite))
            {
                hoverCompositeChanges++;
            }
        }

        var snapshotStats = GetInlinePresentationSnapshotStatsForSmoke();
        bool ok = baselineDrawAdvances >= 60 &&
                  hoverDrawAdvances >= 150 &&
                  baselinePresentedSignatures.Count == 1 &&
                  hoverPresentedSignatures.Count == 1 &&
                  hoverCompositeSignatures.Count == 1 &&
                  hoverPresentedChanges == 0 &&
                  hoverCompositeChanges == 0 &&
                  snapshotStats.snapshotCount > 0 &&
                  snapshotStats.distinctHandleCount >= 2;

        Logger.Info(
            $"Sim-group inline-presentation exact repro hover smoke: baselineDraws={baselineDrawAdvances}/{baselineFrames}, " +
            $"hoverDraws={hoverDrawAdvances}/{hoverFrames}, baselinePresentedSignatures={baselinePresentedSignatures.Count}, " +
            $"hoverPresentedSignatures={hoverPresentedSignatures.Count}, hoverCompositeSignatures={hoverCompositeSignatures.Count}, " +
            $"hoverPresentedChanges={hoverPresentedChanges}, hoverCompositeChanges={hoverCompositeChanges}, " +
            $"snapshotCount={snapshotStats.snapshotCount}, distinctSnapshotHandles={snapshotStats.distinctHandleCount}.");
        return ok;
    }

    private bool RunSimGroupInlinePresentationRealCursorHoverPhase()
    {
        if (!TryGetInlinePixelSortExactReproPathsForSmoke(out string backgroundPath, out string overlayPath))
        {
            Logger.Warn("Sim-group inline-presentation real cursor hover smoke skipped because exact repro assets were unavailable.");
            return false;
        }

        SetupInlinePixelSortPresentationSceneForSmoke(
            out _,
            out _,
            out var background,
            out _,
            out var overlay,
            overlayOpacity: 1.0,
            rows: 480,
            useFileBackedSources: true,
            backgroundFilePathOverride: backgroundPath,
            overlayFilePathOverride: overlayPath,
            backgroundBlendModeOverride: BlendMode.Additive,
            overlayFitModeOverride: FitMode.Fit);

        DateTime deadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while ((background.LastFrame == null || overlay.LastFrame == null) && DateTime.UtcNow < deadlineUtc)
        {
            int priorDrawCount = _renderBackend.PresentationDrawCount;
            _pendingInlineSimulationStepsThisFrame = 1;
            InjectCaptureFrames(injectLayers: true);
            _pendingInlineSimulationStepsThisFrame = 0;
            RenderFrame();
            WaitForPresentationDrawForSmoke(priorDrawCount, maxIdlePasses: 24);
        }

        if (background.LastFrame == null || overlay.LastFrame == null)
        {
            Logger.Warn("Sim-group inline-presentation real cursor hover smoke failed to load exact repro source frames.");
            return false;
        }

        return RunRealCursorHoverStabilityCheckForSmoke(
            "Sim-group inline-presentation real cursor hover smoke",
            baselineFrames: 45,
            hoverSteps: 6,
            hoverFramesPerStep: 18,
            postHoverFrames: 45,
            refreshSources: true);
    }

    private (int DrawAdvances, int EmptyFrames, List<double> Deltas) CollectInlinePresentationMetricsForSmoke(int frames, bool applyHoverStress, bool refreshSources)
    {
        int drawAdvances = 0;
        int emptyFrames = 0;
        var deltas = new List<double>(Math.Max(0, frames - 1));
        byte[]? previousPresented = null;

        for (int i = 0; i < frames; i++)
        {
            if (applyHoverStress)
            {
                ApplyChromeHoverStressForSmoke(i);
                _renderBackend.RequestPresentationRedrawForSmoke();
            }

            int priorDrawCount = _renderBackend.PresentationDrawCount;
            if (refreshSources)
            {
                _pendingInlineSimulationStepsThisFrame = 1;
                InjectCaptureFrames(injectLayers: true);
                _pendingInlineSimulationStepsThisFrame = 0;
            }

            RenderFrame();
            if (WaitForPresentationDrawForSmoke(priorDrawCount, maxIdlePasses: 24))
            {
                drawAdvances++;
            }

            byte[]? presented = _renderBackend.GetPresentedFrameCopyForSmoke();
            if (presented == null || presented.Length == 0)
            {
                emptyFrames++;
                continue;
            }

            if (previousPresented != null)
            {
                deltas.Add(ComputeSmokeAverageDelta(previousPresented, presented));
            }

            previousPresented = presented;
        }

        return (drawAdvances, emptyFrames, deltas);
    }

    private void SetupInlinePixelSortPresentationSceneForSmoke(
        out int width,
        out int height,
        out CaptureSource background,
        out CaptureSource simulationGroup,
        out CaptureSource overlay,
        double overlayOpacity,
        int rows = 240,
        bool useFileBackedSources = false,
        string? backgroundFilePathOverride = null,
        string? overlayFilePathOverride = null,
        BlendMode? backgroundBlendModeOverride = null,
        FitMode? overlayFitModeOverride = null)
    {
        _sources.Clear();
        ClearSimulationLayers();
        _passthroughEnabled = false;
        _passthroughCompositedInPixelBuffer = false;
        _invertComposite = false;
        _isPaused = false;

        ApplyDimensions(rows, 24, DefaultAspectRatio, persist: false);
        width = GetReferenceSimulationEngine().Columns;
        height = GetReferenceSimulationEngine().Rows;

        string? backgroundFilePath = null;
        string? overlayFilePath = null;
        if (useFileBackedSources)
        {
            backgroundFilePath = backgroundFilePathOverride ?? CreateSmokeBitmapFile("inline-pixel-sort-background", BuildSmokeRainbowBgra(width, height, 0.0), width, height);
            overlayFilePath = overlayFilePathOverride ?? CreateSmokeBitmapFile("inline-pixel-sort-overlay", BuildSmokePortraitBgra(width, height, 0), width, height);
        }

        background = CaptureSource.CreateFile(backgroundFilePath ?? "inline-pixel-sort-background", "Inline Pixel Sort Background", width, height);
        background.BlendMode = backgroundBlendModeOverride ?? BlendMode.Normal;
        background.Opacity = 1.0;
        background.UsePinnedFrameForSmoke = !useFileBackedSources;
        background.LastFrame = useFileBackedSources
            ? null
            : new SourceFrame(BuildSmokeRainbowBgra(width, height, 0.0), width, height, null, width, height);

        simulationGroup = CaptureSource.CreateSimulationGroup("Inline Pixel Sort Group");
        simulationGroup.SimulationLayers.Add(new SimulationLayerSpec
        {
            Id = Guid.NewGuid(),
            Kind = LayerEditorSimulationItemKind.Layer,
            LayerType = SimulationLayerType.PixelSort,
            Name = "Pixel Sort",
            Enabled = true,
            InputFunction = SimulationInputFunction.Direct,
            BlendMode = BlendMode.Normal,
            InjectionMode = GameOfLifeEngine.InjectionMode.Threshold,
            LifeMode = GameOfLifeEngine.LifeMode.NaiveGrayscale,
            BinningMode = GameOfLifeEngine.BinningMode.Fill,
            InjectionNoise = 0.0,
            LifeOpacity = 1.0,
            ThresholdMin = 0.35,
            ThresholdMax = 0.75,
            InvertThreshold = false,
            PixelSortCellWidth = 12,
            PixelSortCellHeight = 8
        });

        overlay = CaptureSource.CreateFile(overlayFilePath ?? "inline-pixel-sort-overlay", "Inline Pixel Sort Overlay", width, height);
        overlay.BlendMode = BlendMode.Normal;
        overlay.FitMode = overlayFitModeOverride ?? FitMode.Fill;
        overlay.Opacity = Math.Clamp(overlayOpacity, 0.0, 1.0);
        overlay.UsePinnedFrameForSmoke = !useFileBackedSources;
        overlay.LastFrame = useFileBackedSources
            ? null
            : new SourceFrame(BuildSmokePortraitBgra(width, height, 0), width, height, null, width, height);

        _sources.Add(background);
        _sources.Add(simulationGroup);
        _sources.Add(overlay);
        UpdatePrimaryAspectIfNeeded();
        ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
        ConfigureSimulationLayerEngines(_configuredRows, _configuredDepth, _currentAspectRatio, randomize: false);
    }

    private static bool TryGetInlinePixelSortExactReproPathsForSmoke(out string backgroundPath, out string overlayPath)
    {
        string[] roots =
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string candidateBackground = Path.Combine(root, "Assets", "SmokeRepro", "rainbow-background-1438899140wt8.jpg");
            string candidateOverlay = Path.Combine(root, "Assets", "SmokeRepro", "Mona_Lisa_repro.png");
            if (File.Exists(candidateBackground) && File.Exists(candidateOverlay))
            {
                backgroundPath = candidateBackground;
                overlayPath = candidateOverlay;
                return true;
            }
        }

        backgroundPath = string.Empty;
        overlayPath = string.Empty;
        return false;
    }

    private void ApplyChromeHoverStressForSmoke(int iteration)
    {
        byte accent = (byte)(iteration % 2 == 0 ? 0x22 : 0x55);
        var brush = new SolidColorBrush(Color.FromArgb(0xFF, accent, accent, accent));
        var border = new SolidColorBrush(Color.FromArgb(0xFF, (byte)(accent + 0x11), (byte)(accent + 0x11), (byte)(accent + 0x11)));

        if (ChromeBar != null)
        {
            ChromeBar.BorderBrush = border;
            ChromeBar.InvalidateVisual();
        }

        if (ChromeMenuButton != null)
        {
            ChromeMenuButton.Background = brush;
            ChromeMenuButton.BorderBrush = border;
            ChromeMenuButton.InvalidateVisual();
        }

        if (ChromeSceneEditorButton != null)
        {
            ChromeSceneEditorButton.Background = brush;
            ChromeSceneEditorButton.BorderBrush = border;
            ChromeSceneEditorButton.InvalidateVisual();
        }

        if (ChromeMinimizeButton != null)
        {
            ChromeMinimizeButton.Background = brush;
            ChromeMinimizeButton.BorderBrush = border;
            ChromeMinimizeButton.InvalidateVisual();
        }

        if (ChromeFullscreenButton != null)
        {
            ChromeFullscreenButton.Background = brush;
            ChromeFullscreenButton.BorderBrush = border;
            ChromeFullscreenButton.InvalidateVisual();
        }

        if (ChromeCloseButton != null)
        {
            ChromeCloseButton.Background = brush;
            ChromeCloseButton.BorderBrush = border;
            ChromeCloseButton.InvalidateVisual();
        }
    }

    private void PrimeInlineSmokeLayerStepping(CaptureSource simulationGroup)
    {
        var runtimeGroup = FindSimulationNode(simulationGroup.Id);
        if (runtimeGroup == null)
        {
            return;
        }

        foreach (var layer in EnumerateSimulationLeafLayers(runtimeGroup.Children))
        {
            layer.TimeSinceLastStep = 1.0;
        }
    }

    private static bool BuffersEqual(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    private static ulong? ComputeSmokeBufferSignature(byte[]? buffer)
    {
        if (buffer == null)
        {
            return null;
        }

        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        for (int i = 0; i < buffer.Length; i++)
        {
            hash ^= buffer[i];
            hash *= prime;
        }

        return hash;
    }

    internal bool RunCurrentSceneHoverPresentationSmoke()
    {
        if (!_renderLoopAttached)
        {
            InitializeVisualizer();
        }

        if (_sources.Count == 0)
        {
            Logger.Warn("Current-scene hover presentation smoke has no sources to evaluate.");
            return false;
        }

        const int baselineFrames = 120;
        const int hoverFrames = 120;

        for (int i = 0; i < 24; i++)
        {
            int priorWarmupDrawCount = _renderBackend.PresentationDrawCount;
            if (!WaitForPresentationDrawForSmoke(priorWarmupDrawCount, maxIdlePasses: 24))
            {
                break;
            }
        }

        ResetInlinePresentationDiagnosticsForSmoke();
        var baseline = CollectCurrentSceneHoverPresentationMetrics(frames: baselineFrames, applyHoverStress: false);
        var hover = CollectCurrentSceneHoverPresentationMetrics(frames: hoverFrames, applyHoverStress: true);

        double baselineP95 = ComputePercentile(baseline.Deltas, 0.95);
        double hoverP95 = ComputePercentile(hover.Deltas, 0.95);
        double baselineAverage = baseline.Deltas.Count > 0 ? baseline.Deltas.Average() : 0.0;
        double hoverAverage = hover.Deltas.Count > 0 ? hover.Deltas.Average() : 0.0;
        double baselineSpikeThreshold = Math.Max(8.0, baselineP95 * 1.35);
        int baselineSpikes = baseline.Deltas.Count(delta => delta >= baselineSpikeThreshold);
        int hoverSpikes = hover.Deltas.Count(delta => delta >= baselineSpikeThreshold);
        double baselineSpikeRatio = baseline.Deltas.Count > 0 ? baselineSpikes / (double)baseline.Deltas.Count : 0.0;
        double hoverSpikeRatio = hover.Deltas.Count > 0 ? hoverSpikes / (double)hover.Deltas.Count : 0.0;

        bool ok = hover.DrawAdvances >= (int)Math.Ceiling(hoverFrames * 0.85) &&
                  hover.EmptyFrames == 0 &&
                  hoverP95 <= Math.Max(baselineP95 * 1.75, baselineP95 + 6.0) &&
                  hoverAverage <= Math.Max(baselineAverage * 1.50, baselineAverage + 4.0) &&
                  hoverSpikeRatio <= baselineSpikeRatio + 0.12;

        var snapshotStats = GetInlinePresentationSnapshotStatsForSmoke();
        bool requiresInlineSnapshots = HasEmbeddedSimulationGroups(_sources);
        if (requiresInlineSnapshots)
        {
            ok = ok &&
                 snapshotStats.snapshotCount > 0 &&
                 snapshotStats.distinctHandleCount >= 2;
        }

        Logger.Info(
            $"Current-scene hover presentation smoke: baselineDraws={baseline.DrawAdvances}/{baselineFrames}, " +
            $"hoverDraws={hover.DrawAdvances}/{hoverFrames}, baselineAvg={baselineAverage:F2}, hoverAvg={hoverAverage:F2}, " +
            $"baselineP95={baselineP95:F2}, hoverP95={hoverP95:F2}, baselineSpikeRatio={baselineSpikeRatio:F3}, " +
            $"hoverSpikeRatio={hoverSpikeRatio:F3}, hoverEmptyFrames={hover.EmptyFrames}, " +
            $"snapshotCount={snapshotStats.snapshotCount}, distinctSnapshotHandles={snapshotStats.distinctHandleCount}, " +
            $"requiresInlineSnapshots={requiresInlineSnapshots}.");

        bool realCursorOk = RunRealCursorHoverStabilityCheckForSmoke(
            "Current-scene real cursor hover smoke",
            baselineFrames: 45,
            hoverSteps: 6,
            hoverFramesPerStep: 18,
            postHoverFrames: 45,
            refreshSources: true);

        return ok && realCursorOk;
    }

    private (int DrawAdvances, int EmptyFrames, List<double> Deltas) CollectCurrentSceneHoverPresentationMetrics(int frames, bool applyHoverStress)
    {
        int drawAdvances = 0;
        int emptyFrames = 0;
        var deltas = new List<double>(Math.Max(0, frames - 1));
        byte[]? previousPresented = null;

        for (int i = 0; i < frames; i++)
        {
            if (applyHoverStress)
            {
                ApplyChromeHoverStressForSmoke(i);
                _renderBackend.RequestPresentationRedrawForSmoke();
            }

            int priorDrawCount = _renderBackend.PresentationDrawCount;
            if (WaitForPresentationDrawForSmoke(priorDrawCount, maxIdlePasses: 24))
            {
                drawAdvances++;
            }

            byte[]? presented = _renderBackend.GetPresentedFrameCopyForSmoke();
            if (presented == null || presented.Length == 0)
            {
                emptyFrames++;
                continue;
            }

            if (previousPresented != null)
            {
                deltas.Add(ComputeSmokeAverageDelta(previousPresented, presented));
            }

            previousPresented = presented;
        }

        return (drawAdvances, emptyFrames, deltas);
    }

    private bool RunRealCursorHoverStabilityCheckForSmoke(
        string label,
        int baselineFrames,
        int hoverSteps,
        int hoverFramesPerStep,
        int postHoverFrames,
        bool refreshSources)
    {
        if (!GetCursorPos(out var originalCursor))
        {
            Logger.Warn($"{label}: failed to read original cursor position.");
            return false;
        }

        try
        {
            var baseline = CollectCursorHoverPresentationMetricsForSmoke(
                frames: baselineFrames,
                moveCursor: false,
                hoverSteps: hoverSteps,
                refreshSources: refreshSources);
            var hover = CollectCursorHoverPresentationMetricsForSmoke(
                frames: hoverSteps * hoverFramesPerStep,
                moveCursor: true,
                hoverSteps: hoverSteps,
                refreshSources: refreshSources);
            MoveCursorOutsideWindowForSmoke();
            var postHover = CollectCursorHoverPresentationMetricsForSmoke(
                frames: postHoverFrames,
                moveCursor: false,
                hoverSteps: hoverSteps,
                refreshSources: refreshSources);

            bool ok = baseline.EmptyFrames == 0 &&
                      hover.EmptyFrames == 0 &&
                      postHover.EmptyFrames == 0 &&
                      baseline.UniquePresentedSignatures == 1 &&
                      hover.UniquePresentedSignatures == 1 &&
                      postHover.UniquePresentedSignatures == 1 &&
                      baseline.UniqueCompositeSignatures == 1 &&
                      hover.UniqueCompositeSignatures == 1 &&
                      postHover.UniqueCompositeSignatures == 1 &&
                      hover.PresentedChanges == 0 &&
                      hover.CompositeChanges == 0 &&
                      postHover.PresentedChanges == 0 &&
                      postHover.CompositeChanges == 0;

            Logger.Info(
                $"{label}: baselineDraws={baseline.DrawAdvances}/{baselineFrames}, " +
                $"hoverDraws={hover.DrawAdvances}/{hoverSteps * hoverFramesPerStep}, postHoverDraws={postHover.DrawAdvances}/{postHoverFrames}, " +
                $"baselinePresentedSignatures={baseline.UniquePresentedSignatures}, hoverPresentedSignatures={hover.UniquePresentedSignatures}, postHoverPresentedSignatures={postHover.UniquePresentedSignatures}, " +
                $"baselineCompositeSignatures={baseline.UniqueCompositeSignatures}, hoverCompositeSignatures={hover.UniqueCompositeSignatures}, postHoverCompositeSignatures={postHover.UniqueCompositeSignatures}, " +
                $"hoverPresentedChanges={hover.PresentedChanges}, hoverCompositeChanges={hover.CompositeChanges}, " +
                $"postHoverPresentedChanges={postHover.PresentedChanges}, postHoverCompositeChanges={postHover.CompositeChanges}.");

            return ok;
        }
        finally
        {
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private (int DrawAdvances, int EmptyFrames, int UniquePresentedSignatures, int UniqueCompositeSignatures, int PresentedChanges, int CompositeChanges)
        CollectCursorHoverPresentationMetricsForSmoke(
            int frames,
            bool moveCursor,
            int hoverSteps,
            bool refreshSources)
    {
        int drawAdvances = 0;
        int emptyFrames = 0;
        int presentedChanges = 0;
        int compositeChanges = 0;
        var presentedSignatures = new HashSet<ulong>();
        var compositeSignatures = new HashSet<ulong>();
        byte[]? baselinePresented = null;
        byte[]? baselineComposite = null;

        for (int i = 0; i < frames; i++)
        {
            if (moveCursor)
            {
                MoveCursorAcrossWindowForSmoke(i, hoverSteps);
            }

            int priorDrawCount = _renderBackend.PresentationDrawCount;
            if (refreshSources)
            {
                _pendingInlineSimulationStepsThisFrame = 1;
                InjectCaptureFrames(injectLayers: true);
                _pendingInlineSimulationStepsThisFrame = 0;
            }

            RenderFrame();
            if (WaitForPresentationDrawForSmoke(priorDrawCount, maxIdlePasses: 24))
            {
                drawAdvances++;
            }

            byte[]? presented = _renderBackend.GetPresentedFrameCopyForSmoke();
            byte[]? composite = _lastCompositeFrame?.Downscaled;
            if (presented == null || presented.Length == 0 || composite == null || composite.Length == 0)
            {
                emptyFrames++;
                continue;
            }

            ulong? presentedSignature = ComputeSmokeBufferSignature(presented);
            ulong? compositeSignature = ComputeSmokeBufferSignature(composite);
            if (presentedSignature.HasValue)
            {
                presentedSignatures.Add(presentedSignature.Value);
            }

            if (compositeSignature.HasValue)
            {
                compositeSignatures.Add(compositeSignature.Value);
            }

            if (baselinePresented == null)
            {
                baselinePresented = presented;
                baselineComposite = composite.ToArray();
                continue;
            }

            if (!BuffersEqual(baselinePresented, presented))
            {
                presentedChanges++;
            }

            if (!BuffersEqual(baselineComposite, composite))
            {
                compositeChanges++;
            }
        }

        return (
            drawAdvances,
            emptyFrames,
            presentedSignatures.Count,
            compositeSignatures.Count,
            presentedChanges,
            compositeChanges);
    }

    private void MoveCursorAcrossWindowForSmoke(int iteration, int hoverSteps)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        Point topLeft = PointToScreen(new Point(0, 0));
        int safeSteps = Math.Max(hoverSteps, 1);
        int stepIndex = iteration % safeSteps;
        double normalizedX = safeSteps == 1 ? 0.5 : stepIndex / (double)(safeSteps - 1);
        double x = topLeft.X + (width * (0.15 + normalizedX * 0.70));
        double yBand = iteration % 3 switch
        {
            0 => 0.10,
            1 => 0.35,
            _ => 0.65
        };
        double y = topLeft.Y + (height * yBand);
        SetCursorPos((int)Math.Round(x), (int)Math.Round(y));
    }

    private void MoveCursorOutsideWindowForSmoke()
    {
        Point topLeft = PointToScreen(new Point(0, 0));
        SetCursorPos((int)Math.Round(topLeft.X - 32), (int)Math.Round(topLeft.Y - 32));
    }

    private static double ComputeSmokeAverageDelta(byte[] left, byte[] right)
    {
        int length = Math.Min(left.Length, right.Length);
        if (length == 0)
        {
            return 0.0;
        }

        const int sampleStride = 16;
        long total = 0;
        int count = 0;
        for (int i = 0; i < length; i += sampleStride)
        {
            total += Math.Abs(left[i] - right[i]);
            count++;
        }

        return count > 0 ? total / (double)count : 0.0;
    }

    private static double ComputePercentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        percentile = Math.Clamp(percentile, 0.0, 1.0);
        var ordered = values.OrderBy(value => value).ToArray();
        int index = (int)Math.Clamp(Math.Ceiling((ordered.Length - 1) * percentile), 0, ordered.Length - 1);
        return ordered[index];
    }

    private static string CreateSmokeBitmapFile(string stem, byte[] bgra, int width, int height)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "smoke-assets");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{stem}-{width}x{height}.bmp");

        int stride = width * 4;
        int pixelDataSize = stride * height;
        const int fileHeaderSize = 14;
        const int infoHeaderSize = 40;
        int fileSize = fileHeaderSize + infoHeaderSize + pixelDataSize;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4D42);
        writer.Write(fileSize);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(fileHeaderSize + infoHeaderSize);
        writer.Write(infoHeaderSize);
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(pixelDataSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        for (int row = height - 1; row >= 0; row--)
        {
            int offset = row * stride;
            writer.Write(bgra, offset, stride);
        }

        return path;
    }

    internal void SetReferenceSimulationLayerLifeModeForSmoke(GameOfLifeEngine.LifeMode mode)
    {
        EnsureSimulationLayersInitialized();
        var firstLayer = EnumerateSimulationLeafLayers(_simulationLayers).FirstOrDefault();
        if (firstLayer == null)
        {
            return;
        }

        firstLayer.LifeMode = mode;
        ConfigureSimulationLayerEngines(_configuredRows, _configuredDepth, _currentAspectRatio, randomize: false);
        RenderFrame();
    }

    internal int SetSimulationRowsForSmoke(int rows)
    {
        ApplyDimensions(rows, null, _currentAspectRatio, persist: false);
        return GetReferenceSimulationEngine().Rows;
    }

    internal double ConfigurePacingSmokeScenario(int rows, double targetFps)
    {
        _fpsOscillationEnabled = false;
        _audioReactiveLevelToFpsEnabled = false;
        _currentFpsFromConfig = Math.Clamp(targetFps, 5, 144);
        _currentFps = _currentFpsFromConfig;
        _currentSimulationTargetFps = _currentFpsFromConfig;
        UpdateFramerateMenuChecks();
        ApplyDimensions(rows, null, _currentAspectRatio, persist: false);
        ResetFramePumpCadence(scheduleImmediate: true);
        return _currentFpsFromConfig;
    }

    internal (int rows, double renderFps, double simulationFps, bool fullscreen, bool showFps, bool levelToFramerate, int sourceCount) GetStartupRecoveryStateForSmoke()
    {
        return (_configuredRows, _currentFpsFromConfig, _currentSimulationTargetFps, _pendingFullscreen || _isFullscreen, _showFps, _audioReactiveLevelToFpsEnabled, _sources.Count);
    }

    internal void SetShowFpsForSmoke(bool enabled)
    {
        _showFps = enabled;
        if (ShowFpsMenuItem != null)
        {
            ShowFpsMenuItem.IsChecked = _showFps;
        }
        UpdateFpsOverlay();
    }

    private bool WaitForPresentationDrawForSmoke(int priorDrawCount, int maxIdlePasses = 8)
    {
        for (int pass = 0; pass < maxIdlePasses; pass++)
        {
            if (_renderBackend.PresentationDrawCount > priorDrawCount)
            {
                return true;
            }

            var frame = new DispatcherFrame();
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);

            if (_renderBackend.PresentationDrawCount > priorDrawCount)
            {
                return true;
            }

            frame = new DispatcherFrame();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        return _renderBackend.PresentationDrawCount > priorDrawCount;
    }

    private void InitializeVisualizer()
    {
        _currentAspectRatio = _aspectRatioLocked
            ? _lockedAspectRatio
            : (_sources.Count > 0 ? _sources[0].AspectRatio : DefaultAspectRatio);
        ConfigureSimulationLayerEngines(_configuredRows, _configuredDepth, _currentAspectRatio, randomize: true);
        _configuredRows = GetReferenceSimulationEngine().Rows;
        SnapWindowToAspect(preserveHeight: true);
        _effectiveLifeOpacity = _lifeOpacity;
        UpdateDisplaySurface(force: true);
        UpdateFpsOverlay();
        AttachRenderLoop();
    }

    private void AttachRenderLoop()
    {
        if (_renderLoopAttached || _isShuttingDown)
        {
            return;
        }

        _frameCallbackQueued = 0;
        _nextFramePumpTimestamp = 0;
        Interlocked.Exchange(ref _framePumpStopRequested, 0);
        _framePumpThread = new Thread(FramePumpThreadMain)
        {
            IsBackground = true,
            Name = "LifeViz.FramePump",
            Priority = _lowContentionMode ? ThreadPriority.BelowNormal : ThreadPriority.Normal
        };
        _renderLoopAttached = true;
        _stepStopwatch.Restart();
        if (!_lifetimeStopwatch.IsRunning)
        {
            _lifetimeStopwatch.Start();
        }
        if (!_simulationFpsStopwatch.IsRunning)
        {
            _simulationFpsStopwatch.Start();
        }
        _lastRenderTime = _lifetimeStopwatch.Elapsed.TotalSeconds;
        _framePumpThread.Start();
        ResetFramePumpCadence(scheduleImmediate: true);
    }

    private void DetachRenderLoop()
    {
        if (_renderLoopAttached)
        {
            Interlocked.Exchange(ref _framePumpStopRequested, 1);
            _framePumpWakeEvent.Set();
            if (_framePumpThread is { IsAlive: true } framePumpThread &&
                framePumpThread != Thread.CurrentThread)
            {
                framePumpThread.Join(millisecondsTimeout: 1000);
            }
            _framePumpThread = null;
            Interlocked.Exchange(ref _frameCallbackQueued, 0);
            _nextFramePumpTimestamp = 0;
            _renderLoopAttached = false;
        }

        _stepStopwatch.Stop();
        _lifetimeStopwatch.Stop();
        _simulationFpsStopwatch.Stop();
    }

    private void SuspendRenderLoopForUiInteraction()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _uiInteractionSuspendCount++;
        ResetFramePumpCadence(scheduleImmediate: false);
    }

    private void ResumeRenderLoopAfterUiInteraction()
    {
        if (_uiInteractionSuspendCount <= 0)
        {
            _uiInteractionSuspendCount = 0;
            return;
        }

        _uiInteractionSuspendCount--;
        if (_uiInteractionSuspendCount > 0 || _isShuttingDown)
        {
            return;
        }

        ResetFramePumpCadence(scheduleImmediate: true);
    }

    private IntPtr MainWindow_WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmEnterSizeMove:
                SuspendRenderLoopForUiInteraction();
                break;
            case WmExitSizeMove:
                ResumeRenderLoopAfterUiInteraction();
                break;
        }

        return IntPtr.Zero;
    }

    private void EnableHighResolutionTimer()
    {
        if (_highResolutionTimerEnabled)
        {
            return;
        }

        uint result = timeBeginPeriod(1);
        if (result == 0)
        {
            _highResolutionTimerEnabled = true;
        }
    }

    private void DisableHighResolutionTimer()
    {
        if (!_highResolutionTimerEnabled)
        {
            return;
        }

        timeEndPeriod(1);
        _highResolutionTimerEnabled = false;
    }

    private double _oscillationPhase;
    // We need to track previous time to calculate delta time for phase.
    private double _lastRenderTime;

    private bool IsUiInteractionThrottled()
    {
        return Volatile.Read(ref _uiInteractionSuspendCount) > 0;
    }

    private void LayerEditorWindow_OnClosed(object? sender, EventArgs e)
    {
        _layerEditorWindow = null;
    }

    private void FramePumpThreadMain()
    {
        while (Interlocked.CompareExchange(ref _framePumpStopRequested, 0, 0) == 0)
        {
            if (!_renderLoopAttached || _isShuttingDown)
            {
                _framePumpWakeEvent.WaitOne(20);
                continue;
            }

            long intervalTicks = GetFramePumpIntervalTicks();
            long now = Stopwatch.GetTimestamp();
            long scheduled = Interlocked.Read(ref _nextFramePumpTimestamp);
            if (scheduled == 0)
            {
                scheduled = now;
                Interlocked.Exchange(ref _nextFramePumpTimestamp, scheduled);
            }

            long delayTicks = scheduled - now;
            if (WaitForFramePumpDelay(delayTicks))
            {
                continue;
            }

            now = Stopwatch.GetTimestamp();
            scheduled = Interlocked.Read(ref _nextFramePumpTimestamp);
            long nextScheduled = scheduled + intervalTicks;
            if (nextScheduled < now - intervalTicks)
            {
                nextScheduled = now;
            }
            Interlocked.Exchange(ref _nextFramePumpTimestamp, nextScheduled);

            QueueFrameCallback();
        }
    }

    private void ResetFramePumpCadence(bool scheduleImmediate)
    {
        _nextFramePumpTimestamp = 0;
        _lastUiInteractionRenderTime = 0;
        _lastProfileFrameTimestamp = 0;
        _lastRenderTime = _lifetimeStopwatch.Elapsed.TotalSeconds;

        if (!_renderLoopAttached || _isShuttingDown)
        {
            return;
        }

        if (scheduleImmediate)
        {
            _nextFramePumpTimestamp = 0;
        }

        _framePumpWakeEvent.Set();
    }

    private long GetFramePumpIntervalTicks()
    {
        double throttledTargetFps = IsUiInteractionThrottled()
            ? Math.Min(Math.Max(_currentFpsFromConfig, 1.0), UiInteractionThrottleFps)
            : Math.Max(_currentFpsFromConfig, 1.0);
        double intervalSeconds = 1.0 / throttledTargetFps;
        return Math.Max(1L, (long)Math.Round(intervalSeconds * Stopwatch.Frequency));
    }

    private bool WaitForFramePumpDelay(long delayTicks)
    {
        if (delayTicks <= 0)
        {
            return false;
        }

        double delayMs = delayTicks * 1000.0 / Stopwatch.Frequency;
        if (delayMs > 2.0)
        {
            int coarseWaitMs = Math.Max(1, (int)Math.Floor(delayMs - 0.75));
            if (_framePumpWakeEvent.WaitOne(coarseWaitMs))
            {
                return true;
            }
        }

        while (true)
        {
            if (Interlocked.CompareExchange(ref _framePumpStopRequested, 0, 0) != 0)
            {
                return true;
            }

            if (_framePumpWakeEvent.WaitOne(0))
            {
                return true;
            }

            if (Stopwatch.GetTimestamp() >= Interlocked.Read(ref _nextFramePumpTimestamp))
            {
                return false;
            }

            Thread.SpinWait(256);
        }
    }

    private void QueueFrameCallback()
    {
        if (Interlocked.CompareExchange(ref _frameCallbackQueued, 1, 0) != 0)
        {
            return;
        }

        // Keep the frame loop below WPF's own render/present work so DrawingSurface
        // draw callbacks can continue to drain instead of getting starved by our
        // own frame scheduling.
        DispatcherPriority callbackPriority = DispatcherPriority.Background;

        Dispatcher.BeginInvoke(callbackPriority, new Action(() =>
        {
            Interlocked.Exchange(ref _frameCallbackQueued, 0);
            if (!_renderLoopAttached || _isShuttingDown)
            {
                return;
            }

            FrameTimer_Tick(null, EventArgs.Empty);
        }));
    }

    private void FrameTimer_Tick(object? sender, EventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        long frameStartStamp = BeginProfileStamp();
        double frameGapMs = 0;
        if (frameStartStamp != 0)
        {
            if (_lastProfileFrameTimestamp != 0)
            {
                frameGapMs = FrameProfiler.ElapsedMilliseconds(_lastProfileFrameTimestamp, frameStartStamp);
                _frameProfiler.RecordSample("frame_tick_gap_ms", frameGapMs);
                _frameProfiler.RecordSample("frame_gap_over_25ms", frameGapMs > 25.0 ? 1.0 : 0.0);
                _frameProfiler.RecordSample("frame_gap_over_33ms", frameGapMs > 33.333 ? 1.0 : 0.0);
                _frameProfiler.RecordSample("frame_gap_over_50ms", frameGapMs > 50.0 ? 1.0 : 0.0);
            }

            _lastProfileFrameTimestamp = frameStartStamp;
        }

        double now = _lifetimeStopwatch.Elapsed.TotalSeconds;
        bool interactionThrottled = IsUiInteractionThrottled();
        if (frameStartStamp != 0)
        {
            _frameProfiler.RecordSample("ui_interaction_throttled", interactionThrottled ? 1.0 : 0.0);
        }
        if (interactionThrottled)
        {
            double minInterval = 1.0 / UiInteractionThrottleFps;
            if (now - _lastUiInteractionRenderTime < minInterval)
            {
                return;
            }

            _lastUiInteractionRenderTime = now;
        }

        double dt = now - _lastRenderTime;
        _lastRenderTime = now;
        RecordFrameGapHistory(frameGapMs > 0 ? frameGapMs : dt * 1000.0, dt);

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
            
            _currentSimulationTargetFps = Math.Clamp(targetFps, 1, 144);
        }
        else
        {
            if (Math.Abs(_currentSimulationTargetFps - _currentFpsFromConfig) > 0.1)
            {
                _currentSimulationTargetFps = _currentFpsFromConfig;
            }
        }

        // --- Simulation Step ---
        double elapsed = _stepStopwatch.Elapsed.TotalSeconds;
        _stepStopwatch.Restart();
        _timeSinceLastStep += elapsed;

        UpdateAudioAnalysisRequirements();

        // Update smoothed audio values
        long audioUpdateStamp = BeginProfileStamp();
        double audioLerp = Math.Min(dt * 15.0, 1.0);
        double levelTarget = Math.Clamp(_audioBeatDetector.NormalizedEnergy, 0, 1);
        double levelAttackLerp = Math.Min(dt * 30.0, 1.0);
        double levelReleaseLerp = Math.Min(dt * 20.0, 1.0);
        double levelLerp = levelTarget > _smoothedLevelEnergy ? levelAttackLerp : levelReleaseLerp;
        _smoothedLevelEnergy = _smoothedLevelEnergy + (levelTarget - _smoothedLevelEnergy) * levelLerp;
        if (_smoothedLevelEnergy < 0.01)
        {
            _smoothedLevelEnergy = 0;
        }

        _fastAudioLevel = Math.Clamp(_audioBeatDetector.EnvelopeEnergy, 0, 1);

        double energyTarget = Math.Clamp(_audioBeatDetector.TransientEnergy, 0, 1);
        double energyAttackLerp = Math.Min(dt * 45.0, 1.0);
        double energyReleaseLerp = Math.Min(dt * 70.0, 1.0);
        double energyLerp = energyTarget > _smoothedEnergy ? energyAttackLerp : energyReleaseLerp;
        _smoothedEnergy = _smoothedEnergy + (energyTarget - _smoothedEnergy) * energyLerp;
        if (_smoothedEnergy < 0.001)
        {
            _smoothedEnergy = 0;
        }
        if (NeedsAudioSpectrumAnalysis())
        {
            _smoothedBass = _smoothedBass + (Math.Clamp(_audioBeatDetector.BassEnergy, 0, 100) - _smoothedBass) * audioLerp;
            _smoothedFreq = _smoothedFreq + (Math.Clamp(_audioBeatDetector.MainFrequency, 0, 5000) - _smoothedFreq) * audioLerp;
        }
        else
        {
            _smoothedBass = 0;
            _smoothedFreq = 0;
        }
        ApplyAudioReactiveFps();
        ApplyAudioReactiveLifeOpacity();
        ApplySimulationLayerReactiveState();
        _audioReactiveLevelSeedBurstsLastStep = 0;
        _audioReactiveBeatSeedBurstsLastStep = 0;
        EndProfileStamp("audio_update_ms", audioUpdateStamp);

        var simulationLeaves = EnumerateSimulationLeafLayers(_simulationLayers).ToArray();
        int maxStepsThisFrame = 0;
        bool anyLayerNeedsInject = false;
        foreach (var layer in simulationLeaves)
        {
            if (!layer.Enabled)
            {
                layer.TimeSinceLastStep = 0;
                continue;
            }

            double layerTargetFps = Math.Max(layer.EffectiveSimulationTargetFps, 0);
            if (interactionThrottled)
            {
                layerTargetFps = Math.Min(layerTargetFps, UiInteractionThrottleFps);
            }

            layer.EffectiveSimulationTargetFps = layerTargetFps;
            if (layerTargetFps < 0.01)
            {
                layer.TimeSinceLastStep = 0;
                continue;
            }

            layer.TimeSinceLastStep += elapsed;
            double desiredInterval = 1.0 / layerTargetFps;
            if (layer.TimeSinceLastStep >= desiredInterval)
            {
                anyLayerNeedsInject = true;
                int stepsToRun = (int)Math.Floor(layer.TimeSinceLastStep / desiredInterval);
                stepsToRun = Math.Clamp(stepsToRun, 1, MaxSimulationStepsPerRender);
                maxStepsThisFrame = Math.Max(maxStepsThisFrame, stepsToRun);
            }
        }

        _timeSinceLastStep = simulationLeaves.Length == 0 ? 0 : simulationLeaves.Max(layer => layer.TimeSinceLastStep);
        bool hasInlineSceneSimulation = HasEmbeddedSimulationGroups(_sources);
        _lastSimulationStepsThisFrame = 0;

        if (_sources.Count > 0)
        {
            long injectStamp = BeginProfileStamp();
            if (hasInlineSceneSimulation)
            {
                if (maxStepsThisFrame == 0)
                {
                    if (!_isPaused && simulationLeaves.Any(layer => layer.Enabled))
                    {
                        _audioReactiveLevelSeedBurstsLastStep = ApplyAudioReactiveLevelSeeding(1);
                    }

                    _pendingInlineSimulationStepsThisFrame = 0;
                }
                else if (!_isPaused && anyLayerNeedsInject)
                {
                    long seedingStamp = BeginProfileStamp();
                    _audioReactiveBeatSeedBurstsLastStep = ApplyAudioReactiveBeatSeeding();
                    _audioReactiveLevelSeedBurstsLastStep = ApplyAudioReactiveLevelSeeding(maxStepsThisFrame);
                    EndProfileStamp("audio_reactive_seeding_ms", seedingStamp);
                    _pendingInlineSimulationStepsThisFrame = maxStepsThisFrame;
                }
                else
                {
                    _pendingInlineSimulationStepsThisFrame = 0;
                }

                InjectCaptureFrames(injectLayers: true);
                if (_pendingInlineSimulationStepsThisFrame > 0)
                {
                    _lastSimulationStepsThisFrame = _pendingInlineSimulationStepsThisFrame;
                    _frameProfiler.RecordSample("simulation_steps_per_frame", _pendingInlineSimulationStepsThisFrame);
                }

                _pendingInlineSimulationStepsThisFrame = 0;
            }
            else
            {
                InjectCaptureFrames(injectLayers: false);
            }
            EndProfileStamp("inject_capture_ms", injectStamp);
        }

        if (!hasInlineSceneSimulation && maxStepsThisFrame == 0)
        {
            if (!_isPaused && simulationLeaves.Any(layer => layer.Enabled))
            {
                _audioReactiveLevelSeedBurstsLastStep = ApplyAudioReactiveLevelSeeding(1);
            }
        }
        else if (!hasInlineSceneSimulation && !_isPaused && anyLayerNeedsInject)
        {
            long simulationStageStamp = BeginProfileStamp();
            long seedingStamp = BeginProfileStamp();
            _audioReactiveBeatSeedBurstsLastStep = ApplyAudioReactiveBeatSeeding();
            _audioReactiveLevelSeedBurstsLastStep = ApplyAudioReactiveLevelSeeding(maxStepsThisFrame);
            EndProfileStamp("audio_reactive_seeding_ms", seedingStamp);

            long stepStamp = BeginProfileStamp();
            RunSimulationStepPasses(maxStepsThisFrame, simulationLeaves);
            EndProfileStamp("simulation_step_ms", stepStamp);
            if (simulationStageStamp != 0)
            {
                _lastSimulationStepsThisFrame = maxStepsThisFrame;
                _frameProfiler.RecordSample("simulation_steps_per_frame", maxStepsThisFrame);
            }
            EndProfileStamp("simulation_total_ms", simulationStageStamp);
        }
        else if (!hasInlineSceneSimulation && _isPaused)
        {
            foreach (var layer in simulationLeaves)
            {
                if (!layer.Enabled || layer.EffectiveSimulationTargetFps < 0.01)
                {
                    continue;
                }

                double desiredInterval = 1.0 / layer.EffectiveSimulationTargetFps;
                layer.TimeSinceLastStep = Math.Min(layer.TimeSinceLastStep, desiredInterval);
            }
        }
        else if (hasInlineSceneSimulation && _isPaused)
        {
            foreach (var layer in simulationLeaves)
            {
                if (!layer.Enabled || layer.EffectiveSimulationTargetFps < 0.01)
                {
                    continue;
                }

                double desiredInterval = 1.0 / layer.EffectiveSimulationTargetFps;
                layer.TimeSinceLastStep = Math.Min(layer.TimeSinceLastStep, desiredInterval);
            }
        }

        // --- Rendering Step ---
        long renderStamp = BeginProfileStamp();
        RenderFrame();
        _renderFrames++;
        EndProfileStamp("render_call_ms", renderStamp);

        // --- FPS Counter Update ---
        if (!_simulationFpsStopwatch.IsRunning)
        {
            _lastPresentationDrawCount = _renderBackend.PresentationDrawCount;
            _simulationFpsStopwatch.Start();
        }
        else if (_simulationFpsStopwatch.ElapsedMilliseconds >= 500)
        {
            double seconds = _simulationFpsStopwatch.Elapsed.TotalSeconds;
            if (seconds > 0)
            {
                int currentPresentationDrawCount = _renderBackend.PresentationDrawCount;
                int presentationFrames = currentPresentationDrawCount - _lastPresentationDrawCount;
                if (presentationFrames < 0)
                {
                    presentationFrames = currentPresentationDrawCount;
                }

                _simulationDisplayFps = _simulationFrames / seconds;
                _renderDisplayFps = _renderFrames / seconds;
                _presentationDisplayFps = presentationFrames / seconds;
                _lastPresentationDrawCount = currentPresentationDrawCount;

                if (_frameProfiler.IsActive)
                {
                    _frameProfiler.RecordSample("frame_loop_fps", _renderDisplayFps);
                    _frameProfiler.RecordSample("presentation_draw_fps", _presentationDisplayFps);
                    _frameProfiler.RecordSample("simulation_steps_per_second", _simulationDisplayFps);
                }

                RecordLiveMetric("frame_loop_fps", _renderDisplayFps);
                RecordLiveMetric("presentation_draw_fps", _presentationDisplayFps);
                RecordLiveMetric("simulation_steps_per_second", _simulationDisplayFps);
            }
            _simulationFrames = 0;
            _renderFrames = 0;
            _simulationFpsStopwatch.Restart();
        }
        long overlayStamp = BeginProfileStamp();
        UpdateFpsOverlay();
        EndProfileStamp("fps_overlay_ms", overlayStamp);
        EndProfileStamp("frame_total_ms", frameStartStamp);
    }

    private void RebuildSurface() => UpdateDisplaySurface(force: true);

    private void RenderFrame()
    {
        if (_isShuttingDown)
        {
            return;
        }

        if (_renderBackend.PixelWidth <= 0 || _renderBackend.PixelHeight <= 0 || _pixelBuffer == null)
        {
            return;
        }

        int width = _renderBackend.PixelWidth;
        int height = _renderBackend.PixelHeight;
        int stride = width * 4;
        int requiredLength = stride * height;

        if (_pixelBuffer.Length != requiredLength)
        {
            _pixelBuffer = new byte[requiredLength];
        }

        var referenceEngine = GetReferenceSimulationEngine();
        int engineRows = referenceEngine.Rows;
        int engineCols = referenceEngine.Columns;
        BuildMappings(width, height, engineCols, engineRows);

        var composite = _lastCompositeFrame;
        if (HasEmbeddedSimulationGroups(_sources) &&
            composite != null)
        {
            bool presentedInlineGpu = false;
            if (!_isRecording &&
                _renderBackend.SupportsGpuSimulationComposition &&
                composite.GpuSurface != null &&
                composite.GpuSurface.Width == width &&
                composite.GpuSurface.Height == height)
            {
                GpuCompositeSurface presentationSurface = composite.GpuSurface;
                if (composite.GpuSurface.SharedTextureHandle != IntPtr.Zero)
                {
                    GpuSharedDevice.FlushIfCreated();
                    if (_inlineGpuPresentationSnapshotter.IsAvailable)
                    {
                        var snappedSurface = _inlineGpuPresentationSnapshotter.Snapshot(composite.GpuSurface);
                        if (snappedSurface != null)
                        {
                            presentationSurface = snappedSurface;
                        }
                    }
                }

                long presentInlineStamp = BeginProfileStamp();
                presentedInlineGpu = _renderBackend.PresentSimulationComposition(
                    Array.Empty<SimulationPresentationLayerData>(),
                    null,
                    presentationSurface,
                    simulationBaseline: 0,
                    useSignedAddSubPassthrough: false,
                    useMixedAddSubPassthroughModel: false,
                    invertComposite: _invertComposite);
                EndProfileStamp("present_frame_ms", presentInlineStamp);
            }

            if (presentedInlineGpu)
            {
                _passthroughCompositedInPixelBuffer = false;

                long inlineEffectStamp = BeginProfileStamp();
                UpdateEffectInput();
                EndProfileStamp("underlay_effect_ms", inlineEffectStamp);

                long inlineRecordStamp = BeginProfileStamp();
                TryRecordFrame(requiredLength);
                EndProfileStamp("record_frame_ms", inlineRecordStamp);
                return;
            }

            if (composite.GpuSurface != null)
            {
                _inlineGpuPresentationFallbackCount++;
                if (DateTime.UtcNow - _lastInlinePresentationFallbackLogUtc > TimeSpan.FromSeconds(2))
                {
                    _lastInlinePresentationFallbackLogUtc = DateTime.UtcNow;
                    Logger.Warn(
                        $"Inline GPU present fell back to CPU. " +
                        $"Surface={composite.GpuSurface.Width}x{composite.GpuSurface.Height}, " +
                        $"Target={width}x{height}, " +
                        $"Handle={(composite.GpuSurface.SharedTextureHandle != IntPtr.Zero ? "shared" : "none")}, " +
                        $"Invert={_invertComposite}, " +
                        $"CpuReadable={composite.Downscaled.Length >= requiredLength}.");
                }
            }

            if (composite.DownscaledWidth == width &&
                composite.DownscaledHeight == height &&
                composite.Downscaled.Length >= requiredLength)
            {
                Buffer.BlockCopy(composite.Downscaled, 0, _pixelBuffer, 0, requiredLength);
                if (_invertComposite)
                {
                    InvertBuffer(_pixelBuffer);
                }

                _passthroughCompositedInPixelBuffer = true;

                long presentInlineStamp = BeginProfileStamp();
                _renderBackend.PresentFrame(_pixelBuffer, stride);
                EndProfileStamp("present_frame_ms", presentInlineStamp);

                long inlineEffectStamp = BeginProfileStamp();
                UpdateEffectInput();
                EndProfileStamp("underlay_effect_ms", inlineEffectStamp);

                long inlineRecordStamp = BeginProfileStamp();
                TryRecordFrame(requiredLength);
                EndProfileStamp("record_frame_ms", inlineRecordStamp);
                return;
            }
        }

        byte[]? passthroughBuffer = null;
        bool compositePassthroughInPixelBuffer = false;
        if ((_passthroughEnabled || !EnumerateSimulationLeafLayers(_simulationLayers).Any(layer => layer.Enabled)) &&
            composite != null &&
            composite.DownscaledWidth == engineCols &&
            composite.DownscaledHeight == engineRows &&
            composite.Downscaled.Length >= engineCols * engineRows * 4)
        {
            // Use the CPU path when passthrough matches engine dimensions.
            passthroughBuffer = composite.Downscaled;
            compositePassthroughInPixelBuffer = true;
        }

        long colorBuffersStamp = BeginProfileStamp();
        var enabledLayers = EnumerateSimulationLeafLayers(_simulationLayers)
            .Where(layer => layer.Enabled)
            .ToArray();
        var activeLayerEntries = new List<(SimulationLayerState Layer, SimulationPresentationLayerData Data)>(enabledLayers.Length);
        foreach (var layer in enabledLayers)
        {
            if (TryBuildSimulationPresentationLayer(layer, Math.Clamp(_effectiveLifeOpacity * layer.EffectiveLifeOpacity, 0, 1), out var presentationLayer))
            {
                activeLayerEntries.Add((layer, presentationLayer));
            }
        }
        _frameProfiler.RecordSample("gpu_shared_sim_layer_count", activeLayerEntries.Count(entry => entry.Data.SharedTextureHandle != IntPtr.Zero));
        _frameProfiler.RecordSample("cpu_uploaded_sim_layer_count", activeLayerEntries.Count(entry => entry.Data.SharedTextureHandle == IntPtr.Zero));
        EndProfileStamp("fill_sim_color_buffers_ms", colorBuffersStamp);
        var activeLayers = activeLayerEntries.Select(entry => entry.Layer).ToArray();
        var presentationLayers = activeLayerEntries.Select(entry => entry.Data).ToArray();

        long blendStamp = BeginProfileStamp();

        bool renderCompositeDirect = presentationLayers.Length == 0 && composite != null;

        if (renderCompositeDirect &&
            presentationLayers.Length == 0 &&
            composite != null &&
            composite.DownscaledWidth == width &&
            composite.DownscaledHeight == height &&
            composite.Downscaled.Length >= requiredLength)
        {
            Buffer.BlockCopy(composite.Downscaled, 0, _pixelBuffer, 0, requiredLength);
            if (_invertComposite)
            {
                InvertBuffer(_pixelBuffer);
            }

            _passthroughCompositedInPixelBuffer = true;

            long presentPassthroughStamp = BeginProfileStamp();
            _renderBackend.PresentFrame(_pixelBuffer, stride);
            EndProfileStamp("present_frame_ms", presentPassthroughStamp);

            long underlayOnlyStamp = BeginProfileStamp();
            UpdateEffectInput();
            EndProfileStamp("underlay_effect_ms", underlayOnlyStamp);

            long recordUnderlayStamp = BeginProfileStamp();
            TryRecordFrame(requiredLength);
            EndProfileStamp("record_frame_ms", recordUnderlayStamp);
            EndProfileStamp("cpu_blend_ms", blendStamp);
            return;
        }

        bool hasEnabledSubtractiveSimulationLayer = activeLayers.Any(layer => layer.BlendMode == BlendMode.Subtractive);
        bool hasEnabledNonSubtractiveSimulationLayer = activeLayers.Any(layer => layer.BlendMode != BlendMode.Subtractive);
        bool hasEnabledAdditiveSimulationLayer = activeLayers.Any(layer => layer.BlendMode == BlendMode.Additive);
        int additiveLayerCount = activeLayers.Count(layer => layer.BlendMode == BlendMode.Additive);
        int subtractiveLayerCount = activeLayers.Count(layer => layer.BlendMode == BlendMode.Subtractive);
        bool hasEnabledNonAddSubSimulationLayer = activeLayers.Any(layer =>
            layer.BlendMode != BlendMode.Additive &&
            layer.BlendMode != BlendMode.Subtractive);
        bool useCompositeUnderlay = _passthroughEnabled || presentationLayers.Length == 0;
        GpuCompositeSurface? passthroughSurface =
            useCompositeUnderlay && !compositePassthroughInPixelBuffer ? composite?.GpuSurface : null;
        bool hasPassthroughBaseline = (compositePassthroughInPixelBuffer && passthroughBuffer != null) || passthroughSurface != null;
        bool useSignedAddSubPassthrough = ShouldUseSignedAddSubPassthrough(
            hasPassthroughBaseline,
            activeLayers.Length > 0,
            hasEnabledNonAddSubSimulationLayer);
        bool useMixedAddSubPassthroughModel = useSignedAddSubPassthrough &&
                                              additiveLayerCount > 0 &&
                                              subtractiveLayerCount > 0;
        int simulationBaseline;
        if (hasEnabledSubtractiveSimulationLayer && !hasEnabledNonSubtractiveSimulationLayer)
        {
            // Subtractive-only stacks use white identity.
            simulationBaseline = 255;
        }
        else if (hasEnabledAdditiveSimulationLayer &&
                 hasEnabledSubtractiveSimulationLayer &&
                 !hasEnabledNonAddSubSimulationLayer)
        {
            // Mixed additive/subtractive stacks converge around 50%; use a
            // neutral gray baseline to keep both layer families visible.
            simulationBaseline = 128;
        }
        else
        {
            simulationBaseline = 0;
        }

        double globalLifeOpacity = Math.Clamp(_effectiveLifeOpacity, 0, 1);
        bool hasGpuPassthroughUnderlay = compositePassthroughInPixelBuffer ? passthroughBuffer != null : passthroughSurface != null;
        if (!_isRecording &&
            _renderBackend.SupportsGpuSimulationComposition &&
            (presentationLayers.Length > 0 || hasGpuPassthroughUnderlay))
        {
            bool hasSharedGpuProducerResources =
                presentationLayers.Any(layer => layer.SharedTextureHandle != IntPtr.Zero) ||
                (passthroughSurface?.SharedTextureHandle ?? IntPtr.Zero) != IntPtr.Zero;
            if (hasSharedGpuProducerResources)
            {
                GpuSharedDevice.FlushIfCreated();
            }

            long gpuCompositeStamp = BeginProfileStamp();
            bool presented = _renderBackend.PresentSimulationComposition(
                presentationLayers,
                compositePassthroughInPixelBuffer ? passthroughBuffer : null,
                passthroughSurface,
                simulationBaseline,
                useSignedAddSubPassthrough,
                useMixedAddSubPassthroughModel,
                _invertComposite);
            EndProfileStamp("gpu_final_composite_submit_ms", gpuCompositeStamp);

            if (presented)
            {
                _passthroughCompositedInPixelBuffer = hasGpuPassthroughUnderlay;
                EndProfileStamp("cpu_blend_ms", blendStamp);

                long gpuUnderlayStamp = BeginProfileStamp();
                UpdateEffectInput();
                EndProfileStamp("underlay_effect_ms", gpuUnderlayStamp);

                long gpuRecordStamp = BeginProfileStamp();
                TryRecordFrame(requiredLength);
                EndProfileStamp("record_frame_ms", gpuRecordStamp);
                return;
            }
        }

        Parallel.For(0, height, row =>
        {
            int sourceRow = _rowMap[row];
            for (int col = 0; col < width; col++)
            {
                int sourceCol = _colMap[col];
                int sourceIndex = (sourceRow * engineCols + sourceCol) * 4;
                int index = (row * stride) + (col * 4);

                int underlayB = simulationBaseline;
                int underlayG = simulationBaseline;
                int underlayR = simulationBaseline;
                if (compositePassthroughInPixelBuffer && passthroughBuffer != null)
                {
                    underlayB = passthroughBuffer[sourceIndex];
                    underlayG = passthroughBuffer[sourceIndex + 1];
                    underlayR = passthroughBuffer[sourceIndex + 2];
                }

                if (useSignedAddSubPassthrough)
                {
                    int addB = 0;
                    int addG = 0;
                    int addR = 0;
                    int subB = 0;
                    int subG = 0;
                    int subR = 0;
                    foreach (var layer in activeLayers)
                    {
                        byte[]? colorBuffer = layer.ColorBuffer;
                        if (colorBuffer == null)
                        {
                            continue;
                        }

                        double layerOpacity = Math.Clamp(globalLifeOpacity * layer.EffectiveLifeOpacity, 0, 1);
                        if (layerOpacity <= 0.0001)
                        {
                            continue;
                        }

                        if (layer.BlendMode == BlendMode.Subtractive)
                        {
                            subR += (int)Math.Round((255 - colorBuffer[sourceIndex]) * layerOpacity);
                            subG += (int)Math.Round((255 - colorBuffer[sourceIndex + 1]) * layerOpacity);
                            subB += (int)Math.Round((255 - colorBuffer[sourceIndex + 2]) * layerOpacity);
                        }
                        else
                        {
                            addR += (int)Math.Round(colorBuffer[sourceIndex] * layerOpacity);
                            addG += (int)Math.Round(colorBuffer[sourceIndex + 1] * layerOpacity);
                            addB += (int)Math.Round(colorBuffer[sourceIndex + 2] * layerOpacity);
                        }
                    }

                    if (useMixedAddSubPassthroughModel)
                    {
                        // Mixed add+sub passthrough model with headroom normalization:
                        // additive only uses darkness headroom, subtractive only uses brightness headroom.
                        double underlayB01 = underlayB / 255.0;
                        double underlayG01 = underlayG / 255.0;
                        double underlayR01 = underlayR / 255.0;

                        double scaledSubB = Math.Clamp(subB * underlayB01, 0, 255);
                        double scaledSubG = Math.Clamp(subG * underlayG01, 0, 255);
                        double scaledSubR = Math.Clamp(subR * underlayR01, 0, 255);

                        double scaledAddB = addB * (1.0 - underlayB01);
                        double scaledAddG = addG * (1.0 - underlayG01);
                        double scaledAddR = addR * (1.0 - underlayR01);

                        int mixedOutB = ClampToByte((int)Math.Round(underlayB + scaledAddB - scaledSubB));
                        int mixedOutG = ClampToByte((int)Math.Round(underlayG + scaledAddG - scaledSubG));
                        int mixedOutR = ClampToByte((int)Math.Round(underlayR + scaledAddR - scaledSubR));

                        _pixelBuffer[index] = (byte)mixedOutB;
                        _pixelBuffer[index + 1] = (byte)mixedOutG;
                        _pixelBuffer[index + 2] = (byte)mixedOutR;
                    }
                    else
                    {
                        int deltaB = addB - subB;
                        int deltaG = addG - subG;
                        int deltaR = addR - subR;

                        _pixelBuffer[index] = ClampToByte(underlayB + deltaB);
                        _pixelBuffer[index + 1] = ClampToByte(underlayG + deltaG);
                        _pixelBuffer[index + 2] = ClampToByte(underlayR + deltaR);
                    }
                    _pixelBuffer[index + 3] = 255;
                    continue;
                }

                int simB = simulationBaseline;
                int simG = simulationBaseline;
                int simR = simulationBaseline;

                foreach (var layer in activeLayers)
                {
                    byte[]? colorBuffer = layer.ColorBuffer;
                    if (colorBuffer == null)
                    {
                        continue;
                    }

                    double layerOpacity = Math.Clamp(globalLifeOpacity * layer.EffectiveLifeOpacity, 0, 1);
                    if (layerOpacity <= 0.0001)
                    {
                        continue;
                    }

                    byte lr = colorBuffer[sourceIndex];
                    byte lg = colorBuffer[sourceIndex + 1];
                    byte lb = colorBuffer[sourceIndex + 2];
                    BlendSimulationLayerInto(ref simB, ref simG, ref simR, lr, lg, lb, layer.BlendMode, layerOpacity);
                }

                int outB;
                int outG;
                int outR;
                if (compositePassthroughInPixelBuffer && passthroughBuffer != null)
                {
                    // Apply simulation as delta over passthrough so passthrough doesn't
                    // compress/attenuate simulation contrast.
                    int deltaB = simB - simulationBaseline;
                    int deltaG = simG - simulationBaseline;
                    int deltaR = simR - simulationBaseline;

                    outB = underlayB + deltaB;
                    outG = underlayG + deltaG;
                    outR = underlayR + deltaR;
                }
                else
                {
                    outB = simB;
                    outG = simG;
                    outR = simR;
                }

                _pixelBuffer[index] = ClampToByte(outB);
                _pixelBuffer[index + 1] = ClampToByte(outG);
                _pixelBuffer[index + 2] = ClampToByte(outR);
                _pixelBuffer[index + 3] = 255;
            }
        });
        _passthroughCompositedInPixelBuffer = compositePassthroughInPixelBuffer;

        if (_invertComposite)
        {
            InvertBuffer(_pixelBuffer);
        }
        EndProfileStamp("cpu_blend_ms", blendStamp);

        long presentStamp = BeginProfileStamp();
        _renderBackend.PresentFrame(_pixelBuffer, stride);
        EndProfileStamp("present_frame_ms", presentStamp);

        long underlayStamp = BeginProfileStamp();
        UpdateUnderlayBitmap(requiredLength);
        UpdateEffectInput();
        EndProfileStamp("underlay_effect_ms", underlayStamp);

        long recordStamp = BeginProfileStamp();
        TryRecordFrame(requiredLength);
        EndProfileStamp("record_frame_ms", recordStamp);
    }

    private void TryRecordFrame(int requiredLength)
    {
        if (!_isRecording || _recordingSession == null || _recordingStopwatch == null)
        {
            return;
        }

        if (_displayWidth <= 0 || _displayHeight <= 0)
        {
            return;
        }

        if (_displayWidth != _recordingDisplayWidth || _displayHeight != _recordingDisplayHeight)
        {
            StopRecording(showMessage: true, reason: "Recording stopped because the output resolution changed.");
            return;
        }

        if (_recordingSession.TryGetError(out var errorMessage))
        {
            StopRecording(showMessage: false);
            ShowRecordingError(errorMessage ?? "Unknown encoder failure.");
            return;
        }

        var elapsed = _recordingStopwatch.Elapsed;
        if (elapsed < _nextRecordingFrameTime)
        {
            return;
        }

        _nextRecordingFrameTime += _recordingFrameInterval;
        while (elapsed >= _nextRecordingFrameTime)
        {
            _nextRecordingFrameTime += _recordingFrameInterval;
        }

        int sourceSize = _recordingSourceWidth * _recordingSourceHeight * 4;
        int frameSize = _recordingWidth * _recordingHeight * 4;
        if (sourceSize > requiredLength || frameSize <= 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(frameSize);
        if (!BuildRecordingFrame(buffer, frameSize, sourceSize))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return;
        }

        if (!_recordingSession.TryEnqueue(buffer))
        {
            Logger.Warn("Recording frame dropped (encoder backlog).");
        }
    }

    private bool BuildRecordingFrame(byte[] destination, int frameSize, int sourceSize)
    {
        if (_pixelBuffer == null || destination.Length < frameSize)
        {
            return false;
        }

        int displayWidth = _recordingDisplayWidth;
        int displayHeight = _recordingDisplayHeight;
        int sourceWidth = _recordingSourceWidth;
        int sourceHeight = _recordingSourceHeight;
        if (displayWidth <= 0 || displayHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return false;
        }

        if (sourceWidth > displayWidth || sourceHeight > displayHeight)
        {
            return false;
        }

        int displaySize = displayWidth * displayHeight * 4;
        if (_pixelBuffer.Length < displaySize || sourceSize < sourceWidth * sourceHeight * 4)
        {
            return false;
        }

        int displayStride = displayWidth * 4;
        int sourceStride = sourceWidth * 4;

        byte[] targetBuffer = destination;
        byte[]? sourceBuffer = null;
        if (_recordingScale > 1)
        {
            sourceBuffer = ArrayPool<byte>.Shared.Rent(sourceSize);
            targetBuffer = sourceBuffer;
        }

        for (int row = 0; row < sourceHeight; row++)
        {
            int srcRowOffset = row * displayStride;
            int destRowOffset = row * sourceStride;
            for (int col = 0; col < sourceWidth; col++)
            {
                int srcIndex = srcRowOffset + (col * 4);
                int destIndex = destRowOffset + (col * 4);
                targetBuffer[destIndex] = _pixelBuffer[srcIndex];
                targetBuffer[destIndex + 1] = _pixelBuffer[srcIndex + 1];
                targetBuffer[destIndex + 2] = _pixelBuffer[srcIndex + 2];
                targetBuffer[destIndex + 3] = 255;
            }
        }

        var composite = _lastCompositeFrame;
        if (!_passthroughEnabled || composite == null || _passthroughCompositedInPixelBuffer)
        {
            if (_recordingScale > 1)
            {
                ScaleRecordingFrame(targetBuffer, destination);
                ArrayPool<byte>.Shared.Return(sourceBuffer!);
            }
            return true;
        }

        byte[]? overlay = null;
        int overlayWidth = 0;
        int overlayHeight = 0;
        if ((composite.DownscaledWidth == sourceWidth && composite.DownscaledHeight == sourceHeight) ||
            (composite.DownscaledWidth == displayWidth && composite.DownscaledHeight == displayHeight))
        {
            overlay = composite.Downscaled;
            overlayWidth = composite.DownscaledWidth;
            overlayHeight = composite.DownscaledHeight;
        }

        int overlayLength = overlayWidth > 0 && overlayHeight > 0 ? overlayWidth * overlayHeight * 4 : 0;
        if (overlay == null || overlay.Length < overlayLength)
        {
            if (_recordingScale > 1)
            {
                ScaleRecordingFrame(targetBuffer, destination);
                ArrayPool<byte>.Shared.Return(sourceBuffer!);
            }
            return true;
        }

        int overlayStride = overlayWidth * 4;
        byte[] overlayBuffer = overlay;
        byte[]? scratchBuffer = null;
        if (_invertComposite)
        {
            scratchBuffer = ArrayPool<byte>.Shared.Rent(overlayLength);
            Buffer.BlockCopy(overlay, 0, scratchBuffer, 0, overlayLength);
            InvertBuffer(scratchBuffer);
            overlayBuffer = scratchBuffer;
        }

        for (int row = 0; row < sourceHeight; row++)
        {
            int destRowOffset = row * sourceStride;
            int overlayRowOffset = row * overlayStride;
            var passthroughBlendMode = GetEffectivePassthroughBlendMode();
            for (int col = 0; col < sourceWidth; col++)
            {
                int destIndex = destRowOffset + (col * 4);
                int overlayIndex = overlayRowOffset + (col * 4);
                BlendInto(targetBuffer, destIndex, overlayBuffer[overlayIndex], overlayBuffer[overlayIndex + 1], overlayBuffer[overlayIndex + 2],
                    overlayBuffer[overlayIndex + 3], passthroughBlendMode, 1.0);
            }
        }

        if (scratchBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(scratchBuffer);
        }

        if (_recordingScale > 1)
        {
            ScaleRecordingFrame(targetBuffer, destination);
            ArrayPool<byte>.Shared.Return(sourceBuffer!);
        }
        return true;
    }

    private void ScaleRecordingFrame(byte[] source, byte[] destination)
    {
        if (_recordingScale <= 1)
        {
            Buffer.BlockCopy(source, 0, destination, 0, source.Length);
            return;
        }

        int srcWidth = _recordingSourceWidth;
        int srcHeight = _recordingSourceHeight;
        int scale = _recordingScale;
        int destWidth = _recordingWidth;

        for (int y = 0; y < srcHeight; y++)
        {
            int srcRowOffset = y * srcWidth * 4;
            int destRowBase = y * scale * destWidth * 4;
            for (int x = 0; x < srcWidth; x++)
            {
                int srcIndex = srcRowOffset + (x * 4);
                byte b = source[srcIndex];
                byte g = source[srcIndex + 1];
                byte r = source[srcIndex + 2];
                byte a = source[srcIndex + 3];
                int destPixelBase = destRowBase + (x * scale * 4);
                for (int dy = 0; dy < scale; dy++)
                {
                    int destRowOffset = destPixelBase + (dy * destWidth * 4);
                    for (int dx = 0; dx < scale; dx++)
                    {
                        int destIndex = destRowOffset + (dx * 4);
                        destination[destIndex] = b;
                        destination[destIndex + 1] = g;
                        destination[destIndex + 2] = r;
                        destination[destIndex + 3] = a;
                    }
                }
            }
        }
    }

    private static int GetRecordingTargetHeight(int baseHeight)
    {
        if (baseHeight <= 0)
        {
            return baseHeight;
        }

        int[] targets = { 720, 1080, 1440, 2160 };
        foreach (int target in targets)
        {
            if (target >= baseHeight && target % baseHeight == 0)
            {
                return target;
            }
        }

        return baseHeight;
    }

    private void TogglePause_Click(object sender, RoutedEventArgs e)
    {
        _isPaused = !_isPaused;
        PauseMenuItem.Header = _isPaused ? "Resume Simulation" : "Pause Simulation";
    }

    private void Randomize_Click(object sender, RoutedEventArgs e)
    {
        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            layer.Engine?.Randomize();
        }
        RenderFrame();
    }

    private void UpdateUpdateMenuItem()
    {
        if (UpdateMenuItem != null)
        {
            UpdateMenuItem.IsEnabled = !_updateInProgress;
        }
    }

    private async void UpdateToLatestRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_updateInProgress)
        {
            return;
        }

        var confirm = MessageBox.Show(this,
            "Download and install the latest LifeViz release from GitHub? LifeViz will close while the installer runs.",
            "Update LifeViz",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _updateInProgress = true;
        UpdateUpdateMenuItem();

        try
        {
            var release = await FetchLatestReleaseAsync();
            if (release == null)
            {
                MessageBox.Show(this, "Unable to reach the latest GitHub release.", "Update Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var asset = release.Assets.FirstOrDefault(entry =>
                string.Equals(entry.Name, GitHubReleaseAssetName, StringComparison.OrdinalIgnoreCase));
            if (asset == null || string.IsNullOrWhiteSpace(asset.DownloadUrl))
            {
                MessageBox.Show(this,
                    $"The latest release ({release.TagName ?? "unknown"}) does not include {GitHubReleaseAssetName}.",
                    "Update Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), "lifeviz-update");
            Directory.CreateDirectory(tempRoot);
            string? safeTag = SanitizeTag(release.TagName);
            string installerName = string.IsNullOrWhiteSpace(safeTag)
                ? GitHubReleaseAssetName
                : $"lifeviz_installer_{safeTag}.exe";
            string installerPath = Path.Combine(tempRoot, installerName);

            await DownloadFileAsync(asset.DownloadUrl, installerPath);
            Logger.Info($"Downloaded GitHub release {release.TagName ?? "unknown"} to {installerPath}");

            MessageBox.Show(this,
                "Update downloaded. The installer will launch now; LifeViz will close to finish the update.",
                "Update Ready",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            });

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to update from GitHub release.", ex);
            MessageBox.Show(this, $"Update failed:\n{ex.Message}", "Update Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _updateInProgress = false;
            UpdateUpdateMenuItem();
        }
    }

    private static string? SanitizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(tag.Where(ch => !invalid.Contains(ch)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static async Task<GitHubRelease?> FetchLatestReleaseAsync()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("lifeviz-updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.Timeout = TimeSpan.FromSeconds(30);

        using var response = await client.GetAsync(GitHubLatestReleaseUri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            Logger.Warn($"GitHub release check failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, GitHubJsonOptions);
    }

    private static async Task DownloadFileAsync(string url, string destination)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("lifeviz-updater");
        client.Timeout = TimeSpan.FromMinutes(5);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
        await stream.CopyToAsync(fileStream);
    }

    private void OpenLayerEditor_Click(object sender, RoutedEventArgs e)
    {
        OpenLayerEditor();
    }

    internal void OpenLayerEditor()
    {
        try
        {
            if (_layerEditorWindow == null)
            {
                _layerEditorWindow = new LayerEditorWindow(this);
                _layerEditorWindow.Closed += LayerEditorWindow_OnClosed;
                _layerEditorWindow.Show();
                return;
            }

            if (_layerEditorWindow.WindowState == WindowState.Minimized)
            {
                _layerEditorWindow.WindowState = WindowState.Normal;
            }

            _layerEditorWindow.Activate();
            _layerEditorWindow.RefreshFromSourcesIfLive();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to open Scene Editor window.", ex);
            MessageBox.Show(this, $"Failed to open Scene Editor:\n{ex.Message}", "Scene Editor Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _layerEditorWindow = null;
        }
    }

    internal LayerEditorWindow? GetOpenLayerEditorForSmoke() => _layerEditorWindow;

    internal bool RunFramePumpThreadSafetySmoke()
    {
        OpenLayerEditor();
        var editor = _layerEditorWindow;
        if (editor == null)
        {
            return false;
        }

        editor.Activate();

        Exception? workerException = null;
        using var done = new ManualResetEventSlim(false);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                for (int i = 0; i < 16; i++)
                {
                    _ = IsUiInteractionThrottled();
                }
            }
            catch (Exception ex)
            {
                workerException = ex;
            }
            finally
            {
                done.Set();
            }
        });

        if (!done.Wait(TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        editor.Close();
        if (workerException != null)
        {
            Logger.Error("Frame pump thread-safety smoke failed.", workerException);
            return false;
        }

        return true;
    }

    internal void OpenRootContextMenuAtScreenPoint(double screenX, double screenY)
    {
        if (RootContextMenu == null)
        {
            return;
        }

        RootContextMenu.Placement = PlacementMode.AbsolutePoint;
        RootContextMenu.HorizontalOffset = screenX;
        RootContextMenu.VerticalOffset = screenY;
        RootContextMenu.IsOpen = false;
        RootContextMenu.IsOpen = true;
    }

    private void RecordMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            StopRecording(showMessage: false);
        }
        else
        {
            StartRecording();
        }
    }

    private void StartRecording()
    {
        if (_isRecording)
        {
            return;
        }

        if (_displayWidth <= 0 || _displayHeight <= 0)
        {
            MessageBox.Show(this, "Recording is unavailable until the renderer is initialized.", "Recording", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int width = _displayWidth;
        int height = _displayHeight;
        int sourceWidth = width - (width % 2);
        int sourceHeight = height - (height % 2);
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            MessageBox.Show(this, "Recording requires an even output size.", "Recording", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int targetHeight = GetRecordingTargetHeight(sourceHeight);
        int scale = Math.Max(1, targetHeight / sourceHeight);
        int targetWidth = sourceWidth * scale;
        int targetOutputHeight = sourceHeight * scale;
        if (targetOutputHeight != targetHeight)
        {
            targetHeight = targetOutputHeight;
        }
        int fps = Math.Clamp((int)Math.Round(_currentFpsFromConfig), 1, 144);
        var settings = RecordingSettings.FromQuality(_recordingQuality, targetWidth, targetHeight, fps);

        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LifeViz");
        Directory.CreateDirectory(folder);
        string extension = settings.FileExtension.StartsWith(".") ? settings.FileExtension : $".{settings.FileExtension}";
        string filePath = Path.Combine(folder, $"lifeviz_{DateTime.Now:yyyyMMdd_HHmmss}{extension}");

        try
        {
            _recordingSession = new RecordingSession(filePath, targetWidth, targetHeight, fps, settings);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start recording.", ex);
            _recordingSession = null;
            ShowRecordingError(ex.Message);
            return;
        }

        _recordingDisplayWidth = width;
        _recordingDisplayHeight = height;
        _recordingSourceWidth = sourceWidth;
        _recordingSourceHeight = sourceHeight;
        _recordingScale = scale;
        _recordingWidth = targetWidth;
        _recordingHeight = targetHeight;
        _recordingPath = filePath;
        _recordingFrameInterval = TimeSpan.FromSeconds(1.0 / fps);
        _nextRecordingFrameTime = TimeSpan.Zero;
        _recordingStopwatch = Stopwatch.StartNew();
        _isRecording = true;
        UpdateRecordingUi();
        Logger.Info($"Recording started: {filePath} ({width}x{height} @ {fps} fps, {settings.Quality})");
    }

    private void StopRecording(bool showMessage, string? reason = null)
    {
        if (!_isRecording)
        {
            return;
        }

        _isRecording = false;
        _recordingStopwatch?.Stop();
        _recordingStopwatch = null;

        try
        {
            _recordingSession?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("Recording finalize failed.", ex);
        }
        finally
        {
            _recordingSession = null;
        }

        UpdateRecordingUi();

        if (showMessage && !string.IsNullOrWhiteSpace(reason))
        {
            MessageBox.Show(this, reason, "Recording", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        if (!string.IsNullOrWhiteSpace(_recordingPath))
        {
            Logger.Info($"Recording saved: {_recordingPath}");
        }

        _recordingPath = null;
        _recordingWidth = 0;
        _recordingHeight = 0;
        _recordingSourceWidth = 0;
        _recordingSourceHeight = 0;
        _recordingDisplayWidth = 0;
        _recordingDisplayHeight = 0;
        _recordingScale = 1;
        _recordingFrameInterval = TimeSpan.Zero;
        _nextRecordingFrameTime = TimeSpan.Zero;
    }

    private void UpdateRecordingUi()
    {
        if (RecordMenuItem != null)
        {
            RecordMenuItem.Header = _isRecording ? "Stop Recording" : "Start Recording";
        }

        if (TaskbarInfo != null)
        {
            TaskbarInfo.Overlay = _isRecording ? GetRecordingOverlayIcon() : null;
        }
    }

    private void UpdateRecordingQualityMenuChecks()
    {
        if (RecordingQualityMenu == null)
        {
            return;
        }

        foreach (var item in RecordingQualityMenu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Tag is string tag &&
                Enum.TryParse<RecordingQuality>(tag, out var quality))
            {
                menuItem.IsCheckable = true;
                menuItem.IsChecked = quality == _recordingQuality;
            }
        }
    }

    private void RecordingQualityItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            return;
        }

        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<RecordingQuality>(tag, out var quality))
        {
            _recordingQuality = quality;
            UpdateRecordingQualityMenuChecks();
            SaveConfig();
        }
    }

    private void ShowRecordingError(string message)
    {
        string fullMessage = $"Recording failed:\n{message}";
        try
        {
            Clipboard.SetText(fullMessage);
        }
        catch
        {
            // Ignore clipboard errors.
        }

        MessageBox.Show(this, $"{fullMessage}\n\n(The message has been copied to your clipboard.)",
            "Recording Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private ImageSource GetRecordingOverlayIcon()
    {
        if (_recordingOverlayIcon != null)
        {
            return _recordingOverlayIcon;
        }

        var group = new DrawingGroup();
        var pen = new Pen(Brushes.White, 1);
        var circle = new GeometryDrawing(Brushes.Red, pen, new EllipseGeometry(new Point(8, 8), 6, 6));
        group.Children.Add(circle);
        group.Freeze();
        _recordingOverlayIcon = new DrawingImage(group);
        _recordingOverlayIcon.Freeze();
        return _recordingOverlayIcon;
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
        int? requested = PromptForInteger("Height", GetReferenceSimulationEngine().Rows, MinRows, MaxRows);
        if (requested.HasValue)
        {
            ApplyDimensions(requested.Value, null);
        }
    }

    private void SetDepth_Click(object sender, RoutedEventArgs e)
    {
        int? requested = PromptForInteger("Depth", GetReferenceSimulationEngine().Depth, 3, 96);
        if (requested.HasValue)
        {
            ApplyDimensions(null, requested.Value);
        }
    }

    private void ApplyDimensions(int? rows, int? depth, double? aspectOverride = null, bool persist = true)
    {
        var referenceEngine = GetReferenceSimulationEngine();
        int nextRows = rows ?? _configuredRows;
        int nextDepth = depth ?? referenceEngine.Depth;
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
        ConfigureSimulationLayerEngines(_configuredRows, _configuredDepth, _currentAspectRatio, randomize: false);
        _configuredRows = GetReferenceSimulationEngine().Rows;
        SnapWindowToAspect(preserveHeight: true);
        _lastCompositeFrame = null;
        RebuildSurface();
        ResetFramePumpCadence(scheduleImmediate: false);
        RenderFrame();
        var resizedEngine = GetReferenceSimulationEngine();
        Logger.Info($"Applied simulation dimensions: {resizedEngine.Columns}x{resizedEngine.Rows} depth {_configuredDepth}.");

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

    private string? PromptForText(string label, string current, int maxLength)
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
            Text = $"Enter {label}",
            Margin = new Thickness(0, 0, 0, 8)
        };

        var input = new TextBox
        {
            Text = current,
            Margin = new Thickness(0, 0, 0, 8),
            MaxLength = Math.Max(1, maxLength)
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

        string? result = null;

        var okButton = new Button
        {
            Content = "OK",
            IsDefault = true,
            Width = 70,
            Margin = new Thickness(0, 0, 8, 0)
        };
        okButton.Click += (_, _) =>
        {
            string value = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                error.Text = "Please enter a name.";
                error.Visibility = Visibility.Visible;
                return;
            }

            result = value;
            dialog.DialogResult = true;
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
        if (PassthroughMenuItem != null)
        {
            PassthroughMenuItem.IsChecked = _passthroughEnabled;
            PassthroughMenuItem.IsEnabled = _sources.Count > 0;
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
        UpdateRgbHueShiftControls();
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
        UpdateRecordingUi();
        if (RecordingQualityMenu != null)
        {
            RecordingQualityMenu.IsEnabled = !_isRecording;
            UpdateRecordingQualityMenuChecks();
        }

        UpdateFramerateMenuChecks();
        UpdateLifeModeMenuChecks();
        UpdateBinningModeMenuChecks();
        UpdateInjectionModeMenuChecks();
        _suppressThresholdControlEvents = true;
        try
        {
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
        finally
        {
            _suppressThresholdControlEvents = false;
        }
        if (AnimationBpmSlider != null && AnimationBpmValueText != null)
        {
            AnimationBpmSlider.Value = _animationBpm;
            AnimationBpmValueText.Text = $"{_animationBpm:F0}";
            AnimationBpmSlider.IsEnabled = !_animationAudioSyncEnabled;
        }
        if (AnimationAudioSyncCheckBox != null)
        {
            AnimationAudioSyncCheckBox.IsChecked = _animationAudioSyncEnabled;
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

        if (AudioReactiveEnabledMenuItem != null)
        {
            AudioReactiveEnabledMenuItem.IsChecked = _audioReactiveEnabled;
        }
        if (AudioReactiveLevelToFpsMenuItem != null)
        {
            AudioReactiveLevelToFpsMenuItem.IsChecked = _audioReactiveLevelToFpsEnabled;
        }
        if (AudioReactiveLevelToLifeOpacityMenuItem != null)
        {
            AudioReactiveLevelToLifeOpacityMenuItem.IsChecked = _audioReactiveLevelToLifeOpacityEnabled;
        }
        if (AudioReactiveLevelSeedMenuItem != null)
        {
            AudioReactiveLevelSeedMenuItem.IsChecked = _audioReactiveLevelSeedEnabled;
        }
        if (AudioReactiveBeatSeedMenuItem != null)
        {
            AudioReactiveBeatSeedMenuItem.IsChecked = _audioReactiveBeatSeedEnabled;
        }
        if (AudioReactiveEnergyGainSlider != null && AudioReactiveEnergyGainText != null)
        {
            AudioReactiveEnergyGainSlider.Value = _audioReactiveEnergyGain;
            AudioReactiveEnergyGainText.Text = $"{_audioReactiveEnergyGain:0.0}x";
        }
        if (AudioInputGainSlider != null && AudioInputGainText != null)
        {
            UpdateAudioInputGainUi();
        }
        if (AudioReactiveLevelSeedMaxSlider != null && AudioReactiveLevelSeedMaxText != null)
        {
            AudioReactiveLevelSeedMaxSlider.Value = _audioReactiveLevelSeedMaxBursts;
            AudioReactiveLevelSeedMaxText.Text = _audioReactiveLevelSeedMaxBursts.ToString(CultureInfo.InvariantCulture);
        }
        if (AudioReactiveFpsBoostSlider != null && AudioReactiveFpsBoostText != null)
        {
            AudioReactiveFpsBoostSlider.Value = _audioReactiveFpsBoost;
            AudioReactiveFpsBoostText.Text = $"+{_audioReactiveFpsBoost * 100:0}%";
        }
        if (AudioReactiveFpsMinSlider != null && AudioReactiveFpsMinText != null)
        {
            AudioReactiveFpsMinSlider.Value = _audioReactiveFpsMinPercent * 100.0;
            AudioReactiveFpsMinText.Text = $"{_audioReactiveFpsMinPercent * 100.0:0}%";
        }
        if (AudioReactiveOpacityMinScalarSlider != null && AudioReactiveOpacityMinScalarText != null)
        {
            AudioReactiveOpacityMinScalarSlider.Value = _audioReactiveLifeOpacityMinScalar * 100.0;
            AudioReactiveOpacityMinScalarText.Text = $"{_audioReactiveLifeOpacityMinScalar * 100.0:0}%";
        }
        if (AudioReactiveSeedsPerBeatSlider != null && AudioReactiveSeedsPerBeatText != null)
        {
            AudioReactiveSeedsPerBeatSlider.Value = _audioReactiveSeedsPerBeat;
            AudioReactiveSeedsPerBeatText.Text = _audioReactiveSeedsPerBeat.ToString(CultureInfo.InvariantCulture);
        }
        if (AudioReactiveSeedCooldownSlider != null && AudioReactiveSeedCooldownText != null)
        {
            AudioReactiveSeedCooldownSlider.Value = _audioReactiveSeedCooldownMs;
            AudioReactiveSeedCooldownText.Text = $"{_audioReactiveSeedCooldownMs:0} ms";
        }
        UpdateAudioReactivePatternChecks();
        UpdateAudioReactiveMenuState();
        UpdatePerformanceMenuState();

        UpdateUpdateMenuItem();
    }

    private void RootContextMenu_OnClosed(object sender, RoutedEventArgs e)
    {
    }

    internal async Task OpenAndCloseRootContextMenuForSmokeAsync(TimeSpan openDuration)
    {
        if (RootContextMenu == null)
        {
            throw new InvalidOperationException("Root context menu is not available.");
        }

        RootContextMenu.IsOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(openDuration);
        RootContextMenu.IsOpen = false;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private async void SourcesMenu_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.Source))
        {
            return;
        }

        await PopulateSourcesMenuAsync();
    }

    private void AudioSourceMenu_OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.Source))
        {
            return;
        }

        PopulateAudioMenu();
    }

    private async void PopulateAudioMenu()
    {
        if (AudioSourceMenu == null) return;
        AudioSourceMenu.Items.Clear();

        var noneItem = new MenuItem
        {
            Header = "None",
            IsCheckable = true,
            IsChecked = string.IsNullOrWhiteSpace(_selectedAudioDeviceId)
        };
        noneItem.Click += (_, _) => ClearAudioDeviceSelection();
        AudioSourceMenu.Items.Add(noneItem);
        AudioSourceMenu.Items.Add(new Separator());

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

        var outputDevices = _cachedAudioDevices
            .Where(d => d.Kind == AudioBeatDetector.AudioDeviceInfo.AudioDeviceKind.Render)
            .ToList();
        var inputDevices = _cachedAudioDevices
            .Where(d => d.Kind == AudioBeatDetector.AudioDeviceInfo.AudioDeviceKind.Capture)
            .ToList();

        if (outputDevices.Count > 0)
        {
            AudioSourceMenu.Items.Add(new MenuItem { Header = "Output Devices", IsEnabled = false });
            foreach (var device in outputDevices)
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

        if (inputDevices.Count > 0)
        {
            if (outputDevices.Count > 0)
            {
                AudioSourceMenu.Items.Add(new Separator());
            }

            AudioSourceMenu.Items.Add(new MenuItem { Header = "Input Devices", IsEnabled = false });
            foreach (var device in inputDevices)
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
    }

    private async void AudioDeviceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AudioBeatDetector.AudioDeviceInfo device }) return;

        if (_selectedAudioDeviceId == device.Id) return;

        _selectedAudioDeviceId = device.Id;
        ApplyAudioInputGainForSelection();
        UpdateAudioInputGainUi();
        await _audioBeatDetector.InitializeAsync(device.Id);
        if (!_audioBeatDetector.IsRunning)
        {
            MessageBox.Show(this,
                $"Could not initialize audio device \"{device.Name}\".\n\nIf this is an output device, loopback capture may not be available on this system/device.",
                "Audio Device Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _selectedAudioDeviceId = null;
            ApplyAudioInputGainForSelection();
            UpdateAudioInputGainUi();
        }
        _lastAudioReactiveBeatCount = _audioBeatDetector.BeatCount;
        _lastAudioReactiveSeedUtc = DateTime.MinValue;
        _smoothedLevelEnergy = 0;
        _smoothedEnergy = 0;
        _fastAudioLevel = 0;
        _audioReactiveLevelSeedBurstsLastStep = 0;
        _audioReactiveBeatSeedBurstsLastStep = 0;
        UpdateAudioReactiveMenuState();
        SaveConfig();
        PopulateAudioMenu();
    }

    private void ClearAudioDeviceSelection()
    {
        if (string.IsNullOrWhiteSpace(_selectedAudioDeviceId))
        {
            return;
        }

        _selectedAudioDeviceId = null;
        _audioBeatDetector.Stop();
        ApplyAudioInputGainForSelection();
        UpdateAudioInputGainUi();
        _lastAudioReactiveBeatCount = 0;
        _lastAudioReactiveSeedUtc = DateTime.MinValue;
        _smoothedLevelEnergy = 0;
        _smoothedEnergy = 0;
        _fastAudioLevel = 0;
        _audioReactiveFpsMultiplier = 1.0;
        _audioReactiveLevelSeedBurstsLastStep = 0;
        _audioReactiveBeatSeedBurstsLastStep = 0;
        UpdateAudioReactiveMenuState();
        SaveConfig();
        PopulateAudioMenu();
    }

    private async Task PopulateSourcesMenuAsync()
    {
        if (SourcesMenu == null)
        {
            return;
        }

        SourcesMenu.Items.Clear();
        SourcesMenu.Items.Add(new MenuItem { Header = "Loading...", IsEnabled = false });

        var windowsTask = Task.Run(() => _windowCapture.EnumerateWindows(_windowHandle));
        var camerasTask = Task.Run(() => _webcamCapture.EnumerateCameras());

        await Task.WhenAll(windowsTask, camerasTask);

        _cachedWindows = windowsTask.Result;
        Logger.Info($"Enumerated windows: count={_cachedWindows.Count}");
        _cachedCameras = camerasTask.Result;
        Logger.Info($"Enumerated webcams: count={_cachedCameras.Count}");

        RebuildSourcesMenu();
    }

    private void RebuildSourcesMenu()
    {
        if (SourcesMenu == null)
        {
            return;
        }

        SourcesMenu.Items.Clear();

        SourcesMenu.Items.Add(BuildAddLayerGroupMenuItem(null));
        SourcesMenu.Items.Add(BuildAddWindowMenuItem(null));
        SourcesMenu.Items.Add(BuildAddWebcamMenuItem(null));
        SourcesMenu.Items.Add(BuildAddFileMenuItem(null));
        SourcesMenu.Items.Add(BuildAddYoutubeMenuItem(null));
        SourcesMenu.Items.Add(BuildAddVideoSequenceMenuItem(null));
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

        NotifyLayerEditorSourcesChanged();
    }

    private void NotifyLayerEditorSourcesChanged()
    {
        if (_suppressLayerEditorRefresh)
        {
            return;
        }

        _layerEditorWindow?.RefreshFromSourcesIfLive();
    }

    private void RunWithoutLayerEditorRefresh(Action action)
    {
        _suppressLayerEditorRefresh = true;
        try
        {
            action();
        }
        finally
        {
            _suppressLayerEditorRefresh = false;
        }
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

    private sealed class AnimationAddTarget
    {
        public AnimationAddTarget(CaptureSource source, AnimationType type, TranslateDirection? translateDirection = null, RotationDirection? rotationDirection = null)
        {
            Source = source;
            Type = type;
            TranslateDirection = translateDirection;
            RotationDirection = rotationDirection;
        }

        public CaptureSource Source { get; }
        public AnimationType Type { get; }
        public TranslateDirection? TranslateDirection { get; }
        public RotationDirection? RotationDirection { get; }
    }

    private sealed class AnimationTarget
    {
        public AnimationTarget(CaptureSource source, LayerAnimation animation)
        {
            Source = source;
            Animation = animation;
        }

        public CaptureSource Source { get; }
        public LayerAnimation Animation { get; }
    }

    private sealed class AnimationLoopTarget
    {
        public AnimationLoopTarget(CaptureSource source, LayerAnimation animation, AnimationLoop loop)
        {
            Source = source;
            Animation = animation;
            Loop = loop;
        }

        public CaptureSource Source { get; }
        public LayerAnimation Animation { get; }
        public AnimationLoop Loop { get; }
    }

    private sealed class AnimationSpeedTarget
    {
        public AnimationSpeedTarget(CaptureSource source, LayerAnimation animation, AnimationSpeed speed)
        {
            Source = source;
            Animation = animation;
            Speed = speed;
        }

        public CaptureSource Source { get; }
        public LayerAnimation Animation { get; }
        public AnimationSpeed Speed { get; }
    }

    private sealed class AnimationTranslateTarget
    {
        public AnimationTranslateTarget(CaptureSource source, LayerAnimation animation, TranslateDirection direction)
        {
            Source = source;
            Animation = animation;
            Direction = direction;
        }

        public CaptureSource Source { get; }
        public LayerAnimation Animation { get; }
        public TranslateDirection Direction { get; }
    }

    private sealed class AnimationRotateTarget
    {
        public AnimationRotateTarget(CaptureSource source, LayerAnimation animation, RotationDirection direction)
        {
            Source = source;
            Animation = animation;
            Direction = direction;
        }

        public CaptureSource Source { get; }
        public LayerAnimation Animation { get; }
        public RotationDirection Direction { get; }
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

    private MenuItem BuildAddVideoSequenceMenuItem(CaptureSource? parentGroup)
    {
        var addSequenceItem = new MenuItem { Header = "Add Video Sequence...", Tag = parentGroup };
        addSequenceItem.Click += AddVideoSequenceMenuItem_Click;
        return addSequenceItem;
    }

    private MenuItem BuildAddYoutubeMenuItem(CaptureSource? parentGroup)
    {
        var addYoutubeItem = new MenuItem { Header = "Add YouTube Source...", Tag = parentGroup };
        addYoutubeItem.Click += AddYoutubeSourceMenuItem_Click;
        return addYoutubeItem;
    }

    private async void AddYoutubeSourceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var menuItem = sender as MenuItem;
        var parentGroup = menuItem?.Tag as CaptureSource;

        var dialog = new TextInputDialog("Add YouTube Source", "Enter YouTube URL:", string.Empty)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText))
        {
            return;
        }

        string url = dialog.InputText.Trim();
        
        // Basic validation
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Please enter a valid URL.", "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var (success, info, error) = await _fileCapture.TryCreateYoutubeSource(url);
            if (!success)
            {
                MessageBox.Show(this, $"Failed to load YouTube video:\n{error}", "YouTube Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var source = CaptureSource.CreateFile(info.Path, info.DisplayName, info.Width, info.Height);
            
            if (parentGroup != null)
            {
                parentGroup.Children.Add(source);
            }
            else
            {
                _sources.Add(source);
            }

            RebuildSourcesMenu();
            NotifyLayerEditorSourcesChanged();
        }
        catch (Exception ex)
        {
             MessageBox.Show(this, $"An unexpected error occurred:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private MenuItem BuildAnimationsMenu(CaptureSource source)
    {
        var animationsMenu = new MenuItem { Header = "Animations" };

        var addZoomItem = new MenuItem
        {
            Header = "Add Zoom In",
            Tag = new AnimationAddTarget(source, AnimationType.ZoomIn)
        };
        addZoomItem.Click += AddAnimationMenuItem_Click;
        animationsMenu.Items.Add(addZoomItem);

        var addTranslateMenu = new MenuItem { Header = "Add Translate" };
        foreach (var direction in Enum.GetValues(typeof(TranslateDirection)).Cast<TranslateDirection>())
        {
            var addTranslateItem = new MenuItem
            {
                Header = direction.ToString(),
                Tag = new AnimationAddTarget(source, AnimationType.Translate, translateDirection: direction)
            };
            addTranslateItem.Click += AddAnimationMenuItem_Click;
            addTranslateMenu.Items.Add(addTranslateItem);
        }
        animationsMenu.Items.Add(addTranslateMenu);

        var addRotateMenu = new MenuItem { Header = "Add Rotate" };
        foreach (var direction in Enum.GetValues(typeof(RotationDirection)).Cast<RotationDirection>())
        {
            var addRotateItem = new MenuItem
            {
                Header = direction.ToString(),
                Tag = new AnimationAddTarget(source, AnimationType.Rotate, rotationDirection: direction)
            };
            addRotateItem.Click += AddAnimationMenuItem_Click;
            addRotateMenu.Items.Add(addRotateItem);
        }
        animationsMenu.Items.Add(addRotateMenu);

        var addDvdItem = new MenuItem
        {
            Header = "Add DVD Bounce",
            Tag = new AnimationAddTarget(source, AnimationType.DvdBounce)
        };
        addDvdItem.Click += AddAnimationMenuItem_Click;
        animationsMenu.Items.Add(addDvdItem);

        var addBeatShakeItem = new MenuItem
        {
            Header = "Add Beat Shake",
            Tag = new AnimationAddTarget(source, AnimationType.BeatShake)
        };
        addBeatShakeItem.Click += AddAnimationMenuItem_Click;
        animationsMenu.Items.Add(addBeatShakeItem);

        var addAudioGranularItem = new MenuItem
        {
            Header = "Add Audio Granular",
            Tag = new AnimationAddTarget(source, AnimationType.AudioGranular)
        };
        addAudioGranularItem.Click += AddAnimationMenuItem_Click;
        animationsMenu.Items.Add(addAudioGranularItem);

        var addFadeItem = new MenuItem
        {
            Header = "Add Fade",
            Tag = new AnimationAddTarget(source, AnimationType.Fade)
        };
        addFadeItem.Click += AddAnimationMenuItem_Click;
        animationsMenu.Items.Add(addFadeItem);

        if (source.Animations.Count == 0)
        {
            return animationsMenu;
        }

        animationsMenu.Items.Add(new Separator());

        for (int i = 0; i < source.Animations.Count; i++)
        {
            var animation = source.Animations[i];
            var animationItem = new MenuItem
            {
                Header = BuildAnimationLabel(animation, i),
                Tag = new AnimationTarget(source, animation)
            };

            var loopMenu = new MenuItem { Header = "Loop" };
            foreach (var loop in Enum.GetValues(typeof(AnimationLoop)).Cast<AnimationLoop>())
            {
                var loopItem = new MenuItem
                {
                    Header = loop == AnimationLoop.PingPong ? "Reverse" : loop.ToString(),
                    IsCheckable = true,
                    IsChecked = animation.Loop == loop,
                    Tag = new AnimationLoopTarget(source, animation, loop)
                };
                loopItem.Click += AnimationLoopItem_Click;
                loopMenu.Items.Add(loopItem);
            }

            var speedMenu = new MenuItem { Header = "Speed" };
            foreach (var speed in Enum.GetValues(typeof(AnimationSpeed)).Cast<AnimationSpeed>())
            {
                var speedItem = new MenuItem
                {
                    Header = DescribeSpeed(speed),
                    IsCheckable = true,
                    IsChecked = animation.Speed == speed,
                    Tag = new AnimationSpeedTarget(source, animation, speed)
                };
                speedItem.Click += AnimationSpeedItem_Click;
                speedMenu.Items.Add(speedItem);
            }

            animationItem.Items.Add(loopMenu);
            animationItem.Items.Add(speedMenu);

            var cycleItem = new MenuItem
            {
                Header = "Cycle Length",
                StaysOpenOnClick = true
            };
            double cycleMax = animation.Type == AnimationType.DvdBounce ? 128 : 4096;
            double cycleLargeChange = animation.Type == AnimationType.DvdBounce ? 8 : 32;
            var cycleValueItem = new MenuItem
            {
                Header = DescribeCycleBeats(animation.BeatsPerCycle),
                IsEnabled = false
            };
            var cycleSlider = new Slider
            {
                Minimum = 1,
                Maximum = cycleMax,
                Value = Math.Clamp(animation.BeatsPerCycle, 1, cycleMax),
                Width = 140,
                SmallChange = 1,
                LargeChange = cycleLargeChange,
                Margin = new Thickness(12, 4, 12, 8)
            };
            cycleSlider.ValueChanged += (_, args) =>
            {
                animation.BeatsPerCycle = Math.Clamp(args.NewValue, 1, cycleMax);
                cycleValueItem.Header = DescribeCycleBeats(animation.BeatsPerCycle);
                SaveConfig();
            };
            cycleItem.Items.Add(cycleValueItem);
            cycleItem.Items.Add(cycleSlider);
            animationItem.Items.Add(cycleItem);

            if (animation.Type == AnimationType.Translate)
            {
                var directionMenu = new MenuItem { Header = "Direction" };
                foreach (var direction in Enum.GetValues(typeof(TranslateDirection)).Cast<TranslateDirection>())
                {
                    var directionItem = new MenuItem
                    {
                        Header = direction.ToString(),
                        IsCheckable = true,
                        IsChecked = animation.TranslateDirection == direction,
                        Tag = new AnimationTranslateTarget(source, animation, direction)
                    };
                    directionItem.Click += AnimationTranslateDirectionItem_Click;
                    directionMenu.Items.Add(directionItem);
                }
                animationItem.Items.Add(directionMenu);
            }
            else if (animation.Type == AnimationType.Rotate)
            {
                var directionMenu = new MenuItem { Header = "Direction" };
                foreach (var direction in Enum.GetValues(typeof(RotationDirection)).Cast<RotationDirection>())
                {
                    var directionItem = new MenuItem
                    {
                        Header = direction.ToString(),
                        IsCheckable = true,
                        IsChecked = animation.RotationDirection == direction,
                        Tag = new AnimationRotateTarget(source, animation, direction)
                    };
                    directionItem.Click += AnimationRotateDirectionItem_Click;
                    directionMenu.Items.Add(directionItem);
                }
                animationItem.Items.Add(directionMenu);
            }
            else if (animation.Type == AnimationType.DvdBounce)
            {
                var sizeItem = new MenuItem
                {
                    Header = "Size",
                    StaysOpenOnClick = true
                };
                var sizeValueItem = new MenuItem
                {
                    Header = DescribeScale(animation.DvdScale),
                    IsEnabled = false
                };
                var sizeSlider = new Slider
                {
                    Minimum = 0.05,
                    Maximum = 0.5,
                    Value = Math.Clamp(animation.DvdScale, 0.05, 0.5),
                    Width = 140,
                    SmallChange = 0.01,
                    LargeChange = 0.05,
                    Margin = new Thickness(12, 4, 12, 8)
                };
                sizeSlider.ValueChanged += (_, args) =>
                {
                    animation.DvdScale = Math.Clamp(args.NewValue, 0.05, 0.5);
                    sizeValueItem.Header = DescribeScale(animation.DvdScale);
                    RebuildSourcesMenu();
                    SaveConfig();
                };
                sizeItem.Items.Add(sizeValueItem);
                sizeItem.Items.Add(sizeSlider);
                animationItem.Items.Add(sizeItem);
            }
            else if (animation.Type == AnimationType.BeatShake || animation.Type == AnimationType.AudioGranular)
            {
                var intensityItem = new MenuItem
                {
                    Header = "Intensity",
                    StaysOpenOnClick = true
                };
                var intensityValueItem = new MenuItem
                {
                    Header = DescribeShakeIntensity(animation.BeatShakeIntensity),
                    IsEnabled = false
                };
                
                double maxIntensity = GetAnimationIntensityMax(animation.Type);
                double intensityLargeChange = animation.Type == AnimationType.AudioGranular ? 0.5 : 0.2;
                
                var intensitySlider = new Slider
                {
                    Minimum = 0,
                    Maximum = maxIntensity,
                    Value = ClampAnimationIntensity(animation.Type, animation.BeatShakeIntensity),
                    Width = 140,
                    SmallChange = 0.05,
                    LargeChange = intensityLargeChange,
                    Margin = new Thickness(12, 4, 12, 8)
                };
                intensitySlider.ValueChanged += (_, args) =>
                {
                    animation.BeatShakeIntensity = ClampAnimationIntensity(animation.Type, args.NewValue);
                    intensityValueItem.Header = DescribeShakeIntensity(animation.BeatShakeIntensity);
                    SaveConfig();
                };
                intensityItem.Items.Add(intensityValueItem);
                intensityItem.Items.Add(intensitySlider);
                animationItem.Items.Add(intensityItem);

                if (animation.Type == AnimationType.AudioGranular)
                {
                    var eqMenu = new MenuItem { Header = "3-Band EQ" };
                    eqMenu.Items.Add(BuildAudioGranularEqBandMenuItem(
                        "Low",
                        () => animation.AudioGranularLowGain,
                        value => animation.AudioGranularLowGain = value));
                    eqMenu.Items.Add(BuildAudioGranularEqBandMenuItem(
                        "Mid",
                        () => animation.AudioGranularMidGain,
                        value => animation.AudioGranularMidGain = value));
                    eqMenu.Items.Add(BuildAudioGranularEqBandMenuItem(
                        "High",
                        () => animation.AudioGranularHighGain,
                        value => animation.AudioGranularHighGain = value));
                    animationItem.Items.Add(eqMenu);
                }
            }

            var removeItem = new MenuItem
            {
                Header = "Remove",
                Tag = new AnimationTarget(source, animation)
            };
            removeItem.Click += RemoveAnimationMenuItem_Click;
            animationItem.Items.Add(new Separator());
            animationItem.Items.Add(removeItem);

            animationsMenu.Items.Add(animationItem);
        }

        return animationsMenu;
    }

    private MenuItem BuildAudioGranularEqBandMenuItem(string label, Func<double> getter, Action<double> setter)
    {
        var bandItem = new MenuItem
        {
            Header = label,
            StaysOpenOnClick = true
        };

        var valueItem = new MenuItem
        {
            Header = DescribeAudioGranularEqGain(getter()),
            IsEnabled = false
        };

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = MaxAudioGranularEqBandGain,
            Value = Math.Clamp(getter(), 0, MaxAudioGranularEqBandGain),
            Width = 140,
            SmallChange = 0.05,
            LargeChange = 0.25,
            Margin = new Thickness(12, 4, 12, 8)
        };
        slider.ValueChanged += (_, args) =>
        {
            double next = Math.Clamp(args.NewValue, 0, MaxAudioGranularEqBandGain);
            setter(next);
            valueItem.Header = DescribeAudioGranularEqGain(next);
            SaveConfig();
        };

        bandItem.Items.Add(valueItem);
        bandItem.Items.Add(slider);
        return bandItem;
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
            sourceItem.Items.Add(BuildAddYoutubeMenuItem(source));
            sourceItem.Items.Add(BuildAddVideoSequenceMenuItem(source));
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

        var animationsMenu = BuildAnimationsMenu(source);

        MenuItem? restartVideoItem = null;
        bool isVideoLayer = source.Type == CaptureSource.SourceType.VideoSequence ||
                            (source.Type == CaptureSource.SourceType.File &&
                             !string.IsNullOrWhiteSpace(source.FilePath) &&
                             IsVideoFileSourcePath(source.FilePath));
        MenuItem? videoAudioItem = null;
        MenuItem? videoAudioVolumeItem = null;
        MenuItem? videoPlaybackItem = null;
        MenuItem? videoSeekItem = null;
        if (isVideoLayer)
        {
            restartVideoItem = new MenuItem
            {
                Header = source.Type == CaptureSource.SourceType.VideoSequence ? "Restart Sequence" : "Restart Video"
            };
            restartVideoItem.Click += (_, _) => RestartVideoSource(source);

            bool hasPlaybackState = TryGetSourceVideoPlaybackState(source, out var playbackState);
            if (hasPlaybackState)
            {
                source.VideoPlaybackPaused = playbackState.IsPaused;
            }
            videoPlaybackItem = new MenuItem
            {
                Header = source.VideoPlaybackPaused ? "Play" : "Pause"
            };
            videoPlaybackItem.Click += (_, _) =>
            {
                bool shouldPause = !source.VideoPlaybackPaused;
                if (SetSourceVideoPlaybackPaused(source, shouldPause))
                {
                    source.VideoPlaybackPaused = shouldPause;
                    videoPlaybackItem.Header = shouldPause ? "Play" : "Pause";
                    NotifyLayerEditorSourcesChanged();
                }
            };

            videoSeekItem = new MenuItem
            {
                Header = "Scrub",
                StaysOpenOnClick = true,
                IsEnabled = hasPlaybackState && playbackState.IsSeekable
            };
            var videoSeekValueItem = new MenuItem
            {
                Header = hasPlaybackState
                    ? $"{FormatPlaybackTime(playbackState.PositionSeconds)} / {FormatPlaybackTime(playbackState.DurationSeconds)}"
                    : "Unavailable",
                IsEnabled = false
            };
            var videoSeekSlider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = hasPlaybackState ? playbackState.NormalizedPosition : 0,
                Width = 160,
                SmallChange = 0.01,
                LargeChange = 0.05,
                IsEnabled = hasPlaybackState && playbackState.IsSeekable,
                Margin = new Thickness(12, 4, 12, 8)
            };
            videoSeekSlider.ValueChanged += (_, args) =>
            {
                if (!videoSeekSlider.IsMouseCaptureWithin)
                {
                    return;
                }

                if (!SeekSourceVideo(source, args.NewValue))
                {
                    return;
                }

                if (TryGetSourceVideoPlaybackState(source, out var updatedState))
                {
                    source.VideoPlaybackPaused = updatedState.IsPaused;
                    videoSeekValueItem.Header =
                        $"{FormatPlaybackTime(updatedState.PositionSeconds)} / {FormatPlaybackTime(updatedState.DurationSeconds)}";
                }
                NotifyLayerEditorSourcesChanged();
            };
            videoSeekItem.Items.Add(videoSeekValueItem);
            videoSeekItem.Items.Add(videoSeekSlider);

            videoAudioItem = new MenuItem
            {
                Header = "Play Audio",
                IsCheckable = true,
                IsChecked = source.VideoAudioEnabled
            };
            videoAudioItem.Click += (_, _) =>
            {
                source.VideoAudioEnabled = !source.VideoAudioEnabled;
                ApplySourceVideoAudioState(source);
                Logger.Info($"Video audio toggled for {source.DisplayName}: {source.VideoAudioEnabled}");
                SaveConfig();
                NotifyLayerEditorSourcesChanged();
            };

            videoAudioVolumeItem = new MenuItem
            {
                Header = "Audio Volume",
                StaysOpenOnClick = true
            };
            var videoAudioVolumeValueItem = new MenuItem
            {
                Header = $"{Math.Clamp(source.VideoAudioVolume, 0, 1):P0}",
                IsEnabled = false
            };
            var videoAudioVolumeSlider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = Math.Clamp(source.VideoAudioVolume, 0, 1),
                Width = 140,
                SmallChange = 0.05,
                LargeChange = 0.1,
                Margin = new Thickness(12, 4, 12, 8)
            };
            videoAudioVolumeSlider.ValueChanged += (_, args) =>
            {
                source.VideoAudioVolume = Math.Clamp(args.NewValue, 0, 1);
                videoAudioVolumeValueItem.Header = $"{source.VideoAudioVolume:P0}";
                ApplySourceVideoAudioState(source);
                SaveConfig();
                NotifyLayerEditorSourcesChanged();
            };
            videoAudioVolumeItem.Items.Add(videoAudioVolumeValueItem);
            videoAudioVolumeItem.Items.Add(videoAudioVolumeSlider);
        }

        MenuItem? renameItem = null;
        if (source.Type == CaptureSource.SourceType.Group)
        {
            renameItem = new MenuItem { Header = "Rename Group..." };
            renameItem.Click += (_, _) => RenameLayerGroup(source);
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
            NotifyLayerEditorSourcesChanged();
        };

        var keyMenu = new MenuItem { Header = "Keying (Normal)" };
        var keyEnabledItem = new MenuItem
        {
            Header = "Enable Keying",
            IsCheckable = true,
            IsChecked = source.KeyEnabled
        };
        keyEnabledItem.Click += (_, _) =>
        {
            source.KeyEnabled = !source.KeyEnabled;
            Logger.Info($"Keying toggled for {source.DisplayName}: {source.KeyEnabled}");
            RenderFrame();
            SaveConfig();
            NotifyLayerEditorSourcesChanged();
        };

        var keyColorItem = new MenuItem { Header = "Key Color..." };
        keyColorItem.Click += (_, _) =>
        {
            string defaultValue = FormatHexColor(source.KeyColorR, source.KeyColorG, source.KeyColorB);
            var dialog = new TextInputDialog("Key Color", "Enter hex color (#RRGGBB) or R,G,B:", defaultValue)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true)
            {
                if (TryParseHexColor(dialog.InputText, out var keyR, out var keyG, out var keyB))
                {
                    source.KeyColorR = keyR;
                    source.KeyColorG = keyG;
                    source.KeyColorB = keyB;
                    RenderFrame();
                    SaveConfig();
                    NotifyLayerEditorSourcesChanged();
                }
                else
                {
                    MessageBox.Show(this, "Invalid color value. Use #RRGGBB or R,G,B.", "Key Color",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        };

        var keyRangeItem = new MenuItem
        {
            Header = "Key Range",
            StaysOpenOnClick = true
        };
        var keyRangeValueItem = new MenuItem
        {
            Header = $"{Math.Clamp(source.KeyTolerance, 0, 1):P0}",
            IsEnabled = false
        };
        var keyRangeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = Math.Clamp(source.KeyTolerance, 0, 1),
            Width = 140,
            SmallChange = 0.01,
            LargeChange = 0.05,
            Margin = new Thickness(12, 4, 12, 8)
        };
        keyRangeSlider.ValueChanged += (_, args) =>
        {
            source.KeyTolerance = Math.Clamp(args.NewValue, 0, 1);
            Logger.Info($"Key range changed: {source.DisplayName} = {source.KeyTolerance:F2}");
            keyRangeValueItem.Header = $"{source.KeyTolerance:P0}";
            RenderFrame();
            SaveConfig();
            NotifyLayerEditorSourcesChanged();
        };
        keyRangeItem.Items.Add(keyRangeValueItem);
        keyRangeItem.Items.Add(keyRangeSlider);

        keyMenu.Items.Add(keyEnabledItem);
        keyMenu.Items.Add(keyColorItem);
        keyMenu.Items.Add(keyRangeItem);

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
            NotifyLayerEditorSourcesChanged();
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
        sourceItem.Items.Add(animationsMenu);
        if (restartVideoItem != null)
        {
            sourceItem.Items.Add(restartVideoItem);
        }
        if (videoPlaybackItem != null)
        {
            sourceItem.Items.Add(videoPlaybackItem);
        }
        if (videoSeekItem != null)
        {
            sourceItem.Items.Add(videoSeekItem);
        }
        if (videoAudioItem != null)
        {
            sourceItem.Items.Add(videoAudioItem);
        }
        if (videoAudioVolumeItem != null)
        {
            sourceItem.Items.Add(videoAudioVolumeItem);
        }
        if (renameItem != null)
        {
            sourceItem.Items.Add(renameItem);
        }
        sourceItem.Items.Add(primaryItem);
        sourceItem.Items.Add(moveUpItem);
        sourceItem.Items.Add(moveDownItem);
        sourceItem.Items.Add(mirrorItem);
        sourceItem.Items.Add(keyMenu);
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
            CaptureSource.SourceType.VideoSequence => $"{prefix}Video Sequence: {source.DisplayName}",
            CaptureSource.SourceType.Group => $"{prefix}Group: {source.DisplayName}",
            _ => $"{prefix}{source.DisplayName}"
        };
    }

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

    private void AddVideoSequenceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CaptureSource? parentGroup = null;
        if (sender is MenuItem { Tag: CaptureSource group })
        {
            parentGroup = group;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select Video Sequence",
            Filter = "Video Files|*.mp4;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg|All Files|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddVideoSequenceSource(dialog.FileNames, parentGroup?.Children ?? _sources);
        }
    }

    private void SourceBlendModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Header: string header, Tag: CaptureSource source })
        {
            return;
        }

        if (TryParseBlendMode(header, source.BlendMode, out var mode))
        {
            source.BlendMode = mode;
            RebuildSourcesMenu();
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
            RebuildSourcesMenu();
            RenderFrame();
            SaveConfig();
        }
    }

    private static string BuildAnimationLabel(LayerAnimation animation, int index)
    {
        string prefix = $"{index + 1}. ";
        return animation.Type switch
        {
            AnimationType.ZoomIn => $"{prefix}Zoom In ({DescribeLoop(animation.Loop)}, {DescribeSpeed(animation.Speed)})",
            AnimationType.Translate => $"{prefix}Translate {animation.TranslateDirection} ({DescribeLoop(animation.Loop)}, {DescribeSpeed(animation.Speed)})",
            AnimationType.Rotate => $"{prefix}Rotate {animation.RotationDirection} ({DescribeLoop(animation.Loop)}, {DescribeSpeed(animation.Speed)})",
            AnimationType.DvdBounce => $"{prefix}DVD Bounce ({DescribeLoop(animation.Loop)}, {DescribeSpeed(animation.Speed)}, {DescribeScale(animation.DvdScale)})",
            AnimationType.BeatShake => $"{prefix}Beat Shake ({DescribeLoop(animation.Loop)}, {DescribeSpeed(animation.Speed)}, {DescribeShakeIntensity(animation.BeatShakeIntensity)})",
            AnimationType.AudioGranular => $"{prefix}Audio Granular ({DescribeLoop(animation.Loop)}, {DescribeSpeed(animation.Speed)}, {DescribeShakeIntensity(animation.BeatShakeIntensity)})",
            AnimationType.Fade => $"{prefix}Fade ({DescribeLoop(animation.Loop)}, {DescribeSpeed(animation.Speed)})",
            _ => $"{prefix}Animation"
        };
    }

    private static string DescribeLoop(AnimationLoop loop) => loop == AnimationLoop.PingPong ? "Reverse" : "Forward";

    private static string DescribeSpeed(AnimationSpeed speed) => speed switch
    {
        AnimationSpeed.Eighth => "1/8x",
        AnimationSpeed.Quarter => "1/4x",
        AnimationSpeed.Half => "1/2x",
        AnimationSpeed.Double => "2x",
        AnimationSpeed.Quadruple => "4x",
        AnimationSpeed.Octuple => "8x",
        _ => "1x"
    };

    private static string DescribeScale(double value) => $"{Math.Clamp(value, 0.01, 1):P0}";

    private static string DescribeShakeIntensity(double value) => $"{Math.Clamp(value, 0, MaxAudioGranularIntensity):P0}";

    private static string DescribeAudioGranularEqGain(double value) => $"{Math.Clamp(value, 0, MaxAudioGranularEqBandGain):P0}";

    private static double GetAnimationIntensityMax(AnimationType type) =>
        type == AnimationType.AudioGranular ? MaxAudioGranularIntensity : MaxBeatShakeIntensity;

    private static double ClampAnimationIntensity(AnimationType type, double value) =>
        Math.Clamp(value, 0, GetAnimationIntensityMax(type));

    private string DescribeCycleBeats(double beats)
    {
        double clamped = Math.Clamp(beats, 1, 4096);
        double bpm = _animationBpm > 0 ? _animationBpm : DefaultAnimationBpm;
        double seconds = clamped * 60.0 / Math.Max(1.0, bpm);
        var duration = TimeSpan.FromSeconds(seconds);
        string formatted = duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");
        return $"{clamped:0} beats (~{formatted})";
    }

    private static double GetSpeedMultiplier(AnimationSpeed speed) => speed switch
    {
        AnimationSpeed.Eighth => 0.125,
        AnimationSpeed.Quarter => 0.25,
        AnimationSpeed.Half => 0.5,
        AnimationSpeed.Double => 2.0,
        AnimationSpeed.Quadruple => 4.0,
        AnimationSpeed.Octuple => 8.0,
        _ => 1.0
    };

    private bool TryGetAnimationBeatTiming(double timeSeconds, out double beatDuration, out double beatsElapsed, out bool beatAligned)
    {
        double bpm = _animationBpm > 0 ? _animationBpm : DefaultAnimationBpm;
        bool audioRequested = _animationAudioSyncEnabled && !string.IsNullOrWhiteSpace(_selectedAudioDeviceId);
        double effectiveBpm = bpm;
        if (audioRequested)
        {
            double detectedBpm = _audioBeatDetector.CurrentBpm;
            if (!double.IsNaN(detectedBpm) && !double.IsInfinity(detectedBpm) && detectedBpm > 0)
            {
                effectiveBpm = detectedBpm;
            }
        }

        effectiveBpm = Math.Clamp(effectiveBpm, 10, 300);
        beatDuration = 60.0 / effectiveBpm;
        if (beatDuration <= 0.000001)
        {
            beatsElapsed = 0;
            beatAligned = false;
            return false;
        }

        beatAligned = audioRequested &&
                      _audioBeatDetector.LastBeatTime != DateTime.MinValue &&
                      _audioBeatDetector.BeatCount > 0;

        if (beatAligned)
        {
            double timeSinceBeat = (DateTime.UtcNow - _audioBeatDetector.LastBeatTime).TotalSeconds;
            if (timeSinceBeat < 0)
            {
                timeSinceBeat = 0;
            }

            long beatIndex = Math.Max(0, _audioBeatDetector.BeatCount - 1);
            beatsElapsed = beatIndex + (timeSinceBeat / beatDuration);
            return true;
        }

        beatsElapsed = timeSeconds / beatDuration;
        return true;
    }

    private void AddAnimationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AnimationAddTarget target })
        {
            return;
        }

        var animation = new LayerAnimation
        {
            Type = target.Type,
            Loop = target.Type == AnimationType.DvdBounce ? AnimationLoop.PingPong : AnimationLoop.Forward,
            Speed = AnimationSpeed.Normal
        };

        if (target.TranslateDirection.HasValue)
        {
            animation.TranslateDirection = target.TranslateDirection.Value;
        }
        if (target.RotationDirection.HasValue)
        {
            animation.RotationDirection = target.RotationDirection.Value;
        }

        target.Source.Animations.Add(animation);
        Logger.Info($"Animation added: {animation.Type} ({target.Source.DisplayName})");
        RebuildSourcesMenu();
        SaveConfig();
    }

    private void AnimationLoopItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AnimationLoopTarget target })
        {
            return;
        }

        target.Animation.Loop = target.Loop;
        RebuildSourcesMenu();
        SaveConfig();
    }

    private void AnimationSpeedItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AnimationSpeedTarget target })
        {
            return;
        }

        target.Animation.Speed = target.Speed;
        RebuildSourcesMenu();
        SaveConfig();
    }

    private void AnimationTranslateDirectionItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AnimationTranslateTarget target })
        {
            return;
        }

        target.Animation.TranslateDirection = target.Direction;
        RebuildSourcesMenu();
        SaveConfig();
    }

    private void AnimationRotateDirectionItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AnimationRotateTarget target })
        {
            return;
        }

        target.Animation.RotationDirection = target.Direction;
        RebuildSourcesMenu();
        SaveConfig();
    }

    private void RemoveAnimationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AnimationTarget target })
        {
            return;
        }

        if (target.Source.Animations.Remove(target.Animation))
        {
            Logger.Info($"Animation removed: {target.Source.DisplayName}");
            RebuildSourcesMenu();
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
        NotifyLayerEditorSourcesChanged();
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
        NotifyLayerEditorSourcesChanged();
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
        NotifyLayerEditorSourcesChanged();
    }

    private void AddVideoSequenceSource(IReadOnlyList<string> paths, List<CaptureSource> targetList)
    {
        if (!_fileCapture.TryCreateVideoSequence(paths, out var sequence, out var error))
        {
            string message = error ?? "Unsupported video sequence.";
            MessageBox.Show(this, $"Failed to load video sequence:\n{message}", "Video Sequence Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            Logger.Warn($"Failed to add video sequence. {message}");
            return;
        }

        var existing = FindSource(s => s.Type == CaptureSource.SourceType.VideoSequence &&
            s.HasSameVideoSequence(sequence!.Paths));
        if (existing != null)
        {
            sequence!.Dispose();
            Logger.Info("Video sequence already active.");
        }
        else
        {
            targetList.Add(CaptureSource.CreateVideoSequence(sequence!));
            Logger.Info($"Inserted new video sequence (appended): {sequence!.DisplayName}");
        }

        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
        NotifyLayerEditorSourcesChanged();
    }

    private void AddLayerGroup(List<CaptureSource> targetList)
    {
        targetList.Add(CaptureSource.CreateGroup());
        Logger.Info("Inserted new layer group.");
        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
        NotifyLayerEditorSourcesChanged();
    }

    private void AddSimulationGroup(List<CaptureSource> targetList)
    {
        var source = CaptureSource.CreateSimulationGroup();
        targetList.Add(source);
        ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
        Logger.Info("Inserted new simulation group.");
        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
        NotifyLayerEditorSourcesChanged();
    }

    private void RenameLayerGroup(CaptureSource source)
    {
        if (source.Type != CaptureSource.SourceType.Group)
        {
            return;
        }

        string? updated = PromptForText("Group Name", source.DisplayName, 60);
        if (string.IsNullOrWhiteSpace(updated))
        {
            return;
        }

        if (string.Equals(updated, source.DisplayName, StringComparison.Ordinal))
        {
            return;
        }

        source.SetDisplayName(updated);
        Logger.Info($"Layer group renamed: {updated}");
        RebuildSourcesMenu();
        SaveConfig();
    }

    private void RestartVideoSource(CaptureSource source)
    {
        bool restarted = false;
        if (source.Type == CaptureSource.SourceType.VideoSequence && source.VideoSequence != null)
        {
            source.VideoSequence.Restart();
            ApplySourceVideoAudioState(source);
            restarted = true;
        }
        else if (source.Type == CaptureSource.SourceType.File &&
                 !string.IsNullOrWhiteSpace(source.FilePath) &&
                 IsVideoFileSourcePath(source.FilePath))
        {
            restarted = _fileCapture.RestartVideo(source.FilePath);
            ApplySourceVideoAudioState(source);
        }

        if (!restarted)
        {
            Logger.Warn($"Restart video ignored (not ready): {source.DisplayName}");
            return;
        }

        source.MissedFrames = 0;
        source.FirstFrameReceived = false;
        source.HasError = false;
        source.AddedUtc = DateTime.UtcNow;
        source.VideoPlaybackPaused = false;
        Logger.Info($"Restarted video source: {source.DisplayName}");
        RenderFrame();
    }

    private static bool IsVideoFileSourcePath(string path) =>
        FileCaptureService.IsVideoPath(path) || path.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase);

    private bool IsVideoSource(CaptureSource source) =>
        source.Type == CaptureSource.SourceType.VideoSequence ||
        (source.Type == CaptureSource.SourceType.File &&
         !string.IsNullOrWhiteSpace(source.FilePath) &&
         IsVideoFileSourcePath(source.FilePath));

    private void ApplySourceVideoAudioState(CaptureSource source)
    {
        if (!IsVideoSource(source))
        {
            return;
        }

        if (source.Type == CaptureSource.SourceType.VideoSequence)
        {
            source.VideoSequence?.SetAudioMaster(_sourceAudioMasterEnabled, _sourceAudioMasterVolume);
            source.VideoSequence?.SetAudioVolume(source.VideoAudioVolume);
            source.VideoSequence?.SetAudioEnabled(source.VideoAudioEnabled);
            return;
        }

        if (!string.IsNullOrWhiteSpace(source.FilePath))
        {
            _fileCapture.SetVideoAudioVolume(source.FilePath, source.VideoAudioVolume);
            if (!_fileCapture.SetVideoAudioEnabled(source.FilePath, source.VideoAudioEnabled))
            {
                Logger.Warn($"Failed to apply video audio state for {source.DisplayName} ({source.FilePath}).");
            }
        }
    }

    private void ApplyMasterVideoAudioState()
    {
        _fileCapture.SetMasterVideoAudioEnabled(_sourceAudioMasterEnabled);
        _fileCapture.SetMasterVideoAudioVolume(_sourceAudioMasterVolume);

        foreach (var source in EnumerateSources(_sources))
        {
            if (source.Type == CaptureSource.SourceType.VideoSequence)
            {
                source.VideoSequence?.SetAudioMaster(_sourceAudioMasterEnabled, _sourceAudioMasterVolume);
            }
        }
    }

    private bool TryGetSourceVideoPlaybackState(CaptureSource source, out FileCaptureService.VideoPlaybackState playbackState)
    {
        playbackState = default;
        if (!IsVideoSource(source))
        {
            return false;
        }

        if (source.Type == CaptureSource.SourceType.VideoSequence)
        {
            return source.VideoSequence?.TryGetPlaybackState(out playbackState) == true;
        }

        if (!string.IsNullOrWhiteSpace(source.FilePath))
        {
            return _fileCapture.TryGetVideoPlaybackState(source.FilePath, out playbackState);
        }

        return false;
    }

    private static bool TryConsumeStartupRecoveryFlag()
    {
        if (App.IsDiagnosticTestMode)
        {
            return false;
        }

        try
        {
            if (!File.Exists(StartupRecoveryPath))
            {
                return false;
            }

            File.Delete(StartupRecoveryPath);
            Logger.Warn("Startup recovery triggered after previous incomplete launch.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to inspect startup recovery flag. {ex.Message}");
            return false;
        }
    }

    private static void MarkStartupPending()
    {
        if (App.IsDiagnosticTestMode)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(StartupRecoveryPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(StartupRecoveryPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to write startup recovery flag. {ex.Message}");
        }
    }

    private static void MarkStartupComplete()
    {
        if (App.IsDiagnosticTestMode)
        {
            return;
        }

        try
        {
            if (File.Exists(StartupRecoveryPath))
            {
                File.Delete(StartupRecoveryPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to clear startup recovery flag. {ex.Message}");
        }
    }

    private bool SetSourceVideoPlaybackPaused(CaptureSource source, bool paused)
    {
        if (!IsVideoSource(source))
        {
            return false;
        }

        bool applied;
        if (source.Type == CaptureSource.SourceType.VideoSequence)
        {
            if (source.VideoSequence == null)
            {
                return false;
            }

            source.VideoSequence.SetPlaybackPaused(paused);
            applied = true;
        }
        else if (!string.IsNullOrWhiteSpace(source.FilePath))
        {
            applied = _fileCapture.SetVideoPaused(source.FilePath, paused);
        }
        else
        {
            return false;
        }

        if (applied)
        {
            source.VideoPlaybackPaused = paused;
            RenderFrame();
        }

        return applied;
    }

    private bool SeekSourceVideo(CaptureSource source, double normalizedPosition)
    {
        if (!IsVideoSource(source))
        {
            return false;
        }

        double clamped = Math.Clamp(normalizedPosition, 0, 1);
        bool applied;
        if (source.Type == CaptureSource.SourceType.VideoSequence)
        {
            if (source.VideoSequence == null)
            {
                return false;
            }

            source.VideoSequence.SeekNormalized(clamped);
            applied = true;
        }
        else if (!string.IsNullOrWhiteSpace(source.FilePath))
        {
            applied = _fileCapture.SeekVideo(source.FilePath, clamped);
        }
        else
        {
            return false;
        }

        if (applied)
        {
            RenderFrame();
        }

        return applied;
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

    private CaptureSource? FindSourceById(Guid id) =>
        FindSource(source => source.Id == id);

    private void ApplySimulationLayersFromSourceStack(bool fallbackToDefault)
    {
        var specs = new List<SimulationLayerSpec>();
        foreach (var source in EnumerateSources(_sources))
        {
            if (source.Type != CaptureSource.SourceType.SimGroup)
            {
                continue;
            }

            specs.Add(new SimulationLayerSpec
            {
                Id = source.Id,
                Kind = LayerEditorSimulationItemKind.Group,
                Name = source.DisplayName,
                Enabled = source.Enabled,
                Children = source.SimulationLayers
                    .Select(CloneSimulationLayerSpec)
                    .ToList()
            });
        }

        if (specs.Count == 0)
        {
            if (!fallbackToDefault)
            {
                ClearSimulationLayers();
                return;
            }

            specs = BuildDefaultSimulationLayerSpecs();
        }

        ApplySimulationLayerSpecs(specs);
    }

    private void ClearSimulationLayers()
    {
        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            RetireSimulationEngine(layer.Engine);
        }

        _simulationLayers.Clear();
    }

    private void RetireSimulationEngine(ISimulationBackend? backend)
    {
        if (backend == null || ReferenceEquals(backend, _engine))
        {
            return;
        }

        if (!_retiredSimulationBackends.Contains(backend))
        {
            _retiredSimulationBackends.Add(backend);
        }
    }

    private static SimulationLayerSpec CloneSimulationLayerSpec(SimulationLayerSpec layer)
    {
        return new SimulationLayerSpec
        {
            Id = layer.Id,
            Kind = layer.Kind,
            LayerType = layer.LayerType,
            Name = layer.Name,
            Enabled = layer.Enabled,
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
            ReactiveMappings = layer.ReactiveMappings
                .Select(mapping => new SimulationReactiveMapping
                {
                    Id = mapping.Id,
                    Input = mapping.Input,
                    Output = mapping.Output,
                    Amount = mapping.Amount,
                    ThresholdMin = mapping.ThresholdMin,
                    ThresholdMax = mapping.ThresholdMax
                })
                .ToList(),
            ThresholdMin = layer.ThresholdMin,
            ThresholdMax = layer.ThresholdMax,
            InvertThreshold = layer.InvertThreshold,
            PixelSortCellWidth = layer.PixelSortCellWidth,
            PixelSortCellHeight = layer.PixelSortCellHeight,
            Children = layer.Children.Select(CloneSimulationLayerSpec).ToList()
        };
    }

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

    internal IReadOnlyList<WindowHandleInfo> GetAvailableWindows() =>
        _windowCapture.EnumerateWindows(_windowHandle);

    internal IReadOnlyList<WebcamCaptureService.CameraInfo> GetAvailableWebcams() =>
        _webcamCapture.EnumerateCameras();

    internal void AddLayerGroupFromEditor(Guid? parentId)
    {
        var targetList = ResolveTargetList(parentId);
        if (targetList == null)
        {
            return;
        }

        RunWithoutLayerEditorRefresh(() => AddLayerGroup(targetList));
    }

    internal void AddSimulationGroupFromEditor(Guid? parentId)
    {
        var targetList = ResolveTargetList(parentId);
        if (targetList == null)
        {
            return;
        }

        RunWithoutLayerEditorRefresh(() => AddSimulationGroup(targetList));
    }

    internal void AddWindowSourceFromEditor(WindowHandleInfo info, Guid? parentId)
    {
        var targetList = ResolveTargetList(parentId);
        if (targetList == null)
        {
            return;
        }

        RunWithoutLayerEditorRefresh(() => AddOrPromoteWindowSource(info, targetList));
    }

    internal void AddWebcamSourceFromEditor(WebcamCaptureService.CameraInfo camera, Guid? parentId)
    {
        var targetList = ResolveTargetList(parentId);
        if (targetList == null)
        {
            return;
        }

        RunWithoutLayerEditorRefresh(() => AddOrPromoteWebcamSource(camera, targetList));
    }

    internal void AddFileSourceFromEditor(string path, Guid? parentId)
    {
        var targetList = ResolveTargetList(parentId);
        if (targetList == null)
        {
            return;
        }

        RunWithoutLayerEditorRefresh(() => AddOrPromoteFileSource(path, targetList));
    }

    internal void AddVideoSequenceFromEditor(IReadOnlyList<string> paths, Guid? parentId)
    {
        var targetList = ResolveTargetList(parentId);
        if (targetList == null)
        {
            return;
        }

        RunWithoutLayerEditorRefresh(() => AddVideoSequenceSource(paths, targetList));
    }

    internal async void AddYoutubeSourceFromEditor(string url, Guid? parentId)
    {
         var targetList = ResolveTargetList(parentId);
         if (targetList == null)
         {
             return;
         }

         try
         {
             var (success, info, error) = await _fileCapture.TryCreateYoutubeSource(url);
             if (!success)
             {
                 MessageBox.Show(this, $"Failed to load YouTube video:\n{error}", "YouTube Error", MessageBoxButton.OK, MessageBoxImage.Error);
                 return;
             }
             
             RunWithoutLayerEditorRefresh(() => 
             {
                 var source = CaptureSource.CreateFile(info.Path, info.DisplayName, info.Width, info.Height);
                 targetList.Add(source);
             });
             
             NotifyLayerEditorSourcesChanged();
         }
         catch (Exception ex)
         {
             Logger.Error($"Failed to add YouTube source from editor: {ex.Message}", ex);
         }
    }

    internal void UpdateSourceBlendMode(Guid sourceId, string blendMode)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            if (TryParseBlendMode(blendMode, source.BlendMode, out var mode))
            {
                source.BlendMode = mode;
                RenderFrame();
                SaveConfig();
            }
        });
    }

    internal void UpdateSourceFitMode(Guid sourceId, string fitMode)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            if (Enum.TryParse<FitMode>(fitMode, true, out var mode))
            {
                source.FitMode = mode;
                RenderFrame();
                SaveConfig();
            }
        });
    }

    internal void UpdateSourceOpacity(Guid sourceId, double opacity)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            source.Opacity = Math.Clamp(opacity, 0, 1);
            RenderFrame();
            SaveConfig();
        });
    }

    internal void UpdateSourceVideoAudioEnabled(Guid sourceId, bool enabled)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null || !IsVideoSource(source))
            {
                return;
            }

            source.VideoAudioEnabled = enabled;
            ApplySourceVideoAudioState(source);
            SaveConfig();
        });
    }

    internal void UpdateSourceVideoAudioVolume(Guid sourceId, double volume)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null || !IsVideoSource(source))
            {
                return;
            }

            source.VideoAudioVolume = Math.Clamp(volume, 0, 1);
            ApplySourceVideoAudioState(source);
            SaveConfig();
        });
    }

    internal bool TryGetSourceVideoPlaybackState(Guid sourceId, out FileCaptureService.VideoPlaybackState playbackState)
    {
        playbackState = default;
        var source = FindSourceById(sourceId);
        if (source == null)
        {
            return false;
        }

        bool success = TryGetSourceVideoPlaybackState(source, out playbackState);
        if (success)
        {
            source.VideoPlaybackPaused = playbackState.IsPaused;
        }
        return success;
    }

    internal void UpdateSourceVideoPlaybackPaused(Guid sourceId, bool paused)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            if (!SetSourceVideoPlaybackPaused(source, paused))
            {
                Logger.Warn($"Failed to {(paused ? "pause" : "resume")} video source: {source.DisplayName}");
            }
        });
    }

    internal void SeekSourceVideo(Guid sourceId, double normalizedPosition)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            if (!SeekSourceVideo(source, normalizedPosition))
            {
                Logger.Warn($"Failed to seek video source: {source.DisplayName}");
            }
        });
    }

    internal bool GetSourceAudioMasterEnabled() => _sourceAudioMasterEnabled;

    internal double GetSourceAudioMasterVolume() => _sourceAudioMasterVolume;

    internal LayerEditorProjectSettings GetProjectSettingsForEditor()
    {
        return new LayerEditorProjectSettings
        {
            Height = _configuredRows,
            Depth = _configuredDepth,
            Framerate = _currentFpsFromConfig,
            LifeOpacity = _lifeOpacity,
            RgbHueShiftDegrees = _rgbHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = _rgbHueShiftSpeedDegreesPerSecond,
            InvertComposite = _invertComposite,
            Passthrough = _passthroughEnabled,
            CompositeBlendMode = _blendMode.ToString()
        };
    }

    internal void ApplyProjectSettingsFromEditor(LayerEditorProjectSettings? settings)
    {
        if (settings == null)
        {
            return;
        }

        RunWithoutLayerEditorRefresh(() =>
        {
            int nextRows = Math.Clamp(settings.Height, MinRows, MaxRows);
            int nextDepth = Math.Clamp(settings.Depth, 3, 96);
            bool dimensionsChanged = nextRows != _configuredRows || nextDepth != _configuredDepth;

            _configuredRows = nextRows;
            _configuredDepth = nextDepth;
            _currentFpsFromConfig = Math.Clamp(settings.Framerate, 5, 144);
            _currentFps = _currentFpsFromConfig;
            _currentSimulationTargetFps = _currentFpsFromConfig;
            _lifeOpacity = Math.Clamp(settings.LifeOpacity, 0, 1);
            _effectiveLifeOpacity = _lifeOpacity;
            _rgbHueShiftDegrees = NormalizeHueDegrees(settings.RgbHueShiftDegrees);
            _rgbHueShiftSpeedDegreesPerSecond = Math.Clamp(settings.RgbHueShiftSpeedDegreesPerSecond, -MaxRgbHueShiftSpeedDegreesPerSecond, MaxRgbHueShiftSpeedDegreesPerSecond);
            _invertComposite = settings.InvertComposite;
            _passthroughEnabled = settings.Passthrough;
            _blendMode = ParseBlendModeOrDefault(settings.CompositeBlendMode, _blendMode);

            if (dimensionsChanged)
            {
                ApplyDimensions(_configuredRows, _configuredDepth, _currentAspectRatio, persist: false);
            }
            else
            {
                _pulseStep = 0;
                RenderFrame();
            }

            UpdateFramerateMenuChecks();
            UpdateBlendModeMenuChecks();
            UpdateEffectInput();
            SaveConfig();
        });
    }

    internal void GetSimulationLayerSettingsForEditor(out IReadOnlyList<LayerEditorSimulationLayer> simulationLayers)
    {
        simulationLayers = _simulationLayers
            .Select(ToEditorSimulationLayer)
            .ToList();
    }

    internal void ApplySimulationLayerSettingsFromEditor(IReadOnlyList<LayerEditorSimulationLayer>? simulationLayers)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            if (simulationLayers == null || simulationLayers.Count == 0)
            {
                ClearSimulationLayers();
                _pulseStep = 0;
                RenderFrame();
                SaveConfig();
                return;
            }

            var specs = NormalizeSimulationLayerSpecs(simulationLayers);
            if (!ApplySimulationLayerSpecs(specs))
            {
                return;
            }

            var first = EnumerateSimulationLeafLayers(_simulationLayers).FirstOrDefault();
            if (first != null)
            {
                _injectionMode = first.InjectionMode;
                _lifeMode = first.LifeMode;
                _binningMode = first.BinningMode;
                _injectionNoise = first.InjectionNoise;
                _captureThresholdMin = first.ThresholdMin;
                _captureThresholdMax = first.ThresholdMax;
                _invertThreshold = first.InvertThreshold;
            }

            _pulseStep = 0;
            UpdateInjectionModeMenuChecks();
            RenderFrame();
            SaveConfig();
        });
    }

    internal void UpdateMasterSourceAudioEnabled(bool enabled)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            _sourceAudioMasterEnabled = enabled;
            ApplyMasterVideoAudioState();
            SaveConfig();
        });
    }

    internal void UpdateMasterSourceAudioVolume(double volume)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            _sourceAudioMasterVolume = Math.Clamp(volume, 0, 1);
            ApplyMasterVideoAudioState();
            SaveConfig();
        });
    }

    internal void UpdateSourceMirror(Guid sourceId, bool mirror)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            source.Mirror = mirror;
            RenderFrame();
            SaveConfig();
        });
    }

    internal void UpdateSourceKeyEnabled(Guid sourceId, bool enabled)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            source.KeyEnabled = enabled;
            RenderFrame();
            SaveConfig();
        });
    }

    internal void UpdateSourceKeyTolerance(Guid sourceId, double tolerance)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            source.KeyTolerance = Math.Clamp(tolerance, 0, 1);
            RenderFrame();
            SaveConfig();
        });
    }

    internal void UpdateSourceKeyColor(Guid sourceId, string? value)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            if (TryParseHexColor(value, out var keyR, out var keyG, out var keyB))
            {
                source.KeyColorR = keyR;
                source.KeyColorG = keyG;
                source.KeyColorB = keyB;
                RenderFrame();
                SaveConfig();
            }
        });
    }

    internal void UpdateGroupName(Guid sourceId, string displayName)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null || source.Type != CaptureSource.SourceType.Group)
            {
                return;
            }

            source.SetDisplayName(string.IsNullOrWhiteSpace(displayName) ? "Layer Group" : displayName);
            SaveConfig();
        });
    }

    internal void UpdateSourceDisplayName(Guid sourceId, string displayName)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            string fallbackName = source.Type switch
            {
                CaptureSource.SourceType.Group => "Layer Group",
                CaptureSource.SourceType.SimGroup => "Sim Group",
                _ => source.DisplayName
            };

            source.SetDisplayName(string.IsNullOrWhiteSpace(displayName) ? fallbackName : displayName);
            if (source.Type == CaptureSource.SourceType.SimGroup)
            {
                ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
                RenderFrame();
            }

            SaveConfig();
        });
    }

    internal void UpdateSourceEnabled(Guid sourceId, bool enabled)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            source.Enabled = enabled;
            if (source.Type == CaptureSource.SourceType.SimGroup)
            {
                ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
            }

            RenderFrame();
            SaveConfig();
        });
    }

    internal void UpdateSimulationGroupLayers(Guid sourceId, IReadOnlyList<LayerEditorSimulationLayer>? simulationLayers)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null || source.Type != CaptureSource.SourceType.SimGroup)
            {
                return;
            }

            source.SimulationLayers.Clear();
            foreach (var simulationLayer in FlattenSourceSimulationLayerSpecs(NormalizeSimulationLayerSpecs(simulationLayers, fallbackToDefault: false)))
            {
                source.SimulationLayers.Add(simulationLayer);
            }

            int enabledLayerCount = source.SimulationLayers.Count(layer => layer.Enabled);
            Logger.Info($"Updated sim group '{source.DisplayName}' with {source.SimulationLayers.Count} layer(s), enabled={enabledLayerCount}.");

            ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
            RenderFrame();
            SaveConfig();
        });
    }

    internal void MakePrimaryFromEditor(Guid sourceId)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            MakePrimarySource(source);
        });
    }

    internal void MoveSourceFromEditor(Guid sourceId, int delta)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            MoveSource(source, delta);
        });
    }

    internal void RemoveSourceFromEditor(Guid sourceId)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            RemoveSource(source);
        });
    }

    internal void RestartVideoFromEditor(Guid sourceId)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            RestartVideoSource(source);
        });
    }

    internal void AddAnimationFromEditor(Guid sourceId, string type, string? translateDirection, string? rotationDirection)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            if (!Enum.TryParse<AnimationType>(type, true, out var animationType))
            {
                return;
            }

            var animation = new LayerAnimation
            {
                Type = animationType,
                Loop = animationType == AnimationType.DvdBounce ? AnimationLoop.PingPong : AnimationLoop.Forward,
                Speed = AnimationSpeed.Normal
            };

            if (!string.IsNullOrWhiteSpace(translateDirection) &&
                Enum.TryParse<TranslateDirection>(translateDirection, true, out var translate))
            {
                animation.TranslateDirection = translate;
            }

            if (!string.IsNullOrWhiteSpace(rotationDirection) &&
                Enum.TryParse<RotationDirection>(rotationDirection, true, out var rotate))
            {
                animation.RotationDirection = rotate;
            }

            source.Animations.Add(animation);
            SaveConfig();
        });
    }

    internal void RemoveAnimationFromEditor(Guid sourceId, Guid animationId)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            var animation = source.Animations.FirstOrDefault(item => item.Id == animationId);
            if (animation == null)
            {
                return;
            }

            source.Animations.Remove(animation);
            SaveConfig();
        });
    }

    internal void UpdateAnimationFromEditor(Guid sourceId, LayerEditorAnimation model)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            var source = FindSourceById(sourceId);
            if (source == null)
            {
                return;
            }

            var animation = source.Animations.FirstOrDefault(item => item.Id == model.Id);
            if (animation == null)
            {
                return;
            }

            ApplyAnimationModel(animation, model);
            SaveConfig();
        });
    }

    private void ApplyAnimationModel(LayerAnimation animation, LayerEditorAnimation model)
    {
        if (Enum.TryParse<AnimationType>(model.Type, true, out var type))
        {
            animation.Type = type;
        }
        if (Enum.TryParse<AnimationLoop>(model.Loop, true, out var loop))
        {
            animation.Loop = loop;
        }
        if (Enum.TryParse<AnimationSpeed>(model.Speed, true, out var speed))
        {
            animation.Speed = speed;
        }
        if (Enum.TryParse<TranslateDirection>(model.TranslateDirection, true, out var translate))
        {
            animation.TranslateDirection = translate;
        }
        if (Enum.TryParse<RotationDirection>(model.RotationDirection, true, out var rotate))
        {
            animation.RotationDirection = rotate;
        }
        animation.RotationDegrees = Math.Clamp(model.RotationDegrees, 0, 360);
        if (model.DvdScale > 0)
        {
            animation.DvdScale = Math.Clamp(model.DvdScale, 0.01, 1.0);
        }
        animation.BeatShakeIntensity = ClampAnimationIntensity(animation.Type, model.BeatShakeIntensity);
        animation.AudioGranularLowGain = Math.Clamp(model.AudioGranularLowGain, 0, MaxAudioGranularEqBandGain);
        animation.AudioGranularMidGain = Math.Clamp(model.AudioGranularMidGain, 0, MaxAudioGranularEqBandGain);
        animation.AudioGranularHighGain = Math.Clamp(model.AudioGranularHighGain, 0, MaxAudioGranularEqBandGain);
        if (model.BeatsPerCycle > 0)
        {
            animation.BeatsPerCycle = Math.Clamp(model.BeatsPerCycle, 1, 4096);
        }
    }

    private List<CaptureSource>? ResolveTargetList(Guid? parentId)
    {
        if (!parentId.HasValue)
        {
            return _sources;
        }

        var parent = FindSourceById(parentId.Value);
        if (parent != null && parent.Type == CaptureSource.SourceType.Group)
        {
            return parent.Children;
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
        else if (source.Type == CaptureSource.SourceType.VideoSequence)
        {
            source.DisposeVideoSequence();
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
        NotifyLayerEditorSourcesChanged();
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
        NotifyLayerEditorSourcesChanged();
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
        ApplySimulationLayersFromSourceStack(fallbackToDefault: false);

        if (_sources.Count == 0)
        {
            ClearSources();
            return;
        }

        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
        NotifyLayerEditorSourcesChanged();
    }

    private void ClearSources()
    {
        bool hadSources = _sources.Count > 0;
        bool preservePassthrough = _passthroughEnabled;

        foreach (var source in _sources.ToList())
        {
            CleanupSource(source);
        }

        _fileCapture.Clear();
        
        _sources.Clear();
        ClearSimulationLayers();
        _lastCompositeFrame = null;
        _passthroughEnabled = preservePassthrough;
        
        Logger.Info("Cleared all sources; reset webcam capture.");
        if (PassthroughMenuItem != null)
        {
            PassthroughMenuItem.IsChecked = _passthroughEnabled;
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
        NotifyLayerEditorSourcesChanged();
    }

    private void InjectCaptureFrames(bool injectLayers = true)
    {
        if (_sources.Count == 0)
        {
            _lastCompositeFrame = null;
            return;
        }

        double animationTime = _lifetimeStopwatch.Elapsed.TotalSeconds;
        long captureSourcesStamp = BeginProfileStamp();
        bool removedAny = CaptureSourceList(_sources, animationTime);
        EndProfileStamp("capture_sources_ms", captureSourcesStamp);
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

        if (HasEmbeddedSimulationGroups(_sources))
        {
            bool injectedAnyInlineLayer = false;
            int steppedInlinePassCount = 0;
            int inlineStepsThisFrame = injectLayers ? _pendingInlineSimulationStepsThisFrame : 0;

            long compositeBuildStampInline = BeginProfileStamp();
            var inlineComposite = BuildInlineCompositeFrame(
                _sources,
                ref _compositeDownscaledBuffer,
                useEngineDimensions: true,
                animationTime,
                inlineStepsThisFrame,
                ref injectedAnyInlineLayer,
                ref steppedInlinePassCount);
            EndProfileStamp("build_composite_ms", compositeBuildStampInline);

            if (inlineComposite == null)
            {
                _lastCompositeFrame = null;
                return;
            }

            if (injectedAnyInlineLayer)
            {
                _pulseStep++;
            }

            if (steppedInlinePassCount > 0)
            {
                _simulationFrames += steppedInlinePassCount;
            }

            _lastCompositeFrame = inlineComposite;
            long inlineSurfaceStamp = BeginProfileStamp();
            UpdateDisplaySurface();
            EndProfileStamp("update_display_surface_ms", inlineSurfaceStamp);
            return;
        }

        bool hasEnabledSimulationLayers = EnumerateSimulationLeafLayers(_simulationLayers).Any(layer => layer.Enabled);
        bool requireCompositeCpuReadback =
            _isRecording ||
            !hasEnabledSimulationLayers ||
            (_passthroughEnabled && !_renderBackend.SupportsGpuSimulationComposition);
        long compositeBuildStamp = BeginProfileStamp();
        var composite = BuildCompositeFrame(_sources, ref _compositeDownscaledBuffer, useEngineDimensions: true, animationTime,
            includeCpuReadback: requireCompositeCpuReadback);
        EndProfileStamp("build_composite_ms", compositeBuildStamp);
        if (composite == null)
        {
            _lastCompositeFrame = null;
            return;
        }

        _lastCompositeFrame = composite;
        long surfaceStamp = BeginProfileStamp();
        UpdateDisplaySurface();
        EndProfileStamp("update_display_surface_ms", surfaceStamp);

        if (!injectLayers)
        {
            return;
        }

        bool compositeHasCpuReadback = composite.DownscaledWidth > 0 &&
                                       composite.DownscaledHeight > 0 &&
                                       composite.Downscaled.Length >= composite.DownscaledWidth * composite.DownscaledHeight * 4;
        bool attemptedCpuCompositeFallback = false;

        bool EnsureCompositeCpuReadback()
        {
            if (compositeHasCpuReadback)
            {
                return true;
            }

            if (attemptedCpuCompositeFallback)
            {
                return false;
            }

            attemptedCpuCompositeFallback = true;
            long fallbackCompositeStamp = BeginProfileStamp();
            var cpuComposite = BuildCompositeFrame(
                _sources,
                ref _compositeDownscaledBuffer,
                useEngineDimensions: true,
                animationTime,
                includeCpuReadback: true);
            EndProfileStamp("build_composite_fallback_ms", fallbackCompositeStamp);
            if (cpuComposite == null)
            {
                return false;
            }

            composite = cpuComposite;
            _lastCompositeFrame = cpuComposite;
            compositeHasCpuReadback = composite.DownscaledWidth > 0 &&
                                      composite.DownscaledHeight > 0 &&
                                      composite.Downscaled.Length >= composite.DownscaledWidth * composite.DownscaledHeight * 4;
            return compositeHasCpuReadback;
        }

        bool injectedAnyLayer = false;
        int gpuHandoffLayerCount = 0;
        int cpuGrayInjectLayerCount = 0;
        int cpuRgbInjectLayerCount = 0;
        long injectLayersStamp = BeginProfileStamp();
        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            if (!layer.Enabled)
            {
                continue;
            }

            var engine = layer.Engine;
            if (engine == null)
            {
                continue;
            }

            bool invertInput = layer.InputFunction == SimulationInputFunction.Inverse;
            double currentHueShiftDegrees = CurrentRgbHueShiftDegrees(layer);
            if (engine is GpuSimulationBackend gpuEngine &&
                composite.GpuSurface != null &&
                gpuEngine.TryInjectCompositeSurface(
                    composite.GpuSurface,
                    layer.EffectiveThresholdMin,
                    layer.EffectiveThresholdMax,
                    layer.InvertThreshold,
                    layer.InjectionMode,
                    layer.EffectiveInjectionNoise,
                    gpuEngine.Depth,
                    _pulseStep,
                    invertInput,
                    currentHueShiftDegrees))
            {
                gpuHandoffLayerCount++;
                injectedAnyLayer = true;
                continue;
            }

            if (layer.LifeMode == GameOfLifeEngine.LifeMode.NaiveGrayscale)
            {
                if (!EnsureCompositeCpuReadback())
                {
                    continue;
                }

                layer.GrayMask = EnsureLayerMask(layer.GrayMask, composite.DownscaledHeight, composite.DownscaledWidth);
                var grayMask = layer.GrayMask;
                FillLuminanceMask(
                    composite.Downscaled,
                    composite.DownscaledWidth,
                    composite.DownscaledHeight,
                    layer.EffectiveThresholdMin,
                    layer.EffectiveThresholdMax,
                    layer.InvertThreshold,
                    layer.InjectionMode,
                    layer.EffectiveInjectionNoise,
                    engine.Depth,
                    _pulseStep,
                    grayMask,
                    invertInput);
                engine.InjectFrame(grayMask);
                cpuGrayInjectLayerCount++;
                injectedAnyLayer = true;
            }
            else
            {
                if (!EnsureCompositeCpuReadback())
                {
                    continue;
                }

                layer.RedMask = EnsureLayerMask(layer.RedMask, composite.DownscaledHeight, composite.DownscaledWidth);
                layer.GreenMask = EnsureLayerMask(layer.GreenMask, composite.DownscaledHeight, composite.DownscaledWidth);
                layer.BlueMask = EnsureLayerMask(layer.BlueMask, composite.DownscaledHeight, composite.DownscaledWidth);
                var rMask = layer.RedMask;
                var gMask = layer.GreenMask;
                var bMask = layer.BlueMask;
                FillChannelMasks(
                    composite.Downscaled,
                    composite.DownscaledWidth,
                    composite.DownscaledHeight,
                    currentHueShiftDegrees,
                    layer.EffectiveThresholdMin,
                    layer.EffectiveThresholdMax,
                    layer.InvertThreshold,
                    layer.InjectionMode,
                    layer.EffectiveInjectionNoise,
                    engine.RDepth,
                    engine.GDepth,
                    engine.BDepth,
                    _pulseStep,
                    rMask,
                    gMask,
                    bMask,
                    invertInput);
                engine.InjectRgbFrame(rMask, gMask, bMask);
                cpuRgbInjectLayerCount++;
                injectedAnyLayer = true;
            }
        }
        EndProfileStamp("inject_layers_ms", injectLayersStamp);
        if (_frameProfiler.IsActive)
        {
            _frameProfiler.RecordSample("gpu_handoff_layer_count", gpuHandoffLayerCount);
            _frameProfiler.RecordSample("cpu_gray_inject_layer_count", cpuGrayInjectLayerCount);
            _frameProfiler.RecordSample("cpu_rgb_inject_layer_count", cpuRgbInjectLayerCount);
        }

        if (injectedAnyLayer)
        {
            _pulseStep++;
        }
    }

    private void RunSimulationStepPasses(int maxStepsThisFrame, IReadOnlyList<SimulationLayerState> simulationLeaves)
    {
        if (maxStepsThisFrame <= 0)
        {
            return;
        }

        if (RunSimulationInjectionAndStepPass())
        {
            _simulationFrames++;
        }

        for (int i = 1; i < maxStepsThisFrame; i++)
        {
            if (RunSimulationStepOnlyPass(simulationLeaves))
            {
                _simulationFrames++;
            }
        }
    }

    private bool RunSimulationInjectionAndStepPass()
    {
        CompositeFrame? composite = _lastCompositeFrame;
        bool compositeHasCpuReadback = composite != null &&
                                       composite.DownscaledWidth > 0 &&
                                       composite.DownscaledHeight > 0 &&
                                       composite.Downscaled.Length >= composite.DownscaledWidth * composite.DownscaledHeight * 4;
        bool attemptedCpuCompositeFallback = false;
        bool injectedAnyLayer = false;
        bool steppedAnyLayer = false;
        GpuCompositeSurface? previousTopLevelSurface = null;

        foreach (var layer in _simulationLayers)
        {
            previousTopLevelSurface = ExecuteSimulationNodeInjectionAndStep(
                layer,
                ref composite,
                ref compositeHasCpuReadback,
                ref attemptedCpuCompositeFallback,
                previousTopLevelSurface,
                ref injectedAnyLayer,
                ref steppedAnyLayer);
        }

        if (injectedAnyLayer)
        {
            _pulseStep++;
        }

        return steppedAnyLayer;
    }

    private GpuCompositeSurface? ExecuteSimulationNodeInjectionAndStep(
        SimulationLayerState layer,
        ref CompositeFrame? composite,
        ref bool compositeHasCpuReadback,
        ref bool attemptedCpuCompositeFallback,
        GpuCompositeSurface? upstreamSurface,
        ref bool injectedAnyLayer,
        ref bool steppedAnyLayer)
    {
        if (layer.IsGroup)
        {
            if (!layer.Enabled)
            {
                return upstreamSurface;
            }

            GpuCompositeSurface? published = upstreamSurface ?? composite?.GpuSurface;
            foreach (var child in layer.Children)
            {
                published = ExecuteSimulationGroupChildInjectionAndStep(
                    child,
                    ref composite,
                    ref compositeHasCpuReadback,
                    ref attemptedCpuCompositeFallback,
                    published,
                    ref injectedAnyLayer,
                    ref steppedAnyLayer);
            }

            return published ?? upstreamSurface;
        }

        bool stepped = TryInjectAndStepLayerFromSceneComposite(
            layer,
            ref composite,
            ref compositeHasCpuReadback,
            ref attemptedCpuCompositeFallback,
            ref injectedAnyLayer);
        steppedAnyLayer |= stepped;

        return layer.Enabled ? GetPublishedSimulationSurface(layer) ?? upstreamSurface : upstreamSurface;
    }

    private GpuCompositeSurface? ExecuteSimulationGroupChildInjectionAndStep(
        SimulationLayerState layer,
        ref CompositeFrame? composite,
        ref bool compositeHasCpuReadback,
        ref bool attemptedCpuCompositeFallback,
        GpuCompositeSurface? inputSurface,
        ref bool injectedAnyLayer,
        ref bool steppedAnyLayer)
    {
        if (layer.IsGroup)
        {
            if (!layer.Enabled)
            {
                return inputSurface;
            }

            GpuCompositeSurface? published = inputSurface;
            foreach (var child in layer.Children)
            {
                published = ExecuteSimulationGroupChildInjectionAndStep(
                    child,
                    ref composite,
                    ref compositeHasCpuReadback,
                    ref attemptedCpuCompositeFallback,
                    published,
                    ref injectedAnyLayer,
                    ref steppedAnyLayer);
            }

            return published ?? inputSurface;
        }

        bool stepped = TryInjectAndStepLayer(
            layer,
            ref composite,
            ref compositeHasCpuReadback,
            ref attemptedCpuCompositeFallback,
            inputSurface,
            ref injectedAnyLayer,
            useSceneCompositeFallbackWhenGpuSurfaceUnavailable: inputSurface == null);
        steppedAnyLayer |= stepped;

        return layer.Enabled ? GetPublishedSimulationSurface(layer) ?? inputSurface : inputSurface;
    }

    private bool RunSimulationStepOnlyPass(IReadOnlyList<SimulationLayerState> simulationLeaves)
    {
        bool stepped = false;
        foreach (var layer in simulationLeaves)
        {
            stepped |= TryStepLayerOnceIfDue(layer);
        }

        return stepped;
    }

    private bool TryInjectAndStepLayerFromSceneComposite(
        SimulationLayerState layer,
        ref CompositeFrame? composite,
        ref bool compositeHasCpuReadback,
        ref bool attemptedCpuCompositeFallback,
        ref bool injectedAnyLayer)
    {
        return TryInjectAndStepLayer(
            layer,
            ref composite,
            ref compositeHasCpuReadback,
            ref attemptedCpuCompositeFallback,
            inputSurface: null,
            ref injectedAnyLayer,
            useSceneCompositeFallbackWhenGpuSurfaceUnavailable: true);
    }

    private bool TryInjectAndStepLayer(
        SimulationLayerState layer,
        ref CompositeFrame? composite,
        ref bool compositeHasCpuReadback,
        ref bool attemptedCpuCompositeFallback,
        GpuCompositeSurface? inputSurface,
        ref bool injectedAnyLayer,
        bool useSceneCompositeFallbackWhenGpuSurfaceUnavailable)
    {
        bool shouldStep = LayerNeedsStep(layer);
        if (!shouldStep)
        {
            return false;
        }

        bool injected = false;
        if (inputSurface != null)
        {
            injected = TryInjectLayerFromGpuSurface(layer, inputSurface);
        }
        else if (composite != null)
        {
            injected = TryInjectLayerFromComposite(
                layer,
                ref composite,
                ref compositeHasCpuReadback,
                ref attemptedCpuCompositeFallback,
                useSceneCompositeFallbackWhenGpuSurfaceUnavailable);
        }

        if (injected)
        {
            injectedAnyLayer = true;
        }

        return TryStepLayerOnceIfDue(layer);
    }

    private bool LayerNeedsStep(SimulationLayerState layer)
    {
        if (layer.IsGroup || !layer.Enabled)
        {
            return false;
        }

        double targetFps = Math.Max(layer.EffectiveSimulationTargetFps, 0);
        if (targetFps < 0.01)
        {
            layer.TimeSinceLastStep = 0;
            return false;
        }

        double desiredInterval = 1.0 / targetFps;
        return layer.TimeSinceLastStep + 0.0000001 >= desiredInterval;
    }

    private bool TryStepLayerOnceIfDue(SimulationLayerState layer)
    {
        if (layer.IsGroup || !layer.Enabled || layer.Engine == null)
        {
            return false;
        }

        double targetFps = Math.Max(layer.EffectiveSimulationTargetFps, 0);
        if (targetFps < 0.01)
        {
            layer.TimeSinceLastStep = 0;
            return false;
        }

        double desiredInterval = 1.0 / targetFps;
        if (layer.TimeSinceLastStep + 0.0000001 < desiredInterval)
        {
            return false;
        }

        layer.Engine.Step();
        layer.TimeSinceLastStep -= desiredInterval;
        if (layer.TimeSinceLastStep > desiredInterval * MaxSimulationStepsPerRender)
        {
            layer.TimeSinceLastStep = desiredInterval;
        }

        return true;
    }

    private bool TryInjectLayerFromGpuSurface(SimulationLayerState layer, GpuCompositeSurface inputSurface)
    {
        if (layer.Engine is not IGpuSimulationSurfaceBackend gpuEngine)
        {
            return false;
        }

        bool invertInput = layer.InputFunction == SimulationInputFunction.Inverse;
        double currentHueShiftDegrees = CurrentRgbHueShiftDegrees(layer);
        return gpuEngine.TryInjectCompositeSurface(
            inputSurface,
            layer.EffectiveThresholdMin,
            layer.EffectiveThresholdMax,
            layer.InvertThreshold,
            layer.InjectionMode,
            layer.EffectiveInjectionNoise,
            layer.Engine!.Depth,
            _pulseStep,
            invertInput,
            currentHueShiftDegrees);
    }

    private bool TryInjectLayerFromComposite(
        SimulationLayerState layer,
        ref CompositeFrame? composite,
        ref bool compositeHasCpuReadback,
        ref bool attemptedCpuCompositeFallback,
        bool allowCpuFallback)
    {
        var currentComposite = composite;
        var engine = layer.Engine;
        if (currentComposite == null || engine == null)
        {
            return false;
        }

        bool invertInput = layer.InputFunction == SimulationInputFunction.Inverse;
        double currentHueShiftDegrees = CurrentRgbHueShiftDegrees(layer);
        if (engine is IGpuSimulationSurfaceBackend gpuEngine &&
            currentComposite.GpuSurface != null &&
            gpuEngine.TryInjectCompositeSurface(
                currentComposite.GpuSurface,
                layer.EffectiveThresholdMin,
                layer.EffectiveThresholdMax,
                layer.InvertThreshold,
                layer.InjectionMode,
                layer.EffectiveInjectionNoise,
                gpuEngine.Depth,
                _pulseStep,
                invertInput,
                currentHueShiftDegrees))
        {
            return true;
        }

        if (!allowCpuFallback)
        {
            return false;
        }

        if (!EnsureCompositeCpuReadback(ref composite, ref compositeHasCpuReadback, ref attemptedCpuCompositeFallback))
        {
            return false;
        }

        currentComposite = composite;
        if (currentComposite == null)
        {
            return false;
        }

        invertInput = layer.InputFunction == SimulationInputFunction.Inverse;
        currentHueShiftDegrees = CurrentRgbHueShiftDegrees(layer);

        if (layer.LifeMode == GameOfLifeEngine.LifeMode.NaiveGrayscale)
        {
            var grayMask = layer.GrayMask = EnsureLayerMask(layer.GrayMask, currentComposite.DownscaledHeight, currentComposite.DownscaledWidth);
            FillLuminanceMask(
                currentComposite.Downscaled,
                currentComposite.DownscaledWidth,
                currentComposite.DownscaledHeight,
                layer.EffectiveThresholdMin,
                layer.EffectiveThresholdMax,
                layer.InvertThreshold,
                layer.InjectionMode,
                layer.EffectiveInjectionNoise,
                engine.Depth,
                _pulseStep,
                grayMask,
                invertInput);
            engine.InjectFrame(grayMask);
            return true;
        }

        var rMask = layer.RedMask = EnsureLayerMask(layer.RedMask, currentComposite.DownscaledHeight, currentComposite.DownscaledWidth);
        var gMask = layer.GreenMask = EnsureLayerMask(layer.GreenMask, currentComposite.DownscaledHeight, currentComposite.DownscaledWidth);
        var bMask = layer.BlueMask = EnsureLayerMask(layer.BlueMask, currentComposite.DownscaledHeight, currentComposite.DownscaledWidth);
        FillChannelMasks(
            currentComposite.Downscaled,
            currentComposite.DownscaledWidth,
            currentComposite.DownscaledHeight,
            currentHueShiftDegrees,
            layer.EffectiveThresholdMin,
            layer.EffectiveThresholdMax,
            layer.InvertThreshold,
            layer.InjectionMode,
            layer.EffectiveInjectionNoise,
            engine.RDepth,
            engine.GDepth,
            engine.BDepth,
            _pulseStep,
            rMask,
            gMask,
            bMask,
            invertInput);
        engine.InjectRgbFrame(rMask, gMask, bMask);
        return true;
    }

    private bool EnsureCompositeCpuReadback(
        ref CompositeFrame? composite,
        ref bool compositeHasCpuReadback,
        ref bool attemptedCpuCompositeFallback)
    {
        if (compositeHasCpuReadback)
        {
            return true;
        }

        if (attemptedCpuCompositeFallback)
        {
            return false;
        }

        attemptedCpuCompositeFallback = true;
        long fallbackCompositeStamp = BeginProfileStamp();
        var cpuComposite = BuildCompositeFrame(
            _sources,
            ref _compositeDownscaledBuffer,
            useEngineDimensions: true,
            _lifetimeStopwatch.Elapsed.TotalSeconds,
            includeCpuReadback: true);
        EndProfileStamp("build_composite_fallback_ms", fallbackCompositeStamp);
        if (cpuComposite == null)
        {
            return false;
        }

        composite = cpuComposite;
        _lastCompositeFrame = cpuComposite;
        compositeHasCpuReadback = composite.DownscaledWidth > 0 &&
                                  composite.DownscaledHeight > 0 &&
                                  composite.Downscaled.Length >= composite.DownscaledWidth * composite.DownscaledHeight * 4;
        return compositeHasCpuReadback;
    }

    private GpuCompositeSurface? GetPublishedSimulationSurface(SimulationLayerState layer)
    {
        return layer.Engine is IGpuSimulationSurfaceBackend gpuBackend &&
               gpuBackend.TryGetColorSurface(out var surface)
            ? surface
            : null;
    }

    private void ApplyAudioReactiveFps()
    {
        if (!_audioReactiveEnabled || !_audioReactiveLevelToFpsEnabled || string.IsNullOrWhiteSpace(_selectedAudioDeviceId))
        {
            _audioReactiveFpsMultiplier = 1.0;
            return;
        }

        double gainScale = _audioReactiveEnergyGain / DefaultAudioReactiveEnergyGain;
        double normalizedLevel = Math.Clamp(_smoothedEnergy * gainScale, 0, 1);
        // Scale simulation rate from configured floor..100% of the current target FPS.
        // FpsBoost increases how quickly loudness reaches full-speed, but never exceeds target.
        double drive = Math.Clamp(normalizedLevel * (1.0 + _audioReactiveFpsBoost), 0, 1);
        double shapedDrive = Math.Pow(drive, 0.8);
        double minMultiplier = Math.Clamp(_audioReactiveFpsMinPercent, 0, 1);
        double mappedMultiplier = minMultiplier + ((1.0 - minMultiplier) * shapedDrive);
        _audioReactiveFpsMultiplier = mappedMultiplier;
        _currentSimulationTargetFps = Math.Clamp(_currentSimulationTargetFps * mappedMultiplier, 0, 144);
    }

    private double GetDisplayedAudioLevel()
    {
        return Math.Clamp(_fastAudioLevel, 0, 1);
    }

    private void UpdateAudioAnalysisRequirements()
    {
        bool needsDebugHistory = _showFps;
        bool needsSpectrum = NeedsAudioSpectrumAnalysis();
        _audioBeatDetector.SetAnalysisRequirements(needsSpectrum, needsDebugHistory);

        if (!needsDebugHistory)
        {
            ClearAudioDebugHistory();
        }
    }

    private bool NeedsAudioSpectrumAnalysis()
    {
        if (_showFps)
        {
            return true;
        }

        if (EnumerateSimulationLeafLayers(_simulationLayers).Any(layer =>
                layer.Enabled &&
                layer.ReactiveMappings.Any(mapping => SimulationReactivity.RequiresSpectrum(mapping.Input))))
        {
            return true;
        }

        return SourceTreeHasAudioGranularAnimation(_sources);
    }

    private static bool SourceTreeHasAudioGranularAnimation(IEnumerable<CaptureSource> sources)
    {
        foreach (var source in sources)
        {
            if (source.Animations.Any(animation => animation.Type == AnimationType.AudioGranular))
            {
                return true;
            }

            if (source.Children.Count > 0 && SourceTreeHasAudioGranularAnimation(source.Children))
            {
                return true;
            }
        }

        return false;
    }

    private void ClearAudioDebugHistory()
    {
        _frameGapHistoryAccumulator = 0;
        _frameGapHistory.Clear();
    }

    private void RecordFrameGapHistory(double frameGapMs, double dt)
    {
        if (!_showFps)
        {
            return;
        }

        _frameGapHistoryAccumulator += Math.Max(0, dt) * AudioDebugHistorySampleRate;
        while (_frameGapHistoryAccumulator >= 1.0)
        {
            _frameGapHistory.Enqueue(Math.Max(0, frameGapMs));
            _frameGapHistoryAccumulator -= 1.0;
        }

        while (_frameGapHistory.Count > AudioDebugHistorySize)
        {
            _frameGapHistory.Dequeue();
        }
    }

    private (double averageMs, double p95Ms, int over25Ms, int over33Ms, int over50Ms) GetRecentFrameGapStats()
    {
        if (_frameGapHistory.Count == 0)
        {
            return (0, 0, 0, 0, 0);
        }

        double[] values = _frameGapHistory.ToArray();
        double average = values.Average();
        int over25 = values.Count(value => value > 25.0);
        int over33 = values.Count(value => value > 33.333);
        int over50 = values.Count(value => value > 50.0);
        Array.Sort(values);
        int index = (int)Math.Round((values.Length - 1) * 0.95);
        index = Math.Clamp(index, 0, values.Length - 1);
        return (average, values[index], over25, over33, over50);
    }

    private void ApplyAudioReactiveLifeOpacity()
    {
        if (!_audioReactiveEnabled || !_audioReactiveLevelToLifeOpacityEnabled || string.IsNullOrWhiteSpace(_selectedAudioDeviceId))
        {
            _effectiveLifeOpacity = _lifeOpacity;
            return;
        }

        double gainScale = _audioReactiveEnergyGain / DefaultAudioReactiveEnergyGain;
        double drive = Math.Clamp(_fastAudioLevel * gainScale, 0, 1);
        double minScalar = Math.Clamp(_audioReactiveLifeOpacityMinScalar, 0, 1);
        double scalar = minScalar + ((1.0 - minScalar) * drive);
        _effectiveLifeOpacity = Math.Clamp(_lifeOpacity * scalar, 0, 1);
    }

    private bool HasSimulationReactiveAudioInput()
    {
        return !string.IsNullOrWhiteSpace(_selectedAudioDeviceId);
    }

    private double GetReactiveInputValue(SimulationReactiveInput input)
    {
        if (!HasSimulationReactiveAudioInput())
        {
            return 0;
        }

        return input switch
        {
            SimulationReactiveInput.Level => Math.Clamp(_fastAudioLevel, 0, 1),
            SimulationReactiveInput.Bass => Math.Clamp(_audioBeatDetector.BassNormalizedLevel, 0, 1),
            SimulationReactiveInput.Mid => Math.Clamp(_audioBeatDetector.MidNormalizedLevel, 0, 1),
            SimulationReactiveInput.High => Math.Clamp(_audioBeatDetector.HighNormalizedLevel, 0, 1),
            SimulationReactiveInput.Frequency => Math.Clamp(_audioBeatDetector.MainFrequencyNormalized, 0, 1),
            SimulationReactiveInput.BassFrequency => Math.Clamp(_audioBeatDetector.BassFrequencyNormalized, 0, 1),
            SimulationReactiveInput.MidFrequency => Math.Clamp(_audioBeatDetector.MidFrequencyNormalized, 0, 1),
            SimulationReactiveInput.HighFrequency => Math.Clamp(_audioBeatDetector.HighFrequencyNormalized, 0, 1),
            _ => 0
        };
    }

    private static double NormalizeReactiveInputThreshold(double value, double thresholdMin, double thresholdMax)
    {
        double min = Math.Clamp(thresholdMin, 0, 1);
        double max = Math.Clamp(thresholdMax, 0, 1);
        if (max < min)
        {
            (min, max) = (max, min);
        }

        double clampedValue = Math.Clamp(value, 0, 1);
        if (max - min <= 0.000001)
        {
            return clampedValue >= max ? 1.0 : 0.0;
        }

        return Math.Clamp((clampedValue - min) / (max - min), 0, 1);
    }

    private void ApplySimulationLayerReactiveState()
    {
        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            layer.EffectiveLifeOpacity = Math.Clamp(layer.LifeOpacity, 0, 1);
            layer.EffectiveSimulationTargetFps = Math.Max(_currentSimulationTargetFps, 0);
            layer.ReactiveHueShiftDegrees = 0;
            layer.EffectiveRgbHueShiftSpeedDegreesPerSecond = Math.Clamp(
                layer.RgbHueShiftSpeedDegreesPerSecond,
                -MaxRgbHueShiftSpeedDegreesPerSecond,
                MaxRgbHueShiftSpeedDegreesPerSecond);
            layer.EffectiveInjectionNoise = Math.Clamp(layer.InjectionNoise, 0, 1);
            layer.EffectiveThresholdMin = Math.Clamp(layer.ThresholdMin, 0, 1);
            layer.EffectiveThresholdMax = Math.Clamp(layer.ThresholdMax, 0, 1);
            layer.EffectivePixelSortCellWidth = Math.Clamp(layer.PixelSortCellWidth, 1, Math.Max(1, layer.Engine?.Columns ?? _configuredRows));
            layer.EffectivePixelSortCellHeight = Math.Clamp(layer.PixelSortCellHeight, 1, Math.Max(1, layer.Engine?.Rows ?? _configuredRows));

            if (!layer.Enabled)
            {
                layer.TimeSinceLastStep = 0;
                ApplySimulationLayerEngineSettings(layer);
                continue;
            }

            if (!HasSimulationReactiveAudioInput())
            {
                ApplySimulationLayerEngineSettings(layer);
                continue;
            }

            foreach (var mapping in layer.ReactiveMappings)
            {
                double inputValue = NormalizeReactiveInputThreshold(
                    GetReactiveInputValue(mapping.Input),
                    mapping.ThresholdMin,
                    mapping.ThresholdMax);
                switch (mapping.Output)
                {
                    case SimulationReactiveOutput.Opacity:
                    {
                        double multiplier = 1.0 + ((inputValue - 1.0) * Math.Clamp(mapping.Amount, 0, 1));
                        layer.EffectiveLifeOpacity = Math.Clamp(layer.EffectiveLifeOpacity * multiplier, 0, 1);
                        break;
                    }
                    case SimulationReactiveOutput.Framerate:
                    {
                        double multiplier = 1.0 + ((inputValue - 1.0) * Math.Clamp(mapping.Amount, 0, 1));
                        layer.EffectiveSimulationTargetFps = Math.Max(0, layer.EffectiveSimulationTargetFps * multiplier);
                        break;
                    }
                    case SimulationReactiveOutput.HueShift:
                        layer.ReactiveHueShiftDegrees += inputValue * Math.Clamp(mapping.Amount, 0, 360);
                        break;
                    case SimulationReactiveOutput.HueSpeed:
                        layer.EffectiveRgbHueShiftSpeedDegreesPerSecond = Math.Clamp(
                            layer.EffectiveRgbHueShiftSpeedDegreesPerSecond + (inputValue * Math.Clamp(mapping.Amount, 0, 180)),
                            -MaxRgbHueShiftSpeedDegreesPerSecond,
                            MaxRgbHueShiftSpeedDegreesPerSecond);
                        break;
                    case SimulationReactiveOutput.InjectionNoise:
                        layer.EffectiveInjectionNoise = Math.Clamp(layer.EffectiveInjectionNoise + (inputValue * Math.Clamp(mapping.Amount, 0, 1)), 0, 1);
                        break;
                    case SimulationReactiveOutput.ThresholdMin:
                        layer.EffectiveThresholdMin = Math.Clamp(layer.EffectiveThresholdMin + (inputValue * Math.Clamp(mapping.Amount, 0, 1)), 0, 1);
                        break;
                    case SimulationReactiveOutput.ThresholdMax:
                        layer.EffectiveThresholdMax = Math.Clamp(layer.EffectiveThresholdMax - (inputValue * Math.Clamp(mapping.Amount, 0, 1)), 0, 1);
                        break;
                    case SimulationReactiveOutput.PixelSortCellWidth:
                    {
                        int maxWidth = Math.Max(1, layer.Engine?.Columns ?? _configuredRows);
                        layer.EffectivePixelSortCellWidth = Math.Clamp(
                            layer.EffectivePixelSortCellWidth + (int)Math.Round(inputValue * Math.Clamp(mapping.Amount, 0, 50)),
                            1,
                            maxWidth);
                        break;
                    }
                    case SimulationReactiveOutput.PixelSortCellHeight:
                    {
                        int maxHeight = Math.Max(1, layer.Engine?.Rows ?? _configuredRows);
                        layer.EffectivePixelSortCellHeight = Math.Clamp(
                            layer.EffectivePixelSortCellHeight + (int)Math.Round(inputValue * Math.Clamp(mapping.Amount, 0, 50)),
                            1,
                            maxHeight);
                        break;
                    }
                }
            }

            if (layer.EffectiveThresholdMin > layer.EffectiveThresholdMax)
            {
                (layer.EffectiveThresholdMin, layer.EffectiveThresholdMax) = (layer.EffectiveThresholdMax, layer.EffectiveThresholdMin);
            }

            ApplySimulationLayerEngineSettings(layer);
        }
    }

    private int ApplyAudioReactiveLevelSeeding(int stepFactor)
    {
        if (!_audioReactiveEnabled || !_audioReactiveLevelSeedEnabled || string.IsNullOrWhiteSpace(_selectedAudioDeviceId))
        {
            return 0;
        }

        double gainScale = _audioReactiveEnergyGain / DefaultAudioReactiveEnergyGain;
        double normalizedLevel = Math.Clamp(_smoothedEnergy * gainScale, 0, 1);
        if (normalizedLevel < 0.002)
        {
            return 0;
        }

        int scaledMaxBursts = Math.Max(1, _audioReactiveLevelSeedMaxBursts * Math.Clamp(stepFactor, 1, 4));
        double shapedLevel = Math.Pow(normalizedLevel, 0.6);
        int burstCount = (int)Math.Ceiling(shapedLevel * scaledMaxBursts);
        burstCount = Math.Clamp(burstCount, 1, MaxAudioReactiveSeedBurstsPerStep);

        InjectAudioReactiveSeedBursts(burstCount);
        return burstCount;
    }

    private int ApplyAudioReactiveBeatSeeding()
    {
        long beatCount = _audioBeatDetector.BeatCount;
        if (!_audioReactiveEnabled || !_audioReactiveBeatSeedEnabled || string.IsNullOrWhiteSpace(_selectedAudioDeviceId))
        {
            _lastAudioReactiveBeatCount = beatCount;
            return 0;
        }

        long beatDelta = beatCount - _lastAudioReactiveBeatCount;
        if (beatDelta <= 0)
        {
            return 0;
        }

        _lastAudioReactiveBeatCount = beatCount;

        var now = DateTime.UtcNow;
        if (_lastAudioReactiveSeedUtc != DateTime.MinValue &&
            (now - _lastAudioReactiveSeedUtc).TotalMilliseconds < _audioReactiveSeedCooldownMs)
        {
            return 0;
        }

        int beatFactor = (int)Math.Clamp(beatDelta, 1, 8);
        int burstCount = Math.Clamp(beatFactor * _audioReactiveSeedsPerBeat, 1, MaxAudioReactiveSeedBurstsPerStep);
        InjectAudioReactiveSeedBursts(burstCount);
        _lastAudioReactiveSeedUtc = now;
        return burstCount;
    }

    private void InjectAudioReactiveSeedBursts(int burstCount)
    {
        var referenceEngine = GetReferenceSimulationEngine();
        int rows = referenceEngine.Rows;
        int cols = referenceEngine.Columns;
        if (rows <= 0 || cols <= 0 || burstCount <= 0)
        {
            return;
        }

        var mask = new bool[rows, cols];
        for (int i = 0; i < burstCount; i++)
        {
            int centerRow = _audioReactiveRandom.Next(rows);
            int centerCol = _audioReactiveRandom.Next(cols);
            InjectAudioReactivePattern(mask, centerRow, centerCol);
        }

        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            if (!layer.Enabled)
            {
                continue;
            }

            layer.Engine?.InjectFrame(mask);
        }
    }

    private void InjectAudioReactivePattern(bool[,] mask, int centerRow, int centerCol)
    {
        switch (_audioReactiveSeedPattern)
        {
            case AudioReactiveSeedPattern.RPentomino:
                PlacePatternWithRandomRotation(mask, centerRow, centerCol, RPentominoPattern);
                break;
            case AudioReactiveSeedPattern.RandomBurst:
                PlaceRandomBurstPattern(mask, centerRow, centerCol);
                break;
            case AudioReactiveSeedPattern.Glider:
            default:
                PlacePatternWithRandomRotation(mask, centerRow, centerCol, GliderPattern);
                break;
        }
    }

    private void PlacePatternWithRandomRotation(bool[,] mask, int centerRow, int centerCol, IReadOnlyList<(int rowOffset, int colOffset)> pattern)
    {
        int rotation = _audioReactiveRandom.Next(4);
        foreach (var (rowOffset, colOffset) in pattern)
        {
            int rotatedRow = rowOffset;
            int rotatedCol = colOffset;
            switch (rotation)
            {
                case 1:
                    rotatedRow = -colOffset;
                    rotatedCol = rowOffset;
                    break;
                case 2:
                    rotatedRow = -rowOffset;
                    rotatedCol = -colOffset;
                    break;
                case 3:
                    rotatedRow = colOffset;
                    rotatedCol = -rowOffset;
                    break;
            }

            SetMaskCell(mask, centerRow + rotatedRow, centerCol + rotatedCol);
        }
    }

    private void PlaceRandomBurstPattern(bool[,] mask, int centerRow, int centerCol)
    {
        for (int dr = -1; dr <= 1; dr++)
        {
            for (int dc = -1; dc <= 1; dc++)
            {
                if (_audioReactiveRandom.NextDouble() < 0.58)
                {
                    SetMaskCell(mask, centerRow + dr, centerCol + dc);
                }
            }
        }
    }

    private static void SetMaskCell(bool[,] mask, int row, int col)
    {
        int rows = mask.GetLength(0);
        int cols = mask.GetLength(1);
        if (row >= 0 && row < rows && col >= 0 && col < cols)
        {
            mask[row, col] = true;
        }
    }

    private bool CaptureSourceList(List<CaptureSource> sources, double animationTime)
    {
        bool removedAny = false;
        var removed = new List<CaptureSource>();

        foreach (var source in sources.ToList())
        {
            if (source.UsePinnedFrameForSmoke && source.LastFrame != null)
            {
                source.HasError = false;
                source.MissedFrames = 0;
                source.FirstFrameReceived = true;
                continue;
            }

            bool includeNativeSource = ShouldPreferNativeSourceFrame(source);

            if (source.Type == CaptureSource.SourceType.SimGroup)
            {
                source.LastFrame = null;
                source.HasError = false;
                source.MissedFrames = 0;
                continue;
            }

            if (source.Type == CaptureSource.SourceType.Group)
            {
                if (source.Children.Count > 0)
                {
                    removedAny |= CaptureSourceList(source.Children, animationTime);
                }

                var groupDownscaled = source.CompositeDownscaledBuffer;
                long groupCompositeStamp = BeginProfileStamp();
                var groupComposite = BuildCompositeFrame(source.Children, ref groupDownscaled, useEngineDimensions: false, animationTime);
                EndProfileStamp("capture_group_composite_ms", groupCompositeStamp);
                source.CompositeDownscaledBuffer = groupDownscaled;
                if (groupComposite != null)
                {
                    source.LastFrame = new SourceFrame(groupComposite.Downscaled, groupComposite.DownscaledWidth, groupComposite.DownscaledHeight,
                        null, groupComposite.DownscaledWidth, groupComposite.DownscaledHeight);
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
                    var referenceEngine = GetReferenceSimulationEngine();
                    long windowCaptureStamp = BeginProfileStamp();
                    var windowFrame = _windowCapture.CaptureFrame(source.Window, referenceEngine.Columns, referenceEngine.Rows, source.FitMode, includeSource: includeNativeSource);
                    EndProfileStamp("capture_window_frame_ms", windowCaptureStamp);
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
                    var referenceEngine = GetReferenceSimulationEngine();
                    long webcamCaptureStamp = BeginProfileStamp();
                    var webcamFrame = _webcamCapture.CaptureFrame(source.WebcamId, referenceEngine.Columns, referenceEngine.Rows, source.FitMode, includeSource: includeNativeSource);
                    EndProfileStamp("capture_webcam_frame_ms", webcamCaptureStamp);
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
                else if (source.Type == CaptureSource.SourceType.VideoSequence && source.VideoSequence != null)
                {
                    var referenceEngine = GetReferenceSimulationEngine();
                    long sequenceCaptureStamp = BeginProfileStamp();
                    var sequenceFrame = source.VideoSequence.CaptureFrame(referenceEngine.Columns, referenceEngine.Rows, source.FitMode, includeSource: includeNativeSource);
                    EndProfileStamp("capture_sequence_frame_ms", sequenceCaptureStamp);
                    if (sequenceFrame.HasValue)
                    {
                        var value = sequenceFrame.Value;
                        frame = new SourceFrame(value.OverlayDownscaled, value.DownscaledWidth, value.DownscaledHeight,
                            value.OverlaySource, value.SourceWidth, value.SourceHeight, value.FrameToken, value.FramePublishTimestamp);
                        source.UpdateFileDimensions(value.SourceWidth, value.SourceHeight);
                        source.HasError = false;
                        source.MissedFrames = 0;
                        if (!source.FirstFrameReceived)
                        {
                            source.FirstFrameReceived = true;
                            Logger.Info($"Video sequence frame acquired for {source.DisplayName}: {value.SourceWidth}x{value.SourceHeight}");
                        }
                    }
                }
                else if (source.Type == CaptureSource.SourceType.File && !string.IsNullOrWhiteSpace(source.FilePath))
                {
                    var referenceEngine = GetReferenceSimulationEngine();
                    long fileCaptureStamp = BeginProfileStamp();
                    var fileFrame = _fileCapture.CaptureFrame(source.FilePath, referenceEngine.Columns, referenceEngine.Rows, source.FitMode, includeSource: includeNativeSource);
                    EndProfileStamp("capture_file_frame_ms", fileCaptureStamp);
                    if (fileFrame.HasValue)
                    {
                        var value = fileFrame.Value;
                        frame = new SourceFrame(value.OverlayDownscaled, value.DownscaledWidth, value.DownscaledHeight,
                            value.OverlaySource, value.SourceWidth, value.SourceHeight, value.FrameToken, value.FramePublishTimestamp);
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

                if (source.Type == CaptureSource.SourceType.VideoSequence)
                {
                    if (source.VideoSequence?.State == FileCaptureService.FileCaptureState.Pending)
                    {
                        continue;
                    }

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

                if (source.Type == CaptureSource.SourceType.File)
                {
                    if (!string.IsNullOrWhiteSpace(source.FilePath) &&
                        _fileCapture.GetState(source.FilePath) == FileCaptureService.FileCaptureState.Pending)
                    {
                        continue;
                    }

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
            RecordSourceFreshnessMetrics(source, frame);
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

    private bool ShouldPreferNativeSourceFrame(CaptureSource source)
    {
        if (!_renderBackend.PrefersNativeSourceFrames)
        {
            return false;
        }

        if (source.Type != CaptureSource.SourceType.File &&
            source.Type != CaptureSource.SourceType.VideoSequence)
        {
            return false;
        }

        if (source.Type == CaptureSource.SourceType.VideoSequence)
        {
            return false;
        }

        if (source.Type == CaptureSource.SourceType.File &&
            !string.IsNullOrWhiteSpace(source.FilePath) &&
            _fileCapture.GetKind(source.FilePath) == FileCaptureService.FileSourceKind.Video)
        {
            // For file-backed video, exact/adapted decoder output is consistently
            // fresher than switching back to native frames and resampling on CPU.
            return false;
        }

        // Static file-backed images stay on the exact CPU-downscaled path so they
        // remain pixel-stable and avoid native-source redraw/flicker quirks during
        // hover-driven WPF redraw pressure.
        return false;
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
        UpdateChromeUi();
        UpdateFullscreenMenuItem();
        ResetFramePumpCadence(scheduleImmediate: true);
    }

    internal void EnterFullscreenForSmoke()
    {
        EnterFullscreen(applyConfig: false);
    }

    internal (bool ok, string detail) ValidateRenderLayoutForSmoke(bool fullscreenExpected)
    {
        if (RenderSurfaceHost == null)
        {
            return (false, "RenderSurfaceHost was null.");
        }

        double hostWidth = RenderSurfaceHost.ActualWidth;
        double hostHeight = RenderSurfaceHost.ActualHeight;
        if (hostWidth < 4 || hostHeight < 4)
        {
            return (false, $"Render host size was invalid ({hostWidth:0.0}x{hostHeight:0.0}).");
        }

        FrameworkElement? presentedElement = null;
        if (RenderSurfaceHost.Children.Count > 0)
        {
            presentedElement = RenderSurfaceHost.Children[0] as FrameworkElement;
        }
        else if (GameImage != null)
        {
            presentedElement = GameImage;
        }

        if (presentedElement == null)
        {
            return (false, "No presented render element was available.");
        }

        double elementWidth = presentedElement.ActualWidth;
        double elementHeight = presentedElement.ActualHeight;
        if (elementWidth < 4 || elementHeight < 4)
        {
            return (false, $"Presented element size was invalid ({elementWidth:0.0}x{elementHeight:0.0}).");
        }

        GeneralTransform transform = presentedElement.TransformToAncestor(RenderSurfaceHost);
        Rect elementBounds = transform.TransformBounds(new Rect(0, 0, elementWidth, elementHeight));

        double aspect = _displayHeight > 0
            ? Math.Max(0.01, _displayWidth / (double)_displayHeight)
            : Math.Max(0.01, _currentAspectRatio);
        double hostAspect = hostWidth / hostHeight;
        double expectedWidth;
        double expectedHeight;
        if (hostAspect > aspect)
        {
            expectedHeight = hostHeight;
            expectedWidth = hostHeight * aspect;
        }
        else
        {
            expectedWidth = hostWidth;
            expectedHeight = hostWidth / aspect;
        }

        double widthCoverage = elementBounds.Width / Math.Max(1.0, expectedWidth);
        double heightCoverage = elementBounds.Height / Math.Max(1.0, expectedHeight);
        double expectedLeft = (hostWidth - expectedWidth) * 0.5;
        double expectedTop = (hostHeight - expectedHeight) * 0.5;
        double offsetErrorX = Math.Abs(elementBounds.Left - expectedLeft);
        double offsetErrorY = Math.Abs(elementBounds.Top - expectedTop);

        bool sizeOk = widthCoverage >= 0.85 && heightCoverage >= 0.85;
        bool alignmentOk = offsetErrorX <= Math.Max(24.0, hostWidth * 0.05) &&
                           offsetErrorY <= Math.Max(24.0, hostHeight * 0.05);
        bool fullscreenOk = !fullscreenExpected || (_isFullscreen && WindowState == WindowState.Normal);

        string detail = $"host={hostWidth:0.0}x{hostHeight:0.0}, element={elementBounds.Width:0.0}x{elementBounds.Height:0.0}@({elementBounds.Left:0.0},{elementBounds.Top:0.0}), expected={expectedWidth:0.0}x{expectedHeight:0.0}@({expectedLeft:0.0},{expectedTop:0.0}), coverage={widthCoverage:0.00}/{heightCoverage:0.00}, fullscreen={_isFullscreen}.";
        return (fullscreenOk && sizeOk && alignmentOk, detail);
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
        UpdateChromeUi();
        SnapWindowToAspect(preserveHeight: true);
        SaveConfig();
        UpdateFullscreenMenuItem();
        ResetFramePumpCadence(scheduleImmediate: true);
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

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private void UpdateFullscreenMenuItem()
    {
        if (FullscreenMenuItem != null)
        {
            FullscreenMenuItem.IsChecked = _isFullscreen;
        }
    }

    private void ThresholdSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressThresholdControlEvents)
        {
            return;
        }

        _captureThresholdMin = ThresholdMinSlider?.Value ?? _captureThresholdMin;
        _captureThresholdMax = ThresholdMaxSlider?.Value ?? _captureThresholdMax;
        if (_captureThresholdMin > _captureThresholdMax)
        {
            (_captureThresholdMin, _captureThresholdMax) = (_captureThresholdMax, _captureThresholdMin);
            if (ThresholdMinSlider != null && Math.Abs(ThresholdMinSlider.Value - _captureThresholdMin) > 0.000001)
            {
                ThresholdMinSlider.Value = _captureThresholdMin;
            }
            if (ThresholdMaxSlider != null && Math.Abs(ThresholdMaxSlider.Value - _captureThresholdMax) > 0.000001)
            {
                ThresholdMaxSlider.Value = _captureThresholdMax;
            }
        }

        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            layer.ThresholdMin = _captureThresholdMin;
            layer.ThresholdMax = _captureThresholdMax;
        }

        RenderFrame();
        SaveConfig();
    }

    private void NoiseSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressThresholdControlEvents)
        {
            return;
        }

        _injectionNoise = Math.Clamp(e.NewValue, 0, 1);
        SaveConfig();
    }

    private void AnimationBpmSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _animationBpm = Math.Clamp(e.NewValue, 10, 300);
        if (AnimationBpmValueText != null)
        {
            AnimationBpmValueText.Text = $"{_animationBpm:F0}";
        }
        SaveConfig();
    }

    private void AnimationAudioSync_OnChecked(object sender, RoutedEventArgs e)
    {
        _animationAudioSyncEnabled = AnimationAudioSyncCheckBox?.IsChecked == true;
        if (AnimationBpmSlider != null)
        {
            AnimationBpmSlider.IsEnabled = !_animationAudioSyncEnabled;
        }
        SaveConfig();
    }

    private void InvertThresholdCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressThresholdControlEvents)
        {
            return;
        }

        _invertThreshold = InvertThresholdCheckBox?.IsChecked == true;
        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            layer.InvertThreshold = _invertThreshold;
        }
        RenderFrame();
        SaveConfig();
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

        bool insideWindow = value >= min && value <= max;
        return invert ? !insideWindow : insideWindow;
    }

    private static double MapIntensityThroughThresholdWindow(double value, double min, double max, bool invert)
    {
        value = Math.Clamp(value, 0, 1);
        min = Math.Clamp(min, 0, 1);
        max = Math.Clamp(max, 0, 1);
        if (min > max)
        {
            (min, max) = (max, min);
        }

        const double Epsilon = 1e-6;
        if (!invert)
        {
            if (max - min <= Epsilon)
            {
                return value >= max ? 1.0 : 0.0;
            }

            return Math.Clamp((value - min) / (max - min), 0, 1);
        }

        if (max - min <= Epsilon)
        {
            return value < min || value > max ? 1.0 : 0.0;
        }

        double lower = min <= Epsilon ? 0.0 : Math.Clamp((min - value) / min, 0, 1);
        double upper = (1.0 - max) <= Epsilon ? 0.0 : Math.Clamp((value - max) / (1.0 - max), 0, 1);
        return Math.Max(lower, upper);
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

    private void BlendModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetBlendModeFromMenuItem(sender, _blendMode, out var mode))
        {
            return;
        }

        _blendMode = mode;
        UpdateBlendModeMenuChecks();
        RenderFrame();
        SaveConfig();
    }

    private static bool TryGetBlendModeFromMenuItem(object sender, BlendMode fallback, out BlendMode mode)
    {
        if (sender is not MenuItem { Header: string header })
        {
            mode = fallback;
            return false;
        }

        return TryParseBlendMode(header, fallback, out mode);
    }

    private void UpdateBlendModeMenuChecks()
    {
        UpdateBlendModeMenuChecks(BlendModeMenu, _blendMode);
    }

    private static void UpdateBlendModeMenuChecks(MenuItem? menu, BlendMode selectedMode)
    {
        if (menu == null)
        {
            return;
        }

        foreach (var item in menu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Header is string header &&
                TryParseBlendMode(header, selectedMode, out var mode))
            {
                menuItem.IsCheckable = true;
                menuItem.IsChecked = mode == selectedMode;
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

    private enum SimulationInputFunction
    {
        Direct,
        Inverse
    }

    private enum SimulationLayerType
    {
        Life,
        PixelSort
    }

    private enum AudioReactiveSeedPattern
    {
        Glider,
        RPentomino,
        RandomBurst
    }

    private sealed class SimulationLayerState
    {
        public Guid Id { get; set; }
        public LayerEditorSimulationItemKind Kind { get; set; } = LayerEditorSimulationItemKind.Layer;
        public SimulationLayerType LayerType { get; set; } = SimulationLayerType.Life;
        public string Name { get; set; } = "Life Sim";
        public bool Enabled { get; set; } = true;
        public SimulationInputFunction InputFunction { get; set; } = SimulationInputFunction.Direct;
        public BlendMode BlendMode { get; set; } = BlendMode.Subtractive;
        public GameOfLifeEngine.InjectionMode InjectionMode { get; set; } = GameOfLifeEngine.InjectionMode.Threshold;
        public GameOfLifeEngine.LifeMode LifeMode { get; set; } = GameOfLifeEngine.LifeMode.NaiveGrayscale;
        public GameOfLifeEngine.BinningMode BinningMode { get; set; } = GameOfLifeEngine.BinningMode.Fill;
        public double InjectionNoise { get; set; }
        public double LifeOpacity { get; set; } = 1.0;
        public double RgbHueShiftDegrees { get; set; }
        public double RgbHueShiftSpeedDegreesPerSecond { get; set; }
        public double AudioFrequencyHueShiftDegrees { get; set; }
        public List<SimulationReactiveMapping> ReactiveMappings { get; set; } = new();
        public double ThresholdMin { get; set; } = 0.35;
        public double ThresholdMax { get; set; } = 0.75;
        public bool InvertThreshold { get; set; }
        public int PixelSortCellWidth { get; set; } = 12;
        public int PixelSortCellHeight { get; set; } = 8;
        public double EffectiveLifeOpacity { get; set; } = 1.0;
        public double EffectiveSimulationTargetFps { get; set; } = DefaultFps;
        public double ReactiveHueShiftDegrees { get; set; }
        public double EffectiveRgbHueShiftSpeedDegreesPerSecond { get; set; }
        public double EffectiveInjectionNoise { get; set; }
        public double EffectiveThresholdMin { get; set; } = 0.35;
        public double EffectiveThresholdMax { get; set; } = 0.75;
        public int EffectivePixelSortCellWidth { get; set; } = 12;
        public int EffectivePixelSortCellHeight { get; set; } = 8;
        public double TimeSinceLastStep { get; set; }
        public SimulationLayerState? Parent { get; set; }
        public List<SimulationLayerState> Children { get; } = new();
        public ISimulationBackend? Engine { get; set; }
        public byte[]? ColorBuffer { get; set; }
        public bool[,]? GrayMask { get; set; }
        public bool[,]? RedMask { get; set; }
        public bool[,]? GreenMask { get; set; }
        public bool[,]? BlueMask { get; set; }
        public bool IsGroup => Kind == LayerEditorSimulationItemKind.Group;
    }

    private sealed class SimulationLayerSpec
    {
        public Guid Id { get; init; }
        public LayerEditorSimulationItemKind Kind { get; init; } = LayerEditorSimulationItemKind.Layer;
        public SimulationLayerType LayerType { get; init; } = SimulationLayerType.Life;
        public string Name { get; init; } = "Life Sim";
        public bool Enabled { get; init; } = true;
        public SimulationInputFunction InputFunction { get; init; } = SimulationInputFunction.Direct;
        public BlendMode BlendMode { get; init; } = BlendMode.Subtractive;
        public GameOfLifeEngine.InjectionMode InjectionMode { get; init; } = GameOfLifeEngine.InjectionMode.Threshold;
        public GameOfLifeEngine.LifeMode LifeMode { get; init; } = GameOfLifeEngine.LifeMode.NaiveGrayscale;
        public GameOfLifeEngine.BinningMode BinningMode { get; init; } = GameOfLifeEngine.BinningMode.Fill;
        public double InjectionNoise { get; init; }
        public double LifeOpacity { get; init; } = 1.0;
        public double RgbHueShiftDegrees { get; init; }
        public double RgbHueShiftSpeedDegreesPerSecond { get; init; }
        public double AudioFrequencyHueShiftDegrees { get; init; }
        public List<SimulationReactiveMapping> ReactiveMappings { get; init; } = new();
        public double ThresholdMin { get; init; } = 0.35;
        public double ThresholdMax { get; init; } = 0.75;
        public bool InvertThreshold { get; init; }
        public int PixelSortCellWidth { get; init; } = 12;
        public int PixelSortCellHeight { get; init; } = 8;
        public List<SimulationLayerSpec> Children { get; init; } = new();
    }

    private static bool TryParseBlendMode(string? value, BlendMode fallback, out BlendMode mode)
    {
        mode = fallback;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (string.Equals(value, "Alpha", StringComparison.OrdinalIgnoreCase))
        {
            mode = BlendMode.Normal;
            return true;
        }

        if (Enum.TryParse<BlendMode>(value, true, out var parsed))
        {
            mode = parsed;
            return true;
        }

        return false;
    }

    private void RecordSourceFreshnessMetrics(CaptureSource source, SourceFrame frame)
    {
        if (frame.FrameToken == 0)
        {
            return;
        }

        bool isFreshFrame = frame.FrameToken != source.LastObservedFrameToken;
        source.LastObservedFrameToken = frame.FrameToken;

        string ratioMetricName = source.Type switch
        {
            CaptureSource.SourceType.File => "capture_file_fresh_frame_ratio",
            CaptureSource.SourceType.VideoSequence => "capture_sequence_fresh_frame_ratio",
            _ => string.Empty
        };

        string ageMetricName = source.Type switch
        {
            CaptureSource.SourceType.File => "capture_file_frame_age_ms",
            CaptureSource.SourceType.VideoSequence => "capture_sequence_frame_age_ms",
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(ratioMetricName))
        {
            _frameProfiler.RecordSample(ratioMetricName, isFreshFrame ? 1.0 : 0.0);
        }

        string sourceMetricSuffix = BuildFreshnessMetricSuffix(source);
        if (!string.IsNullOrEmpty(sourceMetricSuffix) && !string.IsNullOrEmpty(ratioMetricName))
        {
            _frameProfiler.RecordSample($"{ratioMetricName}_{sourceMetricSuffix}", isFreshFrame ? 1.0 : 0.0);
        }

        if (!string.IsNullOrEmpty(ageMetricName) && frame.FramePublishTimestamp > 0)
        {
            double ageMs = FrameProfiler.ElapsedMilliseconds(frame.FramePublishTimestamp, Stopwatch.GetTimestamp());
            _frameProfiler.RecordSample(ageMetricName, Math.Max(0.0, ageMs));
            if (!string.IsNullOrEmpty(sourceMetricSuffix))
            {
                _frameProfiler.RecordSample($"{ageMetricName}_{sourceMetricSuffix}", Math.Max(0.0, ageMs));
            }
        }
    }

    private static string BuildFreshnessMetricSuffix(CaptureSource source)
    {
        string raw = source.Type switch
        {
            CaptureSource.SourceType.File when !string.IsNullOrWhiteSpace(source.FilePath)
                => Path.GetFileName(source.FilePath),
            CaptureSource.SourceType.VideoSequence when source.VideoSequence != null
                => Path.GetFileName(source.VideoSequence.Paths.FirstOrDefault() ?? source.DisplayName),
            _ => source.DisplayName
        };

        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (builder.Length == 0 || builder[builder.Length - 1] != '_')
            {
                builder.Append('_');
            }

            if (builder.Length >= 48)
            {
                break;
            }
        }

        return builder.ToString().Trim('_');
    }

    private static BlendMode ParseBlendModeOrDefault(string? value, BlendMode fallback)
    {
        return TryParseBlendMode(value, fallback, out var mode) ? mode : fallback;
    }

    private static SimulationInputFunction ParseSimulationInputFunctionOrDefault(string? value, SimulationInputFunction fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<SimulationInputFunction>(value, true, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static GameOfLifeEngine.InjectionMode ParseSimulationInjectionModeOrDefault(string? value, GameOfLifeEngine.InjectionMode fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<GameOfLifeEngine.InjectionMode>(value, true, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static GameOfLifeEngine.LifeMode ParseSimulationLifeModeOrDefault(string? value, GameOfLifeEngine.LifeMode fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<GameOfLifeEngine.LifeMode>(value, true, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static GameOfLifeEngine.BinningMode ParseSimulationBinningModeOrDefault(string? value, GameOfLifeEngine.BinningMode fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<GameOfLifeEngine.BinningMode>(value, true, out var parsed))
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

    private static SimulationLayerType ParseSimulationLayerType(string? value)
    {
        return Enum.TryParse<SimulationLayerType>(value, true, out var parsed)
            ? parsed
            : SimulationLayerType.Life;
    }

    private LayerEditorSimulationLayer ToEditorSimulationLayer(SimulationLayerState layer)
    {
        var editorLayer = new LayerEditorSimulationLayer
        {
            Id = layer.Id,
            Kind = layer.Kind,
            LayerType = layer.LayerType == SimulationLayerType.PixelSort
                ? LayerEditorSimulationLayerType.PixelSort
                : LayerEditorSimulationLayerType.Life,
            Name = layer.Name,
            Enabled = layer.Enabled,
            InputFunction = layer.InputFunction.ToString(),
            BlendMode = layer.BlendMode.ToString(),
            InjectionMode = layer.InjectionMode.ToString(),
            LifeMode = layer.LifeMode.ToString(),
            BinningMode = layer.BinningMode.ToString(),
            InjectionNoise = layer.InjectionNoise,
            LifeOpacity = layer.LifeOpacity,
            RgbHueShiftDegrees = layer.RgbHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = layer.RgbHueShiftSpeedDegreesPerSecond,
            AudioFrequencyHueShiftDegrees = 0,
            ReactiveMappings = new ObservableCollection<LayerEditorSimulationReactiveMapping>(
                layer.ReactiveMappings.Select(mapping => new LayerEditorSimulationReactiveMapping
                {
                    Id = mapping.Id,
                    Input = mapping.Input.ToString(),
                    Output = mapping.Output.ToString(),
                    Amount = mapping.Amount,
                    ThresholdMin = mapping.ThresholdMin,
                    ThresholdMax = mapping.ThresholdMax
                })),
            ThresholdMin = layer.ThresholdMin,
            ThresholdMax = layer.ThresholdMax,
            InvertThreshold = layer.InvertThreshold,
            PixelSortCellWidth = layer.PixelSortCellWidth,
            PixelSortCellHeight = layer.PixelSortCellHeight
        };

        foreach (var child in layer.Children)
        {
            var editorChild = ToEditorSimulationLayer(child);
            editorChild.Parent = editorLayer;
            editorLayer.Children.Add(editorChild);
        }

        return editorLayer;
    }

    private LayerEditorSimulationLayer ToEditorSimulationLayer(SimulationLayerSpec layer)
    {
        var editorLayer = new LayerEditorSimulationLayer
        {
            Id = layer.Id,
            Kind = layer.Kind,
            LayerType = layer.LayerType == SimulationLayerType.PixelSort
                ? LayerEditorSimulationLayerType.PixelSort
                : LayerEditorSimulationLayerType.Life,
            Name = layer.Name,
            Enabled = layer.Enabled,
            InputFunction = layer.InputFunction.ToString(),
            BlendMode = layer.BlendMode.ToString(),
            InjectionMode = layer.InjectionMode.ToString(),
            LifeMode = layer.LifeMode.ToString(),
            BinningMode = layer.BinningMode.ToString(),
            InjectionNoise = layer.InjectionNoise,
            LifeOpacity = layer.LifeOpacity,
            RgbHueShiftDegrees = layer.RgbHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = layer.RgbHueShiftSpeedDegreesPerSecond,
            AudioFrequencyHueShiftDegrees = 0,
            ReactiveMappings = new ObservableCollection<LayerEditorSimulationReactiveMapping>(
                layer.ReactiveMappings.Select(mapping => new LayerEditorSimulationReactiveMapping
                {
                    Id = mapping.Id,
                    Input = mapping.Input.ToString(),
                    Output = mapping.Output.ToString(),
                    Amount = mapping.Amount,
                    ThresholdMin = mapping.ThresholdMin,
                    ThresholdMax = mapping.ThresholdMax
                })),
            ThresholdMin = layer.ThresholdMin,
            ThresholdMax = layer.ThresholdMax,
            InvertThreshold = layer.InvertThreshold,
            PixelSortCellWidth = layer.PixelSortCellWidth,
            PixelSortCellHeight = layer.PixelSortCellHeight
        };

        foreach (var child in layer.Children)
        {
            var editorChild = ToEditorSimulationLayer(child);
            editorChild.Parent = editorLayer;
            editorLayer.Children.Add(editorChild);
        }

        return editorLayer;
    }

    private void EnsureSimulationLayersInitialized()
    {
        if (_simulationLayers.Count > 0)
        {
            return;
        }

        ApplySimulationLayerSpecs(BuildDefaultSimulationLayerSpecs());
    }

    private ISimulationBackend GetReferenceSimulationEngine()
    {
        var firstLayer = EnumerateSimulationLeafLayers(_simulationLayers).FirstOrDefault();
        if (firstLayer?.Engine != null)
        {
            return firstLayer.Engine;
        }

        return _engine;
    }

    private void ConfigureSimulationLayerEngines(int rows, int depth, double aspectRatio, bool randomize)
    {
        if (_simulationLayers.Count == 0)
        {
            ConfigureSimulationEngine(_engine, rows, depth, aspectRatio, randomize);
            return;
        }

        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            if (layer.Engine == null)
            {
                continue;
            }

            ConfigureSimulationEngine(layer.Engine, rows, depth, aspectRatio, randomize);
            ApplySimulationLayerEngineSettings(layer);
        }
    }

    private ISimulationBackend CreateConfiguredSimulationEngine(SimulationLayerType layerType, bool randomize)
    {
        ISimulationBackend engine = layerType == SimulationLayerType.PixelSort
            ? new GpuPixelSortBackend()
            : new GpuSimulationBackend();
        ConfigureSimulationEngine(engine, _configuredRows, _configuredDepth, _currentAspectRatio, randomize);
        return engine;
    }

    private void ConfigureSimulationEngine(ISimulationBackend engine, int rows, int depth, double aspectRatio, bool randomize)
    {
        double resolvedAspect = aspectRatio > 0.01 ? aspectRatio : DefaultAspectRatio;
        engine.Configure(rows, depth, resolvedAspect);
        if (randomize)
        {
            engine.Randomize();
        }
    }

    private static void ApplySimulationLayerEngineSettings(SimulationLayerState layer)
    {
        if (layer.IsGroup || layer.Engine == null)
        {
            return;
        }

        if (layer.LayerType == SimulationLayerType.PixelSort)
        {
            if (layer.Engine is GpuPixelSortBackend pixelSortBackend)
            {
                pixelSortBackend.SetCellSize(layer.EffectivePixelSortCellWidth, layer.EffectivePixelSortCellHeight);
            }
            return;
        }

        if (layer.Engine.Mode != layer.LifeMode)
        {
            layer.Engine.SetMode(layer.LifeMode);
        }

        if (layer.Engine.BinMode != layer.BinningMode)
        {
            layer.Engine.SetBinningMode(layer.BinningMode);
        }

        if (layer.Engine.InjectMode != layer.InjectionMode)
        {
            layer.Engine.SetInjectionMode(layer.InjectionMode);
        }
    }

    private static IEnumerable<SimulationLayerState> EnumerateSimulationLayers(IEnumerable<SimulationLayerState> roots)
    {
        foreach (var layer in roots)
        {
            yield return layer;
            foreach (var child in EnumerateSimulationLayers(layer.Children))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<SimulationLayerState> EnumerateSimulationLeafLayers(IEnumerable<SimulationLayerState> roots)
    {
        foreach (var layer in roots)
        {
            if (!layer.IsGroup)
            {
                yield return layer;
            }

            foreach (var child in EnumerateSimulationLeafLayers(layer.Children))
            {
                yield return child;
            }
        }
    }

    private static List<SimulationLayerSpec> BuildDefaultSimulationLayerSpecs()
    {
        return new List<SimulationLayerSpec>
        {
            new()
            {
                Id = Guid.NewGuid(),
                LayerType = SimulationLayerType.Life,
                Name = "Life Sim",
                Enabled = true,
                InputFunction = SimulationInputFunction.Direct,
                BlendMode = BlendMode.Additive,
                InjectionMode = GameOfLifeEngine.InjectionMode.Threshold,
                RgbHueShiftDegrees = 0,
                RgbHueShiftSpeedDegreesPerSecond = 0,
                AudioFrequencyHueShiftDegrees = 0,
                ThresholdMin = 0.35,
                ThresholdMax = 0.75,
                InvertThreshold = false,
                PixelSortCellWidth = 12,
                PixelSortCellHeight = 8
            }
        };
    }

    private static (double min, double max, bool invert) NormalizeLayerThresholds(
        double min,
        double max,
        bool invert,
        double fallbackMin = 0.35,
        double fallbackMax = 0.75,
        bool fallbackInvert = false)
    {
        if (double.IsNaN(min) || double.IsInfinity(min))
        {
            min = fallbackMin;
        }
        if (double.IsNaN(max) || double.IsInfinity(max))
        {
            max = fallbackMax;
        }

        min = Math.Clamp(min, 0, 1);
        max = Math.Clamp(max, 0, 1);
        if (min > max)
        {
            (min, max) = (max, min);
        }

        return (min, max, invert);
    }

    private static SimulationReactiveInput ParseReactiveInputOrDefault(string? value, SimulationReactiveInput fallback)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Enum.TryParse<SimulationReactiveInput>(value, true, out var parsed))
        {
            return parsed;
        }

        return fallback;
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

    private static List<SimulationReactiveMapping> NormalizeReactiveMappings(
        IEnumerable<LayerEditorSimulationReactiveMapping>? mappings,
        double legacyAudioFrequencyHueShiftDegrees)
    {
        var normalized = new List<SimulationReactiveMapping>();
        var seenIds = new HashSet<Guid>();

        if (mappings != null)
        {
            foreach (var mapping in mappings)
            {
                Guid id = mapping.Id;
                if (id == Guid.Empty || !seenIds.Add(id))
                {
                    do
                    {
                        id = Guid.NewGuid();
                    } while (!seenIds.Add(id));
                }

                var output = ParseReactiveOutputOrDefault(mapping.Output, SimulationReactiveOutput.Opacity);
                normalized.Add(new SimulationReactiveMapping
                {
                    Id = id,
                    Input = ParseReactiveInputOrDefault(mapping.Input, SimulationReactiveInput.Level),
                    Output = output,
                    Amount = SimulationReactivity.ClampAmount(output, mapping.Amount),
                    ThresholdMin = Math.Clamp(mapping.ThresholdMin, 0, 1),
                    ThresholdMax = Math.Clamp(mapping.ThresholdMax, 0, 1)
                });
            }
        }

        MigrateLegacyReactiveHueMapping(normalized, legacyAudioFrequencyHueShiftDegrees);
        return normalized;
    }

    private static List<SimulationReactiveMapping> NormalizeReactiveMappings(
        IEnumerable<AppConfig.ReactiveMappingConfig>? mappings,
        double legacyAudioFrequencyHueShiftDegrees)
    {
        var normalized = new List<SimulationReactiveMapping>();
        var seenIds = new HashSet<Guid>();

        if (mappings != null)
        {
            foreach (var mapping in mappings)
            {
                Guid id = mapping.Id;
                if (id == Guid.Empty || !seenIds.Add(id))
                {
                    do
                    {
                        id = Guid.NewGuid();
                    } while (!seenIds.Add(id));
                }

                var output = ParseReactiveOutputOrDefault(mapping.Output, SimulationReactiveOutput.Opacity);
                normalized.Add(new SimulationReactiveMapping
                {
                    Id = id,
                    Input = ParseReactiveInputOrDefault(mapping.Input, SimulationReactiveInput.Level),
                    Output = output,
                    Amount = SimulationReactivity.ClampAmount(output, mapping.Amount),
                    ThresholdMin = Math.Clamp(mapping.ThresholdMin, 0, 1),
                    ThresholdMax = Math.Clamp(mapping.ThresholdMax, 0, 1)
                });
            }
        }

        MigrateLegacyReactiveHueMapping(normalized, legacyAudioFrequencyHueShiftDegrees);
        return normalized;
    }

    private static void MigrateLegacyReactiveHueMapping(List<SimulationReactiveMapping> mappings, double legacyAudioFrequencyHueShiftDegrees)
    {
        if (legacyAudioFrequencyHueShiftDegrees <= 0.001)
        {
            return;
        }

        if (mappings.Any(mapping => mapping.Output == SimulationReactiveOutput.HueShift))
        {
            return;
        }

        mappings.Add(new SimulationReactiveMapping
        {
            Id = Guid.NewGuid(),
            Input = SimulationReactiveInput.Frequency,
            Output = SimulationReactiveOutput.HueShift,
            Amount = SimulationReactivity.ClampAmount(SimulationReactiveOutput.HueShift, legacyAudioFrequencyHueShiftDegrees),
            ThresholdMin = 0,
            ThresholdMax = 1
        });
    }

    private static bool ReactiveMappingsEqual(IReadOnlyList<SimulationReactiveMapping> left, IReadOnlyList<SimulationReactiveMapping> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i].Id != right[i].Id ||
                left[i].Input != right[i].Input ||
                left[i].Output != right[i].Output ||
                Math.Abs(left[i].Amount - right[i].Amount) > 0.000001 ||
                Math.Abs(left[i].ThresholdMin - right[i].ThresholdMin) > 0.000001 ||
                Math.Abs(left[i].ThresholdMax - right[i].ThresholdMax) > 0.000001)
            {
                return false;
            }
        }

        return true;
    }

    private static List<SimulationReactiveMapping> CloneReactiveMappings(IEnumerable<SimulationReactiveMapping> mappings)
    {
        return mappings.Select(mapping => mapping.Clone()).ToList();
    }

    private static bool HasReactiveMapping(
        IEnumerable<SimulationReactiveMapping> mappings,
        SimulationReactiveInput input,
        SimulationReactiveOutput output)
    {
        return mappings.Any(mapping => mapping.Input == input && mapping.Output == output);
    }

    private static void AddReactiveMappingIfMissing(
        List<SimulationReactiveMapping> mappings,
        SimulationReactiveInput input,
        SimulationReactiveOutput output,
        double amount)
    {
        if (HasReactiveMapping(mappings, input, output))
        {
            return;
        }

        double normalizedAmount = SimulationReactivity.ClampAmount(output, amount);
        if (normalizedAmount <= 0.000001)
        {
            return;
        }

        mappings.Add(new SimulationReactiveMapping
        {
            Id = Guid.NewGuid(),
            Input = input,
            Output = output,
            Amount = normalizedAmount
        });
    }

    private bool MigrateLegacyGlobalReactiveMappings(List<SimulationLayerSpec> specs)
    {
        bool migrateFramerate = _audioReactiveLevelToFpsEnabled;
        bool migrateOpacity = _audioReactiveLevelToLifeOpacityEnabled;
        if (!migrateFramerate && !migrateOpacity)
        {
            return false;
        }

        double framerateAmount = 1.0 - Math.Clamp(_audioReactiveFpsMinPercent, 0, 1);
        double opacityAmount = 1.0 - Math.Clamp(_audioReactiveLifeOpacityMinScalar, 0, 1);
        bool changed = false;

        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var mappings = CloneReactiveMappings(spec.ReactiveMappings);
            int beforeCount = mappings.Count;

            if (migrateFramerate)
            {
                AddReactiveMappingIfMissing(mappings, SimulationReactiveInput.Level, SimulationReactiveOutput.Framerate, framerateAmount);
            }

            if (migrateOpacity)
            {
                AddReactiveMappingIfMissing(mappings, SimulationReactiveInput.Level, SimulationReactiveOutput.Opacity, opacityAmount);
            }

            if (mappings.Count != beforeCount)
            {
                changed = true;
                specs[i] = new SimulationLayerSpec
                {
                    Id = spec.Id,
                    Name = spec.Name,
                    Enabled = spec.Enabled,
                    InputFunction = spec.InputFunction,
                    BlendMode = spec.BlendMode,
                    InjectionMode = spec.InjectionMode,
                    LifeMode = spec.LifeMode,
                    BinningMode = spec.BinningMode,
                    InjectionNoise = spec.InjectionNoise,
                    LifeOpacity = spec.LifeOpacity,
                    RgbHueShiftDegrees = spec.RgbHueShiftDegrees,
                    RgbHueShiftSpeedDegreesPerSecond = spec.RgbHueShiftSpeedDegreesPerSecond,
                    AudioFrequencyHueShiftDegrees = spec.AudioFrequencyHueShiftDegrees,
                    ReactiveMappings = mappings,
                    ThresholdMin = spec.ThresholdMin,
                    ThresholdMax = spec.ThresholdMax,
                    InvertThreshold = spec.InvertThreshold
                };
            }
        }

        if (migrateFramerate)
        {
            _audioReactiveLevelToFpsEnabled = false;
        }

        if (migrateOpacity)
        {
            _audioReactiveLevelToLifeOpacityEnabled = false;
        }

        Logger.Info(
            $"Migrated legacy global audio-reactive controls to per-layer mappings. " +
            $"Framerate={migrateFramerate}, Opacity={migrateOpacity}, Layers={specs.Count}, AddedMappings={changed}.");
        return true;
    }

    private static string NormalizeSimulationLayerName(
        string? value,
        int index,
        LayerEditorSimulationItemKind kind = LayerEditorSimulationItemKind.Layer,
        SimulationLayerType layerType = SimulationLayerType.Life)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return kind == LayerEditorSimulationItemKind.Group
            ? $"Sim Group {index + 1}"
            : layerType == SimulationLayerType.PixelSort
                ? $"Pixel Sort {index + 1}"
                : $"Life Sim {index + 1}";
    }

    private List<SimulationLayerSpec> NormalizeSimulationLayerSpecs(IReadOnlyList<LayerEditorSimulationLayer>? simulationLayers, bool fallbackToDefault = true)
    {
        var normalized = new List<SimulationLayerSpec>();
        var seenIds = new HashSet<Guid>();

        if (simulationLayers != null)
        {
            for (int i = 0; i < simulationLayers.Count; i++)
            {
                normalized.Add(NormalizeSimulationLayerSpec(simulationLayers[i], i, seenIds));
            }
        }

        if (normalized.Count == 0 && fallbackToDefault)
        {
            return BuildDefaultSimulationLayerSpecs();
        }

        return normalized;
    }

    private static List<SimulationLayerSpec> FlattenSourceSimulationLayerSpecs(IEnumerable<SimulationLayerSpec> specs)
    {
        var flattened = new List<SimulationLayerSpec>();
        foreach (var spec in specs)
        {
            FlattenSourceSimulationLayerSpec(spec, flattened, ancestorEnabled: true);
        }

        return flattened;
    }

    private static void FlattenSourceSimulationLayerSpec(SimulationLayerSpec spec, ICollection<SimulationLayerSpec> target, bool ancestorEnabled)
    {
        bool enabled = ancestorEnabled && spec.Enabled;
        if (spec.Kind == LayerEditorSimulationItemKind.Group)
        {
            foreach (var child in spec.Children)
            {
                FlattenSourceSimulationLayerSpec(child, target, enabled);
            }

            return;
        }

        target.Add(new SimulationLayerSpec
        {
            Id = spec.Id,
            Kind = LayerEditorSimulationItemKind.Layer,
            LayerType = spec.LayerType,
            Name = spec.Name,
            Enabled = enabled,
            InputFunction = spec.InputFunction,
            BlendMode = spec.BlendMode,
            InjectionMode = spec.InjectionMode,
            LifeMode = spec.LifeMode,
            BinningMode = spec.BinningMode,
            InjectionNoise = spec.InjectionNoise,
            LifeOpacity = spec.LifeOpacity,
            RgbHueShiftDegrees = spec.RgbHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = spec.RgbHueShiftSpeedDegreesPerSecond,
            AudioFrequencyHueShiftDegrees = spec.AudioFrequencyHueShiftDegrees,
            ReactiveMappings = spec.ReactiveMappings.Select(mapping => new SimulationReactiveMapping
            {
                Id = mapping.Id,
                Input = mapping.Input,
                Output = mapping.Output,
                Amount = mapping.Amount,
                ThresholdMin = mapping.ThresholdMin,
                ThresholdMax = mapping.ThresholdMax
            }).ToList(),
            ThresholdMin = spec.ThresholdMin,
            ThresholdMax = spec.ThresholdMax,
            InvertThreshold = spec.InvertThreshold,
            PixelSortCellWidth = spec.PixelSortCellWidth,
            PixelSortCellHeight = spec.PixelSortCellHeight
        });
    }

    private List<SimulationLayerSpec> NormalizeSimulationLayerSpecs(IReadOnlyList<AppConfig.SimulationLayerConfig>? simulationLayers, bool fallbackToDefault = true)
    {
        var normalized = new List<SimulationLayerSpec>();
        var seenIds = new HashSet<Guid>();

        if (simulationLayers != null)
        {
            for (int i = 0; i < simulationLayers.Count; i++)
            {
                normalized.Add(NormalizeSimulationLayerSpec(simulationLayers[i], i, seenIds));
            }
        }

        if (normalized.Count == 0 && fallbackToDefault)
        {
            return BuildDefaultSimulationLayerSpecs();
        }

        return normalized;
    }

    private SimulationLayerSpec NormalizeSimulationLayerSpec(LayerEditorSimulationLayer layer, int index, ISet<Guid> seenIds)
    {
        Guid id = layer.Id;
        if (id == Guid.Empty || !seenIds.Add(id))
        {
            do
            {
                id = Guid.NewGuid();
            } while (!seenIds.Add(id));
        }

        if (layer.IsGroup)
        {
            return new SimulationLayerSpec
            {
                Id = id,
                Kind = LayerEditorSimulationItemKind.Group,
                Name = NormalizeSimulationLayerName(layer.Name, index, LayerEditorSimulationItemKind.Group),
                Enabled = layer.Enabled,
                Children = layer.Children.Select((child, childIndex) => NormalizeSimulationLayerSpec(child, childIndex, seenIds)).ToList()
            };
        }

        var layerType = layer.LayerType == LayerEditorSimulationLayerType.PixelSort
            ? SimulationLayerType.PixelSort
            : SimulationLayerType.Life;
        var inputFunction = ParseSimulationInputFunctionOrDefault(layer.InputFunction, SimulationInputFunction.Direct);
        var defaultBlend = inputFunction == SimulationInputFunction.Inverse ? BlendMode.Subtractive : BlendMode.Additive;
        var blendMode = ParseBlendModeOrDefault(layer.BlendMode, defaultBlend);
        var injectionMode = ParseSimulationInjectionModeOrDefault(layer.InjectionMode, _injectionMode);
        var lifeMode = ParseSimulationLifeModeOrDefault(layer.LifeMode, _lifeMode);
        var binningMode = ParseSimulationBinningModeOrDefault(layer.BinningMode, _binningMode);
        var (thresholdMin, thresholdMax, invertThreshold) =
            NormalizeLayerThresholds(layer.ThresholdMin, layer.ThresholdMax, layer.InvertThreshold);
        return new SimulationLayerSpec
        {
            Id = id,
            Kind = LayerEditorSimulationItemKind.Layer,
            LayerType = layerType,
            Name = NormalizeSimulationLayerName(layer.Name, index, layerType: layerType),
            Enabled = layer.Enabled,
            InputFunction = inputFunction,
            BlendMode = blendMode,
            InjectionMode = injectionMode,
            LifeMode = lifeMode,
            BinningMode = binningMode,
            InjectionNoise = Math.Clamp(layer.InjectionNoise, 0, 1),
            LifeOpacity = Math.Clamp(layer.LifeOpacity, 0, 1),
            RgbHueShiftDegrees = NormalizeHueDegrees(layer.RgbHueShiftDegrees),
            RgbHueShiftSpeedDegreesPerSecond = Math.Clamp(layer.RgbHueShiftSpeedDegreesPerSecond, -MaxRgbHueShiftSpeedDegreesPerSecond, MaxRgbHueShiftSpeedDegreesPerSecond),
            AudioFrequencyHueShiftDegrees = 0,
            ReactiveMappings = NormalizeReactiveMappings(layer.ReactiveMappings, 0),
            ThresholdMin = thresholdMin,
            ThresholdMax = thresholdMax,
            InvertThreshold = invertThreshold,
            PixelSortCellWidth = Math.Clamp(layer.PixelSortCellWidth, 1, 4096),
            PixelSortCellHeight = Math.Clamp(layer.PixelSortCellHeight, 1, 4096)
        };
    }

    private SimulationLayerSpec NormalizeSimulationLayerSpec(AppConfig.SimulationLayerConfig layer, int index, ISet<Guid> seenIds)
    {
        Guid id = layer.Id;
        if (id == Guid.Empty || !seenIds.Add(id))
        {
            do
            {
                id = Guid.NewGuid();
            } while (!seenIds.Add(id));
        }

        LayerEditorSimulationItemKind kind = ParseSimulationItemKind(layer.Kind);
        if (kind == LayerEditorSimulationItemKind.Group)
        {
            return new SimulationLayerSpec
            {
                Id = id,
                Kind = LayerEditorSimulationItemKind.Group,
                Name = NormalizeSimulationLayerName(layer.Name, index, LayerEditorSimulationItemKind.Group),
                Enabled = layer.Enabled,
                Children = layer.Children.Select((child, childIndex) => NormalizeSimulationLayerSpec(child, childIndex, seenIds)).ToList()
            };
        }

        var layerType = ParseSimulationLayerType(layer.LayerType);
        var inputFunction = ParseSimulationInputFunctionOrDefault(layer.InputFunction, SimulationInputFunction.Direct);
        var defaultBlend = inputFunction == SimulationInputFunction.Inverse ? BlendMode.Subtractive : BlendMode.Additive;
        var blendMode = ParseBlendModeOrDefault(layer.BlendMode, defaultBlend);
        var injectionMode = ParseSimulationInjectionModeOrDefault(layer.InjectionMode, _injectionMode);
        var lifeMode = ParseSimulationLifeModeOrDefault(layer.LifeMode, _lifeMode);
        var binningMode = ParseSimulationBinningModeOrDefault(layer.BinningMode, _binningMode);
        var (thresholdMin, thresholdMax, invertThreshold) =
            NormalizeLayerThresholds(layer.ThresholdMin, layer.ThresholdMax, layer.InvertThreshold, _captureThresholdMin, _captureThresholdMax, _invertThreshold);
        int pixelSortCellWidth = Math.Clamp(layer.PixelSortCellWidth > 0 ? layer.PixelSortCellWidth : layer.PixelSortGridColumns, 1, 4096);
        int pixelSortCellHeight = Math.Clamp(layer.PixelSortCellHeight > 0 ? layer.PixelSortCellHeight : layer.PixelSortGridRows, 1, 4096);
        return new SimulationLayerSpec
        {
            Id = id,
            Kind = LayerEditorSimulationItemKind.Layer,
            LayerType = layerType,
            Name = NormalizeSimulationLayerName(layer.Name, index, layerType: layerType),
            Enabled = layer.Enabled,
            InputFunction = inputFunction,
            BlendMode = blendMode,
            InjectionMode = injectionMode,
            LifeMode = lifeMode,
            BinningMode = binningMode,
            InjectionNoise = Math.Clamp(layer.InjectionNoise, 0, 1),
            LifeOpacity = Math.Clamp(layer.LifeOpacity, 0, 1),
            RgbHueShiftDegrees = NormalizeHueDegrees(layer.RgbHueShiftDegrees != 0 ? layer.RgbHueShiftDegrees : _rgbHueShiftDegrees),
            RgbHueShiftSpeedDegreesPerSecond = Math.Clamp(layer.RgbHueShiftSpeedDegreesPerSecond != 0 ? layer.RgbHueShiftSpeedDegreesPerSecond : _rgbHueShiftSpeedDegreesPerSecond, -MaxRgbHueShiftSpeedDegreesPerSecond, MaxRgbHueShiftSpeedDegreesPerSecond),
            AudioFrequencyHueShiftDegrees = 0,
            ReactiveMappings = NormalizeReactiveMappings(layer.ReactiveMappings, layer.AudioFrequencyHueShiftDegrees),
            ThresholdMin = thresholdMin,
            ThresholdMax = thresholdMax,
            InvertThreshold = invertThreshold,
            PixelSortCellWidth = pixelSortCellWidth,
            PixelSortCellHeight = pixelSortCellHeight
        };
    }

    private static List<SimulationLayerSpec> BuildLegacySimulationLayerSpecs(
        bool positiveLayerEnabled,
        string? positiveLayerBlendMode,
        bool negativeLayerEnabled,
        string? negativeLayerBlendMode,
        IReadOnlyList<string>? simulationLayerOrder,
        GameOfLifeEngine.InjectionMode injectionMode,
        GameOfLifeEngine.LifeMode lifeMode,
        GameOfLifeEngine.BinningMode binningMode,
        double injectionNoise,
        double globalHueShiftDegrees,
        double globalHueShiftSpeedDegreesPerSecond,
        double thresholdMin,
        double thresholdMax,
        bool invertThreshold)
    {
        var (normalizedThresholdMin, normalizedThresholdMax, normalizedInvertThreshold) =
            NormalizeLayerThresholds(thresholdMin, thresholdMax, invertThreshold);
        var positive = new SimulationLayerSpec
        {
            Id = Guid.NewGuid(),
            LayerType = SimulationLayerType.Life,
            Name = "Life Sim",
            Enabled = positiveLayerEnabled,
            InputFunction = SimulationInputFunction.Direct,
            BlendMode = ParseBlendModeOrDefault(positiveLayerBlendMode, BlendMode.Additive),
            InjectionMode = injectionMode,
            LifeMode = lifeMode,
            BinningMode = binningMode,
            InjectionNoise = injectionNoise,
            LifeOpacity = 1.0,
            RgbHueShiftDegrees = globalHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = globalHueShiftSpeedDegreesPerSecond,
            AudioFrequencyHueShiftDegrees = 0,
            ReactiveMappings = new List<SimulationReactiveMapping>(),
            ThresholdMin = normalizedThresholdMin,
            ThresholdMax = normalizedThresholdMax,
            InvertThreshold = normalizedInvertThreshold,
            PixelSortCellWidth = 12,
            PixelSortCellHeight = 8
        };
        var negative = new SimulationLayerSpec
        {
            Id = Guid.NewGuid(),
            LayerType = SimulationLayerType.Life,
            Name = "Negative",
            Enabled = negativeLayerEnabled,
            InputFunction = SimulationInputFunction.Inverse,
            BlendMode = ParseBlendModeOrDefault(negativeLayerBlendMode, BlendMode.Subtractive),
            InjectionMode = injectionMode,
            LifeMode = lifeMode,
            BinningMode = binningMode,
            InjectionNoise = injectionNoise,
            LifeOpacity = 1.0,
            RgbHueShiftDegrees = globalHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = globalHueShiftSpeedDegreesPerSecond,
            AudioFrequencyHueShiftDegrees = 0,
            ReactiveMappings = new List<SimulationReactiveMapping>(),
            ThresholdMin = normalizedThresholdMin,
            ThresholdMax = normalizedThresholdMax,
            InvertThreshold = normalizedInvertThreshold,
            PixelSortCellWidth = 12,
            PixelSortCellHeight = 8
        };

        var list = new List<SimulationLayerSpec>(2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (simulationLayerOrder != null)
        {
            foreach (var item in simulationLayerOrder)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }

                if (item.Equals("Positive", StringComparison.OrdinalIgnoreCase) && seen.Add("Positive"))
                {
                    list.Add(positive);
                }
                else if (item.Equals("Negative", StringComparison.OrdinalIgnoreCase) && seen.Add("Negative"))
                {
                    list.Add(negative);
                }
            }
        }

        if (seen.Add("Positive"))
        {
            list.Add(positive);
        }
        if (seen.Add("Negative"))
        {
            list.Add(negative);
        }

        return list;
    }

    private bool ApplySimulationLayerSpecs(IReadOnlyList<SimulationLayerSpec> specs)
    {
        var existingById = EnumerateSimulationLayers(_simulationLayers).ToDictionary(layer => layer.Id);
        Guid? currentPrimaryId = EnumerateSimulationLeafLayers(_simulationLayers)
            .FirstOrDefault(layer => ReferenceEquals(layer.Engine, _engine))?.Id;
        bool primaryAssigned = false;
        var nextLayers = BuildSimulationLayerStates(specs, null, existingById, currentPrimaryId, ref primaryAssigned);
        EnsurePrimarySimulationEngineAssigned(nextLayers, ref primaryAssigned);

        foreach (var orphan in existingById.Values)
        {
            if (!orphan.IsGroup)
            {
                RetireSimulationEngine(orphan.Engine);
            }
        }

        _simulationLayers.Clear();
        _simulationLayers.AddRange(nextLayers);
        return true;
    }

    private List<SimulationLayerState> BuildSimulationLayerStates(
        IReadOnlyList<SimulationLayerSpec> specs,
        SimulationLayerState? parent,
        IDictionary<Guid, SimulationLayerState> existingById,
        Guid? currentPrimaryId,
        ref bool primaryAssigned)
    {
        var list = new List<SimulationLayerState>(specs.Count);
        for (int index = 0; index < specs.Count; index++)
        {
            list.Add(BuildSimulationLayerState(specs[index], index, parent, existingById, currentPrimaryId, ref primaryAssigned));
        }

        return list;
    }

    private SimulationLayerState BuildSimulationLayerState(
        SimulationLayerSpec spec,
        int index,
        SimulationLayerState? parent,
        IDictionary<Guid, SimulationLayerState> existingById,
        Guid? currentPrimaryId,
        ref bool primaryAssigned)
    {
        SimulationLayerState? existing = null;
        if (existingById.TryGetValue(spec.Id, out var found))
        {
            existing = found;
            existingById.Remove(spec.Id);
        }

        if (existing != null && existing.Kind != spec.Kind)
        {
            if (!existing.IsGroup)
            {
                RetireSimulationEngine(existing.Engine);
            }
            existing = null;
        }

        var layer = existing ?? new SimulationLayerState { Id = spec.Id };
        layer.Kind = spec.Kind;
        layer.Parent = parent;
        layer.LayerType = spec.LayerType;
        layer.Name = spec.Name;
        layer.Enabled = spec.Enabled;

        if (spec.Kind == LayerEditorSimulationItemKind.Group)
        {
            layer.Engine = null;
            layer.ColorBuffer = null;
            layer.GrayMask = null;
            layer.RedMask = null;
            layer.GreenMask = null;
            layer.BlueMask = null;
            layer.Children.Clear();
            foreach (var child in BuildSimulationLayerStates(spec.Children, layer, existingById, currentPrimaryId, ref primaryAssigned))
            {
                layer.Children.Add(child);
            }
            return layer;
        }

        bool needsEngineReplacement =
            layer.Engine == null ||
            (spec.LayerType == SimulationLayerType.PixelSort && layer.Engine is not GpuPixelSortBackend) ||
            (spec.LayerType == SimulationLayerType.Life && layer.Engine is not GpuSimulationBackend);

        if (needsEngineReplacement && layer.Engine != null)
        {
            RetireSimulationEngine(layer.Engine);
            layer.Engine = null;
        }

        if (layer.Engine == null)
        {
            if (spec.LayerType == SimulationLayerType.Life && !primaryAssigned && currentPrimaryId == spec.Id)
            {
                layer.Engine = _engine;
                primaryAssigned = true;
                ConfigureSimulationEngine(layer.Engine, _configuredRows, _configuredDepth, _currentAspectRatio, randomize: true);
            }
            else
            {
                layer.Engine = CreateConfiguredSimulationEngine(spec.LayerType, randomize: true);
            }
        }
        else if (ReferenceEquals(layer.Engine, _engine))
        {
            primaryAssigned = true;
        }

        layer.Children.Clear();
        layer.InputFunction = spec.InputFunction;
        layer.BlendMode = spec.BlendMode;
        layer.InjectionMode = spec.InjectionMode;
        layer.LifeMode = spec.LifeMode;
        layer.BinningMode = spec.BinningMode;
        layer.InjectionNoise = spec.InjectionNoise;
        layer.LifeOpacity = spec.LifeOpacity;
        layer.RgbHueShiftDegrees = spec.RgbHueShiftDegrees;
        layer.RgbHueShiftSpeedDegreesPerSecond = spec.RgbHueShiftSpeedDegreesPerSecond;
        layer.AudioFrequencyHueShiftDegrees = spec.AudioFrequencyHueShiftDegrees;
        layer.ReactiveMappings = CloneReactiveMappings(spec.ReactiveMappings);
        layer.ThresholdMin = spec.ThresholdMin;
        layer.ThresholdMax = spec.ThresholdMax;
        layer.InvertThreshold = spec.InvertThreshold;
        layer.PixelSortCellWidth = spec.PixelSortCellWidth;
        layer.PixelSortCellHeight = spec.PixelSortCellHeight;
        layer.EffectiveLifeOpacity = layer.LifeOpacity;
        layer.EffectiveSimulationTargetFps = _currentSimulationTargetFps;
        layer.ReactiveHueShiftDegrees = 0;
        layer.EffectiveRgbHueShiftSpeedDegreesPerSecond = layer.RgbHueShiftSpeedDegreesPerSecond;
        layer.EffectiveInjectionNoise = layer.InjectionNoise;
        layer.EffectiveThresholdMin = layer.ThresholdMin;
        layer.EffectiveThresholdMax = layer.ThresholdMax;
        layer.EffectivePixelSortCellWidth = layer.PixelSortCellWidth;
        layer.EffectivePixelSortCellHeight = layer.PixelSortCellHeight;
        layer.TimeSinceLastStep = 0;

        ApplySimulationLayerEngineSettings(layer);
        return layer;
    }

    private void EnsurePrimarySimulationEngineAssigned(IReadOnlyList<SimulationLayerState> layers, ref bool primaryAssigned)
    {
        if (primaryAssigned)
        {
            return;
        }

        var firstLeaf = EnumerateSimulationLeafLayers(layers).FirstOrDefault(layer => layer.LayerType == SimulationLayerType.Life);
        if (firstLeaf == null)
        {
            return;
        }

        if (firstLeaf.Engine != null && !ReferenceEquals(firstLeaf.Engine, _engine))
        {
            RetireSimulationEngine(firstLeaf.Engine);
        }

        firstLeaf.Engine = _engine;
        ConfigureSimulationEngine(firstLeaf.Engine, _configuredRows, _configuredDepth, _currentAspectRatio, randomize: true);
        ApplySimulationLayerEngineSettings(firstLeaf);
        primaryAssigned = true;
    }

    private static string FormatHexColor(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    private static bool TryParseHexColor(string? value, out byte r, out byte g, out byte b)
    {
        r = 0;
        g = 0;
        b = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(1);
        }

        if (trimmed.Length == 3)
        {
            if (TryParseHexPair(new string(trimmed[0], 2), out r) &&
                TryParseHexPair(new string(trimmed[1], 2), out g) &&
                TryParseHexPair(new string(trimmed[2], 2), out b))
            {
                return true;
            }
        }
        else if (trimmed.Length == 6)
        {
            if (TryParseHexPair(trimmed.Substring(0, 2), out r) &&
                TryParseHexPair(trimmed.Substring(2, 2), out g) &&
                TryParseHexPair(trimmed.Substring(4, 2), out b))
            {
                return true;
            }
        }
        else
        {
            var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 3 &&
                byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out r) &&
                byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out g) &&
                byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseHexPair(string value, out byte result)
    {
        result = 0;
        if (value.Length != 2)
        {
            return false;
        }

        if (byte.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
            return true;
        }

        return false;
    }

    private enum AnimationType
    {
        ZoomIn,
        Translate,
        Rotate,
        DvdBounce,
        BeatShake,
        AudioGranular,
        Fade
    }

    private enum AnimationLoop
    {
        Forward,
        PingPong
    }

    private enum AnimationSpeed
    {
        Eighth,
        Quarter,
        Half,
        Normal,
        Double,
        Quadruple,
        Octuple
    }

    private enum TranslateDirection
    {
        Up,
        Down,
        Left,
        Right
    }

    private enum RotationDirection
    {
        Clockwise,
        CounterClockwise
    }

    private sealed class LayerAnimation
    {
        public Guid Id { get; } = Guid.NewGuid();
        public AnimationType Type { get; set; } = AnimationType.ZoomIn;
        public AnimationLoop Loop { get; set; } = AnimationLoop.Forward;
        public AnimationSpeed Speed { get; set; } = AnimationSpeed.Normal;
        public TranslateDirection TranslateDirection { get; set; } = TranslateDirection.Right;
        public RotationDirection RotationDirection { get; set; } = RotationDirection.Clockwise;
        public double RotationDegrees { get; set; } = AnimationRotateDegrees;
        public double DvdScale { get; set; } = AnimationDvdScale;
        public double BeatShakeIntensity { get; set; } = 1.0;
        public double AudioGranularLowGain { get; set; } = DefaultAudioGranularEqBandGain;
        public double AudioGranularMidGain { get; set; } = DefaultAudioGranularEqBandGain;
        public double AudioGranularHighGain { get; set; } = DefaultAudioGranularEqBandGain;
        public double BeatsPerCycle { get; set; } = 1.0;
    }

    private void UpdateDisplaySurface(bool force = false)
    {
        if (_isShuttingDown)
        {
            return;
        }

        var referenceEngine = GetReferenceSimulationEngine();
        int targetWidth = referenceEngine.Columns;
        int targetHeight = referenceEngine.Rows;

        if (targetWidth <= 0 || targetHeight <= 0)
        {
            targetWidth = referenceEngine.Columns;
            targetHeight = referenceEngine.Rows;
        }

        _pixelBuffer = _renderBackend.EnsureSurface(targetWidth, targetHeight, force);

        _displayWidth = targetWidth;
        _displayHeight = targetHeight;

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
        if (_suppressWindowResize || _isFullscreen || _isChromeResizing)
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

    private bool TryGetDownscaledDimensions(List<CaptureSource> sources, bool useEngineDimensions, out int width, out int height)
    {
        width = 0;
        height = 0;
        var referenceEngine = GetReferenceSimulationEngine();

        if (useEngineDimensions || sources.Count == 0)
        {
            width = referenceEngine.Columns;
            height = referenceEngine.Rows;
            return width > 0 && height > 0;
        }

        int maxWidth = referenceEngine.Columns;
        int maxHeight = referenceEngine.Rows;
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

    private readonly struct Transform2D
    {
        public Transform2D(double m11, double m12, double m21, double m22, double offsetX, double offsetY)
        {
            M11 = m11;
            M12 = m12;
            M21 = m21;
            M22 = m22;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }

        public double M11 { get; }
        public double M12 { get; }
        public double M21 { get; }
        public double M22 { get; }
        public double OffsetX { get; }
        public double OffsetY { get; }

        public bool IsIdentity =>
            Math.Abs(M11 - 1) < 0.000001 &&
            Math.Abs(M22 - 1) < 0.000001 &&
            Math.Abs(M12) < 0.000001 &&
            Math.Abs(M21) < 0.000001 &&
            Math.Abs(OffsetX) < 0.000001 &&
            Math.Abs(OffsetY) < 0.000001;

        public static Transform2D Identity => new(1, 0, 0, 1, 0, 0);

        public static Transform2D Multiply(Transform2D a, Transform2D b)
        {
            return new Transform2D(
                (a.M11 * b.M11) + (a.M12 * b.M21),
                (a.M11 * b.M12) + (a.M12 * b.M22),
                (a.M21 * b.M11) + (a.M22 * b.M21),
                (a.M21 * b.M12) + (a.M22 * b.M22),
                (a.M11 * b.OffsetX) + (a.M12 * b.OffsetY) + a.OffsetX,
                (a.M21 * b.OffsetX) + (a.M22 * b.OffsetY) + a.OffsetY);
        }

        public bool TryInvert(out Transform2D inverse)
        {
            double det = (M11 * M22) - (M12 * M21);
            if (Math.Abs(det) < 0.0000001)
            {
                inverse = Identity;
                return false;
            }

            double invDet = 1.0 / det;
            double invM11 = M22 * invDet;
            double invM12 = -M12 * invDet;
            double invM21 = -M21 * invDet;
            double invM22 = M11 * invDet;
            double invOffsetX = -((invM11 * OffsetX) + (invM12 * OffsetY));
            double invOffsetY = -((invM21 * OffsetX) + (invM22 * OffsetY));
            inverse = new Transform2D(invM11, invM12, invM21, invM22, invOffsetX, invOffsetY);
            return true;
        }

        public void TransformPoint(double x, double y, out double tx, out double ty)
        {
            tx = (M11 * x) + (M12 * y) + OffsetX;
            ty = (M21 * x) + (M22 * y) + OffsetY;
        }
    }

    private readonly struct KeyingSettings
    {
        public KeyingSettings(bool enabled, bool useAlpha, byte r, byte g, byte b, double tolerance)
        {
            Enabled = enabled;
            UseAlpha = useAlpha;
            R = r;
            G = g;
            B = b;
            Tolerance = tolerance;
        }

        public bool Enabled { get; }
        public bool UseAlpha { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public double Tolerance { get; }
    }

    private CompositeFrame? BuildCompositeFrame(List<CaptureSource> sources, ref byte[]? downscaledBuffer, bool useEngineDimensions, double animationTime, bool includeCpuReadback = true)
        => _renderBackend.BuildCompositeFrame(sources, ref downscaledBuffer, useEngineDimensions, animationTime, includeCpuReadback);

    private bool HasEmbeddedSimulationGroups(IEnumerable<CaptureSource> sources)
    {
        foreach (var source in sources)
        {
            if (source.Type == CaptureSource.SourceType.SimGroup)
            {
                return true;
            }

            if (source.Children.Count > 0 && HasEmbeddedSimulationGroups(source.Children))
            {
                return true;
            }
        }

        return false;
    }

    private SimulationLayerState? FindSimulationNode(Guid id) =>
        EnumerateSimulationLayers(_simulationLayers).FirstOrDefault(layer => layer.Id == id);

    private CompositeFrame? BuildInlineCompositeFrame(
        List<CaptureSource> sources,
        ref byte[]? downscaledBuffer,
        bool useEngineDimensions,
        double animationTime,
        int simulationStepsThisFrame,
        ref bool injectedAnyLayer,
        ref int steppedPassCount)
    {
        bool includeCpuReadback = _isRecording || App.IsSmokeTestMode || App.IsDiagnosticTestMode;
        if (_inlineGpuSourceCompositor.IsAvailable &&
            _gpuSimulationGroupCompositor.IsAvailable)
        {
            var gpuComposite = BuildInlineCompositeFrameGpu(
                sources,
                ref downscaledBuffer,
                useEngineDimensions,
                animationTime,
                simulationStepsThisFrame,
                ref injectedAnyLayer,
                ref steppedPassCount,
                includeCpuReadback);
            if (gpuComposite != null)
            {
                return gpuComposite;
            }
        }

        return BuildInlineCompositeFrameCpu(
            sources,
            ref downscaledBuffer,
            useEngineDimensions,
            animationTime,
            simulationStepsThisFrame,
            ref injectedAnyLayer,
            ref steppedPassCount);
    }

    private CompositeFrame? BuildInlineCompositeFrameGpu(
        List<CaptureSource> sources,
        ref byte[]? downscaledBuffer,
        bool useEngineDimensions,
        double animationTime,
        int simulationStepsThisFrame,
        ref bool injectedAnyLayer,
        ref int steppedPassCount,
        bool includeCpuReadback)
    {
        if (sources.Count == 0)
        {
            return null;
        }

        if (!TryGetDownscaledDimensions(sources, useEngineDimensions, out int downscaledWidth, out int downscaledHeight))
        {
            return null;
        }

        bool wroteAny = false;
        CompositeFrame? currentComposite = null;
        byte[]? scratchBuffer = null;
        int sourceIndex = 0;
        while (sourceIndex < sources.Count)
        {
            var source = sources[sourceIndex];
            if (!source.Enabled)
            {
                sourceIndex++;
                continue;
            }

            if (source.Type == CaptureSource.SourceType.SimGroup)
            {
                var simulationComposite = BuildInlineSimulationGroupCompositeFrameGpu(
                    source,
                    currentComposite,
                    simulationStepsThisFrame,
                    ref injectedAnyLayer,
                    ref steppedPassCount);
                if (simulationComposite == null)
                {
                    if (!source.SimulationLayers.Any(layer => layer.Enabled))
                    {
                        continue;
                    }

                    return null;
                }

                currentComposite = simulationComposite;
                wroteAny = true;
                sourceIndex++;
                continue;
            }

            var segmentInputs = new List<GpuSourceCompositor.PreparedLayerInput>();
            while (sourceIndex < sources.Count)
            {
                source = sources[sourceIndex];
                if (!source.Enabled)
                {
                    sourceIndex++;
                    continue;
                }

                if (source.Type == CaptureSource.SourceType.SimGroup)
                {
                    break;
                }

                if (TryBuildInlineGpuPreparedLayer(
                    source,
                    animationTime,
                    simulationStepsThisFrame,
                    ref injectedAnyLayer,
                    ref steppedPassCount,
                    out var preparedLayer))
                {
                    segmentInputs.Add(preparedLayer);
                }

                sourceIndex++;
            }

            if (segmentInputs.Count == 0)
            {
                continue;
            }

            var nextComposite = _inlineGpuSourceCompositor.ComposePreparedLayersOntoSurface(
                currentComposite?.GpuSurface,
                segmentInputs,
                downscaledWidth,
                downscaledHeight,
                animationTime,
                firstLayer: !wroteAny,
                ref scratchBuffer,
                includeCpuReadback: false);
            if (nextComposite == null)
            {
                return null;
            }

            currentComposite = nextComposite;
            wroteAny = true;
        }

        if (!wroteAny || currentComposite?.GpuSurface == null)
        {
            return null;
        }

        var finalComposite = _inlineGpuSourceCompositor.CreateCompositeFrameFromSurface(
            currentComposite.GpuSurface,
            ref downscaledBuffer,
            includeCpuReadback);
        return finalComposite;
    }

    private bool TryBuildInlineGpuPreparedLayer(
        CaptureSource source,
        double animationTime,
        int simulationStepsThisFrame,
        ref bool injectedAnyLayer,
        ref int steppedPassCount,
        out GpuSourceCompositor.PreparedLayerInput preparedLayer)
    {
        preparedLayer = default;

        if (!source.Enabled || source.Type == CaptureSource.SourceType.SimGroup)
        {
            return false;
        }

        if (source.Type == CaptureSource.SourceType.Group)
        {
            var groupBuffer = source.CompositeDownscaledBuffer;
            var groupComposite = HasEmbeddedSimulationGroups(source.Children)
                ? BuildInlineCompositeFrameGpu(
                    source.Children,
                    ref groupBuffer,
                    useEngineDimensions: false,
                    animationTime,
                    simulationStepsThisFrame,
                    ref injectedAnyLayer,
                    ref steppedPassCount,
                    includeCpuReadback: false)
                : BuildCompositeFrame(source.Children, ref groupBuffer, useEngineDimensions: false, animationTime, includeCpuReadback: false);
            source.CompositeDownscaledBuffer = groupBuffer;
            if (groupComposite == null)
            {
                return false;
            }

            preparedLayer = groupComposite.GpuSurface != null
                ? new GpuSourceCompositor.PreparedLayerInput(
                    source,
                    sourcePixels: null,
                    sourceShaderResourceView: groupComposite.GpuSurface.ShaderResourceView,
                    groupComposite.DownscaledWidth,
                    groupComposite.DownscaledHeight)
                : new GpuSourceCompositor.PreparedLayerInput(
                    source,
                    groupComposite.Downscaled,
                    sourceShaderResourceView: null,
                    groupComposite.DownscaledWidth,
                    groupComposite.DownscaledHeight);
            return true;
        }

        var frame = source.LastFrame;
        if (frame == null)
        {
            return false;
        }

        if (source.Type == CaptureSource.SourceType.Window && source.Window != null)
        {
            source.Window = source.Window.WithDimensions(frame.SourceWidth, frame.SourceHeight);
        }

        int sourceWidth = frame.Source != null ? frame.SourceWidth : frame.DownscaledWidth;
        int sourceHeight = frame.Source != null ? frame.SourceHeight : frame.DownscaledHeight;
        preparedLayer = new GpuSourceCompositor.PreparedLayerInput(
            source,
            frame.Source ?? frame.Downscaled,
            sourceShaderResourceView: null,
            sourceWidth,
            sourceHeight);
        return true;
    }

    private CompositeFrame? BuildInlineCompositeFrameCpu(
        List<CaptureSource> sources,
        ref byte[]? downscaledBuffer,
        bool useEngineDimensions,
        double animationTime,
        int simulationStepsThisFrame,
        ref bool injectedAnyLayer,
        ref int steppedPassCount)
    {
        if (sources.Count == 0)
        {
            return null;
        }

        if (!TryGetDownscaledDimensions(sources, useEngineDimensions, out int downscaledWidth, out int downscaledHeight))
        {
            return null;
        }

        int requiredLength = downscaledWidth * downscaledHeight * 4;
        if (downscaledBuffer == null || downscaledBuffer.Length != requiredLength)
        {
            downscaledBuffer = new byte[requiredLength];
        }

        bool wroteAny = false;
        CompositeFrame? currentComposite = null;

        foreach (var source in sources)
        {
            if (!source.Enabled)
            {
                continue;
            }

            if (source.Type == CaptureSource.SourceType.SimGroup)
            {
                var simulationComposite = BuildInlineSimulationGroupCompositeFrameCpu(
                    source,
                    currentComposite,
                    simulationStepsThisFrame,
                    ref injectedAnyLayer,
                    ref steppedPassCount);
                if (simulationComposite == null)
                {
                    continue;
                }

                if (simulationComposite.DownscaledWidth != downscaledWidth ||
                    simulationComposite.DownscaledHeight != downscaledHeight ||
                    simulationComposite.Downscaled.Length < requiredLength)
                {
                    continue;
                }

                Buffer.BlockCopy(simulationComposite.Downscaled, 0, downscaledBuffer, 0, requiredLength);
                currentComposite = new CompositeFrame(downscaledBuffer, downscaledWidth, downscaledHeight);
                wroteAny = true;
                continue;
            }

            SourceFrame? frame = null;
            if (source.Type == CaptureSource.SourceType.Group)
            {
                var groupBuffer = source.CompositeDownscaledBuffer;
                var groupComposite = HasEmbeddedSimulationGroups(source.Children)
                    ? BuildInlineCompositeFrameCpu(
                        source.Children,
                        ref groupBuffer,
                        useEngineDimensions: false,
                        animationTime,
                        simulationStepsThisFrame,
                        ref injectedAnyLayer,
                        ref steppedPassCount)
                    : BuildCompositeFrame(source.Children, ref groupBuffer, useEngineDimensions: false, animationTime);
                source.CompositeDownscaledBuffer = groupBuffer;
                if (groupComposite != null)
                {
                    frame = new SourceFrame(
                        groupComposite.Downscaled,
                        groupComposite.DownscaledWidth,
                        groupComposite.DownscaledHeight,
                        null,
                        groupComposite.DownscaledWidth,
                        groupComposite.DownscaledHeight);
                }
            }
            else
            {
                frame = source.LastFrame;
            }

            if (frame == null)
            {
                continue;
            }

            _inlineSourceCompositor.CompositeSourceFrameIntoBuffer(
                downscaledBuffer,
                downscaledWidth,
                downscaledHeight,
                frame,
                source,
                animationTime,
                firstLayer: !wroteAny);

            currentComposite = new CompositeFrame(downscaledBuffer, downscaledWidth, downscaledHeight);
            wroteAny = true;
        }

        return wroteAny
            ? new CompositeFrame(downscaledBuffer, downscaledWidth, downscaledHeight)
            : null;
    }

    private CompositeFrame? BuildInlineSimulationGroupCompositeFrameGpu(
        CaptureSource source,
        CompositeFrame? inputComposite,
        int simulationStepsThisFrame,
        ref bool injectedAnyLayer,
        ref int steppedPassCount)
    {
        if (!source.Enabled)
        {
            return inputComposite;
        }

        var runtimeGroup = FindSimulationNode(source.Id);
        if (runtimeGroup == null || !runtimeGroup.IsGroup)
        {
            return inputComposite;
        }

        int groupSteppedPassCount = 0;
        if (!_isPaused && simulationStepsThisFrame > 0 && runtimeGroup.Enabled)
        {
            CompositeFrame? groupInputComposite = inputComposite;
            bool compositeHasCpuReadback = groupInputComposite != null &&
                                           groupInputComposite.DownscaledWidth > 0 &&
                                           groupInputComposite.DownscaledHeight > 0 &&
                                           groupInputComposite.Downscaled.Length >= groupInputComposite.DownscaledWidth * groupInputComposite.DownscaledHeight * 4;
            bool attemptedCpuCompositeFallback = false;
            bool injectedInGroup = false;
            bool steppedInPass = false;
            GpuCompositeSurface? published = groupInputComposite?.GpuSurface;

            foreach (var child in runtimeGroup.Children)
            {
                published = ExecuteSimulationGroupChildInjectionAndStep(
                    child,
                    ref groupInputComposite,
                    ref compositeHasCpuReadback,
                    ref attemptedCpuCompositeFallback,
                    published,
                    ref injectedInGroup,
                    ref steppedInPass);
            }

            if (injectedInGroup)
            {
                injectedAnyLayer = true;
            }

            if (steppedInPass)
            {
                groupSteppedPassCount = 1;
            }

            var groupLeaves = EnumerateSimulationLeafLayers(runtimeGroup.Children).ToArray();
            for (int pass = 1; pass < simulationStepsThisFrame; pass++)
            {
                if (RunSimulationStepOnlyPass(groupLeaves))
                {
                    groupSteppedPassCount++;
                }
            }
        }

        steppedPassCount = Math.Max(steppedPassCount, groupSteppedPassCount);

        var enabledLayers = EnumerateSimulationLeafLayers(runtimeGroup.Children)
            .Where(layer => layer.Enabled)
            .ToArray();
        if (enabledLayers.Length == 0)
        {
            return inputComposite;
        }

        int width;
        int height;
        if (inputComposite != null)
        {
            width = inputComposite.DownscaledWidth;
            height = inputComposite.DownscaledHeight;
        }
        else
        {
            var referenceEngine = GetReferenceSimulationEngine();
            width = referenceEngine.Columns;
            height = referenceEngine.Rows;
        }

        var visibleLayers = new List<SimulationLayerState>(enabledLayers.Length);
        var activeLayerEntries = new List<SimulationPresentationLayerData>(enabledLayers.Length);
        foreach (var layer in enabledLayers)
        {
            if (TryBuildSimulationPresentationLayer(layer, Math.Clamp(_effectiveLifeOpacity * layer.EffectiveLifeOpacity, 0, 1), out var presentationLayer))
            {
                visibleLayers.Add(layer);
                activeLayerEntries.Add(presentationLayer);
            }
        }

        if (activeLayerEntries.Count == 0)
        {
            return inputComposite;
        }

        if (activeLayerEntries.Count > 8)
        {
            return null;
        }

        bool hasEnabledSubtractiveSimulationLayer = visibleLayers.Any(layer => layer.BlendMode == BlendMode.Subtractive);
        bool hasEnabledNonSubtractiveSimulationLayer = visibleLayers.Any(layer => layer.BlendMode != BlendMode.Subtractive);
        bool hasEnabledAdditiveSimulationLayer = visibleLayers.Any(layer => layer.BlendMode == BlendMode.Additive);
        int additiveLayerCount = visibleLayers.Count(layer => layer.BlendMode == BlendMode.Additive);
        int subtractiveLayerCount = visibleLayers.Count(layer => layer.BlendMode == BlendMode.Subtractive);
        bool hasStandaloneOutputLayer = activeLayerEntries.Any(layer => layer.PublishesStandaloneOutput);
        bool hasEnabledNonAddSubSimulationLayer = visibleLayers.Any(layer =>
            layer.BlendMode != BlendMode.Additive &&
            layer.BlendMode != BlendMode.Subtractive);

        int simulationBaseline;
        if (hasEnabledSubtractiveSimulationLayer && !hasEnabledNonSubtractiveSimulationLayer)
        {
            simulationBaseline = 255;
        }
        else if (hasEnabledAdditiveSimulationLayer &&
                 hasEnabledSubtractiveSimulationLayer &&
                 !hasEnabledNonAddSubSimulationLayer)
        {
            simulationBaseline = 128;
        }
        else
        {
            simulationBaseline = 0;
        }

        bool includeUnderlayInFinalComposite = inputComposite != null && !hasStandaloneOutputLayer;
        bool useSignedAddSubPassthrough = includeUnderlayInFinalComposite && !hasEnabledNonAddSubSimulationLayer;
        bool useMixedAddSubPassthroughModel = useSignedAddSubPassthrough &&
                                              additiveLayerCount > 0 &&
                                              subtractiveLayerCount > 0;

        byte[]? underlayBuffer = null;
        int underlayWidth = 0;
        int underlayHeight = 0;
        if (includeUnderlayInFinalComposite && inputComposite != null && inputComposite.GpuSurface == null)
        {
            underlayBuffer = inputComposite.Downscaled;
            underlayWidth = inputComposite.DownscaledWidth;
            underlayHeight = inputComposite.DownscaledHeight;
        }

        byte[]? groupBuffer = source.CompositeDownscaledBuffer;
        var composite = _gpuSimulationGroupCompositor.Compose(
            activeLayerEntries,
            includeUnderlayInFinalComposite ? inputComposite?.GpuSurface : null,
            underlayBuffer,
            underlayWidth,
            underlayHeight,
            simulationBaseline,
            useSignedAddSubPassthrough,
            useMixedAddSubPassthroughModel,
            invertComposite: false,
            width,
            height,
            ref groupBuffer,
            includeCpuReadback: _isRecording || App.IsDiagnosticTestMode || (App.IsSmokeTestMode && !_suppressSmokeIntermediateSimGroupReadback));
        source.CompositeDownscaledBuffer = groupBuffer;
        if (composite != null &&
            composite.Downscaled.Length >= composite.DownscaledWidth * composite.DownscaledHeight * 4)
        {
            source.LastFrame = new SourceFrame(
                composite.Downscaled,
                composite.DownscaledWidth,
                composite.DownscaledHeight,
                null,
                composite.DownscaledWidth,
                composite.DownscaledHeight);
        }
        else
        {
            source.LastFrame = null;
        }

        return composite;
    }

    private CompositeFrame? BuildInlineSimulationGroupCompositeFrameCpu(
        CaptureSource source,
        CompositeFrame? inputComposite,
        int simulationStepsThisFrame,
        ref bool injectedAnyLayer,
        ref int steppedPassCount)
    {
        if (!source.Enabled)
        {
            return inputComposite;
        }

        var runtimeGroup = FindSimulationNode(source.Id);
        if (runtimeGroup == null || !runtimeGroup.IsGroup)
        {
            return inputComposite;
        }

        int groupSteppedPassCount = 0;
        if (!_isPaused && simulationStepsThisFrame > 0 && runtimeGroup.Enabled)
        {
            CompositeFrame? groupInputComposite = inputComposite;
            bool compositeHasCpuReadback = groupInputComposite != null &&
                                           groupInputComposite.DownscaledWidth > 0 &&
                                           groupInputComposite.DownscaledHeight > 0 &&
                                           groupInputComposite.Downscaled.Length >= groupInputComposite.DownscaledWidth * groupInputComposite.DownscaledHeight * 4;
            bool attemptedCpuCompositeFallback = false;
            bool injectedInGroup = false;
            bool steppedInPass = false;
            GpuCompositeSurface? published = groupInputComposite?.GpuSurface;

            foreach (var child in runtimeGroup.Children)
            {
                published = ExecuteSimulationGroupChildInjectionAndStep(
                    child,
                    ref groupInputComposite,
                    ref compositeHasCpuReadback,
                    ref attemptedCpuCompositeFallback,
                    published,
                    ref injectedInGroup,
                    ref steppedInPass);
            }

            if (injectedInGroup)
            {
                injectedAnyLayer = true;
            }

            if (steppedInPass)
            {
                groupSteppedPassCount = 1;
            }

            var groupLeaves = EnumerateSimulationLeafLayers(runtimeGroup.Children).ToArray();
            for (int pass = 1; pass < simulationStepsThisFrame; pass++)
            {
                if (RunSimulationStepOnlyPass(groupLeaves))
                {
                    groupSteppedPassCount++;
                }
            }
        }

        steppedPassCount = Math.Max(steppedPassCount, groupSteppedPassCount);

        var enabledLayers = EnumerateSimulationLeafLayers(runtimeGroup.Children)
            .Where(layer => layer.Enabled)
            .ToArray();
        if (enabledLayers.Length == 0)
        {
            return inputComposite;
        }

        int width;
        int height;
        byte[]? targetBuffer = source.CompositeDownscaledBuffer;
        if (inputComposite != null)
        {
            width = inputComposite.DownscaledWidth;
            height = inputComposite.DownscaledHeight;
        }
        else
        {
            var referenceEngine = GetReferenceSimulationEngine();
            width = referenceEngine.Columns;
            height = referenceEngine.Rows;
        }

        int requiredLength = width * height * 4;
        if (targetBuffer == null || targetBuffer.Length != requiredLength)
        {
            targetBuffer = new byte[requiredLength];
        }

        if (inputComposite != null && inputComposite.Downscaled.Length >= requiredLength)
        {
            Buffer.BlockCopy(inputComposite.Downscaled, 0, targetBuffer, 0, requiredLength);
        }
        else
        {
            Array.Clear(targetBuffer, 0, requiredLength);
        }

        var referenceSimulationEngine = GetReferenceSimulationEngine();
        int engineCols = referenceSimulationEngine.Columns;
        int engineRows = referenceSimulationEngine.Rows;
        if (_rowMap.Length < height)
        {
            _rowMap = new int[height];
        }
        if (_colMap.Length < width)
        {
            _colMap = new int[width];
        }
        BuildMappings(width, height, engineCols, engineRows);

        double globalLifeOpacity = Math.Clamp(_effectiveLifeOpacity, 0, 1);
        InlineSimulationBlendLayerData[] blendLayers = BuildInlineSimulationBlendLayers(enabledLayers, globalLifeOpacity);
        if (blendLayers.Length == 0)
        {
            source.CompositeDownscaledBuffer = targetBuffer;
            return inputComposite;
        }

        bool hasEnabledSubtractiveSimulationLayer = blendLayers.Any(layer => layer.BlendMode == BlendMode.Subtractive);
        bool hasEnabledNonSubtractiveSimulationLayer = blendLayers.Any(layer => layer.BlendMode != BlendMode.Subtractive);
        bool hasEnabledAdditiveSimulationLayer = blendLayers.Any(layer => layer.BlendMode == BlendMode.Additive);
        int additiveLayerCount = blendLayers.Count(layer => layer.BlendMode == BlendMode.Additive);
        int subtractiveLayerCount = blendLayers.Count(layer => layer.BlendMode == BlendMode.Subtractive);
        bool hasStandaloneOutputLayer = blendLayers.Any(layer => layer.PublishesStandaloneOutput);
        bool hasEnabledNonAddSubSimulationLayer = blendLayers.Any(layer =>
            layer.BlendMode != BlendMode.Additive &&
            layer.BlendMode != BlendMode.Subtractive);

        int simulationBaseline;
        if (hasEnabledSubtractiveSimulationLayer && !hasEnabledNonSubtractiveSimulationLayer)
        {
            simulationBaseline = 255;
        }
        else if (hasEnabledAdditiveSimulationLayer &&
                 hasEnabledSubtractiveSimulationLayer &&
                 !hasEnabledNonAddSubSimulationLayer)
        {
            simulationBaseline = 128;
        }
        else
        {
            simulationBaseline = 0;
        }

        bool includeUnderlayInFinalComposite = inputComposite != null && !hasStandaloneOutputLayer;
        bool useSignedAddSubPassthrough = includeUnderlayInFinalComposite && !hasEnabledNonAddSubSimulationLayer;
        bool useMixedAddSubPassthroughModel = useSignedAddSubPassthrough &&
                                              additiveLayerCount > 0 &&
                                              subtractiveLayerCount > 0;

        Parallel.For(0, height, row =>
        {
            int sourceRow = _rowMap[row];
            for (int col = 0; col < width; col++)
            {
                int sourceCol = _colMap[col];
                int sourceIndex = (sourceRow * engineCols + sourceCol) * 4;
                int index = (row * width + col) * 4;

                int underlayB = simulationBaseline;
                int underlayG = simulationBaseline;
                int underlayR = simulationBaseline;
                if (includeUnderlayInFinalComposite && inputComposite != null && inputComposite.Downscaled.Length >= requiredLength)
                {
                    underlayB = targetBuffer[index];
                    underlayG = targetBuffer[index + 1];
                    underlayR = targetBuffer[index + 2];
                }

                if (useSignedAddSubPassthrough)
                {
                    int addB = 0;
                    int addG = 0;
                    int addR = 0;
                    int subB = 0;
                    int subG = 0;
                    int subR = 0;
                    foreach (var blendLayer in blendLayers)
                    {
                        SampleInlineSimulationLayerColor(blendLayer, sourceIndex, out byte sampleR, out byte sampleG, out byte sampleB);
                        if (blendLayer.BlendMode == BlendMode.Subtractive)
                        {
                            subR += (int)Math.Round((255 - sampleR) * blendLayer.Opacity);
                            subG += (int)Math.Round((255 - sampleG) * blendLayer.Opacity);
                            subB += (int)Math.Round((255 - sampleB) * blendLayer.Opacity);
                        }
                        else
                        {
                            addR += (int)Math.Round(sampleR * blendLayer.Opacity);
                            addG += (int)Math.Round(sampleG * blendLayer.Opacity);
                            addB += (int)Math.Round(sampleB * blendLayer.Opacity);
                        }
                    }

                    if (useMixedAddSubPassthroughModel)
                    {
                        double underlayB01 = underlayB / 255.0;
                        double underlayG01 = underlayG / 255.0;
                        double underlayR01 = underlayR / 255.0;

                        double scaledSubB = Math.Clamp(subB * underlayB01, 0, 255);
                        double scaledSubG = Math.Clamp(subG * underlayG01, 0, 255);
                        double scaledSubR = Math.Clamp(subR * underlayR01, 0, 255);

                        double scaledAddB = addB * (1.0 - underlayB01);
                        double scaledAddG = addG * (1.0 - underlayG01);
                        double scaledAddR = addR * (1.0 - underlayR01);

                        targetBuffer[index] = (byte)ClampToByte((int)Math.Round(underlayB + scaledAddB - scaledSubB));
                        targetBuffer[index + 1] = (byte)ClampToByte((int)Math.Round(underlayG + scaledAddG - scaledSubG));
                        targetBuffer[index + 2] = (byte)ClampToByte((int)Math.Round(underlayR + scaledAddR - scaledSubR));
                    }
                    else
                    {
                        targetBuffer[index] = ClampToByte(underlayB + addB - subB);
                        targetBuffer[index + 1] = ClampToByte(underlayG + addG - subG);
                        targetBuffer[index + 2] = ClampToByte(underlayR + addR - subR);
                    }

                    targetBuffer[index + 3] = 255;
                    continue;
                }

                int simB = simulationBaseline;
                int simG = simulationBaseline;
                int simR = simulationBaseline;
                foreach (var blendLayer in blendLayers)
                {
                    SampleInlineSimulationLayerColor(blendLayer, sourceIndex, out byte sampleR, out byte sampleG, out byte sampleB);
                    BlendSimulationLayerInto(ref simB, ref simG, ref simR, sampleR, sampleG, sampleB, blendLayer.BlendMode, blendLayer.Opacity);
                }

                int deltaB = simB - simulationBaseline;
                int deltaG = simG - simulationBaseline;
                int deltaR = simR - simulationBaseline;
                targetBuffer[index] = ClampToByte(underlayB + deltaB);
                targetBuffer[index + 1] = ClampToByte(underlayG + deltaG);
                targetBuffer[index + 2] = ClampToByte(underlayR + deltaR);
                targetBuffer[index + 3] = 255;
            }
        });

        source.CompositeDownscaledBuffer = targetBuffer;
        source.LastFrame = new SourceFrame(targetBuffer, width, height, null, width, height);
        return new CompositeFrame(targetBuffer, width, height);
    }

    internal static void ResetGpuSourceCompositeSmokeCounters() => GpuSourceCompositor.ResetSmokeCounters();

    internal static int GetGpuSourceCompositePassCount() => GpuSourceCompositor.CompositePassCount;

    internal static (int passCount, long buildCount, double uploadMs, double drawMs, double readbackMs) GetGpuSourceCompositeSmokeStats()
        => GpuSourceCompositor.GetSmokeStats();

    internal bool RunGpuSourceCompositeSmoke()
    {
        ResetGpuSourceCompositeSmokeCounters();

        const int width = 64;
        const int height = 36;
        var first = CaptureSource.CreateFile("smoke-source-a", "Smoke A", width, height);
        first.LastFrame = new SourceFrame(BuildSmokeSolidBgra(width, height, 24, 48, 180), width, height, null, width, height);
        first.BlendMode = BlendMode.Additive;
        first.Opacity = 1.0;

        var second = CaptureSource.CreateFile("smoke-source-b", "Smoke B", width, height);
        second.LastFrame = new SourceFrame(BuildSmokeSolidBgra(width, height, 180, 24, 32), width, height, null, width, height);
        second.BlendMode = BlendMode.Additive;
        second.Opacity = 0.5;

        byte[]? compositeBuffer = null;
        var composite = BuildCompositeFrame(new List<CaptureSource> { first, second }, ref compositeBuffer, useEngineDimensions: true, animationTime: 0.0);
        if (composite == null || composite.Downscaled.Length != composite.DownscaledWidth * composite.DownscaledHeight * 4)
        {
            Logger.Warn("GPU source composite smoke: composite frame was null or wrong size.");
            return false;
        }

        int centerIndex = ((composite.DownscaledHeight / 2) * composite.DownscaledWidth + (composite.DownscaledWidth / 2)) * 4;
        byte b = composite.Downscaled[centerIndex];
        byte g = composite.Downscaled[centerIndex + 1];
        byte r = composite.Downscaled[centerIndex + 2];
        int passCount = GetGpuSourceCompositePassCount();
        Logger.Info($"GPU source composite smoke: passes={passCount}, center=({b},{g},{r}).");
        return passCount > 0 &&
               (b != 24 || g != 48 || r != 180) &&
               (b != 0 || g != 0 || r != 0);
    }

    internal bool RunSourceResetSmoke()
    {
        EnsureSimulationLayersInitialized();

        int width = GetReferenceSimulationEngine().Columns;
        int height = GetReferenceSimulationEngine().Rows;
        var source = CaptureSource.CreateFile("source-reset", "Source Reset", width, height);
        source.LastFrame = new SourceFrame(BuildSmokeSolidBgra(width, height, 48, 96, 192), width, height, null, width, height);
        source.BlendMode = BlendMode.Additive;
        source.Opacity = 1.0;

        var simulationLeaves = EnumerateSimulationLeafLayers(_simulationLayers).ToArray();
        bool[] priorLayerEnabled = simulationLeaves.Select(layer => layer.Enabled).ToArray();
        bool priorPassthrough = _passthroughEnabled;

        try
        {
            foreach (var layer in simulationLeaves)
            {
                layer.Enabled = false;
            }

            _passthroughEnabled = true;
            if (PassthroughMenuItem != null)
            {
                PassthroughMenuItem.IsChecked = true;
            }

            _sources.Clear();
            _sources.Add(source);
            UpdatePrimaryAspectIfNeeded();
            byte[]? compositeBuffer = null;
            _lastCompositeFrame = BuildCompositeFrame(_sources, ref compositeBuffer, useEngineDimensions: true, animationTime: 0.0);
            RenderFrame();

            ClearSources();
            bool passthroughPreserved = _passthroughEnabled;

            _sources.Add(source);
            UpdatePrimaryAspectIfNeeded();
            compositeBuffer = null;
            _lastCompositeFrame = BuildCompositeFrame(_sources, ref compositeBuffer, useEngineDimensions: true, animationTime: 0.0);
            bool secondCompositeVisible = BufferHasNonBlackPixel(_lastCompositeFrame?.Downscaled);
            RenderFrame();
            bool secondVisible = BufferHasNonBlackPixel(_pixelBuffer);

            Logger.Info($"Source reset smoke: passthroughPreserved={passthroughPreserved}, secondCompositeVisible={secondCompositeVisible}, secondVisible={secondVisible}.");
            return passthroughPreserved && secondCompositeVisible && secondVisible;
        }
        finally
        {
            _sources.Clear();
            _lastCompositeFrame = null;
            _passthroughEnabled = priorPassthrough;
            if (PassthroughMenuItem != null)
            {
                PassthroughMenuItem.IsChecked = priorPassthrough;
            }

            for (int i = 0; i < simulationLeaves.Length && i < priorLayerEnabled.Length; i++)
            {
                simulationLeaves[i].Enabled = priorLayerEnabled[i];
            }
        }
    }

    internal (bool ok, int passCount, long buildCount, double uploadMs, double drawMs, double readbackMs, int width, int height) RunGpuSourceCompositeBenchmark(int iterations)
    {
        ResetGpuSourceCompositeSmokeCounters();

        const int sourceWidth = 64;
        const int sourceHeight = 36;
        var first = CaptureSource.CreateFile("bench-source-a", "Bench A", sourceWidth, sourceHeight);
        first.LastFrame = new SourceFrame(BuildSmokeSolidBgra(sourceWidth, sourceHeight, 24, 48, 180), sourceWidth, sourceHeight, null, sourceWidth, sourceHeight);
        first.BlendMode = BlendMode.Additive;
        first.Opacity = 1.0;

        var second = CaptureSource.CreateFile("bench-source-b", "Bench B", sourceWidth, sourceHeight);
        second.LastFrame = new SourceFrame(BuildSmokeSolidBgra(sourceWidth, sourceHeight, 180, 24, 32), sourceWidth, sourceHeight, null, sourceWidth, sourceHeight);
        second.BlendMode = BlendMode.Screen;
        second.Opacity = 0.75;

        var third = CaptureSource.CreateFile("bench-source-c", "Bench C", sourceWidth, sourceHeight);
        third.LastFrame = new SourceFrame(BuildSmokeSolidBgra(sourceWidth, sourceHeight, 32, 160, 96), sourceWidth, sourceHeight, null, sourceWidth, sourceHeight);
        third.BlendMode = BlendMode.Overlay;
        third.Opacity = 0.6;

        byte[]? compositeBuffer = null;
        CompositeFrame? composite = null;
        for (int i = 0; i < Math.Max(1, iterations); i++)
        {
            composite = BuildCompositeFrame(new List<CaptureSource> { first, second, third }, ref compositeBuffer, useEngineDimensions: true, animationTime: 0.0);
            if (composite == null)
            {
                break;
            }
        }

        var stats = GetGpuSourceCompositeSmokeStats();
        return (
            composite != null,
            stats.passCount,
            stats.buildCount,
            stats.uploadMs,
            stats.drawMs,
            stats.readbackMs,
            composite?.DownscaledWidth ?? 0,
            composite?.DownscaledHeight ?? 0);
    }

    internal (bool ok, int passCount, long buildCount, double uploadMs, double drawMs, double readbackMs,
        double injectMs, double stepMs, double fillMs, int width, int height) RunGpuCompositeToSimulationBenchmark(int iterations)
    {
        ResetGpuSourceCompositeSmokeCounters();

        const int sourceWidth = 64;
        const int sourceHeight = 36;
        var source = CaptureSource.CreateFile("handoff-bench-source", "Handoff Bench", sourceWidth, sourceHeight);
        source.LastFrame = new SourceFrame(BuildSmokeSolidBgra(sourceWidth, sourceHeight, 128, 128, 128), sourceWidth, sourceHeight, null, sourceWidth, sourceHeight);
        source.BlendMode = BlendMode.Additive;
        source.Opacity = 1.0;

        using var backend = new GpuSimulationBackend();
        bool anyOk = false;
        long injectTicks = 0;
        long stepTicks = 0;
        long fillTicks = 0;
        int width = 0;
        int height = 0;
        byte[]? compositeBuffer = null;

        for (int i = 0; i < Math.Max(1, iterations); i++)
        {
            var composite = BuildCompositeFrame(new List<CaptureSource> { source }, ref compositeBuffer, useEngineDimensions: true, animationTime: 0.0,
                includeCpuReadback: false);
            if (composite?.GpuSurface == null)
            {
                continue;
            }

            if (!backend.IsGpuAvailable)
            {
                break;
            }

            width = composite.DownscaledWidth;
            height = composite.DownscaledHeight;
            backend.Configure(height, 24, (double)width / height);
            backend.SetBinningMode(GameOfLifeEngine.BinningMode.Fill);
            backend.SetInjectionMode(GameOfLifeEngine.InjectionMode.Threshold);
            backend.SetMode(GameOfLifeEngine.LifeMode.NaiveGrayscale);

            long start = Stopwatch.GetTimestamp();
            bool injected = backend.TryInjectCompositeSurface(
                composite.GpuSurface,
                0.45,
                0.55,
                invertThreshold: false,
                GameOfLifeEngine.InjectionMode.Threshold,
                noiseProbability: 0.0,
                period: backend.Depth,
                pulseStep: i,
                invertInput: false,
                hueShiftDegrees: 0.0);
            injectTicks += Stopwatch.GetTimestamp() - start;
            if (!injected)
            {
                continue;
            }

            start = Stopwatch.GetTimestamp();
            backend.Step();
            stepTicks += Stopwatch.GetTimestamp() - start;

            byte[] colorBuffer = new byte[backend.Columns * backend.Rows * 4];
            start = Stopwatch.GetTimestamp();
            backend.FillColorBuffer(colorBuffer);
            fillTicks += Stopwatch.GetTimestamp() - start;

            anyOk = true;
        }

        var stats = GetGpuSourceCompositeSmokeStats();
        double tickScale = 1000.0 / Stopwatch.Frequency;
        double divisor = Math.Max(1, stats.buildCount);
        return (
            anyOk,
            stats.passCount,
            stats.buildCount,
            stats.uploadMs,
            stats.drawMs,
            stats.readbackMs,
            injectTicks * tickScale / divisor,
            stepTicks * tickScale / divisor,
            fillTicks * tickScale / divisor,
            width,
            height);
    }

    internal bool RunGpuPixelSortSmoke()
    {
        const int sourceWidth = 96;
        const int sourceHeight = 54;

        var source = CaptureSource.CreateFile("pixel-sort-source", "Pixel Sort Source", sourceWidth, sourceHeight);
        source.LastFrame = new SourceFrame(
            BuildSmokePixelSortPatternBgra(sourceWidth, sourceHeight),
            sourceWidth,
            sourceHeight,
            null,
            sourceWidth,
            sourceHeight);
        source.BlendMode = BlendMode.Normal;
        source.Opacity = 1.0;

        byte[]? compositeBuffer = null;
        var composite = BuildCompositeFrame(
            new List<CaptureSource> { source },
            ref compositeBuffer,
            useEngineDimensions: true,
            animationTime: 0.0,
            includeCpuReadback: true);
        if (composite?.GpuSurface == null || composite.DownscaledWidth <= 0 || composite.DownscaledHeight <= 0)
        {
            Logger.Warn("GPU pixel sort smoke: composite surface was unavailable.");
            return false;
        }

        using var backend = new GpuPixelSortBackend();
        backend.Configure(composite.DownscaledHeight, 24, (double)composite.DownscaledWidth / composite.DownscaledHeight);
        backend.SetCellSize(12, 8);

        bool injected = backend.TryInjectCompositeSurface(
            composite.GpuSurface,
            0.0,
            1.0,
            invertThreshold: false,
            GameOfLifeEngine.InjectionMode.Threshold,
            noiseProbability: 0.0,
            period: 1,
            pulseStep: 0,
            invertInput: false,
            hueShiftDegrees: 0.0);

        byte[] injectedBuffer = new byte[backend.Columns * backend.Rows * 4];
        backend.FillColorBuffer(injectedBuffer);
        bool injectHistogramPreserved = HaveMatchingPixelHistogram(composite.Downscaled, injectedBuffer, pixelCount: Math.Min(composite.Downscaled.Length, injectedBuffer.Length) / 4);

        backend.Step();

        byte[] sorted = new byte[backend.Columns * backend.Rows * 4];
        backend.FillColorBuffer(sorted);
        bool surfaceOk = backend.TryGetColorSurface(out var surface) && surface != null;

        int changedPixels = 0;
        int pixelCount = Math.Min(composite.Downscaled.Length, sorted.Length) / 4;
        for (int i = 0; i < pixelCount; i++)
        {
            int index = i * 4;
            int diff =
                Math.Abs(composite.Downscaled[index] - sorted[index]) +
                Math.Abs(composite.Downscaled[index + 1] - sorted[index + 1]) +
                Math.Abs(composite.Downscaled[index + 2] - sorted[index + 2]);
            if (diff >= 12)
            {
                changedPixels++;
            }
        }

        bool changedEnough = changedPixels > Math.Max(32, pixelCount / 10);
        bool histogramPreserved = HaveMatchingPixelHistogram(composite.Downscaled, sorted, pixelCount);
        Logger.Info(
            $"GPU pixel sort smoke: injected={injected}, injectHistogramPreserved={injectHistogramPreserved}, surfaceOk={surfaceOk}, changedPixels={changedPixels}, totalPixels={pixelCount}, changedEnough={changedEnough}, histogramPreserved={histogramPreserved}.");
        return injected && surfaceOk && changedEnough && histogramPreserved;
    }

    internal bool RunSimGroupPixelSortColorSmoke()
    {
        if (!_renderLoopAttached)
        {
            InitializeVisualizer();
        }

        _sources.Clear();
        ClearSimulationLayers();
        _passthroughEnabled = false;
        _passthroughCompositedInPixelBuffer = false;
        _invertComposite = false;
        _isPaused = false;

        ApplyDimensions(144, 24, DefaultAspectRatio, persist: false);
        int width = GetReferenceSimulationEngine().Columns;
        int height = GetReferenceSimulationEngine().Rows;

        byte[] sourceBuffer = BuildSmokePixelSortPatternBgra(width, height);
        var source = CaptureSource.CreateFile("sim-group-pixel-sort-source", "Sim Group Pixel Sort Source", width, height);
        source.LastFrame = new SourceFrame(sourceBuffer, width, height, null, width, height);
        source.BlendMode = BlendMode.Normal;
        source.Opacity = 1.0;

        var simulationGroup = CaptureSource.CreateSimulationGroup("Pixel Sort Group");
        simulationGroup.SimulationLayers.Add(new SimulationLayerSpec
        {
            Id = Guid.NewGuid(),
            Kind = LayerEditorSimulationItemKind.Layer,
            LayerType = SimulationLayerType.PixelSort,
            Name = "Pixel Sort",
            Enabled = true,
            InputFunction = SimulationInputFunction.Direct,
            BlendMode = BlendMode.Normal,
            InjectionMode = GameOfLifeEngine.InjectionMode.Threshold,
            LifeMode = GameOfLifeEngine.LifeMode.NaiveGrayscale,
            BinningMode = GameOfLifeEngine.BinningMode.Fill,
            InjectionNoise = 0.0,
            LifeOpacity = 1.0,
            ThresholdMin = 0.0,
            ThresholdMax = 1.0,
            InvertThreshold = false,
            PixelSortCellWidth = 12,
            PixelSortCellHeight = 8
        });

        _sources.Add(source);
        _sources.Add(simulationGroup);

        ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
        ConfigureSimulationLayerEngines(_configuredRows, _configuredDepth, _currentAspectRatio, randomize: false);
        foreach (var runtimeLayer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            runtimeLayer.TimeSinceLastStep = 1.0;
        }

        _pendingInlineSimulationStepsThisFrame = 1;
        InjectCaptureFrames(injectLayers: true);
        _pendingInlineSimulationStepsThisFrame = 0;

        byte[]? output = simulationGroup.LastFrame?.Downscaled;
        bool gpuBacked = _lastCompositeFrame?.GpuSurface != null;
        if (output == null || output.Length < width * height * 4)
        {
            Logger.Warn("Sim-group pixel-sort color smoke: group output was unavailable.");
            return false;
        }

        int pixelCount = Math.Min(sourceBuffer.Length, output.Length) / 4;
        bool histogramPreserved = HaveMatchingPixelHistogram(sourceBuffer, output, pixelCount);
        int changedPixels = 0;
        for (int i = 0; i < pixelCount; i++)
        {
            int index = i * 4;
            int diff =
                Math.Abs(sourceBuffer[index] - output[index]) +
                Math.Abs(sourceBuffer[index + 1] - output[index + 1]) +
                Math.Abs(sourceBuffer[index + 2] - output[index + 2]);
            if (diff >= 12)
            {
                changedPixels++;
            }
        }

        bool changedEnough = changedPixels > Math.Max(32, pixelCount / 10);
        Logger.Info($"Sim-group pixel-sort color smoke: gpuBacked={gpuBacked}, histogramPreserved={histogramPreserved}, changedPixels={changedPixels}, totalPixels={pixelCount}, changedEnough={changedEnough}.");
        return gpuBacked && histogramPreserved && changedEnough;
    }

    internal bool RunGpuCompositeToSimulationSmoke()
    {
        const int width = 128;
        const int height = 72;

        var source = CaptureSource.CreateFile("handoff-source", "Handoff", width, height);
        source.LastFrame = new SourceFrame(BuildSmokeSolidBgra(width, height, 128, 128, 128), width, height, null, width, height);
        source.BlendMode = BlendMode.Additive;
        source.Opacity = 1.0;

        byte[]? compositeBuffer = null;
        var composite = BuildCompositeFrame(new List<CaptureSource> { source }, ref compositeBuffer, useEngineDimensions: true, animationTime: 0.0,
            includeCpuReadback: false);
        if (composite?.GpuSurface == null)
        {
            Logger.Warn("GPU handoff smoke: composite surface was unavailable.");
            return false;
        }

        using var backend = new GpuSimulationBackend();
        backend.Configure(composite.DownscaledHeight, 24, (double)composite.DownscaledWidth / composite.DownscaledHeight);
        backend.SetBinningMode(GameOfLifeEngine.BinningMode.Fill);
        backend.SetInjectionMode(GameOfLifeEngine.InjectionMode.Threshold);
        backend.SetMode(GameOfLifeEngine.LifeMode.NaiveGrayscale);
        if (!backend.IsGpuActive)
        {
            Logger.Warn("GPU handoff smoke: simulation backend was not active.");
            return false;
        }

        bool injected = backend.TryInjectCompositeSurface(
            composite.GpuSurface,
            0.45,
            0.55,
            invertThreshold: false,
            GameOfLifeEngine.InjectionMode.Threshold,
            noiseProbability: 0.0,
            period: backend.Depth,
            pulseStep: 0,
            invertInput: false,
            hueShiftDegrees: 0.0);
        if (!injected)
        {
            Logger.Warn("GPU handoff smoke: direct composite injection was rejected.");
            return false;
        }

        byte[] colorBuffer = new byte[backend.Columns * backend.Rows * 4];
        backend.FillColorBuffer(colorBuffer);
        bool hasAnyLife = false;
        for (int i = 0; i < colorBuffer.Length; i += 4)
        {
            if (colorBuffer[i] != 0 || colorBuffer[i + 1] != 0 || colorBuffer[i + 2] != 0)
            {
                hasAnyLife = true;
                break;
            }
        }

        Logger.Info($"GPU handoff smoke: cpuReadbackBytes={composite.Downscaled.Length}, hasAnyLife={hasAnyLife}.");
        return composite.Downscaled.Length == 0 && hasAnyLife;
    }

    internal bool RunGpuCompositeRgbThresholdSmoke()
    {
        const int width = 128;
        const int height = 72;

        var source = CaptureSource.CreateFile("rgb-threshold-source", "RgbThreshold", width, height);
        source.LastFrame = new SourceFrame(BuildSmokeSolidBgra(width, height, 255, 255, 255), width, height, null, width, height);
        source.BlendMode = BlendMode.Normal;
        source.Opacity = 1.0;

        byte[]? compositeBuffer = null;
        var composite = BuildCompositeFrame(new List<CaptureSource> { source }, ref compositeBuffer, useEngineDimensions: true, animationTime: 0.0,
            includeCpuReadback: false);
        if (composite?.GpuSurface == null)
        {
            Logger.Warn("GPU RGB threshold smoke: composite surface was unavailable.");
            return false;
        }

        using var backend = new GpuSimulationBackend();
        backend.Configure(composite.DownscaledHeight, 24, (double)composite.DownscaledWidth / composite.DownscaledHeight);
        backend.SetBinningMode(GameOfLifeEngine.BinningMode.Fill);
        backend.SetInjectionMode(GameOfLifeEngine.InjectionMode.Threshold);
        backend.SetMode(GameOfLifeEngine.LifeMode.RgbChannels);
        if (!backend.IsGpuActive)
        {
            Logger.Warn("GPU RGB threshold smoke: simulation backend was not active.");
            return false;
        }

        bool injected = backend.TryInjectCompositeSurface(
            composite.GpuSurface,
            0.5,
            1.0,
            invertThreshold: false,
            GameOfLifeEngine.InjectionMode.Threshold,
            noiseProbability: 0.0,
            period: backend.Depth,
            pulseStep: 0,
            invertInput: false,
            hueShiftDegrees: 0.0);
        if (!injected)
        {
            Logger.Warn("GPU RGB threshold smoke: composite RGB injection was rejected.");
            return false;
        }

        byte[] colorBuffer = new byte[backend.Columns * backend.Rows * 4];
        backend.FillColorBuffer(colorBuffer);
        int centerIndex = ((backend.Rows / 2) * backend.Columns + (backend.Columns / 2)) * 4;
        byte r = colorBuffer[centerIndex];
        byte g = colorBuffer[centerIndex + 1];
        byte b = colorBuffer[centerIndex + 2];
        bool allChannelsInjected = r > 0 && g > 0 && b > 0;

        using var directBackend = new GpuSimulationBackend();
        directBackend.Configure(composite.DownscaledHeight, 24, (double)composite.DownscaledWidth / composite.DownscaledHeight);
        directBackend.SetBinningMode(GameOfLifeEngine.BinningMode.Fill);
        directBackend.SetInjectionMode(GameOfLifeEngine.InjectionMode.Threshold);
        directBackend.SetMode(GameOfLifeEngine.LifeMode.RgbChannels);
        bool[,] fullMask = new bool[directBackend.Rows, directBackend.Columns];
        for (int row = 0; row < directBackend.Rows; row++)
        {
            for (int col = 0; col < directBackend.Columns; col++)
            {
                fullMask[row, col] = true;
            }
        }

        directBackend.InjectRgbFrame(fullMask, fullMask, fullMask);
        byte[] directColorBuffer = new byte[directBackend.Columns * directBackend.Rows * 4];
        directBackend.FillColorBuffer(directColorBuffer);
        int directCenterIndex = ((directBackend.Rows / 2) * directBackend.Columns + (directBackend.Columns / 2)) * 4;
        byte directR = directColorBuffer[directCenterIndex];
        byte directG = directColorBuffer[directCenterIndex + 1];
        byte directB = directColorBuffer[directCenterIndex + 2];

        Logger.Info($"GPU RGB threshold smoke: composite center rgb=({r},{g},{b}), direct center rgb=({directR},{directG},{directB}), allChannelsInjected={allChannelsInjected}.");
        return allChannelsInjected;
    }

    internal bool RunGpuInjectionModeSmoke()
    {
        const int width = 128;
        const int height = 72;
        const double min = 0.5;
        const double max = 1.0;

        var source = CaptureSource.CreateFile("injection-mode-source", "InjectionMode", width, height);
        source.LastFrame = new SourceFrame(BuildSmokeSolidBgra(width, height, 191, 191, 191), width, height, null, width, height);
        source.BlendMode = BlendMode.Normal;
        source.Opacity = 1.0;

        byte[]? compositeBuffer = null;
        var composite = BuildCompositeFrame(new List<CaptureSource> { source }, ref compositeBuffer, useEngineDimensions: true, animationTime: 0.0,
            includeCpuReadback: false);
        if (composite?.GpuSurface == null)
        {
            Logger.Warn("GPU injection-mode smoke: composite surface was unavailable.");
            return false;
        }

        static double MeasureDensity(byte[] buffer)
        {
            long lit = 0;
            long total = buffer.Length / 4;
            for (int i = 0; i < buffer.Length; i += 4)
            {
                if (buffer[i] != 0 || buffer[i + 1] != 0 || buffer[i + 2] != 0)
                {
                    lit++;
                }
            }

            return total == 0 ? 0.0 : lit / (double)total;
        }

        double RunMode(GameOfLifeEngine.InjectionMode mode, int pulseStep)
        {
            using var backend = new GpuSimulationBackend();
            backend.Configure(composite.DownscaledHeight, 24, (double)composite.DownscaledWidth / composite.DownscaledHeight);
            backend.SetBinningMode(GameOfLifeEngine.BinningMode.Fill);
            backend.SetInjectionMode(mode);
            backend.SetMode(GameOfLifeEngine.LifeMode.NaiveGrayscale);
            if (!backend.IsGpuActive)
            {
                return -1.0;
            }

            bool injected = backend.TryInjectCompositeSurface(
                composite.GpuSurface,
                min,
                max,
                invertThreshold: false,
                mode,
                noiseProbability: 0.0,
                period: backend.Depth,
                pulseStep: pulseStep,
                invertInput: false,
                hueShiftDegrees: 0.0);
            if (!injected)
            {
                return -1.0;
            }

            byte[] colorBuffer = new byte[backend.Columns * backend.Rows * 4];
            backend.FillColorBuffer(colorBuffer);
            return MeasureDensity(colorBuffer);
        }

        double thresholdDensity = RunMode(GameOfLifeEngine.InjectionMode.Threshold, 0);
        double randomDensity = RunMode(GameOfLifeEngine.InjectionMode.RandomPulse, 0);
        double pwmPhase0Density = RunMode(GameOfLifeEngine.InjectionMode.PulseWidthModulation, 0);
        double pwmPhase23Density = RunMode(GameOfLifeEngine.InjectionMode.PulseWidthModulation, 23);

        bool ok = thresholdDensity > 0.99 &&
                  randomDensity > 0.30 && randomDensity < 0.70 &&
                  pwmPhase0Density > 0.99 &&
                  pwmPhase23Density < 0.01;
        Logger.Info($"GPU injection-mode smoke: threshold={thresholdDensity:0.000}, random={randomDensity:0.000}, pwmPhase0={pwmPhase0Density:0.000}, pwmPhase23={pwmPhase23Density:0.000}, ok={ok}.");
        return ok;
    }

    internal bool RunGpuFileInjectionModeSmoke(string smokeVideoPath)
    {
        if (string.IsNullOrWhiteSpace(smokeVideoPath))
        {
            Logger.Warn("GPU file injection-mode smoke: no video path provided.");
            return false;
        }

        const int width = 128;
        const int height = 72;
        const double min = 0.5;
        const double max = 1.0;

        if (!_fileCapture.TryGetOrAdd(smokeVideoPath, out var info, out var error))
        {
            Logger.Warn($"GPU file injection-mode smoke: failed to prepare source '{smokeVideoPath}': {error ?? "unknown error"}.");
            return false;
        }

        FileCaptureService.FileCaptureFrame? fileFrame = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            fileFrame = _fileCapture.CaptureFrame(info.Path, width, height, FitMode.Fill, includeSource: true);
            if (fileFrame.HasValue)
            {
                break;
            }

            System.Threading.Thread.Sleep(50);
        }

        if (!fileFrame.HasValue)
        {
            Logger.Warn("GPU file injection-mode smoke: file frame was unavailable.");
            return false;
        }

        var source = CaptureSource.CreateFile(info.Path, info.DisplayName, info.Width, info.Height);
        source.LastFrame = new SourceFrame(
            fileFrame.Value.OverlayDownscaled,
            fileFrame.Value.DownscaledWidth,
            fileFrame.Value.DownscaledHeight,
            fileFrame.Value.OverlaySource,
            fileFrame.Value.SourceWidth,
            fileFrame.Value.SourceHeight);
        source.BlendMode = BlendMode.Normal;
        source.Opacity = 1.0;

        byte[]? compositeBuffer = null;
        var composite = BuildCompositeFrame(new List<CaptureSource> { source }, ref compositeBuffer, useEngineDimensions: true, animationTime: 0.0,
            includeCpuReadback: false);
        if (composite?.GpuSurface == null)
        {
            Logger.Warn("GPU file injection-mode smoke: composite surface was unavailable.");
            return false;
        }

        static double MeasureDensity(byte[] buffer)
        {
            long lit = 0;
            long total = buffer.Length / 4;
            for (int i = 0; i < buffer.Length; i += 4)
            {
                if (buffer[i] != 0 || buffer[i + 1] != 0 || buffer[i + 2] != 0)
                {
                    lit++;
                }
            }

            return total == 0 ? 0.0 : lit / (double)total;
        }

        double RunMode(GameOfLifeEngine.InjectionMode mode, int pulseStep)
        {
            using var backend = new GpuSimulationBackend();
            backend.Configure(composite.DownscaledHeight, 24, (double)composite.DownscaledWidth / composite.DownscaledHeight);
            backend.SetBinningMode(GameOfLifeEngine.BinningMode.Fill);
            backend.SetInjectionMode(mode);
            backend.SetMode(GameOfLifeEngine.LifeMode.NaiveGrayscale);
            if (!backend.IsGpuActive)
            {
                return -1.0;
            }

            bool injected = backend.TryInjectCompositeSurface(
                composite.GpuSurface,
                min,
                max,
                invertThreshold: false,
                mode,
                noiseProbability: 0.0,
                period: backend.Depth,
                pulseStep: pulseStep,
                invertInput: false,
                hueShiftDegrees: 0.0);
            if (!injected)
            {
                return -1.0;
            }

            byte[] colorBuffer = new byte[backend.Columns * backend.Rows * 4];
            backend.FillColorBuffer(colorBuffer);
            return MeasureDensity(colorBuffer);
        }

        double thresholdDensity = RunMode(GameOfLifeEngine.InjectionMode.Threshold, 0);
        double randomDensity = RunMode(GameOfLifeEngine.InjectionMode.RandomPulse, 0);
        double pwmPhase0Density = RunMode(GameOfLifeEngine.InjectionMode.PulseWidthModulation, 0);
        double pwmPhase23Density = RunMode(GameOfLifeEngine.InjectionMode.PulseWidthModulation, 23);

        Logger.Info($"GPU file injection-mode smoke ({Path.GetFileName(info.Path)}): threshold={thresholdDensity:0.000}, random={randomDensity:0.000}, pwmPhase0={pwmPhase0Density:0.000}, pwmPhase23={pwmPhase23Density:0.000}.");
        return true;
    }

    internal bool RunGpuFrequencyHueSmoke()
    {
        const int width = 128;
        const int height = 72;
        const double baseHueDegrees = 30.0;
        const double reactiveHueDegrees = 120.0;

        if (!_renderBackend.SupportsGpuSimulationComposition)
        {
            Logger.Warn("GPU frequency-hue smoke: render backend does not support GPU simulation composition.");
            return false;
        }

        using var backend = new GpuSimulationBackend();
        backend.Configure(height, 24, (double)width / height);
        backend.SetBinningMode(GameOfLifeEngine.BinningMode.Fill);
        backend.SetInjectionMode(GameOfLifeEngine.InjectionMode.Threshold);
        backend.SetMode(GameOfLifeEngine.LifeMode.RgbChannels);
        if (!backend.IsGpuActive)
        {
            Logger.Warn("GPU frequency-hue smoke: simulation backend was not active.");
            return false;
        }

        bool[,] redMask = new bool[backend.Rows, backend.Columns];
        bool[,] emptyMask = new bool[backend.Rows, backend.Columns];
        for (int row = 0; row < backend.Rows; row++)
        {
            for (int col = 0; col < backend.Columns; col++)
            {
                redMask[row, col] = true;
            }
        }

        backend.InjectRgbFrame(redMask, emptyMask, emptyMask);

        var layer = new SimulationLayerState
        {
            Id = Guid.NewGuid(),
            Name = "FreqHue",
            Enabled = true,
            InputFunction = SimulationInputFunction.Direct,
            BlendMode = BlendMode.Additive,
            InjectionMode = GameOfLifeEngine.InjectionMode.Threshold,
            LifeMode = GameOfLifeEngine.LifeMode.RgbChannels,
            BinningMode = GameOfLifeEngine.BinningMode.Fill,
            LifeOpacity = 1.0,
            RgbHueShiftDegrees = baseHueDegrees,
            RgbHueShiftSpeedDegreesPerSecond = 0.0,
            AudioFrequencyHueShiftDegrees = reactiveHueDegrees,
            Engine = backend
        };

        double previousSmoothedFreq = _smoothedFreq;
        bool previousRecording = _isRecording;
        try
        {
            _isRecording = false;

            _smoothedFreq = MinReactiveHueFrequencyHz;
            if (!TryBuildSimulationPresentationLayer(layer, 1.0, out var lowPresentation))
            {
                Logger.Warn("GPU frequency-hue smoke: failed to build low-frequency presentation layer.");
                return false;
            }

            byte[]? lowBuffer = lowPresentation.Buffer;
            if (lowBuffer == null || lowBuffer.Length < backend.Columns * backend.Rows * 4)
            {
                Logger.Warn("GPU frequency-hue smoke: low-frequency presentation buffer was unavailable.");
                return false;
            }

            int centerIndex = ((backend.Rows / 2) * backend.Columns + (backend.Columns / 2)) * 4;
            byte lowR = lowBuffer[centerIndex];
            byte lowG = lowBuffer[centerIndex + 1];
            byte lowB = lowBuffer[centerIndex + 2];
            float lowHue = lowPresentation.HueShiftDegrees;

            _smoothedFreq = MaxReactiveHueFrequencyHz;
            if (!TryBuildSimulationPresentationLayer(layer, 1.0, out var highPresentation))
            {
                Logger.Warn("GPU frequency-hue smoke: failed to build high-frequency presentation layer.");
                return false;
            }

            byte[]? highBuffer = highPresentation.Buffer;
            if (highBuffer == null || highBuffer.Length < backend.Columns * backend.Rows * 4)
            {
                Logger.Warn("GPU frequency-hue smoke: high-frequency presentation buffer was unavailable.");
                return false;
            }

            byte highR = highBuffer[centerIndex];
            byte highG = highBuffer[centerIndex + 1];
            byte highB = highBuffer[centerIndex + 2];
            float highHue = highPresentation.HueShiftDegrees;

            bool lowHueOk = Math.Abs(lowHue - baseHueDegrees) <= 0.5f;
            bool highHueOk = Math.Abs(highHue - (baseHueDegrees + reactiveHueDegrees)) <= 0.5f;
            bool hueChanged = highHue > lowHue + 100.0f;
            bool rawBufferStable = lowR == highR && lowG == highG && lowB == highB;
            bool rawBufferLooksUnshifted = lowR > 0 && lowR >= lowG && lowR >= lowB;
            bool ok = lowHueOk && highHueOk && hueChanged && rawBufferStable && rawBufferLooksUnshifted;

            Logger.Info(
                $"GPU frequency-hue smoke: lowHue={lowHue:0.##}deg, highHue={highHue:0.##}deg, " +
                $"lowCenter=({lowR},{lowG},{lowB}), highCenter=({highR},{highG},{highB}), ok={ok}.");
            return ok;
        }
        finally
        {
            _smoothedFreq = previousSmoothedFreq;
            _isRecording = previousRecording;
        }
    }

    internal bool RunSimulationReactiveMappingsSmoke()
    {
        EnsureSimulationLayersInitialized();
        var layer = EnumerateSimulationLeafLayers(_simulationLayers).FirstOrDefault();
        if (layer == null)
        {
            Logger.Warn("Simulation reactive mappings smoke: no layers were initialized.");
            return false;
        }
        double previousSimulationTargetFps = _currentSimulationTargetFps;
        double previousFastAudioLevel = _fastAudioLevel;
        bool previousAudioReactiveEnabled = _audioReactiveEnabled;
        string? previousAudioDeviceId = _selectedAudioDeviceId;
        try
        {
            layer.Enabled = true;
            layer.LifeMode = GameOfLifeEngine.LifeMode.RgbChannels;
            layer.LifeOpacity = 0.8;
            layer.RgbHueShiftDegrees = 30.0;
            layer.ReactiveMappings = new List<SimulationReactiveMapping>
            {
                new()
                {
                    Input = SimulationReactiveInput.Level,
                    Output = SimulationReactiveOutput.Opacity,
                    Amount = 1.0,
                    ThresholdMin = 0.0,
                    ThresholdMax = 0.5
                },
                new()
                {
                    Input = SimulationReactiveInput.Level,
                    Output = SimulationReactiveOutput.Framerate,
                    Amount = 1.0,
                    ThresholdMin = 0.0,
                    ThresholdMax = 0.5
                },
                new()
                {
                    Input = SimulationReactiveInput.Frequency,
                    Output = SimulationReactiveOutput.HueShift,
                    Amount = 180.0,
                    ThresholdMin = 0.4,
                    ThresholdMax = 0.6
                },
                new()
                {
                    Input = SimulationReactiveInput.Bass,
                    Output = SimulationReactiveOutput.InjectionNoise,
                    Amount = 0.5,
                    ThresholdMin = 0.0,
                    ThresholdMax = 0.2
                },
                new()
                {
                    Input = SimulationReactiveInput.Mid,
                    Output = SimulationReactiveOutput.ThresholdMin,
                    Amount = 0.2,
                    ThresholdMin = 0.1,
                    ThresholdMax = 0.3
                },
                new()
                {
                    Input = SimulationReactiveInput.High,
                    Output = SimulationReactiveOutput.ThresholdMax,
                    Amount = 0.1,
                    ThresholdMin = 0.2,
                    ThresholdMax = 0.4
                },
                new()
                {
                    Input = SimulationReactiveInput.Level,
                    Output = SimulationReactiveOutput.HueSpeed,
                    Amount = 60.0,
                    ThresholdMin = 0.0,
                    ThresholdMax = 0.5
                }
            };

            _audioReactiveEnabled = false;
            _selectedAudioDeviceId = "smoke";
            _currentSimulationTargetFps = 60.0;
            _fastAudioLevel = 0.25;
            layer.InjectionNoise = 0.1;
            layer.ThresholdMin = 0.35;
            layer.ThresholdMax = 0.75;
            layer.RgbHueShiftSpeedDegreesPerSecond = 5.0;
            _audioBeatDetector.SetSmokeReactiveState(
                level: 0.25,
                bass: 0.1,
                mid: 0.2,
                high: 0.3,
                frequency: 0.5,
                bassFrequency: 0.4,
                midFrequency: 0.5,
                highFrequency: 0.6);

            ApplySimulationLayerReactiveState();

            bool opacityOk = Math.Abs(layer.EffectiveLifeOpacity - 0.4) < 0.0001;
            bool fpsOk = Math.Abs(layer.EffectiveSimulationTargetFps - 30.0) < 0.0001;
            bool reactiveHueOk = Math.Abs(layer.ReactiveHueShiftDegrees - 90.0) < 0.0001;
            bool finalHueOk = Math.Abs(CurrentRgbHueShiftDegrees(layer) - 120.0) < 0.0001;
            bool noiseOk = Math.Abs(layer.EffectiveInjectionNoise - 0.35) < 0.0001;
            bool thresholdMinOk = Math.Abs(layer.EffectiveThresholdMin - 0.45) < 0.0001;
            bool thresholdMaxOk = Math.Abs(layer.EffectiveThresholdMax - 0.70) < 0.0001;
            bool hueSpeedOk = Math.Abs(layer.EffectiveRgbHueShiftSpeedDegreesPerSecond - 35.0) < 0.0001;
            bool ok = opacityOk && fpsOk && reactiveHueOk && finalHueOk && noiseOk && thresholdMinOk && thresholdMaxOk && hueSpeedOk;

            Logger.Info(
                $"Simulation reactive mappings smoke: globalAudioReactiveEnabled={_audioReactiveEnabled}, " +
                $"opacity={layer.EffectiveLifeOpacity:0.###}, fps={layer.EffectiveSimulationTargetFps:0.###}, " +
                $"reactiveHue={layer.ReactiveHueShiftDegrees:0.###}, finalHue={CurrentRgbHueShiftDegrees(layer):0.###}, " +
                $"noise={layer.EffectiveInjectionNoise:0.###}, thMin={layer.EffectiveThresholdMin:0.###}, thMax={layer.EffectiveThresholdMax:0.###}, " +
                $"hueSpeed={layer.EffectiveRgbHueShiftSpeedDegreesPerSecond:0.###}, ok={ok}.");
            return ok;
        }
        finally
        {
            _currentSimulationTargetFps = previousSimulationTargetFps;
            _fastAudioLevel = previousFastAudioLevel;
            _audioReactiveEnabled = previousAudioReactiveEnabled;
            _selectedAudioDeviceId = previousAudioDeviceId;
        }
    }

    internal bool RunPixelSortReactiveCellSizeSmoke()
    {
        double previousSimulationTargetFps = _currentSimulationTargetFps;
        double previousFastAudioLevel = _fastAudioLevel;
        bool previousAudioReactiveEnabled = _audioReactiveEnabled;
        string? previousAudioDeviceId = _selectedAudioDeviceId;

        try
        {
            var layerId = Guid.NewGuid();
            ApplySimulationLayerSpecs(new List<SimulationLayerSpec>
            {
                new()
                {
                    Id = layerId,
                    Kind = LayerEditorSimulationItemKind.Layer,
                    LayerType = SimulationLayerType.PixelSort,
                    Name = "Pixel Sort Reactive Test",
                    Enabled = true,
                    InputFunction = SimulationInputFunction.Direct,
                    BlendMode = BlendMode.Normal,
                    InjectionMode = GameOfLifeEngine.InjectionMode.Threshold,
                    LifeMode = GameOfLifeEngine.LifeMode.NaiveGrayscale,
                    BinningMode = GameOfLifeEngine.BinningMode.Fill,
                    InjectionNoise = 0.0,
                    LifeOpacity = 1.0,
                    ThresholdMin = 0.0,
                    ThresholdMax = 1.0,
                    InvertThreshold = false,
                    PixelSortCellWidth = 4,
                    PixelSortCellHeight = 6,
                    ReactiveMappings = new List<SimulationReactiveMapping>
                    {
                        new()
                        {
                            Input = SimulationReactiveInput.Level,
                            Output = SimulationReactiveOutput.PixelSortCellWidth,
                            Amount = 8.0
                        },
                        new()
                        {
                            Input = SimulationReactiveInput.High,
                            Output = SimulationReactiveOutput.PixelSortCellHeight,
                            Amount = 5.0
                        }
                    }
                }
            });

            var layer = EnumerateSimulationLeafLayers(_simulationLayers).FirstOrDefault(candidate => candidate.Id == layerId);
            if (layer == null || layer.Engine is not GpuPixelSortBackend pixelSortBackend)
            {
                Logger.Warn("Pixel sort reactive cell size smoke: pixel-sort runtime layer was unavailable.");
                return false;
            }

            _audioReactiveEnabled = false;
            _selectedAudioDeviceId = "smoke";
            _currentSimulationTargetFps = 60.0;
            _fastAudioLevel = 0.5;
            _audioBeatDetector.SetSmokeReactiveState(
                level: 0.5,
                bass: 0.1,
                mid: 0.2,
                high: 0.4,
                frequency: 0.0,
                bassFrequency: 0.0,
                midFrequency: 0.0,
                highFrequency: 0.0);

            ApplySimulationLayerReactiveState();

            int expectedCellWidth = 8;
            int expectedCellHeight = 8;
            bool widthOk = layer.EffectivePixelSortCellWidth == expectedCellWidth;
            bool heightOk = layer.EffectivePixelSortCellHeight == expectedCellHeight;

            byte[] sourceBuffer = BuildSmokePixelSortPatternBgra(layer.Engine.Columns, layer.Engine.Rows);
            var source = CaptureSource.CreateFile("pixel-sort-reactive-source", "Pixel Sort Reactive Source", layer.Engine.Columns, layer.Engine.Rows);
            source.LastFrame = new SourceFrame(sourceBuffer, layer.Engine.Columns, layer.Engine.Rows, null, layer.Engine.Columns, layer.Engine.Rows);
            source.BlendMode = BlendMode.Normal;
            source.Opacity = 1.0;

            byte[]? compositeBuffer = null;
            var composite = BuildCompositeFrame(
                new List<CaptureSource> { source },
                ref compositeBuffer,
                useEngineDimensions: true,
                animationTime: 0.0,
                includeCpuReadback: true);

            bool injected = composite?.GpuSurface != null &&
                            pixelSortBackend.TryInjectCompositeSurface(
                                composite.GpuSurface,
                                0.0,
                                1.0,
                                invertThreshold: false,
                                GameOfLifeEngine.InjectionMode.Threshold,
                                noiseProbability: 0.0,
                                period: 1,
                                pulseStep: 0,
                                invertInput: false,
                                hueShiftDegrees: 0.0);

            pixelSortBackend.Step();

            byte[] sorted = new byte[pixelSortBackend.Columns * pixelSortBackend.Rows * 4];
            pixelSortBackend.FillColorBuffer(sorted);
            bool histogramPreserved = HaveMatchingPixelHistogram(sourceBuffer, sorted, Math.Min(sourceBuffer.Length, sorted.Length) / 4);

            bool ok = widthOk && heightOk && injected && histogramPreserved;
            Logger.Info(
                $"Pixel sort reactive cell size smoke: width={layer.EffectivePixelSortCellWidth}, height={layer.EffectivePixelSortCellHeight}, " +
                $"expected=({expectedCellWidth},{expectedCellHeight}), injected={injected}, histogramPreserved={histogramPreserved}, ok={ok}.");
            return ok;
        }
        finally
        {
            _currentSimulationTargetFps = previousSimulationTargetFps;
            _fastAudioLevel = previousFastAudioLevel;
            _audioReactiveEnabled = previousAudioReactiveEnabled;
            _selectedAudioDeviceId = previousAudioDeviceId;
        }
    }

    internal bool RunSimulationReactiveMappingsPersistenceSmoke()
    {
        var editorLayer = new LayerEditorSimulationLayer
        {
            Id = Guid.NewGuid(),
            Name = "Reactive Test",
            Enabled = true,
            InputFunction = nameof(SimulationInputFunction.Direct),
            BlendMode = nameof(BlendMode.Additive),
            InjectionMode = nameof(GameOfLifeEngine.InjectionMode.RandomPulse),
            LifeMode = nameof(GameOfLifeEngine.LifeMode.RgbChannels),
            BinningMode = nameof(GameOfLifeEngine.BinningMode.Fill),
            InjectionNoise = 0.25,
            LifeOpacity = 0.75,
            RgbHueShiftDegrees = 12.0,
            RgbHueShiftSpeedDegreesPerSecond = 3.0,
            ThresholdMin = 0.2,
            ThresholdMax = 0.8,
            InvertThreshold = false,
            ReactiveMappings = new ObservableCollection<LayerEditorSimulationReactiveMapping>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Input = nameof(SimulationReactiveInput.Level),
                    Output = nameof(SimulationReactiveOutput.Opacity),
                    Amount = 0.8,
                    ThresholdMin = 0.2,
                    ThresholdMax = 0.7
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    Input = nameof(SimulationReactiveInput.Mid),
                    Output = nameof(SimulationReactiveOutput.Framerate),
                    Amount = 0.65,
                    ThresholdMin = 0.1,
                    ThresholdMax = 0.6
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    Input = nameof(SimulationReactiveInput.HighFrequency),
                    Output = nameof(SimulationReactiveOutput.HueShift),
                    Amount = 210.0,
                    ThresholdMin = 0.25,
                    ThresholdMax = 0.9
                }
            }
        };

        static bool EditorMappingsEqual(
            IReadOnlyList<LayerEditorSimulationReactiveMapping> left,
            IReadOnlyList<LayerEditorSimulationReactiveMapping> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i].Id != right[i].Id ||
                    !string.Equals(left[i].Input, right[i].Input, StringComparison.Ordinal) ||
                    !string.Equals(left[i].Output, right[i].Output, StringComparison.Ordinal) ||
                    Math.Abs(left[i].Amount - right[i].Amount) > 0.000001 ||
                    Math.Abs(left[i].ThresholdMin - right[i].ThresholdMin) > 0.000001 ||
                    Math.Abs(left[i].ThresholdMax - right[i].ThresholdMax) > 0.000001)
                {
                    return false;
                }
            }

            return true;
        }

        var projectSettings = new LayerEditorProjectSettings
        {
            Height = 480,
            Depth = 24,
            Framerate = 60,
            LifeOpacity = 1.0
        };

        var file = LayerConfigFile.FromEditorSources(
            Array.Empty<LayerEditorSource>(),
            new[] { editorLayer },
            projectSettings);
        List<LayerEditorSimulationLayer> roundTripLayers = file.ToEditorSimulationLayers();
        bool projectRoundTripOk =
            roundTripLayers.Count == 1 &&
            Math.Abs(file.SimulationLayers[0].AudioFrequencyHueShiftDegrees) <= 0.000001 &&
            Math.Abs(roundTripLayers[0].AudioFrequencyHueShiftDegrees) <= 0.000001 &&
            EditorMappingsEqual(editorLayer.ReactiveMappings, roundTripLayers[0].ReactiveMappings.ToList());

        var appConfigs = new List<AppConfig.SimulationLayerConfig>
        {
            new()
            {
                Id = editorLayer.Id,
                Name = editorLayer.Name,
                Enabled = editorLayer.Enabled,
                InputFunction = editorLayer.InputFunction,
                BlendMode = editorLayer.BlendMode,
                InjectionMode = editorLayer.InjectionMode,
                LifeMode = editorLayer.LifeMode,
                BinningMode = editorLayer.BinningMode,
                InjectionNoise = editorLayer.InjectionNoise,
                LifeOpacity = editorLayer.LifeOpacity,
                RgbHueShiftDegrees = editorLayer.RgbHueShiftDegrees,
                RgbHueShiftSpeedDegreesPerSecond = editorLayer.RgbHueShiftSpeedDegreesPerSecond,
                AudioFrequencyHueShiftDegrees = 180.0,
                ThresholdMin = editorLayer.ThresholdMin,
                ThresholdMax = editorLayer.ThresholdMax,
                InvertThreshold = editorLayer.InvertThreshold,
                ReactiveMappings = editorLayer.ReactiveMappings.Select(mapping => new AppConfig.ReactiveMappingConfig
                {
                    Id = mapping.Id,
                    Input = mapping.Input,
                    Output = mapping.Output,
                    Amount = mapping.Amount,
                    ThresholdMin = mapping.ThresholdMin,
                    ThresholdMax = mapping.ThresholdMax
                }).ToList()
            }
        };

        List<SimulationLayerSpec> normalizedSpecs = NormalizeSimulationLayerSpecs(appConfigs);
        bool appConfigRoundTripOk =
            normalizedSpecs.Count == 1 &&
            Math.Abs(normalizedSpecs[0].AudioFrequencyHueShiftDegrees) <= 0.000001 &&
            normalizedSpecs[0].ReactiveMappings.Count == editorLayer.ReactiveMappings.Count &&
            normalizedSpecs[0].ReactiveMappings.Zip(editorLayer.ReactiveMappings, (runtime, editor) =>
                runtime.Id == editor.Id &&
                runtime.Input.ToString() == editor.Input &&
                runtime.Output.ToString() == editor.Output &&
                Math.Abs(runtime.Amount - editor.Amount) <= 0.000001 &&
                Math.Abs(runtime.ThresholdMin - editor.ThresholdMin) <= 0.000001 &&
                Math.Abs(runtime.ThresholdMax - editor.ThresholdMax) <= 0.000001).All(match => match);

        bool ok = projectRoundTripOk && appConfigRoundTripOk;
        Logger.Info(
            $"Simulation reactive persistence smoke: projectRoundTrip={projectRoundTripOk}, " +
            $"appConfigRoundTrip={appConfigRoundTripOk}, ok={ok}.");
        return ok;
    }

    internal bool RunSimulationReactiveLegacyMigrationSmoke()
    {
        bool previousAudioReactiveEnabled = _audioReactiveEnabled;
        bool previousLevelToFpsEnabled = _audioReactiveLevelToFpsEnabled;
        bool previousLevelToOpacityEnabled = _audioReactiveLevelToLifeOpacityEnabled;
        double previousFpsMinPercent = _audioReactiveFpsMinPercent;
        double previousOpacityMinScalar = _audioReactiveLifeOpacityMinScalar;

        try
        {
            _audioReactiveEnabled = true;
            _audioReactiveLevelToFpsEnabled = true;
            _audioReactiveLevelToLifeOpacityEnabled = true;
            _audioReactiveFpsMinPercent = 0.35;
            _audioReactiveLifeOpacityMinScalar = 0.2;

            var specs = new List<SimulationLayerSpec>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "Reactive Legacy",
                    Enabled = true,
                    InputFunction = SimulationInputFunction.Direct,
                    BlendMode = BlendMode.Additive,
                    InjectionMode = GameOfLifeEngine.InjectionMode.Threshold,
                    LifeMode = GameOfLifeEngine.LifeMode.RgbChannels,
                    BinningMode = GameOfLifeEngine.BinningMode.Fill,
                    InjectionNoise = 0,
                    LifeOpacity = 1.0,
                    RgbHueShiftDegrees = 0,
                    RgbHueShiftSpeedDegreesPerSecond = 0,
                    AudioFrequencyHueShiftDegrees = 0,
                    ReactiveMappings = new List<SimulationReactiveMapping>(),
                    ThresholdMin = 0.35,
                    ThresholdMax = 0.75,
                    InvertThreshold = false
                }
            };

            bool migrated = MigrateLegacyGlobalReactiveMappings(specs);
            var mappings = specs[0].ReactiveMappings;
            var fpsMapping = mappings.FirstOrDefault(mapping =>
                mapping.Input == SimulationReactiveInput.Level &&
                mapping.Output == SimulationReactiveOutput.Framerate);
            var opacityMapping = mappings.FirstOrDefault(mapping =>
                mapping.Input == SimulationReactiveInput.Level &&
                mapping.Output == SimulationReactiveOutput.Opacity);

            bool hasFps = fpsMapping != null;
            bool hasOpacity = opacityMapping != null;
            bool amountsOk =
                fpsMapping != null && Math.Abs(fpsMapping.Amount - 0.65) < 0.000001 &&
                opacityMapping != null && Math.Abs(opacityMapping.Amount - 0.8) < 0.000001;
            bool flagsCleared = !_audioReactiveLevelToFpsEnabled && !_audioReactiveLevelToLifeOpacityEnabled;
            bool ok = migrated && hasFps && hasOpacity && amountsOk && flagsCleared;

            Logger.Info(
                $"Simulation reactive legacy migration smoke: migrated={migrated}, hasFps={hasFps}, hasOpacity={hasOpacity}, " +
                $"amountsOk={amountsOk}, flagsCleared={flagsCleared}, ok={ok}.");
            return ok;
        }
        finally
        {
            _audioReactiveEnabled = previousAudioReactiveEnabled;
            _audioReactiveLevelToFpsEnabled = previousLevelToFpsEnabled;
            _audioReactiveLevelToLifeOpacityEnabled = previousLevelToOpacityEnabled;
            _audioReactiveFpsMinPercent = previousFpsMinPercent;
            _audioReactiveLifeOpacityMinScalar = previousOpacityMinScalar;
        }
    }

    internal bool RunSimulationReactiveRemovalSmoke()
    {
        EnsureSimulationLayersInitialized();
        var layer = EnumerateSimulationLeafLayers(_simulationLayers).FirstOrDefault();
        if (layer == null)
        {
            Logger.Warn("Simulation reactive removal smoke: no layers were initialized.");
            return false;
        }
        double previousSimulationTargetFps = _currentSimulationTargetFps;
        double previousFastAudioLevel = _fastAudioLevel;
        string? previousAudioDeviceId = _selectedAudioDeviceId;
        bool previousAudioReactiveEnabled = _audioReactiveEnabled;
        var previousMappings = CloneReactiveMappings(layer.ReactiveMappings);
        double previousLifeOpacity = layer.LifeOpacity;

        try
        {
            layer.Enabled = true;
            layer.LifeOpacity = 0.8;
            layer.ReactiveMappings = new List<SimulationReactiveMapping>
            {
                new()
                {
                    Input = SimulationReactiveInput.Level,
                    Output = SimulationReactiveOutput.Opacity,
                    Amount = 1.0
                }
            };

            _audioReactiveEnabled = false;
            _selectedAudioDeviceId = "smoke";
            _currentSimulationTargetFps = 60.0;
            _fastAudioLevel = 0.25;

            ApplySimulationLayerReactiveState();
            bool beforeRemovalOk = Math.Abs(layer.EffectiveLifeOpacity - 0.2) < 0.0001;

            layer.ReactiveMappings.Clear();
            ApplySimulationLayerReactiveState();
            bool afterRemovalOk = Math.Abs(layer.EffectiveLifeOpacity - 0.8) < 0.0001;

            bool ok = beforeRemovalOk && afterRemovalOk;
            Logger.Info(
                $"Simulation reactive removal smoke: beforeRemovalOk={beforeRemovalOk}, afterRemovalOk={afterRemovalOk}, ok={ok}.");
            return ok;
        }
        finally
        {
            _currentSimulationTargetFps = previousSimulationTargetFps;
            _fastAudioLevel = previousFastAudioLevel;
            _selectedAudioDeviceId = previousAudioDeviceId;
            _audioReactiveEnabled = previousAudioReactiveEnabled;
            layer.LifeOpacity = previousLifeOpacity;
            layer.ReactiveMappings = previousMappings;
        }
    }

    internal bool RunGpuPassthroughCompositionSmoke()
    {
        if (!_renderLoopAttached)
        {
            InitializeVisualizer();
        }

        if (_sources.Count > 0)
        {
            ClearSources();
        }

        EnsureSimulationLayersInitialized();
        _audioReactiveEnabled = false;
        _isRecording = false;
        _passthroughEnabled = true;
        _blendMode = BlendMode.Additive;
        _lifeOpacity = 1.0;
        _effectiveLifeOpacity = 1.0;

        ApplyDimensions(144, 24, DefaultAspectRatio, persist: false);

        int width = GetReferenceSimulationEngine().Columns;
        int height = GetReferenceSimulationEngine().Rows;
        var source = CaptureSource.CreateFile("gpu-passthrough-source", "GPU Passthrough", width, height);
        source.LastFrame = new SourceFrame(BuildSmokeGradientBgra(width, height), width, height, null, width, height);
        source.BlendMode = BlendMode.Additive;
        source.Opacity = 1.0;
        _sources.Add(source);
        UpdatePrimaryAspectIfNeeded();

        _compositeDownscaledBuffer = null;
        _lastCompositeFrame = null;
        _passthroughCompositedInPixelBuffer = false;

        InjectCaptureFrames();
        RenderFrame();

        int cpuReadbackBytes = _lastCompositeFrame?.Downscaled.Length ?? -1;
        bool gpuSurfacePresent = _lastCompositeFrame?.GpuSurface != null;
        bool compositedInFinalFrame = _passthroughCompositedInPixelBuffer;
        bool overlayDisabled = !ShouldUseShaderPassthrough();

        Logger.Info(
            $"GPU passthrough smoke: cpuReadbackBytes={cpuReadbackBytes}, gpuSurfacePresent={gpuSurfacePresent}, composited={compositedInFinalFrame}, overlayDisabled={overlayDisabled}.");

        return cpuReadbackBytes > 0 &&
               gpuSurfacePresent &&
               compositedInFinalFrame &&
               overlayDisabled;
    }

    internal bool RunGpuPassthroughSignedModelSmoke()
    {
        bool withCpuUnderlay = ShouldUseSignedAddSubPassthrough(
            hasPassthroughBaseline: true,
            hasActiveSimulationLayers: true,
            hasEnabledNonAddSubSimulationLayer: false);
        bool withGpuUnderlay = ShouldUseSignedAddSubPassthrough(
            hasPassthroughBaseline: true,
            hasActiveSimulationLayers: true,
            hasEnabledNonAddSubSimulationLayer: false);
        bool withoutPassthrough = ShouldUseSignedAddSubPassthrough(
            hasPassthroughBaseline: false,
            hasActiveSimulationLayers: true,
            hasEnabledNonAddSubSimulationLayer: false);
        bool withNormalLayer = ShouldUseSignedAddSubPassthrough(
            hasPassthroughBaseline: true,
            hasActiveSimulationLayers: true,
            hasEnabledNonAddSubSimulationLayer: true);

        bool ok = withCpuUnderlay && withGpuUnderlay && !withoutPassthrough && !withNormalLayer;
        Logger.Info(
            $"GPU passthrough signed-model smoke: cpuUnderlay={withCpuUnderlay}, gpuUnderlay={withGpuUnderlay}, " +
            $"withoutPassthrough={withoutPassthrough}, withNormalLayer={withNormalLayer}, ok={ok}.");
        return ok;
    }

    internal bool RunPassthroughUnderlayOnlySmoke()
    {
        if (!_renderLoopAttached)
        {
            InitializeVisualizer();
        }

        if (_sources.Count > 0)
        {
            ClearSources();
        }

        EnsureSimulationLayersInitialized();
        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            layer.Enabled = false;
        }

        _audioReactiveEnabled = false;
        _isRecording = false;
        _passthroughEnabled = true;
        _blendMode = BlendMode.Additive;
        _lifeOpacity = 1.0;
        _effectiveLifeOpacity = 1.0;

        ApplyDimensions(144, 24, DefaultAspectRatio, persist: false);

        int width = GetReferenceSimulationEngine().Columns;
        int height = GetReferenceSimulationEngine().Rows;
        var source = CaptureSource.CreateFile("passthrough-underlay-only", "Passthrough Underlay Only", width, height);
        source.LastFrame = new SourceFrame(BuildSmokeGradientBgra(width, height), width, height, null, width, height);
        source.BlendMode = BlendMode.Additive;
        source.Opacity = 1.0;
        _sources.Add(source);
        UpdatePrimaryAspectIfNeeded();

        _compositeDownscaledBuffer = null;
        _lastCompositeFrame = null;
        FrameTimer_Tick(null, EventArgs.Empty);

        bool outputVisible = BufferHasNonBlackPixel(_pixelBuffer);
        bool compositedInFinalFrame = _passthroughCompositedInPixelBuffer;
        bool overlayDisabled = !ShouldUseShaderPassthrough();
        Logger.Info(
            $"Passthrough underlay-only smoke: outputVisible={outputVisible}, composited={compositedInFinalFrame}, overlayDisabled={overlayDisabled}.");
        return outputVisible && compositedInFinalFrame && overlayDisabled;
    }

    internal void ConfigureProfilingSmokeScene(int rows = 240, bool rgbMode = false, string? smokeVideoPath = null, bool includeSimGroup = false)
    {
        if (!_renderLoopAttached)
        {
            InitializeVisualizer();
        }

        if (_sources.Count > 0)
        {
            ClearSources();
        }

        EnsureSimulationLayersInitialized();
        _suppressSmokeIntermediateSimGroupReadback = includeSimGroup;
        _audioReactiveEnabled = false;
        _passthroughEnabled = true;
        _blendMode = BlendMode.Additive;
        _lifeOpacity = 1.0;
        _effectiveLifeOpacity = 1.0;
        _currentFpsFromConfig = 60;
        _currentFps = 60;
        _currentSimulationTargetFps = 60;

        int targetRows = Math.Clamp(rows, MinRows, MaxRows);
        ApplyDimensions(targetRows, 24, DefaultAspectRatio, persist: false);
        if (rgbMode)
        {
            SetReferenceSimulationLayerLifeModeForSmoke(GameOfLifeEngine.LifeMode.RgbChannels);
        }
        else
        {
            SetReferenceSimulationLayerLifeModeForSmoke(GameOfLifeEngine.LifeMode.NaiveGrayscale);
        }

        int width = GetReferenceSimulationEngine().Columns;
        int height = GetReferenceSimulationEngine().Rows;

        if (!string.IsNullOrWhiteSpace(smokeVideoPath))
        {
            if (!_fileCapture.TryGetOrAdd(smokeVideoPath, out var info, out var error))
            {
                throw new InvalidOperationException($"Failed to prepare smoke video source '{smokeVideoPath}': {error ?? "unknown error"}");
            }

            var fileSource = CaptureSource.CreateFile(info.Path, info.DisplayName, info.Width, info.Height);
            fileSource.BlendMode = BlendMode.Additive;
            fileSource.Opacity = 1.0;
            _sources.Add(fileSource);
            UpdatePrimaryAspectIfNeeded();
            NotifyLayerEditorSourcesChanged();
            RenderFrame();
            return;
        }

        var background = CaptureSource.CreateFile("profile-bg", "Profile Background", width, height);
        background.LastFrame = new SourceFrame(BuildSmokeGradientBgra(width, height), width, height, null, width, height);
        background.BlendMode = BlendMode.Additive;
        background.Opacity = 1.0;

        var overlay = CaptureSource.CreateFile("profile-overlay", "Profile Overlay", width, height);
        overlay.LastFrame = new SourceFrame(BuildSmokeCheckerBgra(width, height, 18, 28, 42, 120, 150, 220), width, height, null, width, height);
        overlay.BlendMode = BlendMode.Screen;
        overlay.Opacity = 0.65;

        var accent = CaptureSource.CreateFile("profile-accent", "Profile Accent", width, height);
        accent.LastFrame = new SourceFrame(BuildSmokeSolidBgra(width, height, 96, 96, 96), width, height, null, width, height);
        accent.BlendMode = BlendMode.Overlay;
        accent.Opacity = 0.45;

        _sources.Add(background);
        if (includeSimGroup)
        {
            var simulationGroup = CaptureSource.CreateSimulationGroup("Profile Sim Group");
            simulationGroup.SimulationLayers.Add(new SimulationLayerSpec
            {
                Id = Guid.NewGuid(),
                Kind = LayerEditorSimulationItemKind.Layer,
                Name = "Profile Positive",
                Enabled = true,
                InputFunction = SimulationInputFunction.Direct,
                BlendMode = BlendMode.Additive,
                InjectionMode = GameOfLifeEngine.InjectionMode.Threshold,
                LifeMode = rgbMode ? GameOfLifeEngine.LifeMode.RgbChannels : GameOfLifeEngine.LifeMode.NaiveGrayscale,
                BinningMode = GameOfLifeEngine.BinningMode.Fill,
                InjectionNoise = 0.0,
                LifeOpacity = 1.0,
                ThresholdMin = 0.25,
                ThresholdMax = 0.85,
                InvertThreshold = false,
                RgbHueShiftDegrees = rgbMode ? 60.0 : 0.0
            });
            simulationGroup.SimulationLayers.Add(new SimulationLayerSpec
            {
                Id = Guid.NewGuid(),
                Kind = LayerEditorSimulationItemKind.Layer,
                Name = "Profile Negative",
                Enabled = true,
                InputFunction = SimulationInputFunction.Inverse,
                BlendMode = BlendMode.Subtractive,
                InjectionMode = GameOfLifeEngine.InjectionMode.Threshold,
                LifeMode = rgbMode ? GameOfLifeEngine.LifeMode.RgbChannels : GameOfLifeEngine.LifeMode.NaiveGrayscale,
                BinningMode = GameOfLifeEngine.BinningMode.Fill,
                InjectionNoise = 0.0,
                LifeOpacity = 0.85,
                ThresholdMin = 0.15,
                ThresholdMax = 0.70,
                InvertThreshold = false,
                RgbHueShiftDegrees = rgbMode ? 180.0 : 0.0
            });
            _sources.Add(simulationGroup);
        }

        _sources.Add(overlay);
        _sources.Add(accent);
        UpdatePrimaryAspectIfNeeded();
        if (includeSimGroup)
        {
            ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
        }
        NotifyLayerEditorSourcesChanged();
        RenderFrame();
    }

    private static byte[] BuildSmokeSolidBgra(int width, int height, byte b, byte g, byte r)
    {
        var buffer = new byte[width * height * 4];
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = b;
            buffer[i + 1] = g;
            buffer[i + 2] = r;
            buffer[i + 3] = 255;
        }

        return buffer;
    }

    private static byte[] BuildSmokeGradientBgra(int width, int height)
    {
        var buffer = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
        {
            double rowT = height > 1 ? row / (double)(height - 1) : 0.0;
            for (int col = 0; col < width; col++)
            {
                double colT = width > 1 ? col / (double)(width - 1) : 0.0;
                int index = (row * width + col) * 4;
                buffer[index] = (byte)Math.Clamp((int)Math.Round(32 + (160 * colT)), 0, 255);
                buffer[index + 1] = (byte)Math.Clamp((int)Math.Round(24 + (176 * rowT)), 0, 255);
                buffer[index + 2] = (byte)Math.Clamp((int)Math.Round(48 + (160 * ((colT + rowT) * 0.5))), 0, 255);
                buffer[index + 3] = 255;
            }
        }

        return buffer;
    }

    private static byte[] BuildSmokeRainbowBgra(int width, int height, double phase)
    {
        var buffer = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
        {
            double rowT = height > 1 ? row / (double)(height - 1) : 0.0;
            for (int col = 0; col < width; col++)
            {
                double colT = width > 1 ? col / (double)(width - 1) : 0.0;
                double t = (colT + (rowT * 0.35) + phase) % 1.0;
                (byte r, byte g, byte b) = HueToRgb(t, 0.88, 1.0);
                int index = (row * width + col) * 4;
                buffer[index] = b;
                buffer[index + 1] = g;
                buffer[index + 2] = r;
                buffer[index + 3] = 255;
            }
        }

        return buffer;
    }

    private static byte[] BuildSmokePixelSortPatternBgra(int width, int height)
    {
        var buffer = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int index = (row * width + col) * 4;
                buffer[index] = (byte)((col * 37 + row * 19) & 0xFF);
                buffer[index + 1] = (byte)((col * 11 + row * 73) & 0xFF);
                buffer[index + 2] = (byte)((col * 59 + row * 29 + ((col ^ row) * 13)) & 0xFF);
                buffer[index + 3] = 255;
            }
        }

        return buffer;
    }

    private static byte[] BuildSmokePortraitBgra(int width, int height, int variant)
    {
        var buffer = new byte[width * height * 4];
        double variantOffset = variant * 0.0125;
        double backgroundWarmth = 0.85 + (variant * 0.03);
        double faceWarmth = 1.0 + (variant * 0.025);
        double hairShift = variant * 0.01;
        double faceCx = 0.53 + hairShift;
        double faceCy = 0.42 + (variant * 0.005);
        double faceRx = 0.135;
        double faceRy = 0.205;
        double hairRx = 0.18;
        double hairRy = 0.28;
        double shoulderCy = 0.83;

        for (int row = 0; row < height; row++)
        {
            double rowT = height > 1 ? row / (double)(height - 1) : 0.0;
            for (int col = 0; col < width; col++)
            {
                double colT = width > 1 ? col / (double)(width - 1) : 0.0;
                double dxFace = (colT - faceCx) / faceRx;
                double dyFace = (rowT - faceCy) / faceRy;
                double dxHair = (colT - (faceCx - 0.005)) / hairRx;
                double dyHair = (rowT - (faceCy - 0.02)) / hairRy;
                double dxLeftEye = (colT - (faceCx - 0.038)) / 0.022;
                double dxRightEye = (colT - (faceCx + 0.038)) / 0.022;
                double dyEye = (rowT - (faceCy - 0.03)) / 0.012;
                double dxMouth = (colT - faceCx) / 0.046;
                double dyMouth = (rowT - (faceCy + 0.065)) / 0.018;
                double dxShoulder = (colT - 0.52) / 0.24;
                double dyShoulder = (rowT - shoulderCy) / 0.13;

                double background = 0.84 - (0.28 * rowT) + (0.06 * Math.Sin((colT * 8.0) + variantOffset));
                double shoulder = Math.Max(0.0, 1.0 - ((dxShoulder * dxShoulder) + (dyShoulder * dyShoulder)));
                double hair = Math.Max(0.0, 1.0 - ((dxHair * dxHair) + (dyHair * dyHair)));
                double face = Math.Max(0.0, 1.0 - ((dxFace * dxFace) + (dyFace * dyFace)));
                double leftEye = Math.Max(0.0, 1.0 - ((dxLeftEye * dxLeftEye) + (dyEye * dyEye)));
                double rightEye = Math.Max(0.0, 1.0 - ((dxRightEye * dxRightEye) + (dyEye * dyEye)));
                double mouth = Math.Max(0.0, 1.0 - ((dxMouth * dxMouth) + (dyMouth * dyMouth)));

                double baseR = 188 * backgroundWarmth * background;
                double baseG = 196 * backgroundWarmth * background;
                double baseB = 178 * backgroundWarmth * background;

                baseR -= shoulder * 70;
                baseG -= shoulder * 74;
                baseB -= shoulder * 72;

                baseR -= hair * 116;
                baseG -= hair * 120;
                baseB -= hair * 124;

                baseR += face * 44 * faceWarmth;
                baseG += face * 28 * faceWarmth;
                baseB += face * 12 * faceWarmth;

                double featureDarkness = (leftEye + rightEye) * 58 + mouth * 36;
                baseR -= featureDarkness;
                baseG -= featureDarkness;
                baseB -= featureDarkness;

                int index = (row * width + col) * 4;
                buffer[index] = (byte)Math.Clamp((int)Math.Round(baseB), 0, 255);
                buffer[index + 1] = (byte)Math.Clamp((int)Math.Round(baseG), 0, 255);
                buffer[index + 2] = (byte)Math.Clamp((int)Math.Round(baseR), 0, 255);
                buffer[index + 3] = 255;
            }
        }

        return buffer;
    }

    private static bool HaveMatchingPixelHistogram(byte[] left, byte[] right, int pixelCount)
    {
        if (left.Length < pixelCount * 4 || right.Length < pixelCount * 4)
        {
            return false;
        }

        var counts = new Dictionary<int, int>(pixelCount);
        for (int i = 0; i < pixelCount; i++)
        {
            int index = i * 4;
            int leftKey = left[index] |
                          (left[index + 1] << 8) |
                          (left[index + 2] << 16) |
                          (left[index + 3] << 24);
            counts[leftKey] = counts.TryGetValue(leftKey, out int existingLeft) ? existingLeft + 1 : 1;

            int rightKey = right[index] |
                           (right[index + 1] << 8) |
                           (right[index + 2] << 16) |
                           (right[index + 3] << 24);
            counts[rightKey] = counts.TryGetValue(rightKey, out int existingRight) ? existingRight - 1 : -1;
        }

        return counts.Values.All(value => value == 0);
    }

    private static (byte r, byte g, byte b) HueToRgb(double hue, double saturation, double value)
    {
        hue = ((hue % 1.0) + 1.0) % 1.0;
        saturation = Math.Clamp(saturation, 0.0, 1.0);
        value = Math.Clamp(value, 0.0, 1.0);

        double scaled = hue * 6.0;
        int sector = (int)Math.Floor(scaled);
        double fraction = scaled - sector;
        double p = value * (1.0 - saturation);
        double q = value * (1.0 - (saturation * fraction));
        double t = value * (1.0 - (saturation * (1.0 - fraction)));

        (double r, double g, double b) = sector switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q)
        };

        return (
            (byte)Math.Clamp((int)Math.Round(r * 255.0), 0, 255),
            (byte)Math.Clamp((int)Math.Round(g * 255.0), 0, 255),
            (byte)Math.Clamp((int)Math.Round(b * 255.0), 0, 255));
    }

    private static byte[] BuildSmokeCheckerBgra(int width, int height, byte bA, byte gA, byte rA, byte bB, byte gB, byte rB)
    {
        var buffer = new byte[width * height * 4];
        int checkerSize = Math.Max(6, Math.Min(width, height) / 12);
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                bool useA = ((row / checkerSize) + (col / checkerSize)) % 2 == 0;
                int index = (row * width + col) * 4;
                buffer[index] = useA ? bA : bB;
                buffer[index + 1] = useA ? gA : gB;
                buffer[index + 2] = useA ? rA : rB;
                buffer[index + 3] = 255;
            }
        }

        return buffer;
    }

    private static bool BufferHasNonBlackPixel(byte[]? buffer)
    {
        if (buffer == null)
        {
            return false;
        }

        for (int i = 0; i <= buffer.Length - 4; i += 4)
        {
            if (buffer[i] != 0 || buffer[i + 1] != 0 || buffer[i + 2] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private Transform2D BuildAnimationTransform(CaptureSource source, int destWidth, int destHeight, double timeSeconds)
    {
        if (source.Animations.Count == 0 || destWidth <= 0 || destHeight <= 0)
        {
            return Transform2D.Identity;
        }

        if (!TryGetAnimationBeatTiming(timeSeconds, out _, out double beatsElapsed, out _))
        {
            return Transform2D.Identity;
        }

        double centerX = (destWidth - 1) / 2.0;
        double centerY = (destHeight - 1) / 2.0;

        Transform2D combined = Transform2D.Identity;
        foreach (var animation in source.Animations)
        {
            double tempoMultiplier = GetSpeedMultiplier(animation.Speed);
            double beatsPerCycle = Math.Clamp(animation.BeatsPerCycle, 1, 4096);
            double cycleBeats = beatsPerCycle / Math.Max(tempoMultiplier, 0.000001);
            if (cycleBeats <= 0.000001)
            {
                continue;
            }

            double phase = (beatsElapsed / cycleBeats) % 1.0;
            if (phase < 0)
            {
                phase += 1.0;
            }
            double progress = GetLoopProgress(phase, animation.Loop);

            Transform2D animTransform = Transform2D.Identity;
            switch (animation.Type)
            {
                case AnimationType.ZoomIn:
                {
                    double scale = 1.0 + (AnimationZoomScale * progress);
                    animTransform = CreateScale(scale, centerX, centerY);
                    break;
                }
                case AnimationType.Translate:
                {
                    double distance = Math.Min(destWidth, destHeight) * AnimationTranslateFactor * progress;
                    double dx = 0;
                    double dy = 0;
                    switch (animation.TranslateDirection)
                    {
                        case TranslateDirection.Up:
                            dy = -distance;
                            break;
                        case TranslateDirection.Down:
                            dy = distance;
                            break;
                        case TranslateDirection.Left:
                            dx = -distance;
                            break;
                        case TranslateDirection.Right:
                            dx = distance;
                            break;
                    }
                    animTransform = CreateTranslation(dx, dy);
                    break;
                }
                case AnimationType.Rotate:
                {
                    double angle = DegreesToRadians(animation.RotationDegrees * progress);
                    if (animation.RotationDirection == RotationDirection.CounterClockwise)
                    {
                        angle = -angle;
                    }
                    animTransform = CreateRotation(angle, centerX, centerY);
                    break;
                }
                case AnimationType.DvdBounce:
                {
                    double baseCycleBeats = (AnimationDvdCycleBeats * beatsPerCycle) / Math.Max(tempoMultiplier, 0.000001);
                    if (baseCycleBeats <= 0.000001)
                    {
                        break;
                    }

                    double cycleXBeats = baseCycleBeats;
                    double cycleYBeats = baseCycleBeats * AnimationDvdAspectFactor;
                    double phaseX = (beatsElapsed / cycleXBeats) % 1.0;
                    double phaseY = (beatsElapsed / cycleYBeats) % 1.0;
                    if (phaseX < 0)
                    {
                        phaseX += 1.0;
                    }
                    if (phaseY < 0)
                    {
                        phaseY += 1.0;
                    }
                    double progressX = GetLoopProgress(phaseX, animation.Loop);
                    double progressY = GetLoopProgress(phaseY, animation.Loop);

                    double scale = Math.Clamp(animation.DvdScale, 0.01, 1.0);
                    double maxX = Math.Max(0, destWidth - (destWidth * scale));
                    double maxY = Math.Max(0, destHeight - (destHeight * scale));
                    // Point-sampled layers shimmer badly when DVD bounce lands between pixels.
                    double posX = Math.Round(maxX * progressX);
                    double posY = Math.Round(maxY * progressY);

                    var scaleTransform = CreateScaleAtOrigin(scale);
                    var translateTransform = CreateTranslation(posX, posY);
                    animTransform = Transform2D.Multiply(translateTransform, scaleTransform);
                    break;
                }
                case AnimationType.BeatShake:
                {
                    double baseBpm = _animationBpm > 0 ? _animationBpm : DefaultAnimationBpm;
                    bool audioRequested = _animationAudioSyncEnabled && !string.IsNullOrWhiteSpace(_selectedAudioDeviceId);
                    bool beatAligned = audioRequested &&
                                       _audioBeatDetector.LastBeatTime != DateTime.MinValue &&
                                       _audioBeatDetector.BeatCount > 0;
                    double detectedBpm = audioRequested ? _audioBeatDetector.CurrentBpm : baseBpm;
                    if (double.IsNaN(detectedBpm) || double.IsInfinity(detectedBpm) || detectedBpm <= 0)
                    {
                        detectedBpm = baseBpm;
                    }

                    double shakeBpm = Math.Clamp(detectedBpm, 10, 300);
                    double shakeBeatDuration = 60.0 / shakeBpm;
                    double shakeWindow = shakeBeatDuration * AnimationBeatShakeWindowBeats;
                    if (shakeBeatDuration <= 0.000001 || shakeWindow <= 0.000001)
                    {
                        break;
                    }

                    double timeSinceBeat;
                    long beatSeed;
                    if (beatAligned)
                    {
                        timeSinceBeat = (DateTime.UtcNow - _audioBeatDetector.LastBeatTime).TotalSeconds;
                        beatSeed = _audioBeatDetector.LastBeatTime.Ticks;
                    }
                    else
                    {
                        timeSinceBeat = timeSeconds % shakeBeatDuration;
                        beatSeed = (long)(timeSeconds / shakeBeatDuration);
                    }

                    if (timeSinceBeat < 0)
                    {
                        timeSinceBeat = 0;
                    }

                    if (timeSinceBeat >= shakeWindow)
                    {
                        break;
                    }

                    double progressShake = timeSinceBeat / shakeWindow;
                    double envelope = 1.0 - progressShake;
                    envelope *= envelope;

                    double intensity = Math.Clamp(animation.BeatShakeIntensity, 0, MaxBeatShakeIntensity);
                    double amplitude = Math.Min(destWidth, destHeight) * AnimationBeatShakeFactor * intensity * envelope;
                    if (amplitude <= 0.0001)
                    {
                        break;
                    }

                    double frequency = AnimationBeatShakeFrequency;
                    ulong seed = BuildBeatShakeSeed(source.Id, beatSeed);
                    double phaseX = SeedToRadians(seed);
                    double phaseY = SeedToRadians(ScrambleSeed(seed + 0x9E3779B97F4A7C15UL));

                    double t = timeSinceBeat;
                    double shakeX = Math.Sin(2 * Math.PI * frequency * t + phaseX) +
                                    0.5 * Math.Sin(2 * Math.PI * (frequency * 1.9) * t + phaseY);
                    double shakeY = Math.Cos(2 * Math.PI * (frequency * 1.3) * t + phaseY) +
                                    0.5 * Math.Sin(2 * Math.PI * (frequency * 2.3) * t + phaseX);

                    double dx = amplitude * 0.6 * shakeX;
                    double dy = amplitude * 0.6 * shakeY;
                    animTransform = CreateTranslation(dx, dy);
                    break;
                }
                case AnimationType.AudioGranular:
                {
                    double intensityRaw = Math.Clamp(animation.BeatShakeIntensity, 0, MaxAudioGranularIntensity);
                    double intensity = Math.Pow(intensityRaw / MaxAudioGranularIntensity, 1.1) * 7.0;
                    double energy = Math.Clamp(_fastAudioLevel, 0, 1);
                    double bassNorm = Math.Clamp(_smoothedBass / 16.0, 0, 1);
                    double freqNorm = Math.Clamp(_smoothedFreq / 4500.0, 0, 1);
                    double lowEq = Math.Clamp(animation.AudioGranularLowGain, 0, MaxAudioGranularEqBandGain);
                    double midEq = Math.Clamp(animation.AudioGranularMidGain, 0, MaxAudioGranularEqBandGain);
                    double highEq = Math.Clamp(animation.AudioGranularHighGain, 0, MaxAudioGranularEqBandGain);

                    // Keep small-signal chatter down, but do not bury normal content.
                    double gatedEnergy = Math.Clamp((energy - 0.06) / 0.94, 0, 1);

                    if (gatedEnergy < 0.001 || intensity <= 0.0001)
                    {
                        break;
                    }

                    double energyDrive = Math.Pow(gatedEnergy, 1.45);
                    double lowBand = bassNorm;
                    double midFreqBand = Math.Clamp(1.0 - Math.Abs(freqNorm - 0.45) / 0.55, 0, 1);
                    double midBand = Math.Clamp((0.55 * energy) + (0.45 * midFreqBand), 0, 1);
                    double highBand = freqNorm;
                    double eqWeightSum = Math.Max(0.001, lowEq + midEq + highEq);
                    double lowDrive = Math.Clamp((lowBand * lowEq) / eqWeightSum * 3.0, 0, 1.5);
                    double midDrive = Math.Clamp((midBand * midEq) / eqWeightSum * 3.0, 0, 1.5);
                    double highDrive = Math.Clamp((highBand * highEq) / eqWeightSum * 3.0, 0, 1.5);

                    // 1) 3-band EQ mix -> zoom and subtle vibration.
                    double bandMix = Math.Clamp((0.40 * lowDrive) + (0.38 * midDrive) + (0.22 * highDrive), 0, 1.4);
                    double zoomBase = 1.0 + (bandMix * 0.18 * intensity);
                    double zoomVibrationFreq = 6.5 + (2.0 * midDrive) + (5.0 * highDrive);
                    double zoomVibrationAmp = (0.003 + (0.013 * energyDrive)) * (0.55 + (0.45 * bandMix)) * intensity;
                    double zoomVibration = Math.Sin(2 * Math.PI * zoomVibrationFreq * timeSeconds) * zoomVibrationAmp;
                    double scaleFactor = Math.Clamp(zoomBase + zoomVibration, 0.90, 1.36);

                    // 2) Mid/High emphasis -> bounded orbital translation.
                    double shakeFreq = 0.55 + (1.4 * midDrive) + (2.2 * highDrive);
                    double maxShake = Math.Min(destWidth, destHeight) * 0.05;
                    double baseShake = Math.Min(destWidth, destHeight) * 0.034;
                    double shakeBandAmp = Math.Clamp((0.20 * lowDrive) + (0.45 * midDrive) + (0.35 * highDrive), 0, 1.4);
                    double shakeAmp = Math.Clamp(baseShake * energyDrive * shakeBandAmp * intensity, 0, maxShake);
                    double transX = Math.Sin(2 * Math.PI * shakeFreq * timeSeconds) * shakeAmp;
                    double transY = Math.Cos(2 * Math.PI * (shakeFreq * 0.91) * timeSeconds) * shakeAmp;

                    // 3) Smooth rotational shake (no per-frame random jitter).
                    ulong seed = BuildBeatShakeSeed(source.Id, 0);
                    double rotPhase = SeedToRadians(seed);
                    double rotFreq = 0.70 + (1.2 * midDrive) + (1.6 * highDrive);
                    double rotBandAmp = Math.Clamp((0.12 * lowDrive) + (0.45 * midDrive) + (0.43 * highDrive), 0, 1.4);
                    double rotAmp = (0.45 + (3.0 * energyDrive * rotBandAmp)) * intensity; // degrees
                    double rotation = Math.Sin((2 * Math.PI * rotFreq * timeSeconds) + rotPhase) * rotAmp;

                    var scaleT = CreateScale(scaleFactor, centerX, centerY);
                    var rotateT = CreateRotation(DegreesToRadians(rotation), centerX, centerY);
                    var transT = CreateTranslation(transX, transY);
                    animTransform = Transform2D.Multiply(transT, Transform2D.Multiply(rotateT, scaleT));
                    break;
                }
                case AnimationType.Fade:
                {
                    break;
                }
            }

            combined = Transform2D.Multiply(animTransform, combined);
        }

        if (!combined.TryInvert(out var inverse))
        {
            return Transform2D.Identity;
        }

        return inverse;
    }

    private double BuildAnimationOpacity(CaptureSource source, double timeSeconds)
    {
        if (source.Animations.Count == 0)
        {
            return 1.0;
        }

        if (!TryGetAnimationBeatTiming(timeSeconds, out _, out double beatsElapsed, out _))
        {
            return 1.0;
        }

        double opacity = 1.0;
        foreach (var animation in source.Animations)
        {
            if (animation.Type != AnimationType.Fade)
            {
                continue;
            }

            double tempoMultiplier = GetSpeedMultiplier(animation.Speed);
            double beatsPerCycle = Math.Clamp(animation.BeatsPerCycle, 1, 4096);
            double cycleBeats = beatsPerCycle / Math.Max(tempoMultiplier, 0.000001);
            if (cycleBeats <= 0.000001)
            {
                continue;
            }

            double phase = (beatsElapsed / cycleBeats) % 1.0;
            if (phase < 0)
            {
                phase += 1.0;
            }
            double progress = GetLoopProgress(phase, animation.Loop);
            opacity *= Math.Clamp(progress, 0.0, 1.0);
        }

        return Math.Clamp(opacity, 0.0, 1.0);
    }

    private static double GetLoopProgress(double phase, AnimationLoop loop)
    {
        phase -= Math.Floor(phase);
        if (loop == AnimationLoop.PingPong)
        {
            return phase <= 0.5 ? phase * 2.0 : (1.0 - phase) * 2.0;
        }
        return phase;
    }

    private static ulong BuildBeatShakeSeed(Guid id, long beatSeed)
    {
        var bytes = id.ToByteArray();
        ulong left = BitConverter.ToUInt64(bytes, 0);
        ulong right = BitConverter.ToUInt64(bytes, 8);
        ulong seed = left ^ right ^ (ulong)beatSeed;
        return ScrambleSeed(seed);
    }

    private static ulong ScrambleSeed(ulong seed)
    {
        seed ^= seed >> 33;
        seed *= 0xff51afd7ed558ccdUL;
        seed ^= seed >> 33;
        seed *= 0xc4ceb9fe1a85ec53UL;
        seed ^= seed >> 33;
        return seed;
    }

    private static double SeedToRadians(ulong seed) => (seed / (double)ulong.MaxValue) * (Math.PI * 2.0);

    private static Transform2D CreateTranslation(double dx, double dy) => new(1, 0, 0, 1, dx, dy);

    private static Transform2D CreateScaleAtOrigin(double scale) => new(scale, 0, 0, scale, 0, 0);

    private static Transform2D CreateScale(double scale, double centerX, double centerY)
    {
        return new Transform2D(scale, 0, 0, scale, (1 - scale) * centerX, (1 - scale) * centerY);
    }

    private static Transform2D CreateRotation(double radians, double centerX, double centerY)
    {
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double offsetX = (centerX * (1 - cos)) + (sin * centerY);
        double offsetY = (centerY * (1 - cos)) - (sin * centerX);
        return new Transform2D(cos, -sin, sin, cos, offsetX, offsetY);
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);

    private static void BlendSimulationLayerInto(
        ref int destinationB,
        ref int destinationG,
        ref int destinationR,
        byte sr,
        byte sg,
        byte sb,
        BlendMode mode,
        double opacity)
    {
        int db = destinationB;
        int dg = destinationG;
        int dr = destinationR;

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

        destinationB = (int)Math.Round(db + ((b - db) * opacity));
        destinationG = (int)Math.Round(dg + ((g - dg) * opacity));
        destinationR = (int)Math.Round(dr + ((r - dr) * opacity));
    }

    private static void BlendInto(byte[] destination, int destIndex, byte sb, byte sg, byte sr, byte sa, BlendMode mode, double opacity)
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
            case BlendMode.Normal:
            {
                double alpha = (sa / 255.0) * opacity;
                destination[destIndex] = ClampToByte((int)(db + (sb - db) * alpha));
                destination[destIndex + 1] = ClampToByte((int)(dg + (sg - dg) * alpha));
                destination[destIndex + 2] = ClampToByte((int)(dr + (sr - dr) * alpha));
                destination[destIndex + 3] = 255;
                return;
            }
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
            default:
                b = sb;
                g = sg;
                r = sr;
                break;
        }

        destination[destIndex] = ClampToByte((int)(db + (b - db) * opacity));
        destination[destIndex + 1] = ClampToByte((int)(dg + (g - dg) * opacity));
        destination[destIndex + 2] = ClampToByte((int)(dr + (r - dr) * opacity));
        destination[destIndex + 3] = 255;
    }

    private readonly struct InlineSimulationBlendLayerData
    {
        public InlineSimulationBlendLayerData(
            byte[] colorBuffer,
            BlendMode blendMode,
            double opacity,
            bool publishesStandaloneOutput,
            bool applyHueShift,
            double hueRr,
            double hueRg,
            double hueRb,
            double hueGr,
            double hueGg,
            double hueGb,
            double hueBr,
            double hueBg,
            double hueBb)
        {
            ColorBuffer = colorBuffer;
            BlendMode = blendMode;
            Opacity = opacity;
            PublishesStandaloneOutput = publishesStandaloneOutput;
            ApplyHueShift = applyHueShift;
            HueRr = hueRr;
            HueRg = hueRg;
            HueRb = hueRb;
            HueGr = hueGr;
            HueGg = hueGg;
            HueGb = hueGb;
            HueBr = hueBr;
            HueBg = hueBg;
            HueBb = hueBb;
        }

        public byte[] ColorBuffer { get; }
        public BlendMode BlendMode { get; }
        public double Opacity { get; }
        public bool PublishesStandaloneOutput { get; }
        public bool ApplyHueShift { get; }
        public double HueRr { get; }
        public double HueRg { get; }
        public double HueRb { get; }
        public double HueGr { get; }
        public double HueGg { get; }
        public double HueGb { get; }
        public double HueBr { get; }
        public double HueBg { get; }
        public double HueBb { get; }
    }

    private static byte ClampToByte(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    private static void BuildHueRotationMatrix(double hueShiftDegrees,
        out double rr, out double rg, out double rb,
        out double gr, out double gg, out double gb,
        out double br, out double bg, out double bb)
    {
        double radians = DegreesToRadians(hueShiftDegrees);
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        rr = 0.299 + (0.701 * cos) + (0.168 * sin);
        rg = 0.587 - (0.587 * cos) + (0.330 * sin);
        rb = 0.114 - (0.114 * cos) - (0.497 * sin);

        gr = 0.299 - (0.299 * cos) - (0.328 * sin);
        gg = 0.587 + (0.413 * cos) + (0.035 * sin);
        gb = 0.114 - (0.114 * cos) + (0.292 * sin);

        br = 0.299 - (0.300 * cos) + (1.250 * sin);
        bg = 0.587 - (0.588 * cos) - (1.050 * sin);
        bb = 0.114 + (0.886 * cos) - (0.203 * sin);
    }

    private void EnsureEngineColorBuffers()
    {
        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            if (!layer.Enabled)
            {
                continue;
            }

            EnsureEngineColorBuffer(layer);
        }
    }

    private bool TryBuildSimulationPresentationLayer(
        SimulationLayerState layer,
        double effectiveOpacity,
        out SimulationPresentationLayerData presentationLayer)
    {
        presentationLayer = null!;

        float opacity = (float)Math.Clamp(effectiveOpacity, 0, 1);
        if (opacity <= 0.0001f)
        {
            return false;
        }

        var engine = layer.Engine;
        if (engine == null)
        {
            return false;
        }

        if (layer.LayerType == SimulationLayerType.PixelSort &&
            engine is GpuPixelSortBackend pixelSortBackend &&
            pixelSortBackend.TryGetPresentationSurface(out var pixelSortSurface) &&
            pixelSortSurface != null)
        {
            presentationLayer = new SimulationPresentationLayerData
            {
                Buffer = null,
                Surface = pixelSortSurface,
                SharedTextureHandle = IntPtr.Zero,
                Width = pixelSortSurface.Width,
                Height = pixelSortSurface.Height,
                PublishesStandaloneOutput = true,
                BlendMode = (int)layer.BlendMode,
                Opacity = opacity,
                HueShiftDegrees = (float)CurrentRgbHueShiftDegrees(layer)
            };
            return true;
        }

        if (engine is IGpuSimulationSurfaceBackend gpuBackend &&
            gpuBackend.TryGetSharedColorTexture(out IntPtr sharedHandle, out int width, out int height))
        {
            float hueShiftDegrees = (float)CurrentRgbHueShiftDegrees(layer);
            byte[]? fallbackBuffer = null;
            GpuCompositeSurface? surface = null;
            if (App.IsSmokeTestMode && App.CaptureGpuFallbackBuffersInSmokeTest)
            {
                EnsureEngineColorBuffer(layer);
                fallbackBuffer = layer.ColorBuffer;
            }
            gpuBackend.TryGetColorSurface(out surface);

            presentationLayer = new SimulationPresentationLayerData
            {
                Buffer = fallbackBuffer,
                Surface = surface,
                SharedTextureHandle = sharedHandle,
                Width = width,
                Height = height,
                PublishesStandaloneOutput = LayerPublishesStandaloneOutput(layer),
                BlendMode = (int)layer.BlendMode,
                Opacity = opacity,
                HueShiftDegrees = hueShiftDegrees
            };
            return true;
        }

        EnsureEngineColorBuffer(layer);
        if (layer.ColorBuffer == null)
        {
            return false;
        }

        presentationLayer = new SimulationPresentationLayerData
        {
            Buffer = layer.ColorBuffer,
            Surface = null,
            Width = engine.Columns,
            Height = engine.Rows,
            PublishesStandaloneOutput = LayerPublishesStandaloneOutput(layer),
            BlendMode = (int)layer.BlendMode,
            Opacity = opacity,
            HueShiftDegrees = (float)CurrentRgbHueShiftDegrees(layer)
        };
        return true;
    }

    private void EnsureEngineColorBuffer(SimulationLayerState layer)
    {
        var engine = layer.Engine;
        if (engine == null)
        {
            return;
        }

        int size = engine.Columns * engine.Rows * 4;
        if (layer.ColorBuffer == null || layer.ColorBuffer.Length != size)
        {
            layer.ColorBuffer = new byte[size];
        }
        byte[] targetBuffer = layer.ColorBuffer;
        engine.FillColorBuffer(targetBuffer);
        if (!_renderBackend.SupportsGpuSimulationComposition || _isRecording)
        {
            double hueShiftDegrees = CurrentRgbHueShiftDegrees(layer);
            bool applyHueShift =
                Math.Abs(hueShiftDegrees) > 0.001 &&
                (layer.LayerType == SimulationLayerType.PixelSort || layer.LifeMode == GameOfLifeEngine.LifeMode.RgbChannels);
            if (applyHueShift)
            {
                ApplyHueShiftToColorBuffer(targetBuffer, engine.Columns, engine.Rows, hueShiftDegrees);
            }
        }
    }

    private static void ApplyHueShiftToColorBuffer(byte[] targetBuffer, int width, int height, double hueShiftDegrees)
    {
        BuildHueRotationMatrix(hueShiftDegrees, out double rr, out double rg, out double rb,
            out double gr, out double gg, out double gb,
            out double br, out double bg, out double bb);

        Parallel.For(0, height, row =>
        {
            int rowOffset = row * width * 4;
            for (int col = 0; col < width; col++)
            {
                int index = rowOffset + (col * 4);
                byte r = targetBuffer[index];
                byte g = targetBuffer[index + 1];
                byte b = targetBuffer[index + 2];
                targetBuffer[index] = ClampToByte((int)Math.Round((rr * r) + (rg * g) + (rb * b)));
                targetBuffer[index + 1] = ClampToByte((int)Math.Round((gr * r) + (gg * g) + (gb * b)));
                targetBuffer[index + 2] = ClampToByte((int)Math.Round((br * r) + (bg * g) + (bb * b)));
                targetBuffer[index + 3] = 255;
            }
        });
    }

    private InlineSimulationBlendLayerData[] BuildInlineSimulationBlendLayers(
        IReadOnlyList<SimulationLayerState> enabledLayers,
        double globalLifeOpacity)
    {
        var blendLayers = new List<InlineSimulationBlendLayerData>(enabledLayers.Count);
        foreach (var layer in enabledLayers)
        {
            EnsureEngineColorBuffer(layer);
            byte[]? colorBuffer = layer.ColorBuffer;
            if (colorBuffer == null)
            {
                continue;
            }

            double opacity = Math.Clamp(globalLifeOpacity * layer.EffectiveLifeOpacity, 0, 1);
            if (opacity <= 0.0001)
            {
                continue;
            }

            bool applyHueShift = false;
            double hueRr = 1.0;
            double hueRg = 0.0;
            double hueRb = 0.0;
            double hueGr = 0.0;
            double hueGg = 1.0;
            double hueGb = 0.0;
            double hueBr = 0.0;
            double hueBg = 0.0;
            double hueBb = 1.0;

            if (_renderBackend.SupportsGpuSimulationComposition && !_isRecording && layer.LifeMode == GameOfLifeEngine.LifeMode.RgbChannels)
            {
                double hueShiftDegrees = CurrentRgbHueShiftDegrees(layer);
                if (Math.Abs(hueShiftDegrees) > 0.001)
                {
                    applyHueShift = true;
                    BuildHueRotationMatrix(
                        hueShiftDegrees,
                        out hueRr,
                        out hueRg,
                        out hueRb,
                        out hueGr,
                        out hueGg,
                        out hueGb,
                        out hueBr,
                        out hueBg,
                        out hueBb);
                }
            }

            blendLayers.Add(new InlineSimulationBlendLayerData(
                colorBuffer,
                layer.BlendMode,
                opacity,
                LayerPublishesStandaloneOutput(layer),
                applyHueShift,
                hueRr,
                hueRg,
                hueRb,
                hueGr,
                hueGg,
                hueGb,
                hueBr,
                hueBg,
                hueBb));
        }

        return blendLayers.ToArray();
    }

    private static bool LayerPublishesStandaloneOutput(SimulationLayerState layer)
        => layer.LayerType == SimulationLayerType.PixelSort;

    private static void SampleInlineSimulationLayerColor(
        InlineSimulationBlendLayerData layer,
        int sourceIndex,
        out byte sampleR,
        out byte sampleG,
        out byte sampleB)
    {
        byte[] colorBuffer = layer.ColorBuffer;
        sampleR = colorBuffer[sourceIndex];
        sampleG = colorBuffer[sourceIndex + 1];
        sampleB = colorBuffer[sourceIndex + 2];
        if (!layer.ApplyHueShift)
        {
            return;
        }

        byte originalR = sampleR;
        byte originalG = sampleG;
        byte originalB = sampleB;
        sampleR = ClampToByte((int)Math.Round((layer.HueRr * originalR) + (layer.HueRg * originalG) + (layer.HueRb * originalB)));
        sampleG = ClampToByte((int)Math.Round((layer.HueGr * originalR) + (layer.HueGg * originalG) + (layer.HueGb * originalB)));
        sampleB = ClampToByte((int)Math.Round((layer.HueBr * originalR) + (layer.HueBg * originalG) + (layer.HueBb * originalB)));
    }

    private void UpdateUnderlayBitmap(int requiredLength)
    {
        if (_passthroughCompositedInPixelBuffer)
        {
            // Underlay is already in _pixelBuffer for this frame.
            return;
        }

        var composite = _lastCompositeFrame;
        bool hasOverlay = ShouldUseShaderPassthrough() && composite != null;
        if (!hasOverlay)
        {
            return;
        }

        int width = _renderBackend.PixelWidth;
        int height = _renderBackend.PixelHeight;
        byte[]? buffer = null;
        int stride = width * 4;

        if (composite != null && composite.Downscaled.Length >= requiredLength &&
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

        _renderBackend.PresentUnderlay(buffer, stride);
    }

    private void UpdateEffectInput()
    {
        if (_renderBackend == null)
        {
            return;
        }

        bool useOverlay = ShouldUseShaderPassthrough();
        var passthroughBlendMode = GetEffectivePassthroughBlendMode();
        double blendModeValue = ToBlendEffectModeValue(passthroughBlendMode);
        _renderBackend.UpdateEffectState(useOverlay, blendModeValue);
    }

    private static double ToBlendEffectModeValue(BlendMode passthroughBlendMode) => passthroughBlendMode switch
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

    private bool ShouldUseShaderPassthrough()
    {
        return _passthroughEnabled &&
               _lastCompositeFrame != null &&
               !_passthroughCompositedInPixelBuffer;
    }

    private BlendMode GetEffectivePassthroughBlendMode()
    {
        bool hasEnabledSubtractiveSimulationLayer = EnumerateSimulationLeafLayers(_simulationLayers).Any(layer =>
            layer.Enabled &&
            layer.BlendMode == BlendMode.Subtractive);

        if (_passthroughEnabled &&
            _blendMode == BlendMode.Additive &&
            hasEnabledSubtractiveSimulationLayer)
        {
            // White passthrough regions clip additive overlays; use Darken so subtractive-layer
            // detail stays visible inside bright underlay regions.
            return BlendMode.Darken;
        }

        return _blendMode;
    }

    private static bool ShouldUseSignedAddSubPassthrough(
        bool hasPassthroughBaseline,
        bool hasActiveSimulationLayers,
        bool hasEnabledNonAddSubSimulationLayer)
    {
        return hasPassthroughBaseline &&
               hasActiveSimulationLayers &&
               !hasEnabledNonAddSubSimulationLayer;
    }

    private void UpdateFpsOverlay()
    {
        if (FpsText == null)
        {
            return;
        }

        if (_showFps)
        {
            double now = _lifetimeStopwatch.Elapsed.TotalSeconds;
            var pacingStats = GetRecentFrameGapStats();
            string audioStats;
            if (!string.IsNullOrWhiteSpace(_selectedAudioDeviceId))
            {
                double displayedAudioLevel = GetDisplayedAudioLevel();
                string signalState = displayedAudioLevel >= 0.02 ? "Signal" : "Low/No Signal";
                audioStats = $"\nAudio: {displayedAudioLevel * 100:0}% ({signalState}) | Bass: {_smoothedBass:0.0} | Freq: {_smoothedFreq:0}Hz";
            }
            else
            {
                audioStats = "\nAudio: None (Select Device)";
            }

            string pacingStatsText = $"\nPacing: avg {pacingStats.averageMs:0.0}ms | p95 {pacingStats.p95Ms:0.0}ms | >25 {pacingStats.over25Ms} | >33 {pacingStats.over33Ms} | >50 {pacingStats.over50Ms}";
            string stageStatsText =
                $"\nWork: frame {GetLiveMetricAverage("frame_total_ms"):0.0}ms | capture {GetLiveMetricAverage("capture_sources_ms"):0.0} | composite {GetLiveMetricAverage("build_composite_ms"):0.0} | surface {GetLiveMetricAverage("update_display_surface_ms"):0.0} | inject {GetLiveMetricAverage("inject_layers_ms"):0.0} | sim {GetLiveMetricAverage("simulation_total_ms"):0.0} | render {GetLiveMetricAverage("render_call_ms"):0.0}";

            string reactiveStats = string.Empty;
            if (_audioReactiveEnabled)
            {
                string deviceState = string.IsNullOrWhiteSpace(_selectedAudioDeviceId) ? "No Device" : "Active";
                reactiveStats = $"\nReactive: {deviceState} | InGain x{_audioInputGain:0.00} | FPS x{_audioReactiveFpsMultiplier:0.00} (min {_audioReactiveFpsMinPercent * 100.0:0}%) | Opacity {_effectiveLifeOpacity:0.00} | Beats: {_audioBeatDetector.BeatCount} | Seeds L:{_audioReactiveLevelSeedBurstsLastStep} B:{_audioReactiveBeatSeedBurstsLastStep}";
            }

            FpsText.Text = $"Present {_presentationDisplayFps:0.0} fps (target {_currentFpsFromConfig:0.0}) | Loop {_renderDisplayFps:0.0} fps | Sim {_simulationDisplayFps:0.0} sps (target {_currentSimulationTargetFps:0.0}) | Steps/frame {_lastSimulationStepsThisFrame}{pacingStatsText}{stageStatsText}{audioStats}{reactiveStats}";
            FpsText.Visibility = Visibility.Visible;

            UpdateDebugOverlays(now);
        }
        else
        {
            FpsText.Visibility = Visibility.Collapsed;
            UpdateDebugOverlayVisibility(false);
        }
    }

    private void UpdateDebugOverlays(double now)
    {
        if (FrameDebugOverlay == null || FrameDebugCanvas == null || FrameBudgetLine == null || FrameTimingLine == null || FrameUnderrunBars == null ||
            AudioDebugOverlay == null || AudioDebugCanvas == null || AudioWaveformRange == null || AudioLevelLine == null || AudioBassLine == null || AudioFrequencyLine == null || AudioBassFrequencyLine == null || AudioMidFrequencyLine == null || AudioHighFrequencyLine == null)
        {
            return;
        }

        UpdateDebugOverlayVisibility(_showFps);
        if (!_showFps)
        {
            return;
        }

        if (now - _lastTimingOverlayUpdateTime >= (1.0 / DebugTimingOverlayRefreshRateHz))
        {
            _lastTimingOverlayUpdateTime = now;

            double frameWidth = FrameDebugCanvas.ActualWidth;
            double frameHeight = FrameDebugCanvas.ActualHeight;
            if (frameWidth >= 4 && frameHeight >= 4)
            {
                double[] frameGapHistory = _frameGapHistory.ToArray();
                if (frameGapHistory.Length == 0)
                {
                    frameGapHistory = new[] { 0d, 0d };
                }
                double frameBottom = frameHeight - 2;
                double frameUsableHeight = frameHeight - 4;
                FrameTimingLine.Points = BuildSampledPointCollection(
                    frameGapHistory.Length,
                    frameWidth,
                    GetOverlayPointCount(frameWidth, frameGapHistory.Length),
                    sourceIndex =>
                    {
                        double normalizedGap = Math.Clamp(frameGapHistory[sourceIndex] / FrameTimingOverlayMaxMs, 0, 1);
                        return frameBottom - (normalizedGap * frameUsableHeight);
                    });

                double frameBudgetMs = 1000.0 / Math.Max(1.0, _currentFps);
                double normalizedBudget = Math.Clamp(frameBudgetMs / FrameTimingOverlayMaxMs, 0, 1);
                double budgetY = frameBottom - (normalizedBudget * frameUsableHeight);
                FrameBudgetLine.Points = new PointCollection
                {
                    new Point(0, budgetY),
                    new Point(frameWidth, budgetY)
                };

                FrameUnderrunBars.Data = BuildFrameUnderrunGeometry(
                    frameGapHistory,
                    frameWidth,
                    frameBottom,
                    frameBudgetMs);
            }
        }

        if (now - _lastAudioOverlayUpdateTime < (1.0 / DebugAudioOverlayRefreshRateHz))
        {
            return;
        }
        _lastAudioOverlayUpdateTime = now;

        double width = AudioDebugCanvas.ActualWidth;
        double height = AudioDebugCanvas.ActualHeight;
        if (width < 4 || height < 4)
        {
            return;
        }

        var (waveformMin, waveformMax) = _audioBeatDetector.GetWaveformRangeHistory();
        if (waveformMin.Length == 0 || waveformMax.Length == 0)
        {
            waveformMin = new[] { 0f, 0f };
            waveformMax = new[] { 0f, 0f };
        }
        double centerY = height * 0.5;
        double waveformAmplitude = height * 0.38;
        AudioWaveformRange.Data = BuildVerticalRangeGeometry(
            waveformMin,
            waveformMax,
            width,
            GetOverlayPointCount(width, Math.Min(waveformMin.Length, waveformMax.Length)),
            sample => centerY - (sample * waveformAmplitude));

        float[] detectorEnvelopeHistory = _audioBeatDetector.GetEnvelopeHistory();
        double[] levelHistory = detectorEnvelopeHistory.Length == 0
            ? Array.Empty<double>()
            : detectorEnvelopeHistory.Select(static value => (double)value).ToArray();
        if (levelHistory.Length == 0)
        {
            levelHistory = new[] { 0d, 0d };
        }
        float[] detectorBassHistory = _audioBeatDetector.GetBassEnergyHistory();
        double[] bassHistory = detectorBassHistory.Length == 0
            ? Array.Empty<double>()
            : detectorBassHistory.Select(static value => (double)value).ToArray();
        if (bassHistory.Length == 0)
        {
            bassHistory = new[] { 0d, 0d };
        }
        float[] detectorMainFrequencyHistory = _audioBeatDetector.GetMainFrequencyHistory();
        double[] freqHistory = detectorMainFrequencyHistory.Length == 0
            ? Array.Empty<double>()
            : detectorMainFrequencyHistory.Select(static value => (double)value).ToArray();
        if (freqHistory.Length == 0)
        {
            freqHistory = new[] { 0d, 0d };
        }
        float[] detectorBassFrequencyHistory = _audioBeatDetector.GetBassFrequencyHistory();
        double[] bassFreqHistory = detectorBassFrequencyHistory.Length == 0
            ? Array.Empty<double>()
            : detectorBassFrequencyHistory.Select(static value => (double)value).ToArray();
        if (bassFreqHistory.Length == 0)
        {
            bassFreqHistory = new[] { 0d, 0d };
        }
        float[] detectorMidFrequencyHistory = _audioBeatDetector.GetMidFrequencyHistory();
        double[] midFreqHistory = detectorMidFrequencyHistory.Length == 0
            ? Array.Empty<double>()
            : detectorMidFrequencyHistory.Select(static value => (double)value).ToArray();
        if (midFreqHistory.Length == 0)
        {
            midFreqHistory = new[] { 0d, 0d };
        }
        float[] detectorHighFrequencyHistory = _audioBeatDetector.GetHighFrequencyHistory();
        double[] highFreqHistory = detectorHighFrequencyHistory.Length == 0
            ? Array.Empty<double>()
            : detectorHighFrequencyHistory.Select(static value => (double)value).ToArray();
        if (highFreqHistory.Length == 0)
        {
            highFreqHistory = new[] { 0d, 0d };
        }

        double scalarAmplitude = height * 0.38;
        const double MinNoteFrequencyHz = 27.5;
        const double MaxNoteFrequencyHz = 4186.01;
        double minLogFrequency = Math.Log(MinNoteFrequencyHz);
        double maxLogFrequency = Math.Log(MaxNoteFrequencyHz);
        double logFrequencyRange = Math.Max(0.0001, maxLogFrequency - minLogFrequency);
        static double NormalizeLogFrequency(double hz, double minLogHz, double logRange)
        {
            const double minFrequency = MinNoteFrequencyHz;
            const double maxFrequency = MaxNoteFrequencyHz;
            return hz <= 0
                ? 0
                : Math.Clamp((Math.Log(Math.Clamp(hz, minFrequency, maxFrequency)) - minLogHz) / logRange, 0, 1);
        }
        AudioLevelLine.Points = BuildSampledPointCollection(
            levelHistory.Length,
            width,
            GetOverlayPointCount(width, levelHistory.Length),
            sourceIndex => centerY - (Math.Clamp(levelHistory[sourceIndex], 0, 1) * scalarAmplitude));
        AudioBassLine.Points = BuildSampledPointCollection(
            bassHistory.Length,
            width,
            GetOverlayPointCount(width, bassHistory.Length),
            sourceIndex => centerY - (Math.Clamp(bassHistory[sourceIndex], 0, 1) * scalarAmplitude));
        AudioFrequencyLine.Points = BuildSampledPointCollection(
            freqHistory.Length,
            width,
            GetOverlayPointCount(width, freqHistory.Length),
            sourceIndex =>
            {
                double normalizedLogFrequency = NormalizeLogFrequency(Math.Clamp(freqHistory[sourceIndex], 0, 5000.0), minLogFrequency, logFrequencyRange);
                return (height - 2) - (normalizedLogFrequency * (height - 4));
            });
        AudioBassFrequencyLine.Points = BuildSampledPointCollection(
            bassFreqHistory.Length,
            width,
            GetOverlayPointCount(width, bassFreqHistory.Length),
            sourceIndex =>
            {
                double normalizedLogFrequency = NormalizeLogFrequency(bassFreqHistory[sourceIndex], minLogFrequency, logFrequencyRange);
                return (height - 2) - (normalizedLogFrequency * (height - 4));
            });
        AudioMidFrequencyLine.Points = BuildSampledPointCollection(
            midFreqHistory.Length,
            width,
            GetOverlayPointCount(width, midFreqHistory.Length),
            sourceIndex =>
            {
                double normalizedLogFrequency = NormalizeLogFrequency(midFreqHistory[sourceIndex], minLogFrequency, logFrequencyRange);
                return (height - 2) - (normalizedLogFrequency * (height - 4));
            });
        AudioHighFrequencyLine.Points = BuildSampledPointCollection(
            highFreqHistory.Length,
            width,
            GetOverlayPointCount(width, highFreqHistory.Length),
            sourceIndex =>
            {
                double normalizedLogFrequency = NormalizeLogFrequency(highFreqHistory[sourceIndex], minLogFrequency, logFrequencyRange);
                return (height - 2) - (normalizedLogFrequency * (height - 4));
            });
    }

    private void UpdateDebugOverlayVisibility(bool visible)
    {
        if (FrameDebugOverlay != null)
        {
            FrameDebugOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (AudioDebugOverlay != null)
        {
            AudioDebugOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static Geometry? BuildFrameUnderrunGeometry(double[] frameGapHistory, double width, double bottomY, double budgetMs)
    {
        if (frameGapHistory.Length == 0 || width <= 0 || bottomY <= 0 || budgetMs <= 0)
        {
            return null;
        }

        int sampleCount = GetOverlayPointCount(width, frameGapHistory.Length);
        if (sampleCount <= 0)
        {
            return null;
        }

        int overrunCount = 0;
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            int sourceIndex = sampleCount == 1
                ? frameGapHistory.Length - 1
                : (int)Math.Round(sampleIndex * (frameGapHistory.Length - 1d) / (sampleCount - 1d));
            if (frameGapHistory[sourceIndex] > budgetMs)
            {
                overrunCount++;
            }
        }

        if (overrunCount == 0)
        {
            return null;
        }

        double barTop = 2;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                int sourceIndex = sampleCount == 1
                    ? frameGapHistory.Length - 1
                    : (int)Math.Round(sampleIndex * (frameGapHistory.Length - 1d) / (sampleCount - 1d));
                if (frameGapHistory[sourceIndex] <= budgetMs)
                {
                    continue;
                }

                double x = sampleCount == 1
                    ? width * 0.5
                    : sampleIndex * width / (sampleCount - 1d);
                context.BeginFigure(new Point(x, bottomY), isFilled: false, isClosed: false);
                context.LineTo(new Point(x, barTop), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static Geometry? BuildVerticalRangeGeometry(float[] minValues, float[] maxValues, double width, int pointCount, Func<double, double> ySelector)
    {
        int sourceLength = Math.Min(minValues.Length, maxValues.Length);
        if (sourceLength <= 0 || width <= 0 || pointCount <= 0)
        {
            return null;
        }

        pointCount = Math.Clamp(pointCount, 2, Math.Max(2, sourceLength));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            double xDenom = Math.Max(1, pointCount - 1);
            double sourceDenom = Math.Max(1, sourceLength - 1);
            for (int i = 0; i < pointCount; i++)
            {
                int sourceIndex = pointCount >= sourceLength
                    ? i
                    : (int)Math.Round(i * sourceDenom / xDenom);
                sourceIndex = Math.Clamp(sourceIndex, 0, sourceLength - 1);
                double x = width * i / xDenom;
                double yMin = ySelector(minValues[sourceIndex]);
                double yMax = ySelector(maxValues[sourceIndex]);
                context.BeginFigure(new Point(x, Math.Min(yMin, yMax)), isFilled: false, isClosed: false);
                context.LineTo(new Point(x, Math.Max(yMin, yMax)), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static int GetOverlayPointCount(double width, int sourceLength)
    {
        int maxPoints = Math.Max(2, Math.Min((int)Math.Ceiling(width), DebugOverlayMaxPoints));
        return Math.Clamp(sourceLength, 2, maxPoints);
    }

    private static PointCollection BuildSampledPointCollection(int sourceLength, double width, int pointCount, Func<int, double> ySelector)
    {
        if (sourceLength <= 0)
        {
            return new PointCollection
            {
                new Point(0, 0),
                new Point(width, 0)
            };
        }

        pointCount = Math.Clamp(pointCount, 2, Math.Max(2, sourceLength));
        var points = new PointCollection(pointCount);
        double xDenom = Math.Max(1, pointCount - 1);
        double sourceDenom = Math.Max(1, sourceLength - 1);
        for (int i = 0; i < pointCount; i++)
        {
            int sourceIndex = pointCount >= sourceLength
                ? i
                : (int)Math.Round(i * sourceDenom / xDenom);
            sourceIndex = Math.Clamp(sourceIndex, 0, sourceLength - 1);
            double x = width * i / xDenom;
            points.Add(new Point(x, ySelector(sourceIndex)));
        }

        return points;
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
        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            layer.Engine?.SetBinningMode(mode);
        }
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
        bool changed = _injectionMode != mode;
        _injectionMode = mode;
        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            if (layer.InjectionMode != mode)
            {
                layer.InjectionMode = mode;
                changed = true;
            }
            layer.Engine?.SetInjectionMode(mode);
        }
        if (!changed)
        {
            return;
        }

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
        _currentFps = fps;
        if (!_fpsOscillationEnabled)
        {
            _currentSimulationTargetFps = fps;
        }
        ResetFramePumpCadence(scheduleImmediate: true);
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
        ApplyAudioReactiveLifeOpacity();
        Logger.Info($"Life opacity set to {_lifeOpacity:F2}");
        UpdateEffectInput();
        RenderFrame();
        SaveConfig();
    }

    private void RgbHueShiftSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _rgbHueShiftDegrees = NormalizeHueDegrees(e.NewValue);
        if (RgbHueShiftValueText != null)
        {
            RgbHueShiftValueText.Text = $"{_rgbHueShiftDegrees:0.#}deg";
        }

        RenderFrame();
        SaveConfig();
    }

    private void RgbHueShiftSpeedSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _rgbHueShiftSpeedDegreesPerSecond = Math.Clamp(e.NewValue, -MaxRgbHueShiftSpeedDegreesPerSecond, MaxRgbHueShiftSpeedDegreesPerSecond);
        if (RgbHueShiftSpeedValueText != null)
        {
            RgbHueShiftSpeedValueText.Text = $"{_rgbHueShiftSpeedDegreesPerSecond:+0.#;-0.#;0}deg/s";
        }

        RenderFrame();
        SaveConfig();
    }

    private void RgbHueShiftSpeedSlider_OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not Slider slider)
        {
            return;
        }

        slider.Value = 0;
        e.Handled = true;
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

    private void LowContentionMode_Click(object sender, RoutedEventArgs e)
    {
        _lowContentionMode = LowContentionModeMenuItem?.IsChecked == true;
        ApplyPerformancePreferences();
        UpdatePerformanceMenuState();
        SaveConfig();
    }

    private void DecoderThreadsItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int threads))
        {
            return;
        }

        _decoderThreadLimit = Math.Clamp(threads, 0, 8);
        ApplyPerformancePreferences();
        UpdatePerformanceMenuState();
        SaveConfig();
    }

    private void VideoDecodeFpsItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps))
        {
            return;
        }

        _videoDecodeFpsLimit = fps switch
        {
            15 => 15,
            30 => 30,
            _ => DefaultVideoDecodeFpsLimit
        };

        ApplyPerformancePreferences();
        UpdatePerformanceMenuState();
        SaveConfig();
    }

    private async void ExportLiveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_liveProfileExportInProgress || _isShuttingDown)
        {
            return;
        }

        _liveProfileExportInProgress = true;
        if (sender is MenuItem menuItem)
        {
            menuItem.IsEnabled = false;
        }

        try
        {
            string sessionName = $"live-profile-{_configuredRows}p";
            StartProfilingSession(sessionName);
            await Task.Delay(TimeSpan.FromSeconds(6));
            var (report, path) = StopProfilingSessionAndExport();

            var presentMetric = report.Metrics.FirstOrDefault(metric => string.Equals(metric.Name, "presentation_draw_fps", StringComparison.Ordinal));
            if (presentMetric != null)
            {
                Logger.Info($"Live profile exported to {path}. Present avg={presentMetric.Average:F2} fps, p95={presentMetric.P95:F2} fps-equivalent samples.");
            }
            else
            {
                Logger.Info($"Live profile exported to {path}.");
            }

            try
            {
                Clipboard.SetText(path);
            }
            catch
            {
            }

            MessageBox.Show(
                this,
                $"Live profile exported to:\n{path}",
                "LifeViz",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to export live profile.", ex);
            MessageBox.Show(
                this,
                $"Failed to export live profile.\n{ex.Message}",
                "LifeViz",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _liveProfileExportInProgress = false;
            if (sender is MenuItem item)
            {
                item.IsEnabled = true;
            }
        }
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
        foreach (var layer in EnumerateSimulationLeafLayers(_simulationLayers))
        {
            layer.Engine?.SetMode(mode);
        }
        _pulseStep = 0;
        UpdateRgbHueShiftControls();
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

    private static double NormalizeHueDegrees(double value)
    {
        double normalized = value % 360.0;
        if (normalized < 0)
        {
            normalized += 360.0;
        }
        return normalized;
    }

    private static double NormalizeReactiveHueFrequency(double hz)
    {
        if (hz <= 0)
        {
            return 0;
        }

        double clampedHz = Math.Clamp(hz, MinReactiveHueFrequencyHz, MaxReactiveHueFrequencyHz);
        double minLogFrequency = Math.Log(MinReactiveHueFrequencyHz);
        double maxLogFrequency = Math.Log(MaxReactiveHueFrequencyHz);
        double logRange = Math.Max(0.0001, maxLogFrequency - minLogFrequency);
        return Math.Clamp((Math.Log(clampedHz) - minLogFrequency) / logRange, 0, 1);
    }

    private double CurrentRgbHueShiftDegrees(SimulationLayerState layer)
    {
        if (layer.LifeMode != GameOfLifeEngine.LifeMode.RgbChannels)
        {
            return 0;
        }

        double animatedDegrees = layer.RgbHueShiftDegrees;
        if (Math.Abs(layer.EffectiveRgbHueShiftSpeedDegreesPerSecond) > 0.001)
        {
            animatedDegrees += _lifetimeStopwatch.Elapsed.TotalSeconds * layer.EffectiveRgbHueShiftSpeedDegreesPerSecond;
        }

        animatedDegrees += layer.ReactiveHueShiftDegrees;

        return NormalizeHueDegrees(animatedDegrees);
    }

    private void UpdateRgbHueShiftControls()
    {
        if (RgbHueShiftMenu != null)
        {
            RgbHueShiftMenu.Visibility = Visibility.Collapsed;
        }
    }

    private static bool[,] EnsureLayerMask(bool[,]? mask, int rows, int cols)
    {
        if (mask == null || mask.GetLength(0) != rows || mask.GetLength(1) != cols)
        {
            return new bool[rows, cols];
        }

        return mask;
    }

    private void FillLuminanceMask(byte[] buffer, int width, int height, double min, double max, bool invert, GameOfLifeEngine.InjectionMode mode, double noiseProbability, int period, int pulseStep, bool[,] mask, bool invertInput = false)
    {
        min = Math.Clamp(min, 0, 1);
        max = Math.Clamp(max, 0, 1);
        noiseProbability = Math.Clamp(noiseProbability, 0, 1);
        period = Math.Max(1, period);
        int rows = Math.Max(0, height);
        int cols = Math.Max(0, width);
        bool useNoise = noiseProbability > 0;
        bool randomPulse = mode == GameOfLifeEngine.InjectionMode.RandomPulse;

        if (rows == 0 || cols == 0 || buffer.Length < rows * cols * 4 ||
            mask.GetLength(0) != rows || mask.GetLength(1) != cols)
        {
            return;
        }

        int stride = cols * 4;
        if (mode == GameOfLifeEngine.InjectionMode.Threshold && !useNoise)
        {
            Parallel.For(0, rows, row =>
            {
                int rowOffset = row * stride;
                for (int col = 0; col < cols; col++)
                {
                    int index = rowOffset + (col * 4);
                    byte b = buffer[index];
                    byte g = buffer[index + 1];
                    byte r = buffer[index + 2];

                    double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                    if (invertInput)
                    {
                        luminance = 1.0 - luminance;
                    }

                    mask[row, col] = EvaluateThresholdValue(luminance, min, max, invert);
                }
            });
            return;
        }

        if (mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation && !useNoise)
        {
            Parallel.For(0, rows, row =>
            {
                int rowOffset = row * stride;
                for (int col = 0; col < cols; col++)
                {
                    int index = rowOffset + (col * 4);
                    byte b = buffer[index];
                    byte g = buffer[index + 1];
                    byte r = buffer[index + 2];

                    double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                    if (invertInput)
                    {
                        luminance = 1.0 - luminance;
                    }

                    double injectionDrive = MapIntensityThroughThresholdWindow(luminance, min, max, invert);
                    mask[row, col] = PulseWidthAlive(injectionDrive, period, pulseStep);
                }
            });
            return;
        }

        Parallel.For(0, rows, row =>
        {
            int rowOffset = row * stride;
            for (int col = 0; col < cols; col++)
            {
                int index = rowOffset + (col * 4);
                byte b = buffer[index];
                byte g = buffer[index + 1];
                byte r = buffer[index + 2];

                double randomValue = (useNoise || randomPulse) ? Random.Shared.NextDouble() : 0.0;
                bool noiseFail = useNoise && randomValue < noiseProbability;
                double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                if (invertInput)
                {
                    luminance = 1.0 - luminance;
                }

                double injectionDrive = MapIntensityThroughThresholdWindow(luminance, min, max, invert);
                bool alive = false;
                if (mode == GameOfLifeEngine.InjectionMode.RandomPulse)
                {
                    // Random pulse uses threshold-window-shaped intensity as alive probability.
                    alive = randomValue < injectionDrive;
                }
                else if (mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation)
                {
                    // PWM uses threshold-window-shaped intensity as pulse duty cycle.
                    alive = PulseWidthAlive(injectionDrive, period, pulseStep);
                }
                else
                {
                    alive = EvaluateThresholdValue(luminance, min, max, invert);
                }
                mask[row, col] = !noiseFail && alive;
            }
        });
    }

    private void FillChannelMasks(byte[] buffer, int width, int height, double hueShiftDegrees, double min, double max, bool invert, GameOfLifeEngine.InjectionMode mode, double noiseProbability, int rPeriod, int gPeriod, int bPeriod, int pulseStep, bool[,] rMask, bool[,] gMask, bool[,] bMask, bool invertInput = false)
    {
        min = Math.Clamp(min, 0, 1);
        max = Math.Clamp(max, 0, 1);
        noiseProbability = Math.Clamp(noiseProbability, 0, 1);
        rPeriod = Math.Max(1, rPeriod);
        gPeriod = Math.Max(1, gPeriod);
        bPeriod = Math.Max(1, bPeriod);
        int rows = Math.Max(0, height);
        int cols = Math.Max(0, width);
        bool useNoise = noiseProbability > 0;
        bool randomPulse = mode == GameOfLifeEngine.InjectionMode.RandomPulse;

        if (rows == 0 || cols == 0 || buffer.Length < rows * cols * 4 ||
            rMask.GetLength(0) != rows || rMask.GetLength(1) != cols ||
            gMask.GetLength(0) != rows || gMask.GetLength(1) != cols ||
            bMask.GetLength(0) != rows || bMask.GetLength(1) != cols)
        {
            return;
        }

        bool remapToRotatedBins = Math.Abs(hueShiftDegrees) > 0.001;
        double rr = 0, rg = 0, rb = 0, gr = 0, gg = 0, gb = 0, br = 0, bg = 0, bb = 0;
        if (remapToRotatedBins)
        {
            // Convert captured RGB into the rotated bin basis so hue shift changes channel injection behavior.
            BuildHueRotationMatrix(-hueShiftDegrees, out rr, out rg, out rb, out gr, out gg, out gb, out br, out bg, out bb);
        }

        int stride = cols * 4;
        if (mode == GameOfLifeEngine.InjectionMode.Threshold && !useNoise)
        {
            Parallel.For(0, rows, row =>
            {
                int rowOffset = row * stride;
                for (int col = 0; col < cols; col++)
                {
                    int index = rowOffset + (col * 4);
                    byte b = buffer[index];
                    byte g = buffer[index + 1];
                    byte r = buffer[index + 2];

                    double nr = r / 255.0;
                    double ng = g / 255.0;
                    double nb = b / 255.0;
                    if (invertInput)
                    {
                        nr = 1.0 - nr;
                        ng = 1.0 - ng;
                        nb = 1.0 - nb;
                    }
                    if (remapToRotatedBins)
                    {
                        double mappedR = (rr * nr) + (rg * ng) + (rb * nb);
                        double mappedG = (gr * nr) + (gg * ng) + (gb * nb);
                        double mappedB = (br * nr) + (bg * ng) + (bb * nb);
                        nr = Math.Clamp(mappedR, 0, 1);
                        ng = Math.Clamp(mappedG, 0, 1);
                        nb = Math.Clamp(mappedB, 0, 1);
                    }

                    rMask[row, col] = EvaluateThresholdValue(nr, min, max, invert);
                    gMask[row, col] = EvaluateThresholdValue(ng, min, max, invert);
                    bMask[row, col] = EvaluateThresholdValue(nb, min, max, invert);
                }
            });
            return;
        }

        if (mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation && !useNoise)
        {
            Parallel.For(0, rows, row =>
            {
                int rowOffset = row * stride;
                for (int col = 0; col < cols; col++)
                {
                    int index = rowOffset + (col * 4);
                    byte b = buffer[index];
                    byte g = buffer[index + 1];
                    byte r = buffer[index + 2];

                    double nr = r / 255.0;
                    double ng = g / 255.0;
                    double nb = b / 255.0;
                    if (invertInput)
                    {
                        nr = 1.0 - nr;
                        ng = 1.0 - ng;
                        nb = 1.0 - nb;
                    }
                    if (remapToRotatedBins)
                    {
                        double mappedR = (rr * nr) + (rg * ng) + (rb * nb);
                        double mappedG = (gr * nr) + (gg * ng) + (gb * nb);
                        double mappedB = (br * nr) + (bg * ng) + (bb * nb);
                        nr = Math.Clamp(mappedR, 0, 1);
                        ng = Math.Clamp(mappedG, 0, 1);
                        nb = Math.Clamp(mappedB, 0, 1);
                    }

                    double rDrive = MapIntensityThroughThresholdWindow(nr, min, max, invert);
                    double gDrive = MapIntensityThroughThresholdWindow(ng, min, max, invert);
                    double bDrive = MapIntensityThroughThresholdWindow(nb, min, max, invert);
                    rMask[row, col] = PulseWidthAlive(rDrive, rPeriod, pulseStep);
                    gMask[row, col] = PulseWidthAlive(gDrive, gPeriod, pulseStep);
                    bMask[row, col] = PulseWidthAlive(bDrive, bPeriod, pulseStep);
                }
            });
            return;
        }

        Parallel.For(0, rows, row =>
        {
            int rowOffset = row * stride;
            for (int col = 0; col < cols; col++)
            {
                int index = rowOffset + (col * 4);
                byte b = buffer[index];
                byte g = buffer[index + 1];
                byte r = buffer[index + 2];

                double randomGate = (useNoise || randomPulse) ? Random.Shared.NextDouble() : 0.0;
                bool noiseFail = useNoise && randomGate < noiseProbability;
                double nr = r / 255.0;
                double ng = g / 255.0;
                double nb = b / 255.0;
                if (invertInput)
                {
                    nr = 1.0 - nr;
                    ng = 1.0 - ng;
                    nb = 1.0 - nb;
                }
                if (remapToRotatedBins)
                {
                    double mappedR = (rr * nr) + (rg * ng) + (rb * nb);
                    double mappedG = (gr * nr) + (gg * ng) + (gb * nb);
                    double mappedB = (br * nr) + (bg * ng) + (bb * nb);
                    nr = Math.Clamp(mappedR, 0, 1);
                    ng = Math.Clamp(mappedG, 0, 1);
                    nb = Math.Clamp(mappedB, 0, 1);
                }

                double rDrive = MapIntensityThroughThresholdWindow(nr, min, max, invert);
                double gDrive = MapIntensityThroughThresholdWindow(ng, min, max, invert);
                double bDrive = MapIntensityThroughThresholdWindow(nb, min, max, invert);

                bool rAlive = mode == GameOfLifeEngine.InjectionMode.RandomPulse
                    ? randomGate < rDrive
                    : mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation
                        ? PulseWidthAlive(rDrive, rPeriod, pulseStep)
                    : EvaluateThresholdValue(nr, min, max, invert);
                bool gAlive = mode == GameOfLifeEngine.InjectionMode.RandomPulse
                    ? randomGate < gDrive
                    : mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation
                        ? PulseWidthAlive(gDrive, gPeriod, pulseStep)
                    : EvaluateThresholdValue(ng, min, max, invert);
                bool bAlive = mode == GameOfLifeEngine.InjectionMode.RandomPulse
                    ? randomGate < bDrive
                    : mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation
                        ? PulseWidthAlive(bDrive, bPeriod, pulseStep)
                    : EvaluateThresholdValue(nb, min, max, invert);

                rMask[row, col] = !noiseFail && rAlive;
                gMask[row, col] = !noiseFail && gAlive;
                bMask[row, col] = !noiseFail && bAlive;
            }
        });
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

    private void AudioReactiveEnabled_OnChecked(object sender, RoutedEventArgs e)
    {
        _audioReactiveEnabled = AudioReactiveEnabledMenuItem?.IsChecked == true;
        if (!_audioReactiveEnabled)
        {
            _lastAudioReactiveBeatCount = _audioBeatDetector.BeatCount;
            _audioReactiveFpsMultiplier = 1.0;
            _audioReactiveLevelSeedBurstsLastStep = 0;
            _audioReactiveBeatSeedBurstsLastStep = 0;
        }
        UpdateAudioReactiveMenuState();
        SaveConfig();
    }

    private void AudioReactiveLevelToFps_OnChecked(object sender, RoutedEventArgs e)
    {
        _audioReactiveLevelToFpsEnabled = AudioReactiveLevelToFpsMenuItem?.IsChecked == true;
        UpdateAudioReactiveMenuState();
        SaveConfig();
    }

    private void AudioReactiveLevelToLifeOpacity_OnChecked(object sender, RoutedEventArgs e)
    {
        _audioReactiveLevelToLifeOpacityEnabled = AudioReactiveLevelToLifeOpacityMenuItem?.IsChecked == true;
        ApplyAudioReactiveLifeOpacity();
        UpdateEffectInput();
        UpdateAudioReactiveMenuState();
        SaveConfig();
    }

    private void AudioReactiveLevelSeed_OnChecked(object sender, RoutedEventArgs e)
    {
        _audioReactiveLevelSeedEnabled = AudioReactiveLevelSeedMenuItem?.IsChecked == true;
        UpdateAudioReactiveMenuState();
        SaveConfig();
    }

    private void AudioReactiveBeatSeed_OnChecked(object sender, RoutedEventArgs e)
    {
        _audioReactiveBeatSeedEnabled = AudioReactiveBeatSeedMenuItem?.IsChecked == true;
        if (!_audioReactiveBeatSeedEnabled)
        {
            _lastAudioReactiveBeatCount = _audioBeatDetector.BeatCount;
        }
        UpdateAudioReactiveMenuState();
        SaveConfig();
    }

    private void AudioReactiveEnergyGainSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _audioReactiveEnergyGain = Math.Clamp(e.NewValue, 1, 48);
        if (AudioReactiveEnergyGainText != null)
        {
            AudioReactiveEnergyGainText.Text = $"{_audioReactiveEnergyGain:0.0}x";
        }
        SaveConfig();
    }

    private bool IsOutputAudioSelection(string? deviceId) => AudioBeatDetector.IsRenderSelectionId(deviceId);

    private void ApplyAudioInputGainForSelection()
    {
        _audioInputGain = IsOutputAudioSelection(_selectedAudioDeviceId)
            ? _audioInputGainRender
            : _audioInputGainCapture;
        _audioBeatDetector.InputGain = _audioInputGain;
    }

    private void UpdateAudioInputGainUi()
    {
        if (AudioInputGainSlider != null && Math.Abs(AudioInputGainSlider.Value - _audioInputGain) > 0.0001)
        {
            AudioInputGainSlider.Value = _audioInputGain;
        }

        if (AudioInputGainText != null)
        {
            string sourceLabel = IsOutputAudioSelection(_selectedAudioDeviceId) ? "Output" : "Input";
            AudioInputGainText.Text = $"{_audioInputGain:0.00}x ({sourceLabel})";
        }
    }

    private void AudioInputGainSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _audioInputGain = Math.Clamp(e.NewValue, MinAudioInputGain, MaxAudioInputGain);
        if (IsOutputAudioSelection(_selectedAudioDeviceId))
        {
            _audioInputGainRender = _audioInputGain;
        }
        else
        {
            _audioInputGainCapture = _audioInputGain;
        }
        _audioBeatDetector.InputGain = _audioInputGain;
        UpdateAudioInputGainUi();
        SaveConfig();
    }

    private void AudioReactiveLevelSeedMaxSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _audioReactiveLevelSeedMaxBursts = Math.Clamp((int)Math.Round(e.NewValue), 1, 64);
        if (AudioReactiveLevelSeedMaxText != null)
        {
            AudioReactiveLevelSeedMaxText.Text = _audioReactiveLevelSeedMaxBursts.ToString(CultureInfo.InvariantCulture);
        }
        SaveConfig();
    }

    private void AudioReactiveFpsBoostSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _audioReactiveFpsBoost = Math.Clamp(e.NewValue, 0, 2);
        if (AudioReactiveFpsBoostText != null)
        {
            AudioReactiveFpsBoostText.Text = $"+{_audioReactiveFpsBoost * 100:0}%";
        }
        SaveConfig();
    }

    private void AudioReactiveFpsMinSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _audioReactiveFpsMinPercent = Math.Clamp(e.NewValue / 100.0, 0, 1);
        if (AudioReactiveFpsMinText != null)
        {
            AudioReactiveFpsMinText.Text = $"{_audioReactiveFpsMinPercent * 100.0:0}%";
        }
        SaveConfig();
    }

    private void AudioReactiveOpacityMinScalarSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _audioReactiveLifeOpacityMinScalar = Math.Clamp(e.NewValue / 100.0, 0, 1);
        if (AudioReactiveOpacityMinScalarText != null)
        {
            AudioReactiveOpacityMinScalarText.Text = $"{_audioReactiveLifeOpacityMinScalar * 100.0:0}%";
        }
        ApplyAudioReactiveLifeOpacity();
        UpdateEffectInput();
        SaveConfig();
    }

    private void AudioReactiveSeedsPerBeatSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _audioReactiveSeedsPerBeat = Math.Clamp((int)Math.Round(e.NewValue), 1, 8);
        if (AudioReactiveSeedsPerBeatText != null)
        {
            AudioReactiveSeedsPerBeatText.Text = _audioReactiveSeedsPerBeat.ToString(CultureInfo.InvariantCulture);
        }
        SaveConfig();
    }

    private void AudioReactiveSeedCooldownSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _audioReactiveSeedCooldownMs = Math.Clamp(e.NewValue, 80, 1000);
        if (AudioReactiveSeedCooldownText != null)
        {
            AudioReactiveSeedCooldownText.Text = $"{_audioReactiveSeedCooldownMs:0} ms";
        }
        SaveConfig();
    }

    private void AudioReactivePatternItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string tag)
        {
            return;
        }

        if (Enum.TryParse<AudioReactiveSeedPattern>(tag, true, out var pattern))
        {
            _audioReactiveSeedPattern = pattern;
            UpdateAudioReactivePatternChecks();
            SaveConfig();
        }
    }

    private void UpdateAudioReactivePatternChecks()
    {
        if (AudioReactivePatternMenu == null)
        {
            return;
        }

        foreach (var item in AudioReactivePatternMenu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Tag is string tag &&
                Enum.TryParse<AudioReactiveSeedPattern>(tag, true, out var pattern))
            {
                menuItem.IsCheckable = true;
                menuItem.IsChecked = pattern == _audioReactiveSeedPattern;
            }
        }
    }

    private void UpdateAudioReactiveMenuState()
    {
        bool hasAudioDevice = !string.IsNullOrWhiteSpace(_selectedAudioDeviceId);
        bool masterEnabled = _audioReactiveEnabled;
        bool levelEnabled = masterEnabled && _audioReactiveLevelToFpsEnabled && hasAudioDevice;
        bool levelOpacityEnabled = masterEnabled && _audioReactiveLevelToLifeOpacityEnabled && hasAudioDevice;
        bool levelSeedEnabled = masterEnabled && _audioReactiveLevelSeedEnabled && hasAudioDevice;
        bool beatSeedEnabled = masterEnabled && _audioReactiveBeatSeedEnabled && hasAudioDevice;

        if (AudioReactiveLevelToFpsMenuItem != null)
        {
            AudioReactiveLevelToFpsMenuItem.IsEnabled = masterEnabled && hasAudioDevice;
        }
        if (AudioReactiveLevelToLifeOpacityMenuItem != null)
        {
            AudioReactiveLevelToLifeOpacityMenuItem.IsEnabled = masterEnabled && hasAudioDevice;
        }
        if (AudioReactiveLevelSeedMenuItem != null)
        {
            AudioReactiveLevelSeedMenuItem.IsEnabled = masterEnabled && hasAudioDevice;
        }
        if (AudioReactiveBeatSeedMenuItem != null)
        {
            AudioReactiveBeatSeedMenuItem.IsEnabled = masterEnabled && hasAudioDevice;
        }
        if (AudioReactiveEnergyGainSlider != null)
        {
            AudioReactiveEnergyGainSlider.IsEnabled = levelEnabled;
        }
        if (AudioInputGainSlider != null)
        {
            AudioInputGainSlider.IsEnabled = hasAudioDevice;
        }
        if (AudioReactiveFpsBoostSlider != null)
        {
            AudioReactiveFpsBoostSlider.IsEnabled = levelEnabled;
        }
        if (AudioReactiveFpsMinSlider != null)
        {
            AudioReactiveFpsMinSlider.IsEnabled = levelEnabled;
        }
        if (AudioReactiveOpacityMinScalarSlider != null)
        {
            AudioReactiveOpacityMinScalarSlider.IsEnabled = levelOpacityEnabled;
        }
        if (AudioReactiveLevelSeedMaxSlider != null)
        {
            AudioReactiveLevelSeedMaxSlider.IsEnabled = levelSeedEnabled;
        }
        if (AudioReactiveSeedsPerBeatSlider != null)
        {
            AudioReactiveSeedsPerBeatSlider.IsEnabled = beatSeedEnabled;
        }
        if (AudioReactiveSeedCooldownSlider != null)
        {
            AudioReactiveSeedCooldownSlider.IsEnabled = beatSeedEnabled;
        }
        if (AudioReactivePatternMenu != null)
        {
            AudioReactivePatternMenu.IsEnabled = beatSeedEnabled || levelSeedEnabled;
        }
    }

    private void LoadConfig()
    {
        bool clearedLegacyGlobalSimulationConfig = false;
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
            _currentSimulationTargetFps = _currentFpsFromConfig;
            _lifeOpacity = Math.Clamp(config.LifeOpacity, 0, 1);
            _rgbHueShiftDegrees = NormalizeHueDegrees(config.RgbHueShiftDegrees);
            _rgbHueShiftSpeedDegreesPerSecond = Math.Clamp(config.RgbHueShiftSpeedDegreesPerSecond, -MaxRgbHueShiftSpeedDegreesPerSecond, MaxRgbHueShiftSpeedDegreesPerSecond);
            if (Enum.TryParse<GameOfLifeEngine.LifeMode>(config.LifeMode, out var lifeMode))
            {
                _lifeMode = lifeMode;
            }
            if (Enum.TryParse<GameOfLifeEngine.BinningMode>(config.BinningMode, out var binMode))
            {
                _binningMode = binMode;
            }
            _injectionNoise = Math.Clamp(config.InjectionNoise, 0, 1);
            if (Enum.TryParse<GameOfLifeEngine.InjectionMode>(config.InjectionMode, out var injMode))
            {
                _injectionMode = injMode;
            }
            _invertComposite = config.InvertComposite;
            _showFps = config.ShowFps;
            if (config.AnimationBpm > 0)
            {
                _animationBpm = Math.Clamp(config.AnimationBpm, 10, 300);
            }
            _animationAudioSyncEnabled = config.AnimationAudioSyncEnabled;
            if (!string.IsNullOrWhiteSpace(config.RecordingQuality) &&
                Enum.TryParse<RecordingQuality>(config.RecordingQuality, true, out var recordingQuality))
            {
                _recordingQuality = recordingQuality;
            }
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
            _audioReactiveEnabled = config.AudioReactiveEnabled;
            _audioReactiveLevelToFpsEnabled = config.AudioReactiveLevelToFpsEnabled;
            _audioReactiveLevelToLifeOpacityEnabled = config.AudioReactiveLevelToLifeOpacityEnabled;
            _audioReactiveLevelSeedEnabled = config.AudioReactiveLevelSeedEnabled;
            _audioInputGainCapture = Math.Clamp(config.AudioInputGainCapture, MinAudioInputGain, MaxAudioInputGain);
            _audioInputGainRender = Math.Clamp(config.AudioInputGainRender, MinAudioInputGain, MaxAudioInputGain);
            _audioReactiveEnergyGain = Math.Clamp(config.AudioReactiveEnergyGain, 1, 48);
            _audioReactiveFpsBoost = Math.Clamp(config.AudioReactiveFpsBoost, 0, 2);
            _audioReactiveFpsMinPercent = Math.Clamp(config.AudioReactiveFpsMinPercent, 0, 1);
            _audioReactiveLifeOpacityMinScalar = Math.Clamp(config.AudioReactiveLifeOpacityMinScalar, 0, 1);
            _audioReactiveLevelSeedMaxBursts = Math.Clamp(config.AudioReactiveLevelSeedMaxBursts, 1, 64);
            _audioReactiveBeatSeedEnabled = config.AudioReactiveBeatSeedEnabled;
            _audioReactiveSeedsPerBeat = Math.Clamp(config.AudioReactiveSeedsPerBeat, 1, 8);
            _audioReactiveSeedCooldownMs = Math.Clamp(config.AudioReactiveSeedCooldownMs, 80, 1000);
            if (!string.IsNullOrWhiteSpace(config.AudioReactiveSeedPattern) &&
                Enum.TryParse<AudioReactiveSeedPattern>(config.AudioReactiveSeedPattern, true, out var seedPattern))
            {
                _audioReactiveSeedPattern = seedPattern;
            }
            _selectedAudioDeviceId = config.AudioDeviceId;
            _sourceAudioMasterEnabled = config.SourceAudioMasterEnabled;
            _sourceAudioMasterVolume = Math.Clamp(config.SourceAudioMasterVolume, 0, 1);
            _lowContentionMode = config.LowContentionMode;
            _decoderThreadLimit = Math.Clamp(config.DecoderThreadLimit, 0, 8);
            _videoDecodeFpsLimit = config.VideoDecodeFpsLimit == 15 || config.VideoDecodeFpsLimit == 30
                ? config.VideoDecodeFpsLimit
                : DefaultVideoDecodeFpsLimit;
            _fileCapture.SetMasterVideoAudioEnabled(_sourceAudioMasterEnabled);
            _fileCapture.SetMasterVideoAudioVolume(_sourceAudioMasterVolume);
            ApplyPerformancePreferences();
            ApplyAudioInputGainForSelection();
            _aspectRatioLocked = config.AspectRatioLocked;
            _lockedAspectRatio = config.LockedAspectRatio > 0 ? config.LockedAspectRatio : DefaultAspectRatio;
            _pendingFullscreen = config.Fullscreen;

            // Apply startup recovery before any heavyweight scene/audio/simulation restore.
            ApplyStartupRecoveryOverridesIfNeeded();

            if (!string.IsNullOrWhiteSpace(_selectedAudioDeviceId))
            {
                 _ = _audioBeatDetector.InitializeAsync(_selectedAudioDeviceId);
            }
            _lastAudioReactiveBeatCount = _audioBeatDetector.BeatCount;

            _blendMode = ParseBlendModeOrDefault(config.BlendMode, _blendMode);
            bool hasEmbeddedSimulationGroups = ContainsSimulationGroupSource(config.Sources);
            if (!hasEmbeddedSimulationGroups)
            {
                bool hasLegacySimulationConfig =
                    (config.SimulationLayers != null && config.SimulationLayers.Count > 0) ||
                    config.PositiveLayerEnabled ||
                    config.NegativeLayerEnabled ||
                    (config.SimulationLayerOrder != null && config.SimulationLayerOrder.Count > 0);
                if (hasLegacySimulationConfig)
                {
                    clearedLegacyGlobalSimulationConfig = true;
                    Logger.Info("Ignoring legacy global simulation config because the scene stack has no Sim Group source.");
                }

                ClearSimulationLayers();
            }

            RestoreSources(config.Sources);

            ApplyMasterVideoAudioState();
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
            if (clearedLegacyGlobalSimulationConfig)
            {
                SaveConfig();
            }
        }
    }

    private void ApplyStartupRecoveryOverridesIfNeeded()
    {
        if (!_startupRecoveryTriggered)
        {
            return;
        }

        int recoveredRows = Math.Min(_configuredRows, 480);
        double recoveredFps = Math.Min(_currentFpsFromConfig, 60);
        bool wasFullscreen = _pendingFullscreen;
        bool wasShowFps = _showFps;
        bool wasReactiveFpsEnabled = _audioReactiveLevelToFpsEnabled;

        _configuredRows = Math.Clamp(recoveredRows, MinRows, MaxRows);
        _currentFpsFromConfig = Math.Clamp(recoveredFps, 5, 144);
        _currentFps = _currentFpsFromConfig;
        _currentSimulationTargetFps = _currentFpsFromConfig;
        _pendingFullscreen = false;
        _showFps = false;
        _fpsOscillationEnabled = false;
        _audioReactiveLevelToFpsEnabled = false;

        PersistStartupRecoveryOverridesToConfig();

        Logger.Warn(
            $"Startup recovery applied safe launch overrides. " +
            $"Rows={_configuredRows}, Fps={_currentFpsFromConfig:0}, Fullscreen {wasFullscreen}->{_pendingFullscreen}, " +
            $"ShowFps {wasShowFps}->{_showFps}, LevelToFramerate {wasReactiveFpsEnabled}->{_audioReactiveLevelToFpsEnabled}.");
    }

    private void PersistStartupRecoveryOverridesToConfig()
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

            config.Height = _configuredRows;
            config.Columns = 0;
            config.Framerate = _currentFpsFromConfig;
            config.Fullscreen = false;
            config.ShowFps = false;
            config.OscillationEnabled = false;
            config.AudioReactiveLevelToFpsEnabled = false;

            string directory = Path.GetDirectoryName(ConfigPath) ?? string.Empty;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string updatedJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, updatedJson);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to persist startup recovery overrides. {ex.Message}");
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
            bool hasEmbeddedSimulationGroups = EnumerateSources(_sources).Any(source => source.Type == CaptureSource.SourceType.SimGroup);
            var config = new AppConfig
            {
                CaptureThresholdMin = _captureThresholdMin,
                CaptureThresholdMax = _captureThresholdMax,
                InvertThreshold = _invertThreshold,
                Framerate = _currentFpsFromConfig,
                LifeMode = _lifeMode.ToString(),
                BinningMode = _binningMode.ToString(),
                InjectionMode = _injectionMode.ToString(),
                InjectionNoise = _injectionNoise,
                LifeOpacity = _lifeOpacity,
                RgbHueShiftDegrees = 0,
                RgbHueShiftSpeedDegreesPerSecond = 0,
                InvertComposite = _invertComposite,
                ShowFps = _showFps,
                AnimationBpm = _animationBpm,
                AnimationAudioSyncEnabled = _animationAudioSyncEnabled,
                RecordingQuality = _recordingQuality.ToString(),
                Height = _configuredRows,
                Depth = _configuredDepth,
                Passthrough = _passthroughEnabled,
                OscillationEnabled = _fpsOscillationEnabled,
                OscillationBpm = _oscillationBpm,
                OscillationMinFps = _oscillationMinFps,
                OscillationMaxFps = _oscillationMaxFps,
                AudioSyncEnabled = _audioSyncEnabled,
                AudioReactiveEnabled = _audioReactiveEnabled,
                AudioReactiveLevelToFpsEnabled = _audioReactiveLevelToFpsEnabled,
                AudioReactiveLevelToLifeOpacityEnabled = _audioReactiveLevelToLifeOpacityEnabled,
                AudioReactiveLevelSeedEnabled = _audioReactiveLevelSeedEnabled,
                AudioInputGain = _audioInputGainCapture,
                AudioInputGainCapture = _audioInputGainCapture,
                AudioInputGainRender = _audioInputGainRender,
                AudioReactiveEnergyGain = _audioReactiveEnergyGain,
                AudioReactiveFpsBoost = _audioReactiveFpsBoost,
                AudioReactiveFpsMinPercent = _audioReactiveFpsMinPercent,
                AudioReactiveLifeOpacityMinScalar = _audioReactiveLifeOpacityMinScalar,
                AudioReactiveLevelSeedMaxBursts = _audioReactiveLevelSeedMaxBursts,
                AudioReactiveBeatSeedEnabled = _audioReactiveBeatSeedEnabled,
                AudioReactiveSeedsPerBeat = _audioReactiveSeedsPerBeat,
                AudioReactiveSeedCooldownMs = _audioReactiveSeedCooldownMs,
                AudioReactiveSeedPattern = _audioReactiveSeedPattern.ToString(),
                AudioDeviceId = _selectedAudioDeviceId,
                SourceAudioMasterEnabled = _sourceAudioMasterEnabled,
                SourceAudioMasterVolume = _sourceAudioMasterVolume,
                LowContentionMode = _lowContentionMode,
                DecoderThreadLimit = _decoderThreadLimit,
                VideoDecodeFpsLimit = _videoDecodeFpsLimit,
                BlendMode = _blendMode.ToString(),
                SimulationLayers = hasEmbeddedSimulationGroups ? new List<AppConfig.SimulationLayerConfig>() : BuildSimulationLayerConfigs(),
                PositiveLayerBlendMode = BuildLegacyPositiveLayerBlendMode(),
                NegativeLayerBlendMode = BuildLegacyNegativeLayerBlendMode(),
                PositiveLayerEnabled = BuildLegacyPositiveLayerEnabled(),
                NegativeLayerEnabled = BuildLegacyNegativeLayerEnabled(),
                SimulationLayerOrder = BuildLegacySimulationLayerOrder(),
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

    private List<AppConfig.SimulationLayerConfig> BuildSimulationLayerConfigs()
    {
        return _simulationLayers.Select(BuildSimulationLayerConfig).ToList();
    }

    private static AppConfig.SimulationLayerConfig BuildSimulationLayerConfig(SimulationLayerState layer)
    {
        var config = new AppConfig.SimulationLayerConfig
        {
            Id = layer.Id,
            Kind = layer.Kind.ToString(),
            LayerType = layer.LayerType.ToString(),
            Name = layer.Name,
            Enabled = layer.Enabled,
            InputFunction = layer.InputFunction.ToString(),
            BlendMode = layer.BlendMode.ToString(),
            InjectionMode = layer.InjectionMode.ToString(),
            LifeMode = layer.LifeMode.ToString(),
            BinningMode = layer.BinningMode.ToString(),
            InjectionNoise = layer.InjectionNoise,
            LifeOpacity = layer.LifeOpacity,
            RgbHueShiftDegrees = layer.RgbHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = layer.RgbHueShiftSpeedDegreesPerSecond,
            AudioFrequencyHueShiftDegrees = 0,
            ReactiveMappings = layer.ReactiveMappings.Select(mapping => new AppConfig.ReactiveMappingConfig
            {
                Id = mapping.Id,
                Input = mapping.Input.ToString(),
                Output = mapping.Output.ToString(),
                Amount = mapping.Amount,
                ThresholdMin = mapping.ThresholdMin,
                ThresholdMax = mapping.ThresholdMax
            }).ToList(),
            ThresholdMin = layer.ThresholdMin,
            ThresholdMax = layer.ThresholdMax,
            InvertThreshold = layer.InvertThreshold,
            PixelSortCellWidth = layer.PixelSortCellWidth,
            PixelSortCellHeight = layer.PixelSortCellHeight
        };

        foreach (var child in layer.Children)
        {
            config.Children.Add(BuildSimulationLayerConfig(child));
        }

        return config;
    }

    private static AppConfig.SimulationLayerConfig BuildSimulationLayerConfig(SimulationLayerSpec layer)
    {
        var config = new AppConfig.SimulationLayerConfig
        {
            Id = layer.Id,
            Kind = layer.Kind.ToString(),
            LayerType = layer.LayerType.ToString(),
            Name = layer.Name,
            Enabled = layer.Enabled,
            InputFunction = layer.InputFunction.ToString(),
            BlendMode = layer.BlendMode.ToString(),
            InjectionMode = layer.InjectionMode.ToString(),
            LifeMode = layer.LifeMode.ToString(),
            BinningMode = layer.BinningMode.ToString(),
            InjectionNoise = layer.InjectionNoise,
            LifeOpacity = layer.LifeOpacity,
            RgbHueShiftDegrees = layer.RgbHueShiftDegrees,
            RgbHueShiftSpeedDegreesPerSecond = layer.RgbHueShiftSpeedDegreesPerSecond,
            AudioFrequencyHueShiftDegrees = 0,
            ReactiveMappings = layer.ReactiveMappings.Select(mapping => new AppConfig.ReactiveMappingConfig
            {
                Id = mapping.Id,
                Input = mapping.Input.ToString(),
                Output = mapping.Output.ToString(),
                Amount = mapping.Amount,
                ThresholdMin = mapping.ThresholdMin,
                ThresholdMax = mapping.ThresholdMax
            }).ToList(),
            ThresholdMin = layer.ThresholdMin,
            ThresholdMax = layer.ThresholdMax,
            InvertThreshold = layer.InvertThreshold,
            PixelSortCellWidth = layer.PixelSortCellWidth,
            PixelSortCellHeight = layer.PixelSortCellHeight
        };

        foreach (var child in layer.Children)
        {
            config.Children.Add(BuildSimulationLayerConfig(child));
        }

        return config;
    }

    private bool BuildLegacyPositiveLayerEnabled()
    {
        if (!EnumerateSimulationLeafLayers(_simulationLayers).Any())
        {
            return false;
        }

        var positive = EnumerateSimulationLeafLayers(_simulationLayers).FirstOrDefault(layer => layer.InputFunction == SimulationInputFunction.Direct);
        return positive?.Enabled ?? true;
    }

    private bool BuildLegacyNegativeLayerEnabled()
    {
        if (!EnumerateSimulationLeafLayers(_simulationLayers).Any())
        {
            return false;
        }

        var negative = EnumerateSimulationLeafLayers(_simulationLayers).FirstOrDefault(layer => layer.InputFunction == SimulationInputFunction.Inverse);
        return negative?.Enabled ?? true;
    }

    private string BuildLegacyPositiveLayerBlendMode()
    {
        var positive = EnumerateSimulationLeafLayers(_simulationLayers).FirstOrDefault(layer => layer.InputFunction == SimulationInputFunction.Direct);
        return (positive?.BlendMode ?? BlendMode.Additive).ToString();
    }

    private string BuildLegacyNegativeLayerBlendMode()
    {
        var negative = EnumerateSimulationLeafLayers(_simulationLayers).FirstOrDefault(layer => layer.InputFunction == SimulationInputFunction.Inverse);
        return (negative?.BlendMode ?? BlendMode.Subtractive).ToString();
    }

    private List<string> BuildLegacySimulationLayerOrder()
    {
        var simulationLeaves = EnumerateSimulationLeafLayers(_simulationLayers).ToArray();
        if (simulationLeaves.Length == 0)
        {
            return new List<string>();
        }

        var positive = simulationLeaves.FirstOrDefault(layer => layer.InputFunction == SimulationInputFunction.Direct);
        var negative = simulationLeaves.FirstOrDefault(layer => layer.InputFunction == SimulationInputFunction.Inverse);
        var order = new List<string>(2);
        foreach (var layer in simulationLeaves)
        {
            if (positive != null && ReferenceEquals(layer, positive) && !order.Contains("Positive", StringComparer.OrdinalIgnoreCase))
            {
                order.Add("Positive");
            }
            else if (negative != null && ReferenceEquals(layer, negative) && !order.Contains("Negative", StringComparer.OrdinalIgnoreCase))
            {
                order.Add("Negative");
            }
        }

        if (!order.Contains("Positive", StringComparer.OrdinalIgnoreCase))
        {
            order.Add("Positive");
        }
        if (!order.Contains("Negative", StringComparer.OrdinalIgnoreCase))
        {
            order.Add("Negative");
        }

        return order;
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
                Enabled = source.Enabled,
                WindowTitle = source.Window?.Title,
                WebcamId = source.WebcamId,
                FilePath = source.FilePath,
                DisplayName = source.DisplayName,
                BlendMode = source.BlendMode.ToString(),
                FitMode = source.FitMode.ToString(),
                Opacity = source.Opacity,
                VideoAudioEnabled = source.VideoAudioEnabled,
                VideoAudioVolume = source.VideoAudioVolume,
                Mirror = source.Mirror,
                KeyEnabled = source.KeyEnabled,
                KeyColor = FormatHexColor(source.KeyColorR, source.KeyColorG, source.KeyColorB),
                KeyTolerance = source.KeyTolerance,
                Animations = BuildAnimationConfigs(source.Animations)
            };

            if (source.Type == CaptureSource.SourceType.VideoSequence && source.FilePaths.Count > 0)
            {
                config.FilePaths = new List<string>(source.FilePaths);
            }

            if (source.Type == CaptureSource.SourceType.Group && source.Children.Count > 0)
            {
                config.Children = BuildSourceConfigs(source.Children);
            }

            if (source.Type == CaptureSource.SourceType.SimGroup && source.SimulationLayers.Count > 0)
            {
                config.SimulationLayers = source.SimulationLayers.Select(BuildSimulationLayerConfig).ToList();
            }

            configs.Add(config);
        }

        return configs;
    }

    private static bool ContainsSimulationGroupSource(IReadOnlyList<AppConfig.SourceConfig>? sources)
    {
        if (sources == null)
        {
            return false;
        }

        foreach (var source in sources)
        {
            if (Enum.TryParse<CaptureSource.SourceType>(source.Type, true, out var type) &&
                type == CaptureSource.SourceType.SimGroup)
            {
                return true;
            }

            if (ContainsSimulationGroupSource(source.Children))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryAddLegacySimulationGroupSource(IReadOnlyList<SimulationLayerSpec> simulationSpecs)
    {
        if (simulationSpecs.Count == 0 ||
            EnumerateSources(_sources).Any(source => source.Type == CaptureSource.SourceType.SimGroup))
        {
            return false;
        }

        var source = CaptureSource.CreateSimulationGroup("Simulation");
        foreach (var simulationLayer in FlattenSourceSimulationLayerSpecs(simulationSpecs))
        {
            source.SimulationLayers.Add(CloneSimulationLayerSpec(simulationLayer));
        }

        if (source.SimulationLayers.Count == 0)
        {
            return false;
        }

        _sources.Add(source);
        return true;
    }

    private static List<AppConfig.AnimationConfig> BuildAnimationConfigs(List<LayerAnimation> animations)
    {
        var configs = new List<AppConfig.AnimationConfig>(animations.Count);
        foreach (var animation in animations)
        {
            configs.Add(new AppConfig.AnimationConfig
            {
                Type = animation.Type.ToString(),
                Loop = animation.Loop.ToString(),
                Speed = animation.Speed.ToString(),
                TranslateDirection = animation.TranslateDirection.ToString(),
                RotationDirection = animation.RotationDirection.ToString(),
                RotationDegrees = animation.RotationDegrees,
                DvdScale = animation.DvdScale,
                BeatShakeIntensity = animation.BeatShakeIntensity,
                AudioGranularLowGain = animation.AudioGranularLowGain,
                AudioGranularMidGain = animation.AudioGranularMidGain,
                AudioGranularHighGain = animation.AudioGranularHighGain,
                BeatsPerCycle = animation.BeatsPerCycle
            });
        }
        return configs;
    }

    internal List<LayerEditorSource> BuildLayerEditorSources() => BuildLayerEditorSources(_sources, null);

    private List<LayerEditorSource> BuildLayerEditorSources(List<CaptureSource> sources, LayerEditorSource? parent)
    {
        var list = new List<LayerEditorSource>(sources.Count);
        foreach (var source in sources)
        {
            var model = new LayerEditorSource
            {
                Id = source.Id,
                Kind = source.Type switch
                {
                    CaptureSource.SourceType.Window => LayerEditorSourceKind.Window,
                    CaptureSource.SourceType.Webcam => LayerEditorSourceKind.Webcam,
                    CaptureSource.SourceType.File => LayerEditorSourceKind.File,
                    CaptureSource.SourceType.VideoSequence => LayerEditorSourceKind.VideoSequence,
                    CaptureSource.SourceType.Group => LayerEditorSourceKind.Group,
                    CaptureSource.SourceType.SimGroup => LayerEditorSourceKind.SimGroup,
                    _ => LayerEditorSourceKind.File
                },
                Enabled = source.Enabled,
                DisplayName = source.DisplayName,
                WindowTitle = source.Window?.Title,
                WindowHandle = source.Window?.Handle,
                WebcamId = source.WebcamId,
                FilePath = source.FilePath,
                BlendMode = source.BlendMode.ToString(),
                FitMode = source.FitMode.ToString(),
                Opacity = source.Opacity,
                VideoAudioEnabled = source.VideoAudioEnabled,
                VideoAudioVolume = source.VideoAudioVolume,
                Mirror = source.Mirror,
                KeyEnabled = source.KeyEnabled,
                KeyTolerance = source.KeyTolerance,
                KeyColorHex = FormatHexColor(source.KeyColorR, source.KeyColorG, source.KeyColorB),
                Parent = parent
            };

            if (TryGetSourceVideoPlaybackState(source, out var playbackState))
            {
                model.VideoPlaybackPaused = playbackState.IsPaused;
                model.VideoPlaybackPosition = playbackState.NormalizedPosition;
                model.VideoPlaybackPositionSeconds = playbackState.PositionSeconds;
                model.VideoPlaybackDurationSeconds = playbackState.DurationSeconds;
                source.VideoPlaybackPaused = playbackState.IsPaused;
            }

            if (source.Type == CaptureSource.SourceType.VideoSequence && source.FilePaths.Count > 0)
            {
                model.FilePaths.AddRange(source.FilePaths);
            }

            foreach (var animation in source.Animations)
            {
                model.Animations.Add(new LayerEditorAnimation
                {
                    Id = animation.Id,
                    Type = animation.Type.ToString(),
                    Loop = animation.Loop.ToString(),
                    Speed = animation.Speed.ToString(),
                    TranslateDirection = animation.TranslateDirection.ToString(),
                    RotationDirection = animation.RotationDirection.ToString(),
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

            if (source.Type == CaptureSource.SourceType.Group && source.Children.Count > 0)
            {
                foreach (var child in BuildLayerEditorSources(source.Children, model))
                {
                    model.Children.Add(child);
                }
            }

            if (source.Type == CaptureSource.SourceType.SimGroup && source.SimulationLayers.Count > 0)
            {
                foreach (var simulationLayer in FlattenSourceSimulationLayerSpecs(source.SimulationLayers))
                {
                    model.SimulationLayers.Add(ToEditorSimulationLayer(simulationLayer));
                }
            }

            list.Add(model);
        }

        return list;
    }

    internal void ApplyLayerEditorSources(IReadOnlyList<LayerEditorSource> sources)
    {
        var existing = EnumerateSources(_sources).ToDictionary(source => source.Id);
        var used = new HashSet<Guid>();
        var windows = _windowCapture.EnumerateWindows(_windowHandle);
        var webcams = _webcamCapture.EnumerateCameras();

        var rebuilt = BuildSourcesFromEditor(sources, existing, used, windows, webcams);

        if (rebuilt.Count == 0)
        {
            ClearSources();
            return;
        }

        foreach (var source in EnumerateSources(_sources))
        {
            if (!used.Contains(source.Id))
            {
                CleanupSource(source);
            }
        }

        _sources.Clear();
        _sources.AddRange(rebuilt);
        ApplySimulationLayersFromSourceStack(fallbackToDefault: false);

        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
        RebuildSourcesMenu();
    }

    private List<CaptureSource> BuildSourcesFromEditor(
        IReadOnlyList<LayerEditorSource> models,
        IDictionary<Guid, CaptureSource> existing,
        HashSet<Guid> used,
        IReadOnlyList<WindowHandleInfo> windows,
        IReadOnlyList<WebcamCaptureService.CameraInfo> webcams)
    {
        var list = new List<CaptureSource>(models.Count);
        foreach (var model in models)
        {
            var source = BuildSourceFromEditor(model, existing, used, windows, webcams);
            if (source != null)
            {
                list.Add(source);
            }
        }

        return list;
    }

    private CaptureSource? BuildSourceFromEditor(
        LayerEditorSource model,
        IDictionary<Guid, CaptureSource> existing,
        HashSet<Guid> used,
        IReadOnlyList<WindowHandleInfo> windows,
        IReadOnlyList<WebcamCaptureService.CameraInfo> webcams)
    {
        if (!existing.TryGetValue(model.Id, out var source))
        {
            source = CreateSourceFromEditor(model, windows, webcams);
            if (source == null)
            {
                return null;
            }
        }

        used.Add(source.Id);
        ApplySourceModel(source, model);

        if (source.Type == CaptureSource.SourceType.Group)
        {
            source.Children.Clear();
            foreach (var child in BuildSourcesFromEditor(model.Children, existing, used, windows, webcams))
            {
                source.Children.Add(child);
            }
        }
        else if (source.Children.Count > 0)
        {
            source.Children.Clear();
        }

        return source;
    }

    private CaptureSource? CreateSourceFromEditor(
        LayerEditorSource model,
        IReadOnlyList<WindowHandleInfo> windows,
        IReadOnlyList<WebcamCaptureService.CameraInfo> webcams)
    {
        switch (model.Kind)
        {
            case LayerEditorSourceKind.Group:
                return CaptureSource.CreateGroup(string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName);

            case LayerEditorSourceKind.SimGroup:
                return CaptureSource.CreateSimulationGroup(string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName);

            case LayerEditorSourceKind.Window:
            {
                WindowHandleInfo? match = null;
                if (model.WindowHandle.HasValue)
                {
                    match = windows.FirstOrDefault(w => w.Handle == model.WindowHandle.Value);
                }
                if (match == null && !string.IsNullOrWhiteSpace(model.WindowTitle))
                {
                    match = windows.FirstOrDefault(w => string.Equals(w.Title, model.WindowTitle, StringComparison.OrdinalIgnoreCase));
                }

                return match != null ? CaptureSource.CreateWindow(match) : null;
            }

            case LayerEditorSourceKind.Webcam:
            {
                var camera = webcams.FirstOrDefault(c =>
                    (!string.IsNullOrWhiteSpace(model.WebcamId) && string.Equals(c.Id, model.WebcamId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(model.DisplayName) && string.Equals(c.Name, model.DisplayName, StringComparison.OrdinalIgnoreCase)));
                return !string.IsNullOrWhiteSpace(camera.Id) ? CaptureSource.CreateWebcam(camera.Id, camera.Name) : null;
            }

            case LayerEditorSourceKind.File:
            {
                if (string.IsNullOrWhiteSpace(model.FilePath))
                {
                    return null;
                }

                if (_fileCapture.TryGetOrAdd(model.FilePath, out var info, out _))
                {
                    return CaptureSource.CreateFile(info.Path, info.DisplayName, info.Width, info.Height);
                }

                return null;
            }

            case LayerEditorSourceKind.VideoSequence:
            {
                var sequencePaths = model.FilePaths.Count > 0
                    ? model.FilePaths
                    : (!string.IsNullOrWhiteSpace(model.FilePath) ? new List<string> { model.FilePath } : null);
                if (sequencePaths == null || sequencePaths.Count == 0)
                {
                    return null;
                }

                if (_fileCapture.TryCreateVideoSequence(sequencePaths, out var sequence, out _))
                {
                    return CaptureSource.CreateVideoSequence(sequence!);
                }

                return null;
            }

            case LayerEditorSourceKind.Youtube:
            {
                if (string.IsNullOrWhiteSpace(model.FilePath))
                {
                    return null;
                }

                string rawPath = model.FilePath.Trim();
                string? youtubeKey = NormalizeYoutubeKey(rawPath);
                if (youtubeKey != null)
                {
                    string displayName = string.IsNullOrWhiteSpace(model.DisplayName) ? "YouTube Source" : model.DisplayName;
                    var source = CaptureSource.CreateFile(youtubeKey, displayName, 0, 0);
                    QueueYoutubeResolution(youtubeKey, source);
                    return source;
                }

                string url = rawPath;
                if (url.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase))
                {
                    url = url.Substring(8);
                }

                // Blocking call for async resolution (fallback when the key is ambiguous).
                try
                {
                    var task = _fileCapture.TryCreateYoutubeSource(url);
                    task.Wait();
                    var (success, info, _) = task.Result;

                    if (success)
                    {
                        return CaptureSource.CreateFile(info.Path, info.DisplayName, info.Width, info.Height);
                    }
                }
                catch
                {
                    // Ignore errors during apply.
                }

                return null;
            }
        }

        return null;
    }

    private void ApplySourceModel(CaptureSource source, LayerEditorSource model)
    {
        source.Enabled = model.Enabled;
        source.BlendMode = ParseBlendModeOrDefault(model.BlendMode, source.BlendMode);

        if (Enum.TryParse<FitMode>(model.FitMode, true, out var fitMode))
        {
            source.FitMode = fitMode;
        }

        source.Opacity = Math.Clamp(model.Opacity, 0, 1);
        source.VideoAudioEnabled = model.VideoAudioEnabled;
        source.VideoAudioVolume = Math.Clamp(model.VideoAudioVolume, 0, 1);
        source.Mirror = model.Mirror;
        source.KeyEnabled = model.KeyEnabled;
        source.KeyTolerance = Math.Clamp(model.KeyTolerance, 0, 1);
        if (TryParseHexColor(model.KeyColorHex, out var keyR, out var keyG, out var keyB))
        {
            source.KeyColorR = keyR;
            source.KeyColorG = keyG;
            source.KeyColorB = keyB;
        }
        ApplySourceVideoAudioState(source);

        if (source.Type == CaptureSource.SourceType.Group || source.Type == CaptureSource.SourceType.SimGroup)
        {
            source.SetDisplayName(string.IsNullOrWhiteSpace(model.DisplayName)
                ? (source.Type == CaptureSource.SourceType.SimGroup ? "Sim Group" : "Layer Group")
                : model.DisplayName);
        }

        if (source.Type == CaptureSource.SourceType.SimGroup)
        {
            source.SimulationLayers.Clear();
            foreach (var simulationLayer in FlattenSourceSimulationLayerSpecs(NormalizeSimulationLayerSpecs(model.SimulationLayers, fallbackToDefault: false)))
            {
                source.SimulationLayers.Add(simulationLayer);
            }
        }
        else if (source.SimulationLayers.Count > 0)
        {
            source.SimulationLayers.Clear();
        }

        source.Animations.Clear();
        foreach (var animationModel in model.Animations)
        {
            var animation = new LayerAnimation();
            if (Enum.TryParse<AnimationType>(animationModel.Type, true, out var type))
            {
                animation.Type = type;
            }
            if (Enum.TryParse<AnimationLoop>(animationModel.Loop, true, out var loop))
            {
                animation.Loop = loop;
            }
            if (Enum.TryParse<AnimationSpeed>(animationModel.Speed, true, out var speed))
            {
                animation.Speed = speed;
            }
            if (Enum.TryParse<TranslateDirection>(animationModel.TranslateDirection, true, out var translate))
            {
                animation.TranslateDirection = translate;
            }
            if (Enum.TryParse<RotationDirection>(animationModel.RotationDirection, true, out var rotate))
            {
                animation.RotationDirection = rotate;
            }
            if (animationModel.DvdScale > 0)
            {
                animation.DvdScale = Math.Clamp(animationModel.DvdScale, 0.01, 1.0);
            }
            if (animationModel.BeatsPerCycle > 0)
            {
                animation.BeatsPerCycle = Math.Clamp(animationModel.BeatsPerCycle, 1, 4096);
            }
            animation.BeatShakeIntensity = ClampAnimationIntensity(animation.Type, animationModel.BeatShakeIntensity);
            animation.AudioGranularLowGain = Math.Clamp(animationModel.AudioGranularLowGain, 0, MaxAudioGranularEqBandGain);
            animation.AudioGranularMidGain = Math.Clamp(animationModel.AudioGranularMidGain, 0, MaxAudioGranularEqBandGain);
            animation.AudioGranularHighGain = Math.Clamp(animationModel.AudioGranularHighGain, 0, MaxAudioGranularEqBandGain);
            animation.RotationDegrees = Math.Clamp(animationModel.RotationDegrees, 0, 360);

            source.Animations.Add(animation);
        }
    }

    private static string? NormalizeYoutubeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase))
        {
            string idCandidate = trimmed.Substring(8).Trim();
            return IsLikelyYoutubeId(idCandidate) ? $"youtube:{idCandidate}" : null;
        }

        return IsLikelyYoutubeId(trimmed) ? $"youtube:{trimmed}" : null;
    }

    private static bool IsLikelyYoutubeId(string value)
    {
        if (value.Length != 11)
        {
            return false;
        }

        foreach (char ch in value)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
            {
                return false;
            }
        }

        return true;
    }

    private void QueueYoutubeResolution(string youtubeKey, CaptureSource source)
    {
        _ = ResolveYoutubeSourceAsync(youtubeKey, source);
    }

    private async Task ResolveYoutubeSourceAsync(string youtubeKey, CaptureSource source)
    {
        if (string.IsNullOrWhiteSpace(youtubeKey))
        {
            return;
        }

        string url = youtubeKey;
        if (url.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase))
        {
            url = url.Substring(8);
        }

        var (success, info, error) = await _fileCapture.TryCreateYoutubeSource(url);
        if (!success)
        {
            Logger.Warn($"Failed to resolve YouTube source: {youtubeKey}. {error}");
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (FindSourceById(source.Id) == null)
            {
                return;
            }

            source.UpdateFileDimensions(info.Width, info.Height);
            if (string.IsNullOrWhiteSpace(source.DisplayName) ||
                string.Equals(source.DisplayName, "YouTube Source", StringComparison.OrdinalIgnoreCase))
            {
                source.SetDisplayName(info.DisplayName);
            }
            ApplySourceVideoAudioState(source);

            UpdatePrimaryAspectIfNeeded();
            RenderFrame();
            SaveConfig();
            RebuildSourcesMenu();
            NotifyLayerEditorSourcesChanged();
        });
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
        if (EnumerateSources(_sources).Any(source => source.Type == CaptureSource.SourceType.SimGroup))
        {
            ApplySimulationLayersFromSourceStack(fallbackToDefault: false);
        }

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

                case CaptureSource.SourceType.SimGroup:
                    restored = CaptureSource.CreateSimulationGroup(string.IsNullOrWhiteSpace(config.DisplayName) ? null : config.DisplayName);
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

                    string? youtubeKey = NormalizeYoutubeKey(config.FilePath);
                    if (youtubeKey != null)
                    {
                        string displayName = string.IsNullOrWhiteSpace(config.DisplayName)
                            ? "YouTube Source"
                            : config.DisplayName;
                        restored = CaptureSource.CreateFile(youtubeKey, displayName, 0, 0);
                        QueueYoutubeResolution(youtubeKey, restored);
                        break;
                    }

                    if (_fileCapture.TryGetOrAdd(config.FilePath, out var info, out _))
                    {
                        restored = CaptureSource.CreateFile(info.Path, info.DisplayName, info.Width, info.Height);
                    }
                    break;

                case CaptureSource.SourceType.VideoSequence:
                    var sequencePaths = config.FilePaths != null && config.FilePaths.Count > 0
                        ? config.FilePaths
                        : (!string.IsNullOrWhiteSpace(config.FilePath) ? new List<string> { config.FilePath } : null);
                    if (sequencePaths == null || sequencePaths.Count == 0)
                    {
                        break;
                    }

                    if (_fileCapture.TryCreateVideoSequence(sequencePaths, out var sequence, out _))
                    {
                        restored = CaptureSource.CreateVideoSequence(sequence!);
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

    private void ApplySourceSettings(CaptureSource source, AppConfig.SourceConfig config)
    {
        source.Enabled = config.Enabled;
        source.BlendMode = ParseBlendModeOrDefault(config.BlendMode, source.BlendMode);

        if (Enum.TryParse<FitMode>(config.FitMode, true, out var fitMode))
        {
            source.FitMode = fitMode;
        }

        source.Opacity = Math.Clamp(config.Opacity, 0, 1);
        source.VideoAudioEnabled = config.VideoAudioEnabled;
        source.VideoAudioVolume = Math.Clamp(config.VideoAudioVolume, 0, 1);
        source.Mirror = config.Mirror;
        source.KeyEnabled = config.KeyEnabled;
        source.KeyTolerance = Math.Clamp(config.KeyTolerance, 0, 1);
        if (TryParseHexColor(config.KeyColor, out var keyR, out var keyG, out var keyB))
        {
            source.KeyColorR = keyR;
            source.KeyColorG = keyG;
            source.KeyColorB = keyB;
        }
        ApplySourceVideoAudioState(source);

        source.SimulationLayers.Clear();
        if (source.Type == CaptureSource.SourceType.SimGroup && config.SimulationLayers.Count > 0)
        {
            foreach (var simulationLayer in FlattenSourceSimulationLayerSpecs(NormalizeSimulationLayerSpecs(config.SimulationLayers, fallbackToDefault: false)))
            {
                source.SimulationLayers.Add(simulationLayer);
            }
        }

        source.Animations.Clear();
        if (config.Animations != null && config.Animations.Count > 0)
        {
            foreach (var animationConfig in config.Animations)
            {
                var animation = new LayerAnimation();
                if (Enum.TryParse<AnimationType>(animationConfig.Type, true, out var type))
                {
                    animation.Type = type;
                }
                if (Enum.TryParse<AnimationLoop>(animationConfig.Loop, true, out var loop))
                {
                    animation.Loop = loop;
                }
                if (Enum.TryParse<AnimationSpeed>(animationConfig.Speed, true, out var speed))
                {
                    animation.Speed = speed;
                }
                if (Enum.TryParse<TranslateDirection>(animationConfig.TranslateDirection, true, out var translate))
                {
                    animation.TranslateDirection = translate;
                }
                if (Enum.TryParse<RotationDirection>(animationConfig.RotationDirection, true, out var rotate))
                {
                    animation.RotationDirection = rotate;
                }
                animation.RotationDegrees = Math.Clamp(animationConfig.RotationDegrees, 0, 360);
                if (animationConfig.DvdScale > 0)
                {
                    animation.DvdScale = Math.Clamp(animationConfig.DvdScale, 0.01, 1.0);
                }
                animation.BeatShakeIntensity = ClampAnimationIntensity(animation.Type, animationConfig.BeatShakeIntensity);
                animation.AudioGranularLowGain = Math.Clamp(animationConfig.AudioGranularLowGain, 0, MaxAudioGranularEqBandGain);
                animation.AudioGranularMidGain = Math.Clamp(animationConfig.AudioGranularMidGain, 0, MaxAudioGranularEqBandGain);
                animation.AudioGranularHighGain = Math.Clamp(animationConfig.AudioGranularHighGain, 0, MaxAudioGranularEqBandGain);
                if (animationConfig.BeatsPerCycle > 0)
                {
                    animation.BeatsPerCycle = Math.Clamp(animationConfig.BeatsPerCycle, 1, 4096);
                }
                source.Animations.Add(animation);
            }
        }
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
        public double InjectionNoise { get; set; } = 0.0;
        public double LifeOpacity { get; set; } = 1.0;
        public double RgbHueShiftDegrees { get; set; }
        public double RgbHueShiftSpeedDegreesPerSecond { get; set; }
        public bool InvertComposite { get; set; }
        public bool ShowFps { get; set; }
        public double AnimationBpm { get; set; } = DefaultAnimationBpm;
        public bool AnimationAudioSyncEnabled { get; set; }
        public string RecordingQuality { get; set; } = global::lifeviz.RecordingQuality.High.ToString();
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
        public bool AudioReactiveEnabled { get; set; }
        public bool AudioReactiveLevelToFpsEnabled { get; set; } = true;
        public bool AudioReactiveLevelToLifeOpacityEnabled { get; set; }
        public bool AudioReactiveLevelSeedEnabled { get; set; } = true;
        public double AudioInputGain { get; set; } = DefaultAudioInputGain;
        public double AudioInputGainCapture { get; set; } = DefaultAudioInputGain;
        public double AudioInputGainRender { get; set; } = DefaultAudioOutputGain;
        public double AudioReactiveEnergyGain { get; set; } = DefaultAudioReactiveEnergyGain;
        public double AudioReactiveFpsBoost { get; set; } = DefaultAudioReactiveFpsBoost;
        public double AudioReactiveFpsMinPercent { get; set; } = DefaultAudioReactiveFpsMinPercent;
        public double AudioReactiveLifeOpacityMinScalar { get; set; } = DefaultAudioReactiveLifeOpacityMinScalar;
        public int AudioReactiveLevelSeedMaxBursts { get; set; } = DefaultAudioReactiveLevelSeedMaxBursts;
        public bool AudioReactiveBeatSeedEnabled { get; set; } = true;
        public int AudioReactiveSeedsPerBeat { get; set; } = DefaultAudioReactiveSeedsPerBeat;
        public double AudioReactiveSeedCooldownMs { get; set; } = DefaultAudioReactiveSeedCooldownMs;
        public string AudioReactiveSeedPattern { get; set; } = MainWindow.AudioReactiveSeedPattern.Glider.ToString();
        public string? AudioDeviceId { get; set; }
        public bool SourceAudioMasterEnabled { get; set; } = true;
        public double SourceAudioMasterVolume { get; set; } = 1.0;
        public bool LowContentionMode { get; set; }
        public int DecoderThreadLimit { get; set; } = DefaultDecoderThreadLimit;
        public int VideoDecodeFpsLimit { get; set; } = DefaultVideoDecodeFpsLimit;
        public string BlendMode { get; set; } = MainWindow.BlendMode.Additive.ToString();
        public List<SimulationLayerConfig> SimulationLayers { get; set; } = new();
        public string PositiveLayerBlendMode { get; set; } = MainWindow.BlendMode.Additive.ToString();
        public string NegativeLayerBlendMode { get; set; } = MainWindow.BlendMode.Subtractive.ToString();
        public bool PositiveLayerEnabled { get; set; } = true;
        public bool NegativeLayerEnabled { get; set; } = true;
        public List<string> SimulationLayerOrder { get; set; } = new() { "Positive", "Negative" };
        public bool Fullscreen { get; set; }
        public bool AspectRatioLocked { get; set; }
        public double LockedAspectRatio { get; set; } = DefaultAspectRatio;
        public List<SourceConfig> Sources { get; set; } = new();

        public sealed class SimulationLayerConfig
        {
            public Guid Id { get; set; }
            public string Kind { get; set; } = nameof(LayerEditorSimulationItemKind.Layer);
            public string LayerType { get; set; } = nameof(SimulationLayerType.Life);
            public string Name { get; set; } = "Life Sim";
            public bool Enabled { get; set; } = true;
            public string InputFunction { get; set; } = SimulationInputFunction.Direct.ToString();
            public string BlendMode { get; set; } = MainWindow.BlendMode.Subtractive.ToString();
            public string InjectionMode { get; set; } = GameOfLifeEngine.InjectionMode.Threshold.ToString();
            public string LifeMode { get; set; } = GameOfLifeEngine.LifeMode.NaiveGrayscale.ToString();
            public string BinningMode { get; set; } = GameOfLifeEngine.BinningMode.Fill.ToString();
            public double InjectionNoise { get; set; }
            public double LifeOpacity { get; set; } = 1.0;
            public double RgbHueShiftDegrees { get; set; }
            public double RgbHueShiftSpeedDegreesPerSecond { get; set; }
            public double AudioFrequencyHueShiftDegrees { get; set; }
            public List<ReactiveMappingConfig> ReactiveMappings { get; set; } = new();
            public double ThresholdMin { get; set; } = 0.35;
            public double ThresholdMax { get; set; } = 0.75;
            public bool InvertThreshold { get; set; }
            public int PixelSortCellWidth { get; set; } = 12;
            public int PixelSortCellHeight { get; set; } = 8;
            public int PixelSortGridColumns { get; set; }
            public int PixelSortGridRows { get; set; }
            public List<SimulationLayerConfig> Children { get; set; } = new();
        }

        public sealed class ReactiveMappingConfig
        {
            public Guid Id { get; set; }
            public string Input { get; set; } = nameof(SimulationReactiveInput.Level);
            public string Output { get; set; } = nameof(SimulationReactiveOutput.Opacity);
            public double Amount { get; set; } = 1.0;
            public double ThresholdMin { get; set; }
            public double ThresholdMax { get; set; } = 1.0;
        }

        public sealed class SourceConfig
        {
            public string Type { get; set; } = CaptureSource.SourceType.Window.ToString();
            public bool Enabled { get; set; } = true;
            public string? WindowTitle { get; set; }
            public string? WebcamId { get; set; }
            public string? FilePath { get; set; }
            public List<string> FilePaths { get; set; } = new();
            public string? DisplayName { get; set; }
            public string BlendMode { get; set; } = MainWindow.BlendMode.Additive.ToString();
            public string FitMode { get; set; } = lifeviz.FitMode.Fill.ToString();
            public double Opacity { get; set; } = 1.0;
            public bool VideoAudioEnabled { get; set; }
            public double VideoAudioVolume { get; set; } = 1.0;
            public bool Mirror { get; set; }
            public bool KeyEnabled { get; set; }
            public string KeyColor { get; set; } = "#000000";
            public double KeyTolerance { get; set; } = DefaultKeyTolerance;
            public List<AnimationConfig> Animations { get; set; } = new();
            public List<SimulationLayerConfig> SimulationLayers { get; set; } = new();
            public List<SourceConfig> Children { get; set; } = new();
        }

        public sealed class AnimationConfig
        {
            public string Type { get; set; } = AnimationType.ZoomIn.ToString();
            public string Loop { get; set; } = AnimationLoop.Forward.ToString();
            public string Speed { get; set; } = AnimationSpeed.Normal.ToString();
            public string TranslateDirection { get; set; } = global::lifeviz.MainWindow.TranslateDirection.Right.ToString();
            public string RotationDirection { get; set; } = global::lifeviz.MainWindow.RotationDirection.Clockwise.ToString();
            public double RotationDegrees { get; set; } = AnimationRotateDegrees;
            public double DvdScale { get; set; } = AnimationDvdScale;
            public double BeatShakeIntensity { get; set; } = 1.0;
            public double AudioGranularLowGain { get; set; } = DefaultAudioGranularEqBandGain;
            public double AudioGranularMidGain { get; set; } = DefaultAudioGranularEqBandGain;
            public double AudioGranularHighGain { get; set; } = DefaultAudioGranularEqBandGain;
            public double BeatsPerCycle { get; set; } = 1.0;
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; set; }
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
            VideoSequence,
            Group,
            SimGroup
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

        public static CaptureSource CreateVideoSequence(FileCaptureService.VideoSequenceSession session)
        {
            var source = new CaptureSource(SourceType.VideoSequence, null, null, null, session.DisplayName, null, null)
            {
                AddedUtc = DateTime.UtcNow
            };
            source.SetVideoSequence(session);
            return source;
        }

        public static CaptureSource CreateGroup(string? displayName = null) =>
            new(SourceType.Group, null, null, null, displayName ?? "Layer Group", null, null) { AddedUtc = DateTime.UtcNow };

        public static CaptureSource CreateSimulationGroup(string? displayName = null) =>
            new(SourceType.SimGroup, null, null, null, displayName ?? "Sim Group", null, null) { AddedUtc = DateTime.UtcNow };

        public Guid Id { get; } = Guid.NewGuid();
        public SourceType Type { get; }
        public WindowHandleInfo? Window { get; set; }
        public string? WebcamId { get; }
        public string? FilePath { get; }
        public List<string> FilePaths { get; } = new();
        public FileCaptureService.VideoSequenceSession? VideoSequence { get; private set; }
        public string DisplayName { get; private set; }
        public List<CaptureSource> Children { get; } = new();
        public List<SimulationLayerSpec> SimulationLayers { get; } = new();
        public List<LayerAnimation> Animations { get; } = new();
        public BlendMode BlendMode { get; set; } = BlendMode.Additive;
        public FitMode FitMode { get; set; } = FitMode.Fill;
        public bool Enabled { get; set; } = true;
        public SourceFrame? LastFrame { get; set; }
        public bool HasError { get; set; }
        public int MissedFrames { get; set; }
        public bool FirstFrameReceived { get; set; }
        public DateTime AddedUtc { get; set; }
        public double Opacity { get; set; } = 1.0;
        public bool VideoAudioEnabled { get; set; }
        public double VideoAudioVolume { get; set; } = 1.0;
        public bool VideoPlaybackPaused { get; set; }
        public bool Mirror { get; set; }
        public bool KeyEnabled { get; set; }
        public double KeyTolerance { get; set; } = DefaultKeyTolerance;
        public byte KeyColorR { get; set; }
        public byte KeyColorG { get; set; }
        public byte KeyColorB { get; set; }
        public bool UsePinnedFrameForSmoke { get; set; }
        public bool RetryInitializationAttempted { get; set; }
        public long LastObservedFrameToken { get; set; }

        public bool IsInitialized { get; set; }
        public int? FileWidth { get; private set; }
        public int? FileHeight { get; private set; }
        public byte[]? CompositeDownscaledBuffer { get; set; }

        public void SetDisplayName(string displayName)
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? (Type == SourceType.SimGroup ? "Sim Group" : "Layer Group")
                : displayName.Trim();
        }

        public void SetVideoSequence(FileCaptureService.VideoSequenceSession session)
        {
            VideoSequence = session;
            FilePaths.Clear();
            FilePaths.AddRange(session.Paths);
        }

        public void DisposeVideoSequence()
        {
            VideoSequence?.Dispose();
            VideoSequence = null;
            FilePaths.Clear();
        }

        public bool HasSameVideoSequence(IReadOnlyList<string> paths)
        {
            if (Type != SourceType.VideoSequence || paths.Count != FilePaths.Count)
            {
                return false;
            }

            for (int i = 0; i < paths.Count; i++)
            {
                if (!string.Equals(paths[i], FilePaths[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

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

                if (Type == SourceType.SimGroup)
                {
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

                if (Type == SourceType.VideoSequence && FileWidth.HasValue && FileHeight.HasValue && FileHeight > 0)
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
            SourceType.VideoSequence => FileWidth,
            SourceType.Group => Children.Count > 0 ? Children[0].FallbackWidth : null,
            SourceType.SimGroup => null,
            _ => LastFrame?.SourceWidth
        };

        public int? FallbackHeight => Type switch
        {
            SourceType.Window => Window?.Height,
            SourceType.File => FileHeight,
            SourceType.VideoSequence => FileHeight,
            SourceType.Group => Children.Count > 0 ? Children[0].FallbackHeight : null,
            SourceType.SimGroup => null,
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
        public SourceFrame(byte[] downscaled, int downscaledWidth, int downscaledHeight, byte[]? source, int sourceWidth, int sourceHeight, long frameToken = 0, long framePublishTimestamp = 0)
        {
            Downscaled = downscaled;
            DownscaledWidth = downscaledWidth;
            DownscaledHeight = downscaledHeight;
            Source = source;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            FrameToken = frameToken;
            FramePublishTimestamp = framePublishTimestamp;
        }

        public byte[] Downscaled { get; }
        public int DownscaledWidth { get; }
        public int DownscaledHeight { get; }
        public byte[]? Source { get; }
        public int SourceWidth { get; }
        public int SourceHeight { get; }
        public long FrameToken { get; }
        public long FramePublishTimestamp { get; }
    }

    private sealed class CompositeFrame
    {
        public CompositeFrame(byte[] downscaled, int downscaledWidth, int downscaledHeight, GpuCompositeSurface? gpuSurface = null)
        {
            Downscaled = downscaled;
            DownscaledWidth = downscaledWidth;
            DownscaledHeight = downscaledHeight;
            GpuSurface = gpuSurface;
        }

        public byte[] Downscaled { get; }
        public int DownscaledWidth { get; }
        public int DownscaledHeight { get; }
        public GpuCompositeSurface? GpuSurface { get; }
    }
}
