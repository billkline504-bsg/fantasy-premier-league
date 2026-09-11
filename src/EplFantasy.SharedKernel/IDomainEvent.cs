namespace EplFantasy.SharedKernel;

/// <summary>
/// Marker for something an <see cref="AggregateRoot{TId}"/> raises when its state changes in a way
/// other parts of the system care about (Architecture v1.15 §7's domain event catalog — e.g.
/// <c>UserRegistered</c>, <c>PlayerDrafted</c>). Deliberately has no members of its own: a
/// timestamp belongs on the concrete event (or is already on the aggregate's own <c>CreatedAt</c>-
/// style field) rather than defaulting to <c>DateTimeOffset.UtcNow</c> here, which would bypass
/// the injectable clock every deadline-dependent rule is required to go through (Testing Strategy
/// v1.0 §4) — <c>IClock</c> does not exist yet as of this task (IT-F02); it is IT-F04.
///
/// Dispatching a raised event (persisting it to the notification outbox, invoking in-process
/// handlers, etc.) is an Infrastructure-layer concern, not SharedKernel's — no dispatcher
/// implementation lives here.
/// </summary>
public interface IDomainEvent;
