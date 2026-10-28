using CollabDesk.Domain;

namespace CollabDesk.Application;

/// <summary>
/// An at-least-once outbox consumer. Implementations must make the side effect idempotent using
/// <see cref="OutboxEvent.Id"/> as the stable event key; a transport acknowledgement can be lost.
/// </summary>
public interface IOutboxEventDispatcher
{
    Task DispatchAsync(OutboxEvent @event, CancellationToken ct);
}
