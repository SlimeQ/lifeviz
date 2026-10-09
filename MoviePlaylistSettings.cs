using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace lifeviz;

internal sealed class MoviePlaylistEntry : LayerEditorNotify
{
    private string _subtitleMode = "Embedded";
    private string? _subtitlePath;
    private int _subtitleTrack;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = string.Empty;
    public string SubtitleMode { get => _subtitleMode; set => SetField(ref _subtitleMode, value); }
    public string? SubtitlePath { get => _subtitlePath; set => SetField(ref _subtitlePath, value); }
    public int SubtitleTrack { get => _subtitleTrack; set => SetField(ref _subtitleTrack, Math.Max(0, value)); }
    [JsonIgnore] public string DisplayName => Path.GetFileName(FilePath);
    [JsonIgnore] public LayerEditorOption[] SubtitleModes => Modes;
    private static readonly LayerEditorOption[] Modes = {
        new("Embedded", "Embedded text track"), new("Srt", "External SRT"), new("Off", "Off") };
    public MoviePlaylistEntry Clone() => new() { Id = Id, FilePath = FilePath, SubtitleMode = SubtitleMode, SubtitlePath = SubtitlePath, SubtitleTrack = SubtitleTrack };
}

internal sealed class MoviePlaylistSettings : LayerEditorNotify
{
    private bool _resumePlayback;
    public ObservableCollection<MoviePlaylistEntry> Movies { get; set; } = new();
    public bool ResumePlayback { get => _resumePlayback; set => SetField(ref _resumePlayback, value); }
    public Guid BookmarkMovieId { get; set; }
    public double BookmarkSeconds { get; set; }
    public MoviePlaylistSettings Clone() => new() {
        Movies = new(Movies.Select(movie => movie.Clone())), ResumePlayback = ResumePlayback,
        BookmarkMovieId = BookmarkMovieId, BookmarkSeconds = NormalizeSeconds(BookmarkSeconds) };
    internal static double NormalizeSeconds(double seconds) => double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;

    internal static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double part) ||
                !double.IsFinite(part) || part < 0 || (i > 0 && part >= 60) || (i < parts.Length - 1 && part != Math.Floor(part))) return false;
            seconds = seconds * 60 + part;
        }
        return double.IsFinite(seconds);
    }

    // FFmpeg filter options have two escaping levels (option and filtergraph).
    internal static string BuildSubtitleFilter(string path, int? track, double offsetSeconds)
    {
        string escaped = path.Replace('\\', '/').Replace("'", "\\'").Replace(":", "\\:");
        escaped = escaped.Replace("\\", "\\\\").Replace("'", "\\'").Replace(",", "\\,").Replace(";", "\\;").Replace("[", "\\[").Replace("]", "\\]");
        string filter = $"subtitles=filename={escaped}" + (track.HasValue ? $":si={track.Value}" : "");
        string offset = NormalizeSeconds(offsetSeconds).ToString("0.######", CultureInfo.InvariantCulture);
        return $"setpts=PTS+{offset}/TB,{filter},setpts=PTS-{offset}/TB";
    }

    internal static string[] ParseSubtitleCodecs(string output) => Regex.Matches(output,
        @"(?m)^[ \t]*Stream #0:[^\r\n]*?\bSubtitle:\s*([A-Za-z0-9_]+)")
        .Select(match => match.Groups[1].Value).ToArray();
    internal static bool IsTextSubtitle(string codec) => codec is "subrip" or "srt" or "ass" or "ssa" or "mov_text" or "webvtt" or "text";
}
