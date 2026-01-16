using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Buffers;
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
using System.Windows.Shell;
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
    private const double DefaultAnimationBpm = 140;
    private const double AnimationZoomScale = 0.2;
    private const double AnimationTranslateFactor = 0.1;
    private const double AnimationRotateDegrees = 12;
    private const double AnimationDvdScale = 0.2;
    private const double AnimationDvdCycleBeats = 4.0;
    private const double AnimationDvdAspectFactor = 1.3;

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
            StopRecording(showMessage: false);
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
        TryRecordFrame(requiredLength);
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

        double lifeOpacity = Math.Clamp(_lifeOpacity, 0, 1);
        for (int row = 0; row < sourceHeight; row++)
        {
            int srcRowOffset = row * displayStride;
            int destRowOffset = row * sourceStride;
            for (int col = 0; col < sourceWidth; col++)
            {
                int srcIndex = srcRowOffset + (col * 4);
                int destIndex = destRowOffset + (col * 4);
                targetBuffer[destIndex] = ClampToByte((int)(_pixelBuffer[srcIndex] * lifeOpacity));
                targetBuffer[destIndex + 1] = ClampToByte((int)(_pixelBuffer[srcIndex + 1] * lifeOpacity));
                targetBuffer[destIndex + 2] = ClampToByte((int)(_pixelBuffer[srcIndex + 2] * lifeOpacity));
                targetBuffer[destIndex + 3] = 255;
            }
        }

        var composite = _lastCompositeFrame;
        if (!_passthroughEnabled || composite == null)
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
        if (_preserveResolution && composite.HighRes is { Length: > 0 } highRes)
        {
            if ((composite.HighResWidth == sourceWidth && composite.HighResHeight == sourceHeight) ||
                (composite.HighResWidth == displayWidth && composite.HighResHeight == displayHeight))
            {
                overlay = highRes;
                overlayWidth = composite.HighResWidth;
                overlayHeight = composite.HighResHeight;
            }
        }

        if (overlay == null)
        {
            if ((composite.DownscaledWidth == sourceWidth && composite.DownscaledHeight == sourceHeight) ||
                (composite.DownscaledWidth == displayWidth && composite.DownscaledHeight == displayHeight))
            {
                overlay = composite.Downscaled;
                overlayWidth = composite.DownscaledWidth;
                overlayHeight = composite.DownscaledHeight;
            }
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
            for (int col = 0; col < sourceWidth; col++)
            {
                int destIndex = destRowOffset + (col * 4);
                int overlayIndex = overlayRowOffset + (col * 4);
                BlendInto(targetBuffer, destIndex, overlayBuffer[overlayIndex], overlayBuffer[overlayIndex + 1], overlayBuffer[overlayIndex + 2], _blendMode, 1.0);
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
        _engine.Randomize();
        RenderFrame();
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

    private async void RootContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        PopulateAudioMenu();
        await PopulateSourcesMenuAsync();

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
        if (AnimationBpmSlider != null && AnimationBpmValueText != null)
        {
            AnimationBpmSlider.Value = _animationBpm;
            AnimationBpmValueText.Text = $"{_animationBpm:F0}";
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
        sourceItem.Items.Add(animationsMenu);
        if (renameItem != null)
        {
            sourceItem.Items.Add(renameItem);
        }
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
            CaptureSource.SourceType.VideoSequence => $"{prefix}Video Sequence: {source.DisplayName}",
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

        if (Enum.TryParse<BlendMode>(header, ignoreCase: true, out var mode))
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
    }

    private void AddLayerGroup(List<CaptureSource> targetList)
    {
        targetList.Add(CaptureSource.CreateGroup());
        Logger.Info("Inserted new layer group.");
        UpdatePrimaryAspectIfNeeded();
        RenderFrame();
        SaveConfig();
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

        double animationTime = _lifetimeStopwatch.Elapsed.TotalSeconds;
        bool removedAny = CaptureSourceList(_sources, animationTime);
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

        var composite = BuildCompositeFrame(_sources, ref _compositeDownscaledBuffer, ref _compositeHighResBuffer, useEngineDimensions: true, animationTime);
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

    private bool CaptureSourceList(List<CaptureSource> sources, double animationTime)
    {
        bool removedAny = false;
        var removed = new List<CaptureSource>();

        foreach (var source in sources.ToList())
        {
            if (source.Type == CaptureSource.SourceType.Group)
            {
                if (source.Children.Count > 0)
                {
                    removedAny |= CaptureSourceList(source.Children, animationTime);
                }

                var groupDownscaled = source.CompositeDownscaledBuffer;
                var groupHighRes = source.CompositeHighResBuffer;
                var groupComposite = BuildCompositeFrame(source.Children, ref groupDownscaled, ref groupHighRes, useEngineDimensions: false, animationTime);
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
                else if (source.Type == CaptureSource.SourceType.VideoSequence && source.VideoSequence != null)
                {
                    var sequenceFrame = source.VideoSequence.CaptureFrame(_engine.Columns, _engine.Rows, source.FitMode, _preserveResolution);
                    if (sequenceFrame.HasValue)
                    {
                        var value = sequenceFrame.Value;
                        frame = new SourceFrame(value.OverlayDownscaled, value.DownscaledWidth, value.DownscaledHeight,
                            value.OverlaySource, value.SourceWidth, value.SourceHeight);
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

    private void AnimationBpmSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _animationBpm = Math.Clamp(e.NewValue, 10, 300);
        if (AnimationBpmValueText != null)
        {
            AnimationBpmValueText.Text = $"{_animationBpm:F0}";
        }
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

    private enum AnimationType
    {
        ZoomIn,
        Translate,
        Rotate,
        DvdBounce,
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
        public AnimationType Type { get; set; } = AnimationType.ZoomIn;
        public AnimationLoop Loop { get; set; } = AnimationLoop.Forward;
        public AnimationSpeed Speed { get; set; } = AnimationSpeed.Normal;
        public TranslateDirection TranslateDirection { get; set; } = TranslateDirection.Right;
        public RotationDirection RotationDirection { get; set; } = RotationDirection.Clockwise;
        public double DvdScale { get; set; } = AnimationDvdScale;
        public double BeatsPerCycle { get; set; } = 1.0;
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

    private CompositeFrame? BuildCompositeFrame(List<CaptureSource> sources, ref byte[]? downscaledBuffer, ref byte[]? highResBuffer, bool useEngineDimensions, double animationTime)
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

            var downscaledTransform = BuildAnimationTransform(source, downscaledWidth, downscaledHeight, animationTime);
            double animationOpacity = BuildAnimationOpacity(source, animationTime);
            double effectiveOpacity = Math.Clamp(source.Opacity * animationOpacity, 0.0, 1.0);
            if (!primedDownscaled)
            {
                CopyIntoBuffer(downscaledBuffer, downscaledWidth, downscaledHeight,
                    frame.Downscaled, frame.DownscaledWidth, frame.DownscaledHeight, effectiveOpacity,
                    source.Mirror && source.Type == CaptureSource.SourceType.Webcam, source.FitMode, downscaledTransform);
                primedDownscaled = true;
                wroteDownscaled = true;
            }
            else
            {
                CompositeIntoBuffer(downscaledBuffer, downscaledWidth, downscaledHeight,
                    frame.Downscaled, frame.DownscaledWidth, frame.DownscaledHeight, source.BlendMode, effectiveOpacity,
                    source.Mirror && source.Type == CaptureSource.SourceType.Webcam, source.FitMode, downscaledTransform);
                wroteDownscaled = true;
            }

            if (highRes != null && frame.Source != null)
            {
                var sourceBuffer = frame.Source;
                int sourceWidth = frame.SourceWidth;
                int sourceHeight = frame.SourceHeight;
                var highResTransform = BuildAnimationTransform(source, targetWidth, targetHeight, animationTime);

                if (!primedHighRes)
                {
                    CopyIntoBuffer(highRes, targetWidth, targetHeight, sourceBuffer, sourceWidth, sourceHeight, effectiveOpacity,
                        source.Mirror && source.Type == CaptureSource.SourceType.Webcam, source.FitMode, highResTransform);
                    primedHighRes = true;
                    wroteHighRes = true;
                }
                else
                {
                    CompositeIntoBuffer(highRes, targetWidth, targetHeight, sourceBuffer, sourceWidth, sourceHeight,
                        source.BlendMode, effectiveOpacity, source.Mirror && source.Type == CaptureSource.SourceType.Webcam, source.FitMode, highResTransform);
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

    private Transform2D BuildAnimationTransform(CaptureSource source, int destWidth, int destHeight, double timeSeconds)
    {
        if (source.Animations.Count == 0 || destWidth <= 0 || destHeight <= 0)
        {
            return Transform2D.Identity;
        }

        double bpm = _animationBpm > 0 ? _animationBpm : DefaultAnimationBpm;
        double beatDuration = 60.0 / Math.Max(1.0, bpm);
        if (beatDuration <= 0)
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
            double cycle = beatDuration * beatsPerCycle / Math.Max(tempoMultiplier, 0.000001);
            if (cycle <= 0.000001)
            {
                continue;
            }

            double phase = (timeSeconds % cycle) / cycle;
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
                    double angle = DegreesToRadians(AnimationRotateDegrees * progress);
                    if (animation.RotationDirection == RotationDirection.CounterClockwise)
                    {
                        angle = -angle;
                    }
                    animTransform = CreateRotation(angle, centerX, centerY);
                    break;
                }
                case AnimationType.DvdBounce:
                {
                    double baseCycle = beatDuration * AnimationDvdCycleBeats * beatsPerCycle / Math.Max(tempoMultiplier, 0.000001);
                    if (baseCycle <= 0.000001)
                    {
                        break;
                    }

                    double cycleX = baseCycle;
                    double cycleY = baseCycle * AnimationDvdAspectFactor;
                    double phaseX = (timeSeconds % cycleX) / cycleX;
                    double phaseY = (timeSeconds % cycleY) / cycleY;
                    double progressX = GetLoopProgress(phaseX, animation.Loop);
                    double progressY = GetLoopProgress(phaseY, animation.Loop);

                    double scale = Math.Clamp(animation.DvdScale, 0.01, 1.0);
                    double maxX = Math.Max(0, destWidth - (destWidth * scale));
                    double maxY = Math.Max(0, destHeight - (destHeight * scale));
                    double posX = maxX * progressX;
                    double posY = maxY * progressY;

                    var scaleTransform = CreateScaleAtOrigin(scale);
                    var translateTransform = CreateTranslation(posX, posY);
                    animTransform = Transform2D.Multiply(translateTransform, scaleTransform);
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

        double bpm = _animationBpm > 0 ? _animationBpm : DefaultAnimationBpm;
        double beatDuration = 60.0 / Math.Max(1.0, bpm);
        if (beatDuration <= 0)
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
            double cycle = beatDuration * beatsPerCycle / Math.Max(tempoMultiplier, 0.000001);
            if (cycle <= 0.000001)
            {
                continue;
            }

            double phase = (timeSeconds % cycle) / cycle;
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

    private void CopyIntoBuffer(byte[] destination, int destWidth, int destHeight, byte[] source, int sourceWidth, int sourceHeight,
        double opacity, bool mirror, FitMode fitMode, Transform2D transform)
    {
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        int destStride = destWidth * 4;
        int sourceStride = sourceWidth * 4;
        var mapping = ImageFit.GetMapping(fitMode, sourceWidth, sourceHeight, destWidth, destHeight);
        bool useTransform = !transform.IsIdentity;

        Parallel.For(0, destHeight, row =>
        {
            int destRowOffset = row * destStride;
            for (int col = 0; col < destWidth; col++)
            {
                int destIndex = destRowOffset + (col * 4);
                byte sb = 0;
                byte sg = 0;
                byte sr = 0;
                bool mapped;
                int srcX;
                int srcY;
                if (useTransform)
                {
                    transform.TransformPoint(col, row, out double tx, out double ty);
                    mapped = ImageFit.TryMapPixel(mapping, tx, ty, out srcX, out srcY);
                }
                else
                {
                    mapped = ImageFit.TryMapPixel(mapping, col, row, out srcX, out srcY);
                }

                if (mapped)
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

    private void CompositeIntoBuffer(byte[] destination, int destWidth, int destHeight, byte[] source, int sourceWidth, int sourceHeight,
        BlendMode mode, double opacity, bool mirror, FitMode fitMode, Transform2D transform)
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

        if (destWidth == sourceWidth && destHeight == sourceHeight && transform.IsIdentity)
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
        bool useTransform = !transform.IsIdentity;
        Parallel.For(0, destHeight, row =>
        {
            int destRowOffset = row * destStride;
            for (int col = 0; col < destWidth; col++)
            {
                int destIndex = destRowOffset + (col * 4);
                byte sb = 0;
                byte sg = 0;
                byte sr = 0;
                bool mapped;
                int srcX;
                int srcY;
                if (useTransform)
                {
                    transform.TransformPoint(col, row, out double tx, out double ty);
                    mapped = ImageFit.TryMapPixel(mapping, tx, ty, out srcX, out srcY);
                }
                else
                {
                    mapped = ImageFit.TryMapPixel(mapping, col, row, out srcX, out srcY);
                }

                if (mapped)
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
            if (config.AnimationBpm > 0)
            {
                _animationBpm = Math.Clamp(config.AnimationBpm, 10, 300);
            }
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
                AnimationBpm = _animationBpm,
                RecordingQuality = _recordingQuality.ToString(),
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
                Mirror = source.Mirror,
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

            configs.Add(config);
        }

        return configs;
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
                DvdScale = animation.DvdScale,
                BeatsPerCycle = animation.BeatsPerCycle
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
                if (animationConfig.DvdScale > 0)
                {
                    animation.DvdScale = Math.Clamp(animationConfig.DvdScale, 0.01, 1.0);
                }
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
        public bool PreserveResolution { get; set; }
        public double InjectionNoise { get; set; } = 0.0;
        public double LifeOpacity { get; set; } = 1.0;
        public bool InvertComposite { get; set; }
        public bool ShowFps { get; set; }
        public double AnimationBpm { get; set; } = DefaultAnimationBpm;
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
            public List<string> FilePaths { get; set; } = new();
            public string? DisplayName { get; set; }
            public string BlendMode { get; set; } = MainWindow.BlendMode.Normal.ToString();
            public string FitMode { get; set; } = lifeviz.FitMode.Fit.ToString();
            public double Opacity { get; set; } = 1.0;
            public bool Mirror { get; set; }
            public List<AnimationConfig> Animations { get; set; } = new();
            public List<SourceConfig> Children { get; set; } = new();
        }

        public sealed class AnimationConfig
        {
            public string Type { get; set; } = AnimationType.ZoomIn.ToString();
            public string Loop { get; set; } = AnimationLoop.Forward.ToString();
            public string Speed { get; set; } = AnimationSpeed.Normal.ToString();
            public string TranslateDirection { get; set; } = global::lifeviz.MainWindow.TranslateDirection.Right.ToString();
            public string RotationDirection { get; set; } = global::lifeviz.MainWindow.RotationDirection.Clockwise.ToString();
            public double DvdScale { get; set; } = AnimationDvdScale;
            public double BeatsPerCycle { get; set; } = 1.0;
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
            VideoSequence,
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

        public SourceType Type { get; }
        public WindowHandleInfo? Window { get; set; }
        public string? WebcamId { get; }
        public string? FilePath { get; }
        public List<string> FilePaths { get; } = new();
        public FileCaptureService.VideoSequenceSession? VideoSequence { get; private set; }
        public string DisplayName { get; private set; }
        public List<CaptureSource> Children { get; } = new();
        public List<LayerAnimation> Animations { get; } = new();
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

        public void SetDisplayName(string displayName)
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Layer Group" : displayName.Trim();
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
            _ => LastFrame?.SourceWidth
        };

        public int? FallbackHeight => Type switch
        {
            SourceType.Window => Window?.Height,
            SourceType.File => FileHeight,
            SourceType.VideoSequence => FileHeight,
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
