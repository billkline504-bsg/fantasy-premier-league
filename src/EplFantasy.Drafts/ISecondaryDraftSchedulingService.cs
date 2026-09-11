namespace EplFantasy.Drafts;

/// <summary>
/// IT-45 (F-006.1, BR-069/BR-281): the DB-touching half of <see cref="SecondaryDraftScheduler"/> —
/// resolves a Season's own configured <c>SecondaryDraftSchedulingOffsetDays</c> and its real fixture
/// calendar (every EPL fixture date under that Season's own <c>EplSeasonIdentifier</c>), then
/// delegates the actual proposal math to the pure calculator. Returns null when
/// <paramref name="transferWindowCloseDate"/> itself is null (AC4 — the close date isn't confirmed
/// yet, so no date is proposed).
/// </summary>
public interface ISecondaryDraftSchedulingService
{
    Task<DateOnly?> ProposeStartDateAsync(Guid seasonId, DateOnly? transferWindowCloseDate, CancellationToken cancellationToken = default);
}
