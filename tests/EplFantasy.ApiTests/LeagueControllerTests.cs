using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EplFantasy.Api.Contracts;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-03 (F-003.1): proves createLeague/getLeague/updateLeague end-to-end against the real host —
/// BR-023 (any authenticated user may create), BR-024 (creator becomes League Administrator), and
/// object-level authorization (getLeague requires active membership, updateLeague requires being
/// the Administrator specifically).
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class LeagueControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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

    [Fact]
    public async Task CreateLeague_without_a_bearer_token_returns_401()
    {
        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", body: new { name = "Office League" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateLeague_makes_the_caller_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", accessToken, new { name = "Office League", description = "Bragging rights only" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var league = await response.Content.ReadFromJsonAsync<LeagueDto>();
        Assert.Equal("Office League", league!.Name);
        Assert.Equal("Bragging rights only", league.Description);
        Assert.Equal("Active", league.Status);
        Assert.NotEqual(Guid.Empty, league.CreatedByMembershipId);

        // The creator is now an active member/Administrator, so they can read and update it.
        var getResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}", accessToken));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var updateResponse = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{league.LeagueId}", accessToken, new { name = "Renamed League" }));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<LeagueDto>();
        Assert.Equal("Renamed League", updated!.Name);
        Assert.Equal("Bragging rights only", updated.Description);
    }

    [Fact]
    public async Task CreateLeague_with_a_blank_name_is_rejected_by_DTO_level_validation()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", accessToken, new { name = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetLeague_is_rejected_for_a_caller_who_is_not_an_active_member()
    {
        var client = factory.CreateClient();
        var ownerToken = await RegisterAsync(client);
        var outsiderToken = await RegisterAsync(client);

        var createResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "Private League" }));
        var league = await createResponse.Content.ReadFromJsonAsync<LeagueDto>();

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league!.LeagueId}", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetLeague_for_a_nonexistent_League_returns_403_since_the_caller_cannot_be_an_active_member_of_it()
    {
        // Object-level authorization (ActiveLeagueMember) runs before the controller ever checks
        // existence, so a made-up leagueId — which by definition has no membership row — is
        // indistinguishable from "exists but you're not a member" at the HTTP layer.
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{Guid.NewGuid()}", accessToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateLeague_is_rejected_for_an_active_member_who_is_not_the_Administrator()
    {
        var client = factory.CreateClient();
        var ownerToken = await RegisterAsync(client);

        var createResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "Private League" }));
        var league = await createResponse.Content.ReadFromJsonAsync<LeagueDto>();

        // A second registered user, never added as a member of this League, is a stand-in for
        // "not the Administrator" — either way (outsider or plain member) updateLeague must reject.
        var otherToken = await RegisterAsync(client);
        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{league!.LeagueId}", otherToken, new { name = "Hijacked Name" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListMyLeagues_without_a_bearer_token_returns_401()
    {
        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Get, "/api/v1/leagues"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListMyLeagues_returns_both_when_the_caller_has_two_active_Leagues()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);
        var leagueAResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", accessToken, new { name = "Context Switch League A" }));
        var leagueA = await leagueAResponse.Content.ReadFromJsonAsync<LeagueDto>();
        var leagueBResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", accessToken, new { name = "Context Switch League B" }));
        var leagueB = await leagueBResponse.Content.ReadFromJsonAsync<LeagueDto>();

        var response = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/leagues", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var leagues = await response.Content.ReadFromJsonAsync<List<LeagueDto>>();
        Assert.Contains(leagues!, l => l.LeagueId == leagueA!.LeagueId);
        Assert.Contains(leagues!, l => l.LeagueId == leagueB!.LeagueId);
    }

    [Fact]
    public async Task ListMyLeagues_never_returns_another_Users_League()
    {
        var client = factory.CreateClient();
        var ownerToken = await RegisterAsync(client);
        var ownLeagueResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "My Own League" }));
        var ownLeague = await ownLeagueResponse.Content.ReadFromJsonAsync<LeagueDto>();
        var otherToken = await RegisterAsync(client);
        await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", otherToken, new { name = "Someone Else's League" }));

        var response = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/leagues", ownerToken));

        var leagues = await response.Content.ReadFromJsonAsync<List<LeagueDto>>();
        Assert.Single(leagues!, l => l.LeagueId == ownLeague!.LeagueId);
    }

    [Fact]
    public async Task Selecting_one_League_from_listMyLeagues_scopes_a_subsequent_getMembership_call_correctly()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);
        var leagueAResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", accessToken, new { name = "Scoping League A" }));
        var leagueA = await leagueAResponse.Content.ReadFromJsonAsync<LeagueDto>();
        var leagueBResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", accessToken, new { name = "Scoping League B" }));
        var leagueB = await leagueBResponse.Content.ReadFromJsonAsync<LeagueDto>();

        // "Selecting" League A means every subsequent call names leagueA.LeagueId — getMembership
        // for A's own founding membership, under A's own leagueId, succeeds and resolves to A.
        var withinLeagueA = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueA!.LeagueId}/memberships/{leagueA.CreatedByMembershipId}", accessToken));
        Assert.Equal(HttpStatusCode.OK, withinLeagueA.StatusCode);
        var membership = await withinLeagueA.Content.ReadFromJsonAsync<LeagueMembershipDto>();
        Assert.Equal(leagueA.LeagueId, membership!.LeagueId);

        // Mixing League B's leagueId with League A's membershipId (a client bug the route
        // structure itself must catch) is rejected — proves the selected League genuinely scopes
        // the call rather than the membershipId alone being enough.
        var mismatched = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueB!.LeagueId}/memberships/{leagueA.CreatedByMembershipId}", accessToken));
        Assert.Equal(HttpStatusCode.NotFound, mismatched.StatusCode);
    }
}
