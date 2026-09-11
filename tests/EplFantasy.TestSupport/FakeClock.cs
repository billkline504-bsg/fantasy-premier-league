using EplFantasy.SharedKernel;

namespace EplFantasy.TestSupport;

/// <summary>
/// A settable/advanceable <see cref="IClock"/> for deterministic tests of deadline/timer-dependent
/// rules (Testing Strategy v1.0 §4) — invitation expiry, draft timers, roster-lock deadlines, the
/// ADR-012 sweep. Never referenced outside test projects.
/// </summary>
public sealed class FakeClock(DateTimeOffset initialUtcNow) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = initialUtcNow;

    public static FakeClock StartingAt(DateTimeOffset utcNow) => new(utcNow);

    public void AdvanceTo(DateTimeOffset utcNow) => UtcNow = utcNow;

    public void AdvanceBy(TimeSpan duration) => UtcNow += duration;
}
