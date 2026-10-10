using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Liro.Infrastructure.Persistence;

public sealed class CatalogCheckpointConfiguration : IEntityTypeConfiguration<CatalogCheckpointState>
{
    public void Configure(EntityTypeBuilder<CatalogCheckpointState> builder)
    {
        builder.ToTable("CatalogCheckpoints");
        builder.HasKey(x => x.Scope);
        builder.Property(x => x.Scope).HasMaxLength(200);
    }
}
