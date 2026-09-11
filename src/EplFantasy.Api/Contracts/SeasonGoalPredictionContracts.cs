using System.ComponentModel.DataAnnotations;

namespace EplFantasy.Api.Contracts;

// IT-07 (F-010.3): shape/format validation only (see AuthContracts.cs's header comment) — mirrors
// OpenAPI's submitSeasonGoalPrediction request body and SeasonGoalPrediction schema exactly.

public sealed class SubmitSeasonGoalPredictionRequest
{
    /// <summary>Nullable so [Required] can actually detect a missing value — see CreateSeasonRequest.StartDate's own remarks on why a non-nullable value type can't.</summary>
    [Required]
    [Range(0, int.MaxValue)]
    public int? PredictedEplGoals { get; set; }
}

public sealed class SeasonGoalPredictionDto
{
    public required Guid PredictionId { get; init; }
    public required Guid SeasonId { get; init; }
    public required Guid FantasyTeamId { get; init; }
    public required int PredictedEplGoals { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public required DateTimeOffset LockedAt { get; init; }
    public int? FinalActualGoals { get; init; }
    public int? FinalAbsoluteDifference { get; init; }
}
