using EplFantasy.Api.Contracts;
using EplFantasy.Competition;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-41 (F-009.4, BR-218-BR-220): read-only H2H schedule/result display — OpenAPI Specification
/// v1.0's getSchedule/getMatch. Purely read composition (no new domain, per the task breakdown's
/// own "Domain: none new") — IT-39/IT-40 already populate every field this exposes. BR-219's own
/// "opponent shown via username/League icon" is a client-side composition of this response with
/// getFantasyTeam (IT-11)/getLeague — the HeadToHeadMatch schema itself carries only FantasyTeamIds,
/// no denormalized name/icon fields, so there's nothing further for this controller to resolve.
/// IT-44 (F-010.4, BR-215/BR-217) adds getStandings the same way — IT-42 already populates every
/// LeagueStanding field this exposes; "current" (an omitted asOfGameweekId) resolves to whichever
/// Gameweek this Season's most recently computed snapshot is for, since IStandingsCalculationService
/// itself isn't wired to any automatic trigger yet (IT-42's own deliberately-left gap) — a Season
/// with no snapshot computed at all yet returns an empty list, not a 404 or an on-the-fly recompute.
/// </summary>
[ApiController]
[Route("api/v1/leagues/{leagueId}/seasons/{seasonId}")]
public class CompetitionController(EplFantasyDbContext dbContext) : ControllerBase
{
    [HttpGet("schedule")]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> GetSchedule(Guid leagueId, Guid seasonId, Guid? gameweekId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Seasons.AnyAsync(s => s.SeasonId == seasonId && s.LeagueId == leagueId, cancellationToken))
        {
            return NotFound();
        }

        var query = dbContext.HeadToHeadMatches.AsNoTracking().Where(m => m.SeasonId == seasonId);
        if (gameweekId is { } gw)
        {
            query = query.Where(m => m.GameweekId == gw);
        }

        var matches = await query.ToListAsync(cancellationToken);

        return Ok(matches.Select(ToDto).ToList());
    }

    [HttpGet("matches/{matchId}")]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> GetMatch(Guid leagueId, Guid seasonId, Guid matchId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Seasons.AnyAsync(s => s.SeasonId == seasonId && s.LeagueId == leagueId, cancellationToken))
        {
            return NotFound();
        }

        var match = await dbContext.HeadToHeadMatches.AsNoTracking()
            .SingleOrDefaultAsync(m => m.MatchId == matchId && m.SeasonId == seasonId, cancellationToken);

        return match is null ? NotFound() : Ok(ToDto(match));
    }

    [HttpGet("standings")]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> GetStandings(Guid leagueId, Guid seasonId, Guid? asOfGameweekId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Seasons.AnyAsync(s => s.SeasonId == seasonId && s.LeagueId == leagueId, cancellationToken))
        {
            return NotFound();
        }

        var targetGameweekId = asOfGameweekId ?? await (
            from standing in dbContext.LeagueStandings.AsNoTracking()
            join gameweek in dbContext.Gameweeks.AsNoTracking() on standing.AsOfGameweekId equals gameweek.GameweekId
            where standing.SeasonId == seasonId
            orderby gameweek.Number descending
            select (Guid?)standing.AsOfGameweekId
        ).FirstOrDefaultAsync(cancellationToken);

        if (targetGameweekId is null)
        {
            return Ok(new List<LeagueStandingDto>()); // no snapshot has ever been computed for this Season yet.
        }

        var standings = await dbContext.LeagueStandings.AsNoTracking()
            .Where(s => s.SeasonId == seasonId && s.AsOfGameweekId == targetGameweekId)
            .OrderBy(s => s.Position)
            .ToListAsync(cancellationToken);

        return Ok(standings.Select(ToDto).ToList());
    }

    private static HeadToHeadMatchDto ToDto(HeadToHeadMatch match) => new()
    {
        MatchId = match.MatchId,
        SeasonId = match.SeasonId,
        GameweekId = match.GameweekId,
        HomeFantasyTeamId = match.HomeFantasyTeamId,
        AwayFantasyTeamId = match.AwayFantasyTeamId,
        HomeScore = match.HomeScore,
        AwayScore = match.AwayScore,
        Result = match.Result?.ToString(),
        LeaguePointsHome = match.LeaguePointsHome,
        LeaguePointsAway = match.LeaguePointsAway,
    };

    private static LeagueStandingDto ToDto(LeagueStanding standing) => new()
    {
        FantasyTeamId = standing.FantasyTeamId,
        LeaguePoints = standing.LeaguePoints,
        Played = standing.Played,
        Won = standing.Won,
        Drawn = standing.Drawn,
        Lost = standing.Lost,
        FantasyGoalsFor = standing.FantasyGoalsFor,
        FantasyGoalsAgainst = standing.FantasyGoalsAgainst,
        FantasyGoalDifference = standing.FantasyGoalDifference,
        CaptainPointsTotal = standing.CaptainPointsTotal,
        Position = standing.Position,
        AsOfGameweekId = standing.AsOfGameweekId,
    };
}
