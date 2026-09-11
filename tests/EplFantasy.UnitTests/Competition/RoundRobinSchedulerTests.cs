using EplFantasy.Competition;
using Xunit;

namespace EplFantasy.UnitTests.Competition;

public class RoundRobinSchedulerTests
{
    private static List<Guid> Teams(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

    [Fact]
    public void GenerateRounds_produces_a_complete_round_robin_for_an_even_team_count()
    {
        var teams = Teams(4);

        var rounds = RoundRobinScheduler.GenerateRounds(teams, roundCount: 3); // N - 1 rounds for N = 4.

        Assert.Equal(3, rounds.Count);
        Assert.All(rounds, round => Assert.Equal(2, round.Count)); // no byes — every team plays every round.

        var everyPairPlayed = rounds.SelectMany(r => r).Select(p => (p.Home, p.Away)).ToList();
        Assert.Equal(6, everyPairPlayed.Count); // C(4,2) = 6 — every pair meets exactly once.

        // Every unordered pair of teams appears in exactly one match (BR-109: as balanced as
        // mathematically possible — a single round-robin cycle means literally once each).
        var unorderedPairs = everyPairPlayed.Select(p => (First: p.Home < p.Away ? p.Home : p.Away, Second: p.Home < p.Away ? p.Away : p.Home)).ToHashSet();
        Assert.Equal(6, unorderedPairs.Count);
    }

    [Fact]
    public void GenerateRounds_gives_every_team_exactly_one_bye_per_cycle_for_an_odd_team_count()
    {
        var teams = Teams(5);

        var rounds = RoundRobinScheduler.GenerateRounds(teams, roundCount: 5); // one full cycle for N = 5 (bye seat included).

        Assert.Equal(5, rounds.Count);
        Assert.All(rounds, round => Assert.Equal(2, round.Count)); // 5 teams -> 2 matches + 1 bye per round.

        var playingTeamsPerRound = rounds.Select(round => round.SelectMany(p => new[] { p.Home, p.Away }).ToHashSet()).ToList();
        foreach (var playing in playingTeamsPerRound)
        {
            Assert.Equal(4, playing.Count); // 4 of the 5 teams play; exactly one sits out (BR-306).
        }

        var byeCounts = teams.ToDictionary(t => t, _ => 0);
        foreach (var playing in playingTeamsPerRound)
        {
            foreach (var team in teams.Where(t => !playing.Contains(t)))
            {
                byeCounts[team]++;
            }
        }

        Assert.All(byeCounts.Values, count => Assert.Equal(1, count)); // exactly one bye each across the cycle.
    }

    [Fact]
    public void GenerateRounds_never_pairs_a_team_against_itself()
    {
        var teams = Teams(7);

        var rounds = RoundRobinScheduler.GenerateRounds(teams, roundCount: 10);

        Assert.All(rounds.SelectMany(r => r), pair => Assert.NotEqual(pair.Home, pair.Away));
    }

    [Fact]
    public void GenerateRounds_repeats_the_cycle_when_more_rounds_are_requested_than_a_single_cycle_provides()
    {
        var teams = Teams(4); // cycle length 3.

        var rounds = RoundRobinScheduler.GenerateRounds(teams, roundCount: 7); // more than two full cycles.

        Assert.Equal(7, rounds.Count);
        // Round 3 (index 3) must reproduce round 0 exactly — the cycle restarting from its own beginning.
        Assert.Equal(rounds[0].ToHashSet(), rounds[3].ToHashSet());
        Assert.Equal(rounds[1].ToHashSet(), rounds[4].ToHashSet());
        Assert.Equal(rounds[0].ToHashSet(), rounds[6].ToHashSet());
    }

    [Fact]
    public void GenerateRounds_returns_nothing_for_fewer_than_two_teams_or_a_non_positive_round_count()
    {
        Assert.Empty(RoundRobinScheduler.GenerateRounds(Teams(1), roundCount: 5));
        Assert.Empty(RoundRobinScheduler.GenerateRounds([], roundCount: 5));
        Assert.Empty(RoundRobinScheduler.GenerateRounds(Teams(4), roundCount: 0));
    }
}
