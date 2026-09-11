using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace lifeviz;

public partial class LayerEditorWindow
{
    internal void VerifyNewProjectLayout(string imagePath)
    {
        Width = MinWidth;
        Height = MinHeight;
        ShowInTaskbar = false;
        ShowActivated = false;
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();
        var root = (FrameworkElement)Content;
        foreach (var control in new[] { NewProjectButton, LoadLayersButton, SaveLayersButton, RecoverLayersButton, ApplyButton })
        {
            Point position = control.TranslatePoint(new Point(), root);
            if (control.ActualWidth <= 0 || position.X < 0 || position.X + control.ActualWidth > root.ActualWidth + 1)
                throw new InvalidOperationException($"Project control is clipped: {control.Name}");
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(imagePath);
        encoder.Save(stream);
    }

    internal void VerifySceneRecoveryLayout(string imagePath)
    {
        VerifyNewProjectLayout(imagePath);
        _viewModel.LiveMode = false;
        _viewModel.Sources[0].DisplayName = "Unapplied draft survives";
        PrepareForOwnerShutdown();
        var draft = LayerConfigFile.Parse(File.ReadAllText(_owner.EditorDraftRecoveryPath));
        if (draft.Sources[0].DisplayName != "Unapplied draft survives" ||
            _owner.BuildLayerEditorSources()[0].DisplayName == "Unapplied draft survives")
            throw new InvalidOperationException("Unapplied draft checkpoint changed the live scene or lost the draft.");
        Close();
    }

    internal void PrepareNewProjectDraftForSmoke()
    {
        if (_viewModel.LiveMode) throw new InvalidOperationException("Draft check requires Live Mode off.");
        _viewModel.Sources[0].DisplayName = "Unapplied New Project draft";
    }
}
