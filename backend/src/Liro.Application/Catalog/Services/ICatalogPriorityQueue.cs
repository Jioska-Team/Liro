using Liro.Domain.Catalog.Models;

namespace Liro.Application.Catalog.Services;

public sealed record CatalogPriorityEntry(CatalogItemKey Key, DateTime DetectedAtUtc);
public sealed record CatalogQueueHealth(int Pending, DateTime? OldestDetectedAtUtc, int Overdue, DateTime? OldestDueAtUtc);

public interface ICatalogPriorityQueue
{
    Task EnqueueAsync(IReadOnlyCollection<CatalogItemKey> keys, DateTime detectedAtUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<CatalogPriorityEntry>> GetDueAsync(DateTime now, int limit, CancellationToken cancellationToken);
    Task CompleteAsync(CatalogPriorityEntry entry, CancellationToken cancellationToken);
    Task<CatalogQueueHealth> GetHealthAsync(DateTime now, CancellationToken cancellationToken);
}
