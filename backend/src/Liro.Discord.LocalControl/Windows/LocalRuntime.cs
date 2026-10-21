using System.Diagnostics;
using System.Management;
using System.Text.Json;
using Liro.Discord.LocalControl.Configuration;
using Liro.Discord.LocalControl.Hosting;

namespace Liro.Discord.LocalControl.Windows;

public sealed class LocalRuntime(AgentOptions options, ICommandRunner commands) : ILocalRuntime
{
    private string DockerDesktopPath()
    {
        var candidates = new[] { options.DockerDesktopPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DockerDesktop", "Docker Desktop.exe") };
        var path = candidates.FirstOrDefault(path => path is not null && File.Exists(path))
            ?? throw new InvalidOperationException("Docker Desktop no esta instalado en una ruta conocida.");
        return path;
    }

    public async Task<bool> IsDockerReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ready = await CheckDockerAsync(cancellationToken);
            return ready;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A slow CLI probe is not the end of the shared eight-minute startup budget.
            return false;
        }
    }

    private async Task<bool> CheckDockerAsync(CancellationToken cancellationToken)
    {
        var docker = Path.Combine(Path.GetDirectoryName(DockerDesktopPath())!, "resources", "bin", "docker.exe");
        if (!File.Exists(docker))
        {
            throw new InvalidOperationException("No se encontro Docker CLI local.");
        }
        var context = await commands.RunAsync(docker, ["context", "inspect", "desktop-linux", "--format", "{{json .Endpoints.docker.Host}}"], cancellationToken);
        if (context.ExitCode != 0)
        {
            return false;
        }
        var address = JsonSerializer.Deserialize<string>(context.Output.Trim());
        if (address is not ("npipe:////./pipe/dockerDesktopLinuxEngine" or "npipe:////./pipe/docker_engine"))
        {
            throw new InvalidOperationException("El contexto desktop-linux no apunta a un motor Docker local permitido.");
        }
        var result = await commands.RunAsync(docker, ["--context", "desktop-linux", "info", "--format", "{{.ServerVersion}}"], cancellationToken);
        var ready = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output);
        return ready;
    }

    public Task<bool> IsDockerDesktopRunningAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processes = Process.GetProcessesByName("Docker Desktop");
        try
        {
            return Task.FromResult(processes.Length > 0);
        }
        finally { foreach (var process in processes) { process.Dispose(); } }
    }

    public async Task StartDockerDesktopAsync(CancellationToken cancellationToken)
    {
        if (!await IsDockerDesktopRunningAsync(cancellationToken))
        {
            using var process = Process.Start(new ProcessStartInfo(DockerDesktopPath())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("No se pudo abrir Docker Desktop.");
        }
    }

    public Task<AppHostState> GetAppHostStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Name,ExecutablePath,CommandLine FROM Win32_Process WHERE Name='Liro.AppHost.exe' OR Name='dotnet.exe' OR Name='powershell.exe' OR Name='pwsh.exe'");
            search.Options.Timeout = TimeSpan.FromSeconds(5);
            using var processes = search.Get();
            var backend = Path.GetFullPath(options.BackendPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (ManagementObject process in processes)
            {
                using (process)
                {
                    var name = process["Name"] as string ?? "";
                    var executable = process["ExecutablePath"] as string;
                    var command = process["CommandLine"] as string;
                    if (name.Equals("Liro.AppHost.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (executable is null)
                        {
                            return Task.FromResult(AppHostState.Unknown);
                        }
                        if (executable.StartsWith(backend, StringComparison.OrdinalIgnoreCase))
                        {
                            return Task.FromResult(AppHostState.Running);
                        }
                    }
                    if (command is not null && command.Contains(backend, StringComparison.OrdinalIgnoreCase)
                        && (command.Contains("Liro.AppHost", StringComparison.OrdinalIgnoreCase) || command.Contains("Start-Lan.ps1", StringComparison.OrdinalIgnoreCase)))
                    {
                        return Task.FromResult(AppHostState.Starting);
                    }
                    if (name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                        && (command is null || command.Contains("Liro.AppHost", StringComparison.OrdinalIgnoreCase)))
                    {
                        return Task.FromResult(AppHostState.Unknown);
                    }
                }
            }
            var occupied = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == 5205);
            return Task.FromResult(occupied ? AppHostState.Incompatible : AppHostState.Stopped);
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(AppHostState.Unknown);
        }
    }

    public async Task StopAppHostAsync(CancellationToken cancellationToken)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "bin", "aspire.exe");
        var result = await commands.RunAsync(executable, StopArguments(options.BackendPath), cancellationToken);
        if (result.ExitCode != 0)
        {
            // CLI output can include credentials. Never forward it to Discord or logs.
            throw new InvalidOperationException("Aspire no pudo detener el proyecto configurado.");
        }
    }

    public static string[] StopArguments(string backendPath) =>
        ["stop", "--apphost", Path.GetFullPath(Path.Combine(backendPath, "src", "Liro.AppHost", "Liro.AppHost.csproj")), "--non-interactive"];

    public async Task StartAppHostAsync(LanEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (await GetAppHostStateAsync(cancellationToken) != AppHostState.Stopped)
        {
            throw new InvalidOperationException("Ya existe un AppHost o no se pudo verificar su estado.");
        }
        var info = new ProcessStartInfo(ProcessRunner.PowerShellPath)
        {
            WorkingDirectory = options.BackendPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(options.BackendPath, "scripts", "development", "Start-Lan.ps1"), "-AgentMode", "-InterfaceIndex", endpoint.InterfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            info.ArgumentList.Add(argument);
        }
        info.Environment.Remove("DOCKER_HOST");
        info.Environment["DOCKER_CONTEXT"] = "desktop-linux";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("No se pudo iniciar Liro.");
    }
}
