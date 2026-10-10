using Liro.Domain.Common.Events;

namespace Liro.Domain.Catalog.Events;

public sealed record ItemBecameLimited(Guid ItemId, long RobloxAssetId, DateTime OccurredAtUtc) : IDomainEvent;
