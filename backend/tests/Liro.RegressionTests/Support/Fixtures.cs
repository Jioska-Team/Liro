using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace Liro.RegressionTests;

static class Fixtures
{
    public static Item Item() => new(1, "Item", null, ItemMarketStatus.Normal);
    public static ImportRobloxItemService Importer(MemoryRepository repo, IRobloxThumbnailClient? thumbnail = null, CatalogRefreshPolicy? refreshPolicy = null) => new(
        new CatalogStub(),
        repo,
        thumbnail ?? new PendingThumbnail(),
        NullLogger<ImportRobloxItemService>.Instance,
        new MemoryFailureStore(), refreshPolicy ?? new CatalogRefreshPolicy());
}
