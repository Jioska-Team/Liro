namespace Liro.DevelopmentAgent.Hosting;

public sealed class AgentInstance : IDisposable
{
    private readonly FileStream lease;

    public AgentInstance(string localDirectory)
    {
        Directory.CreateDirectory(localDirectory);
        // FileShare.None is released by Windows even if the process crashes; stale PID files are not trusted.
        lease = new FileStream(Path.Combine(localDirectory, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() { lease.Dispose(); }
}
