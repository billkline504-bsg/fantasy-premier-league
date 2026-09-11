using System.ComponentModel.DataAnnotations;

namespace EplFantasy.Api.Contracts;

// IT-06 (F-003.4): shape/format validation only (see AuthContracts.cs's header comment) — mirrors
// the OpenAPI createSeason request body and Season schema exactly.

public sealed class CreateSeasonRequest
{
    [Required]
    public string EplSeasonIdentifier { get; set; } = null!;

    /// <summary>Nullable so [Required] can actually detect a missing value — DateOnly is a non-nullable value type, so a bare `DateOnly StartDate` would just silently bind to `default` instead of failing validation.</summary>
    [Required]
    public DateOnly? StartDate { get; set; }
}

public sealed class SeasonDto
{
    public required Guid SeasonId { get; init; }
    public required Guid LeagueId { get; init; }
    public required string EplSeasonIdentifier { get; init; }
    public required string Status { get; init; }
    public required DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
}
