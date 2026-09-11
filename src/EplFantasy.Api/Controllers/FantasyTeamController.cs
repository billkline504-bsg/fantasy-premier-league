using EplFantasy.Api.Contracts;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-11 (F-002.3): the caller's own FantasyTeam per League+Season, and reading a Season's
/// FantasyTeams (OpenAPI Specification v1.0's createFantasyTeam/listFantasyTeams/getFantasyTeam) —
/// any active member may read; createFantasyTeam additionally rejects (409) a second FantasyTeam
/// for the same (LeagueMembership, Season) pair, BR-193/Invariant 2. IT-25 (F-007.5) adds getSquad
/// — its own OpenAPI x-authorization says only "an active member of leagueId," but its Feature
/// Behavior Spec's own Security considerations are explicit and more specific: "Squad View data is
/// scoped to the owning FantasyTeam... independent of the League-membership check that gates other
/// league-facing screens." The more specific, more detailed source wins — getSquad uses
/// FantasyTeamOwner, not ActiveLeagueMember, the same judgment call this codebase has made before
/// when the two documents disagree.
///
/// IT-57 (F-013.2, BR-014/BR-175/BR-272/BR-326): this is the one place in the API that ever
/// attaches a Username to a FantasyTeam (FantasyTeamDto's own remarks: "a plain FantasyTeam row
/// doesn't itself carry [Username]... FantasyTeamController joins it at read time") — every other
/// historical DTO (standings, head-to-head results, draft selections) carries only FantasyTeamId,
/// composed client-side with this endpoint. BR-326 requires that composed username to be the one
/// the User held at the time of the record, not their current one, so every read here resolves via
/// <see cref="IUsernameHistoryResolver"/> — a real query against <c>UsernameHistory</c> — instead
/// of the plain <c>User.Username</c> join this controller used before this task. The "as of"
/// instant is this FantasyTeam's own <c>CreatedAt</c>: the codebase has no per-record timestamp of
/// its own for "when did this FantasyTeam's Season participation begin" other than that field, and
/// every downstream historical record (drafts, rosters, standings) for this FantasyTeam
/// necessarily happened no earlier than it.
/// </summary>
[ApiController]
[Route("api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams")]
public class FantasyTeamController(
    IFantasyTeamService fantasyTeamService,
    IUsernameHistoryResolver usernameHistoryResolver,
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> CreateFantasyTeam(Guid leagueId, Guid seasonId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Seasons.AnyAsync(s => s.SeasonId == seasonId && s.LeagueId == leagueId, cancellationToken))
        {
            return NotFound();
        }

        var userId = currentUser.UserId!.Value;
        var membershipId = await dbContext.LeagueMemberships
            .Where(m => m.LeagueId == leagueId && m.UserId == userId && m.Status == MembershipStatus.Active)
            .Select(m => m.LeagueMembershipId)
            .SingleAsync(cancellationToken);

        var result = await fantasyTeamService.CreateAsync(membershipId, seasonId, cancellationToken);

        if (result.IsFailure)
        {
            return ToProblem(result.Error, StatusCodes.Status409Conflict);
        }

        var username = await usernameHistoryResolver.ResolveAsOfAsync(userId, result.Value.CreatedAt, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, FantasyTeamDto.From(result.Value, username));
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> ListFantasyTeams(Guid leagueId, Guid seasonId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Seasons.AnyAsync(s => s.SeasonId == seasonId && s.LeagueId == leagueId, cancellationToken))
        {
            return NotFound();
        }

        var teams = await (
            from t in dbContext.FantasyTeams.AsNoTracking()
            join m in dbContext.LeagueMemberships.AsNoTracking() on t.LeagueMembershipId equals m.LeagueMembershipId
            where t.SeasonId == seasonId
            select new { Team = t, m.UserId }
        ).ToListAsync(cancellationToken);

        var usernamesByFantasyTeamId = await usernameHistoryResolver.ResolveManyAsOfAsync(
            teams.Select(x => (x.Team.FantasyTeamId, x.UserId, x.Team.CreatedAt)).ToList(), cancellationToken);

        var dtos = teams.Select(x => FantasyTeamDto.From(x.Team, usernamesByFantasyTeamId[x.Team.FantasyTeamId])).ToList();

        return Ok(dtos);
    }

    [HttpGet("{fantasyTeamId}")]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> GetFantasyTeam(Guid leagueId, Guid seasonId, Guid fantasyTeamId, CancellationToken cancellationToken)
    {
        var team = await dbContext.FantasyTeams.AsNoTracking()
            .SingleOrDefaultAsync(t => t.FantasyTeamId == fantasyTeamId && t.SeasonId == seasonId, cancellationToken);

        if (team is null)
        {
            return NotFound();
        }

        var membership = await dbContext.LeagueMemberships.AsNoTracking()
            .SingleAsync(m => m.LeagueMembershipId == team.LeagueMembershipId, cancellationToken);

        if (membership.LeagueId != leagueId)
        {
            return NotFound();
        }

        var username = await usernameHistoryResolver.ResolveAsOfAsync(membership.UserId, team.CreatedAt, cancellationToken);

        return Ok(FantasyTeamDto.From(team, username));
    }

    [HttpGet("{fantasyTeamId}/squad")]
    [Authorize(Policy = AuthorizationPolicies.FantasyTeamOwner)]
    public async Task<IActionResult> GetSquad(
        Guid leagueId,
        Guid seasonId,
        Guid fantasyTeamId,
        PlayerPosition? position,
        string? search,
        string? sort,
        CancellationToken cancellationToken)
    {
        var team = await dbContext.FantasyTeams.AsNoTracking()
            .SingleOrDefaultAsync(t => t.FantasyTeamId == fantasyTeamId && t.SeasonId == seasonId, cancellationToken);

        if (team is null)
        {
            return NotFound();
        }

        var season = await dbContext.Seasons.AsNoTracking().SingleAsync(s => s.SeasonId == seasonId, cancellationToken);
        if (season.LeagueId != leagueId)
        {
            return NotFound();
        }

        // BR-209/the breakdown's own Tests bullet: the *complete* squad, including a released
        // player (IsCurrentlyOwned = false) — BR-264's retained acquisition history, not just
        // whoever is presently owned.
        var query =
            from squadPlayer in dbContext.SquadPlayers.AsNoTracking()
            join player in dbContext.Players.AsNoTracking() on squadPlayer.PlayerId equals player.PlayerId
            where squadPlayer.FantasyTeamId == fantasyTeamId
            select new { squadPlayer, player };

        if (position is not null)
        {
            query = query.Where(x => x.player.Position == position);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalizedSearch = search.ToLowerInvariant();
            query = query.Where(x => x.player.Name.ToLower().Contains(normalizedSearch));
        }

        var rows = await query.ToListAsync(cancellationToken);

        var clubIds = rows.Where(r => r.player.CurrentClubId is not null).Select(r => r.player.CurrentClubId!.Value).Distinct().ToList();
        var clubNamesById = await dbContext.Clubs.AsNoTracking()
            .Where(c => clubIds.Contains(c.ClubId))
            .ToDictionaryAsync(c => c.ClubId, c => c.Name, cancellationToken);

        // BR-317: a LEFT JOIN-equivalent lookup — a player with no PlayerPerformance rows yet
        // (before the Season's first Gameweek is scored) has no row in this view at all, resolving
        // to zero via SquadPlayerViewDto.From's own null-coalescing rather than being omitted.
        var playerIds = rows.Select(r => r.player.PlayerId).ToList();
        var statsByPlayerId = await dbContext.PlayerSeasonStatistics.AsNoTracking()
            .Where(s => s.EplSeasonIdentifier == season.EplSeasonIdentifier && playerIds.Contains(s.PlayerId))
            .ToDictionaryAsync(s => s.PlayerId, cancellationToken);

        var withClubNames = rows
            .Select(r => (
                Dto: SquadPlayerViewDto.From(r.squadPlayer, r.player, statsByPlayerId.GetValueOrDefault(r.player.PlayerId)),
                ClubName: r.player.CurrentClubId is { } clubId ? clubNamesById.GetValueOrDefault(clubId, "") : ""))
            .ToList();

        return Ok(ApplySort(withClubNames, sort));
    }

    /// <summary>BR-316: any displayed column, toggled ascending/descending by a leading "-" — shared implementation with the Draft Player Pool endpoint (IT-28), <see cref="PlayerPoolSorting"/>, per Architecture §6.3's "one implementation, two read-models."</summary>
    private static List<SquadPlayerViewDto> ApplySort(List<(SquadPlayerViewDto Dto, string ClubName)> rows, string? sort) =>
        PlayerPoolSorting.Apply(
            rows,
            sort,
            name: dto => dto.PlayerName,
            position: dto => dto.Position,
            minutesPlayed: dto => dto.SeasonMinutesPlayed,
            gamesPlayed: dto => dto.SeasonGamesPlayed,
            fantasyPoints: dto => dto.SeasonFantasyPoints);

    // Mirrors AuthController.ToProblem — see that method's own remarks on why the fallback
    // errorCode has to be overwritten explicitly rather than passed to Problem() directly.
    private ObjectResult ToProblem(Error error, int statusCode)
    {
        var problem = Problem(detail: error.Message, statusCode: statusCode);
        ((ProblemDetails)problem.Value!).Extensions["errorCode"] = error.Code;
        return problem;
    }
}
