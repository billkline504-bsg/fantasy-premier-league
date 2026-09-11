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
/// Proves IT-50 (F-013.1, BR-174/BR-176/BR-217/BR-257/BR-296) against real Postgres: a completed
/// Season's own HeadToHeadMatch/LeagueStanding/SquadPlayer/SeasonConfiguration rows remain
/// queryable byte-for-byte after (a) the owning User renames themselves, (b) the League's own
/// default configuration changes, and (c) an entirely new Season is created for the same League —
/// none of which retroactively touches the completed Season's own already-persisted data. This
/// task's own Domain bullet is "none new" — the schema already retains everything permanently, so
/// there's no purge/archival code to write; the only thing to prove is that nothing *does* purge
/// it. `listSeasons?status=completed` (SeasonControllerTests) and getStandings' own historical
/// `asOfGameweekId` (CompetitionControllerTests) already separately prove the read-side of this —
/// this suite proves the underlying data survives everything that could disturb it.
/// </summary>
public class HistoricalSeasonArchiveTests : IAsyncLifetime
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

    [Fact]
    public async Task A_completed_Seasons_own_data_survives_a_username_change_a_League_default_change_and_a_new_Season()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var ownerId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        db.Users.Add(new User { UserId = ownerId, Username = $"owner{suffix}", Email = $"owner{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow });
        db.UsernameHistories.Add(UsernameHistory.Open(Guid.NewGuid(), ownerId, $"owner{suffix}", _clock.UtcNow)); // ChangeUsernameAsync's own precondition — a currently-open row to close.
        await db.SaveChangesAsync();

        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(ownerId, $"Archive League {suffix}", null);

        var eplSeasonIdentifier = $"archive-season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
        var season1 = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;

        var fantasyTeamService = scope.ServiceProvider.GetRequiredService<IFantasyTeamService>();
        var homeTeam = (await fantasyTeamService.CreateAsync(league.CreatedByMembershipId, season1.SeasonId)).Value;

        var memberId = Guid.NewGuid();
        db.Users.Add(new User { UserId = memberId, Username = $"member{suffix}", Email = $"member{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow });
        var membership = LeagueMembership.Join(Guid.NewGuid(), league.LeagueId, memberId, _clock.UtcNow);
        db.LeagueMemberships.Add(membership);
        await db.SaveChangesAsync();
        var awayTeam = (await fantasyTeamService.CreateAsync(membership.LeagueMembershipId, season1.SeasonId)).Value;

        var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = 1, RosterLockDeadline = _clock.UtcNow.AddDays(7) };
        db.Gameweeks.Add(gameweek);
        await db.SaveChangesAsync();

        var match = HeadToHeadMatch.Schedule(Guid.NewGuid(), season1.SeasonId, gameweek.GameweekId, homeTeam.FantasyTeamId, awayTeam.FantasyTeamId);
        match.CalculateResult(homeFantasyPoints: 60, awayFantasyPoints: 45);
        match.AllocateLeaguePoints(win: 3, draw: 1, loss: 0);
        db.HeadToHeadMatches.Add(match);

        var standing = LeagueStanding.Calculate(
            season1.SeasonId, homeTeam.FantasyTeamId, gameweek.GameweekId,
            leaguePoints: 3, played: 1, won: 1, drawn: 0, lost: 0,
            fantasyGoalsFor: 60, fantasyGoalsAgainst: 45, fantasyGoalDifference: 15, captainPointsTotal: 20);
        standing.AssignPosition(1);
        db.LeagueStandings.Add(standing);

        var playerId = Guid.NewGuid();
        db.Players.Add(new Player { PlayerId = playerId, EplPlayerId = $"p{suffix}", Name = "Historical Player", Position = PlayerPosition.Mid });
        var squadPlayer = new SquadPlayer
        {
            SquadPlayerId = Guid.NewGuid(),
            FantasyTeamId = homeTeam.FantasyTeamId,
            PlayerId = playerId,
            SeasonId = season1.SeasonId,
            AcquisitionType = AcquisitionType.InitialDraft,
            AcquiredAt = _clock.UtcNow,
            IsCurrentlyOwned = true,
        };
        db.SquadPlayers.Add(squadPlayer);
        await db.SaveChangesAsync();

        // Season 1 is now "complete" — no lifecycle service transitions this yet (out of this
        // task's own scope, which is about data retention, not the completion workflow itself).
        var trackedSeason1 = await db.Seasons.SingleAsync(s => s.SeasonId == season1.SeasonId);
        trackedSeason1.Status = SeasonStatus.Completed;
        await db.SaveChangesAsync();

        var originalSeasonConfiguration = await db.SeasonConfigurations.AsNoTracking().SingleAsync(sc => sc.SeasonId == season1.SeasonId);

        // --- Everything below is something that could, in principle, disturb Season 1's own history. ---

        // (a) BR-176/BR-257: the owning User renames themselves.
        var renameResult = await scope.ServiceProvider.GetRequiredService<IUsernameService>().ChangeUsernameAsync(ownerId, $"renamed{suffix}");
        Assert.True(renameResult.IsSuccess);

        // (b) BR-296: the League's own configured defaults change.
        await scope.ServiceProvider.GetRequiredService<IConfigurationService>().UpdateLeagueConfigurationAsync(
            league.LeagueId,
            league.CreatedByMembershipId,
            new ConfigurationValues(
                InitialSquadSize: 30, WeeklyRosterSize: 16, PositionalMinimumGk: 2, PositionalMinimumDef: 4,
                PositionalMinimumMid: 3, PositionalMinimumFwd: 2, DraftTimerSecondsInitial: 400,
                DraftTimerSecondsSecondary: 400, DraftTimerSecondsReplacement: 400, SecondaryDraftSelectionsPerTeam: 6,
                SecondaryDraftSchedulingOffsetDays: 2, GameweekRosterLockOffsetBeforeKickoffMinutes: 90,
                LeaguePointsWin: 4, LeaguePointsDraw: 2, LeaguePointsLoss: 1, InvitationExpirationDays: 10,
                ReplacementSelectionCap: 5, GameweekReminderLeadTimeHours: 48, TieBreakRulesetVersion: "v2"));

        // (c) An entirely new Season starts for the very same League.
        var eplSeasonIdentifier2 = $"archive-season-2-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier2 });
        await db.SaveChangesAsync();
        var season2 = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier2, new DateOnly(2027, 8, 15))).Value;
        Assert.NotEqual(season1.SeasonId, season2.SeasonId);

        // --- Season 1's own data must be exactly what it was before any of the above. ---

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var matchAfter = await verifyDb.HeadToHeadMatches.AsNoTracking().SingleAsync(m => m.SeasonId == season1.SeasonId);
        Assert.Equal(MatchResult.HomeWin, matchAfter.Result);
        Assert.Equal(60, matchAfter.HomeScore);
        Assert.Equal(45, matchAfter.AwayScore);
        Assert.Equal(3, matchAfter.LeaguePointsHome);

        var standingAfter = await verifyDb.LeagueStandings.AsNoTracking()
            .SingleAsync(s => s.SeasonId == season1.SeasonId && s.FantasyTeamId == homeTeam.FantasyTeamId);
        Assert.Equal(3, standingAfter.LeaguePoints);
        Assert.Equal(1, standingAfter.Position);
        Assert.Equal(20, standingAfter.CaptainPointsTotal);

        var squadPlayerAfter = await verifyDb.SquadPlayers.AsNoTracking().SingleAsync(sp => sp.SquadPlayerId == squadPlayer.SquadPlayerId);
        Assert.Equal(playerId, squadPlayerAfter.PlayerId);
        Assert.True(squadPlayerAfter.IsCurrentlyOwned);

        // BR-296: Season 1's own already-locked configuration values are untouched by the League's
        // later default change — still exactly what CopyFrom snapshotted at Season 1's own creation.
        var seasonConfigurationAfter = await verifyDb.SeasonConfigurations.AsNoTracking().SingleAsync(sc => sc.SeasonId == season1.SeasonId);
        Assert.Equal(originalSeasonConfiguration.InitialSquadSize, seasonConfigurationAfter.InitialSquadSize);
        Assert.Equal(originalSeasonConfiguration.LeaguePointsWin, seasonConfigurationAfter.LeaguePointsWin);
        Assert.NotEqual(30, seasonConfigurationAfter.InitialSquadSize); // sanity: genuinely different from the League's new default.

        // Season 1's own Status is still exactly Completed — a new Season starting never reopens it.
        var season1After = await verifyDb.Seasons.AsNoTracking().SingleAsync(s => s.SeasonId == season1.SeasonId);
        Assert.Equal(SeasonStatus.Completed, season1After.Status);
    }
}
