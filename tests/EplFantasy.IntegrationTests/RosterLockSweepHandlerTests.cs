using EplFantasy.Competition;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using EplFantasy.Rosters;
using EplFantasy.SharedKernel;
using EplFantasy.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves IT-31's RosterLockSweepHandler (F-007.3, BR-093-BR-096/BR-305) against real Postgres: a
/// Submitted roster past its Gameweek's RosterLockDeadline locks (AC3); a FantasyTeam that never
/// submits at all gets its last Locked roster (including Captain) carried forward and locked
/// (AC6), minus any player it no longer owns (AC8); a FantasyTeam with no prior Locked roster to
/// carry forward locks empty (AC7); and a second sweep pass changes nothing further (the
/// idempotency the ADR-012 background loop's own repeated ticking relies on).
/// </summary>
public class RosterLockSweepHandlerTests : IAsyncLifetime
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
        services.AddScoped<IDeadlineSweepHandler, RosterLockSweepHandler>(); // IT-31's sweep handler — registered the same way Program.cs registers it.
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
    /// since this suite cares about lock/carry-forward timing, not roster composition.
    /// </summary>
    private async Task<(Guid FantasyTeamId, Guid SeasonId, string EplSeasonIdentifier)> SeedFantasyTeamAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var owner = new User { UserId = Guid.NewGuid(), Username = $"owner{suffix}", Email = $"owner{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(owner.UserId, $"Sweep League {suffix}", null);

        var eplSeasonIdentifier = $"sweep-season-{suffix}";
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
        await db.SaveChangesAsync();

        return (fantasyTeamId, season.SeasonId, eplSeasonIdentifier);
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
        db.Players.Add(new Player { PlayerId = playerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = "Sweep Forward", Position = PlayerPosition.Fwd });
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

    private async Task ReleaseSquadPlayerAsync(Guid playerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var squadPlayer = await db.SquadPlayers.SingleAsync(sp => sp.PlayerId == playerId);
        squadPlayer.IsCurrentlyOwned = false;
        squadPlayer.ReleasedAt = _clock.UtcNow;
        await db.SaveChangesAsync();
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

    private async Task<GameweekRoster> LoadRosterAsync(Guid fantasyTeamId, Guid gameweekId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.GameweekRosters.Include(r => r.Players)
            .SingleAsync(r => r.FantasyTeamId == fantasyTeamId && r.GameweekId == gameweekId);
    }

    [Fact]
    public async Task Sweep_locks_a_Submitted_roster_once_the_Gameweeks_deadline_passes()
    {
        var (fantasyTeamId, seasonId, eplSeasonIdentifier) = await SeedFantasyTeamAsync();
        var playerId = await SeedOwnedSquadPlayerAsync(fantasyTeamId, seasonId);
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        var gameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 1, _clock.UtcNow.AddHours(2));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var rosterService = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await rosterService.SubmitAsync(fantasyTeamId, gameweekId, [playerId], playerId, ifMatchXmin: null);
        }

        _clock.AdvanceBy(TimeSpan.FromHours(3)); // past the deadline.
        await RunSweepAsync();

        var roster = await LoadRosterAsync(fantasyTeamId, gameweekId);
        Assert.Equal(RosterStatus.Locked, roster.Status);
        Assert.Equal(_clock.UtcNow, roster.LockedAt);
        Assert.Equal(playerId, roster.CaptainPlayerId);
        Assert.False(roster.IsCarriedForward);
    }

    [Fact]
    public async Task Sweep_carries_forward_the_last_Locked_roster_including_Captain_for_a_FantasyTeam_that_never_submits()
    {
        var (fantasyTeamId, seasonId, eplSeasonIdentifier) = await SeedFantasyTeamAsync();
        var playerId = await SeedOwnedSquadPlayerAsync(fantasyTeamId, seasonId);
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        var firstGameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 1, _clock.UtcNow.AddHours(2));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var rosterService = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await rosterService.SubmitAsync(fantasyTeamId, firstGameweekId, [playerId], playerId, ifMatchXmin: null);
        }

        _clock.AdvanceBy(TimeSpan.FromHours(3));
        await RunSweepAsync(); // locks Gameweek 1's Submitted roster.

        var secondGameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 2, _clock.UtcNow.AddDays(7));
        _clock.AdvanceBy(TimeSpan.FromDays(8)); // Gameweek 2's deadline passes with nothing ever submitted.
        await RunSweepAsync();

        var carriedForward = await LoadRosterAsync(fantasyTeamId, secondGameweekId);
        Assert.Equal(RosterStatus.Locked, carriedForward.Status);
        Assert.True(carriedForward.IsCarriedForward);
        Assert.Equal(_clock.UtcNow, carriedForward.LockedAt);
        Assert.Equal(playerId, carriedForward.CaptainPlayerId);
        Assert.Equal([playerId], carriedForward.Players.Select(p => p.PlayerId));
        Assert.True(carriedForward.Players.Single().IsCaptain);
    }

    [Fact]
    public async Task Sweep_drops_a_carried_forward_player_that_is_no_longer_owned()
    {
        var (fantasyTeamId, seasonId, eplSeasonIdentifier) = await SeedFantasyTeamAsync();
        var playerId = await SeedOwnedSquadPlayerAsync(fantasyTeamId, seasonId);
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        var firstGameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 1, _clock.UtcNow.AddHours(2));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var rosterService = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await rosterService.SubmitAsync(fantasyTeamId, firstGameweekId, [playerId], playerId, ifMatchXmin: null);
        }

        _clock.AdvanceBy(TimeSpan.FromHours(3));
        await RunSweepAsync(); // locks Gameweek 1's Submitted roster.

        await ReleaseSquadPlayerAsync(playerId); // no longer a currently-owned SquadPlayer (e.g. an EPL exit).

        var secondGameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 2, _clock.UtcNow.AddDays(7));
        _clock.AdvanceBy(TimeSpan.FromDays(8));
        await RunSweepAsync();

        // AC8: the roster still locks — even though it now falls short of WeeklyRosterSize/
        // PositionalMinimums — rather than being rejected; that invariant is for user submissions
        // only, never for an automatic continuation.
        var carriedForward = await LoadRosterAsync(fantasyTeamId, secondGameweekId);
        Assert.Equal(RosterStatus.Locked, carriedForward.Status);
        Assert.True(carriedForward.IsCarriedForward);
        Assert.Empty(carriedForward.Players);
        Assert.Null(carriedForward.CaptainPlayerId);
    }

    [Fact]
    public async Task Sweep_locks_empty_when_a_FantasyTeams_first_Gameweek_deadline_passes_with_no_prior_roster()
    {
        var (fantasyTeamId, _, eplSeasonIdentifier) = await SeedFantasyTeamAsync();
        var gameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 1, _clock.UtcNow.AddHours(2));

        _clock.AdvanceBy(TimeSpan.FromHours(3));
        await RunSweepAsync();

        var roster = await LoadRosterAsync(fantasyTeamId, gameweekId);
        Assert.Equal(RosterStatus.Locked, roster.Status);
        Assert.True(roster.IsCarriedForward);
        Assert.Empty(roster.Players);
        Assert.Null(roster.CaptainPlayerId);
    }

    [Fact]
    public async Task Sweep_is_idempotent_across_repeated_runs()
    {
        var (fantasyTeamId, seasonId, eplSeasonIdentifier) = await SeedFantasyTeamAsync();
        var playerId = await SeedOwnedSquadPlayerAsync(fantasyTeamId, seasonId);
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        var gameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 1, _clock.UtcNow.AddHours(2));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var rosterService = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await rosterService.SubmitAsync(fantasyTeamId, gameweekId, [playerId], playerId, ifMatchXmin: null);
        }

        _clock.AdvanceBy(TimeSpan.FromHours(3));
        await RunSweepAsync();
        var afterFirstSweep = await LoadRosterAsync(fantasyTeamId, gameweekId);

        await RunSweepAsync(); // a second tick over the same already-Locked roster.

        await using var scope2 = _provider.CreateAsyncScope();
        var db = scope2.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var rosterCount = await db.GameweekRosters.CountAsync(r => r.FantasyTeamId == fantasyTeamId && r.GameweekId == gameweekId);
        Assert.Equal(1, rosterCount); // no duplicate carry-forward row was created for an already-locked Gameweek.

        var afterSecondSweep = await LoadRosterAsync(fantasyTeamId, gameweekId);
        Assert.Equal(afterFirstSweep.LockedAt, afterSecondSweep.LockedAt); // untouched, not re-locked.
    }
}
