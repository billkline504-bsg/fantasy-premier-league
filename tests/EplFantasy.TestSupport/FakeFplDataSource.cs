using EplFantasy.PlayerData;

namespace EplFantasy.TestSupport;

/// <summary>
/// A controllable <see cref="IFplDataSource"/> for testing IPlayerDataSyncService's idempotent
/// upsert mechanics (AP-008) without depending on the real, unofficial FPL API (BR-289) — every
/// list here is mutable so a test can change what the "external source" returns between two sync
/// calls and assert the resulting upsert behavior.
/// </summary>
public sealed class FakeFplDataSource : IFplDataSource
{
    public List<ClubSyncData> Clubs { get; } = [];
    public List<PlayerSyncData> Players { get; } = [];
    public List<GameweekSyncData> Gameweeks { get; } = [];
    public List<FixtureSyncData> Fixtures { get; } = [];

    /// <summary>Keyed by gameweekNumber — a test adds an entry per Gameweek it wants GetPlayerPerformancesAsync to answer for.</summary>
    public Dictionary<int, List<PlayerPerformanceSyncData>> PlayerPerformancesByGameweekNumber { get; } = [];

    public Task<IReadOnlyList<ClubSyncData>> GetClubsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ClubSyncData>>(Clubs);

    public Task<IReadOnlyList<PlayerSyncData>> GetPlayersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlayerSyncData>>(Players);

    public Task<IReadOnlyList<GameweekSyncData>> GetGameweeksAsync(string eplSeasonIdentifier, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GameweekSyncData>>(Gameweeks.Where(g => g.EplSeasonIdentifier == eplSeasonIdentifier).ToList());

    public Task<IReadOnlyList<FixtureSyncData>> GetFixturesAsync(string eplSeasonIdentifier, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FixtureSyncData>>(Fixtures.Where(f => f.EplSeasonIdentifier == eplSeasonIdentifier).ToList());

    public Task<IReadOnlyList<PlayerPerformanceSyncData>> GetPlayerPerformancesAsync(int gameweekNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlayerPerformanceSyncData>>(
            PlayerPerformancesByGameweekNumber.TryGetValue(gameweekNumber, out var performances) ? performances : []);
}
