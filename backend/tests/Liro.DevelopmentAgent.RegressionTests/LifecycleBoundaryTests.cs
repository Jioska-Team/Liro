using Liro.DevelopmentAgent.Configuration;
using Liro.DevelopmentAgent.Diagnostics;
using Liro.DevelopmentAgent.Discord;
using Liro.DevelopmentAgent.Hosting;
using Liro.DevelopmentAgent.Windows;
using Liro.RegressionTests;

namespace Liro.DevelopmentAgent.RegressionTests;

internal static class LifecycleBoundaryTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Lifecycle buttons require configured role channel guild and panel", async () =>
        {
            var options = new AgentOptions { GuildId = 1, ChannelId = 2, PanelMessageId = 3, RoleId = 4 };
            var coordinator = new CoordinatorFake();
            var handler = new InteractionHandler(options, coordinator, new LocalAgentLog(Path.Combine(AppContext.BaseDirectory, "test-logs")));
            foreach (var action in new[] { InteractionHandler.ButtonId, InteractionHandler.StopButtonId, InteractionHandler.StatusButtonId })
            {
                var valid = new ButtonRequest(1, 2, 3, action, [4]);
                foreach (var invalid in new[] { valid with { RoleIds = [] }, valid with { GuildId = 9 }, valid with { ChannelId = 9 }, valid with { MessageId = 9 } })
                {
                    var denied = new Reply();
                    Assert(!await handler.AcknowledgeAsync(invalid, denied) && denied.Denied && !denied.Deferred);
                }
                var reply = new Reply();
                Assert(await handler.AcknowledgeAsync(valid, reply) && reply.Deferred);
                await handler.CompleteAsync(action, reply, default);
                Assert(!string.IsNullOrEmpty(reply.Message));
            }
            Assert(coordinator.Calls.SequenceEqual(new[] { "restart", "stop", "status" }));
        });
        suite.AsyncTest("Graceful stop targets only configured AppHost without force or all", async () =>
        {
            var backend = Path.Combine(Path.GetTempPath(), "Liro project with spaces");
            var commands = new Commands();
            await new LocalRuntime(new AgentOptions { BackendPath = backend }, commands).StopAppHostAsync(default);
            Assert(commands.Arguments.SequenceEqual(new[] { "stop", "--apphost", Path.Combine(backend, "src", "Liro.AppHost", "Liro.AppHost.csproj"), "--non-interactive" }));
            commands.Fail = true;
            try { await new LocalRuntime(new AgentOptions { BackendPath = backend }, commands).StopAppHostAsync(default); }
            catch (InvalidOperationException exception) { Assert(!exception.Message.Contains("secret")); return; }
            throw new Exception("Failed stop was accepted");
        });
        suite.AsyncTest("Private reply replacement cleans only same users previous reply", async () =>
        {
            var registry = new PrivateReplyRegistry(TimeProvider.System);
            int firstDeleted = 0, otherDeleted = 0, nextDeleted = 0;
            var first = await registry.ReplaceAsync(1, () => { firstDeleted++; return Task.CompletedTask; });
            var other = await registry.ReplaceAsync(2, () => { otherDeleted++; return Task.CompletedTask; });
            var next = await registry.ReplaceAsync(1, () => { nextDeleted++; return Task.CompletedTask; });
            Assert(firstDeleted == 1 && otherDeleted == 0 && nextDeleted == 0);
            Assert(!registry.IsCurrent(first) && registry.IsCurrent(other) && registry.IsCurrent(next));
            await registry.ClearAsync();
            Assert(otherDeleted == 1 && nextDeleted == 1 && !registry.IsCurrent(next));
        });
        suite.AsyncTest("Private replies expire and old timers cannot remove latest reply", async () =>
        {
            var clock = new ManualClock();
            var registry = new PrivateReplyRegistry(clock);
            int removed = 0;
            var first = await registry.ReplaceAsync(1, () => Task.CompletedTask);
            var expiry = registry.ExpireAsync(first, TimeSpan.FromMinutes(2), default);
            var latest = await registry.ReplaceAsync(1, () => { removed++; return Task.CompletedTask; });
            clock.Advance(TimeSpan.FromMinutes(2));
            await expiry;
            Assert(registry.IsCurrent(latest) && removed == 0);
            var latestExpiry = registry.ExpireAsync(latest, TimeSpan.FromMinutes(2), default);
            clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1));
            Assert(registry.IsCurrent(latest) && removed == 0);
            clock.Advance(TimeSpan.FromTicks(1));
            await latestExpiry;
            Assert(!registry.IsCurrent(latest) && removed == 1);
        });
        suite.AsyncTest("Expired Discord response cannot block new reply", async () =>
        {
            var registry = new PrivateReplyRegistry(TimeProvider.System);
            await registry.ReplaceAsync(1, () => throw new InvalidOperationException("Expired token"));
            var latest = await registry.ReplaceAsync(1, () => Task.CompletedTask);
            Assert(registry.IsCurrent(latest));
        });
    }

    private static void Assert(bool condition) { if (!condition) { throw new Exception("Lifecycle boundary invariant failed"); } }
    private sealed class CoordinatorFake : IStartupCoordinator
    {
        public List<string> Calls = [];
        public Task<StartupResult> EnsureReadyAsync(CancellationToken token) => throw new Exception("Button must restart");
        public Task<StartupResult> RestartAsync(CancellationToken token) { Calls.Add("restart"); return Task.FromResult(new StartupResult(StartupStatus.Ready, new Uri("http://192.168.1.2:5205/"))); }
        public Task<StartupResult> StopAsync(CancellationToken token) { Calls.Add("stop"); return Task.FromResult(new StartupResult(StartupStatus.Stopped)); }
        public Task<StartupResult> GetStatusAsync(CancellationToken token) { Calls.Add("status"); return Task.FromResult(new StartupResult(StartupStatus.Unhealthy)); }
    }
    private sealed class Reply : IButtonReply
    {
        public bool Denied, Deferred;
        public string Message = "";
        public Task DeferAsync() { Deferred = true; return Task.CompletedTask; }
        public Task DenyAsync() { Denied = true; return Task.CompletedTask; }
        public Task CompleteAsync(string message) { Message = message; return Task.CompletedTask; }
    }
    private sealed class Commands : ICommandRunner
    {
        public IReadOnlyList<string> Arguments = [];
        public bool Fail;
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Arguments = arguments;
            return Task.FromResult(new CommandResult(Fail ? 1 : 0, "secret"));
        }
    }
}
