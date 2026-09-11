namespace EplFantasy.PlayerData;

// Persistence shapes for the Player & EPL Data context (Architecture v1.15 §6.10; physical
// schema: 06-database-migrations/migrations/V003__player_reference_data.sql).

public enum PlayerPosition
{
    Gk,
    Def,
    Mid,
    Fwd,
}

public enum FixtureStatus
{
    Scheduled,
    Postponed,
    InProgress,
    Completed,
    Abandoned,
}

/// <summary>
/// Platform-level anchor for a real-world EPL season identifier (e.g. "2026/27"); many Leagues'
/// <c>Season</c> rows (EplFantasy.Leagues) can point at the same one. See Database Migration
/// Strategy v1.0 §3 for why this exists as its own table.
/// </summary>
public class EplSeason
{
    public string EplSeasonIdentifier { get; set; } = null!;
}

public class Club
{
    public Guid ClubId { get; set; }
    public string EplClubId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string ShortName { get; set; } = null!;
}

public class Player
{
    public Guid PlayerId { get; set; }
    public string EplPlayerId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public PlayerPosition Position { get; set; }
    public Guid? CurrentClubId { get; set; }
}

public class Gameweek
{
    public Guid GameweekId { get; set; }
    public string EplSeasonIdentifier { get; set; } = null!;
    public int Number { get; set; }
    public DateTimeOffset RosterLockDeadline { get; set; }
}

public class Fixture
{
    public Guid FixtureId { get; set; }
    public string EplFixtureId { get; set; } = null!;
    public Guid GameweekId { get; set; }
    public Guid HomeClubId { get; set; }
    public Guid AwayClubId { get; set; }
    public DateTimeOffset KickoffTime { get; set; }
    public FixtureStatus Status { get; set; } = FixtureStatus.Scheduled;
    public int? HomeGoals { get; set; }
    public int? AwayGoals { get; set; }
}

/// <summary>
/// BR-329–BR-335: the real-world EPL table, recomputed from <see cref="Fixture"/> results —
/// never Administrator-editable (BR-334), unlike the Fantasy League's own standings.
/// </summary>
public class ClubStanding
{
    public string EplSeasonIdentifier { get; set; } = null!;
    public Guid ClubId { get; set; }
    public int Position { get; set; }
    public int Played { get; set; }
    public int Won { get; set; }
    public int Drawn { get; set; }
    public int Lost { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public int GoalDifference { get; set; } // database-generated column (goals_for - goals_against); never written by EF.
    public int Points { get; set; }
}
