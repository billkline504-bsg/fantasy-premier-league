using EplFantasy.Administration;
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
/// Proves IT-03's League-creation/update orchestration against real Postgres: BR-024's mutually
/// referential League/LeagueMembership rows committing together (the DEFERRABLE FK from V004),
/// the founding membership always ending up Active+Administrator, and updateLeague's exactly-one
/// <c>administrative_actions</c> row (AP-005, IT-F07).
/// </summary>
public class LeagueServiceTests : IAsyncLifetime
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
        var user = new User { UserId = Guid.NewGuid(), Username = $"league_owner_{suffix}", Email = $"league_owner_{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    [Fact]
    public async Task CreateAsync_creates_the_League_and_its_founding_Administrator_membership_together()
    {
        await using var scope = _provider.CreateAsyncScope();
        var leagueService = scope.ServiceProvider.GetRequiredService<ILeagueService>();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var userId = await SeedUserAsync(db);

        var league = await leagueService.CreateAsync(userId, "Office League", "Bragging rights only");

        Assert.Equal("Office League", league.Name);
        Assert.Equal(LeagueStatus.Active, league.Status);
        Assert.Equal(_clock.UtcNow, league.CreatedAt);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var persistedLeague = await verifyDb.Leagues.SingleAsync(l => l.LeagueId == league.LeagueId);
        Assert.Equal(persistedLeague.CreatedByMembershipId, league.CreatedByMembershipId);

        var membership = await verifyDb.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == league.CreatedByMembershipId);
        Assert.Equal(league.LeagueId, membership.LeagueId);
        Assert.Equal(userId, membership.UserId);
        Assert.True(membership.IsAdministrator);
        Assert.Equal(MembershipStatus.Active, membership.Status);

        // IT-04 needs this row from the moment the League exists (e.g. InvitationExpirationDays
        // at invitation-issuance time) — it is not deferred to IT-08's own config endpoints.
        var configuration = await verifyDb.LeagueConfigurations.SingleAsync(c => c.LeagueId == league.LeagueId);
        Assert.Equal(7, configuration.InvitationExpirationDays);

        // IT-53 (F-012.1, BR-338): the founding membership gets its own full, disabled-by-default
        // notification preference set the instant it exists — not deferred to a later self-service call.
        var preferences = await verifyDb.NotificationPreferences.Where(p => p.LeagueMembershipId == league.CreatedByMembershipId).ToListAsync();
        Assert.Equal(6, preferences.Count); // 3 event types x 2 channels.
        Assert.All(preferences, p => Assert.False(p.Enabled));
    }

    [Fact]
    public async Task UpdateAsync_applies_only_the_provided_fields_and_writes_one_administrative_action()
    {
        await using var scope = _provider.CreateAsyncScope();
        var leagueService = scope.ServiceProvider.GetRequiredService<ILeagueService>();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var userId = await SeedUserAsync(db);
        var created = await leagueService.CreateAsync(userId, "Original Name", "Original description");

        var updated = await leagueService.UpdateAsync(created.LeagueId, userId, name: null, description: null, status: LeagueStatus.Archived);

        Assert.Equal("Original Name", updated.Name);
        Assert.Equal("Original description", updated.Description);
        Assert.Equal(LeagueStatus.Archived, updated.Status);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var persisted = await verifyDb.Leagues.SingleAsync(l => l.LeagueId == created.LeagueId);
        Assert.Equal(LeagueStatus.Archived, persisted.Status);

        var action = await verifyDb.AdministrativeActions.SingleAsync(a => a.TargetEntityId == created.LeagueId);
        Assert.Equal(AdminActionType.Other, action.ActionType);
        Assert.Equal("League", action.TargetEntityType);
        Assert.Equal(created.CreatedByMembershipId, action.ActingMembershipId);
        Assert.Contains("Archived", action.AfterStateJson);
    }
}
