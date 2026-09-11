namespace EplFantasy.PlayerData;

/// <summary>
/// AP-008 "Idempotent Synchronization": re-running any of these against the same external dataset
/// produces the same internal state — achieved by upserting on the external identifier
/// (EplClubId, EplPlayerId, etc.), never blind-inserting (BR-232). BR-233: each call is one
/// transactional batch — a failure partway through leaves prior data untouched, never a partial
/// commit.
/// </summary>
public interface IPlayerDataSyncService
{
    Task SyncClubsAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves each PlayerSyncData's CurrentEplClubId to an internal ClubId — requires clubs to already be synced.</summary>
    Task SyncPlayersAsync(CancellationToken cancellationToken = default);

    /// <summary>Upserts the EplSeason anchor row, its Gameweeks, and their Fixtures together, in that dependency order, in one batch.</summary>
    Task SyncGameweeksAndFixturesAsync(string eplSeasonIdentifier, CancellationToken cancellationToken = default);
}
