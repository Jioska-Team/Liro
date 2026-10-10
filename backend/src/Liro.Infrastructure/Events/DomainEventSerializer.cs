using System.Text.Json;
using Liro.Domain.Catalog.Events;
using Liro.Domain.Common.Events;

namespace Liro.Infrastructure.Events;

public static class DomainEventSerializer
{
    public static string GetName(IDomainEvent domainEvent) => domainEvent switch
    {
        ItemBecameLimited => "catalog.item-became-limited.v1",
        _ => throw new InvalidOperationException("Unregistered domain event type.")
    };
    public static IDomainEvent Deserialize(string name, string content)
    {
        // Existing rows used assembly-qualified CLR names. Accept only the known legacy event.
        if (name == "catalog.item-became-limited.v1" || name.StartsWith("Liro.Domain.Catalog.Events.ItemBecameLimited,", StringComparison.Ordinal))
        {
            return JsonSerializer.Deserialize<ItemBecameLimited>(content) ?? throw new JsonException("Empty domain event.");
        }

        throw new InvalidOperationException("Unknown event contract: " + name);
    }
}
