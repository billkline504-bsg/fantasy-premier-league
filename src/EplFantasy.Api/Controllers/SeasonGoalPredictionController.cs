using EplFantasy.Api.Contracts;
using EplFantasy.Competition;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-07 (F-010.3): a FantasyTeam's Season Goal Prediction — submission only (OpenAPI Specification
/// v1.0's getSeasonGoalPrediction/submitSeasonGoalPrediction). Tie-break *usage* of an already-
/// submitted, season-ended prediction is IStandingsTieBreakRule's concern (Milestone 3), not this
/// controller's.
/// </summary>
[ApiController]
[Route("api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction")]
public class SeasonGoalPredictionController(
    ISeasonGoalPredictionService predictionService,
    EplFantasyDbContext dbContext) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.FantasyTeamOwnerOrActiveLeagueMember)]
    public async Task<IActionResult> GetSeasonGoalPrediction(Guid leagueId, Guid seasonId, Guid fantasyTeamId, CancellationToken cancellationToken)
    {
        var prediction = await dbContext.SeasonGoalPredictions.AsNoTracking()
            .SingleOrDefaultAsync(p => p.SeasonId == seasonId && p.FantasyTeamId == fantasyTeamId, cancellationToken);

        return prediction is null ? NotFound() : Ok(ToDto(prediction));
    }

    [HttpPut]
    [Authorize(Policy = AuthorizationPolicies.FantasyTeamOwner)]
    public async Task<IActionResult> SubmitSeasonGoalPrediction(
        Guid leagueId,
        Guid seasonId,
        Guid fantasyTeamId,
        SubmitSeasonGoalPredictionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await predictionService.SubmitAsync(seasonId, fantasyTeamId, request.PredictedEplGoals!.Value, cancellationToken);

        if (result.IsFailure)
        {
            return ToProblem(result.Error, StatusCodes.Status400BadRequest);
        }

        return Ok(ToDto(result.Value));
    }

    // Mirrors AuthController.ToProblem — see that method's own remarks on why the fallback
    // errorCode has to be overwritten explicitly rather than passed to Problem() directly.
    private ObjectResult ToProblem(Error error, int statusCode)
    {
        var problem = Problem(detail: error.Message, statusCode: statusCode);
        ((ProblemDetails)problem.Value!).Extensions["errorCode"] = error.Code;
        return problem;
    }

    private static SeasonGoalPredictionDto ToDto(SeasonGoalPrediction prediction) => new()
    {
        PredictionId = prediction.PredictionId,
        SeasonId = prediction.SeasonId,
        FantasyTeamId = prediction.FantasyTeamId,
        PredictedEplGoals = prediction.PredictedEplGoals,
        SubmittedAt = prediction.SubmittedAt,
        LockedAt = prediction.LockedAt,
        FinalActualGoals = prediction.FinalActualGoals,
        FinalAbsoluteDifference = prediction.FinalAbsoluteDifference,
    };
}
