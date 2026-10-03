using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace lifeviz;

/// <summary>
/// Live tempo and audio-reactivity readout in the Scene Editor header, so the beat
/// clock and the values reactive mappings receive are visible while the visual output
/// is on another screen.
/// </summary>
public partial class LayerEditorWindow
{
    private const double MonitorMeterWidth = 28;
    private static readonly Brush MonitorBeatOff = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly Brush MonitorBeatOn = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush MonitorDownbeatOn = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47));
    private DispatcherTimer? _audioMonitorTimer;
    private bool _audioMonitorRegistered;
    private long _audioMonitorLastOnsets = -1;
    private double _audioMonitorHit;
    private readonly double[] _audioMonitorPeaks = new double[4];

    private void InitializeAudioMonitor()
    {
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) StartAudioMonitor();
            else StopAudioMonitor();
        };
        Closed += (_, _) => StopAudioMonitor();
    }

    private void StartAudioMonitor()
    {
        if (_audioMonitorRegistered)
        {
            return;
        }

        // Band levels are only analysed while something needs them; register as a viewer.
        _owner.SetAudioMonitorVisible(true);
        _audioMonitorRegistered = true;
        _audioMonitorTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background,
            (_, _) => UpdateAudioMonitor(), Dispatcher);
        _audioMonitorTimer.Start();
        UpdateAudioMonitor();
    }

    private void StopAudioMonitor()
    {
        _audioMonitorTimer?.Stop();
        if (_audioMonitorRegistered)
        {
            _owner.SetAudioMonitorVisible(false);
            _audioMonitorRegistered = false;
        }
    }

    internal void UpdateAudioMonitor(MainWindow.AudioMonitorSnapshot? injected = null)
    {
        if (MonitorTempoText == null)
        {
            return;
        }

        MainWindow.AudioMonitorSnapshot snapshot = injected ?? _owner.GetAudioMonitorSnapshot();
        MonitorTempoText.Text = $"{snapshot.Bpm:0.0} BPM";
        MonitorTempoState.Text = snapshot.TempoState;

        int beatInBar = (int)Math.Floor(BeatClock.Fraction(snapshot.BarPosition / BeatClock.BeatsPerBar) * BeatClock.BeatsPerBar);
        Ellipse[] dots = { MonitorBeat1, MonitorBeat2, MonitorBeat3, MonitorBeat4 };
        for (int i = 0; i < dots.Length; i++)
        {
            dots[i].Fill = i != beatInBar ? MonitorBeatOff : i == 0 ? MonitorDownbeatOn : MonitorBeatOn;
        }

        // Peak-hold with a short decay so fast hits stay readable at 30 Hz.
        double[] values = { snapshot.Level, snapshot.Low, snapshot.Mid, snapshot.High };
        Border[] bars = { MonitorLevelBar, MonitorLowBar, MonitorMidBar, MonitorHighBar };
        for (int i = 0; i < values.Length; i++)
        {
            _audioMonitorPeaks[i] = Math.Max(Math.Clamp(values[i], 0, 1), _audioMonitorPeaks[i] * 0.82);
            bars[i].Width = Math.Round(_audioMonitorPeaks[i] * MonitorMeterWidth);
        }

        if (_audioMonitorLastOnsets >= 0 && snapshot.Onsets != _audioMonitorLastOnsets)
        {
            _audioMonitorHit = 1;
        }

        _audioMonitorLastOnsets = snapshot.Onsets;
        MonitorHit.Opacity = 0.15 + 0.85 * _audioMonitorHit;
        _audioMonitorHit *= 0.7;

        MonitorGainText.Text = snapshot.AutoGain || Math.Abs(snapshot.GainDb) > 0.05
            ? $"{snapshot.GainDb:+0;-0}dB{(snapshot.AutoGain ? " A" : "")}"
            : string.Empty;
        MonitorMeters.Visibility = snapshot.HasInput ? Visibility.Visible : Visibility.Hidden;
        MonitorInputText.Visibility = snapshot.HasInput ? Visibility.Collapsed : Visibility.Visible;
    }
}
