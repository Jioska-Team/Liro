namespace Liro.Domain.Common.Events;

public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents
    {
        get;
    }

    void ClearDomainEvents();
}
