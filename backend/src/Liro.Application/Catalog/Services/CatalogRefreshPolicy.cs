using System.Linq.Expressions;
using Liro.Domain.Catalog.Entities;

namespace Liro.Application.Catalog.Services;

public sealed class CatalogRefreshPolicy
{
    public TimeSpan ThumbnailInterval
    {
        get;
    }
    public TimeSpan ThumbnailRetryInterval { get; } = TimeSpan.FromMinutes(15);

    public bool ShouldRefreshThumbnail(Item item, DateTime now) =>
        (item.ThumbnailCheckedAtUtc ?? item.ThumbnailUpdatedAt) is not { } last
        || now - last >= (string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? ThumbnailRetryInterval : ThumbnailInterval);
    public TimeSpan Interval
    {
        get;
    }

    public CatalogRefreshPolicy(TimeSpan? interval = null, TimeSpan? thumbnailInterval = null)
    {
        ThumbnailInterval = thumbnailInterval ?? TimeSpan.FromDays(1);
        if (ThumbnailInterval < TimeSpan.FromMinutes(15) || ThumbnailInterval > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException(nameof(thumbnailInterval));
        Interval = interval ?? TimeSpan.FromMinutes(30);
        if (Interval < TimeSpan.FromMinutes(1) || Interval > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Catalog refresh interval must be between one minute and one day.");
        }
    }

    public Expression<Func<Item, bool>> GetLegacyScheduleFilter()
    {
        var interval = Interval;
        Expression<Func<Item, bool>> filter = item => interval < TimeSpan.FromDays(1)
            && item.LastCheckedAt.HasValue
            && item.NextRefreshAt == item.LastCheckedAt.Value.AddDays(1);
        return filter;
    }
}
