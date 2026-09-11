namespace EplFantasy.Competition;

/// <summary>
/// IT-38 (F-009.1, BR-107-BR-110): the "circle method" (a.k.a. polygon method) round-robin
/// algorithm — a fixed seat plus N-1 (or N, once an odd count's bye seat is counted) rotating
/// seats, producing a genuine round-robin cycle where every pair meets exactly once per cycle.
/// Pure and side-effect-free (no randomness of its own, no persistence) precisely so it's
/// unit-testable without a database: <see cref="GenerateRounds"/>'s own caller (the application
/// service) is what shuffles <paramref name="orderedFantasyTeamIds"/> beforehand (BR-108's own
/// randomness requirement) and persists the result.
/// </summary>
public static class RoundRobinScheduler
{
    /// <summary>
    /// Returns exactly <paramref name="roundCount"/> rounds (one per Gameweek, in the same order
    /// the caller will assign them), each a list of Home/Away pairs. An odd
    /// <paramref name="orderedFantasyTeamIds"/> count is padded with one null "bye" seat (BR-306)
    /// — whichever real team draws it in a given round gets no pairing that round at all, never a
    /// fabricated match, so that round's own returned list is simply shorter by one pair.
    /// <paramref name="roundCount"/> may exceed a single full cycle (team count, or team count - 1
    /// if even, since a bye still consumes a round) — BR-109's "same number of times as
    /// mathematically possible" is satisfied by simply repeating the same cycle from its own start
    /// again, rather than continuing to rotate indefinitely and drifting further unbalanced.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(Guid Home, Guid Away)>> GenerateRounds(
        IReadOnlyList<Guid> orderedFantasyTeamIds, int roundCount)
    {
        if (orderedFantasyTeamIds.Count < 2 || roundCount <= 0)
        {
            return [];
        }

        var seats = orderedFantasyTeamIds.Select(id => (Guid?)id).ToList();
        if (seats.Count % 2 != 0)
        {
            seats.Add(null); // BR-306: the bye seat — always present for an odd team count.
        }

        var cycleLength = seats.Count - 1;
        var cycle = new List<IReadOnlyList<(Guid Home, Guid Away)>>(cycleLength);
        var rotating = seats.ToList();

        for (var round = 0; round < cycleLength; round++)
        {
            var pairs = new List<(Guid Home, Guid Away)>();
            for (var i = 0; i < rotating.Count / 2; i++)
            {
                if (rotating[i] is { } home && rotating[^(i + 1)] is { } away)
                {
                    pairs.Add((home, away));
                }
                // else: this pairing involves the bye seat — that team gets no match this round.
            }

            cycle.Add(pairs);

            // Seat 0 stays fixed; every other seat rotates one position, wrapping the last back to
            // position 1 — the standard circle-method step that reaches every other pairing over
            // the next cycleLength - 1 rounds without ever repeating one within the same cycle.
            var last = rotating[^1];
            for (var i = rotating.Count - 1; i > 1; i--)
            {
                rotating[i] = rotating[i - 1];
            }

            rotating[1] = last;
        }

        var rounds = new List<IReadOnlyList<(Guid Home, Guid Away)>>(roundCount);
        for (var round = 0; round < roundCount; round++)
        {
            rounds.Add(cycle[round % cycleLength]);
        }

        return rounds;
    }
}
