using Microsoft.EntityFrameworkCore;
using Liro.Infrastructure.Persistence;
using Liro.Application.Catalog.Services;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;

namespace Liro.RegressionTests;

internal static class CatalogTests
{
    public static void Register(RegressionSuite suite)
    {
        suite.AsyncTest("Successful import schedules refresh after thirty minutes", async () =>
        {
            var item = Fixtures.Item();
            await Fixtures.Importer(new MemoryRepository(item)).ImportAsync(1);
            Check.That(item.LastCheckedAt.HasValue && item.NextRefreshAt - item.LastCheckedAt.Value == TimeSpan.FromMinutes(30), "Default refresh interval was not applied");
        });
        suite.AsyncTest("Successful import uses configured refresh interval", async () =>
        {
            var item = Fixtures.Item();
            await Fixtures.Importer(new MemoryRepository(item), refreshPolicy: new CatalogRefreshPolicy(TimeSpan.FromMinutes(10))).ImportAsync(1);
            Check.That(item.LastCheckedAt.HasValue && item.NextRefreshAt - item.LastCheckedAt.Value == TimeSpan.FromMinutes(10), "Configured refresh interval was ignored");
        });
        suite.Test("Legacy schedule filter preserves current and deferred schedules", () =>
        {
            var filter = new CatalogRefreshPolicy().GetLegacyScheduleFilter().Compile();
            var item = Fixtures.Item();
            Check.That(!filter(item), "Never checked item was treated as legacy");
            item.MarkChecked(DateTime.UtcNow, TimeSpan.FromDays(1));
            Check.That(filter(item), "Legacy daily schedule was missed");
            item.MarkChecked(DateTime.UtcNow, TimeSpan.FromMinutes(30));
            Check.That(!filter(item), "Current schedule was changed");
            item.MarkChecked(DateTime.UtcNow, TimeSpan.FromHours(6));
            Check.That(!filter(item), "Deferred schedule was changed");
            item.MarkChecked(DateTime.UtcNow, TimeSpan.FromDays(1));
            Check.That(!new CatalogRefreshPolicy(TimeSpan.FromDays(1)).GetLegacyScheduleFilter().Compile()(item), "Unchanged interval should not reschedule");
        });
        suite.Test("Legacy schedule query translates to PostgreSQL", () =>
        {
            using var db = new LiroDbContext(new DbContextOptionsBuilder<LiroDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
            var policy = new CatalogRefreshPolicy();
            var interval = policy.Interval;
            var sql = db.Items.Where(policy.GetLegacyScheduleFilter())
                .Where(item => !db.Set<CatalogImportFailure>().Any(failure => failure.AssetId == item.RobloxAssetId))
                .Select(item => item.LastCheckedAt!.Value.AddMinutes(interval.TotalMinutes)).ToQueryString();
            Check.That(sql.Contains("NOT EXISTS") && sql.Contains("INTERVAL '1 days'") && sql.Contains("LastCheckedAt") && sql.Contains("mins"), "Legacy filter or failure exclusion was not translated: " + sql);
        });
        suite.Test("Refresh policy rejects unsafe intervals", () =>
        {
            Check.Throws<ArgumentOutOfRangeException>(() => new CatalogRefreshPolicy(TimeSpan.Zero));
            Check.Throws<ArgumentOutOfRangeException>(() => new CatalogRefreshPolicy(TimeSpan.FromSeconds(59)));
            Check.Throws<ArgumentOutOfRangeException>(() => new CatalogRefreshPolicy(TimeSpan.FromDays(2)));
            Check.That(new CatalogRefreshPolicy(TimeSpan.FromMinutes(1)).Interval == TimeSpan.FromMinutes(1), "Minimum interval rejected");
        });
        suite.Test("Marketplace URL uses asset identity and survives renaming", () =>
        {
            var item = new Item(12803855954, "Glossy Red Baseball Cap");
            const string expected = "https://www.roblox.com/catalog/12803855954";
            Check.That(item.MarketplaceUrl == expected, "Incorrect marketplace URL");
            item.UpdateCatalogData("Gorra roja / ? #", null, ItemMarketStatus.Normal);
            Check.That(item.MarketplaceUrl == expected, "Renaming changed the URL");
            Check.That(Liro.Api.Contracts.ItemResponse.From(item).MarketplaceUrl == expected, "API omitted the URL");
            Check.That(new Item(long.MaxValue, "Item").MarketplaceUrl == "https://www.roblox.com/catalog/9223372036854775807", "Asset ID lost precision");
        });
        suite.Test("Reject nonpositive asset id", () => Check.Throws<ArgumentException>(() => new Item(-1, "Item")));
        suite.Test("Reject whitespace name", () => Check.Throws<ArgumentException>(() => new Item(1, "  ")));
        suite.Test("Reject oversized name", () => Check.Throws<ArgumentException>(() => new Item(1, new string('x', 201))));
        suite.Test("Reject undefined market status", () => Check.Throws<ArgumentException>(() => new Item(1, "Item", null, (ItemMarketStatus)999)));
        suite.Test("Keep known thumbnail when provider is pending", () =>
        {
            var item = Fixtures.Item();
            item.UpdateThumbnail("https://example.com/a.webp");
            item.UpdateThumbnail(null);
            Check.That(item.ThumbnailUrl == "https://example.com/a.webp", "Pending image changed the known URL");
        });
        suite.Test("Emit transition once", () =>
        {
            var item = Fixtures.Item();
            item.UpdateCatalogData("Item", null, ItemMarketStatus.Limited);
            item.UpdateCatalogData("Item", null, ItemMarketStatus.Limited);
            Check.That(item.DomainEvents.Count == 1, "Transition was lost or duplicated");
        });
        suite.AsyncTest("Persist successful unchanged catalog check", async () =>
        {
            var repo = new MemoryRepository(Fixtures.Item());
            await Fixtures.Importer(repo).ImportAsync(1);
            Check.That(repo.Saves == 1, "Successful check was not persisted");
        });
        suite.AsyncTest("Thumbnail outage does not block catalog update", async () =>
        {
            var repo = new MemoryRepository(Fixtures.Item());
            await Fixtures.Importer(repo, new FailingThumbnail()).ImportAsync(1);
            Check.That(repo.Saves == 1, "Catalog was not saved");
        });
        suite.Test("Freshness check preserves content timestamp", () =>
        {
            var item = Fixtures.Item();
            var changedAt = item.LastUpdatedAt;
            var now = DateTime.UtcNow.AddMinutes(1);
            item.MarkChecked(now, TimeSpan.FromHours(24));
            Check.That(
                item.LastUpdatedAt == changedAt && item.LastCheckedAt == now && item.NextRefreshAt == now.AddHours(24),
                "Content and check times were conflated");
        });
    }
}
