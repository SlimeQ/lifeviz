using System.Configuration;
using System.Data;
using System.Windows;

namespace lifeviz;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Unhandled UI exception.", args.Exception);
            MessageBox.Show($"Unexpected error:\n{args.Exception.Message}", "LifeViz Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
