using Liro.DevelopmentAgent.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Liro.DevelopmentAgent.Hosting;

public sealed class StartupCoordinator(ILocalRuntime runtime, ILanNetworkResolver network, IReadinessProbe probe, TimeProvider clock, IHostApplicationLifetime lifetime, LocalAgentLog log) : IStartupCoordinator
{
    private readonly object gate = new();
    private Task<StartupResult>? active;
    private DateTimeOffset failedAt = DateTimeOffset.MinValue;
    private StartupResult? lastFailure;

    public async Task<StartupResult> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        Task<StartupResult> operation;
        lock (gate)
        {
            if (active is { IsCompleted: false }) { operation = active; }
            else if (lastFailure is not null && clock.GetUtcNow() - failedAt < TimeSpan.FromSeconds(30))
            {
                operation = Task.FromResult(lastFailure);
            }
            else { operation = active = RunAsync(); }
        }
        var result = await operation.WaitAsync(cancellationToken);
        return result;
    }

    private async Task<StartupResult> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8), clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, lifetime.ApplicationStopping);
        StartupResult result;
        try { result = await StartAsync(deadline.Token); }
        catch (OperationCanceledException) { result = new StartupResult(StartupStatus.TimedOut); }
        catch (Exception) { result = new StartupResult(StartupStatus.ConfigurationError); }
        lock (gate)
        {
            lastFailure = result.Status == StartupStatus.Ready ? null : result;
            if (lastFailure is not null) { failedAt = clock.GetUtcNow(); }
        }
        log.Write(result.Status == StartupStatus.Ready ? AgentEvent.Ready : AgentEvent.Failed);
        return result;
    }

    private async Task<StartupResult> StartAsync(CancellationToken cancellationToken)
    {
        var endpoint = await network.ResolveAsync(cancellationToken);
        if (await probe.IsReadyAsync(endpoint, cancellationToken)) { return await FinalizeAsync(endpoint, cancellationToken); }
        var state = await runtime.GetAppHostStateAsync(cancellationToken);
        if (state is AppHostState.Unknown or AppHostState.Incompatible) { return new StartupResult(StartupStatus.AppHostUnavailable); }

        if (!await runtime.IsDockerReadyAsync(cancellationToken))
        {
            if (!await runtime.IsDockerDesktopRunningAsync(cancellationToken))
            {
                log.Write(AgentEvent.DockerStarting);
                await runtime.StartDockerDesktopAsync(cancellationToken);
            }
            while (!await runtime.IsDockerReadyAsync(cancellationToken))
            {
                await Task.Delay(TimeSpan.FromSeconds(2), clock, cancellationToken);
            }
        }

        // Recheck after Docker startup: a manual launch may have happened in the meantime.
        state = await runtime.GetAppHostStateAsync(cancellationToken);
        if (state is AppHostState.Unknown or AppHostState.Incompatible) { return new StartupResult(StartupStatus.AppHostUnavailable); }
        if (state == AppHostState.Stopped)
        {
            log.Write(AgentEvent.BackendStarting);
            await runtime.StartAppHostAsync(endpoint, cancellationToken);
        }
        while (!await probe.IsReadyAsync(endpoint, cancellationToken))
        {
            if (await runtime.GetAppHostStateAsync(cancellationToken) == AppHostState.Stopped)
            {
                return new StartupResult(StartupStatus.AppHostUnavailable);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), clock, cancellationToken);
        }
        var result = await FinalizeAsync(endpoint, cancellationToken);
        return result;
    }

    private async Task<StartupResult> FinalizeAsync(LanEndpoint endpoint, CancellationToken cancellationToken)
    {
        var current = await network.ResolveAsync(cancellationToken);
        if (current != endpoint) { return new StartupResult(StartupStatus.NetworkChanged); }
        var ready = await probe.IsReadyAsync(current, cancellationToken);
        var result = ready ? new StartupResult(StartupStatus.Ready, current.ApiBaseUri) : new StartupResult(StartupStatus.AppHostUnavailable);
        return result;
    }
}
