using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace lifeviz;

// Saved layers whose input is unavailable (file moved, window not open, webcam unplugged) are
// kept here verbatim and written back into every autosave at their original position. Before
// this, one missing input paused autosave for the entire session, so hours of edits could be
// lost on the next restart without any visible sign.
public partial class MainWindow
{
    private sealed class MissingSource(CaptureSource? parent, CaptureSource? after, AppConfig.SourceConfig config)
    {
        public CaptureSource? Parent { get; } = parent;
        public CaptureSource? After { get; } = after;
        public AppConfig.SourceConfig Config { get; } = config;
        public string Description { get; } = DescribeMissingSource(config);
    }

    private const int BlockedSceneRecoveryLimit = 10;
    private readonly List<MissingSource> _missingSources = new();
    private int _missingSourcesReported;
    private readonly object _blockedRecoveryLock = new();
    private string? _blockedRecoveryJson;
    private Task? _blockedRecoveryWriter;
    private string? _blockedRecoveryPath;

    private bool IsAutosaveBlocked => _configLoadBlocked || _configConflict;

    internal int MissingInputCount
    {
        get
        {
            PruneOrphanedMissingSources();
            return _missingSources.Count;
        }
    }

    private string MissingInputsStatusSuffix => MissingInputCount switch
    {
        0 => "",
        1 => " · 1 missing input kept",
        int n => $" · {n} missing inputs kept"
    };

    private void PreserveMissingSource(CaptureSource? parent, CaptureSource? after, AppConfig.SourceConfig config)
    {
        var missing = new MissingSource(parent, after, config);
        _missingSources.Add(missing);
        Logger.Warn($"Saved input unavailable; keeping it in the saved scene: {missing.Description}");
    }

    private void PreserveAutomaticallyRemovedSources(List<CaptureSource> siblings, List<CaptureSource> removed)
    {
        if (BackgroundBakeWorker.IsWorker || _isOfflineRendering) return;
        CaptureSource? parent = EnumerateSources(_sources).FirstOrDefault(source => ReferenceEquals(source.Children, siblings));
        if (parent == null && !ReferenceEquals(siblings, _sources)) return;
        var removing = new HashSet<CaptureSource>(removed);
        CaptureSource? after = null;
        foreach (var source in siblings)
        {
            if (!removing.Contains(source))
            {
                after = source;
                continue;
            }

            // Only keep inputs that went away (closed window, unplugged camera, deleted/moved file).
            // Media that is present but undecodable is still pruned, as before.
            if (!IsUnavailableInput(source)) continue;
            var config = BuildSourceConfigs(new List<CaptureSource> { source })[0];
            var missing = new MissingSource(parent, after, config);
            _missingSources.Add(missing);
            Logger.Warn($"Input stopped responding; detached it but kept it in the saved scene: {missing.Description}");
        }

        SaveConfig();
    }

    private static bool IsUnavailableInput(CaptureSource source) => source.Type switch
    {
        CaptureSource.SourceType.Window or CaptureSource.SourceType.Webcam => true,
        CaptureSource.SourceType.File => !string.IsNullOrWhiteSpace(source.FilePath) &&
            (NormalizeYoutubeKey(source.FilePath) != null || !File.Exists(source.FilePath)),
        CaptureSource.SourceType.VideoSequence or CaptureSource.SourceType.AutoClip =>
            source.FilePaths.Count > 0 && source.FilePaths.All(path => !File.Exists(path)),
        _ => false
    };

    private void ReportMissingInputsAfterRestore()
    {
        int count = _missingSources.Count;
        if (count == _missingSourcesReported) return;
        _missingSourcesReported = count;
        string list = string.Join("\n", _missingSources.Take(12).Select(missing => "• " + missing.Description)) +
            (count > 12 ? $"\n• …and {count - 12} more" : "");
        Logger.Warn($"Saved inputs unavailable at startup ({count}): {list.Replace('\n', ' ')}");
        if (App.SuppressErrorDialogs || App.IsSmokeTestMode || App.IsDiagnosticTestMode || BackgroundBakeWorker.IsWorker) return;
        // These layers are not visible in the Scene Editor, so ask once instead of warning on every
        // launch about something the user cannot find or delete there.
        string message = $"LifeViz could not find {(count == 1 ? "this input" : $"these {count} inputs")} from your saved scene:\n\n{list}\n\n" +
            "Remove them from the scene?\n\n" +
            "Yes: remove them for good (the current scene file stays in history).\n" +
            "No: keep them hidden in the saved scene; they load again once the file, window or camera is back. " +
            "You can remove them later with Forget Missing Inputs in the right-click menu.";
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isShuttingDown || _missingSources.Count == 0) return;
            if (MessageBox.Show(this, message, "Missing inputs", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                ForgetMissingInputs();
        }));
    }

    private void ShowSceneNotice(string message, string title = "LifeViz scene")
    {
        if (App.SuppressErrorDialogs || App.IsSmokeTestMode || App.IsDiagnosticTestMode || BackgroundBakeWorker.IsWorker) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isShuttingDown)
                MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }));
    }

    private List<AppConfig.SourceConfig> BuildSourceConfigsWithMissingInputs()
    {
        PruneOrphanedMissingSources();
        return BuildSourceConfigs(_sources, parent: null, includeMissingInputs: true);
    }

    // Emits the kept layers that belong right after `after` in `parent`'s list. Called once with
    // after == null before the first sibling; that call also picks up layers whose anchor was
    // deleted or moved elsewhere, so each kept layer is written exactly once.
    private void AppendMissingSourceConfigs(List<AppConfig.SourceConfig> configs, CaptureSource? parent,
        List<CaptureSource> siblings, CaptureSource? after)
    {
        foreach (var missing in _missingSources)
        {
            if (!ReferenceEquals(missing.Parent, parent)) continue;
            bool belongsHere = after == null
                ? missing.After == null || !siblings.Contains(missing.After)
                : ReferenceEquals(missing.After, after);
            if (belongsHere) configs.Add(missing.Config);
        }
    }

    // A kept layer inside a group the user has since deleted goes with that group.
    private void PruneOrphanedMissingSources()
    {
        if (_missingSources.Count == 0 || _missingSources.All(missing => missing.Parent == null)) return;
        var live = new HashSet<CaptureSource>(EnumerateSources(_sources));
        _missingSources.RemoveAll(missing => missing.Parent != null && !live.Contains(missing.Parent));
    }

    private void UpdateForgetMissingInputsMenuItem()
    {
        int count = MissingInputCount;
        ForgetMissingInputsMenuItem.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ForgetMissingInputsMenuItem.Header = count == 1 ? "Forget 1 Missing Input..." : $"Forget {count} Missing Inputs...";
        ForgetMissingInputsMenuItem.ToolTip = count > 0 ? string.Join("\n", _missingSources.Select(missing => missing.Description)) : null;
    }

    private void ForgetMissingInputs_Click(object sender, RoutedEventArgs e)
    {
        int count = MissingInputCount;
        if (count == 0) return;
        string list = string.Join("\n", _missingSources.Take(12).Select(missing => "• " + missing.Description)) +
            (count > 12 ? $"\n• …and {count - 12} more" : "");
        var answer = MessageBox.Show(this,
            $"Remove these layers from the saved scene?\n\n{list}\n\nThe current scene file is kept in scene history, so Recover... can still bring them back.",
            "Forget missing inputs", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        ForgetMissingInputs();
    }

    internal void ForgetMissingInputs()
    {
        foreach (var missing in _missingSources) Logger.Info($"Forgot missing input: {missing.Description}");
        _missingSources.Clear();
        _missingSourcesReported = 0;
        SaveConfig();
    }

    private static string DescribeMissingSource(AppConfig.SourceConfig config)
    {
        string kind = config.Type switch
        {
            nameof(CaptureSource.SourceType.Window) => "window",
            nameof(CaptureSource.SourceType.Webcam) => "webcam",
            nameof(CaptureSource.SourceType.VideoSequence) => "video sequence",
            nameof(CaptureSource.SourceType.AutoClip) => "AutoClip",
            _ => "file"
        };
        string detail = config.Type switch
        {
            nameof(CaptureSource.SourceType.Window) => config.WindowTitle ?? config.DisplayName ?? "(untitled)",
            nameof(CaptureSource.SourceType.Webcam) => config.DisplayName ?? config.WebcamId ?? "(unknown camera)",
            nameof(CaptureSource.SourceType.VideoSequence) or nameof(CaptureSource.SourceType.AutoClip) =>
                $"{config.DisplayName ?? "(unnamed)"} ({config.FilePaths.Count} files)",
            _ => config.FilePath ?? config.DisplayName ?? "(no path)"
        };
        return $"{detail} ({kind})";
    }

    // --- Recovery copy while config.json itself must not be overwritten ------------------------

    private string BlockedSceneRecoveryPath => _blockedRecoveryPath ??= Path.Combine(SceneRecoveryDirectory,
        $"autosave-paused-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{_sceneSessionId[..8]}.json");

    private void QueueBlockedSceneRecoveryWrite()
    {
        string json;
        try { json = SerializeCurrentConfig(); }
        catch (Exception ex)
        {
            Logger.Warn($"Could not prepare the scene recovery copy. {ex.Message}");
            return;
        }

        string path = BlockedSceneRecoveryPath;
        lock (_blockedRecoveryLock)
        {
            _blockedRecoveryJson = json;
            if (_blockedRecoveryWriter != null) return;
            _blockedRecoveryWriter = Task.Run(() => ProcessBlockedSceneRecoveryWrites(path));
        }
    }

    private void ProcessBlockedSceneRecoveryWrites(string path)
    {
        while (true)
        {
            string? json;
            lock (_blockedRecoveryLock)
            {
                json = _blockedRecoveryJson;
                _blockedRecoveryJson = null;
                if (json == null)
                {
                    _blockedRecoveryWriter = null;
                    return;
                }
            }

            WriteBlockedSceneRecovery(path, json);
        }
    }

    private void WriteBlockedSceneRecoveryNow()
    {
        Task? writer;
        lock (_blockedRecoveryLock) writer = _blockedRecoveryWriter;
        try { writer?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }

        try { WriteBlockedSceneRecovery(BlockedSceneRecoveryPath, SerializeCurrentConfig()); }
        catch (Exception ex) { Logger.Error("Could not write the final scene recovery copy.", ex); }
    }

    private static readonly object BlockedRecoveryFileLock = new();

    private static void WriteBlockedSceneRecovery(string path, string json)
    {
        try
        {
            lock (BlockedRecoveryFileLock)
            {
                string directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(new UTF8Encoding(false).GetBytes(json));
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
                foreach (string old in Directory.GetFiles(directory, "autosave-paused-*.json")
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(BlockedSceneRecoveryLimit))
                    File.Delete(old);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not write the scene recovery copy to {path}. {ex.Message}");
        }
    }

    // A previous session that could not autosave left its latest scene in a recovery file.
    private void NotifyNewerSceneRecoveryFile()
    {
        try
        {
            string directory = SceneRecoveryDirectory;
            if (!Directory.Exists(directory)) return;
            DateTime saved = File.Exists(ConfigPath) ? File.GetLastWriteTimeUtc(ConfigPath) : DateTime.MinValue;
            var newest = new DirectoryInfo(directory).GetFiles("autosave-paused-*.json")
                .Where(file => file.LastWriteTimeUtc > saved)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null) return;
            Logger.Warn($"A previous session could not autosave; its latest scene is at {newest.FullName}.");
            ShowSceneNotice(
                $"A previous LifeViz session could not autosave, so its latest scene was kept separately " +
                $"({newest.LastWriteTime:g}).\n\nTo restore it, open the Scene Editor, choose Recover..., and pick\n{newest.Name}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not check for scene recovery files. {ex.Message}");
        }
    }
}
