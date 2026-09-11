using EplFantasy.Administration;
using EplFantasy.Competition;
using EplFantasy.Drafts;
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
/// Proves IT-23's Initial Draft creation against real Postgres: BR-053–BR-055's InProgress
/// creation with a randomized order, BR-291/BR-293's InitialSquadSize lock, BR-302's two-team
/// minimum, and the Season.Status=Setup precondition (AC1) that keeps a second creation attempt
/// from silently duplicating the Draft. Also proves IT-24's pick flow: the atomic
/// DraftSelection+SquadPlayer insert, turn/ownership rejection, Draft completion, and — the
/// highest-risk claim in the whole task (AP-009/AP-010, db-tests/run_concurrency_test.sh's own
/// reproduction requirement) — that two real, concurrent DbContext-backed picks for the same
/// Player can never both succeed. Also proves IT-26's ExtendTimerAsync: the extension applies and
/// a DraftTimerExtended AdministrativeAction is recorded in the same transaction (BR-058, BR-295-style audit).
/// Also proves IT-27's DraftPickTimeoutSweepHandler (F-005.4, BR-282): a real deliberately-expired
/// pick is skipped (no DraftSelection recorded), queued as a makeup pick, and the Draft still
/// reaches Completed once the makeup pick resolves.
/// </summary>
public class DraftServiceTests : IAsyncLifetime
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
        services.AddScoped<IDeadlineSweepHandler, DraftPickTimeoutSweepHandler>(); // IT-27's sweep handler — registered the same way Program.cs registers it.
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

    /// <summary>Builds a real League/Season (via the actual application services, the same precedent FantasyTeamServiceTests established) with `fantasyTeamCount` Active FantasyTeams, each owned by a distinct User/LeagueMembership.</summary>
    private async Task<(Guid LeagueId, Guid SeasonId)> SeedSeasonWithFantasyTeamsAsync(int fantasyTeamCount)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var ownerId = await SeedUserAsync(db, "owner");
        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(ownerId, $"Draft League {suffix}", null);

        var eplSeasonIdentifier = $"draft-season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>()
            .CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;

        var fantasyTeamService = scope.ServiceProvider.GetRequiredService<IFantasyTeamService>();
        Assert.True((await fantasyTeamService.CreateAsync(league.CreatedByMembershipId, season.SeasonId)).IsSuccess);

        for (var i = 1; i < fantasyTeamCount; i++)
        {
            var memberUserId = await SeedUserAsync(db, $"member{i}");
            var membership = LeagueMembership.Join(Guid.NewGuid(), league.LeagueId, memberUserId, _clock.UtcNow);
            db.LeagueMemberships.Add(membership);
            await db.SaveChangesAsync();
            Assert.True((await fantasyTeamService.CreateAsync(membership.LeagueMembershipId, season.SeasonId)).IsSuccess);
        }

        return (league.LeagueId, season.SeasonId);
    }

    private async Task<Guid> SeedPlayerAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var club = new Club { ClubId = Guid.NewGuid(), EplClubId = $"club{suffix}", Name = $"Test FC {suffix}", ShortName = "TFC" };
        var player = new Player { PlayerId = Guid.NewGuid(), EplPlayerId = $"player{suffix}", Name = $"Test Player {suffix}", Position = PlayerPosition.Mid, CurrentClubId = club.ClubId };
        db.Clubs.Add(club);
        db.Players.Add(player);
        await db.SaveChangesAsync();
        return player.PlayerId;
    }

    private async Task<Guid> GetOwningUserIdAsync(Guid fantasyTeamId)
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

    private async Task<Guid> SeedGameweekAsync(string eplSeasonIdentifier, int number)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = number, RosterLockDeadline = _clock.UtcNow.AddDays(number * 7) };
        db.Gameweeks.Add(gameweek);
        await db.SaveChangesAsync();
        return gameweek.GameweekId;
    }

    /// <summary>Seeds a LeagueStanding row with the given (already tie-break-resolved) Position — this test suite only needs IStandingsCalculationService's own output shape, not a live recalculation.</summary>
    private async Task SeedStandingAsync(Guid seasonId, Guid fantasyTeamId, Guid asOfGameweekId, int position)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var standing = LeagueStanding.Calculate(
            seasonId, fantasyTeamId, asOfGameweekId,
            leaguePoints: 0, played: 0, won: 0, drawn: 0, lost: 0,
            fantasyGoalsFor: 0, fantasyGoalsAgainst: 0, fantasyGoalDifference: 0, captainPointsTotal: 0);
        standing.AssignPosition(position);
        db.LeagueStandings.Add(standing);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CreateInitialDraftAsync_creates_an_InProgress_Draft_with_a_randomized_order_and_locks_InitialSquadSize()
    {
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 3);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var expectedFantasyTeamIds = await db.FantasyTeams.Where(t => t.SeasonId == seasonId).Select(t => t.FantasyTeamId).ToListAsync();
        var seasonConfigBefore = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == seasonId);

        var draft = await scope.ServiceProvider.GetRequiredService<IDraftService>().CreateInitialDraftAsync(seasonId);

        Assert.Equal(DraftType.Initial, draft.DraftType);
        Assert.Equal(DraftStatus.InProgress, draft.Status);
        Assert.Equal(1, draft.CurrentRound);
        Assert.Equal(0, draft.CurrentPickIndex);
        Assert.Equal(expectedFantasyTeamIds.ToHashSet(), draft.DraftOrder.ToHashSet()); // a true permutation of every participating FantasyTeamId.
        Assert.Equal(seasonConfigBefore.DraftTimerSecondsInitial, draft.TimerSeconds);
        Assert.Equal(_clock.UtcNow.AddSeconds(draft.TimerSeconds), draft.CurrentPickDeadline);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.True(await verifyDb.Drafts.AnyAsync(d => d.DraftId == draft.DraftId));

        var seasonConfigAfter = await verifyDb.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == seasonId);
        Assert.Contains(nameof(SeasonConfiguration.InitialSquadSize), seasonConfigAfter.LockedFields);

        var season = await verifyDb.Seasons.SingleAsync(s => s.SeasonId == seasonId);
        Assert.Equal(SeasonStatus.DraftInProgress, season.Status);
    }

    [Fact]
    public async Task CreateInitialDraftAsync_rejects_fewer_than_two_FantasyTeams()
    {
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 1);

        await using var scope = _provider.CreateAsyncScope();
        var draftService = scope.ServiceProvider.GetRequiredService<IDraftService>();

        await Assert.ThrowsAsync<InsufficientFantasyTeamsException>(() => draftService.CreateInitialDraftAsync(seasonId));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.False(await verifyDb.Drafts.AnyAsync(d => d.SeasonId == seasonId));
    }

    [Fact]
    public async Task CreateInitialDraftAsync_rejects_a_second_attempt_once_the_Season_already_has_a_Draft()
    {
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 2);
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDraftService>().CreateInitialDraftAsync(seasonId);
        }

        await using var scope2 = _provider.CreateAsyncScope();
        var draftService = scope2.ServiceProvider.GetRequiredService<IDraftService>();

        await Assert.ThrowsAsync<SeasonNotReadyForInitialDraftException>(() => draftService.CreateInitialDraftAsync(seasonId));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.Equal(1, await verifyDb.Drafts.CountAsync(d => d.SeasonId == seasonId));
    }

    [Fact]
    public async Task CreateSecondaryDraftAsync_derives_DraftOrder_from_current_standings_worst_place_first()
    {
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 3);

        string eplSeasonIdentifier;
        List<Guid> fantasyTeamIds;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            eplSeasonIdentifier = await db.Seasons.Where(s => s.SeasonId == seasonId).Select(s => s.EplSeasonIdentifier).SingleAsync();
            fantasyTeamIds = await db.FantasyTeams.Where(t => t.SeasonId == seasonId).Select(t => t.FantasyTeamId).ToListAsync();
        }

        var gameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 1);
        // fantasyTeamIds[0] is 1st place (best), [1] is 2nd, [2] is 3rd (worst) — Secondary Draft order must reverse this.
        await SeedStandingAsync(seasonId, fantasyTeamIds[0], gameweekId, position: 1);
        await SeedStandingAsync(seasonId, fantasyTeamIds[1], gameweekId, position: 2);
        await SeedStandingAsync(seasonId, fantasyTeamIds[2], gameweekId, position: 3);

        await using var scope2 = _provider.CreateAsyncScope();
        var draft = await scope2.ServiceProvider.GetRequiredService<IDraftService>().CreateSecondaryDraftAsync(seasonId);

        Assert.Equal(DraftType.Secondary, draft.DraftType);
        Assert.Equal(DraftStatus.InProgress, draft.Status);
        Assert.Equal([fantasyTeamIds[2], fantasyTeamIds[1], fantasyTeamIds[0]], draft.DraftOrder);
        Assert.NotNull(draft.StandingsSnapshotTakenAt);
    }

    [Fact]
    public async Task CreateSecondaryDraftAsync_throws_when_no_standings_snapshot_has_ever_been_computed()
    {
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 2);

        await using var scope = _provider.CreateAsyncScope();
        var draftService = scope.ServiceProvider.GetRequiredService<IDraftService>();

        await Assert.ThrowsAsync<StandingsSnapshotUnavailableException>(() => draftService.CreateSecondaryDraftAsync(seasonId));
    }

    [Fact]
    public async Task CreateSecondaryDraftAsync_DraftOrder_and_StandingsSnapshotTakenAt_are_immutable_under_a_later_standings_recalculation()
    {
        // The task breakdown's own required proof (BR-138): once captured, a Secondary Draft's own
        // order must never be reshuffled by a later standings recalculation (e.g. a late score
        // override's own cascade), even though the *current* standings genuinely changed afterward.
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 3);
        string eplSeasonIdentifier;
        List<Guid> fantasyTeamIds;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            eplSeasonIdentifier = await db.Seasons.Where(s => s.SeasonId == seasonId).Select(s => s.EplSeasonIdentifier).SingleAsync();
            fantasyTeamIds = await db.FantasyTeams.Where(t => t.SeasonId == seasonId).Select(t => t.FantasyTeamId).ToListAsync();
        }

        var gameweek1 = await SeedGameweekAsync(eplSeasonIdentifier, 1);
        await SeedStandingAsync(seasonId, fantasyTeamIds[0], gameweek1, position: 1);
        await SeedStandingAsync(seasonId, fantasyTeamIds[1], gameweek1, position: 2);
        await SeedStandingAsync(seasonId, fantasyTeamIds[2], gameweek1, position: 3);

        Guid draftId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var draft = await scope.ServiceProvider.GetRequiredService<IDraftService>().CreateSecondaryDraftAsync(seasonId);
            draftId = draft.DraftId;
        }

        var beforeRecalculation = await GetDraftAsync(draftId);

        // A later Gameweek's own standings snapshot completely reverses the ranking — proving this
        // isn't merely "nothing happened to call it," but that a real, materially different
        // recalculation genuinely occurred afterward.
        var gameweek2 = await SeedGameweekAsync(eplSeasonIdentifier, 2);
        await SeedStandingAsync(seasonId, fantasyTeamIds[0], gameweek2, position: 3);
        await SeedStandingAsync(seasonId, fantasyTeamIds[1], gameweek2, position: 2);
        await SeedStandingAsync(seasonId, fantasyTeamIds[2], gameweek2, position: 1);

        var afterRecalculation = await GetDraftAsync(draftId);

        Assert.Equal(beforeRecalculation.DraftOrder, afterRecalculation.DraftOrder);
        Assert.Equal(beforeRecalculation.StandingsSnapshotTakenAt, afterRecalculation.StandingsSnapshotTakenAt);
    }

    private async Task<Draft> GetDraftAsync(Guid draftId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.Drafts.AsNoTracking().SingleAsync(d => d.DraftId == draftId);
    }

    [Fact]
    public async Task MakePickAsync_records_the_DraftSelection_and_SquadPlayer_atomically_and_advances_the_turn()
    {
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 2);
        var playerId = await SeedPlayerAsync();
        await using var scope = _provider.CreateAsyncScope();
        var draft = await scope.ServiceProvider.GetRequiredService<IDraftService>().CreateInitialDraftAsync(seasonId);
        var onTurnTeamId = draft.DraftOrder[0];
        var onTurnUserId = await GetOwningUserIdAsync(onTurnTeamId);

        var selection = await scope.ServiceProvider.GetRequiredService<IDraftService>().MakePickAsync(draft.DraftId, onTurnUserId, playerId);

        Assert.Equal(onTurnTeamId, selection.FantasyTeamId);
        Assert.Equal(playerId, selection.PlayerId);
        Assert.Equal(1, selection.Round);
        Assert.Equal(1, selection.PickNumber);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.True(await verifyDb.DraftSelections.AnyAsync(s => s.DraftSelectionId == selection.DraftSelectionId));

        var squadPlayer = await verifyDb.SquadPlayers.SingleAsync(sp => sp.PlayerId == playerId && sp.SeasonId == seasonId);
        Assert.Equal(onTurnTeamId, squadPlayer.FantasyTeamId);
        Assert.Equal(AcquisitionType.InitialDraft, squadPlayer.AcquisitionType);
        Assert.True(squadPlayer.IsCurrentlyOwned);

        var updatedDraft = await verifyDb.Drafts.SingleAsync(d => d.DraftId == draft.DraftId);
        Assert.Equal(1, updatedDraft.CurrentRound);
        Assert.Equal(1, updatedDraft.CurrentPickIndex); // advanced to the second team's turn.
    }

    [Fact]
    public async Task MakePickAsync_rejects_a_player_already_owned_by_another_FantasyTeam()
    {
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 2);
        var playerId = await SeedPlayerAsync();
        await using var scope = _provider.CreateAsyncScope();
        var draftService = scope.ServiceProvider.GetRequiredService<IDraftService>();
        var draft = await draftService.CreateInitialDraftAsync(seasonId);
        var firstTeamId = draft.DraftOrder[0];
        var secondTeamId = draft.DraftOrder[1];
        await draftService.MakePickAsync(draft.DraftId, await GetOwningUserIdAsync(firstTeamId), playerId);

        var secondUserId = await GetOwningUserIdAsync(secondTeamId);
        await Assert.ThrowsAsync<PlayerAlreadyOwnedException>(() => draftService.MakePickAsync(draft.DraftId, secondUserId, playerId));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.Equal(1, await verifyDb.SquadPlayers.CountAsync(sp => sp.PlayerId == playerId && sp.SeasonId == seasonId));
    }

    [Fact]
    public async Task MakePickAsync_rejects_a_pick_from_a_FantasyTeam_not_currently_on_the_clock()
    {
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 2);
        var playerId = await SeedPlayerAsync();
        await using var scope = _provider.CreateAsyncScope();
        var draftService = scope.ServiceProvider.GetRequiredService<IDraftService>();
        var draft = await draftService.CreateInitialDraftAsync(seasonId);
        var notOnTurnTeamId = draft.DraftOrder[1];
        var notOnTurnUserId = await GetOwningUserIdAsync(notOnTurnTeamId);

        await Assert.ThrowsAsync<NotYourTurnException>(() => draftService.MakePickAsync(draft.DraftId, notOnTurnUserId, playerId));
    }

    [Fact]
    public async Task MakePickAsync_completes_the_Draft_once_every_team_has_picked_in_a_single_round_squad()
    {
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 2);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == seasonId);
        seasonConfiguration.InitialSquadSize = 1; // a one-round Draft, so the second pick is also the final one.
        await db.SaveChangesAsync();

        var draftService = scope.ServiceProvider.GetRequiredService<IDraftService>();
        var draft = await draftService.CreateInitialDraftAsync(seasonId);
        var firstPlayerId = await SeedPlayerAsync();
        var secondPlayerId = await SeedPlayerAsync();
        await draftService.MakePickAsync(draft.DraftId, await GetOwningUserIdAsync(draft.DraftOrder[0]), firstPlayerId);

        await draftService.MakePickAsync(draft.DraftId, await GetOwningUserIdAsync(draft.DraftOrder[1]), secondPlayerId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var completedDraft = await verifyDb.Drafts.SingleAsync(d => d.DraftId == draft.DraftId);
        Assert.Equal(DraftStatus.Completed, completedDraft.Status);
        Assert.Null(completedDraft.CurrentPickDeadline);
    }

    [Fact]
    public async Task MakePickAsync_for_a_Secondary_Draft_uses_SecondaryDraftSelectionsPerTeam_not_InitialSquadSize_and_adds_to_the_existing_squad()
    {
        // BR-060/BR-062/BR-198: a Secondary Draft has its own, separate size (here shrunk to 1, and
        // deliberately left far apart from InitialSquadSize's own 25 default so the two could never
        // be confused for one another), and its picks add to whatever the FantasyTeam already owns
        // from the Initial Draft rather than replacing it.
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 2);
        var alreadyOwnedPlayerId = await SeedPlayerAsync();
        var secondaryPickPlayerId = await SeedPlayerAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == seasonId);
            seasonConfiguration.SecondaryDraftSelectionsPerTeam = 1;
            await db.SaveChangesAsync();
        }

        var draft = await CreateDraftAsync(DraftType.Secondary, seasonId);
        var onTurnFantasyTeamId = draft.DraftOrder[0];
        var otherFantasyTeamId = draft.DraftOrder[1];

        // Simulates a squad already built up from the (unrelated, already-completed) Initial Draft.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.SquadPlayers.Add(new SquadPlayer
            {
                SquadPlayerId = Guid.NewGuid(),
                FantasyTeamId = onTurnFantasyTeamId,
                PlayerId = alreadyOwnedPlayerId,
                SeasonId = seasonId,
                AcquisitionType = AcquisitionType.InitialDraft,
                AcquiredAt = _clock.UtcNow,
                IsCurrentlyOwned = true,
            });
            await db.SaveChangesAsync();
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDraftService>()
                .MakePickAsync(draft.DraftId, await GetOwningUserIdAsync(onTurnFantasyTeamId), secondaryPickPlayerId);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var otherTeamPickPlayerId = await SeedPlayerAsync();
            await scope.ServiceProvider.GetRequiredService<IDraftService>()
                .MakePickAsync(draft.DraftId, await GetOwningUserIdAsync(otherFantasyTeamId), otherTeamPickPlayerId);
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownedPlayerIds = await verifyDb.SquadPlayers
            .Where(sp => sp.FantasyTeamId == onTurnFantasyTeamId && sp.IsCurrentlyOwned)
            .Select(sp => sp.PlayerId)
            .ToListAsync();
        Assert.Equal(2, ownedPlayerIds.Count);
        Assert.Contains(alreadyOwnedPlayerId, ownedPlayerIds); // the Initial Draft pick is still owned...
        Assert.Contains(secondaryPickPlayerId, ownedPlayerIds); // ...alongside the new Secondary Draft pick (BR-062).

        // SecondaryDraftSelectionsPerTeam = 1: both teams have now picked exactly once, so the
        // Draft is already Completed — proving totalRounds really did come from that field, not
        // the (very different, 25-round-default) InitialSquadSize.
        var completedDraft = await verifyDb.Drafts.SingleAsync(d => d.DraftId == draft.DraftId);
        Assert.Equal(DraftStatus.Completed, completedDraft.Status);
    }

    /// <summary>Creates whichever kind of Draft <paramref name="draftType"/> names, seeding a standings snapshot first for Secondary (CreateSecondaryDraftAsync's own precondition).</summary>
    private async Task<Draft> CreateDraftAsync(DraftType draftType, Guid seasonId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var draftService = scope.ServiceProvider.GetRequiredService<IDraftService>();

        if (draftType == DraftType.Initial)
        {
            return await draftService.CreateInitialDraftAsync(seasonId);
        }

        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var eplSeasonIdentifier = await db.Seasons.Where(s => s.SeasonId == seasonId).Select(s => s.EplSeasonIdentifier).SingleAsync();
        var fantasyTeamIds = await db.FantasyTeams.Where(t => t.SeasonId == seasonId).Select(t => t.FantasyTeamId).ToListAsync();
        var gameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 1);
        for (var i = 0; i < fantasyTeamIds.Count; i++)
        {
            await SeedStandingAsync(seasonId, fantasyTeamIds[i], gameweekId, position: i + 1);
        }

        return await draftService.CreateSecondaryDraftAsync(seasonId);
    }

    /// <summary>
    /// IT-47 (F-006.3, BR-060-BR-062/BR-198): the same concurrency proof IT-24 already established
    /// for the Initial Draft, parameterized to prove it holds for the Secondary Draft too — the
    /// shared atomic-pick engine (Draft.MakePick, ux_squad_players_owned) is reused verbatim, only
    /// DraftService.MakePickAsync's own choice of totalRounds/AcquisitionType varies by DraftType.
    /// </summary>
    [Theory]
    [InlineData(DraftType.Initial, AcquisitionType.InitialDraft)]
    [InlineData(DraftType.Secondary, AcquisitionType.SecondaryDraft)]
    public async Task MakePickAsync_two_concurrent_requests_for_the_same_player_exactly_one_succeeds(DraftType draftType, AcquisitionType expectedAcquisitionType)
    {
        // AP-009/AP-010 (db-tests/run_concurrency_test.sh's own reproduction requirement): each
        // Draft type's own strict single-active-turn design means two genuinely different
        // FantasyTeams can never both legitimately be on the clock for the same pick at once — so
        // the realistic race this guards against is the SAME on-turn team's own request arriving
        // twice concurrently (a network retry, a double-submitted click, with no Idempotency-Key)
        // rather than two rival teams. The guarantee under test is the same either way:
        // ux_squad_players_owned, not an application-level check alone, decides the winner.
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 2);
        var playerId = await SeedPlayerAsync();
        Guid draftId;
        Guid onTurnUserId;
        {
            var draft = await CreateDraftAsync(draftType, seasonId);
            draftId = draft.DraftId;
            onTurnUserId = await GetOwningUserIdAsync(draft.DraftOrder[0]);
        }

        async Task<bool> AttemptAsync()
        {
            await using var scope = _provider.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<IDraftService>().MakePickAsync(draftId, onTurnUserId, playerId);
                return true;
            }
            catch (PlayerAlreadyOwnedException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(AttemptAsync(), AttemptAsync());

        Assert.Equal(1, results.Count(succeeded => succeeded));
        Assert.Equal(1, results.Count(succeeded => !succeeded));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var squadPlayer = await verifyDb.SquadPlayers.SingleAsync(sp => sp.PlayerId == playerId && sp.SeasonId == seasonId);
        Assert.Equal(expectedAcquisitionType, squadPlayer.AcquisitionType); // BR-062/BR-198: distinguishable, even though both simply add to the squad.
        Assert.Equal(1, await verifyDb.DraftSelections.CountAsync(s => s.DraftId == draftId));
    }

    private async Task<Guid> GetLeagueAdministratorUserIdAsync(Guid leagueId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.LeagueMemberships.Where(m => m.LeagueId == leagueId && m.IsAdministrator).Select(m => m.UserId).SingleAsync();
    }

    [Fact]
    public async Task ExtendTimerAsync_pushes_the_deadline_back_and_records_a_DraftTimerExtended_AdministrativeAction()
    {
        var (leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 2);
        var adminUserId = await GetLeagueAdministratorUserIdAsync(leagueId);
        await using var scope = _provider.CreateAsyncScope();
        var draftService = scope.ServiceProvider.GetRequiredService<IDraftService>();
        var draft = await draftService.CreateInitialDraftAsync(seasonId);
        var originalDeadline = draft.CurrentPickDeadline;

        var extended = await draftService.ExtendTimerAsync(draft.DraftId, additionalSeconds: 120, adminUserId);

        Assert.Equal(originalDeadline!.Value.AddSeconds(120), extended.CurrentPickDeadline);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persistedDraft = await verifyDb.Drafts.SingleAsync(d => d.DraftId == draft.DraftId);
        Assert.Equal(originalDeadline.Value.AddSeconds(120), persistedDraft.CurrentPickDeadline);

        var action = await verifyDb.AdministrativeActions.SingleAsync(a => a.TargetEntityId == draft.DraftId && a.ActionType == AdminActionType.DraftTimerExtended);
        Assert.Equal(leagueId, action.LeagueId);
        var adminMembershipId = await verifyDb.LeagueMemberships.Where(m => m.UserId == adminUserId && m.LeagueId == leagueId).Select(m => m.LeagueMembershipId).SingleAsync();
        Assert.Equal(adminMembershipId, action.ActingMembershipId);
    }

    [Fact]
    public async Task ExtendTimerAsync_rejects_a_non_Administrator()
    {
        var (leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 2);
        await using var scope = _provider.CreateAsyncScope();
        var draftService = scope.ServiceProvider.GetRequiredService<IDraftService>();
        var draft = await draftService.CreateInitialDraftAsync(seasonId);

        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var plainMemberUserId = await db.LeagueMemberships.Where(m => m.LeagueId == leagueId && !m.IsAdministrator).Select(m => m.UserId).SingleAsync();

        // DraftLeagueAdministratorAuthorizationHandler already prevents a non-Administrator from
        // reaching this service call in the first place (BR-162) — this proves the service itself
        // has nothing to grant it even if that gate were somehow bypassed: resolving an
        // "acting Administrator membership" for a non-Administrator user finds no matching row.
        await Assert.ThrowsAsync<InvalidOperationException>(() => draftService.ExtendTimerAsync(draft.DraftId, 60, plainMemberUserId));
    }

    [Fact]
    public async Task A_full_Initial_Draft_with_one_deliberately_expired_pick_still_reaches_Completed_with_every_FantasyTeam_at_InitialSquadSize()
    {
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 2);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == seasonId);
        seasonConfiguration.InitialSquadSize = 1; // a one-round Draft — two total picks, one per team.
        await db.SaveChangesAsync();

        var draftService = scope.ServiceProvider.GetRequiredService<IDraftService>();
        var draft = await draftService.CreateInitialDraftAsync(seasonId);
        var firstPlayerId = await SeedPlayerAsync();
        var secondPlayerId = await SeedPlayerAsync();

        // The first team picks normally.
        await draftService.MakePickAsync(draft.DraftId, await GetOwningUserIdAsync(draft.DraftOrder[0]), firstPlayerId);

        // The second team's turn — the deadline passes with no pick and no Administrator extension.
        _clock.AdvanceBy(TimeSpan.FromSeconds(400));
        var sweepHandler = scope.ServiceProvider.GetServices<IDeadlineSweepHandler>().Single();
        await sweepHandler.SweepAsync(CancellationToken.None);

        await using (var verifyScope1 = _provider.CreateAsyncScope())
        {
            var verifyDb1 = verifyScope1.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            var afterSkip = await verifyDb1.Drafts.SingleAsync(d => d.DraftId == draft.DraftId);
            Assert.Equal(DraftStatus.InProgress, afterSkip.Status); // AC2: deferred, not Completed.
            Assert.Equal([draft.DraftOrder[1]], afterSkip.PendingMakeupPicks);
            Assert.False(await verifyDb1.DraftSelections.AnyAsync(s => s.FantasyTeamId == draft.DraftOrder[1])); // AC1: no DraftSelection for the skipped turn.
        }

        // The skipped team now makes its makeup pick, completing the Draft (AC3).
        await draftService.MakePickAsync(draft.DraftId, await GetOwningUserIdAsync(draft.DraftOrder[1]), secondPlayerId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var completedDraft = await verifyDb.Drafts.SingleAsync(d => d.DraftId == draft.DraftId);
        Assert.Equal(DraftStatus.Completed, completedDraft.Status);
        Assert.Empty(completedDraft.PendingMakeupPicks);

        foreach (var fantasyTeamId in draft.DraftOrder)
        {
            Assert.Equal(1, await verifyDb.SquadPlayers.CountAsync(sp => sp.FantasyTeamId == fantasyTeamId && sp.SeasonId == seasonId));
        }

        var makeupSelection = await verifyDb.DraftSelections.SingleAsync(s => s.FantasyTeamId == draft.DraftOrder[1]);
        Assert.True(makeupSelection.IsMakeupPick);
    }

    private async Task<Guid> SeedReplacementOpportunityAsync(Guid fantasyTeamId, Guid sourcePlayerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var opportunity = new ReplacementOpportunity
        {
            ReplacementOpportunityId = Guid.NewGuid(),
            FantasyTeamId = fantasyTeamId,
            SourcePlayerId = sourcePlayerId,
            GrantedAt = _clock.UtcNow,
            GrantReason = ReplacementGrantReason.EplExit,
            SpentAt = null,
        };
        db.ReplacementOpportunities.Add(opportunity);
        await db.SaveChangesAsync();
        return opportunity.ReplacementOpportunityId;
    }

    private async Task<Guid> GetFirstFantasyTeamIdAsync(Guid seasonId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.FantasyTeams.Where(t => t.SeasonId == seasonId).Select(t => t.FantasyTeamId).FirstAsync();
    }

    [Fact]
    public async Task MakeReplacementPickAsync_spends_the_opportunity_and_adds_the_player_via_AcquisitionType_Replacement()
    {
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 1);
        var fantasyTeamId = await GetFirstFantasyTeamIdAsync(seasonId);
        var sourcePlayerId = await SeedPlayerAsync(); // the exited/injured player that triggered the opportunity.
        var replacementPlayerId = await SeedPlayerAsync();
        var opportunityId = await SeedReplacementOpportunityAsync(fantasyTeamId, sourcePlayerId);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var draftService = scope.ServiceProvider.GetRequiredService<IDraftService>();
            var result = await draftService.MakeReplacementPickAsync(fantasyTeamId, opportunityId, replacementPlayerId);
            Assert.NotNull(result.SpentAt);
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var opportunity = await verifyDb.ReplacementOpportunities.SingleAsync(o => o.ReplacementOpportunityId == opportunityId);
        Assert.NotNull(opportunity.SpentAt);

        var squadPlayer = await verifyDb.SquadPlayers.SingleAsync(sp => sp.FantasyTeamId == fantasyTeamId && sp.PlayerId == replacementPlayerId);
        Assert.Equal(AcquisitionType.Replacement, squadPlayer.AcquisitionType);
        Assert.True(squadPlayer.IsCurrentlyOwned);
    }

    [Fact]
    public async Task MakeReplacementPickAsync_does_not_require_releasing_the_now_eligible_player_first()
    {
        // BR-064: using the opportunity does not require first releasing the now-eligible player,
        // nor does any release happen automatically — the eligible player stays owned throughout.
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 1);
        var fantasyTeamId = await GetFirstFantasyTeamIdAsync(seasonId);
        var eligiblePlayerId = await SeedPlayerAsync();
        var replacementPlayerId = await SeedPlayerAsync();
        var opportunityId = await SeedReplacementOpportunityAsync(fantasyTeamId, eligiblePlayerId);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.SquadPlayers.Add(new SquadPlayer
            {
                SquadPlayerId = Guid.NewGuid(),
                FantasyTeamId = fantasyTeamId,
                PlayerId = eligiblePlayerId,
                SeasonId = seasonId,
                AcquisitionType = AcquisitionType.InitialDraft,
                AcquiredAt = _clock.UtcNow,
                IsCurrentlyOwned = true,
                ReplacementEligibleAt = _clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDraftService>()
                .MakeReplacementPickAsync(fantasyTeamId, opportunityId, replacementPlayerId);
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownedPlayerIds = await verifyDb.SquadPlayers
            .Where(sp => sp.FantasyTeamId == fantasyTeamId && sp.IsCurrentlyOwned)
            .Select(sp => sp.PlayerId)
            .ToListAsync();
        Assert.Contains(eligiblePlayerId, ownedPlayerIds); // still owned — no automatic release.
        Assert.Contains(replacementPlayerId, ownedPlayerIds); // added alongside it (BR-062-style expansion).
    }

    [Fact]
    public async Task MakeReplacementPickAsync_throws_when_the_opportunity_is_already_spent()
    {
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 1);
        var fantasyTeamId = await GetFirstFantasyTeamIdAsync(seasonId);
        var sourcePlayerId = await SeedPlayerAsync();
        var firstReplacementPlayerId = await SeedPlayerAsync();
        var secondReplacementPlayerId = await SeedPlayerAsync();
        var opportunityId = await SeedReplacementOpportunityAsync(fantasyTeamId, sourcePlayerId);

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDraftService>()
                .MakeReplacementPickAsync(fantasyTeamId, opportunityId, firstReplacementPlayerId);
        }

        await using var scope2 = _provider.CreateAsyncScope();
        var draftService = scope2.ServiceProvider.GetRequiredService<IDraftService>();

        await Assert.ThrowsAsync<ReplacementOpportunityAlreadySpentException>(
            () => draftService.MakeReplacementPickAsync(fantasyTeamId, opportunityId, secondReplacementPlayerId));
    }

    [Fact]
    public async Task MakeReplacementPickAsync_rejects_a_player_already_owned_by_another_FantasyTeam()
    {
        // BR-261/BR-262: "replacement-eligible" does not mean "unowned" — this proves the ownership
        // guard applies to a Replacement pick exactly as it does to an Initial/Secondary one.
        var (_, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 2);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var fantasyTeamIds = await db.FantasyTeams.Where(t => t.SeasonId == seasonId).Select(t => t.FantasyTeamId).ToListAsync();
        var ownedByOtherTeamPlayerId = await SeedPlayerAsync();
        db.SquadPlayers.Add(new SquadPlayer
        {
            SquadPlayerId = Guid.NewGuid(),
            FantasyTeamId = fantasyTeamIds[1],
            PlayerId = ownedByOtherTeamPlayerId,
            SeasonId = seasonId,
            AcquisitionType = AcquisitionType.InitialDraft,
            AcquiredAt = _clock.UtcNow,
            IsCurrentlyOwned = true,
        });
        await db.SaveChangesAsync();
        var sourcePlayerId = await SeedPlayerAsync();
        var opportunityId = await SeedReplacementOpportunityAsync(fantasyTeamIds[0], sourcePlayerId);

        var draftService = scope.ServiceProvider.GetRequiredService<IDraftService>();
        await Assert.ThrowsAsync<PlayerAlreadyOwnedException>(
            () => draftService.MakeReplacementPickAsync(fantasyTeamIds[0], opportunityId, ownedByOtherTeamPlayerId));
    }

    private async Task<Guid> SeedOwnedSquadPlayerAsync(Guid fantasyTeamId, Guid seasonId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var playerId = await SeedPlayerAsync();
        var squadPlayer = new SquadPlayer
        {
            SquadPlayerId = Guid.NewGuid(),
            FantasyTeamId = fantasyTeamId,
            PlayerId = playerId,
            SeasonId = seasonId,
            AcquisitionType = AcquisitionType.InitialDraft,
            AcquiredAt = _clock.UtcNow,
            IsCurrentlyOwned = true,
        };
        db.SquadPlayers.Add(squadPlayer);
        await db.SaveChangesAsync();
        return squadPlayer.SquadPlayerId;
    }

    [Fact]
    public async Task DeclareSeasonEndingInjuryAsync_marks_the_SquadPlayer_eligible_and_grants_an_opportunity_with_a_real_ActingMembershipId()
    {
        var (leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 1);
        var fantasyTeamId = await GetFirstFantasyTeamIdAsync(seasonId);
        var squadPlayerId = await SeedOwnedSquadPlayerAsync(fantasyTeamId, seasonId);
        var adminUserId = await GetLeagueAdministratorUserIdAsync(leagueId);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var draftService = scope.ServiceProvider.GetRequiredService<IDraftService>();
            var opportunity = await draftService.DeclareSeasonEndingInjuryAsync(leagueId, squadPlayerId, "league consensus reached", adminUserId);

            Assert.NotNull(opportunity);
            Assert.Equal(ReplacementGrantReason.SeasonEndingInjury, opportunity!.GrantReason);
            Assert.Null(opportunity.SpentAt);
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var squadPlayer = await verifyDb.SquadPlayers.SingleAsync(sp => sp.SquadPlayerId == squadPlayerId);
        Assert.NotNull(squadPlayer.ReplacementEligibleAt);

        var action = await verifyDb.AdministrativeActions.SingleAsync(a => a.ActionType == AdminActionType.SeasonEndingInjuryDeclared);
        Assert.Equal(leagueId, action.LeagueId);
        Assert.NotNull(action.ActingMembershipId); // IT-49's own distinction from IT-21's system-generated (null) grants.
        Assert.Equal("league consensus reached", action.Reason);
        Assert.Equal("SquadPlayer", action.TargetEntityType);
        Assert.Equal(squadPlayerId, action.TargetEntityId);
    }

    [Fact]
    public async Task DeclareSeasonEndingInjuryAsync_throws_when_the_SquadPlayer_is_already_replacement_eligible()
    {
        var (leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 1);
        var fantasyTeamId = await GetFirstFantasyTeamIdAsync(seasonId);
        var squadPlayerId = await SeedOwnedSquadPlayerAsync(fantasyTeamId, seasonId);
        var adminUserId = await GetLeagueAdministratorUserIdAsync(leagueId);
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDraftService>()
                .DeclareSeasonEndingInjuryAsync(leagueId, squadPlayerId, null, adminUserId);
        }

        await using var scope2 = _provider.CreateAsyncScope();
        var draftService = scope2.ServiceProvider.GetRequiredService<IDraftService>();

        await Assert.ThrowsAsync<SquadPlayerAlreadyReplacementEligibleException>(
            () => draftService.DeclareSeasonEndingInjuryAsync(leagueId, squadPlayerId, null, adminUserId));
    }

    [Fact]
    public async Task DeclareSeasonEndingInjuryAsync_marks_eligibility_but_grants_no_opportunity_once_the_cap_is_reached()
    {
        // BR-287: the same cap ReplacementOpportunityPolicy already enforces for IT-21's automatic
        // path applies identically here — eligibility is still recorded, but no further token.
        var (leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(fantasyTeamCount: 1);
        var fantasyTeamId = await GetFirstFantasyTeamIdAsync(seasonId);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == seasonId);
            seasonConfiguration.ReplacementSelectionCap = 0; // already at cap before this determination.
            await db.SaveChangesAsync();
        }
        var squadPlayerId = await SeedOwnedSquadPlayerAsync(fantasyTeamId, seasonId);
        var adminUserId = await GetLeagueAdministratorUserIdAsync(leagueId);

        await using var scope2 = _provider.CreateAsyncScope();
        var draftService = scope2.ServiceProvider.GetRequiredService<IDraftService>();
        var opportunity = await draftService.DeclareSeasonEndingInjuryAsync(leagueId, squadPlayerId, null, adminUserId);

        Assert.Null(opportunity);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var squadPlayer = await verifyDb.SquadPlayers.SingleAsync(sp => sp.SquadPlayerId == squadPlayerId);
        Assert.NotNull(squadPlayer.ReplacementEligibleAt); // eligibility marked regardless of the cap.
        Assert.False(await verifyDb.ReplacementOpportunities.AnyAsync(o => o.FantasyTeamId == fantasyTeamId));
    }
}
