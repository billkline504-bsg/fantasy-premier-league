using EplFantasy.Administration;
using EplFantasy.Competition;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using EplFantasy.Rosters;
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
/// Proves IT-37's ScoreOverrideService (F-008.5, BR-139-BR-145) against real Postgres:
/// createScoreOverride snapshots the original value before applying the override, records an
/// AdministrativeAction (ActionType = ScoreOverride), and re-triggers recalculation of an
/// already-Scored GameweekScore this affects; undoScoreOverride sets UndoneAt/records
/// ActionType = ScoreOverrideUndo/re-triggers recalculation, and rejects an already-undone
/// override (BR-142/BR-143); and a non-Administrator caller is rejected the same
/// "the service itself has nothing to grant" way DraftService/RosterService's own equivalents
/// already are.
/// </summary>
public class ScoreOverrideServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
    private readonly FakeClock _clock = FakeClock.StartingAt(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(_container.GetConnectionString());
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

    /// <summary>Builds a real League (returning its founding Administrator's own UserId)/Season/FantasyTeam with WeeklyRosterSize shrunk to 2/(0,0,0,2), a Gameweek, and two owned Forwards, one of which already has a PlayerPerformance row. Returns the PlayerPerformanceId to override.</summary>
    private async Task<(Guid AdminUserId, Guid LeagueId, Guid FantasyTeamId, Guid GameweekId, Guid PlayerPerformanceId, List<Guid> OwnedPlayerIds)> SeedScoringPrerequisitesAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var owner = new User { UserId = Guid.NewGuid(), Username = $"owner{suffix}", Email = $"owner{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(owner.UserId, $"Override League {suffix}", null);

        var eplSeasonIdentifier = $"override-season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;

        var fantasyTeamResult = await scope.ServiceProvider.GetRequiredService<IFantasyTeamService>()
            .CreateAsync(league.CreatedByMembershipId, season.SeasonId);
        var fantasyTeamId = fantasyTeamResult.Value.FantasyTeamId;

        var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == season.SeasonId);
        seasonConfiguration.WeeklyRosterSize = 2;
        seasonConfiguration.PositionalMinimumGk = 0;
        seasonConfiguration.PositionalMinimumDef = 0;
        seasonConfiguration.PositionalMinimumMid = 0;
        seasonConfiguration.PositionalMinimumFwd = 2;
        await db.SaveChangesAsync();

        var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = 1, RosterLockDeadline = _clock.UtcNow.AddDays(7) };
        db.Gameweeks.Add(gameweek);

        var ownedPlayerIds = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var playerId = Guid.NewGuid();
            db.Players.Add(new Player { PlayerId = playerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = $"Forward {i}", Position = PlayerPosition.Fwd });
            db.SquadPlayers.Add(new SquadPlayer
            {
                SquadPlayerId = Guid.NewGuid(),
                FantasyTeamId = fantasyTeamId,
                PlayerId = playerId,
                SeasonId = season.SeasonId,
                AcquisitionType = AcquisitionType.InitialDraft,
                AcquiredAt = _clock.UtcNow,
                IsCurrentlyOwned = true,
            });
            ownedPlayerIds.Add(playerId);
        }

        await db.SaveChangesAsync();

        var performance = new PlayerPerformance
        {
            PlayerPerformanceId = Guid.NewGuid(),
            GameweekId = gameweek.GameweekId,
            PlayerId = ownedPlayerIds[0],
            MinutesPlayed = 90,
            FantasyPoints = 6,
            Goals = 1,
            Source = PerformanceSource.OfficialFpl,
            IsOfficial = true,
            RetrievedAt = _clock.UtcNow,
        };
        db.PlayerPerformances.Add(performance);
        await db.SaveChangesAsync();

        return (owner.UserId, league.LeagueId, fantasyTeamId, gameweek.GameweekId, performance.PlayerPerformanceId, ownedPlayerIds);
    }

    private async Task SeedSeasonGoalPredictionAndLockRosterAsync(Guid fantasyTeamId, Guid gameweekId, List<Guid> playerIds)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var seasonId = (await db.FantasyTeams.SingleAsync(t => t.FantasyTeamId == fantasyTeamId)).SeasonId;
        db.SeasonGoalPredictions.Add(new SeasonGoalPrediction
        {
            PredictionId = Guid.NewGuid(),
            SeasonId = seasonId,
            FantasyTeamId = fantasyTeamId,
            PredictedEplGoals = 1000,
            SubmittedAt = _clock.UtcNow,
            LockedAt = _clock.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();

        var rosterService = scope.ServiceProvider.GetRequiredService<IRosterService>();
        var roster = await rosterService.SubmitAsync(fantasyTeamId, gameweekId, playerIds, playerIds[0], ifMatchXmin: null);
        var tracked = await db.GameweekRosters.SingleAsync(r => r.GameweekRosterId == roster.GameweekRosterId);
        tracked.Lock(_clock.UtcNow);
        await db.SaveChangesAsync();
    }

    private async Task<GameweekScore> GetScoreAsync(Guid fantasyTeamId, Guid gameweekId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.GameweekScores.SingleAsync(s => s.FantasyTeamId == fantasyTeamId && s.GameweekId == gameweekId);
    }

    [Fact]
    public async Task CreateAsync_creates_an_active_override_and_records_an_AdministrativeAction()
    {
        var (adminUserId, leagueId, _, _, playerPerformanceId, _) = await SeedScoringPrerequisitesAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IScoreOverrideService>();
        var scoreOverride = await service.CreateAsync(
            playerPerformanceId, leagueId, new Dictionary<string, int> { ["goals"] = 2 }, "video review", adminUserId);

        Assert.True(scoreOverride.IsActive);
        Assert.Equal(playerPerformanceId, scoreOverride.PlayerPerformanceId);
        Assert.Equal("video review", scoreOverride.Reason);

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var action = await db.AdministrativeActions.SingleAsync(a => a.TargetEntityId == playerPerformanceId);
        Assert.Equal(AdminActionType.ScoreOverride, action.ActionType);
        Assert.Equal(leagueId, action.LeagueId);
        Assert.Equal("PlayerPerformance", action.TargetEntityType);
    }

    [Fact]
    public async Task CreateAsync_snapshots_the_original_value_before_applying_the_override()
    {
        var (adminUserId, leagueId, _, _, playerPerformanceId, _) = await SeedScoringPrerequisitesAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IScoreOverrideService>();
        var scoreOverride = await service.CreateAsync(
            playerPerformanceId, leagueId, new Dictionary<string, int> { ["goals"] = 2 }, null, adminUserId);

        Assert.Contains("\"goals\":1", scoreOverride.OriginalValueJson); // the official value (Goals = 1) at creation time.
        Assert.Contains("\"goals\":2", scoreOverride.OverrideValueJson);
    }

    [Fact]
    public async Task CreateAsync_recalculates_an_already_Scored_GameweekScore()
    {
        var (adminUserId, leagueId, fantasyTeamId, gameweekId, playerPerformanceId, ownedPlayerIds) = await SeedScoringPrerequisitesAsync();
        await SeedSeasonGoalPredictionAndLockRosterAsync(fantasyTeamId, gameweekId, ownedPlayerIds);
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IGameweekScoreCalculationService>().CalculateForGameweekAsync(gameweekId);
        }
        var beforeOverride = await GetScoreAsync(fantasyTeamId, gameweekId);
        Assert.Equal(0, beforeOverride.RecalculatedCount);

        await using var scope2 = _provider.CreateAsyncScope();
        var service = scope2.ServiceProvider.GetRequiredService<IScoreOverrideService>();
        // ownedPlayerIds[0] is the Captain (6 raw points, x2 = 12) — overriding its FantasyPoints to 20 should push the total up accordingly.
        await service.CreateAsync(playerPerformanceId, leagueId, new Dictionary<string, int> { ["fantasyPoints"] = 20 }, null, adminUserId);

        var afterOverride = await GetScoreAsync(fantasyTeamId, gameweekId);
        Assert.Equal(1, afterOverride.RecalculatedCount);
        Assert.Equal(40, afterOverride.CaptainPoints); // 20 x 2 (BR-047), not the original 6 x 2 = 12.
        Assert.NotEqual(beforeOverride.FantasyPoints, afterOverride.FantasyPoints);
    }

    [Fact]
    public async Task CreateAsync_rejects_a_non_Administrator()
    {
        var (_, leagueId, _, _, playerPerformanceId, _) = await SeedScoringPrerequisitesAsync();

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IScoreOverrideService>();

        // The controller-layer check (League Administrator of leagueId) already prevents a
        // non-Administrator from reaching this service call in the first place — the same
        // "the service itself has nothing to grant" precedent DraftServiceTests/RosterServiceTests
        // already established for their own equivalents.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(playerPerformanceId, leagueId, new Dictionary<string, int> { ["goals"] = 2 }, null, Guid.NewGuid()));
    }

    [Fact]
    public async Task UndoAsync_sets_UndoneAt_and_re_triggers_recalculation()
    {
        var (adminUserId, leagueId, fantasyTeamId, gameweekId, playerPerformanceId, ownedPlayerIds) = await SeedScoringPrerequisitesAsync();
        await SeedSeasonGoalPredictionAndLockRosterAsync(fantasyTeamId, gameweekId, ownedPlayerIds);
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IGameweekScoreCalculationService>().CalculateForGameweekAsync(gameweekId);
        }

        Guid scoreOverrideId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IScoreOverrideService>();
            var scoreOverride = await service.CreateAsync(
                playerPerformanceId, leagueId, new Dictionary<string, int> { ["fantasyPoints"] = 20 }, null, adminUserId);
            scoreOverrideId = scoreOverride.ScoreOverrideId;
        }
        var overriddenScore = await GetScoreAsync(fantasyTeamId, gameweekId);

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IScoreOverrideService>();
        var undone = await service2.UndoAsync(scoreOverrideId, adminUserId);

        Assert.NotNull(undone.UndoneAt);
        Assert.False(undone.IsActive);

        var afterUndo = await GetScoreAsync(fantasyTeamId, gameweekId);
        Assert.Equal(2, afterUndo.RecalculatedCount); // once for the override, once for the undo.
        Assert.Equal(12, afterUndo.CaptainPoints); // back to the official 6 x 2 (BR-143), not the overridden 20 x 2.
        Assert.NotEqual(overriddenScore.CaptainPoints, afterUndo.CaptainPoints);

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var action = await db.AdministrativeActions.SingleAsync(a => a.ActionType == AdminActionType.ScoreOverrideUndo);
        Assert.Equal(leagueId, action.LeagueId);
    }

    [Fact]
    public async Task UndoAsync_rejects_an_already_undone_override()
    {
        var (adminUserId, leagueId, _, _, playerPerformanceId, _) = await SeedScoringPrerequisitesAsync();
        Guid scoreOverrideId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IScoreOverrideService>();
            var scoreOverride = await service.CreateAsync(
                playerPerformanceId, leagueId, new Dictionary<string, int> { ["goals"] = 2 }, null, adminUserId);
            scoreOverrideId = scoreOverride.ScoreOverrideId;
            await service.UndoAsync(scoreOverrideId, adminUserId);
        }

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IScoreOverrideService>();

        await Assert.ThrowsAsync<ScoreOverrideAlreadyUndoneException>(() => service2.UndoAsync(scoreOverrideId, adminUserId));
    }
}
