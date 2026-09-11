namespace EplFantasy.SharedKernel;

/// <summary>
/// Base type for anything with a persistent identity that survives state changes (Architecture
/// v1.15 §14 — "shared identity/value-object plumbing only, never business rules"). Equality is
/// identity-based (same concrete type, same <see cref="Id"/>), never structural — that is what
/// distinguishes an Entity from a <see cref="ValueObject"/>.
/// </summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    /// <summary>
    /// Immutable once assigned — every aggregate in this domain (BR-002's "immutable internal
    /// identifier" is the pattern, not the exception) treats its identity as never reassignable.
    /// </summary>
    public TId Id { get; }

    protected Entity(TId id)
    {
        Id = id;
    }

    public bool Equals(Entity<TId>? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        // Two entities of different concrete types (e.g. a User and a League that happened to
        // reuse the same underlying Guid) are never equal, even if TId matches.
        if (GetType() != other.GetType())
        {
            return false;
        }

        return EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

    public override bool Equals(object? obj) => Equals(obj as Entity<TId>);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) => Equals(left, right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !Equals(left, right);
}
