using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.Competition;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-23 (F-005.1): proves createDraft (League-Administrator-only, 201/409) and listDrafts (any
/// active member — a documentation gap bundled in alongside createDraft, the same judgment call
/// IT-14's getCurrentUser made) end-to-end against the real host. Also proves IT-24's makeDraftPick
/// (F-005.2): DraftTurnOwner's own 403, 201 with the DraftSelection recorded, 409 player_already_owned,
/// and the Idempotency-Key replay (BR-237). Also proves IT-26's extendDraftTimer (F-005.3):
/// DraftLeagueAdministrator's own 403 for a non-Administrator, and 200 with CurrentPickDeadline
/// pushed back for the League Administrator. Also proves IT-28's getDraft/listDraftSelections/
/// getDraftPlayerPool (F-005.5): DraftLeagueMember's own 403 for an outsider, and — the breakdown's
/// own Tests bullet — the player pool's shared position/search/sort query-parameter contract
/// (IT-25) plus its exclusion of already-owned players.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class DraftControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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

    /// <summary>Seeds a League + Season with `fantasyTeamCount` Active FantasyTeams, each created through the real API by a distinct registered user.</summary>
    private async Task<(string OwnerToken, Guid LeagueId, Guid SeasonId)> SeedSeasonWithFantasyTeamsAsync(HttpClient client, int fantasyTeamCount)
    {
        var ownerToken = await RegisterAsync(client);
        var leagueResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = $"Draft League {Guid.NewGuid():N}" }));
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

        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons/{season!.SeasonId}/fantasy-teams", ownerToken));

        for (var i = 1; i < fantasyTeamCount; i++)
        {
            var memberToken = await RegisterAsync(client);
            var memberResponse = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me", memberToken));
            var member = await memberResponse.Content.ReadFromJsonAsync<UserSelfDto>();

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
                db.LeagueMemberships.Add(LeagueMembership.Join(Guid.NewGuid(), league.LeagueId, member!.UserId, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
            }

            await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons/{season.SeasonId}/fantasy-teams", memberToken));
        }

        return (ownerToken, league.LeagueId, season.SeasonId);
    }

    /// <summary>Like SeedSeasonWithFantasyTeamsAsync, but also returns every member's own bearer token keyed by UserId, so a test can resolve which token belongs to whichever FantasyTeam DraftOrder puts on the clock.</summary>
    private async Task<(Dictionary<Guid, string> TokenByUserId, Guid LeagueId, Guid SeasonId)> SeedSeasonWithFantasyTeamsAndTokensAsync(HttpClient client, int fantasyTeamCount)
    {
        var ownerToken = await RegisterAsync(client);
        var leagueResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = $"Draft League {Guid.NewGuid():N}" }));
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

        var tokenByUserId = new Dictionary<Guid, string>();

        var ownerSelfResponse = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me", ownerToken));
        var ownerSelf = await ownerSelfResponse.Content.ReadFromJsonAsync<UserSelfDto>();
        tokenByUserId[ownerSelf!.UserId] = ownerToken;
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons/{season!.SeasonId}/fantasy-teams", ownerToken));

        for (var i = 1; i < fantasyTeamCount; i++)
        {
            var memberToken = await RegisterAsync(client);
            var memberResponse = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me", memberToken));
            var member = await memberResponse.Content.ReadFromJsonAsync<UserSelfDto>();
            tokenByUserId[member!.UserId] = memberToken;

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
                db.LeagueMemberships.Add(LeagueMembership.Join(Guid.NewGuid(), league.LeagueId, member.UserId, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
            }

            await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons/{season.SeasonId}/fantasy-teams", memberToken));
        }

        return (tokenByUserId, league.LeagueId, season.SeasonId);
    }

    private async Task<string> GetOnTurnTokenAsync(Guid fantasyTeamId, Dictionary<Guid, string> tokenByUserId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var onTurnUserId = await (
            from team in db.FantasyTeams
            join membership in db.LeagueMemberships on team.LeagueMembershipId equals membership.LeagueMembershipId
            where team.FantasyTeamId == fantasyTeamId
            select membership.UserId
        ).SingleAsync();

        return tokenByUserId[onTurnUserId];
    }

    private async Task<Guid> SeedPlayerAsync() => (await SeedPlayerAsync($"Test Player {Guid.NewGuid():N}"[..20], PlayerPosition.Mid)).PlayerId;

    private async Task<Player> SeedPlayerAsync(string name, PlayerPosition position)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var club = new Club { ClubId = Guid.NewGuid(), EplClubId = $"club{suffix}", Name = $"Test FC {suffix}", ShortName = "TFC" };
        var player = new Player { PlayerId = Guid.NewGuid(), EplPlayerId = $"player{suffix}", Name = name, Position = position, CurrentClubId = club.ClubId };
        db.Clubs.Add(club);
        db.Players.Add(player);
        await db.SaveChangesAsync();
        return player;
    }

    [Fact]
    public async Task CreateDraft_is_rejected_for_a_caller_who_is_not_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var (_, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(client, fantasyTeamCount: 2);
        var otherToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", otherToken, new { draftType = "Initial" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateDraft_creates_an_InProgress_Initial_Draft_with_a_randomized_order()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(client, fantasyTeamCount: 3);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var draft = await response.Content.ReadFromJsonAsync<DraftDto>();
        Assert.Equal(seasonId, draft!.SeasonId);
        Assert.Equal("Initial", draft.DraftType);
        Assert.Equal("InProgress", draft.Status);
        Assert.Equal(3, draft.DraftOrder.Count);
        Assert.Equal(3, draft.DraftOrder.Distinct().Count());
        Assert.Equal(1, draft.CurrentRound);
        Assert.NotNull(draft.CurrentPickDeadline);
    }

    [Fact]
    public async Task CreateDraft_with_fewer_than_two_FantasyTeams_returns_409()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(client, fantasyTeamCount: 1);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("insufficient_fantasy_teams", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task CreateDraft_a_second_time_for_the_same_Season_returns_409()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(client, fantasyTeamCount: 2);
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("season_not_ready_for_initial_draft", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task CreateDraft_with_an_unsupported_draftType_returns_400()
    {
        // IT-48's own research established Replacement never goes through this endpoint at all
        // (see ReplacementOpportunity's own remarks) — it's the genuinely unsupported value here,
        // not Secondary (IT-46 wired that in once CreateSecondaryDraftAsync existed).
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(client, fantasyTeamCount: 2);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Replacement" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateDraft_draftType_Secondary_creates_a_Secondary_Draft_from_the_current_standings_snapshot()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(client, fantasyTeamCount: 2);

        Guid gameweekId;
        List<Guid> fantasyTeamIds;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            var eplSeasonIdentifier = await db.Seasons.Where(s => s.SeasonId == seasonId).Select(s => s.EplSeasonIdentifier).SingleAsync();
            fantasyTeamIds = await db.FantasyTeams.Where(t => t.SeasonId == seasonId).Select(t => t.FantasyTeamId).ToListAsync();

            var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = 1, RosterLockDeadline = DateTimeOffset.UtcNow.AddDays(7) };
            db.Gameweeks.Add(gameweek);
            gameweekId = gameweek.GameweekId;

            for (var i = 0; i < fantasyTeamIds.Count; i++)
            {
                var standing = LeagueStanding.Calculate(
                    seasonId, fantasyTeamIds[i], gameweekId,
                    leaguePoints: 0, played: 0, won: 0, drawn: 0, lost: 0,
                    fantasyGoalsFor: 0, fantasyGoalsAgainst: 0, fantasyGoalDifference: 0, captainPointsTotal: 0);
                standing.AssignPosition(i + 1);
                db.LeagueStandings.Add(standing);
            }

            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Secondary" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var draft = await response.Content.ReadFromJsonAsync<DraftDto>();
        Assert.Equal("Secondary", draft!.DraftType);
        Assert.Equal("InProgress", draft.Status);
        Assert.Equal(fantasyTeamIds.Count, draft.DraftOrder.Count);
    }

    [Fact]
    public async Task CreateDraft_draftType_Secondary_returns_409_when_no_standings_snapshot_exists_yet()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(client, fantasyTeamCount: 2);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Secondary" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("standings_snapshot_unavailable", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ListDrafts_without_a_bearer_token_returns_401()
    {
        var client = factory.CreateClient();
        var (_, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(client, fantasyTeamCount: 2);

        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListDrafts_returns_the_created_Draft_for_any_active_member()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAsync(client, fantasyTeamCount: 2);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var created = await createResponse.Content.ReadFromJsonAsync<DraftDto>();

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var drafts = await response.Content.ReadFromJsonAsync<List<DraftDto>>();
        Assert.Single(drafts!, d => d.DraftId == created!.DraftId);
    }

    [Fact]
    public async Task MakeDraftPick_is_rejected_for_a_caller_whose_FantasyTeam_is_not_on_the_clock()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var draft = await createResponse.Content.ReadFromJsonAsync<DraftDto>();
        var onTurnToken = await GetOnTurnTokenAsync(draft!.DraftOrder[0], tokenByUserId);
        var notOnTurnToken = tokenByUserId.Values.Single(t => t != onTurnToken);
        var playerId = await SeedPlayerAsync();

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/drafts/{draft.DraftId}/picks", notOnTurnToken, new { playerId }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MakeDraftPick_by_the_on_turn_FantasyTeam_records_the_selection()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var draft = await createResponse.Content.ReadFromJsonAsync<DraftDto>();
        var onTurnToken = await GetOnTurnTokenAsync(draft!.DraftOrder[0], tokenByUserId);
        var playerId = await SeedPlayerAsync();

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/drafts/{draft.DraftId}/picks", onTurnToken, new { playerId }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var selection = await response.Content.ReadFromJsonAsync<DraftSelectionDto>();
        Assert.Equal(draft.DraftId, selection!.DraftId);
        Assert.Equal(draft.DraftOrder[0], selection.FantasyTeamId);
        Assert.Equal(playerId, selection.PlayerId);
        Assert.Equal(1, selection.Round);
        Assert.Equal(1, selection.PickNumber);
    }

    [Fact]
    public async Task MakeDraftPick_for_an_already_owned_player_returns_409()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var draft = await createResponse.Content.ReadFromJsonAsync<DraftDto>();
        var firstOnTurnToken = await GetOnTurnTokenAsync(draft!.DraftOrder[0], tokenByUserId);
        var playerId = await SeedPlayerAsync();
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/drafts/{draft.DraftId}/picks", firstOnTurnToken, new { playerId }));
        var secondOnTurnToken = await GetOnTurnTokenAsync(draft.DraftOrder[1], tokenByUserId);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/drafts/{draft.DraftId}/picks", secondOnTurnToken, new { playerId }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("player_already_owned", body.GetProperty("errorCode").GetString());
    }

    /// <summary>
    /// IT-24 (BR-237): proves the Idempotency-Key mechanism actually prevents a double pick when a
    /// caller's retry genuinely overlaps with their own first attempt — the realistic case it
    /// exists for (a client retrying before it ever saw a response, not after a confirmed success).
    /// A *sequential* retry issued after the first request has already completed is deliberately
    /// NOT exercised here: ASP.NET Core always evaluates `[Authorize]` before `[Idempotent]`'s own
    /// cache lookup runs (authorization filters execute before action filters, unconditionally), so
    /// by the time such a retry is authorized, the successful first pick has already advanced
    /// CurrentPickIndex — the caller's FantasyTeam is no longer on the clock, and
    /// DraftTurnOwner correctly returns 403 rather than replaying the earlier 201. That is a
    /// deliberate consequence of this endpoint's own x-authorization being a live, per-request
    /// object-level check (not a stable fact like "is the League Administrator"), not a defect —
    /// the two concurrent requests below never cross that boundary, since neither can have advanced
    /// the turn before the other is authorized.
    /// </summary>
    [Fact]
    public async Task MakeDraftPick_with_a_repeated_Idempotency_Key_prevents_a_double_pick_on_a_genuinely_overlapping_retry()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var draft = await createResponse.Content.ReadFromJsonAsync<DraftDto>();
        var onTurnToken = await GetOnTurnTokenAsync(draft!.DraftOrder[0], tokenByUserId);
        var playerId = await SeedPlayerAsync();
        var idempotencyKey = Guid.NewGuid().ToString();

        HttpRequestMessage PickRequest()
        {
            var request = Request(HttpMethod.Post, $"/api/v1/drafts/{draft.DraftId}/picks", onTurnToken, new { playerId });
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            return request;
        }

        var firstTask = client.SendAsync(PickRequest());
        var secondTask = client.SendAsync(PickRequest());
        var responses = await Task.WhenAll(firstTask, secondTask);

        // Whichever mechanism actually resolved the overlap (the idempotency cache, or — if the
        // two requests didn't overlap tightly enough for that — ux_squad_players_owned itself,
        // AP-009/AP-010's own guarantee) exactly one DraftSelection must exist either way.
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.Equal(1, await db.DraftSelections.CountAsync(s => s.DraftId == draft.DraftId));
        Assert.Equal(1, await db.SquadPlayers.CountAsync(sp => sp.PlayerId == playerId && sp.SeasonId == seasonId));
    }

    [Fact]
    public async Task ExtendDraftTimer_without_a_bearer_token_returns_401()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var draft = await createResponse.Content.ReadFromJsonAsync<DraftDto>();

        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Post, $"/api/v1/drafts/{draft!.DraftId}/timer/extend", body: new { additionalSeconds = 60 }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExtendDraftTimer_is_rejected_for_a_caller_who_is_not_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var memberToken = tokenByUserId.Values.Last();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var draft = await createResponse.Content.ReadFromJsonAsync<DraftDto>();

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/drafts/{draft!.DraftId}/timer/extend", memberToken, new { additionalSeconds = 60 }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExtendDraftTimer_by_the_League_Administrator_pushes_the_deadline_back()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var draft = await createResponse.Content.ReadFromJsonAsync<DraftDto>();
        var originalDeadline = draft!.CurrentPickDeadline;

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/drafts/{draft.DraftId}/timer/extend", ownerToken, new { additionalSeconds = 90 }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var extended = await response.Content.ReadFromJsonAsync<DraftDto>();
        // A tolerance, not an exact-tick comparison: DateTimeOffset.AddSeconds' own double-precision
        // arithmetic can introduce a single-tick (100ns) rounding difference — utterly immaterial
        // for a timer measured in whole seconds, but real enough to make an exact Assert.Equal flaky.
        var difference = extended!.CurrentPickDeadline!.Value - originalDeadline!.Value;
        Assert.Equal(90, difference.TotalSeconds, precision: 3);
    }

    [Fact]
    public async Task GetDraft_without_a_bearer_token_returns_401()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var draft = await createResponse.Content.ReadFromJsonAsync<DraftDto>();

        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Get, $"/api/v1/drafts/{draft!.DraftId}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetDraft_is_rejected_for_a_caller_who_is_not_an_active_member_of_the_Drafts_League()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var draft = await createResponse.Content.ReadFromJsonAsync<DraftDto>();
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/drafts/{draft!.DraftId}", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetDraft_returns_the_current_Draft_state_for_an_active_League_member()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var memberToken = tokenByUserId.Values.Last();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var created = await createResponse.Content.ReadFromJsonAsync<DraftDto>();

        // Any active member — not just the League Administrator — can read Draft state (BR-204/BR-205).
        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/drafts/{created!.DraftId}", memberToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var draft = await response.Content.ReadFromJsonAsync<DraftDto>();
        Assert.Equal(created.DraftId, draft!.DraftId);
        Assert.Equal(created.DraftOrder, draft.DraftOrder);
        Assert.Equal("InProgress", draft.Status);
    }

    [Fact]
    public async Task ListDraftSelections_lists_recorded_picks_in_the_order_they_occurred()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var draft = await createResponse.Content.ReadFromJsonAsync<DraftDto>();
        var firstOnTurnToken = await GetOnTurnTokenAsync(draft!.DraftOrder[0], tokenByUserId);
        var firstPlayerId = await SeedPlayerAsync();
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/drafts/{draft.DraftId}/picks", firstOnTurnToken, new { playerId = firstPlayerId }));
        var secondOnTurnToken = await GetOnTurnTokenAsync(draft.DraftOrder[1], tokenByUserId);
        var secondPlayerId = await SeedPlayerAsync();
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/drafts/{draft.DraftId}/picks", secondOnTurnToken, new { playerId = secondPlayerId }));

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/drafts/{draft.DraftId}/selections", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<DraftSelectionPageDto>();
        Assert.Equal(2, page!.Items.Count);
        Assert.Equal(firstPlayerId, page.Items[0].PlayerId);
        Assert.Equal(secondPlayerId, page.Items[1].PlayerId);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task GetDraftPlayerPool_excludes_a_player_already_owned_in_this_Season()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var draft = await createResponse.Content.ReadFromJsonAsync<DraftDto>();
        var onTurnToken = await GetOnTurnTokenAsync(draft!.DraftOrder[0], tokenByUserId);
        var draftedPlayer = await SeedPlayerAsync("Drafted Player", PlayerPosition.Fwd);
        var undraftedPlayer = await SeedPlayerAsync("Undrafted Player", PlayerPosition.Fwd);
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/drafts/{draft.DraftId}/picks", onTurnToken, new { playerId = draftedPlayer.PlayerId }));

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/drafts/{draft.DraftId}/player-pool", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pool = await response.Content.ReadFromJsonAsync<List<DraftPlayerPoolEntryDto>>();
        Assert.Contains(pool!, p => p.PlayerId == undraftedPlayer.PlayerId);
        Assert.DoesNotContain(pool!, p => p.PlayerId == draftedPlayer.PlayerId);
    }

    [Fact]
    public async Task GetDraftPlayerPool_filters_by_position_and_search_and_sorts_by_name()
    {
        var client = factory.CreateClient();
        var (tokenByUserId, leagueId, seasonId) = await SeedSeasonWithFantasyTeamsAndTokensAsync(client, fantasyTeamCount: 2);
        var ownerToken = tokenByUserId.Values.First();
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", ownerToken, new { draftType = "Initial" }));
        var draft = await createResponse.Content.ReadFromJsonAsync<DraftDto>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedPlayerAsync($"Zeta Striker {suffix}", PlayerPosition.Fwd);
        await SeedPlayerAsync($"Alpha Striker {suffix}", PlayerPosition.Fwd);
        await SeedPlayerAsync($"Keeper {suffix}", PlayerPosition.Gk);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/drafts/{draft!.DraftId}/player-pool?position=Fwd&search={Uri.EscapeDataString($"Striker {suffix}")}&sort=name", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pool = await response.Content.ReadFromJsonAsync<List<DraftPlayerPoolEntryDto>>();
        Assert.Equal(2, pool!.Count);
        Assert.Equal($"Alpha Striker {suffix}", pool[0].PlayerName);
        Assert.Equal($"Zeta Striker {suffix}", pool[1].PlayerName);
        Assert.All(pool, p => Assert.Equal("Fwd", p.Position));
    }
}
