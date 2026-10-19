namespace Liro.Discord.Alerts.Hosting;

// Keep this handle alive for the host lifetime. The OS releases it on a crash.
public sealed class AlertInstance : IDisposable
{
    private readonly FileStream lease;
    public AlertInstance(string directory)
    {
        Directory.CreateDirectory(directory);
        lease = new FileStream(Path.Combine(directory, "alerts.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose() => lease.Dispose();
}
