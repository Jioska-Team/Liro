namespace Liro.MarketWorker;

public sealed class MarketWorkerInstance : IDisposable
{
    private readonly FileStream lease;
    private MarketWorkerInstance(FileStream lease) => this.lease = lease;

    public static MarketWorkerInstance Acquire(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // El bloqueo lo mantiene el sistema operativo. Un archivo residual no bloquea el reinicio.
        return new(new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }

    public void Dispose() => lease.Dispose();
}
