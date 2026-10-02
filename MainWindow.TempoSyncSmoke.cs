using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace lifeviz;

public partial class MainWindow
{
    /// <summary>
    /// Whole-app tempo sync check: a File layer with Sync to Beat follows the window's
    /// own beat clock (Animation BPM) through the real adapter, frame-exactly in offline
    /// mode, across a downbeat resync, and persists/turns off cleanly.
    /// </summary>
    internal void RunTempoSyncIntegrationChecks(string directory, string loopPath, int loopFrames)
    {
        if (!App.IsSmokeTestMode) throw new InvalidOperationException("Tempo sync checks require smoke mode.");
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        _scenePersistenceTestPath = Path.Combine(directory, "session.json");
        _configReady = true;
        _animationBpm = 120;
        _animationAudioSyncEnabled = false;
        _sceneLoopBpm = 120;

        AddFileSourceFromEditor(loopPath, null);
        CaptureSource source = _sources.Last();
        Require(SupportsTempoSync(source), "A video File layer must offer Sync to Beat.");
        SetSourceTempoSync(source, true);

        _fileCapture.BeginOfflineRender(30, 0);
        ResetBeatClocks(0);
        int checkedFrames = 0;
        bool resynced = false;
        for (int step = 0; step < 90; step++)
        {
            double now = step * 0.137;
            UpdateBeatClocks(now);
            if (step == 45)
            {
                ResyncDownbeat();
                resynced = true;
            }

            // 120 BPM manual clock: 2 beats per second, minus the resync offset.
            double expectedPosition = now * 2.0 - (resynced ? Math.Round(45 * 0.137 * 2.0) : 0);
            double adapterPosition = FileCaptureService.TempoClock!.BeatPosition;
            Require(Math.Abs(adapterPosition - expectedPosition) < 1e-6,
                $"Tempo clock adapter reported beat {adapterPosition:F4}, expected {expectedPosition:F4}.");
            int loopBeats = TempoLoopMath.ResolveLoopBeats(loopFrames, 24, 120, 0);
            int expected = TempoLoopMath.LocalFrame(TempoLoopMath.TargetAbsoluteFrame(expectedPosition, loopBeats, loopFrames), loopFrames);
            var frame = CaptureFileLayerFrame(source, 32, 18, includeSource: false);
            Require(frame.HasValue, $"No synced frame at beat {expectedPosition:F2}.");
            int center = (9 * 32 + 16) * 4;
            int actual = (int)Math.Round(frame!.Value.OverlayDownscaled[center + 2] / 4.0);
            Require(actual == expected, $"Synced File layer showed frame {actual} at beat {expectedPosition:F2}; expected {expected}.");
            checkedFrames++;
        }

        Require(_fileCapture.IsTempoLayerActive(source.Id) && !_fileCapture.HasSession(loopPath),
            "A synced layer must use its own player and release the unused real-time decoder.");

        // A second, unsynced layer on the same file (scenes can hold duplicates even
        // though Add File promotes an existing one) keeps ordinary playback beside it.
        CaptureSource plain = CaptureSource.CreateFile(source.FilePath!, source.DisplayName, 64, 36);
        _sources.Add(plain);
        Require(!ReferenceEquals(plain, source) && !plain.TempoSyncEnabled, "Second layer was not added.");
        FileCaptureService.FileCaptureFrame? plainFrame = null;
        for (int attempt = 0; attempt < 200 && !plainFrame.HasValue; attempt++)
        {
            plainFrame = CaptureFileLayerFrame(plain, 32, 18, includeSource: false);
            if (!plainFrame.HasValue) System.Threading.Thread.Sleep(25);
        }

        UpdateBeatClocks(90 * 0.137);
        var syncedFrame = CaptureFileLayerFrame(source, 32, 18, includeSource: false);
        Require(plainFrame.HasValue && syncedFrame.HasValue && _fileCapture.HasSession(loopPath) &&
                _fileCapture.IsTempoLayerActive(source.Id) && !_fileCapture.IsTempoLayerActive(plain.Id),
            "A synced and an unsynced layer on one file must each keep their own playback.");

        // Syncing the second layer too frees the shared real-time decoder.
        SetSourceTempoSync(plain, true);
        CaptureFileLayerFrame(plain, 32, 18, includeSource: false);
        Require(!_fileCapture.HasSession(loopPath) && _fileCapture.IsTempoLayerActive(plain.Id),
            "Real-time decoder kept running with every layer on the file synced.");
        SetSourceTempoSync(plain, false);
        RemoveSourceFromEditor(plain.Id);

        _fileCapture.EndOfflineRender();
        var scene = JsonNode.Parse(SerializeCurrentConfig())!.AsObject();
        var savedSource = scene["Sources"]!.AsArray().Last()!.AsObject();
        Require(savedSource["TempoSyncEnabled"]!.GetValue<bool>() && Math.Abs(scene["LoopBpm"]!.GetValue<double>() - 120) < 1e-9,
            "Scene config did not save Sync to Beat / Video loop BPM.");

        SetSourceTempoSync(source, false);
        Require(!_fileCapture.IsTempoLayerActive(source.Id) && _fileCapture.HasSession(loopPath) &&
                _fileCapture.GetKind(loopPath) == FileCaptureService.FileSourceKind.Video,
            "Turning Sync to Beat off did not restore ordinary playback.");
        Logger.Info($"Tempo sync integration: {checkedFrames} beat-locked frames exact through the window clock (incl. downbeat resync); persistence and switch-off OK.");
    }
}
