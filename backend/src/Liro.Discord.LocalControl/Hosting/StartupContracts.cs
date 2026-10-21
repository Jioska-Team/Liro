namespace Liro.Discord.LocalControl.Hosting;

public sealed record LanEndpoint(Uri ApiBaseUri, string Address, int PrefixLength, int InterfaceIndex);
public enum AppHostState
{
    Stopped, Starting, Running, Incompatible, Unknown
}
public enum StartupStatus
{
    Ready, DockerUnavailable, AppHostUnavailable, NetworkChanged, TimedOut, ConfigurationError, Stopped, Busy, Starting, Restarting, Stopping, Unhealthy, Unknown
}
public sealed record StartupResult(StartupStatus Status, Uri? ApiBaseUri = null);
public interface ILanNetworkResolver
{
    Task<LanEndpoint> ResolveAsync(CancellationToken cancellationToken);
    Task<LanEndpoint> ResolveForStatusAsync(CancellationToken cancellationToken) => ResolveAsync(cancellationToken);
}
public interface IReadinessProbe
{
    Task<bool> IsReadyAsync(LanEndpoint endpoint, CancellationToken cancellationToken);
}
public interface ILocalRuntime
{
    Task<bool> IsDockerReadyAsync(CancellationToken cancellationToken);
    Task<bool> IsDockerDesktopRunningAsync(CancellationToken cancellationToken);
    Task StartDockerDesktopAsync(CancellationToken cancellationToken);
    Task<AppHostState> GetAppHostStateAsync(CancellationToken cancellationToken);
    Task StartAppHostAsync(LanEndpoint endpoint, CancellationToken cancellationToken);
    Task StopAppHostAsync(CancellationToken cancellationToken);
}
public interface IStartupCoordinator
{
    Task<StartupResult> EnsureReadyAsync(CancellationToken cancellationToken);
    Task<StartupResult> RestartAsync(CancellationToken cancellationToken);
    Task<StartupResult> StopAsync(CancellationToken cancellationToken);
    Task<StartupResult> GetStatusAsync(CancellationToken cancellationToken);
}
