using Liro.Domain.Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Liro.Infrastructure.Persistence.Configurations;

public sealed class BundleComponentConfiguration : IEntityTypeConfiguration<BundleComponent>
{
    public void Configure(EntityTypeBuilder<BundleComponent> builder)
    {
        builder.ToTable("BundleComponents", table => table.HasCheckConstraint("CK_BundleComponents_DifferentItems", "\"BundleId\" <> \"ComponentItemId\""));
        builder.HasKey(x => new { x.BundleId, x.ComponentItemId });
        builder.HasOne<Item>().WithMany().HasForeignKey(x => x.BundleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Item>().WithMany().HasForeignKey(x => x.ComponentItemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.ComponentItemId);
    }
}
