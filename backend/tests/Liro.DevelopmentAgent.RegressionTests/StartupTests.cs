using Liro.DevelopmentAgent.Hosting;
using Liro.DevelopmentAgent.Diagnostics;
using Liro.RegressionTests;
using Microsoft.Extensions.Hosting;

namespace Liro.DevelopmentAgent.RegressionTests;

internal static class StartupTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Restart stops running backend before starting and returns ready URL", async () =>
        {
            var fake = new RuntimeFake { Ready = true, DockerReady = true, State = AppHostState.Running };
            var result = await Coordinator(fake).RestartAsync(default);
            Assert(result.Status == StartupStatus.Ready && result.ApiBaseUri is not null);
            Assert(fake.Order.SequenceEqual(new[] { "stop", "backend" }) && fake.DockerStarts == 0);
        });
        suite.AsyncTest("Restart starts stopped backend without issuing stop", async () =>
        {
            var fake = new RuntimeFake();
            var result = await Coordinator(fake).RestartAsync(default);
            Assert(result.Status == StartupStatus.Ready && fake.Stops == 0 && fake.Starts == 1);
        });
        suite.AsyncTest("Stop verifies stopped state without starting backend or Docker", async () =>
        {
            var fake = new RuntimeFake { Ready = true, State = AppHostState.Running };
            var coordinator = Coordinator(fake);
            Assert((await coordinator.StopAsync(default)).Status == StartupStatus.Stopped);
            Assert((await coordinator.GetStatusAsync(default)).Status == StartupStatus.Stopped);
            Assert((await coordinator.StopAsync(default)).Status == StartupStatus.Stopped);
            Assert(fake.Stops == 1 && fake.Starts == 0 && fake.DockerStarts == 0);
        });
        suite.AsyncTest("Failed stop never launches replacement backend", async () =>
        {
            var fake = new RuntimeFake { State = AppHostState.Running, FailStop = true };
            var result = await Coordinator(fake).RestartAsync(default);
            Assert(result.Status == StartupStatus.ConfigurationError && result.ApiBaseUri is null && fake.Starts == 0);
        });
        suite.AsyncTest("Unknown backend cannot be stopped or restarted", async () =>
        {
            foreach (var state in new[] { AppHostState.Unknown, AppHostState.Incompatible })
            {
                var fake = new RuntimeFake { State = state };
                var coordinator = Coordinator(fake);
                Assert((await coordinator.RestartAsync(default)).Status == StartupStatus.AppHostUnavailable);
                Assert((await coordinator.StopAsync(default)).Status == StartupStatus.AppHostUnavailable);
                Assert(fake.Stops == 0 && fake.Starts == 0);
            }
        });
        suite.AsyncTest("Concurrent restart clicks coalesce and conflicting stop is rejected", async () =>
        {
            var fake = new RuntimeFake { State = AppHostState.Running, BlockStartup = true, DockerReady = true };
            var coordinator = Coordinator(fake);
            var first = coordinator.RestartAsync(default);
            var second = coordinator.RestartAsync(default);
            Assert((await coordinator.GetStatusAsync(default)).Status == StartupStatus.Restarting);
            Assert((await coordinator.StopAsync(default)).Status == StartupStatus.Busy);
            fake.Release.TrySetResult();
            Assert((await first).Status == StartupStatus.Ready && (await second).Status == StartupStatus.Ready);
            Assert(fake.Stops == 1 && fake.Starts == 1);
        });
        suite.AsyncTest("Status distinguishes ready unhealthy starting stopped and unknown without mutations", async () =>
        {
            foreach (var entry in new[]
            {
                (AppHostState.Running, true, StartupStatus.Ready),
                (AppHostState.Running, false, StartupStatus.Unhealthy),
                (AppHostState.Starting, false, StartupStatus.Starting),
                (AppHostState.Stopped, false, StartupStatus.Stopped),
                (AppHostState.Unknown, false, StartupStatus.Unknown)
            })
            {
                var fake = new RuntimeFake { State = entry.Item1, Ready = entry.Item2 };
                var result = await Coordinator(fake).GetStatusAsync(default);
                Assert(result.Status == entry.Item3 && fake.Starts == 0 && fake.Stops == 0 && fake.DockerStarts == 0);
                Assert((result.ApiBaseUri is not null) == entry.Item2);
            }
        });
        suite.AsyncTest("Unconfirmed shutdown times out without replacement launch", async () =>
        {
            var fake = new RuntimeFake { State = AppHostState.Running, RemainRunningAfterStop = true };
            var clock = new ManualClock();
            var operation = Coordinator(fake, clock).RestartAsync(default);
            clock.Advance(TimeSpan.FromSeconds(60));
            Assert((await operation).Status == StartupStatus.TimedOut && fake.Starts == 0);
        });
        suite.AsyncTest("Ready API starts nothing", async () =>
        {
            var fake = new RuntimeFake { Ready = true };
            var result = await Coordinator(fake).EnsureReadyAsync(default);
            Assert(result.Status == StartupStatus.Ready && fake.Starts == 0 && fake.DockerStarts == 0);
        });
        suite.AsyncTest("Concurrent requests share one startup and cancellation is isolated", async () =>
        {
            var fake = new RuntimeFake { BlockStartup = true };
            var coordinator = Coordinator(fake);
            using var cancellation = new CancellationTokenSource();
            var first = coordinator.EnsureReadyAsync(cancellation.Token);
            var second = coordinator.EnsureReadyAsync(default);
            cancellation.Cancel();
            try { await first; throw new Exception("Caller was not canceled"); } catch (OperationCanceledException) { }
            fake.Release.TrySetResult();
            var result = await second;
            Assert(result.Status == StartupStatus.Ready && fake.Starts == 1 && fake.DockerStarts == 1);
            Assert(fake.Order.SequenceEqual(new[] { "docker", "backend" }));
        });
        suite.AsyncTest("Incompatible or unknown host never starts a duplicate", async () =>
        {
            foreach (var state in new[] { AppHostState.Unknown, AppHostState.Incompatible })
            {
                var fake = new RuntimeFake { State = state };
                var result = await Coordinator(fake).EnsureReadyAsync(default);
                Assert(result.Status == StartupStatus.AppHostUnavailable && fake.Starts == 0 && fake.DockerStarts == 0);
            }
        });
        suite.AsyncTest("Existing Docker is not reopened", async () =>
        {
            var fake = new RuntimeFake { DockerReady = true };
            await Coordinator(fake).EnsureReadyAsync(default);
            Assert(fake.DockerStarts == 0 && fake.Starts == 1);
        });
        suite.AsyncTest("Network change suppresses stale URL", async () =>
        {
            var fake = new RuntimeFake { Ready = true, ChangeNetwork = true };
            var result = await Coordinator(fake).EnsureReadyAsync(default);
            Assert(result.Status == StartupStatus.NetworkChanged && result.ApiBaseUri is null);
        });
        suite.AsyncTest("Startup expires at eight minutes and observes thirty second cooldown", async () =>
        {
            var fake = new RuntimeFake { BlockStartup = true };
            var clock = new ManualClock();
            var coordinator = Coordinator(fake, clock);
            var operation = coordinator.EnsureReadyAsync(default);
            clock.Advance(TimeSpan.FromMinutes(8) - TimeSpan.FromTicks(1));
            Assert(!operation.IsCompleted);
            clock.Advance(TimeSpan.FromTicks(1));
            var result = await operation;
            Assert(result.Status == StartupStatus.TimedOut && result.ApiBaseUri is null);
            fake.Ready = true;
            clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
            Assert((await coordinator.EnsureReadyAsync(default)).Status == StartupStatus.TimedOut);
            clock.Advance(TimeSpan.FromTicks(1));
            Assert((await coordinator.EnsureReadyAsync(default)).Status == StartupStatus.Ready && fake.Starts == 1);
        });
        suite.AsyncTest("Manual AppHost is awaited without another launch", async () =>
        {
            var fake = new RuntimeFake { State = AppHostState.Running, DockerReady = true, ReadyAfterProbes = 2 };
            var result = await Coordinator(fake).EnsureReadyAsync(default);
            Assert(result.Status == StartupStatus.Ready && fake.Starts == 0 && fake.DockerStarts == 0);
        });
        suite.AsyncTest("Open Desktop is awaited without opening another copy", async () =>
        {
            var fake = new RuntimeFake { DesktopOpen = true, DockerReadyAfterChecks = 2 };
            var result = await Coordinator(fake).EnsureReadyAsync(default);
            Assert(result.Status == StartupStatus.Ready && fake.DockerStarts == 0 && fake.Starts == 1);
        });
    }

    private static StartupCoordinator Coordinator(RuntimeFake fake, TimeProvider? clock = null)
    {
        var coordinator = new StartupCoordinator(fake, fake, fake, clock ?? TimeProvider.System, new Lifetime(), new LocalAgentLog(Path.Combine(AppContext.BaseDirectory, "test-logs")));
        return coordinator;
    }

    private static void Assert(bool condition) { if (!condition) { throw new Exception("Startup invariant failed"); } }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }

    private sealed class RuntimeFake : ILocalRuntime, ILanNetworkResolver, IReadinessProbe
    {
        public bool Ready, DockerReady, BlockStartup, ChangeNetwork, DesktopOpen;
        public int Starts, Stops, DockerStarts, Resolves;
        public bool FailStop, RemainRunningAfterStop;
        public int Probes, DockerChecks, ReadyAfterProbes = int.MaxValue, DockerReadyAfterChecks = int.MaxValue;
        public AppHostState State = AppHostState.Stopped;
        public List<string> Order = [];
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<LanEndpoint> ResolveAsync(CancellationToken token)
        {
            var address = ChangeNetwork && ++Resolves > 1 ? "192.168.50.11" : "192.168.50.10";
            return Task.FromResult(new LanEndpoint(new Uri($"http://{address}:5205/"), address, 24, 1));
        }
        public Task<bool> IsReadyAsync(LanEndpoint endpoint, CancellationToken token) => Task.FromResult(Ready || ++Probes >= ReadyAfterProbes);
        public Task<bool> IsDockerReadyAsync(CancellationToken token) => Task.FromResult(DockerReady || ++DockerChecks >= DockerReadyAfterChecks);
        public Task<bool> IsDockerDesktopRunningAsync(CancellationToken token) => Task.FromResult(DesktopOpen || DockerReady);
        public Task StartDockerDesktopAsync(CancellationToken token) { DockerStarts++; DockerReady = true; Order.Add("docker"); return Task.CompletedTask; }
        public Task<AppHostState> GetAppHostStateAsync(CancellationToken token) => Task.FromResult(State);
        public Task StopAppHostAsync(CancellationToken token)
        {
            Stops++; Order.Add("stop");
            if (FailStop) { throw new InvalidOperationException("Stop failed"); }
            if (!RemainRunningAfterStop) { State = AppHostState.Stopped; Ready = false; }
            return Task.CompletedTask;
        }
        public async Task StartAppHostAsync(LanEndpoint endpoint, CancellationToken token)
        {
            Starts++; State = AppHostState.Starting; Order.Add("backend");
            if (BlockStartup) { await Release.Task.WaitAsync(token); }
            Ready = true;
        }
    }
}
