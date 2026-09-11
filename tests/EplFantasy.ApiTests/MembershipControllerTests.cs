using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-05 (F-003.3): proves listMemberships/getMembership (any active member) and leaveLeague
/// (self-service or League-Administrator-on-behalf-of, with the sole-Administrator 409 guard)
/// end-to-end against the real host.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class MembershipControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
{
    private const string StrongPassword = "correct-horse-battery-staple-97!";

    private async Task<string> RegisterAsync(HttpClient client)
    {
        var username = $"u{Guid.NewGuid():N}"[..16];
        var email = $"{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
        return tokens!.AccessToken;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? accessToken = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    private async Task<(string OwnerToken, LeagueDto League)> CreateLeagueAsync(HttpClient client)
    {
        var ownerToken = await RegisterAsync(client);
        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "Membership Test League" }));
        var league = await response.Content.ReadFromJsonAsync<LeagueDto>();
        return (ownerToken, league!);
    }

    private async Task<(string MemberToken, LeagueMembershipDto Membership)> InviteAndAcceptAsync(HttpClient client, string ownerToken, Guid leagueId)
    {
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/invitations", ownerToken, new { destination = "invitee@example.com", channel = "Email" }));
        var invitation = await createResponse.Content.ReadFromJsonAsync<InvitationDto>();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var rawToken = (await db.Invitations.SingleAsync(i => i.InvitationId == invitation!.InvitationId)).Token;

        var memberToken = await RegisterAsync(client);
        var acceptResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/invitations/{rawToken}/accept", memberToken));
        var membership = await acceptResponse.Content.ReadFromJsonAsync<LeagueMembershipDto>();

        return (memberToken, membership!);
    }

    [Fact]
    public async Task ListMemberships_returns_the_administrator_and_every_accepted_member()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        await InviteAndAcceptAsync(client, ownerToken, league.LeagueId);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/memberships", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var memberships = await response.Content.ReadFromJsonAsync<List<LeagueMembershipDto>>();
        Assert.Equal(2, memberships!.Count);
        Assert.Single(memberships, m => m.IsAdministrator);
        Assert.All(memberships, m => Assert.NotEmpty(m.Username));
    }

    [Fact]
    public async Task ListMemberships_is_rejected_for_a_caller_who_is_not_a_member()
    {
        var client = factory.CreateClient();
        var (_, league) = await CreateLeagueAsync(client);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/memberships", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetMembership_returns_the_composed_shape_including_effectiveIconId()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/memberships/{league.CreatedByMembershipId}", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<LeagueMembershipDto>();
        Assert.Equal(league.CreatedByMembershipId, membership!.LeagueMembershipId);
        Assert.True(membership.IsAdministrator);
        Assert.NotEqual(Guid.Empty, membership.EffectiveIconId);
    }

    [Fact]
    public async Task LeaveLeague_lets_a_member_leave_their_own_membership()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var (memberToken, membership) = await InviteAndAcceptAsync(client, ownerToken, league.LeagueId);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/memberships/{membership.LeagueMembershipId}/leave", memberToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/memberships/{membership.LeagueMembershipId}", ownerToken));
        var updated = await getResponse.Content.ReadFromJsonAsync<LeagueMembershipDto>();
        Assert.Equal("Left", updated!.Status);
    }

    [Fact]
    public async Task LeaveLeague_lets_the_Administrator_remove_another_member()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var (_, membership) = await InviteAndAcceptAsync(client, ownerToken, league.LeagueId);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/memberships/{membership.LeagueMembershipId}/leave", ownerToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task LeaveLeague_is_rejected_for_a_caller_who_is_neither_the_owner_nor_the_Administrator()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var (_, membership) = await InviteAndAcceptAsync(client, ownerToken, league.LeagueId);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/memberships/{membership.LeagueMembershipId}/leave", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LeaveLeague_returns_409_when_the_League_Administrator_tries_to_leave_their_own_membership()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/memberships/{league.CreatedByMembershipId}/leave", ownerToken));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("sole_administrator_cannot_leave", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task SetLeagueIcon_lets_a_member_set_their_own_override_and_getMembership_reflects_it()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var icon = await db.ProfileIcons.Where(i => i.IsActive).FirstAsync();

        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{league.LeagueId}/memberships/{league.CreatedByMembershipId}/icon", ownerToken, new { profileIconId = icon.ProfileIconId }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<LeagueMembershipDto>();
        Assert.Equal(icon.ProfileIconId, updated!.LeagueIconId);
        Assert.Equal(icon.ProfileIconId, updated.EffectiveIconId);

        var getResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/memberships/{league.CreatedByMembershipId}", ownerToken));
        var fetched = await getResponse.Content.ReadFromJsonAsync<LeagueMembershipDto>();
        Assert.Equal(icon.ProfileIconId, fetched!.LeagueIconId);
    }

    [Fact]
    public async Task ClearLeagueIcon_reverts_to_the_global_default()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var icon = await db.ProfileIcons.Where(i => i.IsActive).FirstAsync();
        await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{league.LeagueId}/memberships/{league.CreatedByMembershipId}/icon", ownerToken, new { profileIconId = icon.ProfileIconId }));

        var response = await client.SendAsync(Request(HttpMethod.Delete, $"/api/v1/leagues/{league.LeagueId}/memberships/{league.CreatedByMembershipId}/icon", ownerToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var getResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/memberships/{league.CreatedByMembershipId}", ownerToken));
        var fetched = await getResponse.Content.ReadFromJsonAsync<LeagueMembershipDto>();
        Assert.Null(fetched!.LeagueIconId);
        Assert.NotEqual(Guid.Empty, fetched.EffectiveIconId); // reverted to the global default, not cleared entirely
    }

    [Fact]
    public async Task SetLeagueIcon_is_rejected_for_a_caller_who_does_not_own_the_membership_even_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var (memberToken, membership) = await InviteAndAcceptAsync(client, ownerToken, league.LeagueId);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var icon = await db.ProfileIcons.Where(i => i.IsActive).FirstAsync();

        // The Administrator has no on-behalf-of fallback here — only the member themselves may
        // set their own icon (unlike leaveLeague's MembershipOwnerOrLeagueAdministrator policy).
        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{league.LeagueId}/memberships/{membership.LeagueMembershipId}/icon", ownerToken, new { profileIconId = icon.ProfileIconId }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The owning member themselves can, though.
        var ownProfileResponse = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{league.LeagueId}/memberships/{membership.LeagueMembershipId}/icon", memberToken, new { profileIconId = icon.ProfileIconId }));
        Assert.Equal(HttpStatusCode.OK, ownProfileResponse.StatusCode);
    }

    [Fact]
    public async Task SetLeagueIcon_with_an_unknown_profileIconId_returns_400()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{league.LeagueId}/memberships/{league.CreatedByMembershipId}/icon", ownerToken, new { profileIconId = Guid.NewGuid() }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("profile_icon_not_active", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task SetLeagueIcon_in_one_League_never_changes_the_same_Users_icon_in_another_League()
    {
        var client = factory.CreateClient();
        var ownerToken = await RegisterAsync(client);
        var leagueAResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "League A" }));
        var leagueA = await leagueAResponse.Content.ReadFromJsonAsync<LeagueDto>();
        var leagueBResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "League B" }));
        var leagueB = await leagueBResponse.Content.ReadFromJsonAsync<LeagueDto>();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var icons = await db.ProfileIcons.Where(i => i.IsActive).OrderBy(i => i.SortOrder).Take(2).ToListAsync();

        await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{leagueB!.LeagueId}/memberships/{leagueB.CreatedByMembershipId}/icon", ownerToken, new { profileIconId = icons[1].ProfileIconId }));

        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{leagueA!.LeagueId}/memberships/{leagueA.CreatedByMembershipId}/icon", ownerToken, new { profileIconId = icons[0].ProfileIconId }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var membershipBResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueB.LeagueId}/memberships/{leagueB.CreatedByMembershipId}", ownerToken));
        var membershipB = await membershipBResponse.Content.ReadFromJsonAsync<LeagueMembershipDto>();
        Assert.Equal(icons[1].ProfileIconId, membershipB!.LeagueIconId); // BR-009: untouched by League A's change
    }

    [Fact]
    public async Task GetNotificationPreferences_returns_the_full_disabled_by_default_set()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/memberships/{league.CreatedByMembershipId}/notification-preferences", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preferences = await response.Content.ReadFromJsonAsync<List<NotificationPreferenceDto>>();
        Assert.Equal(6, preferences!.Count);
        Assert.All(preferences, p => Assert.False(p.Enabled));
        Assert.All(preferences, p => Assert.Equal(league.CreatedByMembershipId, p.LeagueMembershipId));
    }

    [Fact]
    public async Task UpdateNotificationPreferences_applies_the_change_and_is_reflected_by_a_subsequent_get()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var path = $"/api/v1/leagues/{league.LeagueId}/memberships/{league.CreatedByMembershipId}/notification-preferences";

        var updateResponse = await client.SendAsync(Request(HttpMethod.Put, path, ownerToken, new[]
        {
            new { leagueMembershipId = league.CreatedByMembershipId, eventType = "WeeklyScore", channel = "Email", enabled = true },
        }));

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<List<NotificationPreferenceDto>>();
        Assert.Contains(updated!, p => p.EventType == "WeeklyScore" && p.Channel == "Email" && p.Enabled);
        Assert.Equal(5, updated!.Count(p => !p.Enabled)); // every other combination is untouched.

        var getResponse = await client.SendAsync(Request(HttpMethod.Get, path, ownerToken));
        var fetched = await getResponse.Content.ReadFromJsonAsync<List<NotificationPreferenceDto>>();
        Assert.Contains(fetched!, p => p.EventType == "WeeklyScore" && p.Channel == "Email" && p.Enabled);
    }

    [Fact]
    public async Task GetNotificationPreferences_is_rejected_for_a_caller_who_does_not_own_the_membership()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/memberships/{league.CreatedByMembershipId}/notification-preferences", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateNotificationPreferences_in_one_League_never_changes_the_same_Users_preferences_in_another_League()
    {
        var client = factory.CreateClient();
        var ownerToken = await RegisterAsync(client);
        var leagueAResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "Notif League A" }));
        var leagueA = await leagueAResponse.Content.ReadFromJsonAsync<LeagueDto>();
        var leagueBResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "Notif League B" }));
        var leagueB = await leagueBResponse.Content.ReadFromJsonAsync<LeagueDto>();

        await client.SendAsync(Request(
            HttpMethod.Put,
            $"/api/v1/leagues/{leagueA!.LeagueId}/memberships/{leagueA.CreatedByMembershipId}/notification-preferences",
            ownerToken,
            new[] { new { leagueMembershipId = leagueA.CreatedByMembershipId, eventType = "WeeklyScore", channel = "Email", enabled = true } }));

        var leagueBResponse2 = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueB!.LeagueId}/memberships/{leagueB.CreatedByMembershipId}/notification-preferences", ownerToken));
        var leagueBPreferences = await leagueBResponse2.Content.ReadFromJsonAsync<List<NotificationPreferenceDto>>();
        Assert.All(leagueBPreferences!, p => Assert.False(p.Enabled)); // BR-338: untouched by League A's own change.
    }
}
