using Liro.Application.Catalog.Services;
using Liro.Application.RobloxIntegration.Models;
using Liro.Domain.Catalog.Entities;
using Liro.Domain.Catalog.Enums;

namespace Liro.RegressionTests;

internal static class LimitedConversionTests
{
    public static void Register(RegressionSuite suite)
    {
        var start = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        foreach (var status in new[] { ItemMarketStatus.Limited, ItemMarketStatus.LimitedUnique })
        {
            suite.Test("LimitedConversion_InitialImportHasOnlyObservation_" + status, () =>
            {
                var result = ImportRobloxItemService.ApplyObservation(null,
                    new RobloxCatalogItem(100, "Imported Limited", null, status) { ObservedAtUtc = start });
                Check.That(result.Save && result.Item.LimitedFirstObservedAtUtc == start
                    && result.Item.BecameLimitedAtUtc is null && result.Item.HasBeenLimited
                    && result.Item.DomainEvents.Count == 0, "Initial import fabricated a conversion");
            });
            foreach (var previous in new[] { ItemMarketStatus.Normal, ItemMarketStatus.Collectible })
                suite.Test("LimitedConversion_KnownStateUsesObservation_" + previous + "_" + status, () =>
                {
                    var item = new Item(1, "Old", marketStatus: previous, observedAtUtc: start);
                    var detected = start.AddMinutes(5);
                    item.UpdateCatalogData("New", null, status, detected);
                    Check.That(item.BecameLimitedAtUtc == detected && item.LimitedFirstObservedAtUtc == detected
                        && item.MarketStatusObservedAtUtc == detected && item.HasBeenLimited,
                        "Conversion must use the actual source observation time");
                    Check.That(item.DomainEvents.Single().OccurredAtUtc == detected, "Event time differs from detection");
                    item.UpdateCatalogData("Renamed", null, ItemMarketStatus.LimitedUnique, detected.AddMinutes(1));
                    item.UpdateCatalogData("Renamed", null, ItemMarketStatus.Limited, detected.AddMinutes(2));
                    Check.That(item.DomainEvents.Count == 1 && item.BecameLimitedAtUtc == detected,
                        "Metadata changes or Limited subtype updates reordered the conversion");
                });
        }
        suite.Test("LimitedConversion_KnownLimitedNeverGetsASecondConversion", () =>
        {
            var item = new Item(1, "Initial", marketStatus: ItemMarketStatus.Limited, observedAtUtc: start);
            item.UpdateCatalogData("Normal", null, ItemMarketStatus.Normal, start.AddMinutes(1));
            item.UpdateCatalogData("Limited", null, ItemMarketStatus.Limited, start.AddMinutes(2));
            Check.That(item.BecameLimitedAtUtc is null && item.LimitedFirstObservedAtUtc == start
                && item.DomainEvents.Count == 0, "A previously known Limited was treated as newly converted");
        });
        suite.Test("LimitedConversion_UnknownDoesNotEraseKnownState", () =>
        {
            var item = new Item(1, "Normal", marketStatus: ItemMarketStatus.Normal, observedAtUtc: start);
            item.UpdateCatalogData("Incomplete", null, ItemMarketStatus.Unknown, start.AddMinutes(1));
            Check.That(item.MarketStatus == ItemMarketStatus.Normal && item.BecameLimitedAtUtc is null,
                "Incomplete classification erased the known normal state");
            item.UpdateCatalogData("Limited", null, ItemMarketStatus.Limited, start.AddMinutes(2));
            Check.That(item.BecameLimitedAtUtc == start.AddMinutes(2) && item.DomainEvents.Count == 1,
                "An incomplete response lost the subsequent confirmed transition");
        });
        foreach (var source in new[] { "RobloxCatalogSearchV2", "RobloxCatalogDetailsV1" })
            suite.Test("LimitedConversion_UnchangedObservationRejectsOlderAndEqualResponses_" + source, () =>
            {
                var item = new Item(1, "Normal", marketStatus: ItemMarketStatus.Normal, observedAtUtc: start);
                var before = item.LastUpdatedAt;
                var observation = new RobloxCatalogItem(1, "Normal", null, ItemMarketStatus.Normal)
                    { ObservedAtUtc = start.AddMinutes(3), Source = source };
                var unchanged = ImportRobloxItemService.ApplyObservation(item, observation);
                Check.That(unchanged.Save && item.MarketStatusObservedAtUtc == observation.ObservedAtUtc
                    && item.LastUpdatedAt == before, "Freshness was lost or changed the content timestamp");
                foreach (var time in new[] { start.AddMinutes(2), start.AddMinutes(3) })
                {
                    var rejected = ImportRobloxItemService.ApplyObservation(item,
                        observation with { Name = "Stale Limited", MarketStatus = ItemMarketStatus.Limited, ObservedAtUtc = time });
                    Check.That(!rejected.Save && item.MarketStatus == ItemMarketStatus.Normal && item.Name == "Normal"
                        && item.BecameLimitedAtUtc is null && item.DomainEvents.Count == 0,
                        "Old or conflicting equal-time observation created a conversion");
                }
            });
        suite.Test("LimitedConversion_RejectsNonUtcObservationWithoutMutation", () =>
        {
            var item = new Item(1, "Normal", marketStatus: ItemMarketStatus.Normal);
            Check.Throws<ArgumentException>(() => item.UpdateCatalogData("Bad", null, ItemMarketStatus.Limited,
                DateTime.SpecifyKind(start, DateTimeKind.Unspecified)));
            Check.That(item.MarketStatus == ItemMarketStatus.Normal && item.BecameLimitedAtUtc is null
                && item.MarketStatusObservedAtUtc is null && item.DomainEvents.Count == 0, "Invalid time mutated the item");
        });
        suite.Test("LimitedConversion_UnknownIsNotAConversion", () =>
        {
            var item = new Item(1, "Unknown");
            item.UpdateCatalogData("Limited", null, ItemMarketStatus.Limited);
            Check.That(item.MarketStatus == ItemMarketStatus.Limited && item.DomainEvents.Count == 0
                && item.BecameLimitedAtUtc is null && item.LimitedFirstObservedAtUtc.HasValue,
                "Discovering a Limited from unknown state must not emit a conversion");
        });
        suite.Test("LimitedConversion_StaleDetailsCannotConvert", () =>
        {
            var item = new Item(1, "Normal", marketStatus: ItemMarketStatus.Normal);
            var now = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
            item.MarkChecked(now, TimeSpan.FromMinutes(30));
            var result = ImportRobloxItemService.ApplyObservation(item,
                new RobloxCatalogItem(1, "Stale", null, ItemMarketStatus.Limited) { ObservedAtUtc = now.AddMinutes(-1) });
            Check.That(!result.Save && item.MarketStatus == ItemMarketStatus.Normal && item.DomainEvents.Count == 0,
                "Stale detail response changed the known state");
        });
    }
}
