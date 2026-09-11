using EplFantasy.Competition;
using Xunit;

namespace EplFantasy.UnitTests.Competition;

public class SeasonGoalPredictionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Submit_establishes_the_correct_initial_state()
    {
        var predictionId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var fantasyTeamId = Guid.NewGuid();
        var lockedAt = Now.AddDays(14); // Season starts two weeks out

        var prediction = SeasonGoalPrediction.Submit(predictionId, seasonId, fantasyTeamId, 1234, Now, lockedAt);

        Assert.Equal(predictionId, prediction.PredictionId);
        Assert.Equal(seasonId, prediction.SeasonId);
        Assert.Equal(fantasyTeamId, prediction.FantasyTeamId);
        Assert.Equal(1234, prediction.PredictedEplGoals);
        Assert.Equal(Now, prediction.SubmittedAt);
        Assert.Equal(lockedAt, prediction.LockedAt);
        Assert.Null(prediction.FinalActualGoals);
        Assert.Null(prediction.FinalAbsoluteDifference);
    }

    [Fact]
    public void IsLocked_is_false_before_lockedAt_and_true_from_lockedAt_onward()
    {
        var prediction = SeasonGoalPrediction.Submit(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1234, Now, Now.AddDays(14));

        Assert.False(prediction.IsLocked(Now.AddDays(13)));
        Assert.True(prediction.IsLocked(Now.AddDays(14)));
        Assert.True(prediction.IsLocked(Now.AddDays(15)));
    }

    [Fact]
    public void UpdatePrediction_changes_the_value_and_re_stamps_submittedAt_before_locking()
    {
        var prediction = SeasonGoalPrediction.Submit(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1200, Now, Now.AddDays(14));

        prediction.UpdatePrediction(1234, Now.AddDays(1), Now.AddDays(14));

        Assert.Equal(1234, prediction.PredictedEplGoals);
        Assert.Equal(Now.AddDays(1), prediction.SubmittedAt);
        Assert.Equal(Now.AddDays(14), prediction.LockedAt);
    }

    [Fact]
    public void UpdatePrediction_throws_once_locked_and_leaves_the_prediction_unchanged()
    {
        var prediction = SeasonGoalPrediction.Submit(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1200, Now, Now.AddDays(14));

        var exception = Assert.Throws<SeasonGoalPredictionLockedException>(
            () => prediction.UpdatePrediction(9999, Now.AddDays(14), Now.AddDays(14)));

        Assert.Equal("season_goal_prediction_locked", exception.ErrorCode);
        Assert.Equal(409, exception.StatusCode);
        Assert.Equal(1200, prediction.PredictedEplGoals);
    }
}
