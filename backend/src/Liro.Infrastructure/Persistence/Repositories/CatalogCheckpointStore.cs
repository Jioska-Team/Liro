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
        var checkpoint = new CatalogCheckpoint(state?.Cursor, state?.Completed ?? false);

        return checkpoint;
    }

    public async Task SaveAsync(string scope, CatalogCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "CatalogCheckpoints" ("Scope", "Cursor", "Completed", "UpdatedAtUtc")
            VALUES ({scope}, {checkpoint.Cursor}, {checkpoint.Completed}, {now})
            ON CONFLICT ("Scope") DO UPDATE SET
            "Cursor" = EXCLUDED."Cursor", "Completed" = EXCLUDED."Completed", "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc"
            """,
            cancellationToken);
    }
}
