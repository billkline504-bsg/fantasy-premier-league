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
/// IT-51 (F-003.6, BR-221-BR-223): listLeagueMessages (any active member)/createLeagueMessage
/// (League Administrator only) — OpenAPI Specification v1.0's League messaging operations.
/// BR-167's output-encoding requirement is a client-side rendering concern — this controller never
/// interprets `Body` as markup itself, only stores and returns it verbatim.
/// </summary>
[ApiController]
[Route("api/v1/leagues/{leagueId}/messages")]
public class LeagueMessageController(ILeagueMessageService leagueMessageService, EplFantasyDbContext dbContext, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> ListLeagueMessages(Guid leagueId, CancellationToken cancellationToken)
    {
        var messages = await dbContext.LeagueMessages.AsNoTracking()
            .Where(m => m.LeagueId == leagueId)
            .OrderByDescending(m => m.PublishedAt)
            .ToListAsync(cancellationToken);

        return Ok(messages.Select(LeagueMessageDto.From).ToList());
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.LeagueAdministrator)]
    public async Task<IActionResult> CreateLeagueMessage(Guid leagueId, CreateLeagueMessageRequest request, CancellationToken cancellationToken)
    {
        var message = await leagueMessageService.PublishAsync(leagueId, currentUser.UserId!.Value, request.Body, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, LeagueMessageDto.From(message));
    }
}
