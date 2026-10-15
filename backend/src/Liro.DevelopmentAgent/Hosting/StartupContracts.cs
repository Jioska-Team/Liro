namespace Liro.DevelopmentAgent.Hosting;

public sealed record LanEndpoint(Uri ApiBaseUri, string Address, int PrefixLength, int InterfaceIndex);
public enum AppHostState { Stopped, Starting, Running, Incompatible, Unknown }
public enum StartupStatus { Ready, DockerUnavailable, AppHostUnavailable, NetworkChanged, TimedOut, ConfigurationError }
public sealed record StartupResult(StartupStatus Status, Uri? ApiBaseUri = null);
public interface ILanNetworkResolver { Task<LanEndpoint> ResolveAsync(CancellationToken cancellationToken); }
public interface IReadinessProbe { Task<bool> IsReadyAsync(LanEndpoint endpoint, CancellationToken cancellationToken); }
public interface ILocalRuntime
{
    Task<bool> IsDockerReadyAsync(CancellationToken cancellationToken);
    Task<bool> IsDockerDesktopRunningAsync(CancellationToken cancellationToken);
    Task StartDockerDesktopAsync(CancellationToken cancellationToken);
    Task<AppHostState> GetAppHostStateAsync(CancellationToken cancellationToken);
    Task StartAppHostAsync(LanEndpoint endpoint, CancellationToken cancellationToken);
}
public interface IStartupCoordinator { Task<StartupResult> EnsureReadyAsync(CancellationToken cancellationToken); }
