using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace lifeviz;

public partial class MainWindow : Window
{
    private const int DefaultColumns = 128;
    private const int DefaultDepth = 24;
    private const double DefaultAspectRatio = 16d / 9d;
    private const double CaptureThreshold = 0.55;
    private const double DefaultFps = 60;

    private readonly GameOfLifeEngine _engine = new();
    private readonly DispatcherTimer _timer;
    private readonly WindowCaptureService _windowCapture = new();
    private readonly BlendEffect _blendEffect = new();
    private IReadOnlyList<WindowHandleInfo> _cachedWindows = Array.Empty<WindowHandleInfo>();
    private WriteableBitmap? _bitmap;
    private byte[]? _pixelBuffer;
    private WriteableBitmap? _underlayBitmap;
    private ImageBrush? _overlayBrush;
    private byte[]? _engineColorBuffer;
    private WindowCaptureService.WindowCaptureFrame? _lastCaptureFrame;
    private int _displayWidth;
    private int _displayHeight;
    private int[] _rowMap = Array.Empty<int>();
    private int[] _colMap = Array.Empty<int>();
    private bool _isPaused;
    private double _currentAspectRatio = DefaultAspectRatio;
    private WindowHandleInfo? _selectedWindow;
    private IntPtr _windowHandle;
    private bool _passthroughEnabled;
    private bool _preserveResolution;
    private BlendMode _blendMode = BlendMode.Additive;
    private double _currentFps = DefaultFps;

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / DefaultFps)
        };
        _timer.Tick += (_, _) => OnTick();

        Loaded += (_, _) => InitializeVisualizer();
        SourceInitialized += (_, _) =>
        {
            var helper = new WindowInteropHelper(this);
            _windowHandle = helper.Handle;
        };
    }

    private void InitializeVisualizer()
    {
        _currentAspectRatio = DefaultAspectRatio;
        _engine.Configure(DefaultColumns, DefaultDepth, _currentAspectRatio);
        UpdateDisplaySurface(force: true);
        InitializeEffect();
        _timer.Start();
    }

    private void OnTick()
    {
        if (!_isPaused)
        {
            _engine.Step();
            InjectWindowCaptureFrame();
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

    private void ApplyDimensions(int? columns, int? depth, double? aspectOverride = null)
    {
        int nextColumns = columns ?? _engine.Columns;
        int nextDepth = depth ?? _engine.Depth;
        double nextAspect = aspectOverride ?? _currentAspectRatio;
        _currentAspectRatio = nextAspect;

        bool wasPaused = _isPaused;
        _isPaused = true;

        _engine.Configure(nextColumns, nextDepth, _currentAspectRatio);
        RebuildSurface();

        _isPaused = wasPaused;
        PauseMenuItem.Header = _isPaused ? "Resume Simulation" : "Pause Simulation";
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
        PopulateWindowMenu();
        if (PassthroughMenuItem != null)
        {
            PassthroughMenuItem.IsChecked = _passthroughEnabled;
            PassthroughMenuItem.IsEnabled = _selectedWindow != null;
        }
        if (PreserveResolutionMenuItem != null)
        {
            PreserveResolutionMenuItem.IsChecked = _preserveResolution;
            PreserveResolutionMenuItem.IsEnabled = _selectedWindow != null;
        }

        if (BlendModeMenu != null)
        {
            BlendModeMenu.IsEnabled = _selectedWindow != null;
            UpdateBlendModeMenuChecks();
        }

        UpdateFramerateMenuChecks();
    }

    private void PopulateWindowMenu()
    {
        if (WindowInputMenu == null)
        {
            return;
        }

        WindowInputMenu.Items.Clear();

        var noneItem = new MenuItem
        {
            Header = "None",
            IsCheckable = true,
            IsChecked = _selectedWindow == null
        };
        noneItem.Click += (_, _) => ClearWindowSelection();
        WindowInputMenu.Items.Add(noneItem);
        WindowInputMenu.Items.Add(new Separator());

        _cachedWindows = _windowCapture.EnumerateWindows(_windowHandle);
        if (_cachedWindows.Count == 0)
        {
            WindowInputMenu.Items.Add(new MenuItem
            {
                Header = "No windows detected",
                IsEnabled = false
            });
            return;
        }

        foreach (var window in _cachedWindows)
        {
            var item = new MenuItem
            {
                Header = window.Title,
                Tag = window,
                IsCheckable = true,
                IsChecked = _selectedWindow != null && window.Handle == _selectedWindow.Handle
            };
            item.Click += WindowInputItem_Click;
            WindowInputMenu.Items.Add(item);
        }
    }

    private void WindowInputItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: WindowHandleInfo info })
        {
            return;
        }

        _selectedWindow = info;
        _currentAspectRatio = info.AspectRatio;
        ApplyDimensions(null, null, _currentAspectRatio);
    }

    private void ClearWindowSelection()
    {
        bool hadSelection = _selectedWindow != null;
        _selectedWindow = null;
        _lastCaptureFrame = null;
        _preserveResolution = false;
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
        else if (hadSelection)
        {
            RenderFrame();
        }
    }

    private void InjectWindowCaptureFrame()
    {
        if (_selectedWindow == null)
        {
            return;
        }

        var frame = _windowCapture.CaptureFrame(_selectedWindow, _engine.Columns, _engine.Rows, CaptureThreshold);
        if (frame == null)
        {
            ClearWindowSelection();
            return;
        }

        _lastCaptureFrame = frame;
        UpdateDisplaySurface();
        _engine.InjectFrame(frame.Mask);
    }

    private void TogglePassthrough_Click(object sender, RoutedEventArgs e)
    {
        _passthroughEnabled = !_passthroughEnabled;
        if (PassthroughMenuItem != null)
        {
            PassthroughMenuItem.IsChecked = _passthroughEnabled;
        }
        RenderFrame();
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
        Darken
    }

    private void UpdateDisplaySurface(bool force = false)
    {
        int targetWidth = _preserveResolution && _lastCaptureFrame != null ? _lastCaptureFrame.SourceWidth : _engine.Columns;
        int targetHeight = _preserveResolution && _lastCaptureFrame != null ? _lastCaptureFrame.SourceHeight : _engine.Rows;

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
        GameImage.Effect = _blendEffect;
        UpdateEffectInput();
    }

    private void UpdateUnderlayBitmap(int requiredLength)
    {
        if (_underlayBitmap == null)
        {
            return;
        }

        bool hasOverlay = _passthroughEnabled && _lastCaptureFrame != null;
        if (!hasOverlay)
        {
            return;
        }

        int width = _underlayBitmap.PixelWidth;
        int height = _underlayBitmap.PixelHeight;
        byte[]? buffer = null;
        int stride = width * 4;

        if (_preserveResolution && _lastCaptureFrame?.OverlaySource is { Length: > 0 } source &&
            _lastCaptureFrame.SourceWidth == width && _lastCaptureFrame.SourceHeight == height)
        {
            buffer = source;
            stride = _lastCaptureFrame.SourceWidth * 4;
        }
        else if (_lastCaptureFrame?.OverlayDownscaled is { Length: > 0 } downscaled &&
                 downscaled.Length >= requiredLength)
        {
            buffer = downscaled;
        }

        if (buffer == null || buffer.Length < stride * height)
        {
            return;
        }

        _underlayBitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, stride, 0);
    }

    private void UpdateEffectInput()
    {
        _blendEffect.UseOverlay = _passthroughEnabled && _lastCaptureFrame != null ? 1.0 : 0.0;
        _blendEffect.Mode = _blendMode switch
        {
            BlendMode.Additive => 0.0,
            BlendMode.Normal => 1.0,
            BlendMode.Multiply => 2.0,
            BlendMode.Screen => 3.0,
            BlendMode.Overlay => 4.0,
            BlendMode.Lighten => 5.0,
            BlendMode.Darken => 6.0,
            _ => 0.0
        };
    }

    private void SetFramerate(double fps)
    {
        fps = Math.Clamp(fps, 5, 120);
        _currentFps = fps;
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / _currentFps);
        UpdateFramerateMenuChecks();
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
}
