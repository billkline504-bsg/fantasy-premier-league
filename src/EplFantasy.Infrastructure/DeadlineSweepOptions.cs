namespace EplFantasy.Infrastructure;

/// <summary>Bound from configuration (the "DeadlineSweep" section).</summary>
public sealed class DeadlineSweepOptions
{
    public const string SectionName = "DeadlineSweep";

    /// <summary>ADR-012: "a shared, short-interval scheduled background sweep (e.g., every 15–30 seconds)."</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(20);
}
