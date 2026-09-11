using System.Text.Json;
using EplFantasy.Scoring;

namespace EplFantasy.Api.Contracts;

/// <summary>
/// IT-37 (F-008.5): createScoreOverride's request body. OpenAPI Specification v1.0's own schema
/// requires only <c>playerPerformanceId</c>/<c>overrideValue</c> — <see cref="LeagueId"/> is this
/// codebase's own addition (not in the documented schema), needed to resolve which of the caller's
/// own League Administrator memberships this correction is acting/audited under, since the entity
/// being corrected (PlayerPerformance) is platform-level, not scoped to any one League — see
/// IScoreOverrideService's own remarks.
/// </summary>
public sealed class ScoreOverrideRequest
{
    public Guid PlayerPerformanceId { get; set; }
    public Guid LeagueId { get; set; }

    /// <summary>Field(s) being overridden, e.g. <c>{"goals": 2}</c> — keyed by the exact PlayerPerformance property name (BR-139-BR-145).</summary>
    public Dictionary<string, int> OverrideValue { get; set; } = [];

    public string? Reason { get; set; }
}

/// <summary>Matches OpenAPI's own ScoreOverride schema, where originalValue/overrideValue are nested JSON objects (`additionalProperties: true`), not strings — this type re-parses the stored JSON text back into an object for the response.</summary>
public sealed class ScoreOverrideDto
{
    public required Guid ScoreOverrideId { get; init; }
    public required Guid PlayerPerformanceId { get; init; }
    public required Guid AdministratorMembershipId { get; init; }
    public required Dictionary<string, int> OriginalValue { get; init; }
    public required Dictionary<string, int> OverrideValue { get; init; }
    public string? Reason { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UndoneAt { get; init; }
    public required bool IsActive { get; init; }

    public static ScoreOverrideDto From(ScoreOverride scoreOverride) => new()
    {
        ScoreOverrideId = scoreOverride.ScoreOverrideId,
        PlayerPerformanceId = scoreOverride.PlayerPerformanceId,
        AdministratorMembershipId = scoreOverride.AdministratorMembershipId,
        OriginalValue = JsonSerializer.Deserialize<Dictionary<string, int>>(scoreOverride.OriginalValueJson)!,
        OverrideValue = JsonSerializer.Deserialize<Dictionary<string, int>>(scoreOverride.OverrideValueJson)!,
        Reason = scoreOverride.Reason,
        CreatedAt = scoreOverride.CreatedAt,
        UndoneAt = scoreOverride.UndoneAt,
        IsActive = scoreOverride.IsActive,
    };
}
