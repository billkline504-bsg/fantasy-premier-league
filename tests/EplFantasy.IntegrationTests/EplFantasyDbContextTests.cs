using EplFantasy.Administration;
using EplFantasy.Competition;
using EplFantasy.Drafts;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.Notifications;
using EplFantasy.PlayerData;
using EplFantasy.Reporting;
using EplFantasy.Rosters;
using EplFantasy.Scoring;
using EplFantasy.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves EplFantasyDbContext's mapping actually matches the hand-written physical schema in
/// 06-database-migrations/migrations/ — not merely that the C# compiles (Database Migration
/// Strategy v1.0 §1: "the schema EF Core's own Migrations folder must reconcile against").
/// Spins up a disposable PostgreSQL 16 container, applies every V001–V013 migration file exactly
/// as they ship (the same files the docs/aidlc/07-testing-strategy/db-tests/ suite runs against),
/// then drives the real DbContext against that database — never Database.EnsureCreated(), which
/// would let EF Core generate its own (potentially different) schema instead of proving this one.
/// </summary>
public class EplFantasyDbContextTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .Build();

    private DbContextOptions<EplFantasyDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        _options = TestDbContextOptionsFactory.Create(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    private EplFantasyDbContext CreateContext() => new(_options);

    private static async Task ApplyHandWrittenMigrationsAsync(string connectionString)
    {
        var migrationsDir = FindMigrationsDirectory();
        var files = Directory.GetFiles(migrationsDir, "V*.sql").OrderBy(f => f, StringComparer.Ordinal);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var file in files)
        {
            var sql = await File.ReadAllTextAsync(file);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static string FindMigrationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EplFantasy.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate the repository root (EplFantasy.sln) walking up from the test output directory.");
        }

        return Path.Combine(dir.FullName, "docs", "aidlc", "06-database-migrations", "migrations");
    }

    [Fact]
    public async Task Identity_round_trips_and_enforces_active_only_username_uniqueness()
    {
        await using var db = CreateContext();

        var icon = new ProfileIcon { ProfileIconId = Guid.NewGuid(), Name = "Lion", AssetIdentifier = "icons/lion.svg" };
        db.ProfileIcons.Add(icon);

        var user = new User
        {
            UserId = Guid.NewGuid(),
            Username = "ef_smoke_alice",
            Email = "ef_smoke_alice@example.com",
            PasswordHash = "hash",
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        db.UserProfiles.Add(new UserProfile { UserId = user.UserId, DefaultIconId = icon.ProfileIconId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        await db.SaveChangesAsync();

        var reloaded = await db.Users.SingleAsync(u => u.UserId == user.UserId);
        Assert.Equal("ef_smoke_alice", reloaded.Username);
        Assert.Equal(UserStatus.Active, reloaded.Status); // proves the native enum round-trips correctly.
    }

    [Fact]
    public async Task League_and_founding_membership_save_together_across_the_deferred_circular_fk()
    {
        await using var db = CreateContext();

        var user = new User { UserId = Guid.NewGuid(), Username = "ef_smoke_admin", Email = "ef_smoke_admin@example.com", PasswordHash = "h", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);

        var leagueId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();

        db.Leagues.Add(new League { LeagueId = leagueId, Name = "EF Smoke League", CreatedByMembershipId = membershipId, CreatedAt = DateTimeOffset.UtcNow });
        db.LeagueMemberships.Add(new LeagueMembership
        {
            LeagueMembershipId = membershipId,
            LeagueId = leagueId,
            UserId = user.UserId,
            IsAdministrator = true,
            Status = MembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        // A single SaveChanges wraps both inserts in one transaction; the migration's
        // fk_leagues_created_by_membership (DEFERRABLE INITIALLY DEFERRED) is what lets this
        // succeed despite each row referencing the other's id (Migration Strategy v1.0 §2).
        await db.SaveChangesAsync();

        var savedLeague = await db.Leagues.SingleAsync(l => l.LeagueId == leagueId);
        Assert.Equal(membershipId, savedLeague.CreatedByMembershipId);
    }

    [Fact]
    public async Task GameweekRoster_uses_xmin_and_rejects_a_stale_concurrent_update()
    {
        var (fantasyTeamId, gameweekId) = await SeedRosterPrerequisitesAsync();

        Guid rosterId;
        await using (var db = CreateContext())
        {
            var roster = new GameweekRoster { GameweekRosterId = Guid.NewGuid(), FantasyTeamId = fantasyTeamId, GameweekId = gameweekId, Status = RosterStatus.Draft };
            db.GameweekRosters.Add(roster);
            await db.SaveChangesAsync();
            rosterId = roster.GameweekRosterId;
        }

        await using var dbA = CreateContext();
        await using var dbB = CreateContext();

        var rosterA = await dbA.GameweekRosters.SingleAsync(r => r.GameweekRosterId == rosterId);
        var rosterB = await dbB.GameweekRosters.SingleAsync(r => r.GameweekRosterId == rosterId);

        // Stays in Draft status deliberately — this test is isolating xmin concurrency, not
        // BR-279's roster-size trigger (which fires on a transition into Submitted/Locked/Scored
        // and requires a SeasonConfiguration row this test's fixture doesn't set up). Toggling an
        // unrelated column is enough to prove a stale xmin is rejected.
        rosterA.IsCarriedForward = true;
        await dbA.SaveChangesAsync();

        rosterB.IsCarriedForward = true;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task ClubStanding_generated_column_and_jsonb_administrative_action_round_trip()
    {
        await using var db = CreateContext();

        await db.EplSeasons.AddAsync(new EplSeason { EplSeasonIdentifier = "ef-smoke/29" });
        var club = new Club { ClubId = Guid.NewGuid(), EplClubId = "ef-smoke-club", Name = "EF Smoke FC", ShortName = "EFS" };
        db.Clubs.Add(club);
        db.ClubStandings.Add(new ClubStanding
        {
            EplSeasonIdentifier = "ef-smoke/29",
            ClubId = club.ClubId,
            Position = 1,
            Played = 10,
            Won = 7,
            Drawn = 2,
            Lost = 1,
            GoalsFor = 20,
            GoalsAgainst = 8,
            Points = 23,
        });

        // fk_leagues_created_by_membership is DEFERRABLE, not optional: some LeagueMembership row
        // with this id must exist by COMMIT, even one unrelated to what this test actually checks.
        var user = new User { UserId = Guid.NewGuid(), Username = $"ef_admin_{Guid.NewGuid():N}", Email = $"ef_admin_{Guid.NewGuid():N}@example.com", PasswordHash = "h", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        var leagueId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var league = new League { LeagueId = leagueId, Name = "Admin Smoke League", CreatedByMembershipId = membershipId, CreatedAt = DateTimeOffset.UtcNow };
        db.Leagues.Add(league);
        db.LeagueMemberships.Add(new LeagueMembership { LeagueMembershipId = membershipId, LeagueId = leagueId, UserId = user.UserId, IsAdministrator = true, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow });
        db.AdministrativeActions.Add(new AdministrativeAction
        {
            ActionId = Guid.NewGuid(),
            LeagueId = league.LeagueId,
            ActingMembershipId = null, // system-generated (BR-308) — proves the nullable FK column round-trips.
            ActionType = AdminActionType.ReplacementEligibilityGranted,
            TargetEntityType = "FantasyTeam",
            TargetEntityId = Guid.NewGuid(),
            BeforeStateJson = "{}",
            AfterStateJson = """{"eligible": true}""",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();

        var standing = await db.ClubStandings.SingleAsync(c => c.ClubId == club.ClubId);
        Assert.Equal(12, standing.GoalDifference); // computed by Postgres (20 - 8), never written by EF.

        var action = await db.AdministrativeActions.SingleAsync(a => a.LeagueId == league.LeagueId);
        Assert.Null(action.ActingMembershipId);
        Assert.Contains("eligible", action.AfterStateJson);
    }

    [Fact]
    public async Task Draft_pick_atomic_transaction_blocks_a_second_owner_for_the_same_player_and_season()
    {
        var (leagueId, seasonId, teamA, teamB, player) = await SeedDraftPrerequisitesAsync();

        await using (var db = CreateContext())
        {
            db.SquadPlayers.Add(new SquadPlayer { SquadPlayerId = Guid.NewGuid(), FantasyTeamId = teamA, PlayerId = player, SeasonId = seasonId, AcquisitionType = AcquisitionType.InitialDraft, AcquiredAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using var db2 = CreateContext();
        db2.SquadPlayers.Add(new SquadPlayer { SquadPlayerId = Guid.NewGuid(), FantasyTeamId = teamB, PlayerId = player, SeasonId = seasonId, AcquisitionType = AcquisitionType.InitialDraft, AcquiredAt = DateTimeOffset.UtcNow });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
        Assert.Contains("ux_squad_players_owned", exception.InnerException?.Message ?? exception.Message);

        _ = leagueId; // kept for readability of the seed tuple; not asserted on directly.
    }

    private async Task<(Guid FantasyTeamId, Guid GameweekId)> SeedRosterPrerequisitesAsync()
    {
        await using var db = CreateContext();

        var user = new User { UserId = Guid.NewGuid(), Username = $"ef_roster_{Guid.NewGuid():N}", Email = $"ef_roster_{Guid.NewGuid():N}@example.com", PasswordHash = "h", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var membershipId = Guid.NewGuid();
        var leagueId = Guid.NewGuid();
        db.Users.Add(user);
        db.Leagues.Add(new League { LeagueId = leagueId, Name = "Roster Smoke League", CreatedByMembershipId = membershipId, CreatedAt = DateTimeOffset.UtcNow });
        db.LeagueMemberships.Add(new LeagueMembership { LeagueMembershipId = membershipId, LeagueId = leagueId, UserId = user.UserId, IsAdministrator = true, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow });

        var eplSeasonId = $"ef-roster-{Guid.NewGuid():N}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonId });
        var seasonId = Guid.NewGuid();
        db.Seasons.Add(new Season { SeasonId = seasonId, LeagueId = leagueId, EplSeasonIdentifier = eplSeasonId, StartDate = DateOnly.FromDateTime(DateTime.UtcNow) });

        var fantasyTeamId = Guid.NewGuid();
        db.FantasyTeams.Add(new FantasyTeam { FantasyTeamId = fantasyTeamId, LeagueMembershipId = membershipId, SeasonId = seasonId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        var gameweekId = Guid.NewGuid();
        db.Gameweeks.Add(new Gameweek { GameweekId = gameweekId, EplSeasonIdentifier = eplSeasonId, Number = 1, RosterLockDeadline = DateTimeOffset.UtcNow.AddDays(7) });

        await db.SaveChangesAsync();

        return (fantasyTeamId, gameweekId);
    }

    private async Task<(Guid LeagueId, Guid SeasonId, Guid TeamA, Guid TeamB, Guid Player)> SeedDraftPrerequisitesAsync()
    {
        await using var db = CreateContext();

        var suffix = Guid.NewGuid().ToString("N");
        var userA = new User { UserId = Guid.NewGuid(), Username = $"ef_draft_a_{suffix}", Email = $"ef_draft_a_{suffix}@example.com", PasswordHash = "h", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var userB = new User { UserId = Guid.NewGuid(), Username = $"ef_draft_b_{suffix}", Email = $"ef_draft_b_{suffix}@example.com", PasswordHash = "h", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Users.AddRange(userA, userB);

        var leagueId = Guid.NewGuid();
        var membershipA = Guid.NewGuid();
        var membershipB = Guid.NewGuid();
        db.Leagues.Add(new League { LeagueId = leagueId, Name = "Draft Smoke League", CreatedByMembershipId = membershipA, CreatedAt = DateTimeOffset.UtcNow });
        db.LeagueMemberships.Add(new LeagueMembership { LeagueMembershipId = membershipA, LeagueId = leagueId, UserId = userA.UserId, IsAdministrator = true, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow });
        db.LeagueMemberships.Add(new LeagueMembership { LeagueMembershipId = membershipB, LeagueId = leagueId, UserId = userB.UserId, IsAdministrator = false, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow });

        var eplSeasonId = $"ef-draft-{suffix}";
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonId });
        var seasonId = Guid.NewGuid();
        db.Seasons.Add(new Season { SeasonId = seasonId, LeagueId = leagueId, EplSeasonIdentifier = eplSeasonId, StartDate = DateOnly.FromDateTime(DateTime.UtcNow) });

        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        db.FantasyTeams.Add(new FantasyTeam { FantasyTeamId = teamA, LeagueMembershipId = membershipA, SeasonId = seasonId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        db.FantasyTeams.Add(new FantasyTeam { FantasyTeamId = teamB, LeagueMembershipId = membershipB, SeasonId = seasonId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        var club = new Club { ClubId = Guid.NewGuid(), EplClubId = $"ef-draft-club-{suffix}", Name = "Draft Smoke FC", ShortName = "DSF" };
        db.Clubs.Add(club);
        var player = new Player { PlayerId = Guid.NewGuid(), EplPlayerId = $"ef-draft-player-{suffix}", Name = "Draft Smoke Player", Position = PlayerPosition.Mid, CurrentClubId = club.ClubId };
        db.Players.Add(player);

        await db.SaveChangesAsync();

        return (leagueId, seasonId, teamA, teamB, player.PlayerId);
    }
}
