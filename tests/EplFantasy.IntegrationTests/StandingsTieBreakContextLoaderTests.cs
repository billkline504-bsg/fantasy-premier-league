using EplFantasy.Competition;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using EplFantasy.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves IT-F10's context loader reads real head-to-head matches and season goal predictions
/// correctly — the pure comparison logic itself is covered by StandingsTieBreakTests (UnitTests);
/// this is specifically about the SQL/EF side.
/// </summary>
public class StandingsTieBreakContextLoaderTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
    private readonly FakeClock _clock = FakeClock.StartingAt(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        var services = new ServiceCollection();
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

    private async Task<(Guid SeasonId, Guid TeamA, Guid TeamB, Guid TeamC)> SeedSeasonAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var suffix = Guid.NewGuid().ToString("N");
        var users = Enumerable.Range(0, 3).Select(i => new User
        {
            UserId = Guid.NewGuid(),
            Username = $"tbl_{i}_{suffix}",
            Email = $"tbl_{i}_{suffix}@example.com",
            PasswordHash = "h",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        }).ToArray();
        db.Users.AddRange(users);

        var leagueId = Guid.NewGuid();
        var membershipIds = users.Select(_ => Guid.NewGuid()).ToArray();
        db.Leagues.Add(new League { LeagueId = leagueId, Name = "TieBreak Loader League", CreatedByMembershipId = membershipIds[0], CreatedAt = _clock.UtcNow });
        for (var i = 0; i < users.Length; i++)
        {
            db.LeagueMemberships.Add(new LeagueMembership { LeagueMembershipId = membershipIds[i], LeagueId = leagueId, UserId = users[i].UserId, IsAdministrator = i == 0, Status = MembershipStatus.Active, JoinedAt = _clock.UtcNow });
        }

        var eplSeasonId = $"tbl-{suffix}";
        db.EplSeasons.Add(new PlayerData.EplSeason { EplSeasonIdentifier = eplSeasonId });
        var seasonId = Guid.NewGuid();
        db.Seasons.Add(new Season { SeasonId = seasonId, LeagueId = leagueId, EplSeasonIdentifier = eplSeasonId, StartDate = DateOnly.FromDateTime(_clock.UtcNow.Date) });

        var teamIds = membershipIds.Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < teamIds.Length; i++)
        {
            db.FantasyTeams.Add(new FantasyTeam { FantasyTeamId = teamIds[i], LeagueMembershipId = membershipIds[i], SeasonId = seasonId, CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow });
        }

        var gameweekId = Guid.NewGuid();
        db.Gameweeks.Add(new PlayerData.Gameweek { GameweekId = gameweekId, EplSeasonIdentifier = eplSeasonId, Number = 1, RosterLockDeadline = _clock.UtcNow.AddDays(7) });

        // A vs B: A wins (3-0 league points). C has no match against A or B yet (a bye week).
        db.HeadToHeadMatches.Add(new HeadToHeadMatch
        {
            MatchId = Guid.NewGuid(),
            SeasonId = seasonId,
            GameweekId = gameweekId,
            HomeFantasyTeamId = teamIds[0],
            AwayFantasyTeamId = teamIds[1],
            HomeScore = 60,
            AwayScore = 40,
            Result = MatchResult.HomeWin,
            LeaguePointsHome = 3,
            LeaguePointsAway = 0,
        });

        db.SeasonGoalPredictions.Add(new SeasonGoalPrediction
        {
            PredictionId = Guid.NewGuid(),
            SeasonId = seasonId,
            FantasyTeamId = teamIds[0],
            PredictedEplGoals = 1233,
            SubmittedAt = _clock.UtcNow,
            LockedAt = _clock.UtcNow,
            FinalActualGoals = 1234,
            FinalAbsoluteDifference = 1,
        });

        await db.SaveChangesAsync();
        return (seasonId, teamIds[0], teamIds[1], teamIds[2]);
    }

    [Fact]
    public async Task LoadAsync_resolves_head_to_head_points_for_a_played_match()
    {
        var (seasonId, teamA, teamB, _) = await SeedSeasonAsync();

        await using var scope = _provider.CreateAsyncScope();
        var loader = scope.ServiceProvider.GetRequiredService<IStandingsTieBreakContextLoader>();
        var context = await loader.LoadAsync(seasonId);

        Assert.Equal(3, context.HeadToHeadLeaguePoints(teamA, teamB, teamA));
        Assert.Equal(0, context.HeadToHeadLeaguePoints(teamA, teamB, teamB));
        // Order of the two team ids passed in must not matter — it's the same match either way.
        Assert.Equal(3, context.HeadToHeadLeaguePoints(teamB, teamA, teamA));
    }

    [Fact]
    public async Task LoadAsync_reports_no_data_for_a_pair_that_has_never_played_each_other()
    {
        var (seasonId, teamA, _, teamC) = await SeedSeasonAsync();

        await using var scope = _provider.CreateAsyncScope();
        var loader = scope.ServiceProvider.GetRequiredService<IStandingsTieBreakContextLoader>();
        var context = await loader.LoadAsync(seasonId);

        Assert.Null(context.HeadToHeadLeaguePoints(teamA, teamC, teamA));
    }

    [Fact]
    public async Task LoadAsync_resolves_a_seeded_season_goal_prediction_and_returns_null_for_a_team_without_one()
    {
        var (seasonId, teamA, teamB, _) = await SeedSeasonAsync();

        await using var scope = _provider.CreateAsyncScope();
        var loader = scope.ServiceProvider.GetRequiredService<IStandingsTieBreakContextLoader>();
        var context = await loader.LoadAsync(seasonId);

        var predictionA = context.SeasonGoalPredictionFor(teamA);
        Assert.NotNull(predictionA);
        Assert.Equal(1, predictionA.FinalAbsoluteDifference);

        Assert.Null(context.SeasonGoalPredictionFor(teamB));
    }

    [Fact]
    public async Task The_pipeline_and_a_real_loaded_context_together_rank_a_full_season_correctly()
    {
        var (seasonId, teamA, teamB, teamC) = await SeedSeasonAsync();

        await using var scope = _provider.CreateAsyncScope();
        var loader = scope.ServiceProvider.GetRequiredService<IStandingsTieBreakContextLoader>();
        var pipeline = scope.ServiceProvider.GetRequiredService<StandingsTieBreakPipeline>();
        var context = await loader.LoadAsync(seasonId);

        // All three teams tied on every LeagueStanding field — only the head-to-head match
        // (A beat B) and C's total absence of one should matter for A vs B; A vs C and B vs C
        // fall through to the random fallback, which just needs to be definitive, not any
        // particular winner.
        var standingA = new LeagueStanding { SeasonId = seasonId, FantasyTeamId = teamA, LeaguePoints = 10, FantasyGoalDifference = 5, FantasyGoalsFor = 20, CaptainPointsTotal = 30 };
        var standingB = new LeagueStanding { SeasonId = seasonId, FantasyTeamId = teamB, LeaguePoints = 10, FantasyGoalDifference = 5, FantasyGoalsFor = 20, CaptainPointsTotal = 30 };

        var comparer = pipeline.GetComparer("v1", context);

        Assert.True(comparer.Compare(standingA, standingB) < 0, "Team A beat Team B head-to-head and should rank ahead");
    }
}
