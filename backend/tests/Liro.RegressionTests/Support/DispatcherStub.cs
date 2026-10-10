using Liro.Application.Common.Events;
using Liro.Domain.Common.Events;

namespace Liro.RegressionTests;

sealed class DispatcherStub : IDomainEventDispatcher
{
    public Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken ct = default) => Task.CompletedTask;
}
