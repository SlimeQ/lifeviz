using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace lifeviz;

public partial class App
{
    private bool _handlingRenderThreadFailure;

    internal static bool IsRenderThreadFailure(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current.HResult == unchecked((int)0x88980406)) return true;
        return false;
    }

    private void HandleRenderThreadFailure(Exception exception)
    {
        // A zombied WPF render partition cannot be recovered by swallowing the
        // dispatcher exception or by opening another WPF window on that process.
        if (_handlingRenderThreadFailure) return;
        _handlingRenderThreadFailure = true;
        try
        {
            Logger.Error("Fatal WPF render-thread failure; stopping this session before offering a fresh-process restart.", exception);
            foreach (var window in Windows.OfType<MainWindow>().ToArray())
            {
                try { window.ShutdownResources(); }
                catch (Exception cleanupError) { Logger.Error("Render-failure cleanup failed.", cleanupError); }
            }
            FfmpegProcessManager.Shared.Shutdown(TimeSpan.FromSeconds(3));
            Logger.Shutdown();
            PreserveRenderFailureLog();

            if (!SuppressErrorDialogs && ShowRenderFailureMessage(IntPtr.Zero,
                "Windows stopped LifeViz's graphics rendering thread (0x88980406). This session cannot continue.\n\n" +
                "Retry restarts LifeViz from your saved scene. Cancel closes LifeViz. Any recording or export in progress has been stopped; verify its partial output before using it.\n\n" +
                "If this repeats after sleep or a display change, close LifeViz and run the latest standalone installer. Details are in %APPDATA%\\lifeviz\\logs\\render-failure-last.log.",
                "LifeViz graphics recovery", 0x00000005 | 0x00000010 | 0x00010000) == 4)
            {
                string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate LifeViz to restart it.");
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetTempPath()
                };
                if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
                Process.Start(start)?.Dispose();
            }
        }
        finally
        {
            // WPF shutdown itself can re-enter the dead render channel. Native
            // resources and media have already been explicitly cleaned up above.
            Environment.Exit(1);
        }
    }

    private static void PreserveRenderFailureLog()
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lifeviz", "logs");
            File.Copy(Path.Combine(directory, "lifeviz.log"), Path.Combine(directory, "render-failure-last.log"), overwrite: true);
        }
        catch
        {
            // A locked/unavailable log must not prevent native recovery or exit.
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int ShowRenderFailureMessage(IntPtr owner, string text, string caption, uint flags);
}
