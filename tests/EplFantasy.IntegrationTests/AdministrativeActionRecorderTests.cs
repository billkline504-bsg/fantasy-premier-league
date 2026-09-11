using System.Text.Json;
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
/// Proves IT-F07's core claim — that IAdministrativeActionRecorder.Record() commits atomically
/// with whatever domain change the caller makes in the same SaveChangesAsync() call — rather than
/// trusting the "same DbContext, same transaction" reasoning in its doc comments on faith.
/// </summary>
public class AdministrativeActionRecorderTests : IAsyncLifetime
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

    private async Task<(Guid LeagueId, Guid AdminMembershipId, Guid UserId)> SeedLeagueAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var suffix = Guid.NewGuid().ToString("N");
        var user = new User { UserId = Guid.NewGuid(), Username = $"aar_{suffix}", Email = $"aar_{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(user);

        var leagueId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        db.Leagues.Add(new League { LeagueId = leagueId, Name = "Recorder Smoke League", CreatedByMembershipId = membershipId, CreatedAt = _clock.UtcNow });
        db.LeagueMemberships.Add(new LeagueMembership { LeagueMembershipId = membershipId, LeagueId = leagueId, UserId = user.UserId, IsAdministrator = true, Status = MembershipStatus.Active, JoinedAt = _clock.UtcNow });

        await db.SaveChangesAsync();
        return (leagueId, membershipId, user.UserId);
    }

    [Fact]
    public async Task Record_and_a_domain_change_commit_together_in_one_SaveChangesAsync()
    {
        var (leagueId, membershipId, _) = await SeedLeagueAsync();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<IAdministrativeActionRecorder>();

        var league = await db.Leagues.SingleAsync(l => l.LeagueId == leagueId);
        var before = new { league.Name };
        league.Name = "Renamed League";
        var after = new { league.Name };

        recorder.Record(leagueId, membershipId, AdminActionType.ConfigurationChanged, "League", leagueId, before, after, "smoke test rename");

        await db.SaveChangesAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var reloadedLeague = await verifyDb.Leagues.SingleAsync(l => l.LeagueId == leagueId);
        Assert.Equal("Renamed League", reloadedLeague.Name);

        var action = await verifyDb.AdministrativeActions.SingleAsync(a => a.LeagueId == leagueId);
        Assert.Equal(membershipId, action.ActingMembershipId);
        Assert.Equal(AdminActionType.ConfigurationChanged, action.ActionType);
        Assert.Equal(_clock.UtcNow, action.CreatedAt);
    }

    [Fact]
    public async Task Record_never_persists_when_the_accompanying_domain_change_fails_in_the_same_SaveChangesAsync()
    {
        var (leagueId, membershipId, userId) = await SeedLeagueAsync();
        var attemptedActionId = Guid.Empty;

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            var recorder = scope.ServiceProvider.GetRequiredService<IAdministrativeActionRecorder>();

            // Deliberately violates ux_league_memberships_one_admin (BR-283): a second
            // administrator for a League that already has one.
            db.LeagueMemberships.Add(new LeagueMembership
            {
                LeagueMembershipId = Guid.NewGuid(),
                LeagueId = leagueId,
                UserId = userId,
                IsAdministrator = true,
                Status = MembershipStatus.Active,
                JoinedAt = _clock.UtcNow,
            });

            recorder.Record(leagueId, membershipId, AdminActionType.Other, "LeagueMembership", membershipId, new { }, new { }, "this must not survive");

            // Capture the id EF assigned so we can prove no row with it exists afterward — the
            // AdministrativeAction row is in the same pending SaveChanges batch as the doomed insert.
            attemptedActionId = db.ChangeTracker.Entries<AdministrativeAction>().Single().Entity.ActionId;

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var survived = await verifyDb.AdministrativeActions.AnyAsync(a => a.ActionId == attemptedActionId);
        Assert.False(survived, "the audit row must not survive when the domain change it accompanies is rolled back");
    }

    [Fact]
    public async Task Record_with_a_null_acting_membership_represents_a_system_generated_entry()
    {
        var (leagueId, _, _) = await SeedLeagueAsync();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<IAdministrativeActionRecorder>();

        var targetId = Guid.NewGuid();
        recorder.Record(leagueId, null, AdminActionType.ReplacementEligibilityGranted, "FantasyTeam", targetId, new { }, new { eligible = true });
        await db.SaveChangesAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var action = await verifyDb.AdministrativeActions.SingleAsync(a => a.TargetEntityId == targetId);

        Assert.Null(action.ActingMembershipId);
    }

    [Fact]
    public async Task BeforeState_and_AfterState_serialize_the_given_objects_as_real_structured_json()
    {
        var (leagueId, membershipId, _) = await SeedLeagueAsync();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<IAdministrativeActionRecorder>();

        var targetId = Guid.NewGuid();
        recorder.Record(
            leagueId, membershipId, AdminActionType.ConfigurationChanged, "League", targetId,
            beforeState: new { Parameter = "WeeklyRosterSize", Value = 15 },
            afterState: new { Parameter = "WeeklyRosterSize", Value = 20 });
        await db.SaveChangesAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var action = await verifyDb.AdministrativeActions.SingleAsync(a => a.TargetEntityId == targetId);

        using var before = JsonDocument.Parse(action.BeforeStateJson);
        using var after = JsonDocument.Parse(action.AfterStateJson);

        Assert.Equal(15, before.RootElement.GetProperty("Value").GetInt32());
        Assert.Equal(20, after.RootElement.GetProperty("Value").GetInt32());
    }
}
