using EplFantasy.Infrastructure;
using EplFantasy.PlayerData;
using EplFantasy.Scoring;
using EplFantasy.SharedKernel;
using EplFantasy.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// IT-20 (F-004.3): proves PlayerPerformanceSyncService's upsert-by-(Gameweek,Player) (AP-008) and,
/// distinctly from IT-17/IT-18's own sync services, BR-234's reconciliation rule — an unchanged
/// re-sync leaves the stored row (and its RetrievedAt) untouched, while a genuine delta updates it
/// and (per ScoreRecalculated.cs's own remarks) would raise a ScoreRecalculated log entry. Uses
/// FakeFplDataSource in place of the real FPL API client, the same pattern PlayerDataSyncServiceTests
/// already established.
/// </summary>
public class PlayerPerformanceSyncServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
    private readonly FakeFplDataSource _fakeSource = new();
    private readonly FakeClock _clock = FakeClock.StartingAt(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        var services = new ServiceCollection();
        services.AddInfrastructure(_container.GetConnectionString());
        services.AddSingleton<IFplDataSource>(_fakeSource);
        services.AddSingleton<IClock>(_clock);
        _provider = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static async Task ApplyHandWrittenMigrationsAsync(string connectionString)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EplFantasy.sln")))
        {
            dir = dir.Parent;
        }

        var migrationsDir = Path.Combine(dir!.FullName, "docs", "aidlc", "06-database-migrations", "migrations");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var file in Directory.GetFiles(migrationsDir, "V*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(file), connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Seeds one Club, one Player, and one Gameweek (via the real sync services, not a raw insert) so SyncGameweekAsync has something to resolve against.</summary>
    private async Task<(string EplSeasonIdentifier, int GameweekNumber, string EplPlayerId)> SeedPlayerAndGameweekAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var eplSeasonId = $"season-{suffix}";
        var eplClubId = $"club-{suffix}";
        var eplPlayerId = $"player-{suffix}";

        _fakeSource.Clubs.Add(new ClubSyncData(eplClubId, "Test FC", "TFC"));
        _fakeSource.Players.Add(new PlayerSyncData(eplPlayerId, "Test Player", PlayerPosition.Mid, eplClubId));
        _fakeSource.Gameweeks.Add(new GameweekSyncData(eplSeasonId, 1, _clock.UtcNow.AddDays(-1)));

        await using var scope = _provider.CreateAsyncScope();
        var playerDataSync = scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>();
        await playerDataSync.SyncClubsAsync();
        await playerDataSync.SyncPlayersAsync();
        await playerDataSync.SyncGameweeksAndFixturesAsync(eplSeasonId);

        return (eplSeasonId, 1, eplPlayerId);
    }

    private async Task<PlayerPerformance> GetPerformanceAsync(string eplSeasonIdentifier, int gameweekNumber, string eplPlayerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var gameweekId = (await db.Gameweeks.SingleAsync(g => g.EplSeasonIdentifier == eplSeasonIdentifier && g.Number == gameweekNumber)).GameweekId;
        var playerId = (await db.Players.SingleAsync(p => p.EplPlayerId == eplPlayerId)).PlayerId;
        return await db.PlayerPerformances.SingleAsync(pp => pp.GameweekId == gameweekId && pp.PlayerId == playerId);
    }

    [Fact]
    public async Task SyncGameweekAsync_creates_a_PlayerPerformance_row_with_FPLs_official_stats()
    {
        var (eplSeasonId, gameweekNumber, eplPlayerId) = await SeedPlayerAndGameweekAsync();
        _fakeSource.PlayerPerformancesByGameweekNumber[gameweekNumber] =
            [new PlayerPerformanceSyncData(eplPlayerId, MinutesPlayed: 90, FantasyPoints: 6, Goals: 1, GoalsConceded: 1, OwnGoals: 0)];

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerPerformanceSyncService>().SyncGameweekAsync(eplSeasonId, gameweekNumber);
        }

        var performance = await GetPerformanceAsync(eplSeasonId, gameweekNumber, eplPlayerId);
        Assert.Equal(90, performance.MinutesPlayed);
        Assert.Equal(6, performance.FantasyPoints);
        Assert.Equal(1, performance.Goals);
        Assert.Equal(1, performance.GoalsConceded);
        Assert.Equal(0, performance.OwnGoals);
        Assert.Equal(PerformanceSource.OfficialFpl, performance.Source);
        Assert.True(performance.IsOfficial);
        Assert.Equal(_clock.UtcNow, performance.RetrievedAt);
    }

    [Fact]
    public async Task SyncGameweekAsync_re_syncing_identical_data_leaves_the_row_and_RetrievedAt_untouched()
    {
        var (eplSeasonId, gameweekNumber, eplPlayerId) = await SeedPlayerAndGameweekAsync();
        _fakeSource.PlayerPerformancesByGameweekNumber[gameweekNumber] =
            [new PlayerPerformanceSyncData(eplPlayerId, MinutesPlayed: 90, FantasyPoints: 6, Goals: 0, GoalsConceded: 1, OwnGoals: 0)];
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerPerformanceSyncService>().SyncGameweekAsync(eplSeasonId, gameweekNumber);
        }
        var firstRetrievedAt = _clock.UtcNow;

        _clock.AdvanceBy(TimeSpan.FromHours(1));
        // Same FakeFplDataSource entry as before — a plain re-sync, no upstream change at all.
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerPerformanceSyncService>().SyncGameweekAsync(eplSeasonId, gameweekNumber);
        }

        var performance = await GetPerformanceAsync(eplSeasonId, gameweekNumber, eplPlayerId);
        Assert.Equal(firstRetrievedAt, performance.RetrievedAt); // BR-234: untouched, not silently bumped.
        Assert.Equal(6, performance.FantasyPoints);
    }

    [Fact]
    public async Task SyncGameweekAsync_a_corrected_value_updates_the_row_and_bumps_RetrievedAt()
    {
        var (eplSeasonId, gameweekNumber, eplPlayerId) = await SeedPlayerAndGameweekAsync();
        _fakeSource.PlayerPerformancesByGameweekNumber[gameweekNumber] =
            [new PlayerPerformanceSyncData(eplPlayerId, MinutesPlayed: 90, FantasyPoints: 6, Goals: 0, GoalsConceded: 1, OwnGoals: 0)];
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerPerformanceSyncService>().SyncGameweekAsync(eplSeasonId, gameweekNumber);
        }

        _clock.AdvanceBy(TimeSpan.FromHours(1));
        var correctedAt = _clock.UtcNow;
        // BR-234: an official correction — a bonus-points recalculation raised FantasyPoints from 6 to 8.
        _fakeSource.PlayerPerformancesByGameweekNumber[gameweekNumber] =
            [new PlayerPerformanceSyncData(eplPlayerId, MinutesPlayed: 90, FantasyPoints: 8, Goals: 0, GoalsConceded: 1, OwnGoals: 0)];
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerPerformanceSyncService>().SyncGameweekAsync(eplSeasonId, gameweekNumber);
        }

        var performance = await GetPerformanceAsync(eplSeasonId, gameweekNumber, eplPlayerId);
        Assert.Equal(8, performance.FantasyPoints);
        Assert.Equal(correctedAt, performance.RetrievedAt); // reconciled, not left at the original ingestion time.
    }

    [Fact]
    public async Task SyncGameweekAsync_for_a_gameweek_that_does_not_exist_yet_throws()
    {
        await using var scope = _provider.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IPlayerPerformanceSyncService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => syncService.SyncGameweekAsync($"season-{Guid.NewGuid():N}", 1));
    }

    [Fact]
    public async Task SyncGameweekAsync_referencing_an_unknown_player_throws_and_persists_nothing()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var eplSeasonId = $"season-{suffix}";
        _fakeSource.Gameweeks.Add(new GameweekSyncData(eplSeasonId, 1, _clock.UtcNow.AddDays(-1)));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncGameweeksAndFixturesAsync(eplSeasonId);
        }

        // No Player was ever synced for this EplPlayerId.
        _fakeSource.PlayerPerformancesByGameweekNumber[1] =
            [new PlayerPerformanceSyncData($"unknown-{suffix}", MinutesPlayed: 90, FantasyPoints: 6, Goals: 0, GoalsConceded: 0, OwnGoals: 0)];

        await using var scope2 = _provider.CreateAsyncScope();
        var syncService = scope2.ServiceProvider.GetRequiredService<IPlayerPerformanceSyncService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => syncService.SyncGameweekAsync(eplSeasonId, 1));

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.False(await db.PlayerPerformances.AnyAsync());
    }
}
