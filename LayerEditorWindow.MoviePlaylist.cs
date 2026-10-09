using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace lifeviz;

public partial class LayerEditorWindow
{
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
        if (_suppressLiveUpdates) return;
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
