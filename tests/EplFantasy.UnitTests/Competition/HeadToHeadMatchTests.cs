using EplFantasy.Competition;
using Xunit;

namespace EplFantasy.UnitTests.Competition;

/// <summary>
/// Proves IT-39's <see cref="HeadToHeadMatch.CalculateResult"/> (F-009.2 AC1-AC3, BR-112-BR-114):
/// the higher FantasyPoints total wins, equal totals draw, and re-calling it re-derives the result
/// from scratch rather than accumulating state.
/// </summary>
public class HeadToHeadMatchTests
{
    private static HeadToHeadMatch ScheduledMatch() =>
        HeadToHeadMatch.Schedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void CalculateResult_gives_the_HomeFantasyTeam_the_win_when_its_FantasyPoints_are_higher()
    {
        var match = ScheduledMatch();

        match.CalculateResult(homeFantasyPoints: 60, awayFantasyPoints: 45);

        Assert.Equal(MatchResult.HomeWin, match.Result);
        Assert.Equal(60, match.HomeScore);
        Assert.Equal(45, match.AwayScore);
    }

    [Fact]
    public void CalculateResult_gives_the_AwayFantasyTeam_the_win_when_its_FantasyPoints_are_higher()
    {
        var match = ScheduledMatch();

        match.CalculateResult(homeFantasyPoints: 30, awayFantasyPoints: 52);

        Assert.Equal(MatchResult.AwayWin, match.Result);
        Assert.Equal(30, match.HomeScore);
        Assert.Equal(52, match.AwayScore);
    }

    [Fact]
    public void CalculateResult_draws_when_both_sides_FantasyPoints_are_equal()
    {
        var match = ScheduledMatch();

        match.CalculateResult(homeFantasyPoints: 41, awayFantasyPoints: 41);

        Assert.Equal(MatchResult.Draw, match.Result);
    }

    [Fact]
    public void CalculateResult_leaves_LeaguePoints_untouched()
    {
        var match = ScheduledMatch();

        match.CalculateResult(homeFantasyPoints: 60, awayFantasyPoints: 45);

        Assert.Null(match.LeaguePointsHome);
        Assert.Null(match.LeaguePointsAway);
    }

    [Fact]
    public void CalculateResult_re_derives_the_result_from_scratch_when_called_again()
    {
        var match = ScheduledMatch();
        match.CalculateResult(homeFantasyPoints: 60, awayFantasyPoints: 45);

        match.CalculateResult(homeFantasyPoints: 20, awayFantasyPoints: 70);

        Assert.Equal(MatchResult.AwayWin, match.Result);
        Assert.Equal(20, match.HomeScore);
        Assert.Equal(70, match.AwayScore);
    }

    /// <summary>
    /// IT-40 (F-009.3 AC1, BR-115): the task breakdown's own required proof — two different
    /// configured Win/Draw/Loss values produce two different, correct outcomes (AP-006), not a
    /// single hard-coded 3/1/0 the caller happens to always pass.
    /// </summary>
    [Theory]
    [InlineData(3, 1, 0)]
    [InlineData(5, 2, 0)]
    public void AllocateLeaguePoints_awards_the_configured_Win_and_Loss_values_to_each_side(int win, int draw, int loss)
    {
        var match = ScheduledMatch();
        match.CalculateResult(homeFantasyPoints: 60, awayFantasyPoints: 45); // HomeWin.

        match.AllocateLeaguePoints(win, draw, loss);

        Assert.Equal(win, match.LeaguePointsHome);
        Assert.Equal(loss, match.LeaguePointsAway);
    }

    [Fact]
    public void AllocateLeaguePoints_awards_the_configured_Draw_value_to_both_sides()
    {
        var match = ScheduledMatch();
        match.CalculateResult(homeFantasyPoints: 45, awayFantasyPoints: 45); // Draw.

        match.AllocateLeaguePoints(win: 3, draw: 1, loss: 0);

        Assert.Equal(1, match.LeaguePointsHome);
        Assert.Equal(1, match.LeaguePointsAway);
    }

    [Fact]
    public void AllocateLeaguePoints_awards_the_AwayFantasyTeam_the_Win_value_when_it_won()
    {
        var match = ScheduledMatch();
        match.CalculateResult(homeFantasyPoints: 20, awayFantasyPoints: 70); // AwayWin.

        match.AllocateLeaguePoints(win: 3, draw: 1, loss: 0);

        Assert.Equal(0, match.LeaguePointsHome);
        Assert.Equal(3, match.LeaguePointsAway);
    }

    [Fact]
    public void AllocateLeaguePoints_throws_before_a_Result_has_been_calculated()
    {
        var match = ScheduledMatch();

        Assert.Throws<InvalidOperationException>(() => match.AllocateLeaguePoints(win: 3, draw: 1, loss: 0));
    }

    [Fact]
    public void AllocateLeaguePoints_re_derives_from_scratch_when_called_again()
    {
        var match = ScheduledMatch();
        match.CalculateResult(homeFantasyPoints: 60, awayFantasyPoints: 45); // HomeWin.
        match.AllocateLeaguePoints(win: 3, draw: 1, loss: 0);

        // A recalculation flips the result to an AwayWin — a stale re-call of AllocateLeaguePoints
        // with the same win/draw/loss values must flip which side gets which value too.
        match.CalculateResult(homeFantasyPoints: 20, awayFantasyPoints: 70);
        match.AllocateLeaguePoints(win: 3, draw: 1, loss: 0);

        Assert.Equal(0, match.LeaguePointsHome);
        Assert.Equal(3, match.LeaguePointsAway);
    }
}
