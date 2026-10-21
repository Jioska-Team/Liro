using System.Diagnostics;
using Liro.Discord.LocalControl.Diagnostics;
using Liro.Discord.LocalControl.Windows;
using Liro.RegressionTests;

namespace Liro.Discord.LocalControl.RegressionTests;

internal static class CommandTimeoutTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.Test("CommandTimeout_DockerAndStopKeepExistingLimits", () =>
        {
            if (ProcessRunner.CommandTimeout("docker.exe", ["info"]) != TimeSpan.FromSeconds(10)
                || ProcessRunner.CommandTimeout("aspire.exe", ["stop", "--apphost", "example.csproj"]) != TimeSpan.FromSeconds(60)
                || ProcessRunner.CommandTimeout(ProcessRunner.PowerShellPath, ["-File", "Other.ps1"]) != TimeSpan.FromSeconds(10))
                throw new Exception("Unrelated command timeout changed");
        });
        suite.AsyncTest("LanCommand_AllowsProbeLongerThanTenSeconds", async () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "lan-command-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            var script = Path.Combine(folder, "Get-LanConfiguration.ps1");
            try
            {
                await File.WriteAllTextAsync(script, "Start-Sleep -Seconds 11\nWrite-Output 'validated'");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                var result = await new ProcessRunner().RunAsync(ProcessRunner.PowerShellPath,
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-CheckFirewall"], deadline.Token);
                if (result.ExitCode != 0 || result.Output.Trim() != "validated")
                    throw new Exception("Valid slow LAN probe was not allowed to finish");
            }
            finally { Directory.Delete(folder, true); }
        });
        suite.AsyncTest("LanCommand_ParentCancellationStopsProbe", async () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "lan-command-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            var script = Path.Combine(folder, "Get-LanConfiguration.ps1");
            var pidFile = Path.Combine(folder, "child.pid");
            try
            {
                await File.WriteAllTextAsync(script, "'{0}' -f $PID | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'child.pid')\nStart-Sleep -Seconds 60");
                using var cancellation = new CancellationTokenSource();
                var operation = new ProcessRunner().RunAsync(ProcessRunner.PowerShellPath,
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-CheckFirewall"], cancellation.Token);
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!File.Exists(pidFile))
                    await Task.Delay(20, startup.Token);
                var id = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
                cancellation.Cancel();
                try
                {
                    await operation;
                    throw new Exception("Cancellation ignored");
                }
                catch (OperationCanceledException) { }
                try
                {
                    using var child = Process.GetProcessById(id);
                    using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await child.WaitForExitAsync(exit.Token);
                }
                catch (ArgumentException) { /* Process already exited. */ }
            }
            finally { Directory.Delete(folder, true); }
        });
        suite.AsyncTest("LanCommand_TimesOutAtThirtySecondsAndLogsSafeEvent", async () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "lan-command-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            var script = Path.Combine(folder, "Get-LanConfiguration.ps1");
            try
            {
                await File.WriteAllTextAsync(script, "Write-Output 'sensitive-output'\nStart-Sleep -Seconds 60");
                var elapsed = Stopwatch.StartNew();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try
                {
                    await new ProcessRunner(new LocalAgentLog(folder)).RunAsync(ProcessRunner.PowerShellPath,
                        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script], deadline.Token);
                    throw new Exception("Hanging probe did not time out");
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                var events = await File.ReadAllTextAsync(Path.Combine(folder, "agent.log"));
                if (elapsed.Elapsed < TimeSpan.FromSeconds(29) || elapsed.Elapsed > TimeSpan.FromSeconds(40)
                    || !events.Contains("NetworkCheckTimedOut") || events.Contains("sensitive-output"))
                    throw new Exception("Timeout boundary or safe diagnostics failed");
            }
            finally { Directory.Delete(folder, true); }
        });
    }
}
