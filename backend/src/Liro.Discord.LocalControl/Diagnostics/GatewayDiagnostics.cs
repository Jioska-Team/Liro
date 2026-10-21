using Discord.Net;
using Discord.WebSocket;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Liro.Discord.LocalControl.Diagnostics;

public enum GatewayEvent
{
    Disconnected, TransportConnected, Reconnected, DisconnectionProlonged, PanelRestored, SessionReady, DiagnosticOverflow
}
public enum GatewayFailure
{
    Unknown, GatewayReconnect, WebSocketClosed, Network, Timeout, Canceled
}
public sealed record GatewayDiagnostic(DateTimeOffset Utc, GatewayEvent Event, GatewayFailure Cause,
    int? CloseCode, long? ElapsedMilliseconds, long DroppedRecords = 0);

// Only enum/numeric fields reach the sink. Never serialize an exception or its message/type name.
public sealed class GatewayDiagnostics(TimeProvider clock, Action<GatewayDiagnostic> write)
{
    private readonly object gate = new();
    private long? disconnectedAt;
    private long? panelPendingAt;
    private GatewayFailure cause;
    private int? closeCode;
    private bool prolonged;
    private bool stopping;

    public void Disconnected(Exception? error)
    {
        lock (gate)
        {
            if (stopping || disconnectedAt.HasValue)
                return;
            (cause, closeCode) = Classify(error);
            disconnectedAt = clock.GetTimestamp();
            panelPendingAt ??= disconnectedAt;
            prolonged = false;
            Emit(GatewayEvent.Disconnected, null);
        }
    }

    public void Connected()
    {
        lock (gate)
        {
            if (stopping)
                return;
            if (disconnectedAt is { } start)
            {
                CheckCore();
                Emit(GatewayEvent.Reconnected, Elapsed(start));
                disconnectedAt = null;
            }
            else
                Emit(GatewayEvent.TransportConnected, null);
        }
    }

    public void PanelAvailable()
    {
        lock (gate)
        {
            if (stopping || disconnectedAt.HasValue || panelPendingAt is not { } start)
                return;
            Emit(GatewayEvent.PanelRestored, Elapsed(start));
            panelPendingAt = null;
        }
    }

    public void SessionReady()
    {
        lock (gate)
        {
            if (!stopping)
                Emit(GatewayEvent.SessionReady, null);
        }
    }

    public void Check()
    {
        lock (gate)
        {
            if (!stopping)
                CheckCore();
        }
    }
    public void Stop()
    {
        lock (gate)
        {
            stopping = true;
        }
    }

    private void CheckCore()
    {
        if (!prolonged && disconnectedAt is { } start && Elapsed(start) >= 60_000)
        {
            prolonged = true;
            Emit(GatewayEvent.DisconnectionProlonged, Elapsed(start));
        }
    }
    private long Elapsed(long start) => Math.Max(0, (long)clock.GetElapsedTime(start).TotalMilliseconds);
    private void Emit(GatewayEvent kind, long? elapsed) => write(new(clock.GetUtcNow(), kind, cause, closeCode, elapsed));

    private static (GatewayFailure, int?) Classify(Exception? error)
    {
        // Bounded traversal; do not inspect messages, Reason, URLs, Data, stack traces or arbitrary type names.
        var fallback = GatewayFailure.Unknown;
        for (var depth = 0; error is not null && depth < 8; depth++, error = error.InnerException)
        {
            if (error is WebSocketClosedException closed)
                return (GatewayFailure.WebSocketClosed, closed.CloseCode is >= 1000 and <= 4999 ? closed.CloseCode : null);
            if (error is GatewayReconnectException)
                return (GatewayFailure.GatewayReconnect, null);
            if (error is SocketException or WebSocketException or HttpRequestException)
                fallback = GatewayFailure.Network;
            else if (error is TimeoutException && fallback == GatewayFailure.Unknown)
                fallback = GatewayFailure.Timeout;
            else if (error is OperationCanceledException && fallback == GatewayFailure.Unknown)
                fallback = GatewayFailure.Canceled;
        }
        return (fallback, null);
    }
}
