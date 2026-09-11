namespace EplFantasy.SharedKernel;

/// <summary>
/// An <see cref="Entity{TId}"/> that is the transactional consistency boundary for its own
/// invariants (Architecture v1.15 §6's aggregate roots — <c>User</c>, <c>League</c>,
/// <c>Draft</c>, etc.) and the only thing in its cluster of objects that may raise domain events.
/// A concrete aggregate's own methods enforce its invariants directly (Architecture §13: "throws
/// domain exceptions rather than allowing an invalid state to be persisted") — this base class
/// supplies only the domain-event bookkeeping every aggregate needs, never a business rule.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id) : base(id)
    {
    }

    /// <summary>
    /// Events raised since this aggregate was loaded (or created), not yet dispatched. Read-only
    /// from outside the aggregate — only <see cref="Raise"/> (called by the aggregate's own
    /// methods) may add to it.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>
    /// Records that this aggregate's state change should notify the rest of the system. Called
    /// from within the aggregate's own methods, alongside the state change it describes — never
    /// from outside the aggregate, and never as a substitute for the aggregate enforcing its own
    /// invariant first.
    /// </summary>
    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    /// <summary>
    /// Called by the infrastructure layer once <see cref="DomainEvents"/> have been dispatched
    /// (e.g. after a successful <c>SaveChanges</c>) — never by domain/application code, which
    /// should have no reason to discard an event before it's been acted on.
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}
