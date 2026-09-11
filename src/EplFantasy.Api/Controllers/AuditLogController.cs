using System.Globalization;
using System.Text;
using EplFantasy.Administration;
using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-52 (F-011.1, BR-177-BR-182/BR-321/BR-322): getAuditLog — OpenAPI Specification v1.0's
/// filterable, paginated Administrative Audit Log. Domain: none new (BeforeState/AfterState are
/// already recorded with every row by IT-F07's own IAdministrativeActionRecorder, and per-entry
/// detail expansion (BR-322) needs no separate endpoint — the list response already carries full
/// state). The one real design question this task raises: AdministrativeAction carries no
/// FantasyTeamId column of its own, so the "affected FantasyTeam" filter (BR-321) must be resolved
/// per row from whichever entity TargetEntityType/TargetEntityId actually names — only
/// GameweekRoster (RosterCorrection), ReplacementOpportunity (ReplacementEligibilityGranted), and
/// SquadPlayer (SeasonEndingInjuryDeclared) resolve to a single FantasyTeamId; everything else
/// (PlayerPerformance for ScoreOverride/ScoreOverrideUndo — global, not one team's; Draft for
/// DraftTimerExtended — affects every participating team; League/Season for ConfigurationChanged)
/// has none, and surfaces under the "__league_settings__" pseudo-value the OpenAPI spec itself
/// names — a broader "not FantasyTeam-scoped" bucket than its own "e.g., ConfigurationChanged"
/// wording suggests, since BR-321's own rule is general ("an action not scoped to a specific
/// FantasyTeam"), not limited to configuration changes alone.
/// </summary>
[ApiController]
[Route("api/v1/leagues/{leagueId}/audit")]
public class AuditLogController(EplFantasyDbContext dbContext) : ControllerBase
{
    private const string LeagueSettingsPseudoValue = "__league_settings__";

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.LeagueAdministrator)]
    public async Task<IActionResult> GetAuditLog(
        Guid leagueId,
        [FromQuery] string? actionType,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? fantasyTeamId,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200)
        {
            return BadRequest();
        }

        AdminActionType? parsedActionType = null;
        if (actionType is not null)
        {
            if (!Enum.TryParse<AdminActionType>(actionType, ignoreCase: true, out var parsed))
            {
                return BadRequest();
            }

            parsedActionType = parsed;
        }

        var wantsLeagueSettingsOnly = fantasyTeamId == LeagueSettingsPseudoValue;
        Guid? fantasyTeamIdFilter = null;
        if (fantasyTeamId is not null && !wantsLeagueSettingsOnly)
        {
            if (!Guid.TryParse(fantasyTeamId, out var parsedTeamId))
            {
                return BadRequest();
            }

            fantasyTeamIdFilter = parsedTeamId;
        }

        var query = dbContext.AdministrativeActions.AsNoTracking().Where(a => a.LeagueId == leagueId);

        if (parsedActionType is not null)
        {
            query = query.Where(a => a.ActionType == parsedActionType.Value);
        }

        if (from is not null)
        {
            query = query.Where(a => a.CreatedAt >= from.Value);
        }

        if (to is not null)
        {
            query = query.Where(a => a.CreatedAt <= to.Value);
        }

        if (cursor is not null)
        {
            if (!TryDecodeCursor(cursor, out var beforeCreatedAt, out var beforeActionId))
            {
                return BadRequest();
            }

            query = query.Where(a => a.CreatedAt < beforeCreatedAt || (a.CreatedAt == beforeCreatedAt && a.ActionId < beforeActionId));
        }

        query = query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.ActionId);

        List<AdministrativeAction> items;
        string? nextCursor;

        if (fantasyTeamIdFilter is null && !wantsLeagueSettingsOnly)
        {
            var page = await query.Take(limit + 1).ToListAsync(cancellationToken);
            items = page.Take(limit).ToList();
            nextCursor = page.Count > limit ? EncodeCursor(items[^1].CreatedAt, items[^1].ActionId) : null;
        }
        else
        {
            // The fantasyTeamId filter is a computed value, not a column — resolving it requires
            // per-TargetEntityType joins, so this branch loads every row the other filters already
            // narrow down, resolves each one's own FantasyTeamId, and paginates in memory instead.
            var candidates = await query.ToListAsync(cancellationToken);
            var fantasyTeamIdByActionId = await ResolveFantasyTeamIdsAsync(candidates, cancellationToken);
            var filtered = candidates
                .Where(a => wantsLeagueSettingsOnly ? fantasyTeamIdByActionId[a.ActionId] is null : fantasyTeamIdByActionId[a.ActionId] == fantasyTeamIdFilter)
                .ToList();

            items = filtered.Take(limit).ToList();
            nextCursor = filtered.Count > limit ? EncodeCursor(items[^1].CreatedAt, items[^1].ActionId) : null;
        }

        return Ok(new AdministrativeActionPageDto
        {
            Items = items.Select(AdministrativeActionDto.From).ToList(),
            NextCursor = nextCursor,
        });
    }

    private async Task<Dictionary<Guid, Guid?>> ResolveFantasyTeamIdsAsync(List<AdministrativeAction> actions, CancellationToken cancellationToken)
    {
        var rosterIds = actions.Where(a => a.TargetEntityType == "GameweekRoster").Select(a => a.TargetEntityId).Distinct().ToList();
        var rosterMap = rosterIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await dbContext.GameweekRosters.AsNoTracking().Where(r => rosterIds.Contains(r.GameweekRosterId)).ToDictionaryAsync(r => r.GameweekRosterId, r => r.FantasyTeamId, cancellationToken);

        var opportunityIds = actions.Where(a => a.TargetEntityType == "ReplacementOpportunity").Select(a => a.TargetEntityId).Distinct().ToList();
        var opportunityMap = opportunityIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await dbContext.ReplacementOpportunities.AsNoTracking().Where(o => opportunityIds.Contains(o.ReplacementOpportunityId)).ToDictionaryAsync(o => o.ReplacementOpportunityId, o => o.FantasyTeamId, cancellationToken);

        var squadPlayerIds = actions.Where(a => a.TargetEntityType == "SquadPlayer").Select(a => a.TargetEntityId).Distinct().ToList();
        var squadPlayerMap = squadPlayerIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await dbContext.SquadPlayers.AsNoTracking().Where(sp => squadPlayerIds.Contains(sp.SquadPlayerId)).ToDictionaryAsync(sp => sp.SquadPlayerId, sp => sp.FantasyTeamId, cancellationToken);

        var result = new Dictionary<Guid, Guid?>();
        foreach (var action in actions)
        {
            result[action.ActionId] = action.TargetEntityType switch
            {
                "GameweekRoster" => rosterMap.GetValueOrDefault(action.TargetEntityId),
                "ReplacementOpportunity" => opportunityMap.GetValueOrDefault(action.TargetEntityId),
                "SquadPlayer" => squadPlayerMap.GetValueOrDefault(action.TargetEntityId),
                _ => null,
            };
        }

        return result;
    }

    private static string EncodeCursor(DateTimeOffset createdAt, Guid actionId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{createdAt.UtcTicks}:{actionId}")));

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset createdAt, out Guid actionId)
    {
        createdAt = default;
        actionId = default;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = decoded.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParse(parts[1], out actionId))
            {
                return false;
            }

            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
