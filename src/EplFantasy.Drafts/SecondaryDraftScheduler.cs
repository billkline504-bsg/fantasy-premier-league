namespace EplFantasy.Drafts;

/// <summary>
/// IT-45 (F-006.1, BR-069/BR-281): pure proposal calculation — no <see cref="Draft"/> aggregate
/// exists yet at this point (this only ever proposes a start date; an Administrator may still
/// override it before a Secondary Draft is actually created, per AC3, a later task's own concern).
/// The official EPL transfer window's confirmed close date has no modeled entity anywhere in this
/// codebase (BRD/Architecture both treat it as external data with no formally specified source) —
/// deliberately taken as a plain, caller-supplied nullable <see cref="DateOnly"/> rather than
/// invented as a new persisted concept; a null value means "not yet confirmed" (AC4), so this
/// returns null rather than proposing a premature date. No DB access here by design (mirrors <c>RoundRobinScheduler</c>'s
/// own "pure calculation, unit-testable without Postgres" precedent, IT-38) — see
/// <c>ISecondaryDraftSchedulingService</c> (EplFantasy.Infrastructure) for the DB-touching caller
/// that assembles <paramref name="datesWithScheduledFixtures"/> from the real fixture calendar.
/// </summary>
public static class SecondaryDraftScheduler
{
    /// <summary>
    /// AC1: starts at <paramref name="transferWindowCloseDate"/> + <paramref name="schedulingOffsetDays"/>
    /// (<c>SeasonConfiguration.SecondaryDraftSchedulingOffsetDays</c>, default 1). AC2: advances one
    /// day at a time — not a bulk skip to the next known-clear date — for as long as the current
    /// candidate date appears in <paramref name="datesWithScheduledFixtures"/>, exactly mirroring
    /// BR-281's own literal "advance one day at a time" wording (also correctly handles a run of
    /// several consecutive fixture-heavy days, not just a single one).
    /// </summary>
    public static DateOnly? ProposeStartDate(
        DateOnly? transferWindowCloseDate,
        int schedulingOffsetDays,
        IReadOnlySet<DateOnly> datesWithScheduledFixtures)
    {
        if (transferWindowCloseDate is not { } closeDate)
        {
            return null;
        }

        var proposedDate = closeDate.AddDays(schedulingOffsetDays);
        while (datesWithScheduledFixtures.Contains(proposedDate))
        {
            proposedDate = proposedDate.AddDays(1);
        }

        return proposedDate;
    }
}
