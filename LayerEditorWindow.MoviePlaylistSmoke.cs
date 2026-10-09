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
    internal void ValidateMoviePlaylistControlsForSmoke(LayerEditorSource model, string imagePath)
    {
        _suppressLiveUpdates = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Left = -10000;
        Top = -10000;
        Show();
        _viewModel.LiveMode = false;
        SetLiveModeForSmoke(false);
        _suppressLiveUpdates = true;
        _viewModel.Sources = new ObservableCollection<LayerEditorSource> { model };
        SetSelectedSource(model);
        model.SelectedMovie = model.MoviePlaylist.Movies[0];
        var root = (FrameworkElement)Content;
        root.Measure(new Size(1240, 820));
        root.Arrange(new Rect(0, 0, 1240, 820));
        root.UpdateLayout();
        var movieList = Descendants(root).OfType<ListBox>().Single(item => item.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == "MoviePlaylist.Movies");
        var subtitleModes = Descendants(root).OfType<ComboBox>().Single(item => item.GetBindingExpression(ComboBox.SelectedValueProperty)?.ParentBinding.Path.Path == "SubtitleMode");
        if (movieList.Items.Count != 2 || subtitleModes.Items.Count != 3) throw new InvalidOperationException("Playlist controls failed to bind.");
        var selected = model.SelectedMovie;
        var playlistGroup = Descendants(root).OfType<GroupBox>().Single(item => Equals(item.Header, "Movie Playlist"));
        var button = Descendants(playlistGroup).OfType<Button>().Single(item => Equals(item.Content, "Move Down"));
        MovieMoveDown_Click(button, new RoutedEventArgs());
        if (model.MoviePlaylist.Movies[1] != selected) throw new InvalidOperationException("Draft reorder failed.");
        MovieMoveUp_Click(button, new RoutedEventArgs());
        var scroll = Descendants(root).OfType<ScrollViewer>().Single(view => view.Content is StackPanel panel && ReferenceEquals(panel.DataContext, model));
        scroll.ScrollToVerticalOffset(440);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1240, 820, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(imagePath);
        encoder.Save(output);
        Close();
    }
}
