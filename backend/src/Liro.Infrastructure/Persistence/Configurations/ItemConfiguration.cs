using Liro.Domain.Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Liro.Infrastructure.Persistence.Configurations;

public sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("Items");
        builder.Ignore(x => x.DomainEvents);
        builder.Ignore(x => x.PendingMarketSnapshots);
        builder.Property(x => x.Restrictions).HasColumnType("text[]");
        builder.HasOne(x => x.MarketState).WithOne().HasForeignKey<ItemMarketState>(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Version).IsRowVersion();
        builder.HasIndex(x => x.NextRefreshAt);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.RobloxAssetId).IsRequired();
        // Generated for existing rows too; item names and localization are not URL identifiers.
        builder.Property(x => x.MarketplaceUrl)
            .HasComputedColumnSql("'https://www.roblox.com/catalog/' || \"RobloxAssetId\"::text", stored: true);
        builder.HasIndex(x => x.RobloxAssetId).IsUnique();
        builder.Property(x => x.CollectibleItemId);
        builder.HasIndex(x => x.CollectibleItemId).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.MarketStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.FirstSeenAt).IsRequired();
        builder.Property(x => x.LastUpdatedAt).IsRequired();
        builder.Property(x => x.ThumbnailUrl).HasMaxLength(1000);
        builder.Property(x => x.ThumbnailUpdatedAt);
    }
}
