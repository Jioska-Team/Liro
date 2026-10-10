namespace Liro.Infrastructure.Persistence.Outbox;

public sealed class OutboxMessage
{
    public Guid Id
    {
        get; set;
    }
    public string Type { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime OccurredAtUtc
    {
        get; set;
    }
    public DateTime? ProcessedAtUtc
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }
    public int Attempts
    {
        get; set;
    }
    public DateTime NextAttemptAtUtc
    {
        get; set;
    }
    public DateTime? DeadLetteredAtUtc
    {
        get; set;
    }

    public void RecordFailure(string error, DateTime now)
    {
        Attempts++;
        Error = error.Length > 2000 ? error[..2000] : error;
        NextAttemptAtUtc = now.AddSeconds(Math.Min(3600, Math.Pow(2, Math.Min(Attempts, 12)) * 5));
        if (Attempts >= 8)
        {
            DeadLetteredAtUtc = now;
        }
    }
}
