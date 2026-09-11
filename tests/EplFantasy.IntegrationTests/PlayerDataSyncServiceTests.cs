using EplFantasy.Administration;
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
/// Proves IT-F11's two central claims — idempotent upsert (AP-008) and all-or-nothing batch
/// failure (BR-233) — against real Postgres, using FakeFplDataSource in place of the real
/// (not-yet-built) FPL API client. Also proves IT-19's ClubStanding recompute (BR-329/BR-330):
/// SyncGameweeksAndFixturesAsync derives the real-world EPL table from whichever fixtures are
/// Completed, in the same atomic batch as the fixture upserts themselves. Also proves IT-21's
/// automatic EPL-exit replacement eligibility (BR-066/BR-071/BR-308): SyncPlayersAsync detects the
/// has-a-club → has-none transition and, for every FantasyTeam currently owning that Player, marks
/// the SquadPlayer eligible and grants a ReplacementOpportunity via a system-generated
/// AdministrativeAction (ActingMembershipId: null).
/// </summary>
public class PlayerDataSyncServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
    private readonly FakeFplDataSource _fakeSource = new();
    private readonly FakeClock _clock = FakeClock.StartingAt(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        var services = new ServiceCollection();
        services.AddInfrastructure(_container.GetConnectionString());
        // AddInfrastructure (IT-17) registers the real FplApiDataSource against the live FPL API —
        // this override, registered after it, replaces that with FakeFplDataSource instead (DI
        // resolves the last registration for a given service type), so this suite never makes a
        // real network call. IPlayerDataSyncService itself is left exactly as AddInfrastructure
        // already wires it.
        services.AddSingleton<IFplDataSource>(_fakeSource);
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
    public async Task Syncing_the_same_club_twice_does_not_duplicate_it()
    {
        var suffix = Guid.NewGuid().ToString("N");
        _fakeSource.Clubs.Add(new ClubSyncData($"club-{suffix}", "Original FC", "OFC"));

        await using var scope1 = _provider.CreateAsyncScope();
        await scope1.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncClubsAsync();

        await using var scope2 = _provider.CreateAsyncScope();
        await scope2.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncClubsAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var matches = await db.Clubs.Where(c => c.EplClubId == $"club-{suffix}").ToListAsync();

        Assert.Single(matches);
    }

    [Fact]
    public async Task Re_syncing_a_club_with_a_changed_name_updates_the_existing_row_in_place()
    {
        var suffix = Guid.NewGuid().ToString("N");
        _fakeSource.Clubs.Add(new ClubSyncData($"club-{suffix}", "Old Name FC", "ONF"));

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncClubsAsync();
        }

        var originalId = await GetClubIdAsync($"club-{suffix}");

        _fakeSource.Clubs.Clear();
        _fakeSource.Clubs.Add(new ClubSyncData($"club-{suffix}", "New Name FC", "NNF"));

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncClubsAsync();
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var club = await db.Clubs.SingleAsync(c => c.EplClubId == $"club-{suffix}");

        Assert.Equal(originalId, club.ClubId); // same internal row, not a duplicate.
        Assert.Equal("New Name FC", club.Name);
    }

    private async Task<Guid> GetClubIdAsync(string eplClubId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return (await db.Clubs.SingleAsync(c => c.EplClubId == eplClubId)).ClubId;
    }

    [Fact]
    public async Task Syncing_players_resolves_their_current_club_by_external_id()
    {
        var suffix = Guid.NewGuid().ToString("N");
        _fakeSource.Clubs.Add(new ClubSyncData($"club-{suffix}", "Player Sync FC", "PSF"));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncClubsAsync();
        }

        _fakeSource.Players.Add(new PlayerSyncData($"player-{suffix}", "Sync Player", PlayerPosition.Mid, $"club-{suffix}"));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncPlayersAsync();
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var player = await db.Players.SingleAsync(p => p.EplPlayerId == $"player-{suffix}");
        var expectedClubId = await GetClubIdAsync($"club-{suffix}");

        Assert.Equal(expectedClubId, player.CurrentClubId);
    }

    [Fact]
    public async Task Syncing_players_referencing_an_unknown_club_persists_nothing_from_the_batch()
    {
        var suffix = Guid.NewGuid().ToString("N");
        // A valid player first, then one with a bad club reference — proving the WHOLE batch is
        // rejected (BR-233), not just the malformed entry.
        _fakeSource.Players.Add(new PlayerSyncData($"player-good-{suffix}", "Good Player", PlayerPosition.Fwd, CurrentEplClubId: null));
        _fakeSource.Players.Add(new PlayerSyncData($"player-bad-{suffix}", "Bad Player", PlayerPosition.Def, $"club-does-not-exist-{suffix}"));

        await using var scope = _provider.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => syncService.SyncPlayersAsync());

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var anyPersisted = await db.Players.AnyAsync(p => p.EplPlayerId == $"player-good-{suffix}" || p.EplPlayerId == $"player-bad-{suffix}");

        Assert.False(anyPersisted, "neither player should persist when the batch contains a bad reference");
    }

    [Fact]
    public async Task Syncing_gameweeks_and_fixtures_together_is_idempotent_and_updates_scores_in_place()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var eplSeasonId = $"season-{suffix}";
        _fakeSource.Clubs.Add(new ClubSyncData($"home-{suffix}", "Home FC", "HFC"));
        _fakeSource.Clubs.Add(new ClubSyncData($"away-{suffix}", "Away FC", "AFC"));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncClubsAsync();
        }

        var kickoff = DateTimeOffset.UtcNow.AddDays(1);
        _fakeSource.Gameweeks.Add(new GameweekSyncData(eplSeasonId, 1, kickoff.AddHours(-1)));
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fixture-{suffix}", eplSeasonId, 1, $"home-{suffix}", $"away-{suffix}", kickoff, FixtureStatus.Scheduled, null, null));

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncGameweeksAndFixturesAsync(eplSeasonId);
        }

        // Re-sync with the match now complete — same external ids, updated result.
        _fakeSource.Fixtures.Clear();
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fixture-{suffix}", eplSeasonId, 1, $"home-{suffix}", $"away-{suffix}", kickoff, FixtureStatus.Completed, 2, 1));

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncGameweeksAndFixturesAsync(eplSeasonId);
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var seasonRows = await db.EplSeasons.Where(s => s.EplSeasonIdentifier == eplSeasonId).ToListAsync();
        Assert.Single(seasonRows);

        var gameweekRows = await db.Gameweeks.Where(g => g.EplSeasonIdentifier == eplSeasonId).ToListAsync();
        Assert.Single(gameweekRows);

        var fixture = await db.Fixtures.SingleAsync(f => f.EplFixtureId == $"fixture-{suffix}");
        Assert.Equal(FixtureStatus.Completed, fixture.Status);
        Assert.Equal(2, fixture.HomeGoals);
        Assert.Equal(1, fixture.AwayGoals);
    }

    [Fact]
    public async Task Syncing_a_fixture_as_Postponed_preserves_its_last_known_GameweekId_and_KickoffTime()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var eplSeasonId = $"season-{suffix}";
        _fakeSource.Clubs.Add(new ClubSyncData($"home-{suffix}", "Home FC", "HFC"));
        _fakeSource.Clubs.Add(new ClubSyncData($"away-{suffix}", "Away FC", "AFC"));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncClubsAsync();
        }

        // Truncated to microseconds — Postgres timestamptz's own precision — so the round-tripped
        // value compares equal; .NET's DateTimeOffset carries finer (100ns tick) precision than
        // that, which would otherwise fail an exact Assert.Equal after a real round trip.
        var originalKickoff = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddDays(1));
        _fakeSource.Gameweeks.Add(new GameweekSyncData(eplSeasonId, 1, originalKickoff.AddHours(-1)));
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fixture-{suffix}", eplSeasonId, 1, $"home-{suffix}", $"away-{suffix}", originalKickoff, FixtureStatus.Scheduled, null, null));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncGameweeksAndFixturesAsync(eplSeasonId);
        }
        var originalGameweekId = (await GetFixtureAsync(suffix)).GameweekId;

        // BR-100: officially postponed — FPL now reports no confirmed kickoff_time, but the
        // fixture stays assigned to Gameweek 1 (not yet reassigned to a new one).
        _fakeSource.Fixtures.Clear();
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fixture-{suffix}", eplSeasonId, 1, $"home-{suffix}", $"away-{suffix}", KickoffTime: null, FixtureStatus.Postponed, null, null));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncGameweeksAndFixturesAsync(eplSeasonId);
        }

        var postponed = await GetFixtureAsync(suffix);
        Assert.Equal(FixtureStatus.Postponed, postponed.Status);
        Assert.Equal(originalGameweekId, postponed.GameweekId); // untouched while postponed.
        Assert.Equal(originalKickoff, postponed.KickoffTime); // the NOT NULL column keeps its last confirmed value, not cleared.
    }

    [Fact]
    public async Task Syncing_a_rescheduled_fixture_reassigns_it_to_its_new_official_Gameweek()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var eplSeasonId = $"season-{suffix}";
        _fakeSource.Clubs.Add(new ClubSyncData($"home-{suffix}", "Home FC", "HFC"));
        _fakeSource.Clubs.Add(new ClubSyncData($"away-{suffix}", "Away FC", "AFC"));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncClubsAsync();
        }

        var originalKickoff = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddDays(1));
        var rescheduledKickoff = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddDays(14));
        _fakeSource.Gameweeks.Add(new GameweekSyncData(eplSeasonId, 1, originalKickoff.AddHours(-1)));
        _fakeSource.Gameweeks.Add(new GameweekSyncData(eplSeasonId, 2, rescheduledKickoff.AddHours(-1)));
        // First synced as normally Scheduled — a fixture with no confirmed kickoff_time at all
        // (Postponed) can only ever preserve/reassign an *existing* row (V003's NOT NULL column),
        // never create a brand-new one from nothing.
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fixture-{suffix}", eplSeasonId, 1, $"home-{suffix}", $"away-{suffix}", originalKickoff, FixtureStatus.Scheduled, null, null));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncGameweeksAndFixturesAsync(eplSeasonId);
        }
        var originalGameweekId = (await GetFixtureAsync(suffix)).GameweekId;

        // Postponed, pending reschedule.
        _fakeSource.Fixtures.Clear();
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fixture-{suffix}", eplSeasonId, 1, $"home-{suffix}", $"away-{suffix}", KickoffTime: null, FixtureStatus.Postponed, null, null));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncGameweeksAndFixturesAsync(eplSeasonId);
        }

        // BR-100/BR-103: officially rescheduled into Gameweek 2, with its new confirmed kickoff.
        _fakeSource.Fixtures.Clear();
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fixture-{suffix}", eplSeasonId, 2, $"home-{suffix}", $"away-{suffix}", rescheduledKickoff, FixtureStatus.Scheduled, null, null));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncGameweeksAndFixturesAsync(eplSeasonId);
        }

        var rescheduled = await GetFixtureAsync(suffix);
        Assert.Equal(FixtureStatus.Scheduled, rescheduled.Status);
        Assert.NotEqual(originalGameweekId, rescheduled.GameweekId);
        Assert.Equal(rescheduledKickoff, rescheduled.KickoffTime);
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), value.Offset);

    private async Task<Fixture> GetFixtureAsync(string suffix)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        return await db.Fixtures.SingleAsync(f => f.EplFixtureId == $"fixture-{suffix}");
    }

    [Fact]
    public async Task Syncing_a_fixture_for_a_gameweek_not_in_the_batch_persists_nothing()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var eplSeasonId = $"season-{suffix}";
        _fakeSource.Clubs.Add(new ClubSyncData($"home-{suffix}", "Home FC", "HFC"));
        _fakeSource.Clubs.Add(new ClubSyncData($"away-{suffix}", "Away FC", "AFC"));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncClubsAsync();
        }

        // No GameweekSyncData added at all — the fixture references Gameweek 1, which this batch
        // never establishes.
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fixture-{suffix}", eplSeasonId, 1, $"home-{suffix}", $"away-{suffix}", DateTimeOffset.UtcNow, FixtureStatus.Scheduled, null, null));

        await using var scope2 = _provider.CreateAsyncScope();
        var syncService = scope2.ServiceProvider.GetRequiredService<IPlayerDataSyncService>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => syncService.SyncGameweeksAndFixturesAsync(eplSeasonId));

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.False(await db.EplSeasons.AnyAsync(s => s.EplSeasonIdentifier == eplSeasonId));
        Assert.False(await db.Fixtures.AnyAsync(f => f.EplFixtureId == $"fixture-{suffix}"));
    }

    [Fact]
    public async Task Syncing_fixtures_recomputes_ClubStandings_ordering_points_then_goal_difference()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var eplSeasonId = $"season-{suffix}";
        var a = $"a-{suffix}";
        var b = $"b-{suffix}";
        var c = $"c-{suffix}";
        var d = $"d-{suffix}"; // never plays a completed fixture — proves a 0-played Club still appears.
        _fakeSource.Clubs.Add(new ClubSyncData(a, "Club A", "A"));
        _fakeSource.Clubs.Add(new ClubSyncData(b, "Club B", "B"));
        _fakeSource.Clubs.Add(new ClubSyncData(c, "Club C", "C"));
        _fakeSource.Clubs.Add(new ClubSyncData(d, "Club D", "D"));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncClubsAsync();
        }

        var kickoff = DateTimeOffset.UtcNow.AddDays(-1);
        _fakeSource.Gameweeks.Add(new GameweekSyncData(eplSeasonId, 1, kickoff.AddHours(-1)));
        // A beats B 2-0; B and C draw 1-1; D's fixture (vs A) is still only Scheduled, not Completed.
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fx-ab-{suffix}", eplSeasonId, 1, a, b, kickoff, FixtureStatus.Completed, 2, 0));
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fx-bc-{suffix}", eplSeasonId, 1, b, c, kickoff, FixtureStatus.Completed, 1, 1));
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fx-ad-{suffix}", eplSeasonId, 1, a, d, kickoff.AddDays(7), FixtureStatus.Scheduled, null, null));

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncGameweeksAndFixturesAsync(eplSeasonId);
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var clubIdByExternalId = await db.Clubs.Where(cl => cl.EplClubId == a || cl.EplClubId == b || cl.EplClubId == c || cl.EplClubId == d)
            .ToDictionaryAsync(cl => cl.EplClubId, cl => cl.ClubId);
        var standings = await db.ClubStandings.Where(s => s.EplSeasonIdentifier == eplSeasonId).ToDictionaryAsync(s => s.ClubId);

        var standingA = standings[clubIdByExternalId[a]];
        Assert.Equal((1, 1, 0, 0, 2, 0, 3), (standingA.Played, standingA.Won, standingA.Drawn, standingA.Lost, standingA.GoalsFor, standingA.GoalsAgainst, standingA.Points));

        var standingB = standings[clubIdByExternalId[b]];
        Assert.Equal((2, 0, 1, 1, 1, 3, 1), (standingB.Played, standingB.Won, standingB.Drawn, standingB.Lost, standingB.GoalsFor, standingB.GoalsAgainst, standingB.Points));

        var standingC = standings[clubIdByExternalId[c]];
        Assert.Equal((1, 0, 1, 0, 1, 1, 1), (standingC.Played, standingC.Won, standingC.Drawn, standingC.Lost, standingC.GoalsFor, standingC.GoalsAgainst, standingC.Points));

        var standingD = standings[clubIdByExternalId[d]];
        Assert.Equal((0, 0, 0, 0, 0, 0, 0), (standingD.Played, standingD.Won, standingD.Drawn, standingD.Lost, standingD.GoalsFor, standingD.GoalsAgainst, standingD.Points));

        // A (3 pts) first; C and B are level on 1 point each, but C's goal difference (0) beats B's (-2).
        Assert.Equal(1, standingA.Position);
        Assert.Equal(2, standingC.Position);
        Assert.Equal(3, standingB.Position);
    }

    [Fact]
    public async Task Re_syncing_a_corrected_fixture_result_updates_the_existing_ClubStanding_rows_in_place()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var eplSeasonId = $"season-{suffix}";
        var a = $"a-{suffix}";
        var b = $"b-{suffix}";
        _fakeSource.Clubs.Add(new ClubSyncData(a, "Club A", "A"));
        _fakeSource.Clubs.Add(new ClubSyncData(b, "Club B", "B"));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncClubsAsync();
        }

        var kickoff = DateTimeOffset.UtcNow.AddDays(-1);
        _fakeSource.Gameweeks.Add(new GameweekSyncData(eplSeasonId, 1, kickoff.AddHours(-1)));
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fx-{suffix}", eplSeasonId, 1, a, b, kickoff, FixtureStatus.Completed, 2, 0));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncGameweeksAndFixturesAsync(eplSeasonId);
        }

        // BR-234: an official correction — the match was actually a 2-2 draw.
        _fakeSource.Fixtures.Clear();
        _fakeSource.Fixtures.Add(new FixtureSyncData($"fx-{suffix}", eplSeasonId, 1, a, b, kickoff, FixtureStatus.Completed, 2, 2));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncGameweeksAndFixturesAsync(eplSeasonId);
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var clubIdByExternalId = await db.Clubs.Where(cl => cl.EplClubId == a || cl.EplClubId == b).ToDictionaryAsync(cl => cl.EplClubId, cl => cl.ClubId);
        var standings = await db.ClubStandings.Where(s => s.EplSeasonIdentifier == eplSeasonId).ToListAsync();

        Assert.Equal(2, standings.Count); // updated in place, not duplicated.
        var standingA = standings.Single(s => s.ClubId == clubIdByExternalId[a]);
        Assert.Equal((1, 0, 1, 0, 2, 2, 1), (standingA.Played, standingA.Won, standingA.Drawn, standingA.Lost, standingA.GoalsFor, standingA.GoalsAgainst, standingA.Points));
    }

    /// <summary>Builds a real League (with its default LeagueConfiguration/founding LeagueMembership), Season (with its copied SeasonConfiguration), and Active FantasyTeam via the actual application services — the same precedent FantasyTeamServiceTests already established — so IT-21's exit-handling has a real FantasyTeam/SeasonConfiguration to grant against.</summary>
    private async Task<(Guid LeagueId, Guid FantasyTeamId, Guid SeasonId)> SeedFantasyTeamAsync(int? replacementSelectionCap = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var ownerId = Guid.NewGuid();
        db.Users.Add(new User { UserId = ownerId, Username = $"owner{suffix}", Email = $"owner{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow });
        await db.SaveChangesAsync();

        var league = await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(ownerId, $"League {suffix}", null);

        var eplSeasonIdentifier = $"exit-season-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        await db.SaveChangesAsync();
        var season = (await scope.ServiceProvider.GetRequiredService<ISeasonService>().CreateAsync(league.LeagueId, eplSeasonIdentifier, new DateOnly(2026, 8, 15))).Value;

        if (replacementSelectionCap is not null)
        {
            var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == season.SeasonId);
            seasonConfiguration.ReplacementSelectionCap = replacementSelectionCap;
            await db.SaveChangesAsync();
        }

        var team = (await scope.ServiceProvider.GetRequiredService<IFantasyTeamService>().CreateAsync(league.CreatedByMembershipId, season.SeasonId)).Value;

        return (league.LeagueId, team.FantasyTeamId, season.SeasonId);
    }

    /// <summary>Syncs a fresh Club+Player (via the real sync services) and gives the FantasyTeam current ownership of that Player via a directly-inserted SquadPlayer row.</summary>
    private async Task<(Guid PlayerId, string EplPlayerId)> SeedOwnedPlayerAsync(Guid fantasyTeamId, Guid seasonId)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var eplClubId = $"club-{suffix}";
        var eplPlayerId = $"player-{suffix}";
        _fakeSource.Clubs.Add(new ClubSyncData(eplClubId, "Test FC", "TFC"));
        _fakeSource.Players.Add(new PlayerSyncData(eplPlayerId, "Test Player", PlayerPosition.Mid, eplClubId));

        await using var scope = _provider.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>();
        await syncService.SyncClubsAsync();
        await syncService.SyncPlayersAsync();

        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var playerId = (await db.Players.SingleAsync(p => p.EplPlayerId == eplPlayerId)).PlayerId;
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

        return (playerId, eplPlayerId);
    }

    [Fact]
    public async Task SyncPlayersAsync_an_EPL_exit_marks_the_SquadPlayer_eligible_and_grants_one_ReplacementOpportunity_via_a_system_generated_AdministrativeAction()
    {
        var (leagueId, fantasyTeamId, seasonId) = await SeedFantasyTeamAsync();
        var (playerId, eplPlayerId) = await SeedOwnedPlayerAsync(fantasyTeamId, seasonId);

        // The player exits the EPL — no longer associated with any club.
        _fakeSource.Players.Clear();
        _fakeSource.Players.Add(new PlayerSyncData(eplPlayerId, "Test Player", PlayerPosition.Mid, CurrentEplClubId: null));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncPlayersAsync();
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var player = await db.Players.SingleAsync(p => p.PlayerId == playerId);
        Assert.Null(player.CurrentClubId);

        var squadPlayer = await db.SquadPlayers.SingleAsync(sp => sp.PlayerId == playerId && sp.FantasyTeamId == fantasyTeamId);
        Assert.Equal(_clock.UtcNow, squadPlayer.ReplacementEligibleAt);

        var opportunity = await db.ReplacementOpportunities.SingleAsync(ro => ro.FantasyTeamId == fantasyTeamId);
        Assert.Equal(playerId, opportunity.SourcePlayerId);
        Assert.Equal(ReplacementGrantReason.EplExit, opportunity.GrantReason);
        Assert.Null(opportunity.SpentAt);

        var action = await db.AdministrativeActions.SingleAsync(a => a.TargetEntityId == opportunity.ReplacementOpportunityId);
        Assert.Null(action.ActingMembershipId); // BR-308: system-generated, no Administrator involved.
        Assert.Equal(AdminActionType.ReplacementEligibilityGranted, action.ActionType);
        Assert.Equal(leagueId, action.LeagueId);
    }

    [Fact]
    public async Task SyncPlayersAsync_re_syncing_an_already_exited_player_does_not_grant_a_second_opportunity()
    {
        var (_, fantasyTeamId, seasonId) = await SeedFantasyTeamAsync();
        var (_, eplPlayerId) = await SeedOwnedPlayerAsync(fantasyTeamId, seasonId);
        _fakeSource.Players.Clear();
        _fakeSource.Players.Add(new PlayerSyncData(eplPlayerId, "Test Player", PlayerPosition.Mid, CurrentEplClubId: null));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncPlayersAsync();
        }

        // Re-sync with the exact same (already-clubless) data — not a new transition.
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncPlayersAsync();
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var count = await db.ReplacementOpportunities.CountAsync(ro => ro.FantasyTeamId == fantasyTeamId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SyncPlayersAsync_marks_eligibility_but_grants_no_further_opportunity_once_the_Seasons_ReplacementSelectionCap_is_reached()
    {
        var (_, fantasyTeamId, seasonId) = await SeedFantasyTeamAsync(replacementSelectionCap: 1);
        var (priorSourcePlayerId, _) = await SeedOwnedPlayerAsync(fantasyTeamId, seasonId); // an earlier exit's source player, purely to satisfy the FK below.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            // Simulates a prior grant already consuming this FantasyTeam's one allowed opportunity.
            db.ReplacementOpportunities.Add(new ReplacementOpportunity
            {
                ReplacementOpportunityId = Guid.NewGuid(),
                FantasyTeamId = fantasyTeamId,
                SourcePlayerId = priorSourcePlayerId,
                GrantedAt = _clock.UtcNow,
                GrantReason = ReplacementGrantReason.EplExit,
            });
            await db.SaveChangesAsync();
        }
        var (playerId, eplPlayerId) = await SeedOwnedPlayerAsync(fantasyTeamId, seasonId);

        _fakeSource.Players.Clear();
        _fakeSource.Players.Add(new PlayerSyncData(eplPlayerId, "Test Player", PlayerPosition.Mid, CurrentEplClubId: null));
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPlayerDataSyncService>().SyncPlayersAsync();
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.Equal(1, await db2.ReplacementOpportunities.CountAsync(ro => ro.FantasyTeamId == fantasyTeamId)); // still just the pre-seeded one — BR-287's cap.

        var squadPlayer = await db2.SquadPlayers.SingleAsync(sp => sp.PlayerId == playerId && sp.FantasyTeamId == fantasyTeamId);
        Assert.Equal(_clock.UtcNow, squadPlayer.ReplacementEligibleAt); // eligibility is still marked even though no token was granted.
    }
}
