using EplFantasy.PlayerData;
using Xunit;

namespace EplFantasy.UnitTests.PlayerData;

/// <summary>IT-22 (F-004.5/BR-337): proves the opponent-resolution rule, most importantly that a Postponed fixture yields no opponent rather than a stale one.</summary>
public class PlayerGameweekOpponentResolverTests
{
    private static Fixture MakeFixture(Guid homeClubId, Guid awayClubId, FixtureStatus status = FixtureStatus.Scheduled) => new()
    {
        FixtureId = Guid.NewGuid(),
        EplFixtureId = Guid.NewGuid().ToString(),
        GameweekId = Guid.NewGuid(),
        HomeClubId = homeClubId,
        AwayClubId = awayClubId,
        KickoffTime = DateTimeOffset.UtcNow,
        Status = status,
    };

    [Fact]
    public void Resolve_returns_the_away_club_when_the_players_club_is_home()
    {
        var home = Guid.NewGuid();
        var away = Guid.NewGuid();
        var fixture = MakeFixture(home, away);

        var opponents = PlayerGameweekOpponentResolver.Resolve(home, [fixture]);

        var opponent = Assert.Single(opponents);
        Assert.Equal(away, opponent.OpponentClubId);
        Assert.True(opponent.IsHome);
    }

    [Fact]
    public void Resolve_returns_the_home_club_when_the_players_club_is_away()
    {
        var home = Guid.NewGuid();
        var away = Guid.NewGuid();
        var fixture = MakeFixture(home, away);

        var opponents = PlayerGameweekOpponentResolver.Resolve(away, [fixture]);

        var opponent = Assert.Single(opponents);
        Assert.Equal(home, opponent.OpponentClubId);
        Assert.False(opponent.IsHome);
    }

    [Fact]
    public void Resolve_returns_nothing_for_a_postponed_fixture_rather_than_a_stale_opponent()
    {
        var home = Guid.NewGuid();
        var away = Guid.NewGuid();
        var fixture = MakeFixture(home, away, FixtureStatus.Postponed);

        var opponents = PlayerGameweekOpponentResolver.Resolve(home, [fixture]);

        Assert.Empty(opponents);
    }

    [Fact]
    public void Resolve_returns_nothing_when_the_player_has_no_current_club()
    {
        var fixture = MakeFixture(Guid.NewGuid(), Guid.NewGuid());

        var opponents = PlayerGameweekOpponentResolver.Resolve(null, [fixture]);

        Assert.Empty(opponents);
    }

    [Fact]
    public void Resolve_returns_nothing_when_the_players_club_has_no_fixture_this_gameweek()
    {
        var playerClubId = Guid.NewGuid();
        var fixture = MakeFixture(Guid.NewGuid(), Guid.NewGuid()); // an unrelated fixture between two other clubs.

        var opponents = PlayerGameweekOpponentResolver.Resolve(playerClubId, [fixture]);

        Assert.Empty(opponents);
    }

    [Fact]
    public void Resolve_returns_every_non_postponed_opponent_for_a_double_gameweek()
    {
        var playerClubId = Guid.NewGuid();
        var firstOpponent = Guid.NewGuid();
        var secondOpponent = Guid.NewGuid();
        var fixtures = new[]
        {
            MakeFixture(playerClubId, firstOpponent),
            MakeFixture(secondOpponent, playerClubId),
        };

        var opponents = PlayerGameweekOpponentResolver.Resolve(playerClubId, fixtures);

        Assert.Equal(2, opponents.Count);
        Assert.Contains(opponents, o => o.IsHome && o.OpponentClubId == firstOpponent);
        Assert.Contains(opponents, o => !o.IsHome && o.OpponentClubId == secondOpponent);
    }
}
