using EplFantasy.Administration;
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
/// Proves IT-08's configuration-update orchestration against real Postgres: BR-292's
/// always-mutable League defaults, BR-293/BR-294's per-field Season lock, and BR-295's exactly-one
/// ConfigurationChanged administrative_actions row per write.
/// </summary>
public class ConfigurationServiceTests : IAsyncLifetime
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

    private static ConfigurationValues ChangedValues(int initialSquadSize = 30) => new(
        InitialSquadSize: initialSquadSize,
        WeeklyRosterSize: 16,
        PositionalMinimumGk: 2,
        PositionalMinimumDef: 4,
        PositionalMinimumMid: 3,
        PositionalMinimumFwd: 2,
        DraftTimerSecondsInitial: 400,
        DraftTimerSecondsSecondary: 400,
        DraftTimerSecondsReplacement: 400,
        SecondaryDraftSelectionsPerTeam: 6,
        SecondaryDraftSchedulingOffsetDays: 2,
        GameweekRosterLockOffsetBeforeKickoffMinutes: 90,
        LeaguePointsWin: 4,
        LeaguePointsDraw: 2,
        LeaguePointsLoss: 1,
        InvitationExpirationDays: 10,
        ReplacementSelectionCap: 5,
        GameweekReminderLeadTimeHours: 48,
        TieBreakRulesetVersion: "v2");

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
        return await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(ownerUserId, "Configuration Test League", null);
    }

    [Fact]
    public async Task UpdateLeagueConfigurationAsync_applies_the_change_and_writes_one_administrative_action()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db);
        var league = await SeedLeagueAsync(ownerId);
        var service = scope.ServiceProvider.GetRequiredService<IConfigurationService>();

        var configuration = await service.UpdateLeagueConfigurationAsync(league.LeagueId, league.CreatedByMembershipId, ChangedValues());

        Assert.Equal(30, configuration.InitialSquadSize);
        Assert.Equal(_clock.UtcNow, configuration.UpdatedAt);
        Assert.Equal(league.CreatedByMembershipId, configuration.UpdatedByMembershipId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.LeagueConfigurations.SingleAsync(c => c.LeagueId == league.LeagueId);
        Assert.Equal(30, persisted.InitialSquadSize);

        var action = await verifyDb.AdministrativeActions.SingleAsync(a => a.TargetEntityId == league.LeagueId && a.ActionType == AdminActionType.ConfigurationChanged);
        Assert.Equal("League", action.TargetEntityType);
        Assert.Equal(league.CreatedByMembershipId, action.ActingMembershipId);
        Assert.Contains("30", action.AfterStateJson);
    }

    [Fact]
    public async Task UpdateSeasonConfigurationAsync_applies_the_change_when_nothing_is_locked_and_writes_one_administrative_action()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db);
        var league = await SeedLeagueAsync(ownerId);
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = "2026/27" });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>().CreateAsync(league.LeagueId, "2026/27", new DateOnly(2026, 8, 15))).Value;
        var service = scope.ServiceProvider.GetRequiredService<IConfigurationService>();

        var result = await service.UpdateSeasonConfigurationAsync(league.LeagueId, season.SeasonId, league.CreatedByMembershipId, ChangedValues());

        Assert.True(result.IsSuccess);
        Assert.Equal(30, result.Value.InitialSquadSize);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var action = await verifyDb.AdministrativeActions.SingleAsync(a => a.TargetEntityId == season.SeasonId && a.ActionType == AdminActionType.ConfigurationChanged);
        Assert.Equal("Season", action.TargetEntityType);
    }

    [Fact]
    public async Task UpdateSeasonConfigurationAsync_rejects_a_changed_locked_field_and_writes_no_administrative_action()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db);
        var league = await SeedLeagueAsync(ownerId);
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = "2026/27" });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>().CreateAsync(league.LeagueId, "2026/27", new DateOnly(2026, 8, 15))).Value;

        // Simulate the owning feature (e.g. IT-23, Initial Draft start) having locked this field —
        // no such feature exists yet, so this reaches into the row directly, the same precedent
        // established throughout this codebase for a downstream feature that isn't built yet.
        var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(c => c.SeasonId == season.SeasonId);
        seasonConfiguration.Lock(nameof(SeasonConfiguration.InitialSquadSize));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
        await Assert.ThrowsAsync<SeasonConfigurationFieldsLockedException>(
            () => service.UpdateSeasonConfigurationAsync(league.LeagueId, season.SeasonId, league.CreatedByMembershipId, ChangedValues()));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.SeasonConfigurations.SingleAsync(c => c.SeasonId == season.SeasonId);
        Assert.Equal(25, persisted.InitialSquadSize); // untouched
        Assert.Equal(15, persisted.WeeklyRosterSize); // untouched, even though it wasn't locked
        Assert.False(await verifyDb.AdministrativeActions.AnyAsync(a => a.TargetEntityId == season.SeasonId));
    }

    [Fact]
    public async Task UpdateSeasonConfigurationAsync_still_applies_unlocked_fields_when_the_locked_ones_are_resubmitted_unchanged()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db);
        var league = await SeedLeagueAsync(ownerId);
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = "2026/27" });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>().CreateAsync(league.LeagueId, "2026/27", new DateOnly(2026, 8, 15))).Value;
        var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(c => c.SeasonId == season.SeasonId);
        seasonConfiguration.Lock(nameof(SeasonConfiguration.InitialSquadSize));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
        // InitialSquadSize kept at its current (default) 25 — every other field changes.
        var result = await service.UpdateSeasonConfigurationAsync(league.LeagueId, season.SeasonId, league.CreatedByMembershipId, ChangedValues(initialSquadSize: 25));

        Assert.True(result.IsSuccess);
        Assert.Equal(25, result.Value.InitialSquadSize);
        Assert.Equal(16, result.Value.WeeklyRosterSize);
    }

    [Fact]
    public async Task UpdateSeasonConfigurationAsync_fails_for_a_seasonId_that_does_not_belong_to_the_League()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db);
        var league = await SeedLeagueAsync(ownerId);
        var otherLeague = await SeedLeagueAsync(ownerId);
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = "2026/27" });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>().CreateAsync(otherLeague.LeagueId, "2026/27", new DateOnly(2026, 8, 15))).Value;

        var service = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
        var result = await service.UpdateSeasonConfigurationAsync(league.LeagueId, season.SeasonId, league.CreatedByMembershipId, ChangedValues());

        Assert.True(result.IsFailure);
        Assert.Equal("season_not_found", result.Error.Code);
    }
}
