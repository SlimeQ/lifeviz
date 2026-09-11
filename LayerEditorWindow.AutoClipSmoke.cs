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

        var reset = Descendants(root).OfType<Button>().Single(item => Equals(item.Content, "Reset AutoClip Sequence"));
        if (reset.IsEnabled) throw new InvalidOperationException("Draft mode exposed live AutoClip reset.");
        _draggedSource = model;
        SceneTree_ClearDragCandidate(SceneTree, new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0));
        if (_draggedSource != null) throw new InvalidOperationException("Scene tree retained a stale layer drag candidate.");

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
            AutoClipFileList.SelectedItems.Add(model.AutoClipVideoOverrides[0]);
            MoveAutoClipVideo(1);
            if (model.FilePaths[1] != first || model.AutoClipVideoPaths[1] != first) throw new InvalidOperationException("Playlist reorder did not update playback order.");
            MoveAutoClipVideo(-1);
            if (model.FilePaths[0] != first) throw new InvalidOperationException("Playlist reorder did not restore order.");
        }
        ValidateAutoClipMultiSelectionForSmoke(model);
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

    private void ValidateAutoClipMultiSelectionForSmoke(LayerEditorSource model)
    {
        var original = model.AutoClipVideoOverrides.ToArray();
        var files = Enumerable.Range(0, 6).Select(index => new LayerEditorAutoClipVideoOverride
        {
            FilePath = $"playlist-{index}.webm",
            BlendMode = index % 2 == 0 ? "Normal" : "Additive",
            KeyColorHex = "#12AB34"
        }).ToArray();
        model.AutoClipVideoOverrides.Clear();
        foreach (var file in files) model.AutoClipVideoOverrides.Add(file);
        CommitAutoClipFileList(model);
        AutoClipFileList.SelectedItems.Add(files[1]);
        AutoClipFileList.SelectedItems.Add(files[3]);
        void CheckOrder(params int[] order)
        {
            if (!model.AutoClipVideoOverrides.SequenceEqual(order.Select(index => files[index])) ||
                !model.FilePaths.SequenceEqual(order.Select(index => files[index].FilePath)) ||
                !model.AutoClipVideoPaths.SequenceEqual(model.FilePaths))
                throw new InvalidOperationException("Multi-file operation lost order, override identity, or playback paths.");
        }
        void CheckSelection()
        {
            if (!GetSelectedAutoClipFiles().SetEquals(new[] { files[1], files[3] }) ||
                model.SelectedAutoClipVideoOverride != null || AutoClipFileOverrides.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Multiple selection was lost or exposed ambiguous file overrides.");
        }
        CheckSelection();
        MoveAutoClipVideo(-1); CheckOrder(1, 0, 3, 2, 4, 5); CheckSelection();
        MoveAutoClipVideo(1); CheckOrder(0, 1, 2, 3, 4, 5); CheckSelection();
        MoveAutoClipVideo(-2); CheckOrder(1, 3, 0, 2, 4, 5); CheckSelection();
        MoveAutoClipVideo(-1); CheckOrder(1, 3, 0, 2, 4, 5); CheckSelection();
        MoveAutoClipVideo(2); CheckOrder(0, 2, 4, 5, 1, 3); CheckSelection();
        MoveAutoClipVideo(1); CheckOrder(0, 2, 4, 5, 1, 3); CheckSelection();
        RemoveSelectedAutoClipFiles(); CheckOrder(0, 2, 4, 5);
        if (AutoClipFileList.SelectedItems.Count != 1 || model.SelectedAutoClipVideoOverride == null)
            throw new InvalidOperationException("Bulk removal did not select a surviving neighbor.");
        AutoClipFileList.SelectAll();
        RemoveSelectedAutoClipFiles(); CheckOrder();
        if (AutoClipRemoveFilesButton.IsEnabled || AutoClipFileList.SelectedItems.Count != 0)
            throw new InvalidOperationException("Remove-all did not clear selection controls.");
        foreach (var file in original) model.AutoClipVideoOverrides.Add(file);
        CommitAutoClipFileList(model);
        AutoClipFileList.SelectAll();
        if (!File.Exists(original[0].FilePath)) throw new InvalidOperationException("Playlist edit must not remove media from disk.");
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
