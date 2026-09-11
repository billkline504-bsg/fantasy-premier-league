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
/// Proves IT-11's FantasyTeam-creation orchestration against real Postgres: BR-018/BR-019's
/// correct initial state, and BR-193/Invariant 2's "one per (LeagueMembership, Season)" —
/// already proven at the database layer by db-tests/030...sql's
/// `fantasy_team.one_per_membership_season`; this is the application-service round-trip the task
/// explicitly asks for.
/// </summary>
public class FantasyTeamServiceTests : IAsyncLifetime
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

    private async Task<Guid> SeedUserAsync(EplFantasyDbContext db, string label)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { UserId = Guid.NewGuid(), Username = $"{label}_{suffix}", Email = $"{label}_{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    private async Task<Season> SeedLeagueAndSeasonAsync(EplFantasyDbContext db, Guid ownerUserId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(ownerUserId, "FantasyTeam Test League", null);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var eplSeasonIdentifier = $"season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();

        return (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;
    }

    [Fact]
    public async Task CreateAsync_establishes_an_active_FantasyTeam_for_the_owning_membership()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var season = await SeedLeagueAndSeasonAsync(db, ownerId);
        var league = await db.Leagues.SingleAsync(l => l.LeagueId == season.LeagueId);
        var service = scope.ServiceProvider.GetRequiredService<IFantasyTeamService>();

        var result = await service.CreateAsync(league.CreatedByMembershipId, season.SeasonId);

        Assert.True(result.IsSuccess);
        var team = result.Value;
        Assert.Equal(league.CreatedByMembershipId, team.LeagueMembershipId);
        Assert.Equal(season.SeasonId, team.SeasonId);
        Assert.Equal(FantasyTeamStatus.Active, team.Status);
        Assert.Equal(_clock.UtcNow, team.CreatedAt);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.True(await verifyDb.FantasyTeams.AnyAsync(t => t.FantasyTeamId == team.FantasyTeamId));
    }

    [Fact]
    public async Task CreateAsync_rejects_a_second_FantasyTeam_for_the_same_membership_and_season()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var season = await SeedLeagueAndSeasonAsync(db, ownerId);
        var league = await db.Leagues.SingleAsync(l => l.LeagueId == season.LeagueId);
        var service = scope.ServiceProvider.GetRequiredService<IFantasyTeamService>();
        var first = await service.CreateAsync(league.CreatedByMembershipId, season.SeasonId);
        Assert.True(first.IsSuccess);

        var second = await service.CreateAsync(league.CreatedByMembershipId, season.SeasonId);

        Assert.True(second.IsFailure);
        Assert.Equal("fantasy_team_already_exists", second.Error.Code);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var count = await verifyDb.FantasyTeams.CountAsync(t => t.LeagueMembershipId == league.CreatedByMembershipId && t.SeasonId == season.SeasonId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CreateAsync_allows_the_same_membership_to_have_FantasyTeams_across_different_Seasons()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var firstSeason = await SeedLeagueAndSeasonAsync(db, ownerId);
        var league = await db.Leagues.SingleAsync(l => l.LeagueId == firstSeason.LeagueId);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var secondEplSeasonIdentifier = $"season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = secondEplSeasonIdentifier });
        await db.SaveChangesAsync();
        var secondSeason = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, secondEplSeasonIdentifier, new DateOnly(2027, 8, 14))).Value;
        var service = scope.ServiceProvider.GetRequiredService<IFantasyTeamService>();

        var first = await service.CreateAsync(league.CreatedByMembershipId, firstSeason.SeasonId);
        var second = await service.CreateAsync(league.CreatedByMembershipId, secondSeason.SeasonId);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.Value.FantasyTeamId, second.Value.FantasyTeamId);
    }
}
