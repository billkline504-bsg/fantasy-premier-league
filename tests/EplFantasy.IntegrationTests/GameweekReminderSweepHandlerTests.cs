using EplFantasy.Competition;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.Notifications;
using EplFantasy.PlayerData;
using EplFantasy.Rosters;
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
/// Proves IT-55's GameweekReminderSweepHandler (F-012.2, BR-152/BR-338) against real Postgres: a
/// reminder is queued for an unsubmitted FantasyTeam once its Season's own configurable lead time
/// window opens (AC1/AC3), never before (AC1), never once a valid roster is already Submitted
/// (AC2), and never twice for the same FantasyTeam/Gameweek across repeated ticks — the same
/// idempotency guarantee RosterLockSweepHandlerTests proves for IT-31's own sweep. A final test
/// wires the queued request through the already-built NotificationOutboxBackgroundService (IT-F12)
/// to prove the task's own required end-to-end claim: a disabled (the IT-53 default)
/// NotificationPreference suppresses it rather than sending it (BR-226).
/// </summary>
public class GameweekReminderSweepHandlerTests : IAsyncLifetime
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
        services.AddScoped<IDeadlineSweepHandler, GameweekReminderSweepHandler>(); // IT-55's sweep handler — registered the same way Program.cs registers it.
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
    /// Builds a real League/Season/FantasyTeam (via the actual application services), shrinking
    /// WeeklyRosterSize/PositionalMinimums to 1/(0,0,0,1) — a single Forward is a valid roster —
    /// and setting GameweekReminderLeadTimeHours to a test-friendly value, since this suite cares
    /// about reminder-window timing, not roster composition.
    /// </summary>
    private async Task<(Guid FantasyTeamId, Guid LeagueMembershipId, Guid SeasonId, string EplSeasonIdentifier)> SeedFantasyTeamAsync(int leadTimeHours = 24)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var owner = new User { UserId = Guid.NewGuid(), Username = $"owner{suffix}", Email = $"owner{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(owner.UserId, $"Reminder League {suffix}", null);

        var eplSeasonIdentifier = $"reminder-season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;

        var fantasyTeamResult = await scope.ServiceProvider.GetRequiredService<IFantasyTeamService>()
            .CreateAsync(league.CreatedByMembershipId, season.SeasonId);
        var fantasyTeamId = fantasyTeamResult.Value.FantasyTeamId;

        var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == season.SeasonId);
        seasonConfiguration.WeeklyRosterSize = 1;
        seasonConfiguration.PositionalMinimumGk = 0;
        seasonConfiguration.PositionalMinimumDef = 0;
        seasonConfiguration.PositionalMinimumMid = 0;
        seasonConfiguration.PositionalMinimumFwd = 1;
        seasonConfiguration.GameweekReminderLeadTimeHours = leadTimeHours;
        await db.SaveChangesAsync();

        return (fantasyTeamId, league.CreatedByMembershipId, season.SeasonId, eplSeasonIdentifier);
    }

    private async Task<Guid> SeedGameweekAsync(string eplSeasonIdentifier, int number, DateTimeOffset rosterLockDeadline)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = number, RosterLockDeadline = rosterLockDeadline };
        db.Gameweeks.Add(gameweek);
        await db.SaveChangesAsync();
        return gameweek.GameweekId;
    }

    /// <summary>Seeds one owned SquadPlayer (a Forward — this suite's single required position) for the FantasyTeam and returns its PlayerId.</summary>
    private async Task<Guid> SeedOwnedSquadPlayerAsync(Guid fantasyTeamId, Guid seasonId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var playerId = Guid.NewGuid();
        db.Players.Add(new Player { PlayerId = playerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = "Reminder Forward", Position = PlayerPosition.Fwd });
        db.SquadPlayers.Add(new SquadPlayer
        {
            SquadPlayerId = Guid.NewGuid(),
            FantasyTeamId = fantasyTeamId,
            PlayerId = playerId,
            SeasonId = seasonId,
            AcquisitionType = AcquisitionType.InitialDraft,
            AcquiredAt = _clock.UtcNow,
            IsCurrentlyOwned = true,
        });
        await db.SaveChangesAsync();
        return playerId;
    }

    private async Task SeedSeasonGoalPredictionAsync(Guid seasonId, Guid fantasyTeamId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        db.SeasonGoalPredictions.Add(new SeasonGoalPrediction
        {
            PredictionId = Guid.NewGuid(),
            SeasonId = seasonId,
            FantasyTeamId = fantasyTeamId,
            PredictedEplGoals = 1000,
            SubmittedAt = _clock.UtcNow,
            LockedAt = _clock.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();
    }

    private async Task RunSweepAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetServices<IDeadlineSweepHandler>().Single();
        await handler.SweepAsync(CancellationToken.None);
    }

    private async Task<List<NotificationRequest>> LoadRemindersAsync(Guid leagueMembershipId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.NotificationRequests
            .Where(r => r.LeagueMembershipId == leagueMembershipId && r.EventType == NotificationEventType.GameweekReminder)
            .ToListAsync();
    }

    [Fact]
    public async Task Sweep_queues_a_reminder_once_the_configured_lead_time_window_opens_for_an_unsubmitted_FantasyTeam()
    {
        var (_, membershipId, _, eplSeasonIdentifier) = await SeedFantasyTeamAsync(leadTimeHours: 24);
        await SeedGameweekAsync(eplSeasonIdentifier, 1, _clock.UtcNow.AddHours(24));

        _clock.AdvanceBy(TimeSpan.FromMinutes(1)); // now inside the 24h window, deadline not yet passed.
        await RunSweepAsync();

        var reminders = await LoadRemindersAsync(membershipId);
        Assert.Equal(2, reminders.Count); // one row per NotificationChannel (Email, Sms).
        Assert.All(reminders, r => Assert.Equal(NotificationStatus.Pending, r.Status));
        Assert.Contains(reminders, r => r.Channel == NotificationChannel.Email);
        Assert.Contains(reminders, r => r.Channel == NotificationChannel.Sms);
    }

    [Fact]
    public async Task Sweep_does_not_queue_a_reminder_before_the_lead_time_window_opens()
    {
        var (_, membershipId, _, eplSeasonIdentifier) = await SeedFantasyTeamAsync(leadTimeHours: 24);
        await SeedGameweekAsync(eplSeasonIdentifier, 1, _clock.UtcNow.AddHours(48)); // window opens in another 24h.

        await RunSweepAsync();

        Assert.Empty(await LoadRemindersAsync(membershipId));
    }

    [Fact]
    public async Task Sweep_does_not_queue_a_reminder_once_a_valid_roster_has_already_been_submitted()
    {
        var (fantasyTeamId, membershipId, seasonId, eplSeasonIdentifier) = await SeedFantasyTeamAsync(leadTimeHours: 24);
        var playerId = await SeedOwnedSquadPlayerAsync(fantasyTeamId, seasonId);
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        var gameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 1, _clock.UtcNow.AddHours(24));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var rosterService = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await rosterService.SubmitAsync(fantasyTeamId, gameweekId, [playerId], playerId, ifMatchXmin: null);
        }

        _clock.AdvanceBy(TimeSpan.FromMinutes(1)); // inside the window.
        await RunSweepAsync();

        Assert.Empty(await LoadRemindersAsync(membershipId)); // AC2: already-submitted, no noise.
    }

    [Fact]
    public async Task Sweep_does_not_queue_a_second_reminder_for_the_same_FantasyTeam_and_Gameweek_across_repeated_ticks()
    {
        var (_, membershipId, _, eplSeasonIdentifier) = await SeedFantasyTeamAsync(leadTimeHours: 24);
        await SeedGameweekAsync(eplSeasonIdentifier, 1, _clock.UtcNow.AddHours(24));

        _clock.AdvanceBy(TimeSpan.FromMinutes(1));
        await RunSweepAsync();
        _clock.AdvanceBy(TimeSpan.FromHours(1)); // still inside the window, well before the deadline.
        await RunSweepAsync();

        Assert.Equal(2, (await LoadRemindersAsync(membershipId)).Count); // still just the original Email+Sms pair.
    }

    [Fact]
    public async Task Sweep_stops_queuing_once_the_Gameweeks_own_deadline_has_passed()
    {
        var (_, membershipId, _, eplSeasonIdentifier) = await SeedFantasyTeamAsync(leadTimeHours: 24);
        await SeedGameweekAsync(eplSeasonIdentifier, 1, _clock.UtcNow.AddHours(24));

        _clock.AdvanceBy(TimeSpan.FromHours(25)); // past the deadline — IT-31's own sweep owns this moment now.
        await RunSweepAsync();

        Assert.Empty(await LoadRemindersAsync(membershipId));
    }

    [Fact]
    public async Task A_queued_reminder_is_suppressed_by_the_outbox_dispatcher_for_the_default_disabled_preference()
    {
        // BR-226/IT-53: NotificationPreferenceSeeder seeds every (EventType, Channel) row disabled
        // by default at League creation, and this handler never checks preferences itself — the
        // suppression is entirely the outbox dispatcher's (IT-F12) job.
        var (_, membershipId, _, eplSeasonIdentifier) = await SeedFantasyTeamAsync(leadTimeHours: 24);
        await SeedGameweekAsync(eplSeasonIdentifier, 1, _clock.UtcNow.AddHours(24));

        _clock.AdvanceBy(TimeSpan.FromMinutes(1));
        await RunSweepAsync();

        var dispatcher = new NotificationOutboxBackgroundService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new NotificationOutboxOptions { BaseBackoff = TimeSpan.FromMinutes(1), MaxBackoff = TimeSpan.FromHours(1), MaxAttempts = 3 }),
            NullLogger<NotificationOutboxBackgroundService>.Instance);
        await dispatcher.RunOneDispatchPassAsync(CancellationToken.None);

        var reminders = await LoadRemindersAsync(membershipId);
        Assert.Equal(2, reminders.Count);
        Assert.All(reminders, r => Assert.Equal(NotificationStatus.Suppressed, r.Status));
    }
}
