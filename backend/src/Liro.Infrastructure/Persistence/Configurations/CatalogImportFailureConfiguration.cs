using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Liro.Infrastructure.Persistence;

public sealed class CatalogImportFailureConfiguration : IEntityTypeConfiguration<CatalogImportFailure>
{
    public void Configure(EntityTypeBuilder<CatalogImportFailure> builder)
    {
        builder.ToTable("CatalogImportFailures");
        builder.HasKey(x => x.AssetId);
        builder.Property(x => x.AssetId).ValueGeneratedNever();
        builder.Property(x => x.Error).HasMaxLength(2000);
        builder.HasIndex(x => x.NextAttemptAtUtc).HasFilter("\"DeadLetteredAtUtc\" IS NULL");
    }
}
