using EplFantasy.FantasyTeams;
using Xunit;

namespace EplFantasy.UnitTests.FantasyTeams;

public class FantasyTeamTests
{
    [Fact]
    public void Create_establishes_the_correct_initial_state()
    {
        var fantasyTeamId = Guid.NewGuid();
        var leagueMembershipId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var team = FantasyTeam.Create(fantasyTeamId, leagueMembershipId, seasonId, now);

        Assert.Equal(fantasyTeamId, team.FantasyTeamId);
        Assert.Equal(leagueMembershipId, team.LeagueMembershipId);
        Assert.Equal(seasonId, team.SeasonId);
        Assert.Equal(FantasyTeamStatus.Active, team.Status);
        Assert.Equal(now, team.CreatedAt);
        Assert.Equal(now, team.UpdatedAt);
    }
}
