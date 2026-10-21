using System.Text.Json;
using Liro.Discord.LocalControl.Configuration;
using Liro.Discord.LocalControl.Hosting;

namespace Liro.Discord.LocalControl.Windows;

public sealed class LanNetworkResolver(AgentOptions options, ICommandRunner commands) : ILanNetworkResolver
{
    private LanEndpoint? validated;

    public Task<LanEndpoint> ResolveForStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = Volatile.Read(ref validated);
        if (endpoint is not null && System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
            && adapter.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv4)
            && adapter.GetIPProperties().GetIPv4Properties()?.Index == endpoint.InterfaceIndex
            && adapter.GetIPProperties().UnicastAddresses.Any(address => address.Address.ToString() == endpoint.Address
                && address.PrefixLength == endpoint.PrefixLength)))
        {
            return Task.FromResult(endpoint);
        }
        return ResolveAsync(cancellationToken);
    }
    public async Task<LanEndpoint> ResolveAsync(CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-NoProfile", "-NonInteractive", "-File", Path.Combine(options.BackendPath, "scripts", "development", "Get-LanConfiguration.ps1"), "-CheckFirewall" };
        if (options.InterfaceIndex is { } index)
        {
            arguments.Add("-InterfaceIndex");
            arguments.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var result = await commands.RunAsync(ProcessRunner.PowerShellPath, arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Revisa la interfaz privada y la configuracion LAN local.");
        }
        var network = JsonSerializer.Deserialize<NetworkSettings>(result.Output)
            ?? throw new InvalidOperationException("No se pudo determinar la interfaz LAN.");
        var endpoint = new LanEndpoint(new Uri($"http://{network.Address}:5205/"), network.Address, network.PrefixLength, network.InterfaceIndex);
        Volatile.Write(ref validated, endpoint);
        return endpoint;
    }

    private sealed record NetworkSettings(string Address, int PrefixLength, int InterfaceIndex);
}
