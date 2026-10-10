namespace Liro.Domain.Common.Events;

public interface IDomainEvent
{
    DateTime OccurredAtUtc
    {
        get;
    }
}
