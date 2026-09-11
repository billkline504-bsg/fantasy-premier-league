using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-03 (F-003.1): League creation, read, and Administrator-only update (OpenAPI Specification
/// v1.0's createLeague/getLeague/updateLeague). BR-023: any authenticated user may create a
/// League — no object-level policy beyond [Authorize] applies to that action; getLeague/updateLeague
/// are object-level-checked against the specific leagueId via IT-F06's ActiveLeagueMember/
/// LeagueAdministrator policies. IT-12 (F-002.4) adds listMyLeagues — no domain logic of its own,
/// a pure read-model query scoped to the caller's own active memberships (BR-030/BR-031).
/// </summary>
[ApiController]
[Route("api/v1/leagues")]
[Authorize]
public class LeagueController(
    ILeagueService leagueService,
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> CreateLeague(CreateLeagueRequest request, CancellationToken cancellationToken)
    {
        var league = await leagueService.CreateAsync(currentUser.UserId!.Value, request.Name, request.Description, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ToDto(league));
    }

    [HttpGet]
    public async Task<IActionResult> ListMyLeagues(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId!.Value;
        var leagueIds = await dbContext.LeagueMemberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active)
            .Select(m => m.LeagueId)
            .ToListAsync(cancellationToken);

        var leagues = await dbContext.Leagues.AsNoTracking()
            .Where(l => leagueIds.Contains(l.LeagueId))
            .ToListAsync(cancellationToken);

        return Ok(leagues.Select(ToDto).ToList());
    }

    [HttpGet("{leagueId}")]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> GetLeague(Guid leagueId, CancellationToken cancellationToken)
    {
        var league = await dbContext.Leagues.AsNoTracking().SingleOrDefaultAsync(l => l.LeagueId == leagueId, cancellationToken);

        return league is null ? NotFound() : Ok(ToDto(league));
    }

    [HttpPut("{leagueId}")]
    [Authorize(Policy = AuthorizationPolicies.LeagueAdministrator)]
    public async Task<IActionResult> UpdateLeague(Guid leagueId, UpdateLeagueRequest request, CancellationToken cancellationToken)
    {
        LeagueStatus? status = null;
        if (request.Status is not null)
        {
            if (!Enum.TryParse<LeagueStatus>(request.Status, ignoreCase: true, out var parsedStatus))
            {
                return BadRequest();
            }

            status = parsedStatus;
        }

        var league = await leagueService.UpdateAsync(leagueId, currentUser.UserId!.Value, request.Name, request.Description, status, cancellationToken);

        return Ok(ToDto(league));
    }

    private static LeagueDto ToDto(League league) => new()
    {
        LeagueId = league.LeagueId,
        Name = league.Name,
        Description = league.Description,
        Status = league.Status.ToString(),
        CreatedByMembershipId = league.CreatedByMembershipId,
        CreatedAt = league.CreatedAt,
    };
}
