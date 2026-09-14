using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace lifeviz;

public partial class LayerEditorWindow
{
    internal void VerifySimulationBlendControls(Guid sourceId)
    {
        Width = 1240;
        Height = 820;
        ShowInTaskbar = false;
        ShowActivated = false;
        Left = Top = -10000;
        Show();
        _viewModel.LiveMode = true;
        SetSelectedSource(_viewModel.Sources.Single(source => source.Id == sourceId));
        UpdateLayout();
        SimulationGroupBlendComboBox.SelectedValue = "Screen";
        SimulationGroupOpacitySlider.Value = 0.4;
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        var runtime = _owner.BuildLayerEditorSources().Single(source => source.Id == sourceId);
        if (runtime.BlendMode != "Screen" || Math.Abs(runtime.Opacity - 0.4) > 0.001)
            throw new InvalidOperationException("Group blend/opacity controls did not update runtime.");
        _viewModel.LiveMode = false;
        SimulationGroupBlendComboBox.SelectedValue = "Multiply";
        if (_owner.BuildLayerEditorSources().Single(source => source.Id == sourceId).BlendMode != "Screen")
            throw new InvalidOperationException("Draft group blend leaked to runtime.");
        SimulationGroupBlendComboBox.SelectedValue = "Screen";
        _viewModel.LiveMode = true;
        UpdateLayout();
        var root = (FrameworkElement)Content;
        foreach (var control in new FrameworkElement[] { SimulationGroupBlendComboBox, SimulationGroupOpacitySlider })
        {
            Point position = control.TranslatePoint(new Point(), root);
            if (!control.IsVisible || control.ActualWidth <= 0 || position.X < 0 || position.X + control.ActualWidth > root.ActualWidth)
                throw new InvalidOperationException("Simulation blend controls are clipped.");
        }
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-simulation-blending-editor.png"));
        encoder.Save(output);
    }
}
