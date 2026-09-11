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
/// IT-51 (F-003.6, BR-221-BR-223): proves createLeagueMessage (League-Administrator-only) and
/// listLeagueMessages (any active member) end-to-end against the real host — the task breakdown's
/// own required test (a non-Administrator author is rejected) plus the companion cases its own
/// acceptance criteria name: a published message is visible to every active member, and an
/// outsider (not a member of the League at all) cannot view it (BR-222).
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class LeagueMessageControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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
        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = $"Messaging Test League {Guid.NewGuid():N}" }));
        var league = await response.Content.ReadFromJsonAsync<LeagueDto>();
        return (ownerToken, league!);
    }

    /// <summary>Joins a second, non-Administrator member to the League via the real invitation flow.</summary>
    private async Task<string> JoinAsMemberAsync(HttpClient client, string ownerToken, Guid leagueId)
    {
        var createInvitationResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/invitations", ownerToken, new { destination = "other@example.com", channel = "Email" }));
        var invitation = await createInvitationResponse.Content.ReadFromJsonAsync<InvitationDto>();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var rawToken = (await db.Invitations.SingleAsync(i => i.InvitationId == invitation!.InvitationId)).Token;

        var memberToken = await RegisterAsync(client);
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/invitations/{rawToken}/accept", memberToken));

        return memberToken;
    }

    [Fact]
    public async Task CreateLeagueMessage_is_rejected_for_a_caller_who_is_not_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var memberToken = await JoinAsMemberAsync(client, ownerToken, league.LeagueId);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/messages", memberToken, new { body = "Welcome to the league!" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateLeagueMessage_then_listLeagueMessages_round_trip_is_visible_to_every_active_member()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var memberToken = await JoinAsMemberAsync(client, ownerToken, league.LeagueId);

        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/messages", ownerToken, new { body = "Draft starts Saturday at noon." }));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<LeagueMessageDto>();
        Assert.Equal(league.LeagueId, created!.LeagueId);
        Assert.Equal("Draft starts Saturday at noon.", created.Body);

        var listResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/messages", memberToken));

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var messages = await listResponse.Content.ReadFromJsonAsync<List<LeagueMessageDto>>();
        Assert.Single(messages!, m => m.LeagueMessageId == created.LeagueMessageId);
    }

    [Fact]
    public async Task ListLeagueMessages_is_rejected_for_an_outsider_who_is_not_a_member_of_the_League()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/messages", ownerToken, new { body = "Members only." }));
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/messages", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateLeagueMessage_with_an_empty_body_is_rejected_by_DTO_level_validation()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/messages", ownerToken, new { body = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
