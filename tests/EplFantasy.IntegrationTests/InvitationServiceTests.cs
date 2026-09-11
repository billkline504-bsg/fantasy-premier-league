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
/// Proves IT-04's invitation issue/revoke/accept orchestration against real Postgres: BR-029's
/// expiry (read from the League's real LeagueConfiguration row, IT-03), BR-020's "new row, not an
/// update" rejoin semantics, and the non-enumerable "invitation_invalid" failure shape.
/// </summary>
public class InvitationServiceTests : IAsyncLifetime
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
    public async Task CreateInvitationAsync_expires_per_the_Leagues_real_InvitationExpirationDays()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var league = await SeedLeagueAsync(ownerId);
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();

        var result = await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, seasonId: null);

        Assert.True(result.IsSuccess);
        var invitation = result.Value;
        Assert.Equal(_clock.UtcNow, invitation.CreatedAt);
        Assert.Equal(_clock.UtcNow.AddDays(7), invitation.ExpiresAt); // default LeagueConfiguration.InvitationExpirationDays
        Assert.Equal(InvitationStatus.Pending, invitation.Status);
        Assert.False(string.IsNullOrWhiteSpace(invitation.Token));
    }

    [Fact]
    public async Task CreateInvitationAsync_rejects_a_seasonId_that_does_not_belong_to_the_League()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var league = await SeedLeagueAsync(ownerId);
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();

        var result = await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, seasonId: Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("season_not_found", result.Error.Code);
    }

    [Fact]
    public async Task RevokeInvitationAsync_marks_a_pending_invitation_revoked()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var league = await SeedLeagueAsync(ownerId);
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();
        var created = (await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, null)).Value;

        var result = await invitationService.RevokeInvitationAsync(league.LeagueId, created.InvitationId);

        Assert.True(result.IsSuccess);
        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persisted = await verifyDb.Invitations.SingleAsync(i => i.InvitationId == created.InvitationId);
        Assert.Equal(InvitationStatus.Revoked, persisted.Status);
    }

    [Fact]
    public async Task RevokeInvitationAsync_fails_for_an_invitation_that_does_not_belong_to_the_League()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var league = await SeedLeagueAsync(ownerId);
        var otherLeague = await SeedLeagueAsync(ownerId);
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();
        var created = (await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, null)).Value;

        var result = await invitationService.RevokeInvitationAsync(otherLeague.LeagueId, created.InvitationId);

        Assert.True(result.IsFailure);
        Assert.Equal("invitation_not_found", result.Error.Code);
    }

    [Fact]
    public async Task RevokeInvitationAsync_throws_for_an_already_accepted_invitation()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var inviteeId = await SeedUserAsync(db, "invitee");
        var league = await SeedLeagueAsync(ownerId);
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();
        var created = (await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, null)).Value;
        await invitationService.AcceptInvitationAsync(created.Token, inviteeId);

        await Assert.ThrowsAsync<InvitationAlreadyAcceptedException>(() => invitationService.RevokeInvitationAsync(league.LeagueId, created.InvitationId));
    }

    [Fact]
    public async Task AcceptInvitationAsync_creates_a_new_active_non_administrator_membership()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var inviteeId = await SeedUserAsync(db, "invitee");
        var league = await SeedLeagueAsync(ownerId);
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();
        var created = (await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, null)).Value;

        var result = await invitationService.AcceptInvitationAsync(created.Token, inviteeId);

        Assert.True(result.IsSuccess);
        var membership = result.Value;
        Assert.Equal(league.LeagueId, membership.LeagueId);
        Assert.Equal(inviteeId, membership.UserId);
        Assert.False(membership.IsAdministrator);
        Assert.Equal(MembershipStatus.Active, membership.Status);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var persistedInvitation = await verifyDb.Invitations.SingleAsync(i => i.InvitationId == created.InvitationId);
        Assert.Equal(InvitationStatus.Accepted, persistedInvitation.Status);

        // IT-53 (F-012.1, BR-338): the newly-joined membership gets its own full, disabled-by-default
        // notification preference set — independent of the founding membership's own set.
        var preferences = await verifyDb.NotificationPreferences.Where(p => p.LeagueMembershipId == membership.LeagueMembershipId).ToListAsync();
        Assert.Equal(6, preferences.Count);
        Assert.All(preferences, p => Assert.False(p.Enabled));
    }

    [Fact]
    public async Task AcceptInvitationAsync_rejects_an_expired_token()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var inviteeId = await SeedUserAsync(db, "invitee");
        var league = await SeedLeagueAsync(ownerId);
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();
        var created = (await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, null)).Value;

        _clock.AdvanceBy(TimeSpan.FromDays(8));
        var result = await invitationService.AcceptInvitationAsync(created.Token, inviteeId);

        Assert.True(result.IsFailure);
        Assert.Equal("invitation_invalid", result.Error.Code);
    }

    [Fact]
    public async Task AcceptInvitationAsync_rejects_an_unknown_token_with_the_same_error_as_expired()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var inviteeId = await SeedUserAsync(db, "invitee");
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();

        var result = await invitationService.AcceptInvitationAsync("this-token-was-never-issued", inviteeId);

        Assert.True(result.IsFailure);
        Assert.Equal("invitation_invalid", result.Error.Code);
    }

    [Fact]
    public async Task AcceptInvitationAsync_rejects_a_revoked_token()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var inviteeId = await SeedUserAsync(db, "invitee");
        var league = await SeedLeagueAsync(ownerId);
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();
        var created = (await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, null)).Value;
        await invitationService.RevokeInvitationAsync(league.LeagueId, created.InvitationId);

        var result = await invitationService.AcceptInvitationAsync(created.Token, inviteeId);

        Assert.True(result.IsFailure);
        Assert.Equal("invitation_invalid", result.Error.Code);
    }

    [Fact]
    public async Task AcceptInvitationAsync_creates_a_new_row_rather_than_reactivating_a_left_one_per_BR_020()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var ownerId = await SeedUserAsync(db, "owner");
        var inviteeId = await SeedUserAsync(db, "invitee");
        var league = await SeedLeagueAsync(ownerId);
        var invitationService = scope.ServiceProvider.GetRequiredService<IInvitationService>();

        var firstInvitation = (await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, null)).Value;
        var firstMembership = (await invitationService.AcceptInvitationAsync(firstInvitation.Token, inviteeId)).Value;

        // Simulate having left, the same way IT-05's leaveLeague eventually will.
        var leftRow = await db.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == firstMembership.LeagueMembershipId);
        leftRow.Status = MembershipStatus.Left;
        leftRow.LeftAt = _clock.UtcNow;
        await db.SaveChangesAsync();

        var secondInvitation = (await invitationService.CreateInvitationAsync(league.LeagueId, "invitee@example.com", InvitationChannel.Email, null)).Value;
        var result = await invitationService.AcceptInvitationAsync(secondInvitation.Token, inviteeId);

        Assert.True(result.IsSuccess);
        var secondMembership = result.Value;
        Assert.NotEqual(firstMembership.LeagueMembershipId, secondMembership.LeagueMembershipId);
        Assert.Equal(MembershipStatus.Active, secondMembership.Status);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var rowCount = await verifyDb.LeagueMemberships.CountAsync(m => m.LeagueId == league.LeagueId && m.UserId == inviteeId);
        Assert.Equal(2, rowCount);
    }
}
