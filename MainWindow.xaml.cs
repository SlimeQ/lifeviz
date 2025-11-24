using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace lifeviz;

public partial class MainWindow : Window
{
    private const int DefaultColumns = 128;
    private const int DefaultDepth = 24;
    private const double DefaultAspectRatio = 16d / 9d;
    private const double CaptureThreshold = 0.55;

    private readonly GameOfLifeEngine _engine = new();
    private readonly DispatcherTimer _timer;
    private readonly WindowCaptureService _windowCapture = new();
    private IReadOnlyList<WindowHandleInfo> _cachedWindows = Array.Empty<WindowHandleInfo>();
    private WriteableBitmap? _bitmap;
    private byte[]? _pixelBuffer;
    private bool _isPaused;
    private double _currentAspectRatio = DefaultAspectRatio;
    private WindowHandleInfo? _selectedWindow;
    private IntPtr _windowHandle;

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
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
        RebuildSurface();
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

    private void RebuildSurface()
    {
        _bitmap = new WriteableBitmap(_engine.Columns, _engine.Rows, 96, 96, PixelFormats.Bgra32, null);
        _pixelBuffer = new byte[_engine.Columns * _engine.Rows * 4];
        GameImage.Source = _bitmap;
        GameImage.Width = _engine.Columns;
        GameImage.Height = _engine.Rows;
        RenderFrame();
    }

    private void RenderFrame()
    {
        if (_bitmap == null || _pixelBuffer == null)
        {
            return;
        }

        int width = _bitmap.PixelWidth;
        int height = _bitmap.PixelHeight;
        int stride = width * 4;

        if (_pixelBuffer.Length != stride * height)
        {
            _pixelBuffer = new byte[stride * height];
        }

        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                var (r, g, b) = _engine.GetColor(row, col);
                int index = (row * stride) + (col * 4);
                _pixelBuffer[index] = b;
                _pixelBuffer[index + 1] = g;
                _pixelBuffer[index + 2] = r;
                _pixelBuffer[index + 3] = 255;
            }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _pixelBuffer, stride, 0);
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

        var mask = _windowCapture.CaptureMask(_selectedWindow, _engine.Columns, _engine.Rows, CaptureThreshold);
        if (mask == null)
        {
            ClearWindowSelection();
            return;
        }

        _engine.InjectFrame(mask);
    }
}
