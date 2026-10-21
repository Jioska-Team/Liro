using System.Diagnostics;
using Liro.Discord.LocalControl.Diagnostics;

namespace Liro.Discord.LocalControl.Windows;

public sealed record CommandResult(int ExitCode, string Output);
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class ProcessRunner(LocalAgentLog? log = null) : ICommandRunner
{
    public static string PowerShellPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = CreateStartInfo(executable, arguments);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("No se pudo iniciar el comando local.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lanCheck = IsLanCheck(executable, arguments);
        deadline.CancelAfter(CommandTimeout(executable, arguments));
        if (lanCheck)
            log?.Write(AgentEvent.NetworkCheckStarted);
        var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var errors = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var result = new CommandResult(process.ExitCode, await output);
            await errors;
            if (lanCheck)
                log?.Write(result.ExitCode == 0 ? AgentEvent.NetworkCheckCompleted : AgentEvent.NetworkCheckFailed);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            if (lanCheck)
                log?.Write(AgentEvent.NetworkCheckTimedOut);
            throw;
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
            }
            try
            {
                await Task.WhenAll(output, errors);
            }
            catch (OperationCanceledException) { }
        }
    }

    public static TimeSpan CommandTimeout(string executable, IReadOnlyList<string> arguments)
        => TimeSpan.FromSeconds(arguments.Count > 0 && arguments[0] == "stop" ? 60
            : IsLanCheck(executable, arguments) ? 30 : 10);

    private static bool IsLanCheck(string executable, IReadOnlyList<string> arguments)
    {
        if (!string.Equals(executable, PowerShellPath, StringComparison.OrdinalIgnoreCase))
            return false;
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (arguments[i].Equals("-File", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(arguments[i + 1]).Equals("Get-LanConfiguration.ps1", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        return info;
    }

    public static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new System.Text.StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (output.Length + count <= 65536)
            {
                output.Append(buffer, 0, count);
            }
        }
        return output.ToString();
    }
}
