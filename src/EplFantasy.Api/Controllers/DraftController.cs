using EplFantasy.Api.Contracts;
using EplFantasy.Drafts;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-23 (F-005.1): createDraft/listDrafts — OpenAPI Specification v1.0's Draft operations on
/// `/leagues/{leagueId}/seasons/{seasonId}/drafts`. listDrafts isn't named by any Implementation
/// Task Breakdown entry (a documentation gap, the same kind AuthController.Logout's request body
/// and IT-14's getCurrentUser already worked around) — it shares createDraft's exact route and
/// needs no domain logic of its own, so it's built alongside it here rather than left permanently
/// unbuilt. createDraft handles `draftType: Initial` (IT-23) and `Secondary` (IT-46) — no Implementation
/// Task Breakdown entry ever explicitly claimed wiring the latter into this controller (IT-45's own
/// "API: createDraft (draftType: Secondary)" line turned out to describe only the scheduling
/// proposal, not the Draft's actual creation), so it's wired here now that CreateSecondaryDraftAsync
/// exists, rather than leaving a fully-built feature permanently unreachable via the API.
/// `Replacement` is deliberately NOT a valid value here — IT-48's own research established it never
/// goes through the Draft/DraftSelection aggregate at all (see ReplacementOpportunity's own remarks)
/// — so a request for it is rejected with 400, the same as any other unsupported value.
/// getDraft (DraftPickController, IT-28) is a *different* route
/// (`/drafts/{draftId}`, not nested under League/Season) serving state this task never reaches
/// anyway (Initial creation always goes straight to InProgress, per AC1 — no Scheduled phase to
/// "start" at all); startDraft/pauseDraft remain unclaimed by any Implementation Task Breakdown
/// entry and are deliberately NOT built here.
/// </summary>
[ApiController]
[Route("api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts")]
public class DraftController(IDraftService draftService, EplFantasyDbContext dbContext) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.LeagueAdministrator)]
    public async Task<IActionResult> CreateDraft(Guid leagueId, Guid seasonId, CreateDraftRequest request, CancellationToken cancellationToken)
    {
        if (!await dbContext.Seasons.AnyAsync(s => s.SeasonId == seasonId && s.LeagueId == leagueId, cancellationToken))
        {
            return NotFound();
        }

        if (!Enum.TryParse<DraftType>(request.DraftType, ignoreCase: true, out var draftType) || draftType == DraftType.Replacement)
        {
            return BadRequest();
        }

        var draft = draftType == DraftType.Secondary
            ? await draftService.CreateSecondaryDraftAsync(seasonId, cancellationToken)
            : await draftService.CreateInitialDraftAsync(seasonId, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, DraftDto.From(draft));
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> ListDrafts(Guid leagueId, Guid seasonId, [FromQuery] string? draftType, CancellationToken cancellationToken)
    {
        if (!await dbContext.Seasons.AnyAsync(s => s.SeasonId == seasonId && s.LeagueId == leagueId, cancellationToken))
        {
            return NotFound();
        }

        var query = dbContext.Drafts.AsNoTracking().Where(d => d.SeasonId == seasonId);

        if (draftType is not null)
        {
            if (!Enum.TryParse<DraftType>(draftType, ignoreCase: true, out var parsed))
            {
                return BadRequest();
            }

            query = query.Where(d => d.DraftType == parsed);
        }

        var drafts = await query.ToListAsync(cancellationToken);

        return Ok(drafts.Select(DraftDto.From));
    }
}
