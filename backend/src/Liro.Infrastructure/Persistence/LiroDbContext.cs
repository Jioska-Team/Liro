using System.Text.Json;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Common.Events;
using Liro.Infrastructure.Events;
using Liro.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Liro.Infrastructure.Persistence;

public sealed class LiroDbContext(DbContextOptions<LiroDbContext> options) : DbContext(options)
{
    private readonly Dictionary<IDomainEvent, OutboxMessage> pendingEvents = new(ReferenceEqualityComparer.Instance);
    public DbSet<Item> Items => Set<Item>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LiroDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var marketItems = CaptureMarketSnapshots();
        var entities = CaptureEvents();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        ClearEvents(entities);
        CompleteMarketSave(marketItems);
        return result;
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => SaveChangesAsync(true, cancellationToken);
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var marketItems = CaptureMarketSnapshots();
        var entities = CaptureEvents();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        ClearEvents(entities);
        CompleteMarketSave(marketItems);
        return result;
    }

    private List<Item> CaptureMarketSnapshots()
    {
        var items = ChangeTracker.Entries<Item>()
            .Where(x => x.Entity.PendingMarketSnapshots.Count > 0)
            .Select(x => x.Entity)
            .ToList();
        foreach (var item in items)
        {
            if (item.MarketState!.IsNew)
            {
                Entry(item.MarketState).State = EntityState.Added;
            }

            foreach (var snapshot in item.PendingMarketSnapshots)
            {
                if (Entry(snapshot).State == EntityState.Detached)
                {
                    Set<ItemMarketSnapshot>().Add(snapshot);
                }
            }
        }

        return items;
    }

    private static void CompleteMarketSave(List<Item> items)
    {
        foreach (var item in items)
        {
            item.MarkMarketPersisted();
        }
    }

    private List<IHasDomainEvents> CaptureEvents()
    {
        var entities = ChangeTracker.Entries<IHasDomainEvents>().Where(x => x.Entity.DomainEvents.Count > 0).Select(x => x.Entity).ToList();
        foreach (var domainEvent in entities.SelectMany(x => x.DomainEvents))
        {
            if (!pendingEvents.TryGetValue(domainEvent, out var message))
            {
                message = new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    Type = DomainEventSerializer.GetName(domainEvent),
                    Content = JsonSerializer.Serialize(domainEvent, domainEvent.GetType()),
                    OccurredAtUtc = domainEvent.OccurredAtUtc,
                    NextAttemptAtUtc = domainEvent.OccurredAtUtc
                };
                pendingEvents.Add(domainEvent, message);
            }

            if (Entry(message).State == EntityState.Detached)
            {
                OutboxMessages.Add(message);
            }
        }

        return entities;
    }

    private void ClearEvents(List<IHasDomainEvents> entities)
    {
        foreach (var entity in entities)
        {
            entity.ClearDomainEvents();
        }

        pendingEvents.Clear();
    }
}
