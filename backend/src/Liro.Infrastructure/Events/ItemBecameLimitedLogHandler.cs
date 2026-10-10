using Liro.Application.Common.Events;
using Liro.Domain.Catalog.Events;
using Microsoft.Extensions.Logging;

namespace Liro.Infrastructure.Events;

public sealed class ItemBecameLimitedLogHandler(ILogger<ItemBecameLimitedLogHandler> logger) : IDomainEventHandler<ItemBecameLimited>
{
    public Task HandleAsync(ItemBecameLimited domainEvent, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Item {ItemId} with RobloxAssetId {RobloxAssetId} became Limited.",
            domainEvent.ItemId,
            domainEvent.RobloxAssetId);
        return Task.CompletedTask;
    }
}
