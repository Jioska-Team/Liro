namespace Liro.RegressionTests;

sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
