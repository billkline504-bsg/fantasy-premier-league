using System.ComponentModel.DataAnnotations;
using EplFantasy.Leagues;

namespace EplFantasy.Api.Contracts;

// IT-03 (F-003.1): shape/format validation only (see AuthContracts.cs's header comment) — mirrors
// the OpenAPI CreateLeagueRequest/UpdateLeagueRequest/League schemas exactly.

public sealed class CreateLeagueRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    [StringLength(1000)]
    public string? Description { get; set; }
}

/// <summary>
/// Every field is optional (a PUT that omits a field leaves it unchanged, per League.UpdateDetails)
/// — <see cref="Status"/> is a plain string, not the <see cref="LeagueStatus"/> enum itself,
/// because this API has no global enum-as-string JSON converter configured (see UserSelfDto's
/// Status field for the same reason); LeagueController parses and validates it.
/// </summary>
public sealed class UpdateLeagueRequest
{
    [StringLength(100, MinimumLength = 1)]
    public string? Name { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    public string? Status { get; set; }
}

public sealed class LeagueDto
{
    public required Guid LeagueId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Status { get; init; }
    public required Guid CreatedByMembershipId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
