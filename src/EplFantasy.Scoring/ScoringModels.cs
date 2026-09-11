using EplFantasy.SharedKernel;

namespace EplFantasy.Scoring;

// Persistence shapes for the Scoring context (Architecture v1.15 §6.6; physical schema:
// 06-database-migrations/migrations/V008__scoring.sql).

public enum PerformanceSource
{
    OfficialFpl,
    Manual,
}

public class PlayerPerformance
{
    public Guid PlayerPerformanceId { get; set; }
    public Guid GameweekId { get; set; }
    public Guid PlayerId { get; set; }
    public int MinutesPlayed { get; set; }
    public int FantasyPoints { get; set; }
    public int Goals { get; set; }
    public int GoalsConceded { get; set; }
    public int OwnGoals { get; set; }
    public PerformanceSource Source { get; set; }
    public bool IsOfficial { get; set; } = true;
    public DateTimeOffset RetrievedAt { get; set; }
}

public class GameweekScore
{
    public Guid GameweekScoreId { get; set; }
    public Guid FantasyTeamId { get; set; }
    public Guid GameweekId { get; set; }
    public int FantasyPoints { get; set; }
    public int CaptainPoints { get; set; }
    public int FantasyGoalsFor { get; set; }
    public int FantasyGoalsAgainst { get; set; }
    public int FantasyGoalDifference { get; set; }
    public DateTimeOffset CalculatedAt { get; set; }
    public int RecalculatedCount { get; set; }

    /// <summary>
    /// IT-33 (F-008.1, BR-073-BR-077/BR-288)/IT-34 (F-008.2/F-008.3, BR-038-BR-040/BR-043/BR-044/
    /// BR-047/BR-303/BR-304)/IT-36 (F-008.4, BR-080-BR-091): a Locked roster's scoring pass. Every
    /// parameter is supplied already computed — summing only the Starting XI (BR-044) and applying
    /// the Captain's own captain multiplier (BR-047) before ranking (BR-304) is
    /// <c>GameweekRoster.DetermineSelectionRoles</c>' own job (Rosters owns
    /// RosterPlayer.SelectionRole/CaptainPlayerId); <see cref="FantasyGoalsFor"/>/
    /// <see cref="FantasyGoalsAgainst"/>/<see cref="FantasyGoalDifference"/> come from this type's
    /// own <see cref="CalculateFantasyGoals"/>. This factory only ever assembles the final row.
    /// BR-288's double-gameweek case needs no special handling at all: official FPL already sums a
    /// player's multi-fixture total into one <see cref="PlayerPerformance.FantasyPoints"/> (and
    /// Goals/GoalsConceded/OwnGoals) value per Gameweek before this ever reads it (IT-20's own
    /// sync, per-Gameweek).
    /// </summary>
    public static GameweekScore Calculate(
        Guid gameweekScoreId,
        Guid fantasyTeamId,
        Guid gameweekId,
        int fantasyPoints,
        int captainPoints,
        int fantasyGoalsFor,
        int fantasyGoalsAgainst,
        int fantasyGoalDifference,
        DateTimeOffset now) =>
        new()
        {
            GameweekScoreId = gameweekScoreId,
            FantasyTeamId = fantasyTeamId,
            GameweekId = gameweekId,
            FantasyPoints = fantasyPoints,
            CaptainPoints = captainPoints,
            FantasyGoalsFor = fantasyGoalsFor,
            FantasyGoalsAgainst = fantasyGoalsAgainst,
            FantasyGoalDifference = fantasyGoalDifference,
            CalculatedAt = now,
            RecalculatedCount = 0,
        };

    /// <summary>
    /// IT-36 (F-008.4, BR-080-BR-091): computed over the *entire* submitted 15-player roster,
    /// regardless of RosterPlayer.SelectionRole (StartingXi/Bench) — this takes already-bucketed
    /// primitive values rather than roster/player entities, the same "pure calculation over
    /// pre-tallied primitives" shape RosterPositionCounts (EplFantasy.Rosters) already established,
    /// so this type never needs to know what a RosterPlayer or a Player.Position actually is; the
    /// caller (GameweekScoreCalculationService, which can see both) is what deliberately ignores
    /// SelectionRole here — unlike FantasyPoints, which BR-044 restricts to the Starting XI, BR-080
    /// explicitly counts all 15.
    /// <paramref name="totalGoalsAcrossRoster"/> is BR-080/BR-081-BR-085 (every position counts;
    /// own goals already excluded per BR-091 — the caller's own job, since a player's raw "Goals"
    /// and "OwnGoals" are two separate PlayerPerformance fields). <paramref name="goalkeeperGoalsConceded"/>/
    /// <paramref name="defenderGoalsConceded"/> are BR-086's two components: goalkeepers' own values
    /// summed outright, defenders' summed then divided by count (BR-087) — ordinary non-negative
    /// integer division already truncates toward zero exactly as BR-087/BR-088's own worked example
    /// demands (7 defenders-total / 5 defenders = 1, not 1.4 or 2); an empty defender list
    /// contributes zero rather than dividing by zero (no PositionalMinimum guarantees a roster can't
    /// carry zero once BR-291's own minimum happens to be configured that way). <paramref name="totalOwnGoalsAcrossRoster"/>
    /// is BR-090/BR-091 — every roster player's own goals, any position, added to Against rather
    /// than counted as a goal For.
    /// </summary>
    public static (int GoalsFor, int GoalsAgainst, int GoalDifference) CalculateFantasyGoals(
        int totalGoalsAcrossRoster,
        IReadOnlyList<int> goalkeeperGoalsConceded,
        IReadOnlyList<int> defenderGoalsConceded,
        int totalOwnGoalsAcrossRoster)
    {
        var goalkeeperComponent = goalkeeperGoalsConceded.Sum();
        var defenderComponent = defenderGoalsConceded.Count == 0 ? 0 : defenderGoalsConceded.Sum() / defenderGoalsConceded.Count;

        var goalsFor = totalGoalsAcrossRoster;
        var goalsAgainst = goalkeeperComponent + defenderComponent + totalOwnGoalsAcrossRoster;

        return (goalsFor, goalsAgainst, goalsFor - goalsAgainst);
    }

    /// <summary>
    /// IT-37 (F-008.5 AC5, BR-148): updates an already-<c>Scored</c> FantasyTeam's row in place —
    /// the opposite of <see cref="Calculate"/>, which only ever creates a fresh one — once an
    /// administrator override (or its undo) has changed what <c>IAuthoritativeValueResolver</c>
    /// now returns for a player this row depends on. Every field is fully replaced (the caller
    /// recomputes from scratch, the same as a first-time <see cref="Calculate"/> would) except
    /// <see cref="GameweekScoreId"/> itself; <see cref="RecalculatedCount"/> increments so the
    /// number of times this specific row has ever been revised is itself retained (BR-148), not
    /// just its latest value.
    /// </summary>
    public void Recalculate(
        int fantasyPoints,
        int captainPoints,
        int fantasyGoalsFor,
        int fantasyGoalsAgainst,
        int fantasyGoalDifference,
        DateTimeOffset now)
    {
        FantasyPoints = fantasyPoints;
        CaptainPoints = captainPoints;
        FantasyGoalsFor = fantasyGoalsFor;
        FantasyGoalsAgainst = fantasyGoalsAgainst;
        FantasyGoalDifference = fantasyGoalDifference;
        CalculatedAt = now;
        RecalculatedCount++;
    }
}

/// <summary>
/// Invariant 12 (BR-140–BR-145): precedence for any read is Active ScoreOverride
/// (<see cref="UndoneAt"/> is null) &gt; Official PlayerPerformance &gt; Application Calculation.
/// </summary>
public class ScoreOverride
{
    public Guid ScoreOverrideId { get; set; }
    public Guid PlayerPerformanceId { get; set; }
    public Guid AdministratorMembershipId { get; set; }
    public string OriginalValueJson { get; set; } = null!;
    public string OverrideValueJson { get; set; } = null!;
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }

    /// <summary>BR-144: "any manually overridden value shall be clearly identifiable" — this is that identification, computed rather than a separately stored flag, since it's always exactly "not yet undone."</summary>
    public bool IsActive => UndoneAt is null;

    /// <summary>
    /// IT-37 (F-008.5 AC2, BR-140/BR-144/BR-145): records an Administrator's manual correction —
    /// <paramref name="originalValueJson"/>/<paramref name="overrideValueJson"/> are the caller's
    /// own responsibility to have already resolved/serialized (this aggregate has no way to reach
    /// PlayerPerformance itself, EplFantasy.Scoring's own persistence shape, from a static factory
    /// — Invariant 12's actual precedence enforcement lives in IAuthoritativeValueResolver, not
    /// here). Always created active (<see cref="UndoneAt"/> null) — BR-141's "takes precedence over
    /// official data" is true from the moment this row exists, needing no separate activation step.
    /// </summary>
    public static ScoreOverride Create(
        Guid scoreOverrideId,
        Guid playerPerformanceId,
        Guid administratorMembershipId,
        string originalValueJson,
        string overrideValueJson,
        string? reason,
        DateTimeOffset now) =>
        new()
        {
            ScoreOverrideId = scoreOverrideId,
            PlayerPerformanceId = playerPerformanceId,
            AdministratorMembershipId = administratorMembershipId,
            OriginalValueJson = originalValueJson,
            OverrideValueJson = overrideValueJson,
            Reason = reason,
            CreatedAt = now,
            UndoneAt = null,
        };

    /// <summary>BR-142/BR-143: "remove/undo" is a timestamp, never a delete (db-tests/060_scoring_invariants.sql's own `scoring.override_undo_is_a_timestamp_not_a_delete` already proves this at the database level) — official data (as currently known) becomes authoritative again the moment this is set, with no further action needed here; IAuthoritativeValueResolver's own precedence check is just "UndoneAt is null".</summary>
    public void Undo(DateTimeOffset now)
    {
        if (UndoneAt is not null)
        {
            throw new ScoreOverrideAlreadyUndoneException();
        }

        UndoneAt = now;
    }
}

/// <summary>IT-37 (F-008.5): an Undo was attempted against a ScoreOverride that was already undone — a well-behaved caller could legitimately hit this via a retried/duplicate request, not just a mistake.</summary>
public sealed class ScoreOverrideAlreadyUndoneException() : DomainException(
    "This score override has already been undone.")
{
    public override string ErrorCode => "score_override_already_undone";

    public override int StatusCode => 409;
}
