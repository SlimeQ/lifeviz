using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace lifeviz;

public partial class LayerEditorWindow
{
    private bool _updatingMoviePlaybackSelection;
    private LayerEditorSource? _movieScrubSource;
    private Guid _movieScrubId;
    private bool _loadingSubtitleChoices;

    private async void MovieList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: MoviePlaylistEntry movie }) await LoadMovieSubtitleTracksAsync(movie);
    }

    private async void MovieSubtitleRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSource?.SelectedMovie is { } movie) await LoadMovieSubtitleTracksAsync(movie, force: true);
    }

    private async Task LoadMovieSubtitleTracksAsync(MoviePlaylistEntry movie, bool force = false)
    {
        if (movie.SubtitleTracksLoading || (movie.SubtitleTracksLoaded && !force)) return;
        movie.SubtitleTracksLoading = true;
        MovieSubtitleTrack[]? tracks = null;
        try { tracks = await FileCaptureService.GetMovieSubtitleTracksAsync(movie.FilePath); }
        catch (Exception ex) { Logger.Warn($"Could not load subtitle tracks for {movie.DisplayName}: {ex.Message}"); }
        finally { movie.SubtitleTracksLoading = false; }
        _loadingSubtitleChoices = true;
        try { movie.SetSubtitleTracks(tracks); }
        finally { _loadingSubtitleChoices = false; }
    }

    private void MovieSubtitleTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSubtitleChoices || _suppressLiveUpdates || sender is not ComboBox { SelectedItem: MovieSubtitleTrack track } ||
            _viewModel.SelectedSource is not { SelectedMovie: not null } source) return;
        if (source.SelectedMovie.SubtitleTrack == track.Index) return;
        source.SelectedMovie.SubtitleTrack = track.Index;
        CommitMoviePlaylist(source);
    }

    private void MoviePlayback_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingMoviePlaybackSelection || !ShouldApplyLive() ||
            sender is not ComboBox { SelectedItem: MoviePlaylistEntry movie } combo ||
            ResolveSourceContext(combo) is not { IsMoviePlaylist: true } source) return;
        StartPlaylistMovie(source, movie);
    }

    private void StartPlaylistMovie(LayerEditorSource source, MoviePlaylistEntry movie)
    {
        if (!_viewModel.LiveMode || !_owner.JumpToMovieFromEditor(source.Id, movie.Id, 0)) return;
        source.VideoPlaybackDurationSeconds = 0;
        source.MovieScrubSeconds = 0;
        RefreshSelectedVideoTransportState();
    }

    private void MovieStep_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSourceContext(sender) is not { IsMoviePlaylist: true, HasMovies: true } source ||
            sender is not Button button || !int.TryParse(button.Tag?.ToString(), out int direction)) return;
        _owner.TryGetMoviePlaylistState(source.Id, out var id, out _, out _);
        int index = source.MoviePlaylist.Movies.ToList().FindIndex(movie => movie.Id == id);
        int next = index < 0 ? 0 : (index + direction + source.MoviePlaylist.Movies.Count) % source.MoviePlaylist.Movies.Count;
        StartPlaylistMovie(source, source.MoviePlaylist.Movies[next]);
    }

    private void MovieList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is not ListBoxItem ||
            ResolveSourceContext(list) is not { IsMoviePlaylist: true, SelectedMovie: not null } source) return;
        StartPlaylistMovie(source, source.SelectedMovie);
        e.Handled = true;
    }

    private void BeginMovieScrub(object sender)
    {
        if (!_viewModel.LiveMode || ResolveSourceContext(sender) is not { IsMoviePlaylist: true } source ||
            !source.VideoSeekAvailable || _movieScrubSource != null) return;
        if (!_owner.TryGetMoviePlaylistState(source.Id, out var id, out _, out _) || id == Guid.Empty) return;
        _movieScrubSource = source;
        _movieScrubId = id;
    }

    private void CommitMovieScrub(object sender)
    {
        var source = _movieScrubSource;
        var movieId = _movieScrubId;
        _movieScrubSource = null;
        _movieScrubId = Guid.Empty;
        if (source == null || sender is not Slider slider || !_viewModel.LiveMode ||
            !ReferenceEquals(source, _viewModel.SelectedSource)) return;
        // Freeze the UI clock while dragging and reopen only once when the gesture ends.
        // Keep the entry ID from gesture start so an EOF transition cannot seek the next movie.
        double seconds = Math.Clamp(slider.Value, 0, source.VideoPlaybackDurationSeconds);
        _owner.JumpToMovieFromEditor(source.Id, movieId, seconds);
        source.MovieScrubSeconds = seconds;
        RefreshSelectedVideoTransportState();
    }

    private void MovieScrub_MouseDown(object sender, MouseButtonEventArgs e) => BeginMovieScrub(sender);
    private void MovieScrub_MouseUp(object sender, MouseButtonEventArgs e) => CommitMovieScrub(sender);
    private void MovieScrub_LostMouseCapture(object sender, MouseEventArgs e) => CommitMovieScrub(sender);
    private static bool IsMovieScrubKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;
    private void MovieScrub_KeyDown(object sender, KeyEventArgs e)
    {
        if (IsMovieScrubKey(e.Key)) BeginMovieScrub(sender);
    }
    private void MovieScrub_KeyUp(object sender, KeyEventArgs e)
    {
        if (IsMovieScrubKey(e.Key)) CommitMovieScrub(sender);
    }
    private void MovieScrub_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitMovieScrub(sender);

    private void AddRootMoviePlaylist_Click(object sender, RoutedEventArgs e) => AddSource(null, LayerEditorSourceKind.MoviePlaylist);
    private void AddChildMoviePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var parent = _viewModel.SelectedSource;
        if (parent?.IsGroup == true) AddSource(parent, LayerEditorSourceKind.MoviePlaylist);
    }

    private void MovieAdd_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSourceContext(sender) is not { IsMoviePlaylist: true } source) return;
        var dialog = new OpenFileDialog { Title = "Add movies to playlist", Multiselect = true,
            Filter = "Movies|*.mp4;*.mkv;*.mov;*.wmv;*.avi;*.webm;*.mpg;*.mpeg|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (string path in dialog.FileNames)
        {
            if (!FileCaptureService.IsVideoPath(path)) continue;
            string srt = Path.ChangeExtension(path, ".srt");
            var movie = new MoviePlaylistEntry { FilePath = path };
            if (File.Exists(srt)) { movie.SubtitleMode = "Srt"; movie.SubtitlePath = srt; }
            source.MoviePlaylist.Movies.Add(movie);
            source.SelectedMovie ??= movie;
        }
        CommitMoviePlaylist(source);
    }

    private void MovieRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSourceContext(sender) is not { IsMoviePlaylist: true, SelectedMovie: not null } source) return;
        int index = source.MoviePlaylist.Movies.IndexOf(source.SelectedMovie);
        source.MoviePlaylist.Movies.Remove(source.SelectedMovie);
        source.SelectedMovie = source.MoviePlaylist.Movies.ElementAtOrDefault(Math.Min(index, source.MoviePlaylist.Movies.Count - 1));
        CommitMoviePlaylist(source);
    }

    private void MovieMoveUp_Click(object sender, RoutedEventArgs e) => MoveMovie(sender, -1);
    private void MovieMoveDown_Click(object sender, RoutedEventArgs e) => MoveMovie(sender, 1);
    private void MoveMovie(object sender, int direction)
    {
        if (ResolveSourceContext(sender) is not { IsMoviePlaylist: true, SelectedMovie: not null } source) return;
        int index = source.MoviePlaylist.Movies.IndexOf(source.SelectedMovie);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= source.MoviePlaylist.Movies.Count) return;
        var selected = source.SelectedMovie;
        source.MoviePlaylist.Movies.Move(index, target);
        source.SelectedMovie = selected;
        CommitMoviePlaylist(source);
    }

    private void MovieSrt_Click(object sender, RoutedEventArgs e)
    {
        var source = _viewModel?.SelectedSource;
        if (source?.SelectedMovie == null) return;
        var dialog = new OpenFileDialog { Title = "Choose subtitles for " + source.SelectedMovie.DisplayName, Filter = "SubRip subtitles|*.srt" };
        if (dialog.ShowDialog(this) != true) return;
        source.SelectedMovie.SubtitlePath = dialog.FileName;
        source.SelectedMovie.SubtitleMode = "Srt";
        CommitMoviePlaylist(source);
    }

    private void MovieOptions_Changed(object sender, EventArgs e)
    {
        if (_suppressLiveUpdates || _loadingSubtitleChoices) return;
        var source = _viewModel?.SelectedSource;
        if (source?.IsMoviePlaylist == true) CommitMoviePlaylist(source);
    }

    private void CommitMoviePlaylist(LayerEditorSource source)
    {
        source.FilePaths.Clear();
        source.FilePaths.AddRange(source.MoviePlaylist.Movies.Select(movie => movie.FilePath));
        source.NotifyMoviePlaylistChanged();
        if (ShouldApplyLive()) _owner.UpdateMoviePlaylistFromEditor(source.Id, source.MoviePlaylist);
    }

    private void MovieJump_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSourceContext(sender) is not { IsMoviePlaylist: true, SelectedMovie: not null } source) return;
        if (!MoviePlaylistSettings.TryParseTime(source.MovieJumpTime, out double seconds))
        {
            MessageBox.Show(this, "Enter seconds, MM:SS, or HH:MM:SS (for example 01:23:45).", "Jump to movie", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!EnsureLiveModeForVideoTransport()) return;
        _owner.JumpToMovieFromEditor(source.Id, source.SelectedMovie.Id, seconds);
        RefreshSelectedVideoTransportState();
    }

    private void MovieJump_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        MovieJump_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void MovieUseCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSourceContext(sender) is not { IsMoviePlaylist: true } source) return;
        if (!_owner.TryGetMoviePlaylistState(source.Id, out var id, out double seconds, out _)) return;
        source.SelectedMovie = source.MoviePlaylist.Movies.FirstOrDefault(movie => movie.Id == id);
        source.MovieJumpTime = TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
    }

    private void RefreshMoviePlaylistTransport(LayerEditorSource source)
    {
        if (!_owner.TryGetMoviePlaylistState(source.Id, out var id, out double seconds, out string label)) return;
        source.CurrentMovieLabel = label;
        _updatingMoviePlaybackSelection = true;
        try
        {
            var playing = source.MoviePlaylist.Movies.FirstOrDefault(movie => movie.Id == id);
            if (source.PlayingMovie?.Id != playing?.Id) source.VideoPlaybackDurationSeconds = 0;
            source.PlayingMovie = playing;
            source.MovieScrubSeconds = seconds;
        }
        finally { _updatingMoviePlaybackSelection = false; }
        source.MoviePlaylist.BookmarkMovieId = id;
        source.MoviePlaylist.BookmarkSeconds = seconds;
    }

    private void RefreshMovieBookmarksForSave()
    {
        if (!_viewModel.LiveMode) return;
        void Visit(LayerEditorSource source)
        {
            if (source.IsMoviePlaylist) RefreshMoviePlaylistTransport(source);
            foreach (var child in source.Children) Visit(child);
        }
        foreach (var source in _viewModel.Sources) Visit(source);
    }
}
