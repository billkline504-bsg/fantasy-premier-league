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
/// IT-06 (F-003.4): Season creation (League-Administrator-only) and read (any active member) —
/// OpenAPI Specification v1.0's createSeason/listSeasons/getSeason.
/// </summary>
[ApiController]
[Route("api/v1/leagues/{leagueId}/seasons")]
public class SeasonController(ISeasonService seasonService, EplFantasyDbContext dbContext) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.LeagueAdministrator)]
    public async Task<IActionResult> CreateSeason(Guid leagueId, CreateSeasonRequest request, CancellationToken cancellationToken)
    {
        var result = await seasonService.CreateAsync(leagueId, request.EplSeasonIdentifier, request.StartDate!.Value, cancellationToken);

        if (result.IsFailure)
        {
            return ToProblem(result.Error, StatusCodes.Status400BadRequest);
        }

        return StatusCode(StatusCodes.Status201Created, ToDto(result.Value));
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> ListSeasons(Guid leagueId, [FromQuery] string? status, CancellationToken cancellationToken)
    {
        SeasonStatus? statusFilter = null;
        if (status is not null)
        {
            if (!Enum.TryParse<SeasonStatus>(status, ignoreCase: true, out var parsed))
            {
                return BadRequest();
            }

            statusFilter = parsed;
        }

        var query = dbContext.Seasons.AsNoTracking().Where(s => s.LeagueId == leagueId);
        if (statusFilter is not null)
        {
            query = query.Where(s => s.Status == statusFilter.Value);
        }

        var seasons = await query.ToListAsync(cancellationToken);

        return Ok(seasons.Select(ToDto).ToList());
    }

    [HttpGet("{seasonId}")]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> GetSeason(Guid leagueId, Guid seasonId, CancellationToken cancellationToken)
    {
        var season = await dbContext.Seasons.AsNoTracking()
            .SingleOrDefaultAsync(s => s.SeasonId == seasonId && s.LeagueId == leagueId, cancellationToken);

        return season is null ? NotFound() : Ok(ToDto(season));
    }

    // Mirrors AuthController.ToProblem — see that method's own remarks on why the fallback
    // errorCode has to be overwritten explicitly rather than passed to Problem() directly.
    private ObjectResult ToProblem(Error error, int statusCode)
    {
        var problem = Problem(detail: error.Message, statusCode: statusCode);
        ((ProblemDetails)problem.Value!).Extensions["errorCode"] = error.Code;
        return problem;
    }

    private static SeasonDto ToDto(Season season) => new()
    {
        SeasonId = season.SeasonId,
        LeagueId = season.LeagueId,
        EplSeasonIdentifier = season.EplSeasonIdentifier,
        Status = season.Status.ToString(),
        StartDate = season.StartDate,
        EndDate = season.EndDate,
    };
}
