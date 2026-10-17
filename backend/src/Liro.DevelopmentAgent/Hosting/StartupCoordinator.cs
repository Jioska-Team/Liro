using Liro.DevelopmentAgent.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Liro.DevelopmentAgent.Hosting;

public sealed class StartupCoordinator(ILocalRuntime runtime, ILanNetworkResolver network, IReadinessProbe probe, TimeProvider clock, IHostApplicationLifetime lifetime, LocalAgentLog log) : IStartupCoordinator
{
    private readonly object gate = new();
    private Task<StartupResult>? active;
    private DateTimeOffset failedAt = DateTimeOffset.MinValue;
    private StartupResult? lastFailure;

    private enum Operation { EnsureReady, Restart, Stop }
    private Operation activeOperation;

    public Task<StartupResult> EnsureReadyAsync(CancellationToken token) => ExecuteAsync(Operation.EnsureReady, token);
    public Task<StartupResult> RestartAsync(CancellationToken token) => ExecuteAsync(Operation.Restart, token);
    public Task<StartupResult> StopAsync(CancellationToken token) => ExecuteAsync(Operation.Stop, token);

    private async Task<StartupResult> ExecuteAsync(Operation requested, CancellationToken cancellationToken)
    {
        Task<StartupResult> operation;
        lock (gate)
        {
            if (active is { IsCompleted: false })
            {
                // Repeated clicks share work; conflicting commands never queue a surprise restart.
                operation = activeOperation == requested ? active : Task.FromResult(new StartupResult(StartupStatus.Busy));
            }
            else if (requested == Operation.EnsureReady && lastFailure is not null && clock.GetUtcNow() - failedAt < TimeSpan.FromSeconds(30))
            {
                operation = Task.FromResult(lastFailure);
            }
            else
            {
                activeOperation = requested;
                operation = active = RunAsync(requested);
            }
        }
        return await operation.WaitAsync(cancellationToken);
    }

    public async Task<StartupResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (active is { IsCompleted: false })
            {
                return new StartupResult(activeOperation switch
                {
                    Operation.Restart => StartupStatus.Restarting,
                    Operation.Stop => StartupStatus.Stopping,
                    _ => StartupStatus.Starting
                });
            }
        }
        try
        {
            var state = await runtime.GetAppHostStateAsync(cancellationToken);
            if (state == AppHostState.Stopped) { return new(StartupStatus.Stopped); }
            if (state is AppHostState.Unknown or AppHostState.Incompatible) { return new(StartupStatus.Unknown); }
            var endpoint = await network.ResolveAsync(cancellationToken);
            if (await probe.IsReadyAsync(endpoint, cancellationToken)) { return await FinalizeAsync(endpoint, cancellationToken); }
            return new(state == AppHostState.Starting ? StartupStatus.Starting : StartupStatus.Unhealthy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StartupStatus.Unknown); }
    }

    private async Task<StartupResult> RunAsync(Operation requested)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8), clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, lifetime.ApplicationStopping);
        StartupResult result;
        try
        {
            result = requested == Operation.EnsureReady ? await StartAsync(deadline.Token) : await StopCoreAsync(deadline.Token);
            if (requested == Operation.Restart && result.Status == StartupStatus.Stopped)
            {
                result = await StartAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) { result = new StartupResult(StartupStatus.TimedOut); }
        catch (Exception) { result = new StartupResult(StartupStatus.ConfigurationError); }
        lock (gate)
        {
            lastFailure = result.Status is StartupStatus.Ready or StartupStatus.Stopped ? null : result;
            if (lastFailure is not null) { failedAt = clock.GetUtcNow(); }
        }
        log.Write(result.Status switch { StartupStatus.Ready => AgentEvent.Ready, StartupStatus.Stopped => AgentEvent.Stopped, _ => AgentEvent.Failed });
        return result;
    }

    private async Task<StartupResult> StopCoreAsync(CancellationToken cancellationToken)
    {
        var state = await runtime.GetAppHostStateAsync(cancellationToken);
        if (state == AppHostState.Stopped) { return new(StartupStatus.Stopped); }
        if (state is AppHostState.Unknown or AppHostState.Incompatible) { return new(StartupStatus.AppHostUnavailable); }
        await runtime.StopAppHostAsync(cancellationToken);
        // A successful CLI exit alone does not prove the backend has stopped.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60), clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        while (await runtime.GetAppHostStateAsync(deadline.Token) != AppHostState.Stopped)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), clock, deadline.Token);
        }
        return new(StartupStatus.Stopped);
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
