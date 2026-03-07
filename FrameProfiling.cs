using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;

namespace lifeviz;

internal sealed class FrameProfiler
{
    private readonly Dictionary<string, List<double>> _samples = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool IsActive { get; private set; }
    public string SessionName { get; private set; } = "profile";
    public DateTime StartedUtc { get; private set; }
    public DateTime EndedUtc { get; private set; }

    public void Start(string sessionName)
    {
        _samples.Clear();
        SessionName = string.IsNullOrWhiteSpace(sessionName) ? "profile" : sessionName.Trim();
        StartedUtc = DateTime.UtcNow;
        EndedUtc = StartedUtc;
        IsActive = true;
    }

    public FrameProfileReport Stop()
    {
        EndedUtc = DateTime.UtcNow;
        IsActive = false;

        var metrics = _samples
            .Select(pair => BuildMetric(pair.Key, pair.Value))
            .OrderByDescending(metric => metric.Average)
            .ToList();

        return new FrameProfileReport
        {
            SessionName = SessionName,
            StartedUtc = StartedUtc,
            EndedUtc = EndedUtc,
            DurationMs = Math.Max(0, (EndedUtc - StartedUtc).TotalMilliseconds),
            Metrics = metrics
        };
    }

    public void RecordSample(string metricName, double value)
    {
        if (!IsActive || string.IsNullOrWhiteSpace(metricName) || double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }

        if (!_samples.TryGetValue(metricName, out var values))
        {
            values = new List<double>(512);
            _samples.Add(metricName, values);
        }

        values.Add(value);
    }

    public string Export(FrameProfileReport report, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string safeSessionName = string.Concat(report.SessionName.Select(ch =>
            char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-')).Trim('-');
        if (string.IsNullOrWhiteSpace(safeSessionName))
        {
            safeSessionName = "profile";
        }

        string path = Path.Combine(outputDirectory, $"{safeSessionName}-{report.StartedUtc:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
        return path;
    }

    public static double ElapsedMilliseconds(long startTimestamp, long endTimestamp)
        => (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    public static double ElapsedMilliseconds(long startTimestamp)
        => ElapsedMilliseconds(startTimestamp, Stopwatch.GetTimestamp());

    private static FrameProfileMetricReport BuildMetric(string name, List<double> values)
    {
        values.Sort();
        double total = values.Sum();
        return new FrameProfileMetricReport
        {
            Name = name,
            Count = values.Count,
            Minimum = values[0],
            Average = total / values.Count,
            Median = Percentile(values, 0.50),
            P95 = Percentile(values, 0.95),
            P99 = Percentile(values, 0.99),
            Maximum = values[^1],
            Total = total
        };
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        if (sortedValues.Count == 1)
        {
            return sortedValues[0];
        }

        double clamped = Math.Clamp(percentile, 0, 1);
        double index = clamped * (sortedValues.Count - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        double blend = index - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * blend);
    }
}

internal sealed class UiThreadLatencyProbe : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly FrameProfiler _profiler;
    private Timer? _timer;
    private int _inputPending;
    private int _backgroundPending;

    public UiThreadLatencyProbe(Dispatcher dispatcher, FrameProfiler profiler)
    {
        _dispatcher = dispatcher;
        _profiler = profiler;
    }

    public void Start(TimeSpan interval)
    {
        Stop();
        _timer = new Timer(OnTimerTick, null, interval, interval);
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _inputPending, 0);
        Interlocked.Exchange(ref _backgroundPending, 0);
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();

    private void OnTimerTick(object? state)
    {
        if (!_profiler.IsActive)
        {
            return;
        }

        QueueInputLatencyProbe();
        QueueBackgroundLatencyProbe();
    }

    private void QueueInputLatencyProbe()
    {
        if (Interlocked.CompareExchange(ref _inputPending, 1, 0) != 0)
        {
            return;
        }

        long queuedAt = Stopwatch.GetTimestamp();
        _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _profiler.RecordSample("ui_input_latency_ms", FrameProfiler.ElapsedMilliseconds(queuedAt));
            Interlocked.Exchange(ref _inputPending, 0);
        }));
    }

    private void QueueBackgroundLatencyProbe()
    {
        if (Interlocked.CompareExchange(ref _backgroundPending, 1, 0) != 0)
        {
            return;
        }

        long queuedAt = Stopwatch.GetTimestamp();
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _profiler.RecordSample("ui_background_latency_ms", FrameProfiler.ElapsedMilliseconds(queuedAt));
            Interlocked.Exchange(ref _backgroundPending, 0);
        }));
    }
}

internal sealed class FrameProfileReport
{
    public string SessionName { get; set; } = "profile";
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    public double DurationMs { get; set; }
    public List<FrameProfileMetricReport> Metrics { get; set; } = new();
}

internal sealed class FrameProfileMetricReport
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public double Minimum { get; set; }
    public double Average { get; set; }
    public double Median { get; set; }
    public double P95 { get; set; }
    public double P99 { get; set; }
    public double Maximum { get; set; }
    public double Total { get; set; }
}

internal sealed class RollingMetricWindow
{
    private readonly Queue<double> _values;
    private readonly int _capacity;
    private double _sum;

    public RollingMetricWindow(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _values = new Queue<double>(_capacity);
    }

    public double Average => _values.Count == 0 ? 0.0 : _sum / _values.Count;

    public void Add(double value)
    {
        _values.Enqueue(value);
        _sum += value;
        while (_values.Count > _capacity)
        {
            _sum -= _values.Dequeue();
        }
    }
}
