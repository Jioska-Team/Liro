using System.Text.Json;
using System.Text.RegularExpressions;
using Liro.Discord.LocalControl.Configuration;

namespace Liro.Discord.LocalControl.Windows;

public interface IDashboardLoginProvider
{
    Task<Uri?> GetLoginAsync(CancellationToken cancellationToken);
}

public sealed class DashboardLoginProvider(AgentOptions options, ICommandRunner commands) : IDashboardLoginProvider
{
    public async Task<Uri?> GetLoginAsync(CancellationToken cancellationToken)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "bin", "aspire.exe");
        if (!File.Exists(executable))
        {
            return null;
        }
        var result = await commands.RunAsync(executable, ["ps", "--format", "Json"], cancellationToken);
        var login = result.ExitCode == 0 ? Parse(result.Output, options.BackendPath) : null;
        return login;
    }

    // Never persist, log, or return unvalidated CLI output. Select this project only.
    public static Uri? Parse(string output, string backendPath)
    {
        using var json = JsonDocument.Parse(output);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var project = Path.GetFullPath(Path.Combine(backendPath, "src", "Liro.AppHost", "Liro.AppHost.csproj"));
        var matches = new List<Uri>();
        foreach (var entry in json.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("appHostPath", out var path) || path.ValueKind != JsonValueKind.String
                || !string.Equals(Path.GetFullPath(path.GetString()!), project, StringComparison.OrdinalIgnoreCase)
                || !entry.TryGetProperty("status", out var status) || !string.Equals(status.GetString(), "running", StringComparison.OrdinalIgnoreCase)
                || !entry.TryGetProperty("dashboardUrl", out var url) || url.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            if (!Uri.TryCreate(url.GetString(), UriKind.Absolute, out var candidate)
                || candidate.Scheme != Uri.UriSchemeHttps || !candidate.IsLoopback
                || candidate.UserInfo.Length != 0 || candidate.Fragment.Length != 0
                || candidate.AbsolutePath != "/login"
                || !Regex.IsMatch(candidate.Query, @"^\?t=[A-Za-z0-9_-]{16,256}$", RegexOptions.CultureInvariant))
            {
                continue;
            }
            matches.Add(candidate);
        }
        var login = matches.Count == 1 ? matches[0] : null;
        return login;
    }
}
