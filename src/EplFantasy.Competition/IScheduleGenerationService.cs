namespace EplFantasy.Competition;

/// <summary>
/// IT-38 (F-009.1, BR-107-BR-110): generates a Season's entire H2H schedule — one round per
/// already-synced Gameweek, covering every currently-Active FantasyTeam — via
/// <see cref="RoundRobinScheduler"/>. BR-110: idempotent once a schedule already exists for this
/// Season ("persisted... does not change thereafter except via an explicit administrative
/// action") — calling this again is a safe no-op, not a regeneration. Not wired to any automatic
/// trigger yet (e.g., Initial Draft completion) — no BR or Architecture section names one, the
/// same kind of deliberately-left gap IT-17 left for its own sync-on-a-schedule trigger.
/// </summary>
public interface IScheduleGenerationService
{
    Task GenerateAsync(Guid seasonId, CancellationToken cancellationToken = default);
}
