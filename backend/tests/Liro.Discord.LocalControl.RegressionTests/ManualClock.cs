namespace Liro.Discord.LocalControl.RegressionTests;

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> timers = [];
    public override DateTimeOffset GetUtcNow() => now;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        timers.Add(timer);
        return timer;
    }
    public void Advance(TimeSpan amount)
    {
        now += amount;
        foreach (var timer in timers.ToArray()) { timer.Fire(now); }
    }
    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? due;
        private TimeSpan period;
        public bool Change(TimeSpan dueTime, TimeSpan repeat)
        {
            due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
            period = repeat;
            return true;
        }
        public void Fire(DateTimeOffset time)
        {
            if (due is null || due > time) { return; }
            due = period > TimeSpan.Zero ? time + period : null;
            callback(state);
        }
        public void Dispose() { due = null; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
