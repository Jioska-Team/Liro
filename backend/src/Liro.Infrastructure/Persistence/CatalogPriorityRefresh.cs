using Liro.Domain.Catalog.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Liro.Infrastructure.Persistence;

public sealed class CatalogPriorityRefresh
{
    public long ItemId
    {
        get; set;
    }
    public CatalogItemType ItemType
    {
        get; set;
    }
    public DateTime DetectedAtUtc
    {
        get; set;
    }
}

public sealed class CatalogPriorityRefreshConfiguration : IEntityTypeConfiguration<CatalogPriorityRefresh>
{
    public void Configure(EntityTypeBuilder<CatalogPriorityRefresh> builder)
    {
        builder.ToTable("CatalogPriorityRefreshes");
        builder.HasKey(x => new { x.ItemType, x.ItemId });
        builder.Property(x => x.ItemType).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(x => x.DetectedAtUtc);
    }
}
