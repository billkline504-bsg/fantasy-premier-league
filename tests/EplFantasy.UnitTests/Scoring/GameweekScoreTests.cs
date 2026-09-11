using EplFantasy.Scoring;
using Xunit;

namespace EplFantasy.UnitTests.Scoring;

public class GameweekScoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Calculate_assembles_the_row_from_its_already_computed_inputs()
    {
        // The actual Starting-XI summation and Captain-multiplier logic lives in
        // GameweekRoster.DetermineSelectionRoles (Rosters owns RosterPlayer.SelectionRole/
        // CaptainPlayerId), and the Goals math in this type's own CalculateFantasyGoals (IT-36) —
        // this factory just assembles the already-computed result (IT-33/IT-34/IT-36).
        var fantasyTeamId = Guid.NewGuid();
        var gameweekId = Guid.NewGuid();

        var score = GameweekScore.Calculate(
            Guid.NewGuid(), fantasyTeamId, gameweekId,
            fantasyPoints: 17, captainPoints: 9, fantasyGoalsFor: 3, fantasyGoalsAgainst: 2, fantasyGoalDifference: 1, Now);

        Assert.Equal(fantasyTeamId, score.FantasyTeamId);
        Assert.Equal(gameweekId, score.GameweekId);
        Assert.Equal(17, score.FantasyPoints);
        Assert.Equal(9, score.CaptainPoints);
        Assert.Equal(3, score.FantasyGoalsFor);
        Assert.Equal(2, score.FantasyGoalsAgainst);
        Assert.Equal(1, score.FantasyGoalDifference);
        Assert.Equal(Now, score.CalculatedAt);
        Assert.Equal(0, score.RecalculatedCount);
    }

    [Fact]
    public void CalculateFantasyGoals_matches_the_BRDs_own_worked_example()
    {
        // BR-087/BR-088's literal worked example: five defenders' clubs conceded 1+2+1+1+2 = 7
        // goals between them; 7 / 5 = 1.4, truncated toward zero to 1 (Testing Strategy §2, BR-250).
        var defenderGoalsConceded = new[] { 1, 2, 1, 1, 2 };

        var (goalsFor, goalsAgainst, goalDifference) = GameweekScore.CalculateFantasyGoals(
            totalGoalsAcrossRoster: 0, goalkeeperGoalsConceded: [], defenderGoalsConceded, totalOwnGoalsAcrossRoster: 0);

        Assert.Equal(0, goalsFor);
        Assert.Equal(1, goalsAgainst); // the defender component alone, per the worked example.
        Assert.Equal(-1, goalDifference);
    }

    [Fact]
    public void CalculateFantasyGoals_sums_BR080_Goals_For_across_the_whole_roster()
    {
        // BR-080-BR-085: every position's goals count toward Goals For, regardless of SelectionRole
        // (the caller's own job to include all 15, not just the Starting XI) — this method only
        // ever sees whatever total the caller hands it.
        var (goalsFor, _, _) = GameweekScore.CalculateFantasyGoals(
            totalGoalsAcrossRoster: 5, goalkeeperGoalsConceded: [], defenderGoalsConceded: [], totalOwnGoalsAcrossRoster: 0);

        Assert.Equal(5, goalsFor);
    }

    [Fact]
    public void CalculateFantasyGoals_sums_the_Goalkeeper_component_without_averaging()
    {
        // BR-086's first component — unlike the defender average, a goalkeeper's own conceded
        // total is added outright, no division.
        var (_, goalsAgainst, _) = GameweekScore.CalculateFantasyGoals(
            totalGoalsAcrossRoster: 0, goalkeeperGoalsConceded: [3], defenderGoalsConceded: [], totalOwnGoalsAcrossRoster: 0);

        Assert.Equal(3, goalsAgainst);
    }

    [Fact]
    public void CalculateFantasyGoals_adds_own_goals_to_Against_rather_than_For()
    {
        // BR-090/BR-091: an own goal never counts as a goal For (the caller already excludes it
        // from totalGoalsAcrossRoster); it adds directly to Against instead.
        var (goalsFor, goalsAgainst, goalDifference) = GameweekScore.CalculateFantasyGoals(
            totalGoalsAcrossRoster: 4, goalkeeperGoalsConceded: [], defenderGoalsConceded: [], totalOwnGoalsAcrossRoster: 2);

        Assert.Equal(4, goalsFor);
        Assert.Equal(2, goalsAgainst);
        Assert.Equal(2, goalDifference);
    }

    [Fact]
    public void CalculateFantasyGoals_treats_zero_defenders_as_a_zero_contribution_not_a_division_by_zero()
    {
        var (_, goalsAgainst, _) = GameweekScore.CalculateFantasyGoals(
            totalGoalsAcrossRoster: 0, goalkeeperGoalsConceded: [1], defenderGoalsConceded: [], totalOwnGoalsAcrossRoster: 0);

        Assert.Equal(1, goalsAgainst); // just the goalkeeper component — no exception, no phantom defender contribution.
    }

    [Fact]
    public void CalculateFantasyGoals_combines_every_component_into_GoalDifference()
    {
        var (goalsFor, goalsAgainst, goalDifference) = GameweekScore.CalculateFantasyGoals(
            totalGoalsAcrossRoster: 10,
            goalkeeperGoalsConceded: [2],
            defenderGoalsConceded: [1, 2, 1, 1, 2], // truncates to 1, per the BRD example.
            totalOwnGoalsAcrossRoster: 1);

        Assert.Equal(10, goalsFor);
        Assert.Equal(4, goalsAgainst); // 2 (goalkeeper) + 1 (defenders, truncated) + 1 (own goal).
        Assert.Equal(6, goalDifference);
    }

    [Fact]
    public void Recalculate_replaces_every_field_and_increments_RecalculatedCount()
    {
        // IT-37 (F-008.5 AC5, BR-148): the opposite of Calculate — updates an already-Scored row
        // in place rather than creating a fresh one, retaining GameweekScoreId/FantasyTeamId/
        // GameweekId while replacing every computed field and bumping RecalculatedCount.
        var score = GameweekScore.Calculate(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            fantasyPoints: 10, captainPoints: 6, fantasyGoalsFor: 1, fantasyGoalsAgainst: 1, fantasyGoalDifference: 0, Now);
        var recalculatedAt = Now.AddHours(2);

        score.Recalculate(fantasyPoints: 15, captainPoints: 8, fantasyGoalsFor: 2, fantasyGoalsAgainst: 1, fantasyGoalDifference: 1, recalculatedAt);

        Assert.Equal(15, score.FantasyPoints);
        Assert.Equal(8, score.CaptainPoints);
        Assert.Equal(2, score.FantasyGoalsFor);
        Assert.Equal(1, score.FantasyGoalsAgainst);
        Assert.Equal(1, score.FantasyGoalDifference);
        Assert.Equal(recalculatedAt, score.CalculatedAt);
        Assert.Equal(1, score.RecalculatedCount);
    }

    [Fact]
    public void Recalculate_increments_RecalculatedCount_across_repeated_calls()
    {
        var score = GameweekScore.Calculate(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            fantasyPoints: 10, captainPoints: 6, fantasyGoalsFor: 1, fantasyGoalsAgainst: 1, fantasyGoalDifference: 0, Now);

        score.Recalculate(11, 6, 1, 1, 0, Now.AddHours(1));
        score.Recalculate(12, 6, 1, 1, 0, Now.AddHours(2));

        Assert.Equal(2, score.RecalculatedCount);
    }
}
