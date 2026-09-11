namespace EplFantasy.PlayerData;

/// <summary>IT-22 (F-004.5/BR-337): the opponent Club a Player's own Club faces in one Gameweek's fixture, and whether that Club is playing at home.</summary>
public sealed record PlayerGameweekOpponent(Guid OpponentClubId, bool IsHome);

/// <summary>
/// BR-337: "resolving, for a given player, the fixture (if any) in which that player's
/// Player.CurrentClubId appears for the current Gameweek, and displaying the other club in that
/// fixture as the opponent" — built ahead of its actual consumer (IT-29's Weekly Roster screen,
/// not yet built) because F-004.5's own breakdown entry names it directly: "a postponed fixture's
/// player has no opponent shown that Gameweek." A pure function over already-loaded data (no
/// database access of its own) so it stays a plain unit test — the caller is responsible for
/// loading <paramref name="gameweekFixtures"/> scoped to one specific Gameweek.
/// </summary>
public static class PlayerGameweekOpponentResolver
{
    /// <summary>
    /// Returns no opponent at all for a Postponed fixture (BR-100/BR-101) rather than the stale
    /// club it was scheduled against — a postponed fixture may yet be reassigned to an entirely
    /// different Gameweek, so showing its current opponent here would mislead a User who's
    /// expected to account for known reschedules themselves. Returns every opponent when the same
    /// Club has more than one fixture this Gameweek (BR-288's double-gameweek), and nothing at all
    /// when the Player has no current Club (BR-066, an EPL exit) or their Club has no fixture this
    /// Gameweek (a bye).
    /// </summary>
    public static IReadOnlyList<PlayerGameweekOpponent> Resolve(Guid? playerCurrentClubId, IEnumerable<Fixture> gameweekFixtures)
    {
        if (playerCurrentClubId is null)
        {
            return [];
        }

        return gameweekFixtures
            .Where(f => f.Status != FixtureStatus.Postponed && (f.HomeClubId == playerCurrentClubId || f.AwayClubId == playerCurrentClubId))
            .Select(f => f.HomeClubId == playerCurrentClubId
                ? new PlayerGameweekOpponent(f.AwayClubId, IsHome: true)
                : new PlayerGameweekOpponent(f.HomeClubId, IsHome: false))
            .ToArray();
    }
}
