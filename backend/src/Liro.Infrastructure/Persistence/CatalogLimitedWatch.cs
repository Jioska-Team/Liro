using Liro.Domain.Catalog.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Liro.Infrastructure.Persistence;

public sealed class CatalogLimitedWatch
{
    public CatalogItemType ItemType
    {
        get; set;
    }
    public long ItemId
    {
        get; set;
    }
    public DateTime AddedAtUtc
    {
        get; set;
    }
    public DateTime NextCheckAtUtc
    {
        get; set;
    }
    public DateTime? LastConfirmedAtUtc
    {
        get; set;
    }
}

public sealed class CatalogLimitedWatchConfiguration : IEntityTypeConfiguration<CatalogLimitedWatch>
{
    public void Configure(EntityTypeBuilder<CatalogLimitedWatch> builder)
    {
        builder.ToTable("CatalogLimitedWatches");
        builder.HasKey(x => new { x.ItemType, x.ItemId });
        builder.Property(x => x.ItemType).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(x => new { x.NextCheckAtUtc, x.ItemType, x.ItemId });
    }
}
