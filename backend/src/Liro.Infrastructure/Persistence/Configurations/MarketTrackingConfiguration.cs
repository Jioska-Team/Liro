using Liro.Domain.Catalog.Entities;
using Liro.Infrastructure.Market;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Liro.Infrastructure.Persistence.Configurations;

public sealed class GeneralCatalogConfiguration : IEntityTypeConfiguration<CatalogoGeneral>
{
    public void Configure(EntityTypeBuilder<CatalogoGeneral> b)
    {
        b.ToTable("CatalogoGeneral");
        b.HasKey(x => x.Id);
        b.Property(x => x.Tipo).HasMaxLength(80);
        b.Property(x => x.Clave).HasMaxLength(120);
        b.Property(x => x.Valor).HasMaxLength(16000);
        b.Property(x => x.TipoDato).HasMaxLength(20);
        b.Property(x => x.Descripcion).HasMaxLength(1000);
        b.Property(x => x.CreadoPor).HasMaxLength(120);
        b.Property(x => x.ModificadoPor).HasMaxLength(120);
        b.HasIndex(x => new { x.Tipo, x.Clave }).IsUnique();
    }
}
public sealed class MarketWatchConfiguration : IEntityTypeConfiguration<MarketWatch>
{
    public void Configure(EntityTypeBuilder<MarketWatch> b)
    {
        b.ToTable("MarketWatches");
        b.HasKey(x => x.ItemId);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.RecentAveragePrice).HasPrecision(28, 6);
        b.Property(x => x.DailySales).HasPrecision(28, 6);
        b.Property(x => x.Activity).HasMaxLength(30);
        b.Property(x => x.LastErrorCode).HasMaxLength(60);
        b.HasIndex(x => new { x.NextCheckAtUtc, x.LeaseUntilUtc });
    }
}
public sealed class MarketHistoryConfiguration : IEntityTypeConfiguration<MarketHistoryPoint>
{
    public void Configure(EntityTypeBuilder<MarketHistoryPoint> b)
    {
        b.ToTable("MarketHistoryPoints");
        b.HasKey(x => new { x.ItemId, x.Source, x.Metric, x.PeriodUtc });
        b.HasIndex(x => new { x.PeriodUtc, x.Source, x.Metric }).IncludeProperties(x => new { x.ItemId, x.Value, x.ObservedAtUtc });
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.Source).HasMaxLength(40);
        b.Property(x => x.Metric).HasMaxLength(30);
        b.Property(x => x.Value).HasPrecision(28, 6);
    }
}
public sealed class MarketRapSnapshotConfiguration : IEntityTypeConfiguration<MarketRapSnapshot>
{
    public void Configure(EntityTypeBuilder<MarketRapSnapshot> b)
    {
        b.ToTable("MarketRapSnapshots");
        b.HasKey(x => new { x.ItemId, x.ObservedAtUtc });
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.Value).HasPrecision(28, 6);
    }
}
