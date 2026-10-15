namespace Liro.DevelopmentAgent.Diagnostics;

public enum AgentEvent { Starting, Connected, Disconnected, Rejected, Ready, DockerStarting, BackendStarting, Failed, Stopped, PanelInstalled }

// Only fixed event codes are accepted: no exception, URL, command output or token can enter the log.
public sealed class LocalAgentLog(string localDirectory)
{
    private readonly object gate = new();

    public void Write(AgentEvent code)
    {
        lock (gate)
        {
            Directory.CreateDirectory(localDirectory);
            var path = Path.Combine(localDirectory, "agent.log");
            if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
            {
                for (var i = 3; i >= 0; i--)
                {
                    var source = i == 0 ? path : path + "." + i;
                    if (File.Exists(source)) { File.Move(source, path + "." + (i + 1), true); }
                }
            }
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {code}\n");
        }
    }
}
