namespace EplFantasy.PlayerData;

/// <summary>
/// ADR-009: "All official FPL/EPL data enters through a dedicated PlayerDataIntegration module
/// that translates external API/CSV shapes into internal domain models. No other module
/// references external DTOs." This interface *is* that boundary — everything on the other side of
/// it (<see cref="IPlayerDataSyncService"/>, and everything downstream of that) only ever sees the
/// already-translated sync records in FplSyncData.cs, never a raw external shape.
///
/// No concrete implementation exists yet as of this foundational task (IT-F11): building one
/// against the real, unofficial FPL API (BR-289 — undocumented, no published schema or rate-limit
/// guarantee) is F-004.1/F-004.2's job (IT-17/IT-18). What this task builds is the boundary itself
/// and the idempotent upsert mechanism (<see cref="IPlayerDataSyncService"/>) every concrete
/// implementation plugs into identically.
/// </summary>
public interface IFplDataSource
{
    Task<IReadOnlyList<ClubSyncData>> GetClubsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlayerSyncData>> GetPlayersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameweekSyncData>> GetGameweeksAsync(string eplSeasonIdentifier, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FixtureSyncData>> GetFixturesAsync(string eplSeasonIdentifier, CancellationToken cancellationToken = default);

    /// <summary>IT-20 (F-004.3): every Player's raw official statistics for one Gameweek, keyed by <see cref="PlayerPerformanceSyncData.EplPlayerId"/> — consumed by IPlayerPerformanceSyncService (EplFantasy.Scoring), not IPlayerDataSyncService (PlayerPerformance is a Scoring-context aggregate, not this module's).</summary>
    Task<IReadOnlyList<PlayerPerformanceSyncData>> GetPlayerPerformancesAsync(int gameweekNumber, CancellationToken cancellationToken = default);
}
