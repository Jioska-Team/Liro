using System.Net;
using Liro.DevelopmentAgent.Configuration;
using Liro.DevelopmentAgent.Diagnostics;
using Liro.DevelopmentAgent.Discord;
using Liro.DevelopmentAgent.Hosting;
using Liro.DevelopmentAgent.Http;
using Liro.DevelopmentAgent.Windows;
using Liro.RegressionTests;

namespace Liro.DevelopmentAgent.RegressionTests;

internal static class BoundaryTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Discord rejects every mismatched identity before starting", async () =>
        {
            var options = new AgentOptions { GuildId = 1, ChannelId = 2, PanelMessageId = 3, RoleId = 4 };
            var startup = new StartupFake();
            var handler = new InteractionHandler(options, startup, Log());
            var valid = new ButtonRequest(1, 2, 3, InteractionHandler.ButtonId, [4]);
            foreach (var request in new[] { valid with { GuildId = null }, valid with { GuildId = 9 }, valid with { ChannelId = 9 }, valid with { MessageId = 9 }, valid with { CustomId = "cmd" }, valid with { RoleIds = [] } })
            {
                var reply = new ReplyFake();
                var accepted = await handler.AcknowledgeAsync(request, reply);
                if (accepted || reply.Deferred || !reply.Denied || startup.Calls != 0) { throw new Exception("Unauthorized interaction reached startup"); }
            }
            var allowed = new ReplyFake();
            if (!await handler.AcknowledgeAsync(valid, allowed) || !allowed.Deferred || startup.Calls != 0) { throw new Exception("Deferral must precede startup"); }
            await handler.CompleteAsync(allowed, default);
            if (startup.Calls != 1 || !allowed.Message.Contains("http://192.168.50.10:5205/")) { throw new Exception("Ready result not delivered"); }
        });
        foreach (var entry in new[]
        {
            (HttpStatusCode.OK, "{\"service\":\"liro-api\",\"status\":\"ready\"}", true),
            (HttpStatusCode.OK, "{\"service\":\"other\",\"status\":\"ready\"}", false),
            (HttpStatusCode.ServiceUnavailable, "{}", false),
            (HttpStatusCode.Redirect, "{}", false),
            (HttpStatusCode.OK, "not json", false)
        })
        {
            suite.AsyncTest("Readiness validates HTTP and identity " + entry, async () =>
            {
                using var client = new HttpClient(new ResponseHandler(entry.Item1, entry.Item2));
                var ready = await new LiroReadinessProbe(client).IsReadyAsync(new LanEndpoint(new Uri("http://192.168.50.10:5205"), "192.168.50.10", 24, 1), default);
                if (ready != entry.Item3) { throw new Exception("Invalid readiness acceptance"); }
            });
        }
        suite.AsyncTest("Remote Docker context cannot trigger remote info requests", async () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "runtime-test-" + Guid.NewGuid());
            Directory.CreateDirectory(Path.Combine(folder, "resources", "bin"));
            try
            {
                var desktop = Path.Combine(folder, "Docker Desktop.exe");
                File.WriteAllText(desktop, "dummy");
                File.WriteAllText(Path.Combine(folder, "resources", "bin", "docker.exe"), "dummy");
                var commands = new CommandsFake();
                var runtime = new LocalRuntime(new AgentOptions { DockerDesktopPath = desktop }, commands);
                var rejected = false;
                try { await runtime.IsDockerReadyAsync(default); } catch (InvalidOperationException) { rejected = true; }
                if (!rejected || commands.Calls != 1) { throw new Exception("Remote Docker endpoint was contacted"); }
                var warmingUp = new LocalRuntime(new AgentOptions { DockerDesktopPath = desktop }, new WarmingDocker());
                if (await warmingUp.IsDockerReadyAsync(default)) { throw new Exception("Timed-out engine was considered ready"); }
            }
            finally { Directory.Delete(folder, true); }
        });
        suite.Test("Second agent is excluded and crash lease can be reacquired", () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "lease-test-" + Guid.NewGuid());
            try
            {
                using (var first = new AgentInstance(folder))
                {
                    var rejected = false;
                    try { using var second = new AgentInstance(folder); } catch (IOException) { rejected = true; }
                    if (!rejected) { throw new Exception("Second agent acquired lease"); }
                }
                using var recovered = new AgentInstance(folder);
            }
            finally { Directory.Delete(folder, true); }
        });
        suite.Test("Fixed event logs rotate within size budget", () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "log-test-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            try
            {
                var logger = new LocalAgentLog(folder);
                for (var i = 0; i < 7; i++)
                {
                    File.WriteAllBytes(Path.Combine(folder, "agent.log"), new byte[2 * 1024 * 1024]);
                    logger.Write(AgentEvent.Ready);
                }
                if (Directory.GetFiles(folder).Length != 5 || !File.ReadAllText(Path.Combine(folder, "agent.log")).Contains("Ready")) { throw new Exception("Log rotation budget violated"); }
            }
            finally { Directory.Delete(folder, true); }
        });
    }

    private static LocalAgentLog Log() => new(Path.Combine(AppContext.BaseDirectory, "test-logs"));
    private sealed class StartupFake : IStartupCoordinator
    {
        public int Calls;
        public Task<StartupResult> RestartAsync(CancellationToken token) => EnsureReadyAsync(token);
        public Task<StartupResult> StopAsync(CancellationToken token) => Task.FromResult(new StartupResult(StartupStatus.Stopped));
        public Task<StartupResult> GetStatusAsync(CancellationToken token) => Task.FromResult(new StartupResult(StartupStatus.Unknown));
        public Task<StartupResult> EnsureReadyAsync(CancellationToken token) { Calls++; return Task.FromResult(new StartupResult(StartupStatus.Ready, new Uri("http://192.168.50.10:5205/"))); }
    }
    private sealed class ReplyFake : IButtonReply
    {
        public bool Deferred, Denied;
        public string Message = "";
        public Task DeferAsync() { Deferred = true; return Task.CompletedTask; }
        public Task DenyAsync() { Denied = true; return Task.CompletedTask; }
        public Task CompleteAsync(string message) { Message = message; return Task.CompletedTask; }
    }
    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
    private sealed class CommandsFake : ICommandRunner
    {
        public int Calls;
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new CommandResult(0, "\"tcp://remote:2375\""));
        }
    }
    private sealed class WarmingDocker : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            if (arguments[0] == "context") { return Task.FromResult(new CommandResult(0, "\"npipe:////./pipe/dockerDesktopLinuxEngine\"")); }
            throw new OperationCanceledException("Local command timed out while the motor warms up");
        }
    }
}
