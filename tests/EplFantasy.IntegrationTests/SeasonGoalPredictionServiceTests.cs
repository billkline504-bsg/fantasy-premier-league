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
/// Proves IT-07's Season Goal Prediction submission against real Postgres: BR-127/BR-128's
/// lock-at-Season-start, BR-299's late-submission-locks-immediately fallback, and that a second
/// submission before locking updates rather than duplicating the row (the unique
/// `(season_id, fantasy_team_id)` index db-tests/070...sql already proves at the database level).
/// </summary>
public class SeasonGoalPredictionServiceTests : IAsyncLifetime
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

    /// <summary>
    /// Seeds a League + Season + one FantasyTeam directly via the DbContext — F-002.3's own
    /// creation flow (IT-11) doesn't exist yet, so this mirrors AuthorizationHandlerTests'
    /// established precedent for standing up a FantasyTeam row ahead of the feature that will
    /// eventually own creating it for real.
    /// </summary>
    private async Task<(Guid SeasonId, Guid FantasyTeamId)> SeedSeasonAndFantasyTeamAsync(DateOnly seasonStartDate)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owner = new User { UserId = Guid.NewGuid(), Username = $"owner_{suffix}", Email = $"owner_{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(owner.UserId, "Prediction Test League", null);

        var eplSeasonIdentifier = $"season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();

        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, seasonStartDate)).Value;

        var fantasyTeamId = Guid.NewGuid();
        db.FantasyTeams.Add(new FantasyTeam
        {
            FantasyTeamId = fantasyTeamId,
            LeagueMembershipId = league.CreatedByMembershipId,
            SeasonId = season.SeasonId,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        });
        await db.SaveChangesAsync();

        return (season.SeasonId, fantasyTeamId);
    }

    [Fact]
    public async Task SubmitAsync_before_Season_start_locks_at_Season_start()
    {
        var (seasonId, fantasyTeamId) = await SeedSeasonAndFantasyTeamAsync(DateOnly.FromDateTime(_clock.UtcNow.AddDays(14).UtcDateTime));

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISeasonGoalPredictionService>();

        var result = await service.SubmitAsync(seasonId, fantasyTeamId, 1234);

        Assert.True(result.IsSuccess);
        var prediction = result.Value;
        Assert.Equal(1234, prediction.PredictedEplGoals);
        Assert.Equal(_clock.UtcNow, prediction.SubmittedAt);
        Assert.Equal(new DateTimeOffset(_clock.UtcNow.AddDays(14).UtcDateTime.Date, TimeSpan.Zero), prediction.LockedAt);
        Assert.False(prediction.IsLocked(_clock.UtcNow));
    }

    [Fact]
    public async Task SubmitAsync_after_Season_start_locks_immediately_per_BR_299()
    {
        // The Season already started (yesterday) — this is the FantasyTeam's first-ever
        // submission, arriving late.
        var (seasonId, fantasyTeamId) = await SeedSeasonAndFantasyTeamAsync(DateOnly.FromDateTime(_clock.UtcNow.AddDays(-1).UtcDateTime));

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISeasonGoalPredictionService>();

        var result = await service.SubmitAsync(seasonId, fantasyTeamId, 1234);

        Assert.True(result.IsSuccess);
        var prediction = result.Value;
        Assert.Equal(_clock.UtcNow, prediction.LockedAt);
        Assert.True(prediction.IsLocked(_clock.UtcNow));
    }

    [Fact]
    public async Task SubmitAsync_updates_the_same_row_when_called_again_before_locking()
    {
        var (seasonId, fantasyTeamId) = await SeedSeasonAndFantasyTeamAsync(DateOnly.FromDateTime(_clock.UtcNow.AddDays(14).UtcDateTime));

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISeasonGoalPredictionService>();
        var first = (await service.SubmitAsync(seasonId, fantasyTeamId, 1200)).Value;

        _clock.AdvanceBy(TimeSpan.FromDays(1));
        var second = await service.SubmitAsync(seasonId, fantasyTeamId, 1234);

        Assert.True(second.IsSuccess);
        Assert.Equal(first.PredictionId, second.Value.PredictionId);
        Assert.Equal(1234, second.Value.PredictedEplGoals);
        Assert.Equal(_clock.UtcNow, second.Value.SubmittedAt);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var rowCount = await verifyDb.SeasonGoalPredictions.CountAsync(p => p.SeasonId == seasonId && p.FantasyTeamId == fantasyTeamId);
        Assert.Equal(1, rowCount);
    }

    [Fact]
    public async Task SubmitAsync_throws_once_the_existing_prediction_is_locked()
    {
        // Season already started, so the first submission locks immediately (BR-299).
        var (seasonId, fantasyTeamId) = await SeedSeasonAndFantasyTeamAsync(DateOnly.FromDateTime(_clock.UtcNow.AddDays(-1).UtcDateTime));

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISeasonGoalPredictionService>();
        await service.SubmitAsync(seasonId, fantasyTeamId, 1200);

        await Assert.ThrowsAsync<SeasonGoalPredictionLockedException>(() => service.SubmitAsync(seasonId, fantasyTeamId, 9999));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.SeasonGoalPredictions.SingleAsync(p => p.SeasonId == seasonId && p.FantasyTeamId == fantasyTeamId);
        Assert.Equal(1200, persisted.PredictedEplGoals); // unchanged
    }

    [Fact]
    public async Task SubmitAsync_fails_for_a_fantasyTeamId_that_does_not_belong_to_the_Season()
    {
        var (seasonId, _) = await SeedSeasonAndFantasyTeamAsync(DateOnly.FromDateTime(_clock.UtcNow.AddDays(14).UtcDateTime));

        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISeasonGoalPredictionService>();

        var result = await service.SubmitAsync(seasonId, Guid.NewGuid(), 1234);

        Assert.True(result.IsFailure);
        Assert.Equal("fantasy_team_not_found", result.Error.Code);
    }
}
