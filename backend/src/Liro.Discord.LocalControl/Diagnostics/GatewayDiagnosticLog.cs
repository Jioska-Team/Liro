using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Liro.Discord.LocalControl.Diagnostics;

// Disk IO runs outside gateway callbacks. Diagnostic failure never stops the bot or starts another client.
public sealed class GatewayDiagnosticLog : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly Channel<GatewayDiagnostic> queue = Channel.CreateBounded<GatewayDiagnostic>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Task consumer;
    private long dropped;
    public GatewayDiagnosticLog(string directory) => consumer = Task.Run(async () =>
    {
        await foreach (var record in queue.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "gateway.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
                {
                    for (var i = 2; i >= 0; i--)
                    {
                        var source = i == 0 ? path : path + "." + i;
                        if (File.Exists(source))
                            File.Move(source, path + "." + (i + 1), true);
                    }
                }
                var lost = Interlocked.Exchange(ref dropped, 0);
                if (lost > 0)
                    File.AppendAllText(path, JsonSerializer.Serialize(new GatewayDiagnostic(record.Utc,
                    GatewayEvent.DiagnosticOverflow, GatewayFailure.Unknown, null, null, lost), Json) + "\n");
                File.AppendAllText(path, JsonSerializer.Serialize(record, Json) + "\n");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref dropped);
            }
        }
    });
    public void Write(GatewayDiagnostic record)
    {
        if (!queue.Writer.TryWrite(record))
            Interlocked.Increment(ref dropped);
    }
    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        await consumer;
    }
}
