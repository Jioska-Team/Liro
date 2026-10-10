using System.Net;
using System.Reflection;
using System.Text;
using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Timeout;
using k8s;

var appHostAssembly = Assembly.Load("Liro.AppHost");
var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true, AssemblyName = appHostAssembly.FullName });
builder.Services.AddLogging(logging => logging.ClearProviders().AddConsole());
await using var app = builder.Build();
var hosting = typeof(DistributedApplication).Assembly;
var executor = app.Services.GetRequiredService(hosting.GetType("Aspire.Hosting.Dcp.IDcpExecutor", true)!);
var watcher = executor.GetType().GetProperty("ResourceWatcher", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(executor)!;
var watcherPipeline = (ResiliencePipeline)watcher.GetType().GetProperty("WatchResourceRetryPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watcher)!;
// The fixed retry policy now lives inside KubernetesService. Inspect it only in tests;
// production uses Aspire's native implementation without modifying internal state.
var kubernetesService = hosting.GetType("Aspire.Hosting.Dcp.KubernetesService", true)!;
var factory = kubernetesService.GetMethod("CreateKubernetesCallResiliencePipeline", BindingFlags.Static | BindingFlags.NonPublic)!;
var activity = Activator.CreateInstance(factory.GetParameters()[2].ParameterType);
var pipeline = (ResiliencePipeline)factory.Invoke(null, [TimeSpan.FromSeconds(1), (Func<Exception, bool>)(_ => false), activity, (Action)(() => { }), true])!;
var attempts = 0;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await pipeline.ExecuteAsync(token =>
{
    attempts++;
    if (attempts == 1)
    {
        throw new TimeoutRejectedException("Idle ContainerExec watch");
    }

    return ValueTask.CompletedTask;
}, deadline.Token);
if (attempts != 2)
{
    throw new Exception("Watch did not recover after its first timeout.");
}

Console.WriteLine("PASS actual Aspire watcher recovers after a timeout");
attempts = 0;
await watcherPipeline.ExecuteAsync(token =>
{
    if (++attempts == 1)
    {
        throw new EndOfStreamException("DCP closed stream");
    }

    return ValueTask.CompletedTask;
}, deadline.Token);
if (attempts != 2)
{
    throw new Exception("Existing end-of-stream recovery was lost.");
}

Console.WriteLine("PASS existing Aspire end-of-stream recovery is retained");
attempts = 0;
var unexpected = new InvalidOperationException("Unrelated failure");
try
{
    await pipeline.ExecuteAsync(token =>
    {
        attempts++;
        return ValueTask.FromException(unexpected);
    });
    throw new Exception("Unrelated failure was swallowed.");
}
catch (InvalidOperationException exception) when (ReferenceEquals(exception, unexpected))
{
    if (attempts != 1)
    {
        throw new Exception("Unrelated failure was retried.");
    }
}

Console.WriteLine("PASS unrelated failures propagate without retry");
attempts = 0;
using (var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
{
    try
    {
        await pipeline.ExecuteAsync(token =>
        {
            attempts++;
            return ValueTask.FromException(new TimeoutRejectedException("Still idle"));
        }, shutdown.Token);
        throw new Exception("Shutdown was ignored.");
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
    }

    if (attempts == 0)
    {
        throw new Exception("Cancellation test never entered the watch.");
    }
}

Console.WriteLine("PASS shutdown cancels retry backoff");
using (var handler = new IdleThenEventHandler())
using (var client = new Kubernetes(new KubernetesClientConfiguration { Host = "http://unused.invalid" }, handler))
using (var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
{
    await pipeline.ExecuteAsync(async token =>
    {
        using var response = await client.CustomObjects.ListClusterCustomObjectWithHttpMessagesAsync("usvc-dev.developer.microsoft.com", "v1", "containerexecs", watch: true, cancellationToken: token);
        if (!response.Body.ToString()!.Contains("recovered"))
        {
            throw new Exception("Watch did not return the recovery event.");
        }
    }, testDeadline.Token);
    if (handler.Requests != 2)
    {
        throw new Exception($"Expected one reconnection, got {handler.Requests} requests.");
    }
}

Console.WriteLine("PASS real KubernetesClient receives event after idle HTTP 200 stream times out");
using (var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
{
    try
    {
        await pipeline.ExecuteAsync(async token => await Task.Delay(Timeout.InfiniteTimeSpan, token), shutdown.Token);
        throw new Exception("An active watch ignored shutdown.");
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
    }
}

Console.WriteLine("PASS shutdown cancels active watch");
Console.WriteLine("6/6 AppHost regression tests passed");
sealed class IdleThenEventHandler : DelegatingHandler
{
    public int Requests
    {
        get; private set;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Stream stream = ++Requests == 1 ? new IdleStream() : new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"ADDED\",\"object\":{\"metadata\":{\"name\":\"recovered\"}}}\n"));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(stream) });
    }
}

sealed class IdleStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException(); set => throw new NotSupportedException();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
