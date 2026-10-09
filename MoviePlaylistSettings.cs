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
    private int _audioTrack;
    private double _subtitleDelaySeconds;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = string.Empty;
    public string SubtitleMode { get => _subtitleMode; set => SetField(ref _subtitleMode, value); }
    public string? SubtitlePath { get => _subtitlePath; set => SetField(ref _subtitlePath, value); }
    public int SubtitleTrack { get => _subtitleTrack; set => SetField(ref _subtitleTrack, Math.Max(0, value)); }
    public double SubtitleDelaySeconds
    {
        get => _subtitleDelaySeconds;
        set
        {
            if (SetField(ref _subtitleDelaySeconds, MoviePlaylistSettings.NormalizeSubtitleDelay(value)))
                OnPropertyChanged(nameof(SubtitleDelayLabel));
        }
    }
    [JsonIgnore] public string SubtitleDelayLabel => SubtitleDelaySeconds == 0 ? "0.0 s" :
        SubtitleDelaySeconds.ToString("+0.0##;-0.0##", CultureInfo.CurrentCulture) + " s (" + (SubtitleDelaySeconds > 0 ? "later" : "earlier") + ")";
    public int AudioTrack
    {
        get => _audioTrack;
        set { SetField(ref _audioTrack, Math.Max(0, value)); OnPropertyChanged(nameof(AudioTrackStatus)); }
    }
    [JsonIgnore] public string DisplayName => Path.GetFileName(FilePath);
    public override string ToString() => DisplayName;
    [JsonIgnore] public LayerEditorOption[] SubtitleModes => Modes;
    [JsonIgnore] public MovieSubtitleTrack[] EmbeddedSubtitleTracks { get; private set; } = Array.Empty<MovieSubtitleTrack>();
    [JsonIgnore] public string EmbeddedSubtitleStatus { get; private set; } = "Reading embedded subtitle tracks...";
    [JsonIgnore] public bool MovieTracksLoaded { get; private set; }
    [JsonIgnore] public bool MovieTracksLoading { get; set; }
    [JsonIgnore] public MovieAudioTrack[] AudioTracks { get; private set; } = Array.Empty<MovieAudioTrack>();
    private bool _audioTracksLoaded;
    private string _audioMetadataStatus = "Reading audio tracks...";
    [JsonIgnore] public string AudioTrackStatus => !_audioTracksLoaded ? _audioMetadataStatus :
        AudioTracks.Length == 0 ? "No audio tracks in this movie." :
        AudioTrack >= AudioTracks.Length ? "Saved audio track unavailable; playback uses the first track." : $"{AudioTracks.Length} audio track(s).";
    internal void SetAudioTracks(MovieAudioTrack[]? tracks)
    {
        AudioTracks = tracks ?? Array.Empty<MovieAudioTrack>();
        _audioTracksLoaded = tracks != null;
        _audioMetadataStatus = "Could not read audio tracks. Check the file path and refresh tracks to retry.";
        OnPropertyChanged(nameof(AudioTracks));
        OnPropertyChanged(nameof(AudioTrack));
        OnPropertyChanged(nameof(AudioTrackStatus));
    }
    internal void SetSubtitleTracks(MovieSubtitleTrack[]? tracks)
    {
        EmbeddedSubtitleTracks = tracks ?? Array.Empty<MovieSubtitleTrack>();
        MovieTracksLoaded = tracks != null;
        EmbeddedSubtitleStatus = tracks == null ? "Could not read movie metadata. Check the file path and retry." :
            tracks.Length == 0 ? "No embedded subtitle tracks. Choose an external SRT or Off." :
            $"{tracks.Length} embedded subtitle track(s).";
        OnPropertyChanged(nameof(EmbeddedSubtitleTracks));
        OnPropertyChanged(nameof(SubtitleTrack));
        OnPropertyChanged(nameof(EmbeddedSubtitleStatus));
    }
    private static readonly LayerEditorOption[] Modes = {
        new("Embedded", "Embedded text track"), new("Srt", "External SRT"), new("Off", "Off") };
    public MoviePlaylistEntry Clone() => new() { Id = Id, FilePath = FilePath, SubtitleMode = SubtitleMode, SubtitlePath = SubtitlePath, SubtitleTrack = SubtitleTrack, AudioTrack = AudioTrack, SubtitleDelaySeconds = SubtitleDelaySeconds };
}

internal sealed record MovieAudioTrack(int Index, int StreamIndex, string Codec, string Language, string Title, bool IsDefault, string Channels)
{
    public override string ToString() => Label;
    public string Label => $"{Index + 1}. {MoviePlaylistSettings.TrackLanguageLabel(Language)}" +
        (string.IsNullOrWhiteSpace(Title) ? "" : $" — {Title}") +
        $" ({Codec}" + (string.IsNullOrWhiteSpace(Channels) ? "" : $", {Channels}") + (IsDefault ? ", default" : "") + ")";
}

internal sealed record MovieSubtitleTrack(int Index, int StreamIndex, string Codec, string Language, string Title, bool IsDefault, bool IsForced)
{
    public override string ToString() => Label;
    public string Label
    {
        get
        {
            string language = MoviePlaylistSettings.TrackLanguageLabel(Language);
            string details = Codec + (IsDefault ? ", default" : "") + (IsForced ? ", forced" : "") +
                (MoviePlaylistSettings.IsTextSubtitle(Codec) ? "" : ", bitmap/unsupported — use SRT");
            return $"{Index + 1}. {language}" + (string.IsNullOrWhiteSpace(Title) ? "" : $" — {Title}") + $" ({details})";
        }
    }
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
    internal static double NormalizeSubtitleDelay(double seconds) => double.IsFinite(seconds) ? Math.Round(Math.Clamp(seconds, -86400, 86400), 3) : 0;

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
    internal static string BuildSubtitleClockExpression(double offsetSeconds, double delaySeconds) =>
        "PTS+(" + (NormalizeSeconds(offsetSeconds) - NormalizeSubtitleDelay(delaySeconds)).ToString("0.######", CultureInfo.InvariantCulture) + ")/TB";

    internal static string BuildSubtitleFilter(string path, int? track, double offsetSeconds, double delaySeconds)
    {
        string escaped = path.Replace('\\', '/').Replace("'", "\\'").Replace(":", "\\:");
        escaped = escaped.Replace("\\", "\\\\").Replace("'", "\\'").Replace(",", "\\,").Replace(";", "\\;").Replace("[", "\\[").Replace("]", "\\]");
        string filter = $"subtitles=filename={escaped}" + (track.HasValue ? $":si={track.Value}" : "");
        // The fps filter establishes a constant cadence. Restore seconds at that
        // cadence before final pacing, regardless of the link's timestamp units.
        return $"setpts@lifeviz_subtitles={BuildSubtitleClockExpression(offsetSeconds, delaySeconds)},{filter},setpts=N/(FRAME_RATE*TB)";
    }

    internal static MovieSubtitleTrack[] ParseSubtitleTracks(string output)
    {
        var streams = Regex.Matches(output, @"(?m)^[ \t]*Stream #0:[^\r\n]*");
        var tracks = new Collection<MovieSubtitleTrack>();
        for (int i = 0; i < streams.Count; i++)
        {
            var stream = streams[i];
            var subtitle = Regex.Match(stream.Value,
                @"Stream #0:(?<stream>\d+)(?:\[[^\]\r\n]*\])?(?:\((?<language>[^)\r\n]*)\))?[^\r\n]*?\bSubtitle:\s*(?<codec>[A-Za-z0-9_]+)");
            if (!subtitle.Success) continue;
            int end = i + 1 < streams.Count ? streams[i + 1].Index : output.Length;
            string block = output.Substring(stream.Index, end - stream.Index);
            var title = Regex.Match(block, @"(?m)^[ \t]*title[ \t]*:[ \t]*(?<title>[^\r\n]+)", RegexOptions.IgnoreCase);
            if (!title.Success) title = Regex.Match(block, @"(?m)^[ \t]*handler_name[ \t]*:[ \t]*(?<title>[^\r\n]+)", RegexOptions.IgnoreCase);
            tracks.Add(new MovieSubtitleTrack(tracks.Count, int.Parse(subtitle.Groups["stream"].Value, CultureInfo.InvariantCulture),
                subtitle.Groups["codec"].Value, subtitle.Groups["language"].Value,
                title.Groups["title"].Value.Trim(), stream.Value.Contains("(default)", StringComparison.Ordinal),
                stream.Value.Contains("(forced)", StringComparison.Ordinal)));
        }
        return tracks.ToArray();
    }
    internal static string TrackLanguageLabel(string language)
    {
        var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c =>
            !string.IsNullOrEmpty(c.Name) && (c.ThreeLetterISOLanguageName == language || c.TwoLetterISOLanguageName == language));
        return culture?.EnglishName ?? (string.IsNullOrWhiteSpace(language) || language == "und" ? "Unknown language" : language);
    }

    internal static MovieAudioTrack[] ParseAudioTracks(string output)
    {
        var streams = Regex.Matches(output, @"(?m)^[ \t]*Stream #0:[^\r\n]*");
        var tracks = new Collection<MovieAudioTrack>();
        for (int i = 0; i < streams.Count; i++)
        {
            var stream = streams[i];
            var audio = Regex.Match(stream.Value,
                @"Stream #0:(?<stream>\d+)(?:\[[^\]\r\n]*\])?(?:\((?<language>[^)\r\n]*)\))?[^\r\n]*?\bAudio:\s*(?<codec>[A-Za-z0-9_]+)");
            if (!audio.Success) continue;
            int end = i + 1 < streams.Count ? streams[i + 1].Index : output.Length;
            string block = output.Substring(stream.Index, end - stream.Index);
            var title = Regex.Match(block, @"(?m)^[ \t]*title[ \t]*:[ \t]*(?<title>[^\r\n]+)", RegexOptions.IgnoreCase);
            if (!title.Success) title = Regex.Match(block, @"(?m)^[ \t]*handler_name[ \t]*:[ \t]*(?<title>[^\r\n]+)", RegexOptions.IgnoreCase);
            var channels = Regex.Match(stream.Value, @"\d+ Hz,\s*(?<channels>[^,\r\n]+)");
            tracks.Add(new MovieAudioTrack(tracks.Count, int.Parse(audio.Groups["stream"].Value, CultureInfo.InvariantCulture),
                audio.Groups["codec"].Value, audio.Groups["language"].Value, title.Groups["title"].Value.Trim(),
                stream.Value.Contains("(default)", StringComparison.Ordinal), channels.Groups["channels"].Value.Trim()));
        }
        return tracks.ToArray();
    }
    internal static bool IsTextSubtitle(string codec) => codec is "subrip" or "srt" or "ass" or "ssa" or "mov_text" or "webvtt" or "text";
}
