using EplFantasy.Competition;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.Notifications;
using EplFantasy.PlayerData;
using EplFantasy.Rosters;
using EplFantasy.Scoring;
using EplFantasy.SharedKernel;
using EplFantasy.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves IT-33/IT-34/IT-36's GameweekScoreCalculationService (F-008.1/F-008.2/F-008.3/F-008.4,
/// BR-073-BR-077/BR-079/BR-080-BR-091/BR-288/BR-047/BR-304) against real Postgres: a Locked
/// roster's players are resolved via IAuthoritativeValueResolver and summed (Captain's own value
/// doubled, BR-047) into a new GameweekScore, and the roster transitions to Scored; a
/// still-Submitted roster is skipped entirely (AC4); a player with no PlayerPerformance row yet
/// contributes zero; an active ScoreOverride takes precedence over official data (Invariant 12)
/// and is itself still doubled when it's the Captain's own; a second calculation pass changes
/// nothing further for an already-scored FantasyTeam (BR-079); and Fantasy Goals For/Against use
/// the BRD's own worked example (BR-088), count a Bench player's goal unlike FantasyPoints
/// (BR-080), and route an own goal to Against rather than For (BR-090/BR-091). The Starting-XI-
/// boundary/tie-break rules themselves (BR-303) are proven at the domain level, GameweekRosterTests
/// — the suites here that don't specifically test Bench exclusion only ever seed 2 players (well
/// under the fixed 11), so both are trivially Starting XI.
/// </summary>
public class GameweekScoreCalculationServiceTests : IAsyncLifetime
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

    /// <summary>Builds a real League/Season/FantasyTeam with WeeklyRosterSize shrunk to <paramref name="playerCount"/>/(0,0,0,playerCount) — that many owned Forwards is a valid roster — and a Gameweek. Returns the owned PlayerIds.</summary>
    private async Task<(Guid FantasyTeamId, Guid SeasonId, Guid GameweekId, List<Guid> OwnedPlayerIds)> SeedFantasyTeamWithSquadAsync(int playerCount = 2)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var owner = new User { UserId = Guid.NewGuid(), Username = $"owner{suffix}", Email = $"owner{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(owner.UserId, $"Scoring League {suffix}", null);

        var eplSeasonIdentifier = $"scoring-season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;

        var fantasyTeamResult = await scope.ServiceProvider.GetRequiredService<IFantasyTeamService>()
            .CreateAsync(league.CreatedByMembershipId, season.SeasonId);
        var fantasyTeamId = fantasyTeamResult.Value.FantasyTeamId;

        var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == season.SeasonId);
        seasonConfiguration.WeeklyRosterSize = playerCount;
        seasonConfiguration.PositionalMinimumGk = 0;
        seasonConfiguration.PositionalMinimumDef = 0;
        seasonConfiguration.PositionalMinimumMid = 0;
        seasonConfiguration.PositionalMinimumFwd = playerCount;
        await db.SaveChangesAsync();

        var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = 1, RosterLockDeadline = _clock.UtcNow.AddDays(7) };
        db.Gameweeks.Add(gameweek);

        var ownedPlayerIds = new List<Guid>();
        for (var i = 0; i < playerCount; i++)
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

        return (fantasyTeamId, season.SeasonId, gameweek.GameweekId, ownedPlayerIds);
    }

    /// <summary>Builds a real League/Season/FantasyTeam with WeeklyRosterSize/PositionalMinimums shrunk to exactly match <paramref name="positions"/> (one player per entry, in order) — and a Gameweek. Returns the owned PlayerIds in the same order as <paramref name="positions"/>.</summary>
    private async Task<(Guid FantasyTeamId, Guid SeasonId, Guid GameweekId, List<Guid> OwnedPlayerIds)> SeedFantasyTeamWithPositionedSquadAsync(IReadOnlyList<PlayerPosition> positions)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var owner = new User { UserId = Guid.NewGuid(), Username = $"owner{suffix}", Email = $"owner{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(owner.UserId, $"Goals League {suffix}", null);

        var eplSeasonIdentifier = $"goals-season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;

        var fantasyTeamResult = await scope.ServiceProvider.GetRequiredService<IFantasyTeamService>()
            .CreateAsync(league.CreatedByMembershipId, season.SeasonId);
        var fantasyTeamId = fantasyTeamResult.Value.FantasyTeamId;

        var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == season.SeasonId);
        seasonConfiguration.WeeklyRosterSize = positions.Count;
        seasonConfiguration.PositionalMinimumGk = positions.Count(p => p == PlayerPosition.Gk);
        seasonConfiguration.PositionalMinimumDef = positions.Count(p => p == PlayerPosition.Def);
        seasonConfiguration.PositionalMinimumMid = positions.Count(p => p == PlayerPosition.Mid);
        seasonConfiguration.PositionalMinimumFwd = positions.Count(p => p == PlayerPosition.Fwd);
        await db.SaveChangesAsync();

        var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = 1, RosterLockDeadline = _clock.UtcNow.AddDays(7) };
        db.Gameweeks.Add(gameweek);

        var ownedPlayerIds = new List<Guid>();
        foreach (var position in positions)
        {
            var playerId = Guid.NewGuid();
            db.Players.Add(new Player { PlayerId = playerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = $"{position} Player", Position = position });
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

        return (fantasyTeamId, season.SeasonId, gameweek.GameweekId, ownedPlayerIds);
    }

    /// <summary>Seeds a full PlayerPerformance row (Goals/GoalsConceded/OwnGoals, not just FantasyPoints) for one player this Gameweek.</summary>
    private async Task SeedFullPlayerPerformanceAsync(Guid gameweekId, Guid playerId, int goals = 0, int goalsConceded = 0, int ownGoals = 0)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        db.PlayerPerformances.Add(new PlayerPerformance
        {
            PlayerPerformanceId = Guid.NewGuid(),
            GameweekId = gameweekId,
            PlayerId = playerId,
            MinutesPlayed = 90,
            Goals = goals,
            GoalsConceded = goalsConceded,
            OwnGoals = ownGoals,
            Source = PerformanceSource.OfficialFpl,
            IsOfficial = true,
            RetrievedAt = _clock.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedSeasonGoalPredictionAsync(Guid seasonId, Guid fantasyTeamId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
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
    }

    private async Task SubmitAndLockRosterAsync(Guid fantasyTeamId, Guid gameweekId, List<Guid> playerIds, Guid? captainPlayerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var rosterService = scope.ServiceProvider.GetRequiredService<IRosterService>();
        var roster = await rosterService.SubmitAsync(fantasyTeamId, gameweekId, playerIds, captainPlayerId, ifMatchXmin: null);

        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var tracked = await db.GameweekRosters.SingleAsync(r => r.GameweekRosterId == roster.GameweekRosterId);
        tracked.Lock(_clock.UtcNow);
        await db.SaveChangesAsync();
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

    private async Task CalculateAsync(Guid gameweekId)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IGameweekScoreCalculationService>().CalculateForGameweekAsync(gameweekId);
    }

    private async Task<GameweekScore> GetScoreAsync(Guid fantasyTeamId, Guid gameweekId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.GameweekScores.SingleAsync(s => s.FantasyTeamId == fantasyTeamId && s.GameweekId == gameweekId);
    }

    private async Task<Guid> GetLeagueMembershipIdAsync(Guid fantasyTeamId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return (await db.FantasyTeams.SingleAsync(ft => ft.FantasyTeamId == fantasyTeamId)).LeagueMembershipId;
    }

    private async Task<List<NotificationRequest>> GetWeeklyScoreNotificationsAsync(Guid leagueMembershipId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.NotificationRequests
            .Where(r => r.LeagueMembershipId == leagueMembershipId && r.EventType == NotificationEventType.WeeklyScore)
            .ToListAsync();
    }

    [Fact]
    public async Task CalculateForGameweekAsync_scores_a_Locked_roster_and_marks_it_Scored()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedFantasyTeamWithSquadAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await SubmitAndLockRosterAsync(fantasyTeamId, gameweekId, ownedPlayerIds, captainPlayerId: ownedPlayerIds[0]);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[0], fantasyPoints: 9);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[1], fantasyPoints: 2);

        await CalculateAsync(gameweekId);

        var score = await GetScoreAsync(fantasyTeamId, gameweekId);
        Assert.Equal(20, score.FantasyPoints); // Captain's 9 doubled (BR-047) to 18, plus 2.
        Assert.Equal(18, score.CaptainPoints);
        Assert.Equal(_clock.UtcNow, score.CalculatedAt);

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var roster = await db.GameweekRosters.SingleAsync(r => r.FantasyTeamId == fantasyTeamId && r.GameweekId == gameweekId);
        Assert.Equal(RosterStatus.Scored, roster.Status);
    }

    [Fact]
    public async Task CalculateForGameweekAsync_skips_a_roster_that_is_still_Submitted()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedFantasyTeamWithSquadAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IRosterService>()
                .SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, ownedPlayerIds[0], ifMatchXmin: null);
        }
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[0], fantasyPoints: 9);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[1], fantasyPoints: 2);

        await CalculateAsync(gameweekId);

        await using var scope2 = _provider.CreateAsyncScope();
        var db = scope2.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.False(await db.GameweekScores.AnyAsync(s => s.FantasyTeamId == fantasyTeamId));
    }

    [Fact]
    public async Task CalculateForGameweekAsync_treats_a_player_with_no_PlayerPerformance_row_as_zero_points()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedFantasyTeamWithSquadAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await SubmitAndLockRosterAsync(fantasyTeamId, gameweekId, ownedPlayerIds, captainPlayerId: null);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[0], fantasyPoints: 9);
        // ownedPlayerIds[1] never got a PlayerPerformance row this Gameweek — didn't play, or stats haven't synced.

        await CalculateAsync(gameweekId);

        var score = await GetScoreAsync(fantasyTeamId, gameweekId);
        Assert.Equal(9, score.FantasyPoints);
        Assert.Equal(0, score.CaptainPoints); // no Captain was designated.
    }

    [Fact]
    public async Task CalculateForGameweekAsync_a_non_playing_Captain_contributes_zero_CaptainPoints_with_no_fallback()
    {
        // IT-35 (F-008.3 AC3, BR-048): the designated Captain (ownedPlayerIds[0]) has no
        // PlayerPerformance row at all this Gameweek — no official appearance. BR-049/BR-051 mean
        // there is no vice-captain for the multiplier to fall back to instead: the Captain
        // contributes exactly zero, and the other player's own points are entirely unaffected.
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedFantasyTeamWithSquadAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await SubmitAndLockRosterAsync(fantasyTeamId, gameweekId, ownedPlayerIds, captainPlayerId: ownedPlayerIds[0]);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[1], fantasyPoints: 7);
        // ownedPlayerIds[0] (the Captain) never got a PlayerPerformance row this Gameweek.

        await CalculateAsync(gameweekId);

        var score = await GetScoreAsync(fantasyTeamId, gameweekId);
        Assert.Equal(0, score.CaptainPoints); // 0 x 2 is still 0, not a fallback to the other player's 7.
        Assert.Equal(7, score.FantasyPoints); // the Captain's own zero, plus the other player's 7.
    }

    [Fact]
    public async Task CalculateForGameweekAsync_prefers_an_active_ScoreOverrides_fantasyPoints_over_official_data()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedFantasyTeamWithSquadAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await SubmitAndLockRosterAsync(fantasyTeamId, gameweekId, ownedPlayerIds, captainPlayerId: ownedPlayerIds[0]);
        var overriddenPerformanceId = await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[0], fantasyPoints: 9);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[1], fantasyPoints: 2);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.ScoreOverrides.Add(new ScoreOverride
            {
                ScoreOverrideId = Guid.NewGuid(),
                PlayerPerformanceId = overriddenPerformanceId,
                AdministratorMembershipId = await db.LeagueMemberships.Select(m => m.LeagueMembershipId).FirstAsync(),
                OriginalValueJson = "{\"fantasyPoints\":9}",
                OverrideValueJson = "{\"fantasyPoints\":15}",
                CreatedAt = _clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await CalculateAsync(gameweekId);

        var score = await GetScoreAsync(fantasyTeamId, gameweekId);
        Assert.Equal(32, score.FantasyPoints); // (15 overridden, not 9) doubled (BR-047) to 30, plus 2.
        Assert.Equal(30, score.CaptainPoints);
    }

    [Fact]
    public async Task CalculateForGameweekAsync_is_idempotent_and_does_not_recalculate_an_already_scored_FantasyTeam()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedFantasyTeamWithSquadAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await SubmitAndLockRosterAsync(fantasyTeamId, gameweekId, ownedPlayerIds, captainPlayerId: ownedPlayerIds[0]);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[0], fantasyPoints: 9);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[1], fantasyPoints: 2);

        await CalculateAsync(gameweekId);
        var firstScore = await GetScoreAsync(fantasyTeamId, gameweekId);

        // BR-079: a later re-sync/re-calculation pass must not silently recompute an already-
        // finalized result. (Even if the underlying PlayerPerformance later changes, this task's
        // own scope has no recalculation-cascade trigger — that's IT-37's job.)
        await CalculateAsync(gameweekId);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var scoreCount = await db.GameweekScores.CountAsync(s => s.FantasyTeamId == fantasyTeamId && s.GameweekId == gameweekId);
        Assert.Equal(1, scoreCount);
        var secondScore = await GetScoreAsync(fantasyTeamId, gameweekId);
        Assert.Equal(firstScore.GameweekScoreId, secondScore.GameweekScoreId);
        Assert.Equal(firstScore.CalculatedAt, secondScore.CalculatedAt);
    }

    [Fact]
    public async Task CalculateForGameweekAsync_persists_SelectionRole_and_excludes_Bench_points_from_FantasyPoints()
    {
        // 12 rostered players — one more than the fixed Starting XI size (BR-044) — so the lowest
        // scorer genuinely lands on the Bench and this proves the persisted RosterPlayer.SelectionRole
        // (not just the in-memory ranking GameweekRosterTests already covers) and that the Bench
        // player's points are excluded from FantasyPoints (BR-038-BR-040).
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedFantasyTeamWithSquadAsync(playerCount: 12);
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await SubmitAndLockRosterAsync(fantasyTeamId, gameweekId, ownedPlayerIds, captainPlayerId: null);

        var expectedFantasyPoints = 0;
        for (var i = 0; i < ownedPlayerIds.Count; i++)
        {
            var points = 20 - i; // strictly descending: player 0 scores highest, the last player (index 11) scores lowest.
            await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[i], points);
            if (i < 11)
            {
                expectedFantasyPoints += points;
            }
        }

        await CalculateAsync(gameweekId);

        var score = await GetScoreAsync(fantasyTeamId, gameweekId);
        Assert.Equal(expectedFantasyPoints, score.FantasyPoints); // the 12th (lowest-scoring) player's points are excluded.

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var roster = await db.GameweekRosters.Include(r => r.Players).SingleAsync(r => r.FantasyTeamId == fantasyTeamId && r.GameweekId == gameweekId);
        Assert.Equal(11, roster.Players.Count(p => p.SelectionRole == SelectionRole.StartingXi));
        Assert.Equal(SelectionRole.Bench, roster.Players.Single(p => p.PlayerId == ownedPlayerIds[11]).SelectionRole);
    }

    [Fact]
    public async Task CalculateForGameweekAsync_computes_FantasyGoalsAgainst_using_the_BRDs_own_worked_example()
    {
        // BR-086/BR-087/BR-088's literal worked example: a goalkeeper who conceded 2, plus five
        // defenders whose clubs conceded 1+2+1+1+2 = 7 between them (7 / 5 = 1.4, truncated to 1).
        var positions = new[] { PlayerPosition.Gk, PlayerPosition.Def, PlayerPosition.Def, PlayerPosition.Def, PlayerPosition.Def, PlayerPosition.Def };
        var (fantasyTeamId, seasonId, gameweekId, playerIds) = await SeedFantasyTeamWithPositionedSquadAsync(positions);
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await SubmitAndLockRosterAsync(fantasyTeamId, gameweekId, playerIds, captainPlayerId: null);

        await SeedFullPlayerPerformanceAsync(gameweekId, playerIds[0], goalsConceded: 2); // the goalkeeper.
        var defenderGoalsConceded = new[] { 1, 2, 1, 1, 2 };
        for (var i = 0; i < defenderGoalsConceded.Length; i++)
        {
            await SeedFullPlayerPerformanceAsync(gameweekId, playerIds[1 + i], goalsConceded: defenderGoalsConceded[i]);
        }

        await CalculateAsync(gameweekId);

        var score = await GetScoreAsync(fantasyTeamId, gameweekId);
        Assert.Equal(0, score.FantasyGoalsFor); // nobody scored.
        Assert.Equal(3, score.FantasyGoalsAgainst); // 2 (goalkeeper) + 1 (defenders, truncated per the worked example).
        Assert.Equal(-3, score.FantasyGoalDifference);
    }

    [Fact]
    public async Task CalculateForGameweekAsync_includes_a_Bench_players_goals_in_FantasyGoalsFor()
    {
        // BR-080: unlike FantasyPoints (Starting XI only, BR-044), Fantasy Goals For counts the
        // entire submitted 15 — here, 12 Forwards so the 12th genuinely lands on the Bench, yet
        // their own goal still counts.
        var positions = Enumerable.Repeat(PlayerPosition.Fwd, 12).ToArray();
        var (fantasyTeamId, seasonId, gameweekId, playerIds) = await SeedFantasyTeamWithPositionedSquadAsync(positions);
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await SubmitAndLockRosterAsync(fantasyTeamId, gameweekId, playerIds, captainPlayerId: null);

        for (var i = 0; i < playerIds.Count; i++)
        {
            // Descending fantasy points so player 11 (index 11) is unambiguously the Bench player,
            // but every player scores exactly one goal regardless of Starting XI/Bench status.
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.PlayerPerformances.Add(new PlayerPerformance
            {
                PlayerPerformanceId = Guid.NewGuid(),
                GameweekId = gameweekId,
                PlayerId = playerIds[i],
                MinutesPlayed = 90,
                FantasyPoints = 20 - i,
                Goals = 1,
                Source = PerformanceSource.OfficialFpl,
                IsOfficial = true,
                RetrievedAt = _clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await CalculateAsync(gameweekId);

        var score = await GetScoreAsync(fantasyTeamId, gameweekId);
        Assert.Equal(12, score.FantasyGoalsFor); // all 12 goals count, including the Bench player's own.

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var roster = await verifyDb.GameweekRosters.Include(r => r.Players).SingleAsync(r => r.FantasyTeamId == fantasyTeamId && r.GameweekId == gameweekId);
        Assert.Equal(SelectionRole.Bench, roster.Players.Single(p => p.PlayerId == playerIds[11]).SelectionRole);
    }

    [Fact]
    public async Task CalculateForGameweekAsync_adds_an_own_goal_to_FantasyGoalsAgainst_and_excludes_it_from_For()
    {
        // BR-090/BR-091: an own goal never counts as a goal scored by that player, and instead
        // increases Fantasy Goals Against.
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedFantasyTeamWithSquadAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await SubmitAndLockRosterAsync(fantasyTeamId, gameweekId, ownedPlayerIds, captainPlayerId: null);
        await SeedFullPlayerPerformanceAsync(gameweekId, ownedPlayerIds[0], goals: 2, ownGoals: 1);
        await SeedFullPlayerPerformanceAsync(gameweekId, ownedPlayerIds[1]);

        await CalculateAsync(gameweekId);

        var score = await GetScoreAsync(fantasyTeamId, gameweekId);
        Assert.Equal(2, score.FantasyGoalsFor); // the own goal is not among these 2.
        Assert.Equal(1, score.FantasyGoalsAgainst); // the own goal instead.
        Assert.Equal(1, score.FantasyGoalDifference);
    }

    [Fact]
    public async Task CalculateForGameweekAsync_queues_a_WeeklyScore_notification_per_channel_for_the_newly_scored_FantasyTeam()
    {
        // IT-56 (F-012.3 AC1/BR-153): finalizing a GameweekScore queues one NotificationRequest per
        // NotificationChannel, unconditionally — preference suppression is the outbox dispatcher's job.
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedFantasyTeamWithSquadAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await SubmitAndLockRosterAsync(fantasyTeamId, gameweekId, ownedPlayerIds, captainPlayerId: ownedPlayerIds[0]);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[0], fantasyPoints: 9);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[1], fantasyPoints: 2);

        await CalculateAsync(gameweekId);

        var membershipId = await GetLeagueMembershipIdAsync(fantasyTeamId);
        var notifications = await GetWeeklyScoreNotificationsAsync(membershipId);
        Assert.Equal(2, notifications.Count); // one per NotificationChannel (Email, Sms).
        Assert.All(notifications, n => Assert.Equal(NotificationStatus.Pending, n.Status));
        Assert.Contains(notifications, n => n.Channel == NotificationChannel.Email);
        Assert.Contains(notifications, n => n.Channel == NotificationChannel.Sms);
    }

    [Fact]
    public async Task CalculateForGameweekAsync_does_not_queue_a_second_WeeklyScore_notification_for_an_already_scored_FantasyTeam()
    {
        // BR-079's own "finalized once" guarantee already prevents this method's outer loop from
        // ever revisiting an already-scored FantasyTeam, so the notification inherits that same
        // once-only guarantee for free — no separate dedup logic was written for it.
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedFantasyTeamWithSquadAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await SubmitAndLockRosterAsync(fantasyTeamId, gameweekId, ownedPlayerIds, captainPlayerId: ownedPlayerIds[0]);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[0], fantasyPoints: 9);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[1], fantasyPoints: 2);

        await CalculateAsync(gameweekId);
        await CalculateAsync(gameweekId); // a second pass over the same, already-scored Gameweek.

        var membershipId = await GetLeagueMembershipIdAsync(fantasyTeamId);
        Assert.Equal(2, (await GetWeeklyScoreNotificationsAsync(membershipId)).Count);
    }

    [Fact]
    public async Task A_queued_WeeklyScore_notification_is_suppressed_by_the_outbox_dispatcher_for_the_default_disabled_preference()
    {
        // The task's own required test (matching IT-55's identical proof): BR-226/IT-53 seed every
        // (EventType, Channel) row disabled by default, and this service never checks preferences
        // itself — suppression is entirely the already-built outbox dispatcher's (IT-F12) job.
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedFantasyTeamWithSquadAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await SubmitAndLockRosterAsync(fantasyTeamId, gameweekId, ownedPlayerIds, captainPlayerId: ownedPlayerIds[0]);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[0], fantasyPoints: 9);
        await SeedPlayerPerformanceAsync(gameweekId, ownedPlayerIds[1], fantasyPoints: 2);

        await CalculateAsync(gameweekId);

        var dispatcher = new NotificationOutboxBackgroundService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new NotificationOutboxOptions { BaseBackoff = TimeSpan.FromMinutes(1), MaxBackoff = TimeSpan.FromHours(1), MaxAttempts = 3 }),
            NullLogger<NotificationOutboxBackgroundService>.Instance);
        await dispatcher.RunOneDispatchPassAsync(CancellationToken.None);

        var membershipId = await GetLeagueMembershipIdAsync(fantasyTeamId);
        var notifications = await GetWeeklyScoreNotificationsAsync(membershipId);
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, n => Assert.Equal(NotificationStatus.Suppressed, n.Status));
    }
}
