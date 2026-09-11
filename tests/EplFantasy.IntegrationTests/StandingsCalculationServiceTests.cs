using System.Text.Json;
using EplFantasy.Competition;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.Notifications;
using EplFantasy.PlayerData;
using EplFantasy.Scoring;
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
/// Proves IT-42's StandingsCalculationService (F-010.1, BR-118/BR-215-BR-217) against real
/// Postgres: Played/Won/Drawn/Lost/League Points/Fantasy Goals For/Against/Difference are tallied
/// from every already-decided HeadToHeadMatch up to and including the given Gameweek (a not-yet-
/// decided match, or one beyond that Gameweek, contributes nothing — BR-217's point-in-time
/// semantics); Captain Points accumulate across every scored Gameweek up to that point, including a
/// bye week with no scheduled match at all; every FantasyTeam gets a row even with zero matches
/// played; Position is assigned via the full ADR-008 tie-break pipeline (BR-216), not merely League
/// Points; and recalculating the same (Season, AsOfGameweek) snapshot replaces it rather than
/// duplicating rows.
/// </summary>
public class StandingsCalculationServiceTests : IAsyncLifetime
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

    /// <summary>Builds a real League/Season with <paramref name="fantasyTeamCount"/> FantasyTeams and <paramref name="gameweekCount"/> Gameweeks.</summary>
    private async Task<(Guid SeasonId, List<Guid> FantasyTeamIds, List<Guid> GameweekIds)> SeedSeasonAsync(int fantasyTeamCount, int gameweekCount)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var ownerId = await SeedUserAsync(db, "owner");
        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(ownerId, $"Standings League {suffix}", null);

        var eplSeasonIdentifier = $"standings-season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;

        var fantasyTeamService = scope.ServiceProvider.GetRequiredService<IFantasyTeamService>();
        var fantasyTeamIds = new List<Guid>();
        if (fantasyTeamCount > 0)
        {
            var firstTeam = await fantasyTeamService.CreateAsync(league.CreatedByMembershipId, season.SeasonId);
            fantasyTeamIds.Add(firstTeam.Value.FantasyTeamId);
        }

        for (var i = 1; i < fantasyTeamCount; i++)
        {
            var memberUserId = await SeedUserAsync(db, $"member{i}");
            var membership = LeagueMembership.Join(Guid.NewGuid(), league.LeagueId, memberUserId, _clock.UtcNow);
            db.LeagueMemberships.Add(membership);
            await db.SaveChangesAsync();
            var team = await fantasyTeamService.CreateAsync(membership.LeagueMembershipId, season.SeasonId);
            fantasyTeamIds.Add(team.Value.FantasyTeamId);
        }

        var gameweekIds = new List<Guid>();
        for (var number = 1; number <= gameweekCount; number++)
        {
            var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = number, RosterLockDeadline = _clock.UtcNow.AddDays(number * 7) };
            db.Gameweeks.Add(gameweek);
            gameweekIds.Add(gameweek.GameweekId);
        }

        await db.SaveChangesAsync();

        return (season.SeasonId, fantasyTeamIds, gameweekIds);
    }

    /// <summary>Seeds a decided HeadToHeadMatch (Result/League Points already computed via IT-39/IT-40's own domain methods).</summary>
    private async Task SeedDecidedMatchAsync(Guid seasonId, Guid gameweekId, Guid homeFantasyTeamId, Guid awayFantasyTeamId, int homeFantasyPoints, int awayFantasyPoints)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var match = HeadToHeadMatch.Schedule(Guid.NewGuid(), seasonId, gameweekId, homeFantasyTeamId, awayFantasyTeamId);
        match.CalculateResult(homeFantasyPoints, awayFantasyPoints);
        match.AllocateLeaguePoints(win: 3, draw: 1, loss: 0);
        db.HeadToHeadMatches.Add(match);
        await db.SaveChangesAsync();
    }

    private async Task SeedScheduledMatchAsync(Guid seasonId, Guid gameweekId, Guid homeFantasyTeamId, Guid awayFantasyTeamId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        db.HeadToHeadMatches.Add(HeadToHeadMatch.Schedule(Guid.NewGuid(), seasonId, gameweekId, homeFantasyTeamId, awayFantasyTeamId));
        await db.SaveChangesAsync();
    }

    private async Task SeedGameweekScoreAsync(Guid fantasyTeamId, Guid gameweekId, int captainPoints)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        db.GameweekScores.Add(GameweekScore.Calculate(Guid.NewGuid(), fantasyTeamId, gameweekId, fantasyPoints: 0, captainPoints, fantasyGoalsFor: 0, fantasyGoalsAgainst: 0, fantasyGoalDifference: 0, _clock.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task CalculateAsync(Guid seasonId, Guid asOfGameweekId)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IStandingsCalculationService>().CalculateAsync(seasonId, asOfGameweekId);
    }

    private async Task<List<LeagueStanding>> GetStandingsAsync(Guid seasonId, Guid asOfGameweekId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.LeagueStandings.Where(s => s.SeasonId == seasonId && s.AsOfGameweekId == asOfGameweekId).ToListAsync();
    }

    private async Task<Guid> GetLeagueMembershipIdAsync(Guid fantasyTeamId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return (await db.FantasyTeams.SingleAsync(ft => ft.FantasyTeamId == fantasyTeamId)).LeagueMembershipId;
    }

    private async Task<List<NotificationRequest>> GetWeeklyStandingsNotificationsAsync(Guid leagueMembershipId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.NotificationRequests
            .Where(r => r.LeagueMembershipId == leagueMembershipId && r.EventType == NotificationEventType.WeeklyStandings)
            .ToListAsync();
    }

    [Fact]
    public async Task CalculateAsync_tallies_a_decided_match_into_both_sides_own_totals()
    {
        var (seasonId, teams, gameweeks) = await SeedSeasonAsync(fantasyTeamCount: 2, gameweekCount: 1);
        await SeedDecidedMatchAsync(seasonId, gameweeks[0], teams[0], teams[1], homeFantasyPoints: 60, awayFantasyPoints: 45);

        await CalculateAsync(seasonId, gameweeks[0]);

        var standings = await GetStandingsAsync(seasonId, gameweeks[0]);
        var home = standings.Single(s => s.FantasyTeamId == teams[0]);
        var away = standings.Single(s => s.FantasyTeamId == teams[1]);

        Assert.Equal(1, home.Played);
        Assert.Equal(1, home.Won);
        Assert.Equal(0, home.Drawn);
        Assert.Equal(0, home.Lost);
        Assert.Equal(3, home.LeaguePoints);
        Assert.Equal(60, home.FantasyGoalsFor);
        Assert.Equal(45, home.FantasyGoalsAgainst);
        Assert.Equal(15, home.FantasyGoalDifference);

        Assert.Equal(1, away.Played);
        Assert.Equal(0, away.Won);
        Assert.Equal(0, away.Drawn);
        Assert.Equal(1, away.Lost);
        Assert.Equal(0, away.LeaguePoints);
        Assert.Equal(45, away.FantasyGoalsFor);
        Assert.Equal(60, away.FantasyGoalsAgainst);
        Assert.Equal(-15, away.FantasyGoalDifference);
    }

    [Fact]
    public async Task CalculateAsync_excludes_a_match_that_has_not_yet_been_decided()
    {
        var (seasonId, teams, gameweeks) = await SeedSeasonAsync(fantasyTeamCount: 2, gameweekCount: 1);
        await SeedScheduledMatchAsync(seasonId, gameweeks[0], teams[0], teams[1]); // no Result yet.

        await CalculateAsync(seasonId, gameweeks[0]);

        var standings = await GetStandingsAsync(seasonId, gameweeks[0]);
        Assert.All(standings, s => Assert.Equal(0, s.Played));
    }

    [Fact]
    public async Task CalculateAsync_gives_every_FantasyTeam_a_row_even_with_zero_matches_played()
    {
        var (seasonId, teams, gameweeks) = await SeedSeasonAsync(fantasyTeamCount: 3, gameweekCount: 1);

        await CalculateAsync(seasonId, gameweeks[0]);

        var standings = await GetStandingsAsync(seasonId, gameweeks[0]);
        Assert.Equal(teams.ToHashSet(), standings.Select(s => s.FantasyTeamId).ToHashSet());
        Assert.All(standings, s => Assert.Equal(0, s.Played));
        // Every Position from 1..N is assigned exactly once, even though every team is fully tied
        // (the RandomFallbackTieBreakRule, IT-F10's own deterministic-per-Season hash, separates them).
        Assert.Equal(new[] { 1, 2, 3 }, standings.Select(s => s.Position).OrderBy(p => p));
    }

    [Fact]
    public async Task CalculateAsync_only_includes_matches_up_to_and_including_the_given_Gameweek()
    {
        // BR-217: a snapshot as-of an earlier Gameweek must never see a later Gameweek's results,
        // even though both already exist in the database by the time this runs.
        var (seasonId, teams, gameweeks) = await SeedSeasonAsync(fantasyTeamCount: 2, gameweekCount: 2);
        await SeedDecidedMatchAsync(seasonId, gameweeks[0], teams[0], teams[1], homeFantasyPoints: 60, awayFantasyPoints: 45);
        await SeedDecidedMatchAsync(seasonId, gameweeks[1], teams[1], teams[0], homeFantasyPoints: 80, awayFantasyPoints: 20);

        await CalculateAsync(seasonId, gameweeks[0]);
        await CalculateAsync(seasonId, gameweeks[1]);

        var asOfGameweek1 = await GetStandingsAsync(seasonId, gameweeks[0]);
        var asOfGameweek2 = await GetStandingsAsync(seasonId, gameweeks[1]);

        Assert.All(asOfGameweek1, s => Assert.Equal(1, s.Played)); // only Gameweek 1's own match.
        Assert.All(asOfGameweek2, s => Assert.Equal(2, s.Played)); // both Gameweeks' matches.

        var team0AsOf2 = asOfGameweek2.Single(s => s.FantasyTeamId == teams[0]);
        Assert.Equal(1, team0AsOf2.Won); // GW1 win.
        Assert.Equal(1, team0AsOf2.Lost); // GW2 loss (team0 is away, 20 v 80).
    }

    [Fact]
    public async Task CalculateAsync_accumulates_CaptainPoints_across_scored_Gameweeks_including_a_bye_week()
    {
        // 3 teams — an odd count always leaves exactly one team with no scheduled match some
        // Gameweek (BR-306), yet that team's own Captain Points for that Gameweek still count.
        var (seasonId, teams, gameweeks) = await SeedSeasonAsync(fantasyTeamCount: 3, gameweekCount: 1);
        await SeedDecidedMatchAsync(seasonId, gameweeks[0], teams[0], teams[1], homeFantasyPoints: 60, awayFantasyPoints: 45);
        // teams[2] has a bye this Gameweek — no HeadToHeadMatch row at all — but was still scored.
        await SeedGameweekScoreAsync(teams[0], gameweeks[0], captainPoints: 12);
        await SeedGameweekScoreAsync(teams[1], gameweeks[0], captainPoints: 8);
        await SeedGameweekScoreAsync(teams[2], gameweeks[0], captainPoints: 20);

        await CalculateAsync(seasonId, gameweeks[0]);

        var standings = await GetStandingsAsync(seasonId, gameweeks[0]);
        Assert.Equal(12, standings.Single(s => s.FantasyTeamId == teams[0]).CaptainPointsTotal);
        Assert.Equal(8, standings.Single(s => s.FantasyTeamId == teams[1]).CaptainPointsTotal);
        var bye = standings.Single(s => s.FantasyTeamId == teams[2]);
        Assert.Equal(20, bye.CaptainPointsTotal);
        Assert.Equal(0, bye.Played); // the bye itself contributes nothing to Played.
    }

    [Fact]
    public async Task CalculateAsync_ranks_Position_by_FantasyGoalDifference_when_LeaguePoints_are_tied()
    {
        // 4 teams, 2 decided matches this Gameweek: both winners tie on League Points (3 each), but
        // team0 wins by a bigger margin (BR-216: the full pipeline, not League Points alone, decides Position).
        var (seasonId, teams, gameweeks) = await SeedSeasonAsync(fantasyTeamCount: 4, gameweekCount: 1);
        await SeedDecidedMatchAsync(seasonId, gameweeks[0], teams[0], teams[1], homeFantasyPoints: 80, awayFantasyPoints: 20); // team0 wins by 60.
        await SeedDecidedMatchAsync(seasonId, gameweeks[0], teams[2], teams[3], homeFantasyPoints: 50, awayFantasyPoints: 45); // team2 wins by 5.

        await CalculateAsync(seasonId, gameweeks[0]);

        var standings = await GetStandingsAsync(seasonId, gameweeks[0]);
        var team0 = standings.Single(s => s.FantasyTeamId == teams[0]);
        var team2 = standings.Single(s => s.FantasyTeamId == teams[2]);
        Assert.True(team0.Position < team2.Position); // both have 3 League Points — the bigger Goal Difference ranks ahead.
    }

    [Fact]
    public async Task CalculateAsync_replaces_an_existing_snapshot_rather_than_duplicating_rows()
    {
        var (seasonId, teams, gameweeks) = await SeedSeasonAsync(fantasyTeamCount: 2, gameweekCount: 1);
        await SeedDecidedMatchAsync(seasonId, gameweeks[0], teams[0], teams[1], homeFantasyPoints: 60, awayFantasyPoints: 45);

        await CalculateAsync(seasonId, gameweeks[0]);
        await CalculateAsync(seasonId, gameweeks[0]); // a later recalculation (e.g. after a ScoreOverride cascade).

        var standings = await GetStandingsAsync(seasonId, gameweeks[0]);
        Assert.Equal(2, standings.Count); // one row per FantasyTeam, not one per call.
    }

    [Fact]
    public async Task CalculateAsync_queues_a_WeeklyStandings_notification_per_channel_per_FantasyTeam_carrying_its_own_Position()
    {
        // IT-56 (F-012.3 AC2/BR-154): every FantasyTeam in the snapshot gets one NotificationRequest
        // per NotificationChannel, unconditionally, and its own newly-assigned Position travels in
        // the payload — preference suppression is left entirely to the outbox dispatcher.
        var (seasonId, teams, gameweeks) = await SeedSeasonAsync(fantasyTeamCount: 2, gameweekCount: 1);
        await SeedDecidedMatchAsync(seasonId, gameweeks[0], teams[0], teams[1], homeFantasyPoints: 60, awayFantasyPoints: 45);

        await CalculateAsync(seasonId, gameweeks[0]);

        var standings = await GetStandingsAsync(seasonId, gameweeks[0]);
        var winner = standings.Single(s => s.FantasyTeamId == teams[0]);
        var winnerMembershipId = await GetLeagueMembershipIdAsync(teams[0]);
        var notifications = await GetWeeklyStandingsNotificationsAsync(winnerMembershipId);

        Assert.Equal(2, notifications.Count); // one per NotificationChannel (Email, Sms).
        Assert.All(notifications, n => Assert.Equal(NotificationStatus.Pending, n.Status));
        Assert.Contains(notifications, n => n.Channel == NotificationChannel.Email);
        Assert.Contains(notifications, n => n.Channel == NotificationChannel.Sms);
        Assert.All(notifications, n =>
        {
            using var payload = JsonDocument.Parse(n.PayloadJson);
            Assert.Equal(winner.Position, payload.RootElement.GetProperty("Position").GetInt32());
        });
    }

    [Fact]
    public async Task CalculateAsync_queues_a_fresh_set_of_WeeklyStandings_notifications_on_every_call_since_a_snapshot_is_always_replaced()
    {
        // Unlike GameweekScoreCalculationService, this method has no first-time-only guard of its
        // own (a snapshot is always fully replaced, BR-154's own "when recalculated" wording) — so
        // a second call for the same (Season, AsOfGameweek) queues a second set rather than none.
        var (seasonId, teams, gameweeks) = await SeedSeasonAsync(fantasyTeamCount: 2, gameweekCount: 1);
        await SeedDecidedMatchAsync(seasonId, gameweeks[0], teams[0], teams[1], homeFantasyPoints: 60, awayFantasyPoints: 45);

        await CalculateAsync(seasonId, gameweeks[0]);
        await CalculateAsync(seasonId, gameweeks[0]);

        var membershipId = await GetLeagueMembershipIdAsync(teams[0]);
        Assert.Equal(4, (await GetWeeklyStandingsNotificationsAsync(membershipId)).Count); // 2 calls x 2 channels.
    }

    [Fact]
    public async Task A_queued_WeeklyStandings_notification_is_suppressed_by_the_outbox_dispatcher_for_the_default_disabled_preference()
    {
        // The task's own required test (matching IT-55's identical proof): BR-226/IT-53 seed every
        // (EventType, Channel) row disabled by default, and this service never checks preferences
        // itself — suppression is entirely the already-built outbox dispatcher's (IT-F12) job.
        var (seasonId, teams, gameweeks) = await SeedSeasonAsync(fantasyTeamCount: 2, gameweekCount: 1);
        await SeedDecidedMatchAsync(seasonId, gameweeks[0], teams[0], teams[1], homeFantasyPoints: 60, awayFantasyPoints: 45);

        await CalculateAsync(seasonId, gameweeks[0]);

        var dispatcher = new NotificationOutboxBackgroundService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new NotificationOutboxOptions { BaseBackoff = TimeSpan.FromMinutes(1), MaxBackoff = TimeSpan.FromHours(1), MaxAttempts = 3 }),
            NullLogger<NotificationOutboxBackgroundService>.Instance);
        await dispatcher.RunOneDispatchPassAsync(CancellationToken.None);

        var membershipId = await GetLeagueMembershipIdAsync(teams[0]);
        var notifications = await GetWeeklyStandingsNotificationsAsync(membershipId);
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, n => Assert.Equal(NotificationStatus.Suppressed, n.Status));
    }
}
