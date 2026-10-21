using System.Text.Json;
using Discord.Net;
using Discord.WebSocket;
using Liro.Discord.LocalControl.Diagnostics;
using Liro.RegressionTests;

namespace Liro.Discord.LocalControl.RegressionTests;

internal static class GatewayDiagnosticsTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("GatewayDiagnostics_RecoveryAndPanelAreMeasuredSeparately", () =>
        {
            var clock = new Clock();
            var records = new List<GatewayDiagnostic>();
            var tracker = new GatewayDiagnostics(clock, records.Add);
            tracker.Disconnected(new GatewayReconnectException("secret"));
            clock.Advance(1400);
            tracker.Connected();
            clock.Advance(3600);
            tracker.PanelAvailable();
            tracker.PanelAvailable();
            Assert(records.Count == 3 && records[0].Cause == GatewayFailure.GatewayReconnect
                && records[1].ElapsedMilliseconds == 1400 && records[2].ElapsedMilliseconds == 5000,
                "Recovery durations or panel deduplication incorrect");
        });
        suite.Test("GatewayDiagnostics_ProlongedOnceAndStopSuppression", () =>
        {
            var clock = new Clock();
            var records = new List<GatewayDiagnostic>();
            var tracker = new GatewayDiagnostics(clock, records.Add);
            tracker.Disconnected(new TimeoutException("secret"));
            clock.Advance(59999);
            tracker.Check();
            Assert(records.Count == 1, "Prolonged reported too early");
            tracker.Disconnected(new Exception("duplicate"));
            clock.Advance(1);
            tracker.Check();
            tracker.Check();
            Assert(records.Count == 2 && records[1].Event == GatewayEvent.DisconnectionProlonged
                && records[1].ElapsedMilliseconds == 60000, "Threshold, duplicate event or original timestamp incorrect");
            tracker.Connected();
            tracker.Disconnected(new Exception());
            clock.Advance(61000);
            tracker.Connected();
            Assert(records.Count(x => x.Event == GatewayEvent.DisconnectionProlonged) == 2, "Reconnect before next timer lost prolonged diagnosis");
            tracker.Stop();
            var count = records.Count;
            tracker.Disconnected(new Exception());
            clock.Advance(60000);
            tracker.Check();
            tracker.Connected();
            tracker.PanelAvailable();
            Assert(records.Count == count, "Intentional shutdown generated outage records");
        });
        suite.AsyncTest("GatewayDiagnostics_LogNeverStoresExceptionText", async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "liro-gateway-test-" + Guid.NewGuid());
            var secret = "TOKEN-DO-NOT-LOG https://private.invalid/?token=secret\nInjected";
            try
            {
                await using (var log = new GatewayDiagnosticLog(path))
                {
                    var clock = new Clock();
                    var tracker = new GatewayDiagnostics(clock, log.Write);
                    tracker.Disconnected(new Exception(secret, new WebSocketClosedException(4008, secret)));
                    clock.Advance(100);
                    tracker.Connected();
                    tracker.PanelAvailable();
                }
                var text = File.ReadAllText(Path.Combine(path, "gateway.jsonl"));
                Assert(!text.Contains("TOKEN") && !text.Contains("private") && !text.Contains("Injected"), "Sensitive exception data leaked");
                using var first = JsonDocument.Parse(File.ReadLines(Path.Combine(path, "gateway.jsonl")).First());
                Assert(first.RootElement.GetProperty("CloseCode").GetInt32() == 4008
                    && first.RootElement.GetProperty("Cause").GetString() == "WebSocketClosed", "Safe close code or classification lost");
                await using var unavailable = new GatewayDiagnosticLog(Path.Combine(path, "gateway.jsonl", "not-a-directory"));
                unavailable.Write(new GatewayDiagnostic(DateTimeOffset.UtcNow, GatewayEvent.TransportConnected, GatewayFailure.Unknown, null, null));
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        });
        suite.Test("GatewayDiagnostics_ClassifiesTransportWithoutGuessingHeartbeat", () =>
        {
            var records = new List<GatewayDiagnostic>();
            foreach (var pair in new (Exception Error, GatewayFailure Cause)[] {
                (new TimeoutException(), GatewayFailure.Timeout),
                (new System.Net.Sockets.SocketException(), GatewayFailure.Network),
                (new System.Net.WebSockets.WebSocketException(), GatewayFailure.Network),
                (new OperationCanceledException(), GatewayFailure.Canceled),
                (new InvalidOperationException("Heartbeat timed out TOKEN"), GatewayFailure.Unknown) })
            {
                var tracker = new GatewayDiagnostics(new Clock(), records.Add);
                tracker.Disconnected(pair.Error);
                Assert(records[^1].Cause == pair.Cause, "Classification guessed from message or missed typed cause");
            }
        });
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(ticks);
        public void Advance(long milliseconds) => ticks += milliseconds;
    }
}
