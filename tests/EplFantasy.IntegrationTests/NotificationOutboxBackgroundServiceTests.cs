using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.Notifications;
using EplFantasy.SharedKernel;
using EplFantasy.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves IT-F12's dispatch-pass claims — BR-226 preference suppression, BR-225 exponential
/// backoff with retry, and eventual Failed after MaxAttempts — against real Postgres, using
/// FakeNotificationSender in place of the not-yet-chosen email/SMS provider (Architecture §15).
/// </summary>
public class NotificationOutboxBackgroundServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
    private readonly FakeClock _clock = FakeClock.StartingAt(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly FakeNotificationSender _emailSender = new(NotificationChannel.Email);
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        var services = new ServiceCollection();
        services.AddInfrastructure(_container.GetConnectionString());
        services.AddSingleton<IClock>(_clock);
        services.AddSingleton<INotificationSender>(_emailSender);
        services.AddSingleton(Options.Create(new NotificationOutboxOptions
        {
            BaseBackoff = TimeSpan.FromMinutes(1),
            MaxBackoff = TimeSpan.FromHours(1),
            MaxAttempts = 3,
        }));
        services.AddSingleton<NotificationOutboxBackgroundService>();
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

    private NotificationOutboxBackgroundService CreateDispatcher() =>
        new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<IOptions<NotificationOutboxOptions>>(),
            NullLogger<NotificationOutboxBackgroundService>.Instance);

    private async Task<Guid> SeedMembershipAsync()
    {
        // notification_requests/notification_preferences both carry real FKs to users and
        // league_memberships (NotificationConfigurations.cs, IT-F12) — a random Guid is rejected.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var suffix = Guid.NewGuid().ToString("N");
        var user = new User { UserId = Guid.NewGuid(), Username = $"nob_{suffix}", Email = $"nob_{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(user);
        var leagueId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        db.Leagues.Add(new League { LeagueId = leagueId, Name = "Outbox Smoke League", CreatedByMembershipId = membershipId, CreatedAt = _clock.UtcNow });
        db.LeagueMemberships.Add(new LeagueMembership { LeagueMembershipId = membershipId, LeagueId = leagueId, UserId = user.UserId, IsAdministrator = true, Status = MembershipStatus.Active, JoinedAt = _clock.UtcNow });

        await db.SaveChangesAsync();
        return membershipId;
    }

    private async Task<Guid> SeedRequestAsync(Guid membershipId, NotificationChannel channel = NotificationChannel.Email)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var membership = await db.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == membershipId);

        var requestId = Guid.NewGuid();
        db.NotificationRequests.Add(new NotificationRequest
        {
            RequestId = requestId,
            UserId = membership.UserId,
            LeagueMembershipId = membershipId,
            EventType = NotificationEventType.GameweekReminder,
            Channel = channel,
            PayloadJson = "{}",
            Status = NotificationStatus.Pending,
            CreatedAt = _clock.UtcNow,
        });

        await db.SaveChangesAsync();
        return requestId;
    }

    private async Task SetPreferenceAsync(Guid membershipId, bool enabled, NotificationChannel channel = NotificationChannel.Email)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        db.NotificationPreferences.Add(new NotificationPreference
        {
            LeagueMembershipId = membershipId,
            EventType = NotificationEventType.GameweekReminder,
            Channel = channel,
            Enabled = enabled,
        });
        await db.SaveChangesAsync();
    }

    private async Task<NotificationRequest> GetRequestAsync(Guid requestId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.NotificationRequests.SingleAsync(r => r.RequestId == requestId);
    }

    [Fact]
    public async Task An_eligible_request_with_an_enabled_preference_is_sent()
    {
        var membershipId = await SeedMembershipAsync();
        await SetPreferenceAsync(membershipId, enabled: true);
        var requestId = await SeedRequestAsync(membershipId);

        await CreateDispatcher().RunOneDispatchPassAsync(CancellationToken.None);

        var request = await GetRequestAsync(requestId);
        Assert.Equal(NotificationStatus.Sent, request.Status);
        Assert.Contains(_emailSender.SentRequests, r => r.RequestId == requestId);
    }

    [Fact]
    public async Task A_disabled_preference_suppresses_the_request_instead_of_sending_it()
    {
        var membershipId = await SeedMembershipAsync();
        await SetPreferenceAsync(membershipId, enabled: false);
        var requestId = await SeedRequestAsync(membershipId);

        await CreateDispatcher().RunOneDispatchPassAsync(CancellationToken.None);

        var request = await GetRequestAsync(requestId);
        Assert.Equal(NotificationStatus.Suppressed, request.Status);
        Assert.DoesNotContain(_emailSender.SentRequests, r => r.RequestId == requestId);
    }

    [Fact]
    public async Task A_missing_preference_row_suppresses_the_request_per_BR_226()
    {
        var membershipId = await SeedMembershipAsync();
        // Deliberately no NotificationPreference row at all.
        var requestId = await SeedRequestAsync(membershipId);

        await CreateDispatcher().RunOneDispatchPassAsync(CancellationToken.None);

        var request = await GetRequestAsync(requestId);
        Assert.Equal(NotificationStatus.Suppressed, request.Status);
    }

    [Fact]
    public async Task A_failed_send_stays_pending_and_retries_only_after_its_backoff_window_elapses()
    {
        var membershipId = await SeedMembershipAsync();
        await SetPreferenceAsync(membershipId, enabled: true);
        var requestId = await SeedRequestAsync(membershipId);

        _emailSender.ShouldFail = true;
        var dispatcher = CreateDispatcher();

        await dispatcher.RunOneDispatchPassAsync(CancellationToken.None);
        var afterFirstFailure = await GetRequestAsync(requestId);
        Assert.Equal(NotificationStatus.Pending, afterFirstFailure.Status);
        Assert.Equal(1, afterFirstFailure.Attempts);

        // Still well inside the 1-minute base backoff — must not retry yet.
        _clock.AdvanceBy(TimeSpan.FromSeconds(5));
        await dispatcher.RunOneDispatchPassAsync(CancellationToken.None);
        var stillBackingOff = await GetRequestAsync(requestId);
        Assert.Equal(1, stillBackingOff.Attempts);

        // Past the backoff window, and the sender now succeeds.
        _clock.AdvanceBy(TimeSpan.FromMinutes(2));
        _emailSender.ShouldFail = false;
        await dispatcher.RunOneDispatchPassAsync(CancellationToken.None);
        var afterRetry = await GetRequestAsync(requestId);
        Assert.Equal(NotificationStatus.Sent, afterRetry.Status);
        Assert.Contains(_emailSender.SentRequests, r => r.RequestId == requestId);
    }

    [Fact]
    public async Task A_request_that_keeps_failing_past_MaxAttempts_is_marked_Failed_instead_of_retried_forever()
    {
        var membershipId = await SeedMembershipAsync();
        await SetPreferenceAsync(membershipId, enabled: true);
        var requestId = await SeedRequestAsync(membershipId);

        _emailSender.ShouldFail = true;
        var dispatcher = CreateDispatcher();

        // MaxAttempts is configured to 3 (InitializeAsync) — three failing passes, each past the
        // prior one's backoff window, exhaust it.
        for (var i = 0; i < 3; i++)
        {
            await dispatcher.RunOneDispatchPassAsync(CancellationToken.None);
            _clock.AdvanceBy(TimeSpan.FromHours(1));
        }

        var request = await GetRequestAsync(requestId);
        Assert.Equal(NotificationStatus.Failed, request.Status);
        Assert.Equal(3, request.Attempts);
    }
}
