using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace lifeviz;

public partial class MainWindow
{
    internal void RunMoviePlaylistChecks(string video, string srt, string directory)
    {
        static void Require(bool value, string message) => SmokeTestRunner.RequireSceneCheck(value, message);
        var first = new MoviePlaylistEntry { FilePath = video };
        var second = new MoviePlaylistEntry { FilePath = video, SubtitleMode = "Srt", SubtitlePath = srt };
        var settings = new MoviePlaylistSettings { ResumePlayback = true, BookmarkMovieId = second.Id, BookmarkSeconds = 1.25 };
        settings.Movies.Add(first);
        settings.Movies.Add(second);
        var source = CaptureSource.CreateMoviePlaylist(settings);
        _sources.Add(source);
        var session = source.VideoSequence!;
        session.SetOfflineRenderMode(true, 10);
        session.SetAudioMaster(false, 0);

        FileCaptureService.FileCaptureFrame NextFrame()
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 20)
            {
                var frame = session.CaptureFrame(320, 180, FitMode.Fit, true);
                if (frame.HasValue) return frame.Value;
                if (session.State == FileCaptureService.FileCaptureState.Error) throw new InvalidOperationException("Playlist decoder failed: " + session.SubtitleStatus);
                Thread.Sleep(10);
            }
            throw new TimeoutException("Playlist did not publish a frame.");
        }
        static bool HasCaption(FileCaptureService.FileCaptureFrame frame) =>
            Enumerable.Range(0, frame.OverlayDownscaled.Length / 4).Any(index => frame.OverlayDownscaled[index * 4] > 100);

        Require(session.GetBookmark().movieId == second.Id, "Resume did not select the saved movie before probing.");
        Require(!HasCaption(NextFrame()), "Seek displayed an early SRT caption at the wrong movie timestamp.");
        Require(session.TryGetPlaybackState(out var state) && state.PositionSeconds >= 1.25, "Resume lost its timestamp.");
        Require(session.JumpToMovie(second.Id, 2.25), "Could not jump to the second movie.");
        Require(HasCaption(NextFrame()), "External SRT was missing after a seek (or its punctuation path was misescaped).");
        Require(session.JumpToMovie(first.Id, 2.25), "Could not select embedded subtitles.");
        Require(HasCaption(NextFrame()) && session.SubtitleStatus.Contains("Embedded"), "Embedded subtitles were missing after a seek.");

        var running = session.GetBookmark();
        var reordered = settings.Clone();
        reordered.Movies.Move(0, 1);
        ApplyMoviePlaylistSettings(source, reordered);
        Require(session.GetBookmark().movieId == running.movieId && Math.Abs(session.GetBookmark().seconds - running.seconds) < 0.001,
            "Reordering changed the running movie or clock.");
        session.SetPlaybackPaused(true);
        double paused = session.GetBookmark().seconds;
        NextFrame();
        Require(Math.Abs(session.GetBookmark().seconds - paused) < 0.001, "Paused playlist advanced its media clock.");
        session.SetPlaybackPaused(false);

        var snapshot = BuildSourceConfigs(new List<CaptureSource> { source }).Single();
        Require(snapshot.MoviePlaylist!.BookmarkMovieId == first.Id && snapshot.MoviePlaylist.BookmarkSeconds >= 2.25, "Autosave lost the running bookmark.");
        var saved = JsonSerializer.Deserialize<AppConfig.SourceConfig>(JsonSerializer.Serialize(snapshot))!;
        var loaded = new List<CaptureSource>();
        RestoreSourceList(new[] { saved }, loaded, Array.Empty<WindowHandleInfo>(), Array.Empty<WebcamCaptureService.CameraInfo>());
        try
        {
            Require(loaded.Count == 1 && loaded[0].Type == CaptureSource.SourceType.MoviePlaylist &&
                loaded[0].VideoSequence!.GetBookmark().movieId == first.Id && loaded[0].VideoSequence!.GetBookmark().seconds >= 2.25,
                "Application autosave restore lost the playlist bookmark.");
        }
        finally { foreach (var restored in loaded) restored.DisposeVideoSequence(); }
        var models = BuildLayerEditorSources(new List<CaptureSource> { source }, null);
        var model = models.Single();
        var project = LayerConfigFile.FromEditorSources(models, Array.Empty<LayerEditorSimulationLayer>(), new LayerEditorProjectSettings());
        var restoredModel = LayerConfigFile.Parse(JsonSerializer.Serialize(project)).ToEditorSources().Single(item => item.IsMoviePlaylist);
        Require(restoredModel.IsMoviePlaylist && restoredModel.MoviePlaylist.Movies.Select(movie => movie.Id).SequenceEqual(reordered.Movies.Select(movie => movie.Id)),
            "Project export/import lost movie order or identity.");
        using (var reopened = new FileCaptureService.VideoSequenceSession(session.Paths, restoredModel.MoviePlaylist))
            Require(reopened.GetBookmark().movieId == first.Id && reopened.GetBookmark().seconds >= 2.25, "Project reopen lost the saved bookmark.");
        var noResume = restoredModel.MoviePlaylist.Clone();
        noResume.ResumePlayback = false;
        using (var reopened = new FileCaptureService.VideoSequenceSession(session.Paths, noResume))
            Require(reopened.GetBookmark().movieId == second.Id && reopened.GetBookmark().seconds == 0, "Resume off did not start at the beginning of the list.");

        var editor = new LayerEditorWindow(this);
        try { editor.ValidateMoviePlaylistControlsForSmoke(model, Path.Combine(directory, "playlist-editor.png"), () => { NextFrame(); }, CheckLayerReorder); }
        finally { editor.Close(); }

        void CheckLayerReorder(LayerEditorWindow activeEditor)
        {
            session.JumpToMovie(first.Id, 2.25);
            NextFrame();
            UpdateSourceVideoPlaybackPaused(source.Id, true);
            var before = session.GetBookmark();
            var otherLayer = CaptureSource.CreateColorPlane(0, 0, 0, "Reorder regression");
            _sources.Add(otherLayer);
            var originalEditor = _layerEditorWindow;
            try
            {
                _layerEditorWindow = activeEditor;
                // Reordering another source rebuilds all live editor models.
                MoveSource(otherLayer, -1);
                activeEditor.FlushMoviePlaylistBindingsForSmoke();
                var after = session.GetBookmark();
                Require(after.movieId == before.movieId && Math.Abs(after.seconds - before.seconds) < 0.001 && source.VideoPlaybackPaused,
                    "Moving another layer reset the movie, timestamp, or pause state.");
                MoveSource(source, -1);
                activeEditor.FlushMoviePlaylistBindingsForSmoke();
                after = session.GetBookmark();
                Require(after.movieId == before.movieId && Math.Abs(after.seconds - before.seconds) < 0.001 && source.VideoPlaybackPaused,
                    "Moving the playlist layer reset playback.");
            }
            finally
            {
                _layerEditorWindow = originalEditor;
                _sources.Remove(otherLayer);
                UpdateSourceVideoPlaybackPaused(source.Id, false);
            }
        }

        // Observe actual end-of-file transitions through two complete entries.
        session.Restart();
        var visited = new List<Guid>();
        for (int i = 0; i < 110 && visited.Count < 3; i++)
        {
            NextFrame();
            Guid id = session.GetBookmark().movieId;
            if (visited.Count == 0 || visited[^1] != id) visited.Add(id);
        }
        Require(visited.SequenceEqual(new[] { second.Id, first.Id, second.Id }), "Playlist did not loop through the authored order.");

        var missing = reordered.Clone();
        missing.Movies[0].SubtitlePath = Path.Combine(directory, "missing.srt");
        ApplyMoviePlaylistSettings(source, missing);
        session.JumpToMovie(second.Id, 2.25);
        Require(!HasCaption(NextFrame()) && session.SubtitleStatus.Contains("missing"), "Missing SRT should preserve movie playback.");
        missing.Movies[0].SubtitleMode = "Embedded";
        missing.Movies[0].SubtitleTrack = 4;
        ApplyMoviePlaylistSettings(source, missing);
        session.JumpToMovie(second.Id, 2.25);
        Require(!HasCaption(NextFrame()) && session.SubtitleStatus.Contains("No embedded"), "Unavailable embedded track should preserve movie playback.");
        string malformedSrt = Path.Combine(directory, "malformed.srt");
        File.WriteAllText(malformedSrt, "This is not a subtitle file.");
        missing.Movies[0].SubtitleMode = "Srt";
        missing.Movies[0].SubtitlePath = malformedSrt;
        ApplyMoviePlaylistSettings(source, missing);
        session.JumpToMovie(second.Id, 2.25);
        Require(!HasCaption(NextFrame()) && session.SubtitleStatus.Contains("failed"), "Malformed subtitles did not fall back to movie playback.");
        missing.Movies[0].SubtitleMode = "Off";
        ApplyMoviePlaylistSettings(source, missing);
        session.JumpToMovie(second.Id, 2.25);
        Require(!HasCaption(NextFrame()), "Subtitles Off still rendered captions.");

        var removed = settings.Clone();
        removed.Movies.RemoveAt(1); // Remove the active entry while its movie is playing.
        ApplyMoviePlaylistSettings(source, removed);
        Require(session.GetBookmark().movieId == first.Id && session.GetBookmark().seconds == 0, "Removing the active entry did not choose the surviving movie at zero.");
        NextFrame();
        var absentMovie = new MoviePlaylistEntry { FilePath = Path.Combine(directory, "missing.mkv") };
        removed.Movies.Insert(0, absentMovie);
        ApplyMoviePlaylistSettings(source, removed);
        session.Restart();
        NextFrame();
        Require(session.GetBookmark().movieId == first.Id, "An unavailable movie was not skipped in order.");
        var unavailable = new MoviePlaylistSettings();
        unavailable.Movies.Add(absentMovie);
        ApplyMoviePlaylistSettings(source, unavailable);
        session.SetOfflineRenderMode(false, 0);
        var failureClock = Stopwatch.StartNew();
        while (session.State != FileCaptureService.FileCaptureState.Error && failureClock.Elapsed.TotalSeconds < 20)
        {
            session.CaptureFrame(320, 180, FitMode.Fit, true);
            Thread.Sleep(10);
        }
        source.LastFrame = null;
        source.FirstFrameReceived = false;
        source.AddedUtc = DateTime.UtcNow.AddSeconds(-30);
        source.MissedFrames = 1000;
        var authored = new List<CaptureSource> { source };
        Require(!CaptureSourceList(authored, 0) && authored.Contains(source) && source.HasError,
            "An unavailable playlist erased its authored layer before receiving a frame.");
        session.SetOfflineRenderMode(true, 10);
        Require(MoviePlaylistSettings.TryParseTime("01:23:45.5", out double time) && time == 5025.5 &&
            !MoviePlaylistSettings.TryParseTime("00:99:00", out _) && !MoviePlaylistSettings.TryParseTime("NaN", out _), "Jump timestamp validation failed.");
        ApplyMoviePlaylistSettings(source, new());
        Require(session.Paths.Count == 0 && !session.CaptureFrame(320, 180, FitMode.Fit, true).HasValue, "Empty playlist retained a movie frame.");
        ApplyMoviePlaylistSettings(source, settings);
        session.JumpToMovie(first.Id, 0);
        Require(HasCaption(NextFrame()), "Empty playlist could not be repopulated.");
    }
}
