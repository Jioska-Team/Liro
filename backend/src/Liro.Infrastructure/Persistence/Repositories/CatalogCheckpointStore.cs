using Liro.Application.Catalog.Services;
using Microsoft.EntityFrameworkCore;

namespace Liro.Infrastructure.Persistence;

public sealed class CatalogCheckpointStore(LiroDbContext db) : ICatalogCheckpointStore
{
    public async Task<CatalogCheckpoint> LoadAsync(string scope, CancellationToken cancellationToken)
    {
        var state = await db.Set<CatalogCheckpointState>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Scope == scope, cancellationToken);
        var checkpoint = state is null ? new CatalogCheckpoint(null, false) : new CatalogCheckpoint(state.Cursor, state.Completed)
        {
            ItemsSeen = state.ItemsSeen,
            PagesProcessed = state.PagesProcessed,
            ItemsAdded = state.ItemsAdded,
            ItemFailures = state.ItemFailures,
            CoverageStatus = state.CoverageStatus,
            LastError = state.LastError
        };

        return checkpoint;
    }

    public async Task SaveAsync(string scope, CatalogCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "CatalogCheckpoints" ("Scope", "Cursor", "Completed", "UpdatedAtUtc", "ItemsSeen", "PagesProcessed", "ItemsAdded", "ItemFailures", "CoverageStatus", "LastError")
            VALUES ({scope}, {checkpoint.Cursor}, {checkpoint.Completed}, {now}, {checkpoint.ItemsSeen}, {checkpoint.PagesProcessed}, {checkpoint.ItemsAdded}, {checkpoint.ItemFailures}, {checkpoint.CoverageStatus}, {checkpoint.LastError})
            ON CONFLICT ("Scope") DO UPDATE SET
            "Cursor" = EXCLUDED."Cursor", "Completed" = EXCLUDED."Completed", "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc",
            "ItemsSeen" = EXCLUDED."ItemsSeen", "PagesProcessed" = EXCLUDED."PagesProcessed",
            "ItemsAdded" = EXCLUDED."ItemsAdded", "ItemFailures" = EXCLUDED."ItemFailures",
            "CoverageStatus" = EXCLUDED."CoverageStatus", "LastError" = EXCLUDED."LastError"
            """,
            cancellationToken);
    }
}

