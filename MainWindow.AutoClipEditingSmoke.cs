using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace lifeviz;

public partial class MainWindow
{
    private void ValidateAutoClipEditingForSmoke(string first, string second)
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        string third = Path.Combine(Path.GetTempPath(), $"lifeviz-edit-{Guid.NewGuid():N}{Path.GetExtension(first)}");
        File.Copy(first, third);
        using var session = new FileCaptureService.AutoClipSession(new[] { first, second }, 0.2, 0.2, 0.2, 0.2);
        var group = CaptureSource.CreateGroup("Editing regression");
        var before = CaptureSource.CreateColorPlane(1, 2, 3);
        var after = CaptureSource.CreateColorPlane(4, 5, 6);
        var source = CaptureSource.CreateAutoClip(session, 0.2, 0.2, 0.2, 0.2);
        source.Enabled = false; // Drive the clock explicitly, without the UI render pump.
        source.AutoClipPlayInOrder = source.AutoClipPlayWholeFile = true;
        group.Children.AddRange(new[] { before, source, after });
        _sources.Add(group);
        session.SetPlaybackOptions(false, true, true);

        object? Read(string field) => typeof(FileCaptureService.AutoClipSession)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session);
        object?[] Snapshot() => new[] { Read("_current"), Read("_pendingClip"), Read("_phase"), Read("_phaseStartSeconds"), Read("_phaseEndSeconds"), Read("_offlineFrameIndex") };
        void CheckPreserved(object?[] snapshot)
        {
            Require(snapshot.SequenceEqual(Snapshot()), "AutoClip edit replaced decoder/preparation or changed the running phase clock.");
            Require(ReferenceEquals(source.AutoClip, session) && group.Children.SequenceEqual(new[] { before, source, after }),
                "AutoClip edit replaced the player or changed sibling order.");
        }
        void AdvanceTo(string path)
        {
            for (int i = 0; i < 180; i++)
                if (session.CaptureFrame(16, 16, FitMode.Stretch, false).HasValue && session.CurrentFramePath == path) return;
            throw new InvalidOperationException($"AutoClip did not advance to {Path.GetFileName(path)}.");
        }
        void Update(string[] paths, double clip = 0.2, double delay = 0.2) => UpdateAutoClipFromEditor(
            source.Id, paths, clip, clip, delay, delay, Array.Empty<LayerEditorAutoClipVideoOverride>());

        try
        {
            session.CaptureFrame(16, 16, FitMode.Stretch, false); // Queue the first live probe.
            var preparing = Snapshot();
            Require(Read("_pendingClip") != null, "Expected a preparing clip.");
            UpdateAutoClipPlaybackOptionsFromEditor(source.Id, false, true, false);
            Update(new[] { first, second }, 0.3);
            CheckPreserved(preparing);
            for (int i = 0; i < 200 && Read("_pendingClip") != null; i++)
            {
                session.CaptureFrame(16, 16, FitMode.Stretch, false);
                System.Threading.Thread.Sleep(10);
            }
            Require(session.GetCurrentPhaseRemainingForSmoke() > 0.8, "Preparing whole-file selection changed to an excerpt after editing options.");
            session.ResetSequence();
            source.AutoClipPlayWholeFile = true;
            session.SetPlaybackOptions(false, true, true);
            session.SetOfflineRenderMode(true, 30);
            AdvanceTo(second);
            var snapshot = Snapshot();
            Update(new[] { first, second, third }, 0.4);
            CheckPreserved(snapshot);
            UpdateAutoClipPlaybackOptionsFromEditor(source.Id, true, true, false);
            UpdateAutoClipLoopSelectedFileFromEditor(source.Id, true);
            CheckPreserved(snapshot);
            var model = BuildLayerEditorSources(new System.Collections.Generic.List<CaptureSource> { source }, null).Single();
            model.Opacity = 0.7;
            ApplySourceModel(source, model);
            CheckPreserved(snapshot);
            AdvanceTo(third); // Appending at the former wrap point must include the new file.

            snapshot = Snapshot();
            Update(new[] { third, second, first });
            CheckPreserved(snapshot);
            AdvanceTo(second);
            snapshot = Snapshot();
            Update(new[] { third, first }); // Removing the active file lets its current clip finish.
            CheckPreserved(snapshot);
            AdvanceTo(first);

            for (int i = 0; i < 90 && !session.IsDelaying; i++) session.CaptureFrame(16, 16, FitMode.Stretch, false);
            Require(session.IsDelaying, "Expected an inter-clip gap.");
            snapshot = Snapshot();
            Update(new[] { third, first }, 0.3, 0.1);
            CheckPreserved(snapshot);
            ResetAutoClipSequenceFromEditor(source.Id);
            Require(session.CurrentPath == null && session.GetOwnedVideoSessionCountForSmoke() == 0, "Reset retained an old clip.");
            Require(!session.CaptureFrame(16, 16, FitMode.Stretch, false).HasValue && session.IsDelaying, "Reset skipped initial delay.");
            AdvanceTo(third);
            Update(Array.Empty<string>());
            Require(session.IsEmpty && session.GetOwnedVideoSessionCountForSmoke() == 0 &&
                !session.CaptureFrame(16, 16, FitMode.Stretch, false).HasValue, "Empty playlist retained media.");
            Update(new[] { first });
            AdvanceTo(first);
            Logger.Info("AutoClip editing regression passed: decoder/clock continuity, append/reorder/remove, draft Apply, gap edits, reset, empty/repopulate, sibling order.");
        }
        finally
        {
            _sources.Remove(group);
            session.Dispose();
            MediaDisposalQueue.Drain(TimeSpan.FromSeconds(5));
            File.Delete(third);
        }
    }
}
