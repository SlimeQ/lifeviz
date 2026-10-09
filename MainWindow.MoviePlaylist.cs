using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace lifeviz;

public partial class MainWindow
{
    private sealed partial class CaptureSource
    {
        public static CaptureSource CreateMoviePlaylist(MoviePlaylistSettings settings, FileCaptureService? fileCapture = null)
        {
            var source = new CaptureSource(SourceType.MoviePlaylist, null, null, null, "Movie Playlist", null, null)
            {
                MoviePlaylist = settings.Clone(), BlendMode = BlendMode.Normal,
                FitMode = lifeviz.FitMode.Fit, VideoAudioEnabled = true
            };
            var initial = source.MoviePlaylist.Clone();
            if (BackgroundBakeWorker.IsWorker) initial.ResumePlayback = false;
            source.SetVideoSequence(fileCapture?.CreateMoviePlaylist(initial) ?? new FileCaptureService.VideoSequenceSession(
                source.MoviePlaylist.Movies.Select(movie => movie.FilePath).ToArray(), initial));
            return source;
        }
    }

    private static MoviePlaylistSettings? SnapshotMoviePlaylist(CaptureSource source)
    {
        if (source.Type != CaptureSource.SourceType.MoviePlaylist) return null;
        var settings = source.MoviePlaylist.Clone();
        if (source.VideoSequence != null)
        {
            var bookmark = source.VideoSequence.GetBookmark();
            settings.BookmarkMovieId = bookmark.movieId;
            settings.BookmarkSeconds = bookmark.seconds;
        }
        return settings;
    }

    private static void ApplyMoviePlaylistSettings(CaptureSource source, MoviePlaylistSettings settings)
    {
        source.VideoSequence!.UpdatePlaylist(settings);
        source.MoviePlaylist = settings.Clone();
        source.FilePaths.Clear();
        source.FilePaths.AddRange(source.VideoSequence.Paths);
    }

    internal void AddMoviePlaylistFromEditor(Guid? parentId)
    {
        var parent = parentId.HasValue ? FindSource(source => source.Id == parentId.Value) : null;
        var target = parent?.Children ?? _sources;
        target.Add(CaptureSource.CreateMoviePlaylist(new(), _fileCapture));
        ApplySourceVideoAudioState(target[^1]);
        ApplySourceDecodeActivation();
        RenderFrame();
        SaveConfig();
        NotifyLayerEditorSourcesChanged();
    }

    private MenuItem BuildAddMoviePlaylistMenuItem(CaptureSource? parent)
    {
        var item = new MenuItem { Header = "Add Movie Playlist" };
        item.Click += (_, _) => AddMoviePlaylistFromEditor(parent?.Id);
        return item;
    }

    internal void UpdateMoviePlaylistFromEditor(Guid sourceId, MoviePlaylistSettings settings)
    {
        var source = FindSource(item => item.Id == sourceId);
        if (source?.Type != CaptureSource.SourceType.MoviePlaylist) return;
        ApplyMoviePlaylistSettings(source, settings);
        ApplySourceDecodeActivation();
        source.LastFrame = null;
        RenderFrame();
        SaveConfig();
    }

    internal bool JumpToMovieFromEditor(Guid sourceId, Guid movieId, double seconds)
    {
        var source = FindSource(item => item.Id == sourceId);
        if (source?.Type != CaptureSource.SourceType.MoviePlaylist || source.VideoSequence?.JumpToMovie(movieId, seconds) != true) return false;
        source.LastFrame = null;
        ApplySourceDecodeActivation();
        RenderFrame();
        SaveConfig();
        return true;
    }

    internal bool TryGetMoviePlaylistState(Guid sourceId, out Guid movieId, out double seconds, out string label)
    {
        movieId = Guid.Empty;
        seconds = 0;
        label = "Empty playlist";
        var source = FindSource(item => item.Id == sourceId);
        if (source?.Type != CaptureSource.SourceType.MoviePlaylist || source.VideoSequence == null) return false;
        if (source.VideoSequence.State == FileCaptureService.FileCaptureState.Error)
        {
            label = "Playlist error: no playable movies. Check file paths or restart the sequence.";
            (movieId, seconds) = source.VideoSequence.GetBookmark();
            return true;
        }
        (movieId, seconds) = source.VideoSequence.GetBookmark();
        Guid currentId = movieId;
        var movie = source.MoviePlaylist.Movies.FirstOrDefault(item => item.Id == currentId);
        if (movie != null)
        {
            string activity = source.VideoPlaybackPaused ? "Paused" : source.VideoSequence.State == FileCaptureService.FileCaptureState.Pending ? "Preparing" : "Playing";
            label = $"{activity} {source.MoviePlaylist.Movies.IndexOf(movie) + 1}/{source.MoviePlaylist.Movies.Count}: {movie.DisplayName}\n{source.VideoSequence.AudioStatus}\n{source.VideoSequence.SubtitleStatus}";
        }
        return true;
    }
}
