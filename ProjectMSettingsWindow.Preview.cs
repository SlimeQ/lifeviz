using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace lifeviz;

internal sealed partial class ProjectMSettingsWindow
{
    private const int PreviewWidth = 480, PreviewHeight = 270;
    private readonly Image _previewImage = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _previewName = new() { Text = "Select a preset in either list", TextWrapping = TextWrapping.Wrap, MaxHeight = 80 };
    private readonly TextBlock _previewStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _previewDemo = new() { Content = "Use demo audio", IsChecked = true, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(0, 8, 0, 8) };
    private readonly CheckBox _previewPaused = new() { Content = "Pause preview", Foreground = Brushes.WhiteSmoke, Margin = new Thickness(0, 0, 0, 8) };
    private readonly DispatcherTimer _previewTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.0 / 15) };
    private readonly Stopwatch _previewClock = Stopwatch.StartNew();
    private readonly float[] _previewPcm = new float[1024];
    private Action<float[]>? _previewAudio;
    private ProjectMRenderer? _previewRenderer;
    private WriteableBitmap? _previewBitmap;
    private string? _previewPath;
    private bool _previewNeedsLoad, _previewFailed, _previewClosed;
    private double _previewSelectedAt, _previewLastTick, _previewTime;
    private long _previewFrames;

    private FrameworkElement BuildPreview(Action<float[]>? audio)
    {
        _previewAudio = audio;
        _previewDemo.IsEnabled = audio != null;
        var panel = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Preset preview", FontSize = 17, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new Border { Background = Brushes.Black, Height = 177, Margin = new Thickness(0, 10, 0, 10), Child = _previewImage });
        panel.Children.Add(_previewName);
        panel.Children.Add(_previewDemo);
        panel.Children.Add(_previewPaused);
        panel.Children.Add(Button("Restart preview", (_, _) => SelectPreview(_previewPath)));
        panel.Children.Add(_previewStatus);
        panel.Children.Add(new TextBlock
        {
            Text = "Preview only. Add Selected puts a library preset in your playlist. Demo audio drives the visuals without playing sound.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 12, 0, 0)
        });
        _library.SelectionChanged += PreviewSelectionChanged;
        _selected.SelectionChanged += PreviewSelectionChanged;
        _previewTimer.Tick += (_, _) => TickPreview(_previewClock.Elapsed.TotalSeconds);
        Loaded += (_, _) => { if (!_previewClosed) { _previewLastTick = _previewClock.Elapsed.TotalSeconds; _previewTimer.Start(); } };
        Closed += (_, _) =>
        {
            _previewClosed = true;
            _previewTimer.Stop();
            _previewRenderer?.Dispose(); _previewRenderer = null;
            _previewImage.Source = null; _previewBitmap = null; _previewAudio = null;
        };
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private void PreviewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // In a multi-selection, audition the most recently added item.
        string? path = e.AddedItems.Cast<string>().LastOrDefault();
        if (path != null) SelectPreview(path);
        else if (sender is ListBox list && !PreviewPathIsSelected()) SelectPreview(list.SelectedItems.Cast<string>().LastOrDefault());
    }

    private bool PreviewPathIsSelected() => _library.SelectedItems.Contains(_previewPath) || _selected.SelectedItems.Contains(_previewPath);

    private void SelectPreview(string? path)
    {
        if (_previewClosed) return;
        _previewPath = path; _previewNeedsLoad = path != null; _previewFailed = false;
        _previewSelectedAt = _previewClock.Elapsed.TotalSeconds;
        _previewImage.Source = null;
        _previewName.Text = path ?? "Select a preset in either list";
        _previewName.ToolTip = path;
        _previewStatus.Text = path == null ? "" : "Preparing preview…";
    }

    private void TickPreview(double now)
    {
        double delta = Math.Clamp(now - _previewLastTick, 0, 0.2);
        _previewLastTick = now;
        if (_previewClosed || _previewPath == null || _previewFailed) return;
        if (_previewPaused.IsChecked == true || WindowState == WindowState.Minimized)
        {
            _previewStatus.Text = "Preview paused";
            return;
        }
        // Avoid compiling every intermediate preset while arrowing through a list.
        if (_previewNeedsLoad && now - _previewSelectedAt < 0.2) return;
        try
        {
            if (!ProjectMLibrary.EnsureReady(wait: false)) { _previewStatus.Text = "Preparing bundled textures…"; return; }
            if (_previewNeedsLoad)
            {
                // Fresh context removes feedback/audio history from the previous audition.
                _previewRenderer?.Dispose(); _previewRenderer = null;
                _previewRenderer = new ProjectMRenderer();
                _previewTime = 0;
                string? error = _previewRenderer.Load(_previewPath, 0, 0, true);
                if (error != null) throw new InvalidOperationException(error);
                _previewNeedsLoad = false;
            }
            else _previewTime += delta;
            if (_previewDemo.IsChecked == true)
            {
                double pulse = Math.Exp(-(_previewTime % 0.5) * 12);
                for (int i = 0; i < _previewPcm.Length; i++)
                {
                    double sampleTime = _previewTime + i / 48000.0;
                    _previewPcm[i] = (float)(0.6 * pulse * Math.Sin(sampleTime * Math.Tau * 80)
                        + 0.12 * Math.Sin(sampleTime * Math.Tau * 440) + 0.05 * Math.Sin(sampleTime * Math.Tau * 1760));
                }
            }
            else { Array.Clear(_previewPcm); _previewAudio?.Invoke(_previewPcm); }
            byte[] pixels = _previewRenderer!.Render(PreviewWidth, PreviewHeight, _previewTime, _previewPcm);
            _previewBitmap ??= new WriteableBitmap(PreviewWidth, PreviewHeight, 96, 96, PixelFormats.Bgra32, null);
            _previewBitmap.WritePixels(new Int32Rect(0, 0, PreviewWidth, PreviewHeight), pixels, PreviewWidth * 4, 0);
            _previewImage.Source = _previewBitmap; _previewFrames++;
            _previewStatus.Text = _previewDemo.IsChecked == true ? "Demo beat · silent audition" : "LifeViz Audio Source · silence if no input";
        }
        catch (Exception ex)
        {
            _previewFailed = true; _previewImage.Source = null;
            _previewStatus.Text = $"Preview unavailable: {ex.Message}";
            _previewRenderer?.Dispose(); _previewRenderer = null;
            Logger.Warn($"projectM preset preview failed for {_previewPath}: {ex.Message}");
        }
    }
}
