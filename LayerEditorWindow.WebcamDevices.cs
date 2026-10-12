using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace lifeviz;

// Camera picker for webcam layers: swap devices without rebuilding the layer.
public partial class LayerEditorWindow
{
    private IReadOnlyList<WebcamCaptureService.CameraInfo> _webcamList = Array.Empty<WebcamCaptureService.CameraInfo>();

    private async void RefreshWebcamOptions(LayerEditorSource source)
    {
        if (_ownerIsShuttingDown || !source.IsWebcam) return;
        source.WebcamOptions = MainWindow.BuildWebcamOptions(_webcamList, source.WebcamId, source.DisplayName);
        try { _webcamList = await _owner.RefreshWebcamDevicesAsync(); }
        catch (Exception ex)
        {
            Logger.Warn($"Could not list cameras: {ex.Message}");
            return;
        }
        // A live refresh may have replaced the model; update whichever webcam layer is selected now.
        if (_viewModel.SelectedSource is { IsWebcam: true } selected)
            selected.WebcamOptions = MainWindow.BuildWebcamOptions(_webcamList, selected.WebcamId, selected.DisplayName);
    }

    private void WebcamRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSourceContext(sender) is { IsWebcam: true } source) RefreshWebcamOptions(source);
    }

    private void WebcamDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ownerIsShuttingDown ||
            sender is not ComboBox { DataContext: LayerEditorSource { IsWebcam: true } source, SelectedItem: LayerEditorOption option } ||
            string.Equals(option.Value, source.WebcamChoice, StringComparison.OrdinalIgnoreCase))
            return;
        ChooseWebcamDevice(source, option.Value);
    }

    // Selects the layer, waits for the camera list, then picks a camera through the real ComboBox.
    internal void RunWebcamPickerSmoke(Guid sourceId, string cameraId, string imagePath)
    {
        RefreshFromSources(sourceId);
        ComboBox? picker = null;
        for (int i = 0; i < 100; i++)
        {
            PumpForSmoke(TimeSpan.FromMilliseconds(30));
            picker = FindVisualChildren<ComboBox>(this).FirstOrDefault(combo => combo.DataContext is LayerEditorSource { IsWebcam: true } &&
                combo.ItemsSource is IReadOnlyList<LayerEditorOption> { Count: > 1 } options && options.Any(o => o.Value == cameraId));
            if (picker != null) break;
        }
        if (picker == null || _viewModel.SelectedSource?.Id != sourceId)
            throw new InvalidOperationException("The camera picker did not list the connected cameras for the selected webcam layer.");
        if (!string.Equals(picker.SelectedValue as string, _viewModel.SelectedSource.WebcamChoice, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The camera picker shows {picker.SelectedValue}, not the layer's camera.");

        picker.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, cameraId);
        PumpForSmoke(TimeSpan.FromMilliseconds(100));
        UpdateLayout();
        var root = (FrameworkElement)Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(imagePath);
        encoder.Save(stream);
    }

    private void PumpForSmoke(TimeSpan duration)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    internal bool ChooseWebcamDevice(LayerEditorSource source, string? webcamId)
    {
        string? id = string.IsNullOrWhiteSpace(webcamId) ? null : webcamId;
        string name = id == null ? MainWindow.AnyWebcamName :
            _webcamList.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)).Name ?? source.DisplayName;
        if (_viewModel.LiveMode)
        {
            if (!_owner.SetWebcamDeviceFromEditor(source.Id, id, name, out var error))
            {
                if (!App.IsSmokeTestMode)
                    MessageBox.Show(this, error ?? "Could not switch camera.", "Camera", MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshFromSources(source.Id);
                return false;
            }
            RefreshFromSources(source.Id);
            return true;
        }

        source.WebcamId = id;
        source.DisplayName = name;
        source.WebcamStatus = "The camera change applies when you press Apply.";
        source.WebcamOptions = MainWindow.BuildWebcamOptions(_webcamList, source.WebcamId, source.DisplayName);
        return true;
    }
}
