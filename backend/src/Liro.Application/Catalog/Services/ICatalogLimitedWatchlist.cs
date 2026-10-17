using Liro.Domain.Catalog.Models;

namespace Liro.Application.Catalog.Services;

public sealed record CatalogLimitedLease(CatalogItemKey Key, DateTime LeaseUntilUtc);
public sealed record CatalogLimitedWatchHealth(int Total, int Pending, int Failed, DateTime? OldestConfirmedAtUtc);

public interface ICatalogLimitedWatchlist
{
    Task TrackAsync(IReadOnlyCollection<CatalogItemKey> keys, DateTime now, CancellationToken ct);
    Task SeedKnownAsync(DateTime now, CancellationToken ct);
    Task<IReadOnlyList<CatalogLimitedLease>> ClaimAsync(DateTime now, int limit, TimeSpan lease, CancellationToken ct);
    Task CompleteAsync(IReadOnlyList<CatalogLimitedLease> entries, DateTime now, DateTime nextCheck, bool succeeded, CancellationToken ct);
    async Task CompleteObservedAsync(IReadOnlyList<CatalogLimitedLease> entries, IReadOnlyDictionary<CatalogItemKey, DateTime> observations, TimeSpan interval, CancellationToken ct)
    {
        foreach (var group in entries.GroupBy(x => observations[x.Key]))
            await CompleteAsync(group.ToArray(), group.Key, group.Key.Add(interval), true, ct);
    }
    Task<CatalogLimitedWatchHealth> GetHealthAsync(DateTime now, CancellationToken ct);
}
