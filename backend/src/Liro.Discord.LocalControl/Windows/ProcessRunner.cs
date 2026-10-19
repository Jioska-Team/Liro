using System.Diagnostics;

namespace Liro.Discord.LocalControl.Windows;

public sealed record CommandResult(int ExitCode, string Output);
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class ProcessRunner : ICommandRunner
{
    public static string PowerShellPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = CreateStartInfo(executable, arguments);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("No se pudo iniciar el comando local.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(arguments.Count > 0 && arguments[0] == "stop" ? 60 : 10));
        var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var errors = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var result = new CommandResult(process.ExitCode, await output);
            await errors;
            return result;
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
        }
    }

    public static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) { info.ArgumentList.Add(argument); }
        return info;
    }

    public static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new System.Text.StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (output.Length + count <= 65536) { output.Append(buffer, 0, count); }
        }
        return output.ToString();
    }
}
