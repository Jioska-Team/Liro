using Liro.Domain.Catalog.Enums;
namespace Liro.Domain.Catalog.Models;

public readonly record struct CatalogItemKey(long Id, CatalogItemType Type = CatalogItemType.Asset)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Id);
        if (!Enum.IsDefined(Type))
        {
            throw new ArgumentOutOfRangeException(nameof(Type));
        }
    }
}

