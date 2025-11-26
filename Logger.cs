using System;
using System.IO;

namespace lifeviz;

internal static class Logger
{
    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static bool _initialized;

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lifeviz", "logs");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "lifeviz.log");
                _writer = new StreamWriter(path, append: false)
                {
                    AutoFlush = true
                };
                WriteInternal($"--- LifeViz session started {DateTime.UtcNow:O} ---");
            }
            catch
            {
                // If logging can't start, keep running silently.
            }

            _initialized = true;
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            try
            {
                WriteInternal($"--- LifeViz session ended {DateTime.UtcNow:O} ---");
                _writer?.Dispose();
            }
            catch
            {
                // Ignore shutdown errors.
            }
            finally
            {
                _writer = null;
            }
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        lock (Sync)
        {
            try
            {
                string line = $"{DateTime.UtcNow:O} [{level}] {message}";
                Console.WriteLine(line);
                _writer?.WriteLine(line);
                if (ex != null)
                {
                    _writer?.WriteLine(ex.ToString());
                }
            }
            catch
            {
                // Swallow logging failures.
            }
        }
    }

    private static void WriteInternal(string message)
    {
        try
        {
            _writer?.WriteLine(message);
        }
        catch
        {
            // Ignore internal failures.
        }
    }
}
