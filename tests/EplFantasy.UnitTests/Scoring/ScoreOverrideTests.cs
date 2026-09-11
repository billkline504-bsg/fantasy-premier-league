using EplFantasy.Scoring;
using Xunit;

namespace EplFantasy.UnitTests.Scoring;

public class ScoreOverrideTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_builds_an_active_override_with_the_given_snapshot()
    {
        var playerPerformanceId = Guid.NewGuid();
        var administratorMembershipId = Guid.NewGuid();

        var scoreOverride = ScoreOverride.Create(
            Guid.NewGuid(), playerPerformanceId, administratorMembershipId,
            originalValueJson: "{\"goals\":0}", overrideValueJson: "{\"goals\":1}", reason: "video review", Now);

        Assert.Equal(playerPerformanceId, scoreOverride.PlayerPerformanceId);
        Assert.Equal(administratorMembershipId, scoreOverride.AdministratorMembershipId);
        Assert.Equal("{\"goals\":0}", scoreOverride.OriginalValueJson);
        Assert.Equal("{\"goals\":1}", scoreOverride.OverrideValueJson);
        Assert.Equal("video review", scoreOverride.Reason);
        Assert.Equal(Now, scoreOverride.CreatedAt);
        Assert.Null(scoreOverride.UndoneAt);
        Assert.True(scoreOverride.IsActive); // BR-141: takes precedence from the moment it exists.
    }

    [Fact]
    public void Undo_sets_UndoneAt_and_IsActive_becomes_false()
    {
        var scoreOverride = ScoreOverride.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"goals\":0}", "{\"goals\":1}", null, Now);
        var undoneAt = Now.AddDays(1);

        scoreOverride.Undo(undoneAt);

        Assert.Equal(undoneAt, scoreOverride.UndoneAt);
        Assert.False(scoreOverride.IsActive);
    }

    [Fact]
    public void Undo_rejects_an_already_undone_override()
    {
        // BR-142/BR-143: "remove/undo" is a one-way timestamp — a well-behaved caller could
        // legitimately hit this via a retried/duplicate undo request, not just a mistake.
        var scoreOverride = ScoreOverride.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"goals\":0}", "{\"goals\":1}", null, Now);
        scoreOverride.Undo(Now.AddDays(1));

        Assert.Throws<ScoreOverrideAlreadyUndoneException>(() => scoreOverride.Undo(Now.AddDays(2)));
        Assert.Equal(Now.AddDays(1), scoreOverride.UndoneAt); // untouched by the rejected second attempt.
    }
}
