using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace lifeviz;

// Status is telemetry, not part of frame production. Keep only the newest
// snapshot and move pipe I/O off the renderer and editor dispatcher threads.
internal static class BakeStatusTransport
{
    private const string Prefix = "LIFEVIZ_BAKE_STATUS_V1 ";

    internal sealed class Publisher : IDisposable
    {
        private readonly Channel<BakeStatus> _pending = Channel.CreateBounded<BakeStatus>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private readonly Task _writer;
        public Exception? WriteError { get; private set; }

        public Publisher(TextWriter output)
        {
            _writer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var status in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
                        await output.WriteLineAsync(Prefix + JsonSerializer.Serialize(status)).ConfigureAwait(false);
                    await output.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    WriteError = ex;
                    Logger.Warn($"Bake status connection failed; rendering continues. {ex.Message}");
                }
            });
        }

        public void Publish(BakeStatus status) => _pending.Writer.TryWrite(status);

        public void Dispose()
        {
            _pending.Writer.TryComplete();
            // The parent drains continuously, independently of its UI. Bound
            // shutdown if that parent disappears or cannot consume the pipe.
            if (!_writer.Wait(TimeSpan.FromSeconds(5)))
                Logger.Warn("Bake status connection did not finish draining before worker shutdown.");
        }
    }

    internal sealed class Receiver
    {
        private BakeStatus? _latest;
        public BakeStatus? Latest => Volatile.Read(ref _latest);

        public async Task DrainAsync(TextReader input)
        {
            while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                // Only protocol records update the queue. Ignore unexpected
                // output; diagnostics belong in the worker's private log file.
                if (!line.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<BakeStatus>(line.AsSpan(Prefix.Length)) is { } status)
                        Volatile.Write(ref _latest, status);
                }
                catch (JsonException) { }
            }
        }
    }
}
