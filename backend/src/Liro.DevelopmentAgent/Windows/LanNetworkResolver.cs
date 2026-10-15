using System.Text.Json;
using Liro.DevelopmentAgent.Configuration;
using Liro.DevelopmentAgent.Hosting;

namespace Liro.DevelopmentAgent.Windows;

public sealed class LanNetworkResolver(AgentOptions options, ICommandRunner commands) : ILanNetworkResolver
{
    public async Task<LanEndpoint> ResolveAsync(CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-NoProfile", "-NonInteractive", "-File", Path.Combine(options.BackendPath, "Get-LanConfiguration.ps1"), "-CheckFirewall" };
        if (options.InterfaceIndex is { } index) { arguments.Add("-InterfaceIndex"); arguments.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        var result = await commands.RunAsync(ProcessRunner.PowerShellPath, arguments, cancellationToken);
        if (result.ExitCode != 0) { throw new InvalidOperationException("Revisa la interfaz privada y la configuracion LAN local."); }
        var network = JsonSerializer.Deserialize<NetworkSettings>(result.Output)
            ?? throw new InvalidOperationException("No se pudo determinar la interfaz LAN.");
        var endpoint = new LanEndpoint(new Uri($"http://{network.Address}:5205/"), network.Address, network.PrefixLength, network.InterfaceIndex);
        return endpoint;
    }

    private sealed record NetworkSettings(string Address, int PrefixLength, int InterfaceIndex);
}
