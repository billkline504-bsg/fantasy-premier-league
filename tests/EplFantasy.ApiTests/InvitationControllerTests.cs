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
/// IT-04 (F-003.2): proves createInvitation/listInvitations/revokeInvitation/acceptInvitation
/// end-to-end against the real host — issue/list/revoke are League-Administrator-only,
/// acceptInvitation's security boundary is the token itself (any authenticated caller may redeem
/// a valid one), and an invalid token fails with 410 (BR-029) without revealing why.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class InvitationControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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
        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "Invite Test League" }));
        var league = await response.Content.ReadFromJsonAsync<LeagueDto>();
        return (ownerToken, league!);
    }

    [Fact]
    public async Task CreateInvitation_is_rejected_for_a_caller_who_is_not_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var (_, league) = await CreateLeagueAsync(client);
        var otherToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/invitations", otherToken, new { destination = "invitee@example.com", channel = "Email" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateInvitation_then_acceptInvitation_creates_an_active_non_administrator_membership()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);

        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/invitations", ownerToken, new { destination = "invitee@example.com", channel = "Email" }));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var invitation = await createResponse.Content.ReadFromJsonAsync<InvitationDto>();
        Assert.Equal("Pending", invitation!.Status);
        Assert.Equal(league.LeagueId, invitation.LeagueId);

        // The token itself is the only credential this endpoint needs beyond a real session —
        // acceptInvitation is reachable by any authenticated user holding the raw invitation object,
        // simulating "clicked the link and is logged in as the invitee."
        var inviteeToken = await RegisterAsync(client);
        var invitationsListResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/invitations", ownerToken));
        var invitations = await invitationsListResponse.Content.ReadFromJsonAsync<List<InvitationDto>>();
        var token = await GetRawTokenAsync(league.LeagueId, invitation.InvitationId);

        var acceptResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/invitations/{token}/accept", inviteeToken));

        Assert.Equal(HttpStatusCode.OK, acceptResponse.StatusCode);
        var membership = await acceptResponse.Content.ReadFromJsonAsync<LeagueMembershipDto>();
        Assert.Equal(league.LeagueId, membership!.LeagueId);
        Assert.False(membership.IsAdministrator);
        Assert.Equal("Active", membership.Status);
        Assert.NotEqual(Guid.Empty, membership.EffectiveIconId);
        Assert.NotEmpty(membership.Username);
        Assert.Single(invitations!);
    }

    [Fact]
    public async Task AcceptInvitation_with_an_unknown_token_returns_410()
    {
        var client = factory.CreateClient();
        var token = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/invitations/this-token-was-never-issued/accept", token));

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task AcceptInvitation_without_a_bearer_token_returns_401()
    {
        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Post, "/api/v1/invitations/whatever/accept"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RevokeInvitation_then_acceptInvitation_returns_410()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/invitations", ownerToken, new { destination = "invitee@example.com", channel = "Email" }));
        var invitation = await createResponse.Content.ReadFromJsonAsync<InvitationDto>();
        var rawToken = await GetRawTokenAsync(league.LeagueId, invitation!.InvitationId);

        var revokeResponse = await client.SendAsync(Request(HttpMethod.Delete, $"/api/v1/leagues/{league.LeagueId}/invitations/{invitation.InvitationId}", ownerToken));
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var inviteeToken = await RegisterAsync(client);
        var acceptResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/invitations/{rawToken}/accept", inviteeToken));

        Assert.Equal(HttpStatusCode.Gone, acceptResponse.StatusCode);
    }

    [Fact]
    public async Task RevokeInvitation_is_rejected_for_a_caller_who_is_not_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/invitations", ownerToken, new { destination = "invitee@example.com", channel = "Email" }));
        var invitation = await createResponse.Content.ReadFromJsonAsync<InvitationDto>();
        var otherToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Delete, $"/api/v1/leagues/{league.LeagueId}/invitations/{invitation!.InvitationId}", otherToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // The raw token is never exposed via listInvitations/Invitation (BR-028's delivery channel is
    // out of scope for IT-04) — this test-only helper reaches into the database directly to get it,
    // simulating "the invitee received the link via email/SMS."
    private async Task<string> GetRawTokenAsync(Guid leagueId, Guid invitationId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var invitation = await db.Invitations.SingleAsync(i => i.InvitationId == invitationId && i.LeagueId == leagueId);
        return invitation.Token;
    }
}
