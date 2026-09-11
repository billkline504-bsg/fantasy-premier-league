using EplFantasy.Scoring;

namespace EplFantasy.Api.Contracts;

// IT-33 (F-008.1): getGameweekScore's exact response OpenAPI schema.

public sealed class GameweekScoreDto
{
    public required Guid GameweekScoreId { get; init; }
    public required Guid FantasyTeamId { get; init; }
    public required Guid GameweekId { get; init; }
    public required int FantasyPoints { get; init; }
    public required int CaptainPoints { get; init; }
    public required int FantasyGoalsFor { get; init; }
    public required int FantasyGoalsAgainst { get; init; }
    public required int FantasyGoalDifference { get; init; }
    public required DateTimeOffset CalculatedAt { get; init; }
    public required int RecalculatedCount { get; init; }

    public static GameweekScoreDto From(GameweekScore score) => new()
    {
        GameweekScoreId = score.GameweekScoreId,
        FantasyTeamId = score.FantasyTeamId,
        GameweekId = score.GameweekId,
        FantasyPoints = score.FantasyPoints,
        CaptainPoints = score.CaptainPoints,
        FantasyGoalsFor = score.FantasyGoalsFor,
        FantasyGoalsAgainst = score.FantasyGoalsAgainst,
        FantasyGoalDifference = score.FantasyGoalDifference,
        CalculatedAt = score.CalculatedAt,
        RecalculatedCount = score.RecalculatedCount,
    };
}
