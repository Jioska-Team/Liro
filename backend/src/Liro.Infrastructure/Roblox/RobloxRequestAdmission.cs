using System.Diagnostics;
using Liro.Application.RobloxIntegration;

namespace Liro.Infrastructure.Roblox;

public static class RobloxRequestAdmission
{
    public static async Task<CancellationTokenSource> CreateAsync(CancellationToken ct,
        params (IRobloxRequestBudget Budget, string Id)[] reservations)
    {
        var started = Stopwatch.GetTimestamp();
        var allowance = TimeSpan.FromSeconds(30);
        foreach (var (budget, id) in reservations)
        {
            var remaining = await budget.GetSendAllowanceAsync(id, ct);
            if (remaining < allowance)
                allowance = remaining;
        }
        allowance -= Stopwatch.GetElapsedTime(started);
        ct.ThrowIfCancellationRequested();
        if (allowance <= TimeSpan.Zero)
            throw new RobloxRateLimitedException(DateTimeOffset.UtcNow.AddSeconds(1));
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(allowance);
        return deadline;
    }
}
