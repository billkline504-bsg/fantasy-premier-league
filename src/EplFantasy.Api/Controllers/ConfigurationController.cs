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
/// IT-08 (F-003.5, core mechanics only): League-level default configuration and Season-level
/// overrides (OpenAPI Specification v1.0's getLeagueConfiguration/updateLeagueConfiguration/
/// getSeasonConfiguration/updateSeasonConfiguration). Reads are open to any active member; writes
/// are League-Administrator-only and each write routes through IT-F07's audit recorder
/// (AdminActionType.ConfigurationChanged, BR-295).
/// </summary>
[ApiController]
public class ConfigurationController(
    IConfigurationService configurationService,
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet("api/v1/leagues/{leagueId}/configuration")]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> GetLeagueConfiguration(Guid leagueId, CancellationToken cancellationToken)
    {
        var configuration = await dbContext.LeagueConfigurations.AsNoTracking()
            .SingleAsync(c => c.LeagueId == leagueId, cancellationToken);

        return Ok(LeagueConfigurationDto.From(configuration));
    }

    [HttpPut("api/v1/leagues/{leagueId}/configuration")]
    [Authorize(Policy = AuthorizationPolicies.LeagueAdministrator)]
    public async Task<IActionResult> UpdateLeagueConfiguration(Guid leagueId, UpdateLeagueConfigurationRequest request, CancellationToken cancellationToken)
    {
        var actingMembershipId = await dbContext.LeagueMemberships
            .Where(m => m.LeagueId == leagueId && m.UserId == currentUser.UserId!.Value && m.IsAdministrator && m.Status == MembershipStatus.Active)
            .Select(m => m.LeagueMembershipId)
            .SingleAsync(cancellationToken);

        var configuration = await configurationService.UpdateLeagueConfigurationAsync(leagueId, actingMembershipId, request.ToValues(), cancellationToken);

        return Ok(LeagueConfigurationDto.From(configuration));
    }

    [HttpGet("api/v1/leagues/{leagueId}/seasons/{seasonId}/configuration")]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> GetSeasonConfiguration(Guid leagueId, Guid seasonId, CancellationToken cancellationToken)
    {
        var seasonBelongsToLeague = await dbContext.Seasons.AnyAsync(s => s.SeasonId == seasonId && s.LeagueId == leagueId, cancellationToken);
        if (!seasonBelongsToLeague)
        {
            return NotFound();
        }

        var configuration = await dbContext.SeasonConfigurations.AsNoTracking()
            .SingleAsync(c => c.SeasonId == seasonId, cancellationToken);

        return Ok(SeasonConfigurationDto.From(configuration));
    }

    [HttpPut("api/v1/leagues/{leagueId}/seasons/{seasonId}/configuration")]
    [Authorize(Policy = AuthorizationPolicies.LeagueAdministrator)]
    public async Task<IActionResult> UpdateSeasonConfiguration(Guid leagueId, Guid seasonId, UpdateSeasonConfigurationRequest request, CancellationToken cancellationToken)
    {
        var actingMembershipId = await dbContext.LeagueMemberships
            .Where(m => m.LeagueId == leagueId && m.UserId == currentUser.UserId!.Value && m.IsAdministrator && m.Status == MembershipStatus.Active)
            .Select(m => m.LeagueMembershipId)
            .SingleAsync(cancellationToken);

        var result = await configurationService.UpdateSeasonConfigurationAsync(leagueId, seasonId, actingMembershipId, request.ToValues(), cancellationToken);

        if (result.IsFailure)
        {
            return NotFound();
        }

        return Ok(SeasonConfigurationDto.From(result.Value));
    }
}
