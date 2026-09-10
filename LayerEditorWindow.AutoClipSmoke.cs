using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace lifeviz;

public partial class LayerEditorWindow
{
    internal void ValidateAutoClipControlsForSmoke(LayerEditorSource model, string imagePath)
    {
        _suppressLiveUpdates = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();
        _viewModel.LiveMode = false;
        SetLiveModeForSmoke(false);
        _suppressLiveUpdates = true;
        _viewModel.Sources = new ObservableCollection<LayerEditorSource> { model };
        SetSelectedSource(model);
        var root = (FrameworkElement)Content;
        root.Measure(new Size(1240, 820));
        root.Arrange(new Rect(0, 0, 1240, 820));
        root.UpdateLayout();

        foreach (string label in new[] { "Take over this visibility group while playing", "Start with delay", "Play files in list order (unchecked = random)", "Play whole file from beginning to end" })
        {
            var checkbox = Descendants(root).OfType<CheckBox>().SingleOrDefault(item => Equals(item.Content, label))
                ?? throw new InvalidOperationException($"AutoClip control missing: {label}");
            if (checkbox.IsChecked != true) throw new InvalidOperationException($"AutoClip checkbox binding failed: {label}");
        }
        var groupText = Descendants(root).OfType<TextBox>().Single(item => item.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "VisibilityGroup");
        groupText.SetCurrentValue(TextBox.TextProperty, "edited group");
        groupText.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        if (model.VisibilityGroup != "edited group") throw new InvalidOperationException("Visibility group input binding failed.");
        model.VisibilityGroup = "show";
        if (model.AutoClipVideoOverrides.Count >= 2)
        {
            string first = model.AutoClipVideoOverrides[0].FilePath;
            model.SelectedAutoClipVideoOverride = model.AutoClipVideoOverrides[0];
            MoveAutoClipVideo(1);
            if (model.FilePaths[1] != first || model.AutoClipVideoPaths[1] != first) throw new InvalidOperationException("Playlist reorder did not update playback order.");
            MoveAutoClipVideo(-1);
            if (model.FilePaths[0] != first) throw new InvalidOperationException("Playlist reorder did not restore order.");
        }
        root.UpdateLayout();
        void SaveSnapshot(string path)
        {
            var bitmap = new RenderTargetBitmap(1240, 820, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(path);
            encoder.Save(output);
        }
        SaveSnapshot(imagePath);
        var scroll = Descendants(root).OfType<ScrollViewer>().Single(view => view.Content is StackPanel panel && ReferenceEquals(panel.DataContext, model));
        if (scroll.ScrollableHeight <= 0) throw new InvalidOperationException("Selected-layer controls must scroll at normal window size.");
        scroll.ScrollToVerticalOffset(420);
        root.UpdateLayout();
        if (scroll.VerticalOffset <= 0) throw new InvalidOperationException("Playback controls were unreachable by scrolling.");
        SaveSnapshot(Path.ChangeExtension(imagePath, "playback.png"));
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
