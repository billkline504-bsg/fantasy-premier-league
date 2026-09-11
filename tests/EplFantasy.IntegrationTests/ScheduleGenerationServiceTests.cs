using EplFantasy.Competition;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using EplFantasy.SharedKernel;
using EplFantasy.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves IT-38's ScheduleGenerationService (F-009.1, BR-107-BR-110) against real Postgres: a
/// balanced round-robin schedule is generated across every already-synced Gameweek for an even
/// FantasyTeam count (every team plays every Gameweek); an odd count instead gives each team
/// exactly one bye across a full cycle, with no HeadToHeadMatch row fabricated for it (BR-306); a
/// second call is a safe no-op (BR-110); and fewer than two FantasyTeams, or no synced Gameweeks
/// yet, produce no schedule at all. RoundRobinScheduler's own pairing/balance correctness is proven
/// at the domain level, RoundRobinSchedulerTests.
/// </summary>
public class ScheduleGenerationServiceTests : IAsyncLifetime
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

    /// <summary>Builds a real League/Season (via the actual application services, the same precedent DraftServiceTests already established) with <paramref name="fantasyTeamCount"/> Active FantasyTeams and <paramref name="gameweekCount"/> already-synced Gameweeks.</summary>
    private async Task<(Guid SeasonId, List<Guid> FantasyTeamIds, List<Guid> GameweekIds)> SeedSeasonAsync(int fantasyTeamCount, int gameweekCount)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var ownerId = await SeedUserAsync(db, "owner");
        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(ownerId, $"Schedule League {suffix}", null);

        var eplSeasonIdentifier = $"schedule-season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;

        var fantasyTeamService = scope.ServiceProvider.GetRequiredService<IFantasyTeamService>();
        var fantasyTeamIds = new List<Guid>();
        if (fantasyTeamCount > 0)
        {
            var firstTeam = await fantasyTeamService.CreateAsync(league.CreatedByMembershipId, season.SeasonId);
            fantasyTeamIds.Add(firstTeam.Value.FantasyTeamId);
        }

        for (var i = 1; i < fantasyTeamCount; i++)
        {
            var memberUserId = await SeedUserAsync(db, $"member{i}");
            var membership = LeagueMembership.Join(Guid.NewGuid(), league.LeagueId, memberUserId, _clock.UtcNow);
            db.LeagueMemberships.Add(membership);
            await db.SaveChangesAsync();
            var team = await fantasyTeamService.CreateAsync(membership.LeagueMembershipId, season.SeasonId);
            fantasyTeamIds.Add(team.Value.FantasyTeamId);
        }

        var gameweekIds = new List<Guid>();
        for (var number = 1; number <= gameweekCount; number++)
        {
            var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = number, RosterLockDeadline = _clock.UtcNow.AddDays(number * 7) };
            db.Gameweeks.Add(gameweek);
            gameweekIds.Add(gameweek.GameweekId);
        }

        await db.SaveChangesAsync();

        return (season.SeasonId, fantasyTeamIds, gameweekIds);
    }

    private async Task<List<HeadToHeadMatch>> GetMatchesAsync(Guid seasonId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.HeadToHeadMatches.Where(m => m.SeasonId == seasonId).ToListAsync();
    }

    [Fact]
    public async Task GenerateAsync_creates_a_balanced_round_robin_schedule_for_an_even_team_count()
    {
        var (seasonId, fantasyTeamIds, gameweekIds) = await SeedSeasonAsync(fantasyTeamCount: 4, gameweekCount: 3);

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IScheduleGenerationService>().GenerateAsync(seasonId);
        }

        var matches = await GetMatchesAsync(seasonId);
        Assert.Equal(6, matches.Count); // C(4,2) = 6 — every pair meets exactly once over 3 Gameweeks.
        Assert.All(matches, m => Assert.NotEqual(m.HomeFantasyTeamId, m.AwayFantasyTeamId));
        Assert.All(gameweekIds, gwId => Assert.Equal(2, matches.Count(m => m.GameweekId == gwId))); // every team plays every Gameweek.
        Assert.All(matches, m => Assert.Null(m.Result)); // BR-098-style: created empty, F-009.2/IT-39's own job to play it out.

        var everyMatchedPair = matches.Select(m => (First: m.HomeFantasyTeamId, Second: m.AwayFantasyTeamId)).ToHashSet();
        Assert.Equal(6, everyMatchedPair.Count); // no duplicate pairing across the schedule.
        Assert.Equal(fantasyTeamIds.ToHashSet(), matches.SelectMany(m => new[] { m.HomeFantasyTeamId, m.AwayFantasyTeamId }).ToHashSet());
    }

    [Fact]
    public async Task GenerateAsync_gives_every_team_exactly_one_bye_for_an_odd_team_count_and_fabricates_no_match()
    {
        var (seasonId, fantasyTeamIds, gameweekIds) = await SeedSeasonAsync(fantasyTeamCount: 3, gameweekCount: 3);

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IScheduleGenerationService>().GenerateAsync(seasonId);
        }

        var matches = await GetMatchesAsync(seasonId);
        Assert.Equal(3, matches.Count); // one match per Gameweek (the third team sits out each time).
        Assert.All(gameweekIds, gwId => Assert.Single(matches, m => m.GameweekId == gwId));

        foreach (var gameweekId in gameweekIds)
        {
            var playing = matches.Where(m => m.GameweekId == gameweekId).SelectMany(m => new[] { m.HomeFantasyTeamId, m.AwayFantasyTeamId }).ToHashSet();
            Assert.Equal(2, playing.Count); // exactly 2 of the 3 teams play; the third has a bye (BR-306) — no row for it at all.
        }
    }

    [Fact]
    public async Task GenerateAsync_is_idempotent_and_does_not_regenerate_an_existing_schedule()
    {
        var (seasonId, _, _) = await SeedSeasonAsync(fantasyTeamCount: 4, gameweekCount: 3);

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IScheduleGenerationService>().GenerateAsync(seasonId);
        }
        var firstSchedule = (await GetMatchesAsync(seasonId)).Select(m => m.MatchId).ToHashSet();

        // BR-110: a second call must not regenerate — the schedule already exists.
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IScheduleGenerationService>().GenerateAsync(seasonId);
        }
        var secondSchedule = (await GetMatchesAsync(seasonId)).Select(m => m.MatchId).ToHashSet();

        Assert.Equal(firstSchedule, secondSchedule);
    }

    [Fact]
    public async Task GenerateAsync_does_nothing_with_fewer_than_two_FantasyTeams()
    {
        var (seasonId, _, _) = await SeedSeasonAsync(fantasyTeamCount: 1, gameweekCount: 3);

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IScheduleGenerationService>().GenerateAsync(seasonId);

        Assert.Empty(await GetMatchesAsync(seasonId));
    }

    [Fact]
    public async Task GenerateAsync_does_nothing_when_no_Gameweeks_have_synced_yet()
    {
        var (seasonId, _, _) = await SeedSeasonAsync(fantasyTeamCount: 4, gameweekCount: 0);

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IScheduleGenerationService>().GenerateAsync(seasonId);

        Assert.Empty(await GetMatchesAsync(seasonId));
    }
}
