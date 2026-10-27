using Liro.Infrastructure.Market;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Liro.Infrastructure.Persistence.Configurations;

public sealed class MarketDashboardSnapshotConfiguration : IEntityTypeConfiguration<MarketDashboardSnapshot>
{
    public void Configure(EntityTypeBuilder<MarketDashboardSnapshot> builder)
    {
        builder.ToTable("MarketDashboardSnapshots");
        builder.HasKey(x => x.BucketUtc);
        builder.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
    }
}
