using Liro.Application.Catalog.Repositories;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;

namespace Liro.Application.Catalog.Services;

public sealed class ItemService
{
    private readonly IItemRepository _repository;
    public ItemService(IItemRepository repository)
    {
        _repository = repository;
    }

    public Task<Item?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _repository.GetByIdAsync(id, cancellationToken);
    }

    public Task<Item?> GetByAssetIdAsync(long robloxAssetId, CancellationToken cancellationToken = default)
    {
        return _repository.GetByAssetIdAsync(robloxAssetId, cancellationToken);
    }

    public async Task<Item?> CreateAsync(long robloxAssetId, string name, Guid? collectibleItemId, ItemMarketStatus marketStatus, CancellationToken cancellationToken = default)
    {
        if (await _repository.ExistsByAssetIdAsync(robloxAssetId, cancellationToken))
        {
            return null;
        }

        var item = new Item(robloxAssetId, name, collectibleItemId, marketStatus);
        await _repository.AddAsync(item, cancellationToken);
        return item;
    }
}
