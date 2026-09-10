using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace lifeviz;

public partial class BakeQueueWindow : Window
{
    private readonly Action<BakeJob> _cancel;
    internal BakeQueueWindow(ObservableCollection<BakeJob> jobs, Action<BakeJob> cancel)
    {
        InitializeComponent();
        JobsList.ItemsSource = jobs;
        _cancel = cancel;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BakeJob job }) _cancel(job);
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BakeJob job }) return;
        try
        {
            string? folder = Path.GetDirectoryName(job.OutputPath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open output folder"); }
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BakeJob job } || !job.HasDiagnostics) return;
        try
        {
            Process.Start(new ProcessStartInfo(job.DiagnosticsPath) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Open bake diagnostics"); }
    }
}
