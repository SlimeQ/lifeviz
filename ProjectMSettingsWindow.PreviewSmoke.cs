using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;

namespace lifeviz;

internal sealed partial class ProjectMSettingsWindow
{
    internal void RunPreviewSmoke()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        byte[] Snapshot()
        {
            var pixels = new byte[PreviewWidth * PreviewHeight * 4];
            _previewBitmap!.CopyPixels(pixels, PreviewWidth * 4, 0); return pixels;
        }
        void Advance(int frames = 20)
        {
            double start = Math.Max(_previewClock.Elapsed.TotalSeconds, _previewLastTick) + 0.25;
            for (int frame = 0; frame < frames; frame++) TickPreview(start + frame / 15.0);
        }
        string original = JsonSerializer.Serialize(Result);
        string first = Result.Presets[0], second = Result.Presets[1];
        _library.SelectedItem = first;
        long framesBefore = _previewFrames;
        TickPreview(_previewSelectedAt + 0.05);
        Check(_previewFrames == framesBefore, "Preview selection debounce was bypassed.");
        Advance();
        Check(_previewImage.Source != null && !_previewFailed, $"Library preview failed: {_previewStatus.Text}");
        byte[] pixels = Snapshot(); Advance();
        Check(!pixels.SequenceEqual(Snapshot()), "Preset preview did not animate.");
        Check(Snapshot().Where((_, i) => i % 4 != 3).Any(b => b > 5), "Preview remained black.");
        Check(_playlist.SequenceEqual(Result.Presets), "Auditioning a library preset modified the playlist.");
        _library.SelectedItems.Add(second); Advance(1);
        Check(_previewPath == second, "Multi-selection did not audition the latest item.");
        _library.SelectedItems.Remove(second); Advance(1);
        Check(_previewPath == first, "Deselecting the audition did not preview the remaining selection.");
        _selected.SelectedItem = second; Advance();
        Check(_previewPath == second && _previewImage.Source != null, "Playlist selection did not update the preview.");
        _previewPaused.IsChecked = true; framesBefore = _previewFrames;
        double time = _previewTime; Advance();
        Check(_previewFrames == framesBefore && _previewTime == time, "Paused preview kept rendering or advancing.");
        _previewPaused.IsChecked = false; _previewDemo.IsChecked = false; Advance(2);
        Check(_previewPcm.All(p => p == 0.25f), "Preview did not use the selected audio input callback.");
        _previewDemo.IsChecked = true;
        // An absolute imported path uses the same audition path as bundled presets.
        string imported = ProjectMLibrary.Resolve(first);
        _playlist.Add(imported); _selected.SelectedItem = imported; Advance();
        Check(_previewPath == imported && !_previewFailed, "Imported preset preview failed.");
        string missing = Path.Combine(Path.GetTempPath(), "lifeviz-missing-preview-" + Guid.NewGuid().ToString("N") + ".milk");
        _playlist.Add(missing); _selected.SelectedItem = missing; Advance(1);
        Check(_previewFailed && _previewImage.Source == null && _previewStatus.Text.StartsWith("Preview unavailable:"), "Invalid preview left stale pixels or hid its error.");
        _selected.SelectedItem = second; Advance();
        Check(!_previewFailed && _previewImage.Source != null, "Preview did not recover after choosing a valid preset.");
        _playlist.Remove(imported); _playlist.Remove(missing);
        _search.Text = "no-match-" + Guid.NewGuid().ToString("N");
        Check(_previewPath == second, "Filtering the library cleared the selected playlist audition.");
        _selected.SelectedItem = null;
        Check(_previewPath == null && _previewImage.Source == null, "Empty selection left a stale preview.");
        _search.Clear(); _library.SelectedItem = first; _library.ScrollIntoView(first); Advance();
        Check(JsonSerializer.Serialize(Result) == original, "Preview changed saved settings before Save.");
        Logger.Info("projectM picker preview passed: selection, animation, debounce, pause, audio, imports, failure recovery and draft isolation.");
    }

    internal void ValidatePreviewClosedForSmoke()
    {
        long frames = _previewFrames;
        TickPreview(_previewLastTick + 1);
        if (!_previewClosed || _previewRenderer != null || _previewTimer.IsEnabled || _previewFrames != frames)
            throw new InvalidOperationException("Closing the preset picker did not release preview resources.");
    }

    internal void ValidatePreviewTimerForSmoke()
    {
        Show();
        // Let the Loaded event's real dispatcher timer animate a fresh selection.
        SelectPreview(Result.Presets[0]);
        long frames = _previewFrames;
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timeout.Tick += (_, _) => { timeout.Stop(); frame.Continue = false; };
        timeout.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        if (!_previewTimer.IsEnabled || _previewFrames <= frames)
            throw new InvalidOperationException("The visible preset picker did not animate through its dispatcher timer.");
    }
}
