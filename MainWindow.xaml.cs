using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace lifeviz;

public partial class MainWindow : Window
{
    private const int DefaultColumns = 128;
    private const int DefaultDepth = 24;

    private readonly GameOfLifeEngine _engine = new();
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;
    private byte[]? _pixelBuffer;
    private bool _isPaused;

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _timer.Tick += (_, _) => OnTick();

        Loaded += (_, _) => InitializeVisualizer();
    }

    private void InitializeVisualizer()
    {
        _engine.Configure(DefaultColumns, DefaultDepth);
        RebuildSurface();
        _timer.Start();
    }

    private void OnTick()
    {
        if (!_isPaused)
        {
            _engine.Step();
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

    private void ApplyDimensions(int? columns, int? depth)
    {
        int nextColumns = columns ?? _engine.Columns;
        int nextDepth = depth ?? _engine.Depth;

        bool wasPaused = _isPaused;
        _isPaused = true;

        _engine.Configure(nextColumns, nextDepth);
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
}
