using System;
using System.Runtime.InteropServices;

namespace lifeviz;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Enable Per-Monitor V2 DPI Awareness before any WPF code runs
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private static class NativeMethods
    {
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
    }
}
