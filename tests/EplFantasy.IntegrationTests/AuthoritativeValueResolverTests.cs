using System.Text.Json;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using EplFantasy.Scoring;
using EplFantasy.SharedKernel;
using EplFantasy.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves IT-F09's precedence claim end-to-end — Active ScoreOverride &gt; Official
/// PlayerPerformance &gt; Application Calculation (Invariant 12) — against real rows, not just
/// that the C# compiles.
/// </summary>
public class AuthoritativeValueResolverTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
    private readonly FakeClock _clock = FakeClock.StartingAt(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        var services = new ServiceCollection();
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

    private async Task<Guid> SeedPlayerPerformanceAsync(bool isOfficial, int goals)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var suffix = Guid.NewGuid().ToString("N");
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = $"avr-{suffix}" });
        var gameweekId = Guid.NewGuid();
        db.Gameweeks.Add(new Gameweek { GameweekId = gameweekId, EplSeasonIdentifier = $"avr-{suffix}", Number = 1, RosterLockDeadline = _clock.UtcNow.AddDays(7) });
        var club = new Club { ClubId = Guid.NewGuid(), EplClubId = $"avr-club-{suffix}", Name = "AVR FC", ShortName = "AVR" };
        db.Clubs.Add(club);
        var player = new Player { PlayerId = Guid.NewGuid(), EplPlayerId = $"avr-player-{suffix}", Name = "AVR Player", Position = PlayerPosition.Fwd, CurrentClubId = club.ClubId };
        db.Players.Add(player);

        var performanceId = Guid.NewGuid();
        db.PlayerPerformances.Add(new PlayerPerformance
        {
            PlayerPerformanceId = performanceId,
            GameweekId = gameweekId,
            PlayerId = player.PlayerId,
            MinutesPlayed = 90,
            FantasyPoints = 6,
            Goals = goals,
            Source = PerformanceSource.OfficialFpl,
            IsOfficial = isOfficial,
            RetrievedAt = _clock.UtcNow,
        });

        await db.SaveChangesAsync();
        return performanceId;
    }

    private async Task<Guid> SeedAdministratorMembershipAsync()
    {
        // score_overrides.administrator_membership_id has a real FK to league_memberships
        // (ScoringConfigurations.cs, IT-F06) — a random Guid is rejected, even though this
        // resolver's own tests don't care which League the "administrator" belongs to.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var suffix = Guid.NewGuid().ToString("N");
        var user = new User { UserId = Guid.NewGuid(), Username = $"avr_admin_{suffix}", Email = $"avr_admin_{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(user);
        var leagueId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        db.Leagues.Add(new League { LeagueId = leagueId, Name = "AVR Smoke League", CreatedByMembershipId = membershipId, CreatedAt = _clock.UtcNow });
        db.LeagueMemberships.Add(new LeagueMembership { LeagueMembershipId = membershipId, LeagueId = leagueId, UserId = user.UserId, IsAdministrator = true, Status = MembershipStatus.Active, JoinedAt = _clock.UtcNow });

        await db.SaveChangesAsync();
        return membershipId;
    }

    private async Task<Guid> SeedNoPerformanceRowAsync()
    {
        // Only what a ScoreOverride's FK to player_performances would need if one were attached —
        // this scenario deliberately has NO PlayerPerformance row at all, exercising the
        // "nothing official has been retrieved yet" branch.
        return await Task.FromResult(Guid.NewGuid());
    }

    [Fact]
    public async Task An_active_override_wins_over_official_data()
    {
        var performanceId = await SeedPlayerPerformanceAsync(isOfficial: true, goals: 1);
        var membershipId = await SeedAdministratorMembershipAsync();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        db.ScoreOverrides.Add(new ScoreOverride
        {
            ScoreOverrideId = Guid.NewGuid(),
            PlayerPerformanceId = performanceId,
            AdministratorMembershipId = membershipId,
            OriginalValueJson = """{"goals":1}""",
            OverrideValueJson = """{"goals":2}""",
            CreatedAt = _clock.UtcNow,
        });
        await db.SaveChangesAsync();

        var resolver = scope.ServiceProvider.GetRequiredService<IAuthoritativeValueResolver>();
        var goals = await resolver.ResolveAsync(
            performanceId,
            fromActiveOverride: o => JsonDocument.Parse(o.OverrideValueJson).RootElement.GetProperty("goals").GetInt32(),
            fromOfficialData: p => p.Goals,
            applicationCalculation: () => -1);

        Assert.Equal(2, goals); // the override's value, not the official row's 1.
    }

    [Fact]
    public async Task Official_data_wins_when_no_override_exists()
    {
        var performanceId = await SeedPlayerPerformanceAsync(isOfficial: true, goals: 3);

        await using var scope = _provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IAuthoritativeValueResolver>();

        var goals = await resolver.ResolveAsync(
            performanceId,
            fromActiveOverride: o => -1,
            fromOfficialData: p => p.Goals,
            applicationCalculation: () => -1);

        Assert.Equal(3, goals);
    }

    [Fact]
    public async Task An_undone_override_no_longer_shadows_official_data()
    {
        var performanceId = await SeedPlayerPerformanceAsync(isOfficial: true, goals: 4);
        var membershipId = await SeedAdministratorMembershipAsync();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        db.ScoreOverrides.Add(new ScoreOverride
        {
            ScoreOverrideId = Guid.NewGuid(),
            PlayerPerformanceId = performanceId,
            AdministratorMembershipId = membershipId,
            OriginalValueJson = """{"goals":4}""",
            OverrideValueJson = """{"goals":99}""",
            CreatedAt = _clock.UtcNow,
            UndoneAt = _clock.UtcNow.AddMinutes(1), // already undone.
        });
        await db.SaveChangesAsync();

        var resolver = scope.ServiceProvider.GetRequiredService<IAuthoritativeValueResolver>();
        var goals = await resolver.ResolveAsync(
            performanceId,
            fromActiveOverride: o => 99,
            fromOfficialData: p => p.Goals,
            applicationCalculation: () => -1);

        Assert.Equal(4, goals); // falls through to official data — the undone override must not apply.
    }

    [Fact]
    public async Task A_newer_active_override_wins_over_an_older_undone_one()
    {
        var performanceId = await SeedPlayerPerformanceAsync(isOfficial: true, goals: 1);
        var membershipId = await SeedAdministratorMembershipAsync();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        db.ScoreOverrides.Add(new ScoreOverride
        {
            ScoreOverrideId = Guid.NewGuid(),
            PlayerPerformanceId = performanceId,
            AdministratorMembershipId = membershipId,
            OriginalValueJson = """{"goals":1}""",
            OverrideValueJson = """{"goals":50}""",
            CreatedAt = _clock.UtcNow,
            UndoneAt = _clock.UtcNow.AddMinutes(1),
        });
        _clock.AdvanceBy(TimeSpan.FromMinutes(5));
        db.ScoreOverrides.Add(new ScoreOverride
        {
            ScoreOverrideId = Guid.NewGuid(),
            PlayerPerformanceId = performanceId,
            AdministratorMembershipId = membershipId,
            OriginalValueJson = """{"goals":1}""",
            OverrideValueJson = """{"goals":7}""",
            CreatedAt = _clock.UtcNow, // newer, still active.
        });
        await db.SaveChangesAsync();

        var resolver = scope.ServiceProvider.GetRequiredService<IAuthoritativeValueResolver>();
        var goals = await resolver.ResolveAsync(
            performanceId,
            fromActiveOverride: o => JsonDocument.Parse(o.OverrideValueJson).RootElement.GetProperty("goals").GetInt32(),
            fromOfficialData: p => p.Goals,
            applicationCalculation: () => -1);

        Assert.Equal(7, goals);
    }

    [Fact]
    public async Task Non_official_data_falls_through_to_application_calculation()
    {
        var performanceId = await SeedPlayerPerformanceAsync(isOfficial: false, goals: 9);

        await using var scope = _provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IAuthoritativeValueResolver>();

        var goals = await resolver.ResolveAsync(
            performanceId,
            fromActiveOverride: o => -1,
            fromOfficialData: p => p.Goals,
            applicationCalculation: () => 0);

        Assert.Equal(0, goals); // a Manual/non-official row does not count as "Official PlayerPerformance".
    }

    [Fact]
    public async Task No_performance_row_at_all_falls_through_to_application_calculation()
    {
        var performanceId = await SeedNoPerformanceRowAsync();

        await using var scope = _provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IAuthoritativeValueResolver>();

        var goals = await resolver.ResolveAsync(
            performanceId,
            fromActiveOverride: o => -1,
            fromOfficialData: p => -1,
            applicationCalculation: () => 0);

        Assert.Equal(0, goals);
    }
}
