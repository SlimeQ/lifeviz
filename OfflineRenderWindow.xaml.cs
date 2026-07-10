using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace lifeviz;

public partial class OfflineRenderWindow : Window
{
    private bool _isRendering;
    private bool _allowClose;

    public OfflineRenderWindow(int sceneFps)
    {
        InitializeComponent();
        OutputFpsComboBox.Items.Add(new ComboBoxItem { Content = "30 (long-form)", Tag = 30 });
        OutputFpsComboBox.Items.Add(new ComboBoxItem { Content = "60", Tag = 60 });
        OutputFpsComboBox.Items.Add(new ComboBoxItem { Content = $"Match scene ({sceneFps})", Tag = sceneFps });
        OutputFpsComboBox.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            DurationTextBox.Focus();
            DurationTextBox.SelectAll();
        };
    }

    public event EventHandler<OfflineRenderRequestEventArgs>? StartRequested;
    public event EventHandler? CancelRequested;

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseDuration(DurationTextBox.Text, out TimeSpan duration, out string error))
        {
            ValidationText.Text = error;
            ValidationText.Visibility = Visibility.Visible;
            DurationTextBox.Focus();
            DurationTextBox.SelectAll();
            return;
        }

        ValidationText.Visibility = Visibility.Collapsed;
        _isRendering = true;
        DurationPanel.IsEnabled = false;
        StartButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Collapsed;
        CancelRenderButton.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Visible;
        int outputFps = OutputFpsComboBox.SelectedItem is ComboBoxItem { Tag: int fps }
            ? fps
            : 30;
        StartRequested?.Invoke(this, new OfflineRenderRequestEventArgs(duration, outputFps));
    }

    private void CancelRenderButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRenderButton.IsEnabled = false;
        ProgressText.Text = "Cancelling after the current frame...";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateProgress(long completedFrames, long totalFrames, TimeSpan elapsed, TimeSpan? remaining)
    {
        double fraction = totalFrames <= 0 ? 0 : Math.Clamp(completedFrames / (double)totalFrames, 0, 1);
        RenderProgressBar.Value = fraction;
        ProgressText.Text = $"{fraction:P1}  •  {completedFrames:N0} / {totalFrames:N0} frames";
        EtaText.Text = remaining.HasValue
            ? $"Elapsed {FormatClock(elapsed)}  •  about {FormatClock(remaining.Value)} remaining"
            : $"Elapsed {FormatClock(elapsed)}  •  estimating remaining time...";
    }

    public void Complete(string message, bool succeeded)
    {
        _isRendering = false;
        _allowClose = true;
        CancelRenderButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
        CloseButton.Content = "Close";
        ProgressText.Text = message;
        EtaText.Text = succeeded ? "The output is ready in Videos\\LifeViz." : string.Empty;
        if (succeeded)
        {
            RenderProgressBar.Value = 1;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isRendering || _allowClose)
        {
            return;
        }

        e.Cancel = true;
        CancelRenderButton_Click(this, new RoutedEventArgs());
    }

    private static bool TryParseDuration(string? text, out TimeSpan duration, out string error)
    {
        duration = TimeSpan.Zero;
        error = string.Empty;
        string value = text?.Trim() ?? string.Empty;
        string[] parts = value.Split(':');
        if (parts.Length is < 2 or > 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int first) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int second) ||
            (parts.Length == 3 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            error = "Use H:MM:SS (for example 4:30:00) or MM:SS.";
            return false;
        }

        int hours;
        int minutes;
        int seconds;
        if (parts.Length == 3)
        {
            hours = first;
            minutes = second;
            seconds = int.Parse(parts[2], CultureInfo.InvariantCulture);
            if (minutes > 59 || seconds > 59)
            {
                error = "Minutes and seconds must each be between 00 and 59.";
                return false;
            }
        }
        else
        {
            hours = 0;
            minutes = first;
            seconds = second;
            if (seconds > 59)
            {
                error = "Seconds must be between 00 and 59.";
                return false;
            }
        }

        try
        {
            duration = TimeSpan.FromSeconds(((long)hours * 3600L) + ((long)minutes * 60L) + seconds);
        }
        catch (OverflowException)
        {
            error = "That duration is too large.";
            return false;
        }

        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromHours(24))
        {
            error = "Choose a duration between 00:01 and 24:00:00.";
            return false;
        }

        return true;
    }

    private static string FormatClock(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}";
        }

        return $"{value.Minutes}:{value.Seconds:00}";
    }
}

public sealed class OfflineRenderRequestEventArgs : EventArgs
{
    public OfflineRenderRequestEventArgs(TimeSpan duration, int outputFps)
    {
        Duration = duration;
        OutputFps = outputFps;
    }

    public TimeSpan Duration { get; }
    public int OutputFps { get; }
}
