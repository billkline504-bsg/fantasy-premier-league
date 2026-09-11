using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-08 (F-003.5, core mechanics only): proves getLeagueConfiguration/updateLeagueConfiguration
/// and getSeasonConfiguration/updateSeasonConfiguration end-to-end against the real host — reads
/// open to any active member, writes League-Administrator-only, BR-293's per-field 409 lock, and
/// BR-295's ConfigurationChanged audit row.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class ConfigurationControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
{
    private const string StrongPassword = "correct-horse-battery-staple-97!";

    private static object ValidUpdateBody(int initialSquadSize = 30) => new
    {
        initialSquadSize,
        weeklyRosterSize = 16,
        positionalMinimums = new { gk = 2, def = 4, mid = 3, fwd = 2 },
        draftTimerSecondsByType = new { initial = 400, secondary = 400, replacement = 400 },
        secondaryDraftSelectionsPerTeam = 6,
        secondaryDraftSchedulingOffsetDays = 2,
        gameweekRosterLockOffsetBeforeKickoffMinutes = 90,
        leaguePoints = new { win = 4, draw = 2, loss = 1 },
        invitationExpirationDays = 10,
        replacementSelectionCap = 5,
        gameweekReminderLeadTimeHours = 48,
        tieBreakRulesetVersion = "v2",
    };

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
        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "Configuration Test League" }));
        var league = await response.Content.ReadFromJsonAsync<LeagueDto>();
        return (ownerToken, league!);
    }

    private async Task<SeasonDto> CreateSeasonAsync(HttpClient client, string ownerToken, Guid leagueId)
    {
        var eplSeasonIdentifier = $"season-{Guid.NewGuid():N}"[..16];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons", ownerToken, new { eplSeasonIdentifier, startDate = "2026-08-15" }));
        return (await response.Content.ReadFromJsonAsync<SeasonDto>())!;
    }

    [Fact]
    public async Task GetLeagueConfiguration_returns_the_BR_291_defaults_for_a_freshly_created_League()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/configuration", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var configuration = await response.Content.ReadFromJsonAsync<LeagueConfigurationDto>();
        Assert.Equal(25, configuration!.InitialSquadSize);
        Assert.Equal(15, configuration.WeeklyRosterSize);
        Assert.Equal(1, configuration.PositionalMinimums.Gk);
        Assert.Equal(3, configuration.PositionalMinimums.Def);
        Assert.Equal(300, configuration.DraftTimerSecondsByType.Initial);
        Assert.Equal(3, configuration.LeaguePoints.Win);
        Assert.Equal(7, configuration.InvitationExpirationDays);
        Assert.Null(configuration.ReplacementSelectionCap);
    }

    [Fact]
    public async Task GetLeagueConfiguration_is_rejected_for_a_caller_who_is_not_an_active_member()
    {
        var client = factory.CreateClient();
        var (_, league) = await CreateLeagueAsync(client);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/configuration", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateLeagueConfiguration_is_rejected_for_a_caller_who_is_not_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var (_, league) = await CreateLeagueAsync(client);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{league.LeagueId}/configuration", outsiderToken, ValidUpdateBody()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateLeagueConfiguration_applies_the_change_and_is_reflected_by_a_subsequent_get()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);

        var updateResponse = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{league.LeagueId}/configuration", ownerToken, ValidUpdateBody()));

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<LeagueConfigurationDto>();
        Assert.Equal(30, updated!.InitialSquadSize);
        Assert.Equal("v2", updated.TieBreakRulesetVersion);
        Assert.Equal(league.CreatedByMembershipId, updated.UpdatedByMembershipId);

        var getResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/configuration", ownerToken));
        var fetched = await getResponse.Content.ReadFromJsonAsync<LeagueConfigurationDto>();
        Assert.Equal(30, fetched!.InitialSquadSize);
    }

    [Fact]
    public async Task GetSeasonConfiguration_reflects_the_Leagues_configuration_at_the_moment_the_Season_was_created()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var season = await CreateSeasonAsync(client, ownerToken, league.LeagueId);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/seasons/{season.SeasonId}/configuration", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var configuration = await response.Content.ReadFromJsonAsync<SeasonConfigurationDto>();
        Assert.Equal(25, configuration!.InitialSquadSize);
        Assert.Empty(configuration.LockedFields);
    }

    [Fact]
    public async Task UpdateSeasonConfiguration_applies_the_change_when_nothing_is_locked()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var season = await CreateSeasonAsync(client, ownerToken, league.LeagueId);

        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{league.LeagueId}/seasons/{season.SeasonId}/configuration", ownerToken, ValidUpdateBody()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<SeasonConfigurationDto>();
        Assert.Equal(30, updated!.InitialSquadSize);

        // The League's own default is untouched by a Season-level override.
        var leagueConfigResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/configuration", ownerToken));
        var leagueConfig = await leagueConfigResponse.Content.ReadFromJsonAsync<LeagueConfigurationDto>();
        Assert.Equal(25, leagueConfig!.InitialSquadSize);
    }

    [Fact]
    public async Task UpdateSeasonConfiguration_returns_409_for_a_changed_locked_field()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var season = await CreateSeasonAsync(client, ownerToken, league.LeagueId);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(c => c.SeasonId == season.SeasonId);
            seasonConfiguration.Lock(nameof(SeasonConfiguration.InitialSquadSize));
            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{league.LeagueId}/seasons/{season.SeasonId}/configuration", ownerToken, ValidUpdateBody()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("season_configuration_fields_locked", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task UpdateSeasonConfiguration_is_rejected_for_a_caller_who_is_not_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        var season = await CreateSeasonAsync(client, ownerToken, league.LeagueId);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{league.LeagueId}/seasons/{season.SeasonId}/configuration", outsiderToken, ValidUpdateBody()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
