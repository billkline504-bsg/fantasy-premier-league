using EplFantasy.Drafts;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves IT-45's SecondaryDraftSchedulingService (F-006.1, BR-069/BR-281) against real Postgres:
/// assembles a Season's own configured SecondaryDraftSchedulingOffsetDays and its real fixture
/// calendar (scoped to that Season's own EplSeasonIdentifier — a different Season's fixtures on the
/// same calendar day must never affect this one), then delegates the actual proposal math to the
/// already unit-tested SecondaryDraftScheduler. SecondaryDraftScheduler's own test suite
/// (SecondaryDraftSchedulerTests) proves the day-by-day advance logic itself — these tests only
/// prove this service assembles its inputs correctly.
/// </summary>
public class SecondaryDraftSchedulingServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(_container.GetConnectionString());
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

    /// <summary>Builds a real League/Season with WeeklyRosterSize/etc untouched, and a Gameweek to hang Fixtures off of. Returns the SeasonId and that Gameweek's own EplSeasonIdentifier.</summary>
    private async Task<(Guid SeasonId, string EplSeasonIdentifier, Guid GameweekId)> SeedSeasonAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var owner = new User { UserId = Guid.NewGuid(), Username = $"owner{suffix}", Email = $"owner{suffix}@example.com", PasswordHash = "h", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(owner.UserId, $"Secondary Draft League {suffix}", null);

        var eplSeasonIdentifier = $"secondary-draft-season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;

        var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = 1, RosterLockDeadline = DateTimeOffset.UtcNow.AddDays(7) };
        db.Gameweeks.Add(gameweek);
        await db.SaveChangesAsync();

        return (season.SeasonId, eplSeasonIdentifier, gameweek.GameweekId);
    }

    private async Task SeedFixtureAsync(Guid gameweekId, DateTimeOffset kickoffTime)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var home = new Club { ClubId = Guid.NewGuid(), EplClubId = $"c{Guid.NewGuid():N}"[..10], Name = "Home Club", ShortName = "HOM" };
        var away = new Club { ClubId = Guid.NewGuid(), EplClubId = $"c{Guid.NewGuid():N}"[..10], Name = "Away Club", ShortName = "AWY" };
        db.Clubs.AddRange(home, away);

        db.Fixtures.Add(new Fixture
        {
            FixtureId = Guid.NewGuid(),
            EplFixtureId = $"f{Guid.NewGuid():N}"[..10],
            GameweekId = gameweekId,
            HomeClubId = home.ClubId,
            AwayClubId = away.ClubId,
            KickoffTime = kickoffTime,
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ProposeStartDateAsync_returns_the_offset_date_when_no_fixtures_are_scheduled()
    {
        var (seasonId, _, _) = await SeedSeasonAsync();

        await using var scope = _provider.CreateAsyncScope();
        var proposed = await scope.ServiceProvider.GetRequiredService<ISecondaryDraftSchedulingService>()
            .ProposeStartDateAsync(seasonId, new DateOnly(2027, 1, 31));

        Assert.Equal(new DateOnly(2027, 2, 1), proposed); // default offset of 1 day (BR-291).
    }

    [Fact]
    public async Task ProposeStartDateAsync_advances_past_a_day_with_a_real_scheduled_Fixture()
    {
        var (seasonId, _, gameweekId) = await SeedSeasonAsync();
        await SeedFixtureAsync(gameweekId, new DateTimeOffset(2027, 2, 1, 15, 0, 0, TimeSpan.Zero));

        await using var scope = _provider.CreateAsyncScope();
        var proposed = await scope.ServiceProvider.GetRequiredService<ISecondaryDraftSchedulingService>()
            .ProposeStartDateAsync(seasonId, new DateOnly(2027, 1, 31));

        Assert.Equal(new DateOnly(2027, 2, 2), proposed);
    }

    [Fact]
    public async Task ProposeStartDateAsync_ignores_a_Fixture_belonging_to_a_different_Seasons_EplSeasonIdentifier()
    {
        var (seasonId, _, _) = await SeedSeasonAsync();
        var (_, _, otherGameweekId) = await SeedSeasonAsync(); // a distinct Season/EplSeasonIdentifier.
        await SeedFixtureAsync(otherGameweekId, new DateTimeOffset(2027, 2, 1, 15, 0, 0, TimeSpan.Zero));

        await using var scope = _provider.CreateAsyncScope();
        var proposed = await scope.ServiceProvider.GetRequiredService<ISecondaryDraftSchedulingService>()
            .ProposeStartDateAsync(seasonId, new DateOnly(2027, 1, 31));

        Assert.Equal(new DateOnly(2027, 2, 1), proposed); // unaffected by the other Season's own Fixture.
    }

    [Fact]
    public async Task ProposeStartDateAsync_returns_null_when_the_transfer_window_close_date_is_not_yet_confirmed()
    {
        var (seasonId, _, _) = await SeedSeasonAsync();

        await using var scope = _provider.CreateAsyncScope();
        var proposed = await scope.ServiceProvider.GetRequiredService<ISecondaryDraftSchedulingService>()
            .ProposeStartDateAsync(seasonId, transferWindowCloseDate: null);

        Assert.Null(proposed);
    }
}
