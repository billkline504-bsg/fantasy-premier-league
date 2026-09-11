using System.ComponentModel.DataAnnotations;
using EplFantasy.Drafts;

namespace EplFantasy.Api.Contracts;

// IT-48 (F-006.4): mirrors OpenAPI's ReplacementOpportunity schema exactly — note the schema's own
// "opportunityId" field name, not "replacementOpportunityId".

public sealed class ReplacementOpportunityDto
{
    public required Guid OpportunityId { get; init; }
    public required Guid FantasyTeamId { get; init; }
    public required Guid SourcePlayerId { get; init; }
    public required DateTimeOffset GrantedAt { get; init; }
    public required string GrantReason { get; init; }
    public DateTimeOffset? SpentAt { get; init; }

    public static ReplacementOpportunityDto From(ReplacementOpportunity opportunity) => new()
    {
        OpportunityId = opportunity.ReplacementOpportunityId,
        FantasyTeamId = opportunity.FantasyTeamId,
        SourcePlayerId = opportunity.SourcePlayerId,
        GrantedAt = opportunity.GrantedAt,
        GrantReason = opportunity.GrantReason.ToString(),
        SpentAt = opportunity.SpentAt,
    };
}

/// <summary>
/// IT-48's own judgment call: no OpenAPI operation literally matches "spend a ReplacementOpportunity"
/// as its own endpoint (the spec's own wording loosely suggests reusing makeDraftPick, but a
/// Replacement pick never goes through the Draft/DraftSelection aggregate at all — see
/// ReplacementOpportunity's own remarks for the research behind that conclusion) — this request
/// shape mirrors makeDraftPick's own (`playerId` only) for consistency.
/// </summary>
public sealed class MakeReplacementPickRequest
{
    [Required]
    public Guid? PlayerId { get; set; }
}

// IT-49 (F-011.2): declareSeasonEndingInjury's exact request body OpenAPI schema.

public sealed class DeclareSeasonEndingInjuryRequest
{
    [StringLength(1000)]
    public string? Reason { get; set; }
}
