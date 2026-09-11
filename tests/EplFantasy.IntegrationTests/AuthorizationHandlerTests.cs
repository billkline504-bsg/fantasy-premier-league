using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using EplFantasy.Api.Authentication;
using EplFantasy.Drafts;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.Leagues;
using EplFantasy.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EplFantasy.IntegrationTests;

/// <summary>
/// Proves the IT-F06 authorization handlers actually grant/deny against real LeagueMembership/User
/// rows — constructing a genuine AuthorizationHandlerContext the same way ASP.NET Core's
/// AuthorizationMiddleware does for a controller [Authorize(Policy = ...)] check (HttpContext as
/// the resource, route values carrying the resource id), rather than calling handler internals
/// directly.
/// </summary>
public class AuthorizationHandlerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
    private EplFantasyDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyHandWrittenMigrationsAsync(_container.GetConnectionString());

        _db = new EplFantasyDbContext(TestDbContextOptionsFactory.Create(_container.GetConnectionString()));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
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
        foreach (var file in Directory.GetFiles(migrationsDir, "V*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(file), connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class StubHttpContextAccessor(HttpContext httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    private async Task<(Guid LeagueId, Guid AdminUserId, Guid MemberUserId, Guid OutsiderUserId, Guid FantasyTeamId)> SeedLeagueAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var adminUser = new User { UserId = Guid.NewGuid(), Username = $"authz_admin_{suffix}", Email = $"authz_admin_{suffix}@example.com", PasswordHash = "h", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var memberUser = new User { UserId = Guid.NewGuid(), Username = $"authz_member_{suffix}", Email = $"authz_member_{suffix}@example.com", PasswordHash = "h", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var outsiderUser = new User { UserId = Guid.NewGuid(), Username = $"authz_outsider_{suffix}", Email = $"authz_outsider_{suffix}@example.com", PasswordHash = "h", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        _db.Users.AddRange(adminUser, memberUser, outsiderUser);

        var leagueId = Guid.NewGuid();
        var adminMembershipId = Guid.NewGuid();
        var memberMembershipId = Guid.NewGuid();
        _db.Leagues.Add(new League { LeagueId = leagueId, Name = "Authz Smoke League", CreatedByMembershipId = adminMembershipId, CreatedAt = DateTimeOffset.UtcNow });
        _db.LeagueMemberships.Add(new LeagueMembership { LeagueMembershipId = adminMembershipId, LeagueId = leagueId, UserId = adminUser.UserId, IsAdministrator = true, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow });
        _db.LeagueMemberships.Add(new LeagueMembership { LeagueMembershipId = memberMembershipId, LeagueId = leagueId, UserId = memberUser.UserId, IsAdministrator = false, Status = MembershipStatus.Active, JoinedAt = DateTimeOffset.UtcNow });

        var eplSeasonId = $"authz-{suffix}";
        _db.EplSeasons.Add(new PlayerData.EplSeason { EplSeasonIdentifier = eplSeasonId });
        var seasonId = Guid.NewGuid();
        _db.Seasons.Add(new Season { SeasonId = seasonId, LeagueId = leagueId, EplSeasonIdentifier = eplSeasonId, StartDate = DateOnly.FromDateTime(DateTime.UtcNow) });

        var fantasyTeamId = Guid.NewGuid();
        _db.FantasyTeams.Add(new FantasyTeam { FantasyTeamId = fantasyTeamId, LeagueMembershipId = memberMembershipId, SeasonId = seasonId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        await _db.SaveChangesAsync();

        return (leagueId, adminUser.UserId, memberUser.UserId, outsiderUser.UserId, fantasyTeamId);
    }

    private async Task<Guid> SeedDraftForFantasyTeamAsync(Guid fantasyTeamId)
    {
        var seasonId = await _db.FantasyTeams.Where(t => t.FantasyTeamId == fantasyTeamId).Select(t => t.SeasonId).SingleAsync();
        var draft = Draft.CreateInitial(Guid.NewGuid(), seasonId, [fantasyTeamId, Guid.NewGuid()], timerSeconds: 300, DateTimeOffset.UtcNow);
        _db.Drafts.Add(draft);
        await _db.SaveChangesAsync();

        return draft.DraftId;
    }

    [Fact]
    public async Task ActiveLeagueMember_succeeds_for_an_active_member_and_fails_for_an_outsider()
    {
        var (leagueId, _, memberUserId, outsiderUserId, _) = await SeedLeagueAsync();

        var memberHttpContext = new DefaultHttpContext();
        memberHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, memberUserId.ToString())], "TestAuth"));
        memberHttpContext.Request.RouteValues["leagueId"] = leagueId.ToString();
        var memberAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(memberHttpContext));
        var memberContext = new AuthorizationHandlerContext([new ActiveLeagueMemberRequirement()], memberHttpContext.User, memberHttpContext);

        await new ActiveLeagueMemberAuthorizationHandler(_db, memberAccessor).HandleAsync(memberContext);
        Assert.True(memberContext.HasSucceeded);

        var outsiderHttpContext = new DefaultHttpContext();
        outsiderHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, outsiderUserId.ToString())], "TestAuth"));
        outsiderHttpContext.Request.RouteValues["leagueId"] = leagueId.ToString();
        var outsiderAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(outsiderHttpContext));
        var outsiderContext = new AuthorizationHandlerContext([new ActiveLeagueMemberRequirement()], outsiderHttpContext.User, outsiderHttpContext);

        await new ActiveLeagueMemberAuthorizationHandler(_db, outsiderAccessor).HandleAsync(outsiderContext);
        Assert.False(outsiderContext.HasSucceeded);
    }

    [Fact]
    public async Task ActiveLeagueMember_fails_for_an_unauthenticated_caller()
    {
        var (leagueId, _, _, _, _) = await SeedLeagueAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["leagueId"] = leagueId.ToString();
        var accessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(httpContext));
        var context = new AuthorizationHandlerContext([new ActiveLeagueMemberRequirement()], httpContext.User, httpContext);

        await new ActiveLeagueMemberAuthorizationHandler(_db, accessor).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task LeagueAdministrator_succeeds_for_the_administrator_and_fails_for_a_plain_member()
    {
        var (leagueId, adminUserId, memberUserId, _, _) = await SeedLeagueAsync();

        var adminHttpContext = new DefaultHttpContext();
        adminHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, adminUserId.ToString())], "TestAuth"));
        adminHttpContext.Request.RouteValues["leagueId"] = leagueId.ToString();
        var adminAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(adminHttpContext));
        var adminContext = new AuthorizationHandlerContext([new LeagueAdministratorRequirement()], adminHttpContext.User, adminHttpContext);

        await new LeagueAdministratorAuthorizationHandler(_db, adminAccessor).HandleAsync(adminContext);
        Assert.True(adminContext.HasSucceeded);

        var memberHttpContext = new DefaultHttpContext();
        memberHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, memberUserId.ToString())], "TestAuth"));
        memberHttpContext.Request.RouteValues["leagueId"] = leagueId.ToString();
        var memberAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(memberHttpContext));
        var memberContext = new AuthorizationHandlerContext([new LeagueAdministratorRequirement()], memberHttpContext.User, memberHttpContext);

        await new LeagueAdministratorAuthorizationHandler(_db, memberAccessor).HandleAsync(memberContext);
        Assert.False(memberContext.HasSucceeded);
    }

    [Fact]
    public async Task SystemAdministrator_ignores_league_administrator_status_entirely()
    {
        // ADR-007: SystemAdministrator is platform-level and unrelated to League Administrator —
        // a League Administrator (even of many Leagues) must NOT satisfy this policy.
        var (_, adminUserId, _, _, _) = await SeedLeagueAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, adminUserId.ToString())], "TestAuth"));
        var accessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(httpContext));
        var context = new AuthorizationHandlerContext([new SystemAdministratorRequirement()], httpContext.User, httpContext);

        await new SystemAdministratorAuthorizationHandler(_db, accessor).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task SystemAdministrator_succeeds_only_for_a_flagged_user()
    {
        var user = new User { UserId = Guid.NewGuid(), Username = $"sysadmin_{Guid.NewGuid():N}", Email = $"sysadmin_{Guid.NewGuid():N}@example.com", PasswordHash = "h", IsSystemAdministrator = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, user.UserId.ToString())], "TestAuth"));
        var accessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(httpContext));
        var context = new AuthorizationHandlerContext([new SystemAdministratorRequirement()], httpContext.User, httpContext);

        await new SystemAdministratorAuthorizationHandler(_db, accessor).HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task FantasyTeamOwner_succeeds_for_the_owning_member_and_fails_for_another_member()
    {
        var (_, adminUserId, memberUserId, _, fantasyTeamId) = await SeedLeagueAsync();

        var ownerHttpContext = new DefaultHttpContext();
        ownerHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, memberUserId.ToString())], "TestAuth"));
        ownerHttpContext.Request.RouteValues["fantasyTeamId"] = fantasyTeamId.ToString();
        var ownerAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(ownerHttpContext));
        var ownerContext = new AuthorizationHandlerContext([new FantasyTeamOwnerRequirement()], ownerHttpContext.User, ownerHttpContext);

        await new FantasyTeamOwnerAuthorizationHandler(_db, ownerAccessor).HandleAsync(ownerContext);
        Assert.True(ownerContext.HasSucceeded);

        // The League Administrator does not automatically own another member's FantasyTeam.
        var adminHttpContext = new DefaultHttpContext();
        adminHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, adminUserId.ToString())], "TestAuth"));
        adminHttpContext.Request.RouteValues["fantasyTeamId"] = fantasyTeamId.ToString();
        var adminAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(adminHttpContext));
        var adminContext = new AuthorizationHandlerContext([new FantasyTeamOwnerRequirement()], adminHttpContext.User, adminHttpContext);

        await new FantasyTeamOwnerAuthorizationHandler(_db, adminAccessor).HandleAsync(adminContext);
        Assert.False(adminContext.HasSucceeded);
    }

    [Fact]
    public async Task MembershipOwnerOrLeagueAdministrator_succeeds_for_the_owner_and_for_the_administrator_but_not_for_an_outsider()
    {
        var (leagueId, adminUserId, memberUserId, outsiderUserId, _) = await SeedLeagueAsync();
        var memberMembershipId = await _db.LeagueMemberships.Where(m => m.LeagueId == leagueId && m.UserId == memberUserId).Select(m => m.LeagueMembershipId).SingleAsync();

        // The member owns their own membership.
        var ownerHttpContext = new DefaultHttpContext();
        ownerHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, memberUserId.ToString())], "TestAuth"));
        ownerHttpContext.Request.RouteValues["leagueId"] = leagueId.ToString();
        ownerHttpContext.Request.RouteValues["membershipId"] = memberMembershipId.ToString();
        var ownerAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(ownerHttpContext));
        var ownerContext = new AuthorizationHandlerContext([new MembershipOwnerOrLeagueAdministratorRequirement()], ownerHttpContext.User, ownerHttpContext);

        await new MembershipOwnerOrLeagueAdministratorAuthorizationHandler(_db, ownerAccessor).HandleAsync(ownerContext);
        Assert.True(ownerContext.HasSucceeded);

        // The League Administrator may act on another member's membership.
        var adminHttpContext = new DefaultHttpContext();
        adminHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, adminUserId.ToString())], "TestAuth"));
        adminHttpContext.Request.RouteValues["leagueId"] = leagueId.ToString();
        adminHttpContext.Request.RouteValues["membershipId"] = memberMembershipId.ToString();
        var adminAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(adminHttpContext));
        var adminContext = new AuthorizationHandlerContext([new MembershipOwnerOrLeagueAdministratorRequirement()], adminHttpContext.User, adminHttpContext);

        await new MembershipOwnerOrLeagueAdministratorAuthorizationHandler(_db, adminAccessor).HandleAsync(adminContext);
        Assert.True(adminContext.HasSucceeded);

        // An outsider — neither the owner nor the Administrator — is rejected.
        var outsiderHttpContext = new DefaultHttpContext();
        outsiderHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, outsiderUserId.ToString())], "TestAuth"));
        outsiderHttpContext.Request.RouteValues["leagueId"] = leagueId.ToString();
        outsiderHttpContext.Request.RouteValues["membershipId"] = memberMembershipId.ToString();
        var outsiderAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(outsiderHttpContext));
        var outsiderContext = new AuthorizationHandlerContext([new MembershipOwnerOrLeagueAdministratorRequirement()], outsiderHttpContext.User, outsiderHttpContext);

        await new MembershipOwnerOrLeagueAdministratorAuthorizationHandler(_db, outsiderAccessor).HandleAsync(outsiderContext);
        Assert.False(outsiderContext.HasSucceeded);
    }

    [Fact]
    public async Task FantasyTeamOwnerOrActiveLeagueMember_succeeds_for_the_owner_and_for_an_active_member_but_not_for_an_outsider()
    {
        var (leagueId, adminUserId, memberUserId, outsiderUserId, fantasyTeamId) = await SeedLeagueAsync();

        // The owning member.
        var ownerHttpContext = new DefaultHttpContext();
        ownerHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, memberUserId.ToString())], "TestAuth"));
        ownerHttpContext.Request.RouteValues["leagueId"] = leagueId.ToString();
        ownerHttpContext.Request.RouteValues["fantasyTeamId"] = fantasyTeamId.ToString();
        var ownerAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(ownerHttpContext));
        var ownerContext = new AuthorizationHandlerContext([new FantasyTeamOwnerOrActiveLeagueMemberRequirement()], ownerHttpContext.User, ownerHttpContext);

        await new FantasyTeamOwnerOrActiveLeagueMemberAuthorizationHandler(_db, ownerAccessor).HandleAsync(ownerContext);
        Assert.True(ownerContext.HasSucceeded);

        // The League Administrator — an active member, but not the owner — may still read it.
        var adminHttpContext = new DefaultHttpContext();
        adminHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, adminUserId.ToString())], "TestAuth"));
        adminHttpContext.Request.RouteValues["leagueId"] = leagueId.ToString();
        adminHttpContext.Request.RouteValues["fantasyTeamId"] = fantasyTeamId.ToString();
        var adminAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(adminHttpContext));
        var adminContext = new AuthorizationHandlerContext([new FantasyTeamOwnerOrActiveLeagueMemberRequirement()], adminHttpContext.User, adminHttpContext);

        await new FantasyTeamOwnerOrActiveLeagueMemberAuthorizationHandler(_db, adminAccessor).HandleAsync(adminContext);
        Assert.True(adminContext.HasSucceeded);

        // An outsider — neither the owner nor a member of the League — is rejected.
        var outsiderHttpContext = new DefaultHttpContext();
        outsiderHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, outsiderUserId.ToString())], "TestAuth"));
        outsiderHttpContext.Request.RouteValues["leagueId"] = leagueId.ToString();
        outsiderHttpContext.Request.RouteValues["fantasyTeamId"] = fantasyTeamId.ToString();
        var outsiderAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(outsiderHttpContext));
        var outsiderContext = new AuthorizationHandlerContext([new FantasyTeamOwnerOrActiveLeagueMemberRequirement()], outsiderHttpContext.User, outsiderHttpContext);

        await new FantasyTeamOwnerOrActiveLeagueMemberAuthorizationHandler(_db, outsiderAccessor).HandleAsync(outsiderContext);
        Assert.False(outsiderContext.HasSucceeded);
    }

    [Fact]
    public async Task ActiveLeagueMember_fails_when_the_route_value_is_missing_or_not_a_guid()
    {
        var (_, _, memberUserId, _, _) = await SeedLeagueAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, memberUserId.ToString())], "TestAuth"));
        httpContext.Request.RouteValues["leagueId"] = "not-a-guid";
        var accessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(httpContext));
        var context = new AuthorizationHandlerContext([new ActiveLeagueMemberRequirement()], httpContext.User, httpContext);

        await new ActiveLeagueMemberAuthorizationHandler(_db, accessor).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    /// <summary>IT-26 (F-005.3): the breakdown's own "unit (extension by a non-Administrator rejected)" — DraftLeagueAdministratorRequirement resolves its League indirectly, via Draft.SeasonId → Season.LeagueId, since the route names only {draftId}.</summary>
    [Fact]
    public async Task DraftLeagueAdministrator_succeeds_for_the_administrator_and_fails_for_a_plain_member()
    {
        var (_, adminUserId, memberUserId, outsiderUserId, fantasyTeamId) = await SeedLeagueAsync();
        var draftId = await SeedDraftForFantasyTeamAsync(fantasyTeamId);

        var adminHttpContext = new DefaultHttpContext();
        adminHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, adminUserId.ToString())], "TestAuth"));
        adminHttpContext.Request.RouteValues["draftId"] = draftId.ToString();
        var adminAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(adminHttpContext));
        var adminContext = new AuthorizationHandlerContext([new DraftLeagueAdministratorRequirement()], adminHttpContext.User, adminHttpContext);

        await new DraftLeagueAdministratorAuthorizationHandler(_db, adminAccessor).HandleAsync(adminContext);
        Assert.True(adminContext.HasSucceeded);

        // A plain (non-Administrator) member of the same League is rejected.
        var memberHttpContext = new DefaultHttpContext();
        memberHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, memberUserId.ToString())], "TestAuth"));
        memberHttpContext.Request.RouteValues["draftId"] = draftId.ToString();
        var memberAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(memberHttpContext));
        var memberContext = new AuthorizationHandlerContext([new DraftLeagueAdministratorRequirement()], memberHttpContext.User, memberHttpContext);

        await new DraftLeagueAdministratorAuthorizationHandler(_db, memberAccessor).HandleAsync(memberContext);
        Assert.False(memberContext.HasSucceeded);

        // An outsider — not a member of the League at all — is also rejected.
        var outsiderHttpContext = new DefaultHttpContext();
        outsiderHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, outsiderUserId.ToString())], "TestAuth"));
        outsiderHttpContext.Request.RouteValues["draftId"] = draftId.ToString();
        var outsiderAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(outsiderHttpContext));
        var outsiderContext = new AuthorizationHandlerContext([new DraftLeagueAdministratorRequirement()], outsiderHttpContext.User, outsiderHttpContext);

        await new DraftLeagueAdministratorAuthorizationHandler(_db, outsiderAccessor).HandleAsync(outsiderContext);
        Assert.False(outsiderContext.HasSucceeded);
    }

    /// <summary>IT-28 (F-005.5): getDraft/listDraftSelections/getDraftPlayerPool's own x-authorization — unlike DraftLeagueAdministrator, a plain (non-Administrator) member succeeds too; only a non-member outsider is rejected.</summary>
    [Fact]
    public async Task DraftLeagueMember_succeeds_for_the_administrator_and_a_plain_member_and_fails_for_an_outsider()
    {
        var (_, adminUserId, memberUserId, outsiderUserId, fantasyTeamId) = await SeedLeagueAsync();
        var draftId = await SeedDraftForFantasyTeamAsync(fantasyTeamId);

        var adminHttpContext = new DefaultHttpContext();
        adminHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, adminUserId.ToString())], "TestAuth"));
        adminHttpContext.Request.RouteValues["draftId"] = draftId.ToString();
        var adminAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(adminHttpContext));
        var adminContext = new AuthorizationHandlerContext([new DraftLeagueMemberRequirement()], adminHttpContext.User, adminHttpContext);

        await new DraftLeagueMemberAuthorizationHandler(_db, adminAccessor).HandleAsync(adminContext);
        Assert.True(adminContext.HasSucceeded);

        // A plain (non-Administrator) member of the same League also succeeds.
        var memberHttpContext = new DefaultHttpContext();
        memberHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, memberUserId.ToString())], "TestAuth"));
        memberHttpContext.Request.RouteValues["draftId"] = draftId.ToString();
        var memberAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(memberHttpContext));
        var memberContext = new AuthorizationHandlerContext([new DraftLeagueMemberRequirement()], memberHttpContext.User, memberHttpContext);

        await new DraftLeagueMemberAuthorizationHandler(_db, memberAccessor).HandleAsync(memberContext);
        Assert.True(memberContext.HasSucceeded);

        // An outsider — not a member of the League at all — is rejected.
        var outsiderHttpContext = new DefaultHttpContext();
        outsiderHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, outsiderUserId.ToString())], "TestAuth"));
        outsiderHttpContext.Request.RouteValues["draftId"] = draftId.ToString();
        var outsiderAccessor = new HttpContextCurrentUserAccessor(new StubHttpContextAccessor(outsiderHttpContext));
        var outsiderContext = new AuthorizationHandlerContext([new DraftLeagueMemberRequirement()], outsiderHttpContext.User, outsiderHttpContext);

        await new DraftLeagueMemberAuthorizationHandler(_db, outsiderAccessor).HandleAsync(outsiderContext);
        Assert.False(outsiderContext.HasSucceeded);
    }
}
