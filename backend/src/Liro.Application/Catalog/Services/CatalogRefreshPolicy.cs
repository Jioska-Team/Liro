using System.Linq.Expressions;
using Liro.Domain.Catalog.Entities;

namespace Liro.Application.Catalog.Services;

public sealed class CatalogRefreshPolicy
{
    public TimeSpan Interval
    {
        get;
    }

    public CatalogRefreshPolicy(TimeSpan? interval = null)
    {
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
