using EplFantasy.Administration;
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
/// Proves IT-29's RosterService.SubmitAsync against real Postgres: BR-279's size/positional-
/// minimum invariants (delegated to GameweekRoster.Submit, unit-tested directly elsewhere), BR-194's
/// currently-owned-SquadPlayer ownership check, BR-299's SeasonGoalPrediction precondition on a
/// FantasyTeam's first-ever submission, PUT-style upsert/resubmission semantics, and Architecture
/// §8.3's If-Match concurrency check.
/// </summary>
public class RosterServiceTests : IAsyncLifetime
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

    /// <summary>
    /// Builds a real League/Season/FantasyTeam (via the actual application services), a Gameweek
    /// for that Season, and exactly 4 currently-owned SquadPlayers (one per BR-279 position
    /// category) — WeeklyRosterSize/PositionalMinimums are shrunk to 4/(1,1,1,1) on the Season's
    /// own configuration, the same "override to a small deterministic size" precedent
    /// DraftServiceTests' single-round-Draft test already established, so a test roster is four
    /// players rather than fifteen.
    /// </summary>
    private async Task<(Guid FantasyTeamId, Guid SeasonId, Guid GameweekId, List<Guid> OwnedPlayerIds)> SeedRosterPrerequisitesAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var owner = new User { UserId = Guid.NewGuid(), Username = $"owner{suffix}", Email = $"owner{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(owner.UserId, $"Roster League {suffix}", null);

        var eplSeasonIdentifier = $"roster-season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;

        var fantasyTeamResult = await scope.ServiceProvider.GetRequiredService<IFantasyTeamService>()
            .CreateAsync(league.CreatedByMembershipId, season.SeasonId);
        var fantasyTeamId = fantasyTeamResult.Value.FantasyTeamId;

        var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == season.SeasonId);
        seasonConfiguration.WeeklyRosterSize = 4;
        seasonConfiguration.PositionalMinimumGk = 1;
        seasonConfiguration.PositionalMinimumDef = 1;
        seasonConfiguration.PositionalMinimumMid = 1;
        seasonConfiguration.PositionalMinimumFwd = 1;
        await db.SaveChangesAsync();

        var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = 1, RosterLockDeadline = _clock.UtcNow.AddDays(7) };
        db.Gameweeks.Add(gameweek);

        var ownedPlayerIds = new List<Guid>();
        foreach (var position in new[] { PlayerPosition.Gk, PlayerPosition.Def, PlayerPosition.Mid, PlayerPosition.Fwd })
        {
            var playerId = Guid.NewGuid();
            db.Players.Add(new Player { PlayerId = playerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = $"{position} Player", Position = position });
            db.SquadPlayers.Add(new SquadPlayer
            {
                SquadPlayerId = Guid.NewGuid(),
                FantasyTeamId = fantasyTeamId,
                PlayerId = playerId,
                SeasonId = season.SeasonId,
                AcquisitionType = AcquisitionType.InitialDraft,
                AcquiredAt = _clock.UtcNow,
                IsCurrentlyOwned = true,
            });
            ownedPlayerIds.Add(playerId);
        }

        await db.SaveChangesAsync();

        return (fantasyTeamId, season.SeasonId, gameweek.GameweekId, ownedPlayerIds);
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

    [Fact]
    public async Task SubmitAsync_creates_a_new_roster_and_transitions_it_to_Submitted()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IRosterService>();

        var roster = await service.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, ownedPlayerIds[0], ifMatchXmin: null);

        Assert.Equal(RosterStatus.Submitted, roster.Status);
        Assert.Equal(ownedPlayerIds[0], roster.CaptainPlayerId);
        Assert.Equal(_clock.UtcNow, roster.SubmittedAt);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.GameweekRosters.Include(r => r.Players).SingleAsync(r => r.GameweekRosterId == roster.GameweekRosterId);
        Assert.Equal(4, persisted.Players.Count);
    }

    [Fact]
    public async Task SubmitAsync_rejects_the_first_ever_submission_of_the_Season_without_a_SeasonGoalPrediction()
    {
        var (fantasyTeamId, _, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IRosterService>();

        await Assert.ThrowsAsync<SeasonGoalPredictionRequiredException>(
            () => service.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, null, ifMatchXmin: null));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.False(await verifyDb.GameweekRosters.AnyAsync(r => r.FantasyTeamId == fantasyTeamId));
    }

    [Fact]
    public async Task SubmitAsync_rejects_a_player_that_is_not_a_currently_owned_SquadPlayer()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        var notOwnedPlayerId = Guid.NewGuid();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.Players.Add(new Player { PlayerId = notOwnedPlayerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = "Not Owned", Position = PlayerPosition.Fwd });
            await db.SaveChangesAsync();
        }
        var submission = ownedPlayerIds.Take(3).Append(notOwnedPlayerId).ToList();

        await using var scope2 = _provider.CreateAsyncScope();
        var service = scope2.ServiceProvider.GetRequiredService<IRosterService>();

        await Assert.ThrowsAsync<InvalidRosterCompositionException>(
            () => service.SubmitAsync(fantasyTeamId, gameweekId, submission, null, ifMatchXmin: null));
    }

    [Fact]
    public async Task SubmitAsync_on_resubmission_replaces_the_prior_players()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        var extraPlayerId = Guid.NewGuid();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.Players.Add(new Player { PlayerId = extraPlayerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = "Extra Fwd", Position = PlayerPosition.Fwd });
            db.SquadPlayers.Add(new SquadPlayer
            {
                SquadPlayerId = Guid.NewGuid(),
                FantasyTeamId = fantasyTeamId,
                PlayerId = extraPlayerId,
                SeasonId = seasonId,
                AcquisitionType = AcquisitionType.InitialDraft,
                AcquiredAt = _clock.UtcNow,
                IsCurrentlyOwned = true,
            });
            await db.SaveChangesAsync();
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await service.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, null, ifMatchXmin: null);
        }

        // Swap the original Forward (ownedPlayerIds[3]) out for the extra Forward — a like-for-like
        // swap, so PositionalMinimums stay satisfied and this resubmission isolates the *replace*
        // behavior rather than a rejection.
        var resubmission = new List<Guid> { ownedPlayerIds[0], ownedPlayerIds[1], ownedPlayerIds[2], extraPlayerId };

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IRosterService>();
        var roster = await service2.SubmitAsync(fantasyTeamId, gameweekId, resubmission, null, ifMatchXmin: null);

        Assert.Equal(resubmission.ToHashSet(), roster.Players.Select(p => p.PlayerId).ToHashSet());
        Assert.DoesNotContain(roster.Players, p => p.PlayerId == ownedPlayerIds[3]);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.GameweekRosters.Include(r => r.Players).SingleAsync(r => r.GameweekRosterId == roster.GameweekRosterId);
        Assert.Equal(4, persisted.Players.Count);
    }

    [Fact]
    public async Task SubmitAsync_rejects_a_stale_If_Match()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await service.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, null, ifMatchXmin: null);
        }

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IRosterService>();

        await Assert.ThrowsAsync<RosterConcurrencyConflictException>(
            () => service2.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, null, ifMatchXmin: 1u));
    }

    [Fact]
    public async Task SubmitAsync_succeeds_with_a_matching_If_Match()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        Guid rosterId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IRosterService>();
            var roster = await service.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, null, ifMatchXmin: null);
            rosterId = roster.GameweekRosterId;
        }

        await using var readScope = _provider.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var loaded = await readDb.GameweekRosters.SingleAsync(r => r.GameweekRosterId == rosterId);
        var currentXmin = (uint)readDb.Entry(loaded).Property("xmin").CurrentValue!;

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IRosterService>();
        var updated = await service2.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, ownedPlayerIds[0], ifMatchXmin: currentXmin);

        Assert.Equal(ownedPlayerIds[0], updated.CaptainPlayerId);
    }

    [Fact]
    public async Task SetCaptainAsync_designates_the_captain_on_an_already_submitted_roster()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await service.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, null, ifMatchXmin: null);
        }

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IRosterService>();
        var roster = await service2.SetCaptainAsync(fantasyTeamId, gameweekId, ownedPlayerIds[1]);

        Assert.Equal(ownedPlayerIds[1], roster.CaptainPlayerId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.GameweekRosters.Include(r => r.Players).SingleAsync(r => r.GameweekRosterId == roster.GameweekRosterId);
        Assert.True(persisted.Players.Single(p => p.PlayerId == ownedPlayerIds[1]).IsCaptain);
    }

    [Fact]
    public async Task SetCaptainAsync_reassigns_the_Captain_away_from_an_already_designated_one()
    {
        // Regression: ux_roster_players_one_captain (V007) is a plain, non-deferrable partial
        // unique index. Reassigning from one already-true Captain row to a different row risks the
        // two underlying UPDATEs landing in whichever order EF's change tracker happens to pick —
        // this is exactly the AC3 "I change my Captain selection" flow, not just the
        // no-prior-Captain case the other SetCaptainAsync tests exercise.
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await service.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, ownedPlayerIds[0], ifMatchXmin: null);
        }

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IRosterService>();
        var roster = await service2.SetCaptainAsync(fantasyTeamId, gameweekId, ownedPlayerIds[2]);

        Assert.Equal(ownedPlayerIds[2], roster.CaptainPlayerId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.GameweekRosters.Include(r => r.Players).SingleAsync(r => r.GameweekRosterId == roster.GameweekRosterId);
        Assert.True(persisted.Players.Single(p => p.PlayerId == ownedPlayerIds[2]).IsCaptain);
        Assert.False(persisted.Players.Single(p => p.PlayerId == ownedPlayerIds[0]).IsCaptain);
        Assert.Single(persisted.Players, p => p.IsCaptain);
    }

    [Fact]
    public async Task SetCaptainAsync_rejects_a_player_not_on_the_roster()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await service.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, null, ifMatchXmin: null);
        }

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IRosterService>();

        await Assert.ThrowsAsync<InvalidRosterCompositionException>(
            () => service2.SetCaptainAsync(fantasyTeamId, gameweekId, Guid.NewGuid()));
    }

    [Fact]
    public async Task SetCaptainAsync_rejects_when_no_roster_exists_yet()
    {
        var (fantasyTeamId, _, gameweekId, _) = await SeedRosterPrerequisitesAsync();
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IRosterService>();

        await Assert.ThrowsAsync<InvalidRosterCompositionException>(
            () => service.SetCaptainAsync(fantasyTeamId, gameweekId, Guid.NewGuid()));
    }

    /// <summary>SeedRosterPrerequisitesAsync's single seeded FantasyTeam is owned by the same User who created the League — its founding League Administrator (IT-03) — so this just resolves that same User back via the FantasyTeam's own LeagueMembership.</summary>
    private async Task<Guid> GetLeagueAdministratorUserIdAsync(Guid fantasyTeamId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await (
            from team in db.FantasyTeams
            join membership in db.LeagueMemberships on team.LeagueMembershipId equals membership.LeagueMembershipId
            where team.FantasyTeamId == fantasyTeamId
            select membership.UserId
        ).SingleAsync();
    }

    /// <summary>Simulates IT-31's RosterLockSweepHandler having already locked this roster (that handler is proven separately, by RosterLockSweepHandlerTests) — this suite only needs a Locked roster to correct, not the sweep itself.</summary>
    private async Task<Guid> LockRosterAsync(Guid fantasyTeamId, Guid gameweekId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var roster = await db.GameweekRosters.SingleAsync(r => r.FantasyTeamId == fantasyTeamId && r.GameweekId == gameweekId);
        roster.Lock(_clock.UtcNow);
        await db.SaveChangesAsync();
        return roster.GameweekRosterId;
    }

    [Fact]
    public async Task CorrectAsync_replaces_players_and_Captain_and_writes_an_AdministrativeAction()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        var extraPlayerId = Guid.NewGuid();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.Players.Add(new Player { PlayerId = extraPlayerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = "Extra Fwd", Position = PlayerPosition.Fwd });
            db.SquadPlayers.Add(new SquadPlayer
            {
                SquadPlayerId = Guid.NewGuid(),
                FantasyTeamId = fantasyTeamId,
                PlayerId = extraPlayerId,
                SeasonId = seasonId,
                AcquisitionType = AcquisitionType.InitialDraft,
                AcquiredAt = _clock.UtcNow,
                IsCurrentlyOwned = true,
            });
            await db.SaveChangesAsync();
        }
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await service.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, ownedPlayerIds[0], ifMatchXmin: null);
        }
        var gameweekRosterId = await LockRosterAsync(fantasyTeamId, gameweekId);
        var adminUserId = await GetLeagueAdministratorUserIdAsync(fantasyTeamId);

        // Swap the original Forward (ownedPlayerIds[3]) for the extra Forward, and re-designate it Captain.
        var correctedPlayerIds = new List<Guid> { ownedPlayerIds[0], ownedPlayerIds[1], ownedPlayerIds[2], extraPlayerId };

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IRosterService>();
        var corrected = await service2.CorrectAsync(gameweekRosterId, correctedPlayerIds, extraPlayerId, "Wrong Forward selected", adminUserId);

        Assert.Equal(RosterStatus.Locked, corrected.Status); // untouched by the correction itself (BR-098).
        Assert.Equal(correctedPlayerIds.ToHashSet(), corrected.Players.Select(p => p.PlayerId).ToHashSet());
        Assert.DoesNotContain(corrected.Players, p => p.PlayerId == ownedPlayerIds[3]);
        Assert.Equal(extraPlayerId, corrected.CaptainPlayerId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var action = await verifyDb.AdministrativeActions.SingleAsync(a => a.TargetEntityId == gameweekRosterId);
        Assert.Equal(AdminActionType.RosterCorrection, action.ActionType);
        Assert.Equal("GameweekRoster", action.TargetEntityType);
        Assert.Equal("Wrong Forward selected", action.Reason);
        Assert.Contains(ownedPlayerIds[3].ToString(), action.BeforeStateJson);
        Assert.Contains(extraPlayerId.ToString(), action.AfterStateJson);
    }

    [Fact]
    public async Task CorrectAsync_with_null_PlayerIds_only_corrects_the_Captain()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await service.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, ownedPlayerIds[0], ifMatchXmin: null);
        }
        var gameweekRosterId = await LockRosterAsync(fantasyTeamId, gameweekId);
        var adminUserId = await GetLeagueAdministratorUserIdAsync(fantasyTeamId);

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IRosterService>();
        var corrected = await service2.CorrectAsync(gameweekRosterId, playerIds: null, ownedPlayerIds[2], "Captain mistake", adminUserId);

        Assert.Equal(ownedPlayerIds.ToHashSet(), corrected.Players.Select(p => p.PlayerId).ToHashSet()); // players untouched.
        Assert.Equal(ownedPlayerIds[2], corrected.CaptainPlayerId);
    }

    [Fact]
    public async Task CorrectAsync_rejects_a_non_Administrator()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await service.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, ownedPlayerIds[0], ifMatchXmin: null);
        }
        var gameweekRosterId = await LockRosterAsync(fantasyTeamId, gameweekId);

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IRosterService>();

        // RosterLeagueAdministratorAuthorizationHandler already prevents a non-Administrator from
        // reaching this service call in the first place (BR-162) — the same precedent
        // DraftServiceTests.ExtendTimerAsync_rejects_a_non_Administrator established — this proves
        // the service itself has nothing to grant even if that gate were somehow bypassed.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service2.CorrectAsync(gameweekRosterId, playerIds: null, ownedPlayerIds[0], "reason", Guid.NewGuid()));
    }

    [Fact]
    public async Task CorrectAsync_rejects_a_roster_that_is_still_Submitted()
    {
        var (fantasyTeamId, seasonId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync();
        await SeedSeasonGoalPredictionAsync(seasonId, fantasyTeamId);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IRosterService>();
            await service.SubmitAsync(fantasyTeamId, gameweekId, ownedPlayerIds, ownedPlayerIds[0], ifMatchXmin: null);
        }
        Guid gameweekRosterId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            gameweekRosterId = await db.GameweekRosters.Where(r => r.FantasyTeamId == fantasyTeamId && r.GameweekId == gameweekId).Select(r => r.GameweekRosterId).SingleAsync();
        }
        var adminUserId = await GetLeagueAdministratorUserIdAsync(fantasyTeamId);

        await using var scope2 = _provider.CreateAsyncScope();
        var service2 = scope2.ServiceProvider.GetRequiredService<IRosterService>();

        await Assert.ThrowsAsync<GameweekRosterNotLockedException>(
            () => service2.CorrectAsync(gameweekRosterId, playerIds: null, ownedPlayerIds[1], "too early", adminUserId));
    }
}
