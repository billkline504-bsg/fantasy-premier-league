using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.Notifications;
using EplFantasy.SharedKernel;
using EplFantasy.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves IT-53's NotificationPreferenceService (F-012.1, BR-150/BR-151/BR-155/BR-338) against
/// real Postgres: GetPreferencesAsync returns the full seeded set, UpdatePreferencesAsync applies
/// only the requested (EventType, Channel) changes and leaves every other row untouched, and — the
/// task's own central rule (BR-338) — a User's preferences in one League Membership are completely
/// independent of their preferences in a different League Membership, even for the exact same
/// event/channel combination.
/// </summary>
public class NotificationPreferenceServiceTests : IAsyncLifetime
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

    [Fact]
    public async Task GetPreferencesAsync_returns_the_full_seeded_set_for_a_freshly_created_League()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(ownerId, "Notification League", null);

        var preferences = await scope.ServiceProvider.GetRequiredService<INotificationPreferenceService>()
            .GetPreferencesAsync(league.CreatedByMembershipId);

        Assert.Equal(6, preferences.Count);
        Assert.All(preferences, p => Assert.Equal(league.CreatedByMembershipId, p.LeagueMembershipId));
        Assert.All(preferences, p => Assert.False(p.Enabled));
    }

    [Fact]
    public async Task UpdatePreferencesAsync_applies_only_the_requested_changes_and_leaves_every_other_row_untouched()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(ownerId, "Notification League", null);
        var service = scope.ServiceProvider.GetRequiredService<INotificationPreferenceService>();

        var updated = await service.UpdatePreferencesAsync(
            league.CreatedByMembershipId,
            [new NotificationPreferenceChange(NotificationEventType.WeeklyScore, NotificationChannel.Email, true)]);

        Assert.Equal(6, updated.Count); // the full set is always returned, not just the changed rows.
        var changedRow = Assert.Single(updated, p => p.EventType == NotificationEventType.WeeklyScore && p.Channel == NotificationChannel.Email);
        Assert.True(changedRow.Enabled);
        Assert.All(updated.Where(p => p != changedRow), p => Assert.False(p.Enabled)); // every other combination is untouched.
    }

    [Fact]
    public async Task Preferences_are_fully_independent_between_two_League_Memberships_for_the_same_User()
    {
        // BR-338: the same User, in two different Leagues, toggling the exact same event/channel
        // combination in one must never affect the other.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var userId = await SeedUserAsync(db, "busy_manager");
        var leagueService = scope.ServiceProvider.GetRequiredService<ILeagueService>();
        var leagueA = await leagueService.CreateAsync(userId, "Busy League", null);
        var leagueB = await leagueService.CreateAsync(userId, "Quiet League", null);
        var service = scope.ServiceProvider.GetRequiredService<INotificationPreferenceService>();

        await service.UpdatePreferencesAsync(
            leagueA.CreatedByMembershipId,
            [new NotificationPreferenceChange(NotificationEventType.WeeklyScore, NotificationChannel.Email, true)]);

        var leagueBPreferences = await service.GetPreferencesAsync(leagueB.CreatedByMembershipId);
        var leagueBWeeklyScoreEmail = Assert.Single(leagueBPreferences, p => p.EventType == NotificationEventType.WeeklyScore && p.Channel == NotificationChannel.Email);
        Assert.False(leagueBWeeklyScoreEmail.Enabled); // unaffected by League A's own change.

        var leagueAPreferences = await service.GetPreferencesAsync(leagueA.CreatedByMembershipId);
        var leagueAWeeklyScoreEmail = Assert.Single(leagueAPreferences, p => p.EventType == NotificationEventType.WeeklyScore && p.Channel == NotificationChannel.Email);
        Assert.True(leagueAWeeklyScoreEmail.Enabled);
    }
}
