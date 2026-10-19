using System.Collections.Concurrent;

namespace Liro.Discord.LocalControl.Discord;

// Interaction handles remain in memory only; they contain short-lived Discord credentials.
public sealed class PrivateReplyRegistry(TimeProvider clock)
{
    private readonly ConcurrentDictionary<ulong, Lease> current = new();

    public sealed class Lease(ulong userId, Func<Task> delete)
    {
        public ulong UserId { get; } = userId;
        internal Func<Task> Delete { get; } = delete;
    }

    public async Task<Lease> ReplaceAsync(ulong userId, Func<Task> delete)
    {
        var lease = new Lease(userId, delete);
        Lease? previous;
        do
        {
            if (!current.TryGetValue(userId, out previous))
            {
                if (current.TryAdd(userId, lease)) { break; }
            }
            else if (current.TryUpdate(userId, lease, previous)) { break; }
        } while (true);
        if (previous is not null) { await DeleteSafelyAsync(previous); }
        return lease;
    }

    public bool IsCurrent(Lease lease) => current.TryGetValue(lease.UserId, out var entry) && ReferenceEquals(entry, lease);

    public async Task ExpireAsync(Lease lease, TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, clock, cancellationToken); }
        catch (OperationCanceledException) { return; }
        current.TryRemove(new KeyValuePair<ulong, Lease>(lease.UserId, lease));
        // Also retry deletion of replaced replies if the earlier HTTP deletion failed.
        await DeleteSafelyAsync(lease);
    }

    public async Task ClearAsync()
    {
        foreach (var entry in current.ToArray())
        {
            if (current.TryRemove(entry)) { await DeleteSafelyAsync(entry.Value); }
        }
    }

    private static async Task DeleteSafelyAsync(Lease lease)
    {
        try { await lease.Delete(); }
        catch (Exception) { /* Expired/deleted responses or Discord outage must not block lifecycle operations. */ }
    }
}
