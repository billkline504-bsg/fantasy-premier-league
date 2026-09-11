namespace EplFantasy.Scoring;

/// <summary>
/// IT-20 (F-004.3): syncs one Gameweek's official <see cref="PlayerPerformance"/> rows (BR-231),
/// upserting by (GameweekId, PlayerId) — never blind-inserting (AP-008/BR-232) — and reconciling a
/// later-corrected value against what's already stored rather than silently overwriting it
/// (BR-234): an unchanged re-sync leaves the row untouched; a genuine delta updates it and raises a
/// <see cref="ScoreRecalculated"/> cascade (Architecture §7, §10 "Reconciliation").
/// </summary>
public interface IPlayerPerformanceSyncService
{
    Task SyncGameweekAsync(string eplSeasonIdentifier, int gameweekNumber, CancellationToken cancellationToken = default);
}
