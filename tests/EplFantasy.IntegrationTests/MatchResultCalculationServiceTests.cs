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
/// Proves IT-39/IT-40's match-result cascade (F-009.2 AC1-AC4/F-009.3, BR-111-BR-117) against real
/// Postgres: once both sides of a scheduled <see cref="HeadToHeadMatch"/> have been scored by
/// <see cref="IGameweekScoreCalculationService"/>, the match's Result/HomeScore/AwayScore and its
/// League Points (the Season's own configured Win/Draw/Loss values, BR-291) are derived
/// automatically — no manual trigger (AC4) — with no result at all while only one side has been
/// scored, and a later ScoreOverride-driven recalculation (IT-37's own cascade) correctly
/// re-derives (and can flip) an already-decided result and its League Points — the task
/// breakdown's own required case. A FantasyTeam with no scheduled fixture this Gameweek (a BR-306
/// bye) is a safe no-op.
/// </summary>
public class MatchResultCalculationServiceTests : IAsyncLifetime
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

    private async Task<Guid> SeedUserAsync(EplFantasyDbContext db, string label)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { UserId = Guid.NewGuid(), Username = $"{label}{suffix}", Email = $"{label}{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    /// <summary>
    /// Builds a real League/Season with two Active FantasyTeams (each with WeeklyRosterSize shrunk
    /// to 1/(0,0,0,1) — a single owned Forward is a valid roster), one Gameweek, and a scheduled
    /// HeadToHeadMatch between them for that Gameweek. Returns each side's own FantasyTeamId/
    /// PlayerId and the Administrator's own UserId/LeagueId (needed to create a ScoreOverride).
    /// </summary>
    private async Task<(Guid AdminUserId, Guid LeagueId, Guid GameweekId, Guid MatchId, Guid HomeFantasyTeamId, Guid HomePlayerId, Guid AwayFantasyTeamId, Guid AwayPlayerId)> SeedScheduledMatchAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var owner = await SeedUserAsync(db, "owner");
        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(owner, $"Match League {suffix}", null);

        var eplSeasonIdentifier = $"match-season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;

        var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == season.SeasonId);
        seasonConfiguration.WeeklyRosterSize = 1;
        seasonConfiguration.PositionalMinimumGk = 0;
        seasonConfiguration.PositionalMinimumDef = 0;
        seasonConfiguration.PositionalMinimumMid = 0;
        seasonConfiguration.PositionalMinimumFwd = 1;
        await db.SaveChangesAsync();

        var fantasyTeamService = scope.ServiceProvider.GetRequiredService<IFantasyTeamService>();
        var homeFantasyTeamId = (await fantasyTeamService.CreateAsync(league.CreatedByMembershipId, season.SeasonId)).Value.FantasyTeamId;

        var memberUserId = await SeedUserAsync(db, "member");
        var membership = LeagueMembership.Join(Guid.NewGuid(), league.LeagueId, memberUserId, _clock.UtcNow);
        db.LeagueMemberships.Add(membership);
        await db.SaveChangesAsync();
        var awayFantasyTeamId = (await fantasyTeamService.CreateAsync(membership.LeagueMembershipId, season.SeasonId)).Value.FantasyTeamId;

        var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = 1, RosterLockDeadline = _clock.UtcNow.AddDays(7) };
        db.Gameweeks.Add(gameweek);

        var homePlayerId = Guid.NewGuid();
        var awayPlayerId = Guid.NewGuid();
        db.Players.Add(new Player { PlayerId = homePlayerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = "Home Forward", Position = PlayerPosition.Fwd });
        db.Players.Add(new Player { PlayerId = awayPlayerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = "Away Forward", Position = PlayerPosition.Fwd });
        db.SquadPlayers.Add(new SquadPlayer { SquadPlayerId = Guid.NewGuid(), FantasyTeamId = homeFantasyTeamId, PlayerId = homePlayerId, SeasonId = season.SeasonId, AcquisitionType = AcquisitionType.InitialDraft, AcquiredAt = _clock.UtcNow, IsCurrentlyOwned = true });
        db.SquadPlayers.Add(new SquadPlayer { SquadPlayerId = Guid.NewGuid(), FantasyTeamId = awayFantasyTeamId, PlayerId = awayPlayerId, SeasonId = season.SeasonId, AcquisitionType = AcquisitionType.InitialDraft, AcquiredAt = _clock.UtcNow, IsCurrentlyOwned = true });

        var matchId = Guid.NewGuid();
        db.HeadToHeadMatches.Add(HeadToHeadMatch.Schedule(matchId, season.SeasonId, gameweek.GameweekId, homeFantasyTeamId, awayFantasyTeamId));

        db.SeasonGoalPredictions.Add(new SeasonGoalPrediction { PredictionId = Guid.NewGuid(), SeasonId = season.SeasonId, FantasyTeamId = homeFantasyTeamId, PredictedEplGoals = 1000, SubmittedAt = _clock.UtcNow, LockedAt = _clock.UtcNow.AddDays(1) });
        db.SeasonGoalPredictions.Add(new SeasonGoalPrediction { PredictionId = Guid.NewGuid(), SeasonId = season.SeasonId, FantasyTeamId = awayFantasyTeamId, PredictedEplGoals = 1000, SubmittedAt = _clock.UtcNow, LockedAt = _clock.UtcNow.AddDays(1) });

        await db.SaveChangesAsync();

        return (owner, league.LeagueId, gameweek.GameweekId, matchId, homeFantasyTeamId, homePlayerId, awayFantasyTeamId, awayPlayerId);
    }

    private async Task<Guid> SeedPlayerPerformanceAsync(Guid gameweekId, Guid playerId, int fantasyPoints)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var performance = new PlayerPerformance
        {
            PlayerPerformanceId = Guid.NewGuid(),
            GameweekId = gameweekId,
            PlayerId = playerId,
            MinutesPlayed = 90,
            FantasyPoints = fantasyPoints,
            Source = PerformanceSource.OfficialFpl,
            IsOfficial = true,
            RetrievedAt = _clock.UtcNow,
        };
        db.PlayerPerformances.Add(performance);
        await db.SaveChangesAsync();
        return performance.PlayerPerformanceId;
    }

    private async Task LockRosterAsync(Guid fantasyTeamId, Guid gameweekId, Guid playerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var rosterService = scope.ServiceProvider.GetRequiredService<IRosterService>();
        var roster = await rosterService.SubmitAsync(fantasyTeamId, gameweekId, [playerId], playerId, ifMatchXmin: null);

        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var tracked = await db.GameweekRosters.SingleAsync(r => r.GameweekRosterId == roster.GameweekRosterId);
        tracked.Lock(_clock.UtcNow);
        await db.SaveChangesAsync();
    }

    private async Task CalculateAsync(Guid gameweekId)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IGameweekScoreCalculationService>().CalculateForGameweekAsync(gameweekId);
    }

    private async Task<HeadToHeadMatch> GetMatchAsync(Guid matchId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.HeadToHeadMatches.SingleAsync(m => m.MatchId == matchId);
    }

    [Fact]
    public async Task CalculateForGameweekAsync_leaves_the_match_undecided_while_only_one_side_has_been_scored()
    {
        var (_, _, gameweekId, matchId, homeFantasyTeamId, homePlayerId, awayFantasyTeamId, awayPlayerId) = await SeedScheduledMatchAsync();
        await LockRosterAsync(homeFantasyTeamId, gameweekId, homePlayerId);
        await SeedPlayerPerformanceAsync(gameweekId, homePlayerId, fantasyPoints: 10);
        // The away side is never locked/scored — AC4 requires BOTH sides Locked and Scored.

        await CalculateAsync(gameweekId);

        var match = await GetMatchAsync(matchId);
        Assert.Null(match.Result);
        Assert.Null(match.HomeScore);
        Assert.Null(match.AwayScore);
    }

    [Fact]
    public async Task CalculateForGameweekAsync_derives_the_HomeWin_result_automatically_once_both_sides_are_scored()
    {
        var (_, _, gameweekId, matchId, homeFantasyTeamId, homePlayerId, awayFantasyTeamId, awayPlayerId) = await SeedScheduledMatchAsync();
        await LockRosterAsync(homeFantasyTeamId, gameweekId, homePlayerId);
        await LockRosterAsync(awayFantasyTeamId, gameweekId, awayPlayerId);
        await SeedPlayerPerformanceAsync(gameweekId, homePlayerId, fantasyPoints: 10);
        await SeedPlayerPerformanceAsync(gameweekId, awayPlayerId, fantasyPoints: 4);

        await CalculateAsync(gameweekId);

        var match = await GetMatchAsync(matchId);
        // Each side's own player is also the Captain (BR-047 doubles it), but the doubling applies
        // equally to both sides here, so the ordering (Home > Away) is unaffected.
        Assert.Equal(MatchResult.HomeWin, match.Result);
        Assert.Equal(20, match.HomeScore);
        Assert.Equal(8, match.AwayScore);
        // IT-40 (BR-115/BR-117): the League's own configured (default, BR-291) 3/1/0 values.
        Assert.Equal(3, match.LeaguePointsHome);
        Assert.Equal(0, match.LeaguePointsAway);
    }

    [Fact]
    public async Task CalculateForGameweekAsync_does_nothing_for_a_FantasyTeam_with_no_scheduled_fixture_this_Gameweek()
    {
        // BR-306: a bye is the absence of a HeadToHeadMatch row — the cascade must not fabricate one.
        var (_, _, gameweekId, matchId, homeFantasyTeamId, homePlayerId, awayFantasyTeamId, awayPlayerId) = await SeedScheduledMatchAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.HeadToHeadMatches.Remove(await db.HeadToHeadMatches.SingleAsync(m => m.MatchId == matchId));
            await db.SaveChangesAsync();
        }

        await LockRosterAsync(homeFantasyTeamId, gameweekId, homePlayerId);
        await SeedPlayerPerformanceAsync(gameweekId, homePlayerId, fantasyPoints: 10);

        await CalculateAsync(gameweekId); // must not throw despite no scheduled match existing.

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.False(await verifyDb.HeadToHeadMatches.AnyAsync(m => m.MatchId == matchId));
    }

    [Fact]
    public async Task A_ScoreOverride_recalculation_correctly_re_derives_and_can_flip_the_match_result()
    {
        // The task breakdown's own required test: "a score recalculation correctly re-derives the
        // match result." Home starts ahead (10 v 4, HomeWin); overriding the Away player's
        // FantasyPoints up to 30 must flip the match to an AwayWin, not merely update the scores.
        var (adminUserId, leagueId, gameweekId, matchId, homeFantasyTeamId, homePlayerId, awayFantasyTeamId, awayPlayerId) = await SeedScheduledMatchAsync();
        await LockRosterAsync(homeFantasyTeamId, gameweekId, homePlayerId);
        await LockRosterAsync(awayFantasyTeamId, gameweekId, awayPlayerId);
        await SeedPlayerPerformanceAsync(gameweekId, homePlayerId, fantasyPoints: 10);
        var awayPerformanceId = await SeedPlayerPerformanceAsync(gameweekId, awayPlayerId, fantasyPoints: 4);
        await CalculateAsync(gameweekId);

        var beforeOverride = await GetMatchAsync(matchId);
        Assert.Equal(MatchResult.HomeWin, beforeOverride.Result);
        Assert.Equal(3, beforeOverride.LeaguePointsHome);
        Assert.Equal(0, beforeOverride.LeaguePointsAway);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var scoreOverrideService = scope.ServiceProvider.GetRequiredService<IScoreOverrideService>();
            await scoreOverrideService.CreateAsync(
                awayPerformanceId, leagueId, new Dictionary<string, int> { ["fantasyPoints"] = 30 }, "video review", adminUserId);
        }

        var afterOverride = await GetMatchAsync(matchId);
        Assert.Equal(MatchResult.AwayWin, afterOverride.Result);
        Assert.Equal(20, afterOverride.HomeScore); // Home's own 10 x 2 (BR-047 Captain doubling) — unchanged.
        Assert.Equal(60, afterOverride.AwayScore); // Away's overridden 30 x 2 (BR-047) — up from the original 8.
        // IT-40: League Points flip along with the result, not just the raw scores.
        Assert.Equal(0, afterOverride.LeaguePointsHome);
        Assert.Equal(3, afterOverride.LeaguePointsAway);
    }
}
