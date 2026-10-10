using Liro.Domain.Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Liro.Infrastructure.Persistence.Configurations;

public sealed class ItemMarketSnapshotConfiguration : IEntityTypeConfiguration<ItemMarketSnapshot>
{
    public void Configure(EntityTypeBuilder<ItemMarketSnapshot> builder)
    {
        builder.ToTable("ItemMarketSnapshots");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Source).HasMaxLength(200);
        builder.ComplexProperty(x => x.Data);
        builder.HasIndex(x => new { x.ItemId, x.ObservedAtUtc }).IsUnique();
        builder.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
    }
}
