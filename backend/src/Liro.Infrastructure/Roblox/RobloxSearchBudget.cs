namespace Liro.Infrastructure.Roblox;

// A separate shared window for catalog searches, inside the global request ceiling.
public sealed class RobloxSearchBudget(IRobloxRequestBudget budget)
{
    public IRobloxRequestBudget Budget { get; } = budget;
}
