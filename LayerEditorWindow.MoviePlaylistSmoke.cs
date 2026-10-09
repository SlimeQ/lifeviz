using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;

namespace lifeviz;

public partial class LayerEditorWindow
{
    internal void ValidateMoviePlaylistControlsForSmoke(LayerEditorSource model, string imagePath, Action nextFrame, Action<LayerEditorWindow> checkLayerReorder)
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
        LoadMovieSubtitleTracksAsync(selected!).GetAwaiter().GetResult();
        var tracks = selected!.EmbeddedSubtitleTracks;
        if (tracks.Length != 2 || !tracks[0].Label.Contains("English SDH") || !tracks[0].Label.Contains("English") ||
            !tracks[0].IsDefault || tracks[1].Language != "fra" || !tracks[1].IsForced || !tracks[1].Label.Contains("French signs"))
            throw new InvalidOperationException("Embedded subtitle track labels lost their language, title, or flags.");
        var subtitleTracks = Descendants(playlistGroup).OfType<ComboBox>().Single(item =>
            item.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path == "EmbeddedSubtitleTracks");
        if (subtitleTracks.Items.Count != 2) throw new InvalidOperationException("Embedded subtitle listing failed to bind.");
        subtitleTracks.SelectedIndex = 1;
        _suppressLiveUpdates = false;
        MovieSubtitleTrack_SelectionChanged(subtitleTracks, new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, Array.Empty<object>(), new object[] { tracks[1] }));
        if (selected.SubtitleTrack != 1) throw new InvalidOperationException("Selecting a labelled subtitle track did not apply its ordinal.");

        _suppressLiveUpdates = true;
        _viewModel.LiveMode = true;
        _viewModel.Sources = new ObservableCollection<LayerEditorSource> { model };
        SetSelectedSource(model);
        _suppressLiveUpdates = false;
        RefreshSelectedVideoTransportState();
        var playingBefore = model.PlayingMovie;
        var playback = Descendants(playlistGroup).OfType<ComboBox>().Single(item => item.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path.Path == "PlayingMovie");
        var scrub = Descendants(playlistGroup).OfType<Slider>().Single();
        if (scrub.ActualWidth < 300 || model.VideoPlaybackDurationSeconds < 3) throw new InvalidOperationException("Movie timeline did not expose a full-width duration.");
        var playPause = Descendants(playlistGroup).OfType<Button>().Single(item => item.GetBindingExpression(ContentControl.ContentProperty)?.ParentBinding.Path.Path == "VideoPlaybackToggleLabel");
        VideoPlayPause_Click(playPause, new RoutedEventArgs());
        if (!model.VideoPlaybackPaused || !Equals(playPause.Content, "Play")) throw new InvalidOperationException("Playlist player failed to pause.");
        BeginMovieScrub(scrub);
        scrub.SetCurrentValue(Slider.ValueProperty, 1.5);
        RefreshSelectedVideoTransportState();
        if (Math.Abs(scrub.Value - 1.5) > 0.01) throw new InvalidOperationException("Playback timer overwrote the drag preview.");
        CommitMovieScrub(scrub);
        nextFrame();
        RefreshSelectedVideoTransportState();
        if (!model.VideoPlaybackPaused || Math.Abs(model.MovieScrubSeconds - 1.5) > 0.15) throw new InvalidOperationException("Scrubbing failed to seek or preserve pause.");
        _owner.TryGetMoviePlaylistState(model.Id, out var selectedId, out double selectedTime, out _);
        // Exercise deferred bindings after leaving/reselecting the scene tree row,
        // then a transient programmatic dropdown selection during panel refresh.
        SetSelectedSource(null);
        root.UpdateLayout();
        SetSelectedSource(model);
        root.UpdateLayout();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        model.PlayingMovie = model.MoviePlaylist.Movies.First(movie => movie.Id != selectedId);
        root.UpdateLayout();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        _owner.TryGetMoviePlaylistState(model.Id, out var afterId, out double afterTime, out _);
        if (afterId != selectedId || Math.Abs(afterTime - selectedTime) > 0.001 || !model.VideoPlaybackPaused)
            throw new InvalidOperationException("Reselecting/rebinding the playlist layer changed its movie, position, or pause state.");
        RefreshSelectedVideoTransportState();
        MoviePlayback_DropDownOpened(playback, EventArgs.Empty);
        MoviePlayback_DropDownClosed(playback, EventArgs.Empty);
        _owner.TryGetMoviePlaylistState(model.Id, out afterId, out afterTime, out _);
        if (afterId != selectedId || Math.Abs(afterTime - selectedTime) > 0.001)
            throw new InvalidOperationException("Opening/closing Now playing without a new choice restarted playback.");
        var next = Descendants(playlistGroup).OfType<Button>().Single(item => Equals(item.Content, "Next movie"));
        MovieStep_Click(next, new RoutedEventArgs());
        nextFrame();
        RefreshSelectedVideoTransportState();
        if (model.PlayingMovie?.Id == playingBefore?.Id || !model.VideoPlaybackPaused) throw new InvalidOperationException("Next movie failed to switch while paused.");
        var previous = Descendants(playlistGroup).OfType<Button>().Single(item => Equals(item.Content, "Previous movie"));
        MovieStep_Click(previous, new RoutedEventArgs());
        nextFrame();
        RefreshSelectedVideoTransportState();
        if (model.PlayingMovie?.Id != playingBefore?.Id) throw new InvalidOperationException("Previous movie did not wrap back.");
        MoviePlayback_DropDownOpened(playback, EventArgs.Empty);
        playback.SelectedItem = model.MoviePlaylist.Movies.First(movie => movie.Id != playingBefore!.Id);
        MoviePlayback_DropDownClosed(playback, EventArgs.Empty);
        nextFrame();
        RefreshSelectedVideoTransportState();
        if (model.PlayingMovie?.Id == playingBefore?.Id) throw new InvalidOperationException("Now playing selector did not switch playback.");
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(playback), 0, Key.Up);
        MoviePlayback_KeyDown(playback, key);
        playback.SelectedItem = playingBefore;
        MoviePlayback_KeyDown(playback, key);
        MoviePlayback_KeyUp(playback, key);
        nextFrame();
        RefreshSelectedVideoTransportState();
        if (model.PlayingMovie?.Id != playingBefore?.Id) throw new InvalidOperationException("Keyboard movie selection failed.");
        VideoPlayPause_Click(playPause, new RoutedEventArgs());
        if (model.VideoPlaybackPaused || !Equals(playPause.Content, "Pause")) throw new InvalidOperationException("Playlist player failed to resume.");
        var scroll = Descendants(root).OfType<ScrollViewer>().Single(view => view.Content is StackPanel panel && ReferenceEquals(panel.DataContext, model));
        scroll.ScrollToVerticalOffset(0);
        root.UpdateLayout();
        if (!Descendants(playback).OfType<TextBlock>().Any(text => text.Text == model.PlayingMovie!.DisplayName) ||
            !Descendants(subtitleTracks).OfType<TextBlock>().Any(text => text.Text == tracks[1].Label))
            throw new InvalidOperationException("Player or subtitle picker showed an object name instead of its display label.");
        var bitmap = new RenderTargetBitmap(1240, 820, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(imagePath);
        encoder.Save(output);
        checkLayerReorder(this);
        Close();
    }

    internal void FlushMoviePlaylistBindingsForSmoke()
    {
        UpdateLayout();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        RefreshSelectedVideoTransportState();
        UpdateLayout();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
}
