using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace lifeviz;

internal static class Logger
{
    private const int QueueCapacity = 512;
    private const int MaxBatchSize = 256;
    private const int QueuePollMilliseconds = 100;
    private const int FlushIntervalMilliseconds = 500;
    private const int ShutdownDrainTimeoutMilliseconds = 5000;
    private const int StreamBufferSize = 64 * 1024;
    private const int MaxQueuedRecordChars = 8 * 1024;
    private const int MaxExceptionMessageChars = 2048;
    private const int MaxExceptionDepth = 4;
    private const long MaxSessionLogBytes = 8L * 1024 * 1024;
    private const string RecordTruncatedSuffix = " ... [truncated]";
    private const string DiskLimitMarker =
        "--- LifeViz disk log limit reached; further messages are omitted for this session. ---";

    private static readonly object Sync = new();
    private static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly int NewLineByteCount = LogEncoding.GetByteCount(Environment.NewLine);
    private static readonly int DiskLimitMarkerByteCount =
        LogEncoding.GetByteCount(DiskLimitMarker) + NewLineByteCount;
    private static LogSession? _session;

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_session != null)
            {
                return;
            }

            StreamWriter? writer = null;
            BlockingCollection<string>? queue = null;
            LogSession? session = null;
            try
            {
                writer = TryCreateWriter();
                queue = new BlockingCollection<string>(new ConcurrentQueue<string>(), QueueCapacity);
                var createdSession = new LogSession(
                    queue,
                    writer,
                    $"--- LifeViz session started {DateTime.UtcNow:O} ---");
                session = createdSession;
                var worker = new Thread(() => WriterLoop(createdSession))
                {
                    IsBackground = true,
                    Name = "LifeViz.Logger",
                    Priority = ThreadPriority.BelowNormal
                };
                createdSession.Worker = worker;

                Volatile.Write(ref _session, createdSession);
                worker.Start();
            }
            catch
            {
                if (session != null)
                {
                    Interlocked.Exchange(ref session.ShutdownStarted, 1);
                }
                Volatile.Write(ref _session, null);
                if (session != null)
                {
                    CloseWriter(session);
                }
                else
                {
                    try
                    {
                        writer?.Dispose();
                    }
                    catch
                    {
                    }
                }

                try
                {
                    queue?.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    public static void Shutdown()
    {
        LogSession? session;
        lock (Sync)
        {
            session = _session;
            if (session == null || Interlocked.Exchange(ref session.ShutdownStarted, 1) != 0)
            {
                return;
            }

            session.EndMessage = $"--- LifeViz session ended {DateTime.UtcNow:O} ---";
            try
            {
                session.Queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
                // The writer already completed its teardown.
            }
        }

        if (session.Worker == Thread.CurrentThread)
        {
            return;
        }

        if (!session.Worker.Join(ShutdownDrainTimeoutMilliseconds))
        {
            SafeWriteConsole(
                $"{DateTime.UtcNow:O} [WARN] Logger shutdown drain exceeded " +
                $"{ShutdownDrainTimeoutMilliseconds} ms; remaining messages will finish on the background worker.");
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        Publish(BuildRecord(level, message, ex));
    }

    private static void Publish(string record)
    {
        LogSession? session = Volatile.Read(ref _session);
        if (session == null)
        {
            // Preserve useful diagnostic output for calls made before initialization.
            SafeWriteConsole(record);
            return;
        }

        if (Volatile.Read(ref session.ShutdownStarted) != 0)
        {
            return;
        }

        try
        {
            if (session.Queue.IsAddingCompleted)
            {
                return;
            }

            if (session.Queue.TryAdd(record))
            {
                return;
            }

            Interlocked.Increment(ref session.DroppedCount);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown disposed the queue between the checks above.
        }
        catch (InvalidOperationException)
        {
            // Shutdown completed the queue between the checks above.
        }
    }

    private static void WriterLoop(LogSession session)
    {
        var flushClock = Stopwatch.StartNew();
        bool flushPending = false;

        try
        {
            WriteRecord(session, session.StartMessage, includeConsole: false);
            flushPending = true;

            while (!session.Queue.IsCompleted)
            {
                int batchCount = 0;
                if (session.Queue.TryTake(out string? record, QueuePollMilliseconds))
                {
                    WriteRecord(
                        session,
                        record,
                        includeConsole: Volatile.Read(ref session.ShutdownStarted) == 0);
                    flushPending = true;
                    batchCount++;

                    while (batchCount < MaxBatchSize && session.Queue.TryTake(out record))
                    {
                        WriteRecord(
                            session,
                            record,
                            includeConsole: Volatile.Read(ref session.ShutdownStarted) == 0);
                        batchCount++;
                    }
                }

                long dropped = Interlocked.Exchange(ref session.DroppedCount, 0);
                if (dropped > 0)
                {
                    WriteRecord(
                        session,
                        $"{DateTime.UtcNow:O} [WARN] Logger dropped {dropped} message(s) because its bounded queue was full.",
                        includeConsole: Volatile.Read(ref session.ShutdownStarted) == 0);
                    flushPending = true;
                }

                if (flushPending && flushClock.ElapsedMilliseconds >= FlushIntervalMilliseconds)
                {
                    FlushWriter(session);
                    flushClock.Restart();
                    flushPending = false;
                }
            }

            long finalDropped = Interlocked.Exchange(ref session.DroppedCount, 0);
            if (finalDropped > 0)
            {
                WriteRecord(
                    session,
                    $"{DateTime.UtcNow:O} [WARN] Logger dropped {finalDropped} message(s) because its bounded queue was full.",
                    includeConsole: false);
            }

            if (!string.IsNullOrWhiteSpace(session.EndMessage))
            {
                WriteRecord(session, session.EndMessage, includeConsole: false);
            }

            FlushWriter(session);
        }
        catch (Exception ex)
        {
            SafeWriteConsole($"{DateTime.UtcNow:O} [WARN] LifeViz logging worker stopped unexpectedly. {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref session.ShutdownStarted, 1);
            try
            {
                if (!session.Queue.IsAddingCompleted)
                {
                    session.Queue.CompleteAdding();
                }
            }
            catch
            {
                // The queue may already be disposed during exceptional startup teardown.
            }

            CloseWriter(session);
            lock (Sync)
            {
                if (ReferenceEquals(_session, session))
                {
                    Volatile.Write(ref _session, null);
                }
            }

            try
            {
                session.Queue.Dispose();
            }
            catch
            {
                // Ignore final queue cleanup errors.
            }
        }
    }

    private static void WriteRecord(LogSession session, string record, bool includeConsole)
    {
        if (includeConsole)
        {
            SafeWriteConsole(record);
        }

        StreamWriter? writer = session.Writer;
        if (writer == null || session.DiskLimitReached)
        {
            return;
        }

        try
        {
            int recordByteCount = LogEncoding.GetByteCount(record) + NewLineByteCount;
            long contentLimit = MaxSessionLogBytes - DiskLimitMarkerByteCount;
            if (session.BytesWritten + recordByteCount > contentLimit)
            {
                writer.WriteLine(DiskLimitMarker);
                session.BytesWritten += DiskLimitMarkerByteCount;
                writer.Flush();
                session.DiskLimitReached = true;
                SafeWriteConsole(DiskLimitMarker);
                CloseWriter(session);
                return;
            }

            writer.WriteLine(record);
            session.BytesWritten += recordByteCount;
        }
        catch (Exception ex)
        {
            DisableFileLogging(session, ex);
        }
    }

    private static void FlushWriter(LogSession session)
    {
        try
        {
            session.Writer?.Flush();
        }
        catch (Exception ex)
        {
            DisableFileLogging(session, ex);
        }
    }

    private static void DisableFileLogging(LogSession session, Exception ex)
    {
        CloseWriter(session);
        if (Interlocked.Exchange(ref session.FileFailureReported, 1) == 0)
        {
            SafeWriteConsole($"{DateTime.UtcNow:O} [WARN] LifeViz file logging was disabled. {ex.Message}");
        }
    }

    private static StreamWriter? TryCreateWriter()
    {
        FileStream? stream = null;
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "lifeviz",
                "logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "lifeviz.log");
            stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                StreamBufferSize,
                FileOptions.SequentialScan);
            return new StreamWriter(stream, LogEncoding, StreamBufferSize, leaveOpen: false)
            {
                AutoFlush = false
            };
        }
        catch
        {
            try
            {
                stream?.Dispose();
            }
            catch
            {
            }

            // If file logging can't start, retain asynchronous console logging.
            return null;
        }
    }

    private static void CloseWriter(LogSession session)
    {
        StreamWriter? writer = session.Writer;
        session.Writer = null;
        if (writer == null)
        {
            return;
        }

        try
        {
            writer.Dispose();
        }
        catch
        {
            // Ignore shutdown and log-limit cleanup errors.
        }
    }

    private static string BuildRecord(string level, string message, Exception? ex)
    {
        var builder = new StringBuilder(Math.Min(512, MaxQueuedRecordChars));
        builder.Append(DateTime.UtcNow.ToString("O"));
        builder.Append(" [");
        builder.Append(level);
        builder.Append("] ");

        int messageBudget = ex == null
            ? MaxQueuedRecordChars - builder.Length
            : Math.Min(MaxQueuedRecordChars / 2, MaxQueuedRecordChars - builder.Length);
        AppendLimited(builder, message ?? string.Empty, messageBudget);

        if (ex != null && builder.Length < MaxQueuedRecordChars)
        {
            AppendLimited(builder, Environment.NewLine, MaxQueuedRecordChars - builder.Length);
            AppendException(builder, ex);
        }

        return builder.ToString();
    }

    private static void AppendException(StringBuilder builder, Exception exception)
    {
        Exception? current = exception;
        int depth = 0;
        while (current != null && depth < MaxExceptionDepth && builder.Length < MaxQueuedRecordChars)
        {
            if (depth > 0)
            {
                AppendLimited(
                    builder,
                    $"{Environment.NewLine}--- Inner exception ---{Environment.NewLine}",
                    MaxQueuedRecordChars - builder.Length);
            }

            try
            {
                AppendLimited(
                    builder,
                    current.GetType().FullName ?? current.GetType().Name,
                    MaxQueuedRecordChars - builder.Length);
                AppendLimited(builder, ": ", MaxQueuedRecordChars - builder.Length);
                AppendLimited(
                    builder,
                    current.Message ?? string.Empty,
                    Math.Min(MaxExceptionMessageChars, MaxQueuedRecordChars - builder.Length));

                string? stackTrace = current.StackTrace;
                if (!string.IsNullOrWhiteSpace(stackTrace) && builder.Length < MaxQueuedRecordChars)
                {
                    AppendLimited(builder, Environment.NewLine, MaxQueuedRecordChars - builder.Length);
                    AppendLimited(builder, stackTrace, MaxQueuedRecordChars - builder.Length);
                }
            }
            catch
            {
                AppendLimited(
                    builder,
                    "<exception details unavailable>",
                    MaxQueuedRecordChars - builder.Length);
            }

            current = current.InnerException;
            depth++;
        }

        if (current != null && builder.Length < MaxQueuedRecordChars)
        {
            AppendLimited(builder, RecordTruncatedSuffix, MaxQueuedRecordChars - builder.Length);
        }
    }

    private static void AppendLimited(StringBuilder builder, string value, int budget)
    {
        int available = Math.Min(Math.Max(0, budget), MaxQueuedRecordChars - builder.Length);
        if (available <= 0 || value.Length == 0)
        {
            return;
        }

        if (value.Length <= available)
        {
            builder.Append(value);
            return;
        }

        int retainedLength = Math.Max(0, available - RecordTruncatedSuffix.Length);
        if (retainedLength > 0)
        {
            builder.Append(value.AsSpan(0, retainedLength));
        }

        int suffixLength = Math.Min(RecordTruncatedSuffix.Length, available - retainedLength);
        if (suffixLength > 0)
        {
            builder.Append(RecordTruncatedSuffix.AsSpan(0, suffixLength));
        }
    }

    private static void SafeWriteConsole(string record)
    {
        try
        {
            Console.WriteLine(record);
        }
        catch
        {
            // Console output is best-effort, especially for the WinExe build.
        }
    }

    private sealed class LogSession
    {
        public LogSession(BlockingCollection<string> queue, StreamWriter? writer, string startMessage)
        {
            Queue = queue;
            Writer = writer;
            StartMessage = startMessage;
        }

        public BlockingCollection<string> Queue { get; }
        public Thread Worker { get; set; } = null!;
        public StreamWriter? Writer { get; set; }
        public string StartMessage { get; }
        public string? EndMessage { get; set; }
        public long BytesWritten { get; set; }
        public long DroppedCount;
        public int ShutdownStarted;
        public int FileFailureReported;
        public bool DiskLimitReached { get; set; }
    }
}
