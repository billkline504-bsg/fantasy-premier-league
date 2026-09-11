using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using EplFantasy.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves IT-05's leaveLeague orchestration against real Postgres: BR-020–BR-022's "Status
/// transitions to Left, row retained" behavior, and BR-025/BR-283's sole-Administrator guard.
/// Also proves IT-10's League-specific icon override (BR-007–BR-010): most importantly, that
/// setting one League's override never touches the same User's membership row in another League.
/// </summary>
public class MembershipServiceTests : IAsyncLifetime
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
        var user = new User { UserId = Guid.NewGuid(), Username = $"{label}_{suffix}", Email = $"{label}_{suffix}@example.com", PasswordHash = "h", CreatedAt = _clock.UtcNow, UpdatedAt = _clock.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    private async Task<League> SeedLeagueAsync(Guid ownerUserId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILeagueService>().CreateAsync(ownerUserId, "Test League", null);
    }

    [Fact]
    public async Task LeaveAsync_sets_a_non_administrator_membership_to_Left_and_retains_the_row()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var inviteeId = await SeedUserAsync(db, "invitee");
        var league = await SeedLeagueAsync(ownerId);
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();
        var invitation = (await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, null)).Value;
        var membership = (await invitationService.AcceptInvitationAsync(invitation.Token, inviteeId)).Value;

        var membershipService = scope.ServiceProvider.GetRequiredService<IMembershipService>();
        await membershipService.LeaveAsync(league.LeagueId, membership.LeagueMembershipId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == membership.LeagueMembershipId);
        Assert.Equal(MembershipStatus.Left, persisted.Status);
        Assert.Equal(_clock.UtcNow, persisted.LeftAt);
    }

    [Fact]
    public async Task LeaveAsync_is_idempotent_for_an_already_left_membership()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var inviteeId = await SeedUserAsync(db, "invitee");
        var league = await SeedLeagueAsync(ownerId);
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();
        var invitation = (await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, null)).Value;
        var membership = (await invitationService.AcceptInvitationAsync(invitation.Token, inviteeId)).Value;
        var membershipService = scope.ServiceProvider.GetRequiredService<IMembershipService>();
        await membershipService.LeaveAsync(league.LeagueId, membership.LeagueMembershipId);

        // No exception on the second call.
        await membershipService.LeaveAsync(league.LeagueId, membership.LeagueMembershipId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == membership.LeagueMembershipId);
        Assert.Equal(MembershipStatus.Left, persisted.Status);
    }

    [Fact]
    public async Task LeaveAsync_throws_for_the_League_Administrators_own_membership()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var league = await SeedLeagueAsync(ownerId);
        var membershipService = scope.ServiceProvider.GetRequiredService<IMembershipService>();

        await Assert.ThrowsAsync<SoleAdministratorCannotLeaveException>(
            () => membershipService.LeaveAsync(league.LeagueId, league.CreatedByMembershipId));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == league.CreatedByMembershipId);
        Assert.Equal(MembershipStatus.Active, persisted.Status);
    }

    [Fact]
    public async Task LeaveAsync_allows_rejoining_afterward_as_a_brand_new_row()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var inviteeId = await SeedUserAsync(db, "invitee");
        var league = await SeedLeagueAsync(ownerId);
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();
        var firstInvitation = (await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, null)).Value;
        var firstMembership = (await invitationService.AcceptInvitationAsync(firstInvitation.Token, inviteeId)).Value;
        var membershipService = scope.ServiceProvider.GetRequiredService<IMembershipService>();
        await membershipService.LeaveAsync(league.LeagueId, firstMembership.LeagueMembershipId);

        var secondInvitation = (await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, null)).Value;
        var secondResult = await invitationService.AcceptInvitationAsync(secondInvitation.Token, inviteeId);

        Assert.True(secondResult.IsSuccess);
        Assert.NotEqual(firstMembership.LeagueMembershipId, secondResult.Value.LeagueMembershipId);
        Assert.Equal(MembershipStatus.Active, secondResult.Value.Status);
    }

    [Fact]
    public async Task SetLeagueIconAsync_sets_the_override_and_clearLeagueIconAsync_removes_it()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var league = await SeedLeagueAsync(ownerId);
        var icon = await db.ProfileIcons.Where(i => i.IsActive).FirstAsync();
        var membershipService = scope.ServiceProvider.GetRequiredService<IMembershipService>();

        var result = await membershipService.SetLeagueIconAsync(league.CreatedByMembershipId, icon.ProfileIconId);

        Assert.True(result.IsSuccess);
        Assert.Equal(icon.ProfileIconId, result.Value.LeagueIconId);

        await membershipService.ClearLeagueIconAsync(league.CreatedByMembershipId);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == league.CreatedByMembershipId);
        Assert.Null(persisted.LeagueIconId);
    }

    [Fact]
    public async Task SetLeagueIconAsync_fails_for_an_unknown_profileIconId()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var league = await SeedLeagueAsync(ownerId);
        var membershipService = scope.ServiceProvider.GetRequiredService<IMembershipService>();

        var result = await membershipService.SetLeagueIconAsync(league.CreatedByMembershipId, Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("profile_icon_not_active", result.Error.Code);
    }

    [Fact]
    public async Task SetLeagueIconAsync_throws_for_a_real_but_inactive_profileIconId()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var league = await SeedLeagueAsync(ownerId);
        var inactiveIcon = new ProfileIcon { ProfileIconId = Guid.NewGuid(), Name = "Retired", AssetIdentifier = "icons/profile/retired.svg", IsActive = false, SortOrder = 99 };
        db.ProfileIcons.Add(inactiveIcon);
        await db.SaveChangesAsync();
        var membershipService = scope.ServiceProvider.GetRequiredService<IMembershipService>();

        await Assert.ThrowsAsync<LeagueIconNotActiveException>(
            () => membershipService.SetLeagueIconAsync(league.CreatedByMembershipId, inactiveIcon.ProfileIconId));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == league.CreatedByMembershipId);
        Assert.Null(persisted.LeagueIconId);
    }

    [Fact]
    public async Task SetLeagueIconAsync_for_one_League_never_touches_the_same_Users_membership_row_in_another_League()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var leagueA = await SeedLeagueAsync(ownerId);
        var leagueB = await SeedLeagueAsync(ownerId); // same User, a second, independent League membership
        var iconA = await db.ProfileIcons.Where(i => i.IsActive).OrderBy(i => i.SortOrder).FirstAsync();
        var iconB = await db.ProfileIcons.Where(i => i.IsActive).OrderBy(i => i.SortOrder).Skip(1).FirstAsync();
        var membershipService = scope.ServiceProvider.GetRequiredService<IMembershipService>();

        // Set League B's override first, establishing a baseline this call must not disturb.
        await membershipService.SetLeagueIconAsync(leagueB.CreatedByMembershipId, iconB.ProfileIconId);

        var result = await membershipService.SetLeagueIconAsync(leagueA.CreatedByMembershipId, iconA.ProfileIconId);

        Assert.True(result.IsSuccess);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var membershipA = await verifyDb.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == leagueA.CreatedByMembershipId);
        var membershipB = await verifyDb.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == leagueB.CreatedByMembershipId);
        Assert.Equal(iconA.ProfileIconId, membershipA.LeagueIconId);
        Assert.Equal(iconB.ProfileIconId, membershipB.LeagueIconId); // BR-009: untouched by League A's change
    }
}
