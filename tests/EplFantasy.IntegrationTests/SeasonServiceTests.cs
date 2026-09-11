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
/// Proves IT-06's Season-creation orchestration against real Postgres: BR-292's LeagueConfiguration
/// snapshot, and that a second Season for the same League neither touches nor is affected by the
/// first's already-copied SeasonConfiguration.
/// </summary>
public class SeasonServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
    private readonly FakeClock _clock = FakeClock.StartingAt(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
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

    private async Task<Guid> SeedUserAsync(EplFantasyDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { UserId = Guid.NewGuid(), Username = $"owner_{suffix}", Email = $"owner_{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    private async Task<League> SeedLeagueAsync(Guid ownerUserId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(ownerUserId, "Season Test League", null);
    }

    private async Task SeedEplSeasonAsync(EplFantasyDbContext db, string eplSeasonIdentifier)
    {
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CreateAsync_creates_the_Season_and_copies_the_Leagues_current_configuration()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db);
        var league = await SeedLeagueAsync(ownerId);
        await SeedEplSeasonAsync(db, "2026/27");

        var seasonService = scope.ServiceProvider.GetRequiredService<ISeasonService>();
        var result = await seasonService.CreateAsync(league.LeagueId, "2026/27", new DateOnly(2026, 8, 15));

        Assert.True(result.IsSuccess);
        var season = result.Value;
        Assert.Equal(league.LeagueId, season.LeagueId);
        Assert.Equal("2026/27", season.EplSeasonIdentifier);
        Assert.Equal(SeasonStatus.Setup, season.Status);
        Assert.Equal(new DateOnly(2026, 8, 15), season.StartDate);
        Assert.Null(season.EndDate);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var seasonConfiguration = await verifyDb.SeasonConfigurations.SingleAsync(c => c.SeasonId == season.SeasonId);
        Assert.Equal(25, seasonConfiguration.InitialSquadSize); // LeagueConfiguration.CreateDefault's own default
        Assert.Equal(7, seasonConfiguration.InvitationExpirationDays);
        Assert.Empty(seasonConfiguration.LockedFields);
    }

    [Fact]
    public async Task CreateAsync_fails_for_an_epl_season_identifier_that_is_not_known_yet()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db);
        var league = await SeedLeagueAsync(ownerId);

        var seasonService = scope.ServiceProvider.GetRequiredService<ISeasonService>();
        var result = await seasonService.CreateAsync(league.LeagueId, "2099/00", new DateOnly(2099, 8, 15));

        Assert.True(result.IsFailure);
        Assert.Equal("epl_season_not_found", result.Error.Code);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.False(await verifyDb.Seasons.AnyAsync(s => s.LeagueId == league.LeagueId));
    }

    [Fact]
    public async Task A_second_Season_for_the_same_League_does_not_touch_the_first_seasons_configuration()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db);
        var league = await SeedLeagueAsync(ownerId);
        await SeedEplSeasonAsync(db, "2026/27");
        await SeedEplSeasonAsync(db, "2027/28");

        var seasonService = scope.ServiceProvider.GetRequiredService<ISeasonService>();
        var firstSeason = (await seasonService.CreateAsync(league.LeagueId, "2026/27", new DateOnly(2026, 8, 15))).Value;

        // The League Administrator overrides a League-level default between the two Seasons (the
        // real path is IT-08's updateLeagueConfiguration; mutating the row directly here proves
        // the same isolation without waiting on that later task to exist).
        var leagueConfiguration = await db.LeagueConfigurations.SingleAsync(c => c.LeagueId == league.LeagueId);
        leagueConfiguration.InitialSquadSize = 30;
        await db.SaveChangesAsync();

        var secondSeason = (await seasonService.CreateAsync(league.LeagueId, "2027/28", new DateOnly(2027, 8, 14))).Value;

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var firstConfiguration = await verifyDb.SeasonConfigurations.SingleAsync(c => c.SeasonId == firstSeason.SeasonId);
        var secondConfiguration = await verifyDb.SeasonConfigurations.SingleAsync(c => c.SeasonId == secondSeason.SeasonId);

        Assert.Equal(25, firstConfiguration.InitialSquadSize); // unaffected by the later League-level change
        Assert.Equal(30, secondConfiguration.InitialSquadSize); // reflects the League's value at ITS creation time
        Assert.NotEqual(firstSeason.SeasonId, secondSeason.SeasonId);

        var seasonsForLeague = await verifyDb.Seasons.Where(s => s.LeagueId == league.LeagueId).ToListAsync();
        Assert.Equal(2, seasonsForLeague.Count);
    }
}
