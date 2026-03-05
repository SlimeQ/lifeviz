using System;
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
    private const int DefaultAudioReactiveSeedsPerBeat = 2;
    private const double DefaultAudioReactiveSeedCooldownMs = 180.0;
    private const int MaxAudioReactiveSeedBurstsPerStep = 64;
    private const int MaxSimulationStepsPerRender = 8;
    private const double MaxRgbHueShiftSpeedDegreesPerSecond = 180.0;
    private const double MaxColorDistance = 441.6729559300637;
    private const string GitHubRepoOwner = "SlimeQ";
    private const string GitHubRepoName = "lifeviz";
    private const string GitHubReleaseAssetName = "lifeviz_installer.exe";
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

    private readonly GameOfLifeEngine _engine = new();
    private readonly List<SimulationLayerState> _simulationLayers = new();
    private readonly WindowCaptureService _windowCapture = new();
    private readonly WebcamCaptureService _webcamCapture = new();
    private readonly FileCaptureService _fileCapture = new();
    private readonly AudioBeatDetector _audioBeatDetector = new();
    private readonly BlendEffect _blendEffect = new();
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
    private WriteableBitmap? _bitmap;
    private byte[]? _pixelBuffer;
    private WriteableBitmap? _underlayBitmap;
    private ImageBrush? _overlayBrush;
    private ImageBrush? _inputBrush;
    private byte[]? _compositeDownscaledBuffer;
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
    private bool _passthroughCompositedInPixelBuffer;
    private BlendMode _blendMode = BlendMode.Additive;
    private double _lifeOpacity = 1.0;
    private double _rgbHueShiftDegrees;
    private double _rgbHueShiftSpeedDegreesPerSecond;
    private bool _suppressRgbHueShiftControlEvents;
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
    private bool _animationAudioSyncEnabled;
    private bool _sourceAudioMasterEnabled = true;
    private double _sourceAudioMasterVolume = 1.0;
    private string? _selectedAudioDeviceId;
    private double _oscillationBpm = 140;
    private double _oscillationMinFps = 30;
    private double _oscillationMaxFps = 60;
    private bool _updateInProgress;
    private double _smoothedEnergy;
    private double _smoothedBass;
    private double _smoothedFreq;
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

    public MainWindow()
    {
        Logger.Initialize();
        InitializeComponent();
        ApplyAudioInputGainForSelection();

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
        EnsureSimulationLayersInitialized();
        _currentAspectRatio = _aspectRatioLocked
            ? _lockedAspectRatio
            : (_sources.Count > 0 ? _sources[0].AspectRatio : DefaultAspectRatio);
        ConfigureSimulationLayerEngines(_configuredRows, _configuredDepth, _currentAspectRatio, randomize: true);
        _configuredRows = GetReferenceSimulationEngine().Rows;
        SnapWindowToAspect(preserveHeight: true);
        _effectiveLifeOpacity = _lifeOpacity;
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

        // Update smoothed audio values
        double audioLerp = Math.Min(dt * 15.0, 1.0);
        double energyTarget = Math.Clamp(_audioBeatDetector.TransientEnergy, 0, 1);
        double energyAttackLerp = Math.Min(dt * 45.0, 1.0);
        double energyReleaseLerp = Math.Min(dt * 70.0, 1.0);
        double energyLerp = energyTarget > _smoothedEnergy ? energyAttackLerp : energyReleaseLerp;
        _smoothedEnergy = _smoothedEnergy + (energyTarget - _smoothedEnergy) * energyLerp;
        if (_smoothedEnergy < 0.001)
        {
            _smoothedEnergy = 0;
        }
        _smoothedBass = _smoothedBass + (Math.Clamp(_audioBeatDetector.BassEnergy, 0, 100) - _smoothedBass) * audioLerp;
        _smoothedFreq = _smoothedFreq + (Math.Clamp(_audioBeatDetector.MainFrequency, 0, 5000) - _smoothedFreq) * audioLerp;
        ApplyAudioReactiveFps();
        ApplyAudioReactiveLifeOpacity();
        _audioReactiveLevelSeedBurstsLastStep = 0;
        _audioReactiveBeatSeedBurstsLastStep = 0;

        double effectiveStepFps = Math.Max(_currentFps, 0);
        if (effectiveStepFps < 0.01)
        {
            if (!_isPaused)
            {
                _audioReactiveLevelSeedBurstsLastStep = ApplyAudioReactiveLevelSeeding(1);
            }
            _timeSinceLastStep = 0;
        }
        else
        {
            double desiredInterval = 1.0 / effectiveStepFps;
            if (_timeSinceLastStep >= desiredInterval)
            {
                if (!_isPaused)
                {
                    int stepsToRun = (int)Math.Floor(_timeSinceLastStep / desiredInterval);
                    stepsToRun = Math.Clamp(stepsToRun, 1, MaxSimulationStepsPerRender);
                    InjectCaptureFrames();
                    _audioReactiveBeatSeedBurstsLastStep = ApplyAudioReactiveBeatSeeding();
                    _audioReactiveLevelSeedBurstsLastStep = ApplyAudioReactiveLevelSeeding(stepsToRun);

                    for (int i = 0; i < stepsToRun; i++)
                    {
                        bool stepped = false;
                        foreach (var layer in _simulationLayers)
                        {
                            if (!layer.Enabled)
                            {
                                continue;
                            }

                            layer.Engine.Step();
                            stepped = true;
                        }
                        if (stepped)
                        {
                            _simulationFrames++;
                        }
                    }

                    _timeSinceLastStep -= stepsToRun * desiredInterval;
                    if (stepsToRun >= MaxSimulationStepsPerRender &&
                        _timeSinceLastStep > desiredInterval * MaxSimulationStepsPerRender)
                    {
                        // Drop excessive backlog if render stalls to avoid unbounded catch-up bursts.
                        _timeSinceLastStep = desiredInterval;
                    }
                }
                else
                {
                    _timeSinceLastStep = Math.Min(_timeSinceLastStep, desiredInterval);
                }
            }
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

        var referenceEngine = GetReferenceSimulationEngine();
        int engineRows = referenceEngine.Rows;
        int engineCols = referenceEngine.Columns;
        BuildMappings(width, height, engineCols, engineRows);

        var composite = _lastCompositeFrame;
        byte[]? passthroughBuffer = null;
        bool compositePassthroughInPixelBuffer = false;
        if (_passthroughEnabled &&
            composite != null &&
            composite.DownscaledWidth == engineCols &&
            composite.DownscaledHeight == engineRows &&
            composite.Downscaled.Length >= engineCols * engineRows * 4)
        {
            // Passthrough is composited first so each simulation layer blends on top
            // using its own blend mode (additive/subtractive/...) against the scene.
            passthroughBuffer = composite.Downscaled;
            compositePassthroughInPixelBuffer = true;
        }

        EnsureEngineColorBuffers();
        var activeLayers = _simulationLayers
            .Where(layer => layer.Enabled && layer.ColorBuffer != null)
            .ToArray();
        bool hasEnabledSubtractiveSimulationLayer = activeLayers.Any(layer => layer.BlendMode == BlendMode.Subtractive);
        bool hasEnabledNonSubtractiveSimulationLayer = activeLayers.Any(layer => layer.BlendMode != BlendMode.Subtractive);

        Parallel.For(0, height, row =>
        {
            int sourceRow = _rowMap[row];
            for (int col = 0; col < width; col++)
            {
                int sourceCol = _colMap[col];
                int sourceIndex = (sourceRow * engineCols + sourceCol) * 4;
                int index = (row * stride) + (col * 4);

                int baseB;
                int baseG;
                int baseR;
                if (compositePassthroughInPixelBuffer && passthroughBuffer != null)
                {
                    baseB = passthroughBuffer[sourceIndex];
                    baseG = passthroughBuffer[sourceIndex + 1];
                    baseR = passthroughBuffer[sourceIndex + 2];
                }
                else
                {
                    // With passthrough off, only subtractive-only stacks use white identity.
                    // Mixed additive/subtractive stacks stay on black baseline.
                    int baseline = (hasEnabledSubtractiveSimulationLayer && !hasEnabledNonSubtractiveSimulationLayer) ? 255 : 0;
                    baseB = baseline;
                    baseG = baseline;
                    baseR = baseline;
                }

                int outB = baseB;
                int outG = baseG;
                int outR = baseR;

                foreach (var layer in activeLayers)
                {
                    byte[]? colorBuffer = layer.ColorBuffer;
                    if (colorBuffer == null)
                    {
                        continue;
                    }

                    byte lr = colorBuffer[sourceIndex];
                    byte lg = colorBuffer[sourceIndex + 1];
                    byte lb = colorBuffer[sourceIndex + 2];
                    BlendSimulationLayerInto(ref outB, ref outG, ref outR, lr, lg, lb, layer.BlendMode);
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

        double lifeOpacity = Math.Clamp(_effectiveLifeOpacity, 0, 1);
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
        foreach (var layer in _simulationLayers)
        {
            layer.Engine.Randomize();
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
        try
        {
            if (_layerEditorWindow == null)
            {
                _layerEditorWindow = new LayerEditorWindow(this);
                _layerEditorWindow.Closed += (_, _) => _layerEditorWindow = null;
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

        UpdateUpdateMenuItem();
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

    internal void GetSimulationLayerSettingsForEditor(out IReadOnlyList<LayerEditorSimulationLayer> simulationLayers)
    {
        EnsureSimulationLayersInitialized();
        simulationLayers = _simulationLayers
            .Select(ToEditorSimulationLayer)
            .ToList();
    }

    internal void ApplySimulationLayerSettingsFromEditor(IReadOnlyList<LayerEditorSimulationLayer>? simulationLayers)
    {
        RunWithoutLayerEditorRefresh(() =>
        {
            EnsureSimulationLayersInitialized();
            var specs = NormalizeSimulationLayerSpecs(simulationLayers);
            if (!ApplySimulationLayerSpecs(specs))
            {
                return;
            }

            _pulseStep = 0;
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

        foreach (var source in _sources.ToList())
        {
            CleanupSource(source);
        }

        _fileCapture.Clear();
        
        _sources.Clear();
        _lastCompositeFrame = null;
        _passthroughEnabled = false;
        
        Logger.Info("Cleared all sources; reset webcam capture.");
        if (PassthroughMenuItem != null)
        {
            PassthroughMenuItem.IsChecked = false;
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

        var composite = BuildCompositeFrame(_sources, ref _compositeDownscaledBuffer, useEngineDimensions: true, animationTime);
        if (composite == null)
        {
            _lastCompositeFrame = null;
            return;
        }

        _lastCompositeFrame = composite;
        UpdateDisplaySurface();

        bool injectedAnyLayer = false;
        foreach (var layer in _simulationLayers)
        {
            if (!layer.Enabled)
            {
                continue;
            }

            bool invertInput = layer.InputFunction == SimulationInputFunction.Inverse;
            if (_lifeMode == GameOfLifeEngine.LifeMode.NaiveGrayscale)
            {
                var grayMask = BuildLuminanceMask(
                    composite.Downscaled,
                    composite.DownscaledWidth,
                    composite.DownscaledHeight,
                    _captureThresholdMin,
                    _captureThresholdMax,
                    _invertThreshold,
                    _injectionMode,
                    _injectionNoise,
                    layer.Engine.Depth,
                    _pulseStep,
                    invertInput);
                layer.Engine.InjectFrame(grayMask);
                injectedAnyLayer = true;
            }
            else
            {
                var (rMask, gMask, bMask) = BuildChannelMasks(
                    composite.Downscaled,
                    composite.DownscaledWidth,
                    composite.DownscaledHeight,
                    _captureThresholdMin,
                    _captureThresholdMax,
                    _invertThreshold,
                    _injectionMode,
                    _injectionNoise,
                    layer.Engine.RDepth,
                    layer.Engine.GDepth,
                    layer.Engine.BDepth,
                    _pulseStep,
                    invertInput);
                layer.Engine.InjectRgbFrame(rMask, gMask, bMask);
                injectedAnyLayer = true;
            }
        }

        if (injectedAnyLayer)
        {
            _pulseStep++;
        }
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
        _currentFps = Math.Clamp(_currentFps * mappedMultiplier, 0, 144);
    }

    private void ApplyAudioReactiveLifeOpacity()
    {
        if (!_audioReactiveEnabled || !_audioReactiveLevelToLifeOpacityEnabled || string.IsNullOrWhiteSpace(_selectedAudioDeviceId))
        {
            _effectiveLifeOpacity = _lifeOpacity;
            return;
        }

        double gainScale = _audioReactiveEnergyGain / DefaultAudioReactiveEnergyGain;
        double normalizedLevel = Math.Clamp(_smoothedEnergy * gainScale, 0, 1);
        double shapedLevel = Math.Pow(normalizedLevel, 0.8);
        double minScalar = Math.Clamp(_audioReactiveLifeOpacityMinScalar, 0, 1);
        double scalar = minScalar + ((1.0 - minScalar) * shapedLevel);
        _effectiveLifeOpacity = Math.Clamp(_lifeOpacity * scalar, 0, 1);
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

        foreach (var layer in _simulationLayers)
        {
            if (!layer.Enabled)
            {
                continue;
            }

            layer.Engine.InjectFrame(mask);
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
            if (source.Type == CaptureSource.SourceType.Group)
            {
                if (source.Children.Count > 0)
                {
                    removedAny |= CaptureSourceList(source.Children, animationTime);
                }

                var groupDownscaled = source.CompositeDownscaledBuffer;
                var groupComposite = BuildCompositeFrame(source.Children, ref groupDownscaled, useEngineDimensions: false, animationTime);
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
                    var windowFrame = _windowCapture.CaptureFrame(source.Window, referenceEngine.Columns, referenceEngine.Rows, source.FitMode, includeSource: false);
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
                    var webcamFrame = _webcamCapture.CaptureFrame(source.WebcamId, referenceEngine.Columns, referenceEngine.Rows, source.FitMode, includeSource: false);
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
                    var sequenceFrame = source.VideoSequence.CaptureFrame(referenceEngine.Columns, referenceEngine.Rows, source.FitMode, includeSource: false);
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
                    var referenceEngine = GetReferenceSimulationEngine();
                    var fileFrame = _fileCapture.CaptureFrame(source.FilePath, referenceEngine.Columns, referenceEngine.Rows, source.FitMode, includeSource: false);
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
        _invertThreshold = InvertThresholdCheckBox?.IsChecked == true;
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

    private enum AudioReactiveSeedPattern
    {
        Glider,
        RPentomino,
        RandomBurst
    }

    private sealed class SimulationLayerState
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "Simulation Layer";
        public bool Enabled { get; set; } = true;
        public SimulationInputFunction InputFunction { get; set; } = SimulationInputFunction.Direct;
        public BlendMode BlendMode { get; set; } = BlendMode.Subtractive;
        public GameOfLifeEngine Engine { get; set; } = new();
        public byte[]? ColorBuffer { get; set; }
    }

    private sealed class SimulationLayerSpec
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "Simulation Layer";
        public bool Enabled { get; init; } = true;
        public SimulationInputFunction InputFunction { get; init; } = SimulationInputFunction.Direct;
        public BlendMode BlendMode { get; init; } = BlendMode.Subtractive;
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

    private LayerEditorSimulationLayer ToEditorSimulationLayer(SimulationLayerState layer)
    {
        return new LayerEditorSimulationLayer
        {
            Id = layer.Id,
            Name = layer.Name,
            Enabled = layer.Enabled,
            InputFunction = layer.InputFunction.ToString(),
            BlendMode = layer.BlendMode.ToString()
        };
    }

    private void EnsureSimulationLayersInitialized()
    {
        if (_simulationLayers.Count > 0)
        {
            return;
        }

        var specs = BuildDefaultSimulationLayerSpecs();
        _simulationLayers.Add(new SimulationLayerState
        {
            Id = specs[0].Id,
            Name = specs[0].Name,
            Enabled = specs[0].Enabled,
            InputFunction = specs[0].InputFunction,
            BlendMode = specs[0].BlendMode,
            Engine = _engine
        });
        _simulationLayers.Add(new SimulationLayerState
        {
            Id = specs[1].Id,
            Name = specs[1].Name,
            Enabled = specs[1].Enabled,
            InputFunction = specs[1].InputFunction,
            BlendMode = specs[1].BlendMode,
            Engine = CreateConfiguredSimulationEngine(randomize: true)
        });
        ConfigureSimulationEngine(_engine, _configuredRows, _configuredDepth, _currentAspectRatio, randomize: true);
    }

    private GameOfLifeEngine GetReferenceSimulationEngine()
    {
        if (_simulationLayers.Count > 0)
        {
            return _simulationLayers[0].Engine;
        }

        return _engine;
    }

    private void ConfigureSimulationLayerEngines(int rows, int depth, double aspectRatio, bool randomize)
    {
        EnsureSimulationLayersInitialized();
        foreach (var layer in _simulationLayers)
        {
            ConfigureSimulationEngine(layer.Engine, rows, depth, aspectRatio, randomize);
        }
    }

    private GameOfLifeEngine CreateConfiguredSimulationEngine(bool randomize)
    {
        var engine = new GameOfLifeEngine();
        ConfigureSimulationEngine(engine, _configuredRows, _configuredDepth, _currentAspectRatio, randomize);
        return engine;
    }

    private void ConfigureSimulationEngine(GameOfLifeEngine engine, int rows, int depth, double aspectRatio, bool randomize)
    {
        double resolvedAspect = aspectRatio > 0.01 ? aspectRatio : DefaultAspectRatio;
        engine.Configure(rows, depth, resolvedAspect);
        engine.SetMode(_lifeMode);
        engine.SetBinningMode(_binningMode);
        engine.SetInjectionMode(_injectionMode);
        if (randomize)
        {
            engine.Randomize();
        }
    }

    private static List<SimulationLayerSpec> BuildDefaultSimulationLayerSpecs()
    {
        return new List<SimulationLayerSpec>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Positive",
                Enabled = true,
                InputFunction = SimulationInputFunction.Direct,
                BlendMode = BlendMode.Additive
            },
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Negative",
                Enabled = true,
                InputFunction = SimulationInputFunction.Inverse,
                BlendMode = BlendMode.Subtractive
            }
        };
    }

    private static string NormalizeSimulationLayerName(string? value, int index)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return $"Simulation Layer {index + 1}";
    }

    private static List<SimulationLayerSpec> NormalizeSimulationLayerSpecs(IReadOnlyList<LayerEditorSimulationLayer>? simulationLayers)
    {
        var normalized = new List<SimulationLayerSpec>();
        var seenIds = new HashSet<Guid>();

        if (simulationLayers != null)
        {
            for (int i = 0; i < simulationLayers.Count; i++)
            {
                var layer = simulationLayers[i];
                Guid id = layer.Id;
                if (id == Guid.Empty || !seenIds.Add(id))
                {
                    do
                    {
                        id = Guid.NewGuid();
                    } while (!seenIds.Add(id));
                }

                var inputFunction = ParseSimulationInputFunctionOrDefault(layer.InputFunction, SimulationInputFunction.Direct);
                var defaultBlend = inputFunction == SimulationInputFunction.Inverse ? BlendMode.Subtractive : BlendMode.Additive;
                var blendMode = ParseBlendModeOrDefault(layer.BlendMode, defaultBlend);
                normalized.Add(new SimulationLayerSpec
                {
                    Id = id,
                    Name = NormalizeSimulationLayerName(layer.Name, i),
                    Enabled = layer.Enabled,
                    InputFunction = inputFunction,
                    BlendMode = blendMode
                });
            }
        }

        if (normalized.Count == 0)
        {
            return BuildDefaultSimulationLayerSpecs();
        }

        return normalized;
    }

    private static List<SimulationLayerSpec> NormalizeSimulationLayerSpecs(IReadOnlyList<AppConfig.SimulationLayerConfig>? simulationLayers)
    {
        var normalized = new List<SimulationLayerSpec>();
        var seenIds = new HashSet<Guid>();

        if (simulationLayers != null)
        {
            for (int i = 0; i < simulationLayers.Count; i++)
            {
                var layer = simulationLayers[i];
                Guid id = layer.Id;
                if (id == Guid.Empty || !seenIds.Add(id))
                {
                    do
                    {
                        id = Guid.NewGuid();
                    } while (!seenIds.Add(id));
                }

                var inputFunction = ParseSimulationInputFunctionOrDefault(layer.InputFunction, SimulationInputFunction.Direct);
                var defaultBlend = inputFunction == SimulationInputFunction.Inverse ? BlendMode.Subtractive : BlendMode.Additive;
                var blendMode = ParseBlendModeOrDefault(layer.BlendMode, defaultBlend);
                normalized.Add(new SimulationLayerSpec
                {
                    Id = id,
                    Name = NormalizeSimulationLayerName(layer.Name, i),
                    Enabled = layer.Enabled,
                    InputFunction = inputFunction,
                    BlendMode = blendMode
                });
            }
        }

        if (normalized.Count == 0)
        {
            return BuildDefaultSimulationLayerSpecs();
        }

        return normalized;
    }

    private static List<SimulationLayerSpec> BuildLegacySimulationLayerSpecs(
        bool positiveLayerEnabled,
        string? positiveLayerBlendMode,
        bool negativeLayerEnabled,
        string? negativeLayerBlendMode,
        IReadOnlyList<string>? simulationLayerOrder)
    {
        var positive = new SimulationLayerSpec
        {
            Id = Guid.NewGuid(),
            Name = "Positive",
            Enabled = positiveLayerEnabled,
            InputFunction = SimulationInputFunction.Direct,
            BlendMode = ParseBlendModeOrDefault(positiveLayerBlendMode, BlendMode.Additive)
        };
        var negative = new SimulationLayerSpec
        {
            Id = Guid.NewGuid(),
            Name = "Negative",
            Enabled = negativeLayerEnabled,
            InputFunction = SimulationInputFunction.Inverse,
            BlendMode = ParseBlendModeOrDefault(negativeLayerBlendMode, BlendMode.Subtractive)
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
        var existingById = _simulationLayers.ToDictionary(layer => layer.Id);
        var nextLayers = new List<SimulationLayerState>(specs.Count);
        bool changed = _simulationLayers.Count != specs.Count;

        for (int index = 0; index < specs.Count; index++)
        {
            var spec = specs[index];
            SimulationLayerState layer;
            if (!existingById.TryGetValue(spec.Id, out layer!))
            {
                GameOfLifeEngine engine;
                if (_simulationLayers.Count == 0 && index == 0)
                {
                    engine = _engine;
                    ConfigureSimulationEngine(engine, _configuredRows, _configuredDepth, _currentAspectRatio, randomize: true);
                }
                else
                {
                    engine = CreateConfiguredSimulationEngine(randomize: true);
                }

                layer = new SimulationLayerState
                {
                    Id = spec.Id,
                    Engine = engine
                };
                changed = true;
            }
            else
            {
                existingById.Remove(spec.Id);
                if (index >= _simulationLayers.Count || _simulationLayers[index].Id != spec.Id)
                {
                    changed = true;
                }
            }

            if (!string.Equals(layer.Name, spec.Name, StringComparison.Ordinal))
            {
                layer.Name = spec.Name;
                changed = true;
            }
            if (layer.Enabled != spec.Enabled)
            {
                layer.Enabled = spec.Enabled;
                changed = true;
            }
            if (layer.InputFunction != spec.InputFunction)
            {
                layer.InputFunction = spec.InputFunction;
                changed = true;
            }
            if (layer.BlendMode != spec.BlendMode)
            {
                layer.BlendMode = spec.BlendMode;
                changed = true;
            }

            nextLayers.Add(layer);
        }

        if (existingById.Count > 0)
        {
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        _simulationLayers.Clear();
        _simulationLayers.AddRange(nextLayers);
        return true;
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
        var referenceEngine = GetReferenceSimulationEngine();
        int targetWidth = referenceEngine.Columns;
        int targetHeight = referenceEngine.Rows;

        if (targetWidth <= 0 || targetHeight <= 0)
        {
            targetWidth = referenceEngine.Columns;
            targetHeight = referenceEngine.Rows;
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

    private static double ComputeKeyAlpha(byte sb, byte sg, byte sr, in KeyingSettings keying)
    {
        if (!keying.Enabled)
        {
            return 1.0;
        }

        int dr = sr - keying.R;
        int dg = sg - keying.G;
        int db = sb - keying.B;
        double distance = Math.Sqrt((dr * dr) + (dg * dg) + (db * db)) / MaxColorDistance;
        double tolerance = Math.Clamp(keying.Tolerance, 0.0, 1.0);
        if (tolerance <= 0.0)
        {
            return distance <= 0.0 ? 0.0 : 1.0;
        }

        return Math.Clamp(distance / tolerance, 0.0, 1.0);
    }

    private CompositeFrame? BuildCompositeFrame(List<CaptureSource> sources, ref byte[]? downscaledBuffer, bool useEngineDimensions, double animationTime)
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

        bool wroteDownscaled = false;
        bool primedDownscaled = false;

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
            var keying = new KeyingSettings(
                source.KeyEnabled && source.BlendMode == BlendMode.Normal,
                source.BlendMode == BlendMode.Normal,
                source.KeyColorR,
                source.KeyColorG,
                source.KeyColorB,
                source.KeyTolerance);
            if (!primedDownscaled)
            {
                CopyIntoBuffer(downscaledBuffer, downscaledWidth, downscaledHeight,
                    frame.Downscaled, frame.DownscaledWidth, frame.DownscaledHeight, effectiveOpacity,
                    source.Mirror && source.Type == CaptureSource.SourceType.Webcam, source.FitMode, downscaledTransform, keying);
                primedDownscaled = true;
                wroteDownscaled = true;
            }
            else
            {
                CompositeIntoBuffer(downscaledBuffer, downscaledWidth, downscaledHeight,
                    frame.Downscaled, frame.DownscaledWidth, frame.DownscaledHeight, source.BlendMode, effectiveOpacity,
                    source.Mirror && source.Type == CaptureSource.SourceType.Webcam, source.FitMode, downscaledTransform, keying);
                wroteDownscaled = true;
            }

        }

        if (!wroteDownscaled)
        {
            return null;
        }

        return new CompositeFrame(downscaledBuffer, downscaledWidth, downscaledHeight);
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
                    double posX = maxX * progressX;
                    double posY = maxY * progressY;

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
                    double energy = Math.Clamp(_smoothedEnergy, 0, 1);
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

    private void CopyIntoBuffer(byte[] destination, int destWidth, int destHeight, byte[] source, int sourceWidth, int sourceHeight,
        double opacity, bool mirror, FitMode fitMode, Transform2D transform, in KeyingSettings keying)
    {
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        int destStride = destWidth * 4;
        int sourceStride = sourceWidth * 4;
        var mapping = ImageFit.GetMapping(fitMode, sourceWidth, sourceHeight, destWidth, destHeight);
        bool useTransform = !transform.IsIdentity;
        var keyingLocal = keying;

        Parallel.For(0, destHeight, row =>
        {
            int destRowOffset = row * destStride;
            for (int col = 0; col < destWidth; col++)
            {
                int destIndex = destRowOffset + (col * 4);
                byte sb = 0;
                byte sg = 0;
                byte sr = 0;
                byte sa = 255;
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
                    sa = source[srcIndex + 3];
                }

                double keyAlpha = ComputeKeyAlpha(sb, sg, sr, keyingLocal);
                double alpha = keyingLocal.UseAlpha ? (sa / 255.0) : 1.0;
                double effectiveOpacity = opacity * keyAlpha * alpha;
                destination[destIndex] = ClampToByte((int)(sb * effectiveOpacity));
                destination[destIndex + 1] = ClampToByte((int)(sg * effectiveOpacity));
                destination[destIndex + 2] = ClampToByte((int)(sr * effectiveOpacity));
                destination[destIndex + 3] = 255;
            }
        });
    }

    private void CompositeIntoBuffer(byte[] destination, int destWidth, int destHeight, byte[] source, int sourceWidth, int sourceHeight,
        BlendMode mode, double opacity, bool mirror, FitMode fitMode, Transform2D transform, in KeyingSettings keying)
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

        var keyingLocal = keying;
        bool applyKeying = keyingLocal.Enabled && mode == BlendMode.Normal;
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
                    byte sa = source[srcIndex + 3];
                    if (applyKeying)
                    {
                        double keyAlpha = ComputeKeyAlpha(sb, sg, sr, keyingLocal);
                        sa = ClampToByte((int)Math.Round(sa * keyAlpha));
                    }
                    BlendInto(destination, destIndex, sb, sg, sr, sa, mode, opacity);
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
                byte sa = 0;
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
                    sa = source[srcIndex + 3];
                }
                if (applyKeying)
                {
                    double keyAlpha = ComputeKeyAlpha(sb, sg, sr, keyingLocal);
                    sa = ClampToByte((int)Math.Round(sa * keyAlpha));
                }
                BlendInto(destination, destIndex, sb, sg, sr, sa, mode, opacity);
            }
        });
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

        // Apply opacity as a lerp between destination and blended result.
        destination[destIndex] = ClampToByte((int)(db + (b - db) * opacity));
        destination[destIndex + 1] = ClampToByte((int)(dg + (g - dg) * opacity));
        destination[destIndex + 2] = ClampToByte((int)(dr + (r - dr) * opacity));
        destination[destIndex + 3] = 255;
    }

    private static void BlendSimulationLayerInto(
        ref int destinationB,
        ref int destinationG,
        ref int destinationR,
        byte sr,
        byte sg,
        byte sb,
        BlendMode mode)
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

        destinationB = b;
        destinationG = g;
        destinationR = r;
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
        foreach (var layer in _simulationLayers)
        {
            if (!layer.Enabled)
            {
                continue;
            }

            EnsureEngineColorBuffer(layer);
        }
    }

    private void EnsureEngineColorBuffer(SimulationLayerState layer)
    {
        var engine = layer.Engine;
        int size = engine.Columns * engine.Rows * 4;
        if (layer.ColorBuffer == null || layer.ColorBuffer.Length != size)
        {
            layer.ColorBuffer = new byte[size];
        }
        byte[] targetBuffer = layer.ColorBuffer;

        double hueShiftDegrees = CurrentRgbHueShiftDegrees();
        bool applyHueShift = _lifeMode == GameOfLifeEngine.LifeMode.RgbChannels && Math.Abs(hueShiftDegrees) > 0.001;
        double rr = 0, rg = 0, rb = 0, gr = 0, gg = 0, gb = 0, br = 0, bg = 0, bb = 0;
        if (applyHueShift)
        {
            BuildHueRotationMatrix(hueShiftDegrees, out rr, out rg, out rb, out gr, out gg, out gb, out br, out bg, out bb);
        }

        Parallel.For(0, engine.Rows, row =>
        {
            int rowOffset = row * engine.Columns * 4;
            for (int col = 0; col < engine.Columns; col++)
            {
                var (r, g, b) = engine.GetColor(row, col);
                if (applyHueShift)
                {
                    int rotatedR = (int)Math.Round((rr * r) + (rg * g) + (rb * b));
                    int rotatedG = (int)Math.Round((gr * r) + (gg * g) + (gb * b));
                    int rotatedB = (int)Math.Round((br * r) + (bg * g) + (bb * b));
                    r = ClampToByte(rotatedR);
                    g = ClampToByte(rotatedG);
                    b = ClampToByte(rotatedB);
                }

                int index = rowOffset + (col * 4);
                targetBuffer[index] = r;
                targetBuffer[index + 1] = g;
                targetBuffer[index + 2] = b;
                targetBuffer[index + 3] = 255;
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

        int width = _underlayBitmap.PixelWidth;
        int height = _underlayBitmap.PixelHeight;
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

        _underlayBitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, stride, 0);
    }

    private void UpdateEffectInput()
    {
        _blendEffect.UseOverlay = ShouldUseShaderPassthrough() ? 1.0 : 0.0;
        var passthroughBlendMode = GetEffectivePassthroughBlendMode();
        _blendEffect.Mode = passthroughBlendMode switch
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
            _inputBrush.Opacity = Math.Clamp(_effectiveLifeOpacity, 0, 1);
        }
    }

    private bool ShouldUseShaderPassthrough()
    {
        return _passthroughEnabled &&
               _lastCompositeFrame != null &&
               !_passthroughCompositedInPixelBuffer;
    }

    private BlendMode GetEffectivePassthroughBlendMode()
    {
        bool hasEnabledSubtractiveSimulationLayer = _simulationLayers.Any(layer =>
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

    private void UpdateFpsOverlay()
    {
        if (FpsText == null)
        {
            return;
        }

        if (_showFps)
        {
            string audioStats;
            if (!string.IsNullOrWhiteSpace(_selectedAudioDeviceId))
            {
                string signalState = _smoothedEnergy >= 0.02 ? "Signal" : "Low/No Signal";
                audioStats = $"\nAudio: {_smoothedEnergy * 100:0}% ({signalState}) | Bass: {_smoothedBass:0.0} | Freq: {_smoothedFreq:0}Hz";
            }
            else
            {
                audioStats = "\nAudio: None (Select Device)";
            }

            string reactiveStats = string.Empty;
            if (_audioReactiveEnabled)
            {
                string deviceState = string.IsNullOrWhiteSpace(_selectedAudioDeviceId) ? "No Device" : "Active";
                reactiveStats = $"\nReactive: {deviceState} | InGain x{_audioInputGain:0.00} | FPS x{_audioReactiveFpsMultiplier:0.00} (min {_audioReactiveFpsMinPercent * 100.0:0}%) | Opacity {_effectiveLifeOpacity:0.00} | Beats: {_audioBeatDetector.BeatCount} | Seeds L:{_audioReactiveLevelSeedBurstsLastStep} B:{_audioReactiveBeatSeedBurstsLastStep}";
            }

            FpsText.Text = $"{_displayFps:0.0} fps (target {_currentFps:0.0}){audioStats}{reactiveStats}";
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
        foreach (var layer in _simulationLayers)
        {
            layer.Engine.SetBinningMode(mode);
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
        if (_injectionMode == mode)
        {
            return;
        }

        _injectionMode = mode;
        foreach (var layer in _simulationLayers)
        {
            layer.Engine.SetInjectionMode(mode);
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

        if (_suppressRgbHueShiftControlEvents)
        {
            return;
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

        if (_suppressRgbHueShiftControlEvents)
        {
            return;
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
        foreach (var layer in _simulationLayers)
        {
            layer.Engine.SetMode(mode);
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

    private double CurrentRgbHueShiftDegrees()
    {
        if (_lifeMode != GameOfLifeEngine.LifeMode.RgbChannels)
        {
            return 0;
        }

        double animatedDegrees = _rgbHueShiftDegrees;
        if (Math.Abs(_rgbHueShiftSpeedDegreesPerSecond) > 0.001)
        {
            animatedDegrees += _lifetimeStopwatch.Elapsed.TotalSeconds * _rgbHueShiftSpeedDegreesPerSecond;
        }

        return NormalizeHueDegrees(animatedDegrees);
    }

    private void UpdateRgbHueShiftControls()
    {
        bool rgbMode = _lifeMode == GameOfLifeEngine.LifeMode.RgbChannels;
        if (RgbHueShiftMenu != null)
        {
            RgbHueShiftMenu.IsEnabled = rgbMode;
        }

        _suppressRgbHueShiftControlEvents = true;
        try
        {
            if (RgbHueShiftSlider != null)
            {
                double normalizedOffset = NormalizeHueDegrees(_rgbHueShiftDegrees);
                if (Math.Abs(RgbHueShiftSlider.Value - normalizedOffset) > 0.001)
                {
                    RgbHueShiftSlider.Value = normalizedOffset;
                }
            }

            if (RgbHueShiftSpeedSlider != null)
            {
                double clampedSpeed = Math.Clamp(_rgbHueShiftSpeedDegreesPerSecond, -MaxRgbHueShiftSpeedDegreesPerSecond, MaxRgbHueShiftSpeedDegreesPerSecond);
                if (Math.Abs(RgbHueShiftSpeedSlider.Value - clampedSpeed) > 0.001)
                {
                    RgbHueShiftSpeedSlider.Value = clampedSpeed;
                }
            }
        }
        finally
        {
            _suppressRgbHueShiftControlEvents = false;
        }

        if (RgbHueShiftValueText != null)
        {
            RgbHueShiftValueText.Text = $"{_rgbHueShiftDegrees:0.#}deg";
        }

        if (RgbHueShiftSpeedValueText != null)
        {
            RgbHueShiftSpeedValueText.Text = $"{_rgbHueShiftSpeedDegreesPerSecond:+0.#;-0.#;0}deg/s";
        }
    }

    private bool[,] BuildLuminanceMask(byte[] buffer, int width, int height, double min, double max, bool invert, GameOfLifeEngine.InjectionMode mode, double noiseProbability, int period, int pulseStep, bool invertInput = false)
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
                if (invertInput)
                {
                    luminance = 1.0 - luminance;
                }
                bool alive = false;
                if (mode == GameOfLifeEngine.InjectionMode.RandomPulse)
                {
                    // Random pulse uses source intensity directly as alive probability.
                    alive = Random.Shared.NextDouble() < luminance;
                }
                else if (mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation)
                {
                    // PWM uses source intensity directly as pulse duty cycle.
                    alive = PulseWidthAlive(luminance, period, pulseStep);
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

    private (bool[,] r, bool[,] g, bool[,] b) BuildChannelMasks(byte[] buffer, int width, int height, double min, double max, bool invert, GameOfLifeEngine.InjectionMode mode, double noiseProbability, int rPeriod, int gPeriod, int bPeriod, int pulseStep, bool invertInput = false)
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

        double hueShiftDegrees = CurrentRgbHueShiftDegrees();
        bool remapToRotatedBins = Math.Abs(hueShiftDegrees) > 0.001;
        double rr = 0, rg = 0, rb = 0, gr = 0, gg = 0, gb = 0, br = 0, bg = 0, bb = 0;
        if (remapToRotatedBins)
        {
            // Convert captured RGB into the rotated bin basis so hue shift changes channel injection behavior.
            BuildHueRotationMatrix(-hueShiftDegrees, out rr, out rg, out rb, out gr, out gg, out gb, out br, out bg, out bb);
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

                bool rAlive = mode == GameOfLifeEngine.InjectionMode.RandomPulse
                    ? randomGate < nr
                    : mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation
                        ? PulseWidthAlive(nr, rPeriod, pulseStep)
                    : EvaluateThresholdValue(nr, min, max, invert);
                bool gAlive = mode == GameOfLifeEngine.InjectionMode.RandomPulse
                    ? randomGate < ng
                    : mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation
                        ? PulseWidthAlive(ng, gPeriod, pulseStep)
                    : EvaluateThresholdValue(ng, min, max, invert);
                bool bAlive = mode == GameOfLifeEngine.InjectionMode.RandomPulse
                    ? randomGate < nb
                    : mode == GameOfLifeEngine.InjectionMode.PulseWidthModulation
                        ? PulseWidthAlive(nb, bPeriod, pulseStep)
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
        _audioInputGain = Math.Clamp(e.NewValue, 0.25, 64);
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
            _audioInputGainCapture = config.AudioInputGainCapture > 0
                ? Math.Clamp(config.AudioInputGainCapture, 0.25, 64)
                : Math.Clamp(config.AudioInputGain, 0.25, 64);
            _audioInputGainRender = config.AudioInputGainRender > 0
                ? Math.Clamp(config.AudioInputGainRender, 0.25, 64)
                : DefaultAudioOutputGain;
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
            _fileCapture.SetMasterVideoAudioEnabled(_sourceAudioMasterEnabled);
            _fileCapture.SetMasterVideoAudioVolume(_sourceAudioMasterVolume);
            ApplyAudioInputGainForSelection();
            _aspectRatioLocked = config.AspectRatioLocked;
            _lockedAspectRatio = config.LockedAspectRatio > 0 ? config.LockedAspectRatio : DefaultAspectRatio;

            if (!string.IsNullOrWhiteSpace(_selectedAudioDeviceId))
            {
                 _ = _audioBeatDetector.InitializeAsync(_selectedAudioDeviceId);
            }
            _lastAudioReactiveBeatCount = _audioBeatDetector.BeatCount;

            _blendMode = ParseBlendModeOrDefault(config.BlendMode, _blendMode);
            var simulationSpecs = (config.SimulationLayers != null && config.SimulationLayers.Count > 0)
                ? NormalizeSimulationLayerSpecs(config.SimulationLayers)
                : BuildLegacySimulationLayerSpecs(
                    config.PositiveLayerEnabled,
                    config.PositiveLayerBlendMode,
                    config.NegativeLayerEnabled,
                    config.NegativeLayerBlendMode,
                    config.SimulationLayerOrder);
            ApplySimulationLayerSpecs(simulationSpecs);

            _pendingFullscreen = config.Fullscreen;
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
            EnsureSimulationLayersInitialized();
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
                RgbHueShiftDegrees = _rgbHueShiftDegrees,
                RgbHueShiftSpeedDegreesPerSecond = _rgbHueShiftSpeedDegreesPerSecond,
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
                BlendMode = _blendMode.ToString(),
                SimulationLayers = BuildSimulationLayerConfigs(),
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
        return _simulationLayers.Select(layer => new AppConfig.SimulationLayerConfig
        {
            Id = layer.Id,
            Name = layer.Name,
            Enabled = layer.Enabled,
            InputFunction = layer.InputFunction.ToString(),
            BlendMode = layer.BlendMode.ToString()
        }).ToList();
    }

    private bool BuildLegacyPositiveLayerEnabled()
    {
        var positive = _simulationLayers.FirstOrDefault(layer => layer.InputFunction == SimulationInputFunction.Direct);
        return positive?.Enabled ?? true;
    }

    private bool BuildLegacyNegativeLayerEnabled()
    {
        var negative = _simulationLayers.FirstOrDefault(layer => layer.InputFunction == SimulationInputFunction.Inverse);
        return negative?.Enabled ?? true;
    }

    private string BuildLegacyPositiveLayerBlendMode()
    {
        var positive = _simulationLayers.FirstOrDefault(layer => layer.InputFunction == SimulationInputFunction.Direct);
        return (positive?.BlendMode ?? BlendMode.Additive).ToString();
    }

    private string BuildLegacyNegativeLayerBlendMode()
    {
        var negative = _simulationLayers.FirstOrDefault(layer => layer.InputFunction == SimulationInputFunction.Inverse);
        return (negative?.BlendMode ?? BlendMode.Subtractive).ToString();
    }

    private List<string> BuildLegacySimulationLayerOrder()
    {
        var positive = _simulationLayers.FirstOrDefault(layer => layer.InputFunction == SimulationInputFunction.Direct);
        var negative = _simulationLayers.FirstOrDefault(layer => layer.InputFunction == SimulationInputFunction.Inverse);
        var order = new List<string>(2);
        foreach (var layer in _simulationLayers)
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
                    _ => LayerEditorSourceKind.File
                },
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

        if (source.Type == CaptureSource.SourceType.Group)
        {
            source.SetDisplayName(string.IsNullOrWhiteSpace(model.DisplayName) ? "Layer Group" : model.DisplayName);
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
            public string Name { get; set; } = "Simulation Layer";
            public bool Enabled { get; set; } = true;
            public string InputFunction { get; set; } = SimulationInputFunction.Direct.ToString();
            public string BlendMode { get; set; } = MainWindow.BlendMode.Subtractive.ToString();
        }

        public sealed class SourceConfig
        {
            public string Type { get; set; } = CaptureSource.SourceType.Window.ToString();
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

        public Guid Id { get; } = Guid.NewGuid();
        public SourceType Type { get; }
        public WindowHandleInfo? Window { get; set; }
        public string? WebcamId { get; }
        public string? FilePath { get; }
        public List<string> FilePaths { get; } = new();
        public FileCaptureService.VideoSequenceSession? VideoSequence { get; private set; }
        public string DisplayName { get; private set; }
        public List<CaptureSource> Children { get; } = new();
        public List<LayerAnimation> Animations { get; } = new();
        public BlendMode BlendMode { get; set; } = BlendMode.Additive;
        public FitMode FitMode { get; set; } = FitMode.Fill;
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
        public bool RetryInitializationAttempted { get; set; }

        public bool IsInitialized { get; set; }
        public int? FileWidth { get; private set; }
        public int? FileHeight { get; private set; }
        public byte[]? CompositeDownscaledBuffer { get; set; }

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
        public CompositeFrame(byte[] downscaled, int downscaledWidth, int downscaledHeight)
        {
            Downscaled = downscaled;
            DownscaledWidth = downscaledWidth;
            DownscaledHeight = downscaledHeight;
        }

        public byte[] Downscaled { get; }
        public int DownscaledWidth { get; }
        public int DownscaledHeight { get; }
    }
}
