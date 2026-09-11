using System.Globalization;
using System.Text;
using EplFantasy.Api.Contracts;
using EplFantasy.Drafts;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.Infrastructure.Idempotency;
using EplFantasy.PlayerData;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-24/IT-26/IT-28 (F-005.2/F-005.3/F-005.5): makeDraftPick/extendDraftTimer/getDraft/
/// listDraftSelections/getDraftPlayerPool — OpenAPI Specification v1.0's `/drafts/{draftId}/...`
/// routes, differently-shaped from DraftController's own
/// `/leagues/{leagueId}/seasons/{seasonId}/drafts` (this resource is addressed by draftId alone,
/// not nested under League/Season at all) — a separate controller rather than forcing one shape
/// onto the other. startDraft/pauseDraft remain unclaimed by any Implementation Task Breakdown
/// entry — not built here. PlayerAlreadyOwnedException/NotYourTurnException/
/// DraftNotInProgressException are left to propagate — IT-F14's GlobalExceptionHandler already
/// maps any DomainException to its own ErrorCode/StatusCode automatically, the same as every other
/// domain-exception-throwing action in this codebase. IT-28's own three reads share one
/// x-authorization ("active member of the Draft's League") via the new DraftLeagueMember policy —
/// the same {draftId}-only route-indirection DraftLeagueAdministrator already established for
/// extendDraftTimer (IT-26); AC9/AC11's skip/re-queue visibility (BR-323/BR-325) needs a Reporting-
/// context projection the breakdown's own Domain bullet ("none new") and Tests bullet (only
/// position/search/sort) don't call for here — deliberately left to whichever future task actually
/// builds that projection, the getDraftPlayerPool's "Remaining undrafted players" framing (not
/// "every player, flagged") is why the player pool simply omits already-owned players rather than
/// returning an isOwned flag DraftPlayerPoolEntry's own OpenAPI schema doesn't define.
/// </summary>
[ApiController]
[Route("api/v1/drafts/{draftId}")]
public class DraftPickController(
    IDraftService draftService,
    ICurrentUserAccessor currentUser,
    EplFantasyDbContext dbContext) : ControllerBase
{
    [HttpPost("picks")]
    [Authorize(Policy = AuthorizationPolicies.DraftTurnOwner)]
    [Idempotent]
    public async Task<IActionResult> MakeDraftPick(Guid draftId, MakeDraftPickRequest request, CancellationToken cancellationToken)
    {
        var selection = await draftService.MakePickAsync(draftId, currentUser.UserId!.Value, request.PlayerId!.Value, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, DraftSelectionDto.From(selection));
    }

    [HttpPost("timer/extend")]
    [Authorize(Policy = AuthorizationPolicies.DraftLeagueAdministrator)]
    public async Task<IActionResult> ExtendDraftTimer(Guid draftId, ExtendDraftTimerRequest request, CancellationToken cancellationToken)
    {
        var draft = await draftService.ExtendTimerAsync(draftId, request.AdditionalSeconds, currentUser.UserId!.Value, cancellationToken);

        return Ok(DraftDto.From(draft));
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.DraftLeagueMember)]
    public async Task<IActionResult> GetDraft(Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts.AsNoTracking().SingleAsync(d => d.DraftId == draftId, cancellationToken);

        return Ok(DraftDto.From(draft));
    }

    /// <summary>BR-207/BR-282: pick history in the order picks occurred — (Round, PickNumber) is unique per Draft (ux_draft_selections_round_pick, V006) and already increases in occurrence order for both regular and makeup picks, so it doubles as the page cursor with no extra column needed.</summary>
    [HttpGet("selections")]
    [Authorize(Policy = AuthorizationPolicies.DraftLeagueMember)]
    public async Task<IActionResult> ListDraftSelections(
        Guid draftId,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200)
        {
            return BadRequest();
        }

        var query = dbContext.DraftSelections.AsNoTracking().Where(s => s.DraftId == draftId);

        if (cursor is not null)
        {
            if (!TryDecodeCursor(cursor, out var afterRound, out var afterPickNumber))
            {
                return BadRequest();
            }

            query = query.Where(s => s.Round > afterRound || (s.Round == afterRound && s.PickNumber > afterPickNumber));
        }

        var page = await query.OrderBy(s => s.Round).ThenBy(s => s.PickNumber).Take(limit + 1).ToListAsync(cancellationToken);
        var items = page.Take(limit).ToList();
        var nextCursor = page.Count > limit ? EncodeCursor(items[^1].Round, items[^1].PickNumber) : null;

        return Ok(new DraftSelectionPageDto
        {
            Items = items.Select(DraftSelectionDto.From).ToList(),
            NextCursor = nextCursor,
        });
    }

    /// <summary>BR-310–BR-313: undrafted players only ("remaining" pool) — an already-owned player is simply absent rather than present-with-a-flag, since DraftPlayerPoolEntry's own OpenAPI schema defines no such flag. Shares its position/search/sort implementation with getSquad (IT-25) via PlayerPoolSorting.</summary>
    [HttpGet("player-pool")]
    [Authorize(Policy = AuthorizationPolicies.DraftLeagueMember)]
    public async Task<IActionResult> GetDraftPlayerPool(
        Guid draftId,
        PlayerPosition? position,
        string? search,
        string? sort,
        CancellationToken cancellationToken)
    {
        var draft = await dbContext.Drafts.AsNoTracking().SingleAsync(d => d.DraftId == draftId, cancellationToken);
        var season = await dbContext.Seasons.AsNoTracking().SingleAsync(s => s.SeasonId == draft.SeasonId, cancellationToken);

        var ownedPlayerIds = await dbContext.SquadPlayers.AsNoTracking()
            .Where(sp => sp.SeasonId == draft.SeasonId && sp.IsCurrentlyOwned)
            .Select(sp => sp.PlayerId)
            .ToListAsync(cancellationToken);

        var query = dbContext.Players.AsNoTracking().Where(p => !ownedPlayerIds.Contains(p.PlayerId));

        if (position is not null)
        {
            query = query.Where(p => p.Position == position);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalizedSearch = search.ToLowerInvariant();
            query = query.Where(p => p.Name.ToLower().Contains(normalizedSearch));
        }

        var players = await query.ToListAsync(cancellationToken);

        var clubIds = players.Where(p => p.CurrentClubId is not null).Select(p => p.CurrentClubId!.Value).Distinct().ToList();
        var clubNamesById = await dbContext.Clubs.AsNoTracking()
            .Where(c => clubIds.Contains(c.ClubId))
            .ToDictionaryAsync(c => c.ClubId, c => c.Name, cancellationToken);

        // BR-313: statistics resolve to zero, not omission, before the Season's first Gameweek is
        // scored — the same LEFT JOIN-equivalent lookup getSquad (IT-25) already established.
        var playerIds = players.Select(p => p.PlayerId).ToList();
        var statsByPlayerId = await dbContext.PlayerSeasonStatistics.AsNoTracking()
            .Where(s => s.EplSeasonIdentifier == season.EplSeasonIdentifier && playerIds.Contains(s.PlayerId))
            .ToDictionaryAsync(s => s.PlayerId, cancellationToken);

        var withClubNames = players
            .Select(p => (
                Dto: DraftPlayerPoolEntryDto.From(p, statsByPlayerId.GetValueOrDefault(p.PlayerId)),
                ClubName: p.CurrentClubId is { } clubId ? clubNamesById.GetValueOrDefault(clubId, "") : ""))
            .ToList();

        var sorted = PlayerPoolSorting.Apply(
            withClubNames,
            sort,
            name: dto => dto.PlayerName,
            position: dto => dto.Position,
            minutesPlayed: dto => dto.SeasonMinutesPlayed,
            gamesPlayed: dto => dto.SeasonGamesPlayed,
            fantasyPoints: dto => dto.SeasonFantasyPoints);

        return Ok(sorted);
    }

    private static string EncodeCursor(int round, int pickNumber) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{round}:{pickNumber}")));

    private static bool TryDecodeCursor(string cursor, out int round, out int pickNumber)
    {
        round = 0;
        pickNumber = 0;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = decoded.Split(':');
            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out round)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pickNumber);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
