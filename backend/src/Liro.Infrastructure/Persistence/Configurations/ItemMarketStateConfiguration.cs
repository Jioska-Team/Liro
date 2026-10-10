using Liro.Domain.Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Liro.Infrastructure.Persistence.Configurations;

public sealed class ItemMarketStateConfiguration : IEntityTypeConfiguration<ItemMarketState>
{
    public void Configure(EntityTypeBuilder<ItemMarketState> builder)
    {
        builder.ToTable("ItemMarketState");
        builder.HasKey(x => x.ItemId);
        builder.Property(x => x.ItemId).ValueGeneratedNever();
        builder.Ignore(x => x.IsNew);
        builder.Property(x => x.Source).HasMaxLength(200);
        builder.ComplexProperty(x => x.Data);
    }
}
