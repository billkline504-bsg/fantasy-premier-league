using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.FantasyTeams;
using EplFantasy.Infrastructure;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-52 (F-011.1, BR-177-BR-182/BR-321/BR-322): proves getAuditLog end-to-end against the real
/// host — the task breakdown's own required proof (actionType/from/to/fantasyTeamId filters, and a
/// ConfigurationChanged row surfacing under the "__league_settings__" pseudo-value for the
/// fantasyTeamId filter), plus League-Administrator-only access and that BeforeState/AfterState
/// already come back as full nested objects with every row (BR-322 — no separate detail call).
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class AuditLogControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
{
    private const string StrongPassword = "correct-horse-battery-staple-97!";

    private static object ValidConfigurationUpdateBody() => new
    {
        initialSquadSize = 30,
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

    private async Task<(string OwnerToken, Guid LeagueId, Guid SeasonId, Guid FantasyTeamId)> SeedLeagueSeasonAndFantasyTeamAsync(HttpClient client)
    {
        var ownerToken = await RegisterAsync(client);
        var leagueResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = $"Audit Log League {Guid.NewGuid():N}" }));
        var league = await leagueResponse.Content.ReadFromJsonAsync<LeagueDto>();

        var eplSeasonIdentifier = $"season-{Guid.NewGuid():N}"[..16];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
            await db.SaveChangesAsync();
        }

        var seasonResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league!.LeagueId}/seasons", ownerToken, new { eplSeasonIdentifier, startDate = "2026-08-15" }));
        var season = await seasonResponse.Content.ReadFromJsonAsync<SeasonDto>();

        var teamResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons/{season!.SeasonId}/fantasy-teams", ownerToken));
        var team = await teamResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();

        return (ownerToken, league.LeagueId, season.SeasonId, team!.FantasyTeamId);
    }

    private async Task<Guid> SeedOwnedSquadPlayerAsync(Guid fantasyTeamId, Guid seasonId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var playerId = Guid.NewGuid();
        db.Players.Add(new Player { PlayerId = playerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = "Injured Player", Position = PlayerPosition.Def });
        var squadPlayer = new SquadPlayer
        {
            SquadPlayerId = Guid.NewGuid(),
            FantasyTeamId = fantasyTeamId,
            PlayerId = playerId,
            SeasonId = seasonId,
            AcquisitionType = AcquisitionType.InitialDraft,
            AcquiredAt = DateTimeOffset.UtcNow,
            IsCurrentlyOwned = true,
        };
        db.SquadPlayers.Add(squadPlayer);
        await db.SaveChangesAsync();
        return squadPlayer.SquadPlayerId;
    }

    /// <summary>Produces one ConfigurationChanged (League-scoped, no FantasyTeam) row and one SeasonEndingInjuryDeclared (SquadPlayer-scoped, resolvable to fantasyTeamId) row.</summary>
    private async Task SeedTwoDistinctActionsAsync(HttpClient client, string ownerToken, Guid leagueId, Guid seasonId, Guid fantasyTeamId)
    {
        await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{leagueId}/configuration", ownerToken, ValidConfigurationUpdateBody()));

        var squadPlayerId = await SeedOwnedSquadPlayerAsync(fantasyTeamId, seasonId);
        await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/squad-players/{squadPlayerId}/declare-season-ending-injury",
            ownerToken,
            new { reason = "league consensus reached" }));
    }

    [Fact]
    public async Task GetAuditLog_filters_by_actionType()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client);
        await SeedTwoDistinctActionsAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/audit?actionType=ConfigurationChanged", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<AdministrativeActionPageDto>();
        Assert.All(page!.Items, a => Assert.Equal("ConfigurationChanged", a.ActionType));
        Assert.Contains(page.Items, a => a.TargetEntityType == "League");
    }

    [Fact]
    public async Task GetAuditLog_filters_by_date_range()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client);

        var before = DateTimeOffset.UtcNow;
        await SeedTwoDistinctActionsAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);
        var after = DateTimeOffset.UtcNow;

        var includingResponse = await client.SendAsync(Request(
            HttpMethod.Get, $"/api/v1/leagues/{leagueId}/audit?from={Uri.EscapeDataString(before.ToString("O"))}&to={Uri.EscapeDataString(after.ToString("O"))}", ownerToken));
        var includingPage = await includingResponse.Content.ReadFromJsonAsync<AdministrativeActionPageDto>();
        Assert.Equal(2, includingPage!.Items.Count);

        var excludingResponse = await client.SendAsync(Request(
            HttpMethod.Get, $"/api/v1/leagues/{leagueId}/audit?to={Uri.EscapeDataString(before.ToString("O"))}", ownerToken));
        var excludingPage = await excludingResponse.Content.ReadFromJsonAsync<AdministrativeActionPageDto>();
        Assert.Empty(excludingPage!.Items);
    }

    [Fact]
    public async Task GetAuditLog_filters_by_fantasyTeamId_resolving_SeasonEndingInjuryDeclared_via_SquadPlayer()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client);
        await SeedTwoDistinctActionsAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/audit?fantasyTeamId={fantasyTeamId}", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<AdministrativeActionPageDto>();
        var action = Assert.Single(page!.Items);
        Assert.Equal("SeasonEndingInjuryDeclared", action.ActionType);
        Assert.Equal("SquadPlayer", action.TargetEntityType);
    }

    [Fact]
    public async Task GetAuditLog_a_ConfigurationChanged_row_surfaces_under_the_league_settings_pseudo_value()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client);
        await SeedTwoDistinctActionsAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/audit?fantasyTeamId=__league_settings__", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<AdministrativeActionPageDto>();
        var action = Assert.Single(page!.Items);
        Assert.Equal("ConfigurationChanged", action.ActionType);
    }

    [Fact]
    public async Task GetAuditLog_returns_BeforeState_and_AfterState_as_nested_JSON_objects_not_escaped_strings()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client);
        await SeedTwoDistinctActionsAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/audit?actionType=SeasonEndingInjuryDeclared", ownerToken));

        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        var firstItem = document.RootElement.GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Object, firstItem.GetProperty("beforeState").ValueKind);
        Assert.Equal(JsonValueKind.Object, firstItem.GetProperty("afterState").ValueKind);
    }

    [Fact]
    public async Task GetAuditLog_is_rejected_for_a_caller_who_is_not_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client);
        await SeedTwoDistinctActionsAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/audit", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
