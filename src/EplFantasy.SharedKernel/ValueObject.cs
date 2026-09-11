namespace EplFantasy.SharedKernel;

/// <summary>
/// Base type for anything defined entirely by its attributes, with no identity of its own — e.g.
/// a future <c>Username</c> or <c>PositionalMinimums</c> wrapper. Equality is structural (every
/// component in <see cref="GetEqualityComponents"/> matches), the opposite of <see cref="Entity{TId}"/>.
/// </summary>
public abstract class ValueObject : IEquatable<ValueObject>
{
    /// <summary>
    /// Yields every field this value object's equality/hash code is computed from, in a stable
    /// order. Implementations should yield the same components used to construct the value —
    /// never a derived/computed one that could disagree with the constructor's inputs.
    /// </summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public bool Equals(ValueObject? other)
    {
        if (other is null || GetType() != other.GetType())
        {
            return false;
        }

        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    public override bool Equals(object? obj) => Equals(obj as ValueObject);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var component in GetEqualityComponents())
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(ValueObject? left, ValueObject? right) => Equals(left, right);

    public static bool operator !=(ValueObject? left, ValueObject? right) => !Equals(left, right);
}
