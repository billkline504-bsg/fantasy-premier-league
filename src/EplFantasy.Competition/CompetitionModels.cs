using EplFantasy.SharedKernel;

namespace EplFantasy.Competition;

// Persistence shapes for the Competition context (Architecture v1.15 §6.7; physical schema:
// 06-database-migrations/migrations/V009__competition.sql).

public enum MatchResult
{
    HomeWin,
    AwayWin,
    Draw,
}

/// <summary>BR-306: a bye week is the absence of a row for that FantasyTeam/Gameweek, never a fabricated match.</summary>
public class HeadToHeadMatch
{
    public Guid MatchId { get; set; }
    public Guid SeasonId { get; set; }
    public Guid GameweekId { get; set; }
    public Guid HomeFantasyTeamId { get; set; }
    public Guid AwayFantasyTeamId { get; set; }
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public MatchResult? Result { get; set; }
    public int? LeaguePointsHome { get; set; }
    public int? LeaguePointsAway { get; set; }

    /// <summary>
    /// IT-38 (F-009.1, BR-107-BR-110): one scheduled fixture, created empty (every score/result/
    /// points field null until F-009.2/IT-39 actually plays it out) — BR-110's "persisted, then
    /// immutable except via an explicit administrative action" is exactly why this only ever
    /// establishes the pairing itself, nothing else.
    /// </summary>
    public static HeadToHeadMatch Schedule(Guid matchId, Guid seasonId, Guid gameweekId, Guid homeFantasyTeamId, Guid awayFantasyTeamId) =>
        new()
        {
            MatchId = matchId,
            SeasonId = seasonId,
            GameweekId = gameweekId,
            HomeFantasyTeamId = homeFantasyTeamId,
            AwayFantasyTeamId = awayFantasyTeamId,
        };

    /// <summary>
    /// IT-39 (F-009.2 AC1-AC3, BR-112-BR-114): the higher of the two sides' own
    /// <c>GameweekScore.FantasyPoints</c> wins, equal scores draw — no other factor (Goals For/
    /// Against, Captain, etc.) enters into a *match* result the way it does the Season-level
    /// tie-break hierarchy (ADR-008/IT-F10), since a single Gameweek head-to-head has no "tie" to
    /// break at all (BR-113 makes an equal score a legitimate draw outright). Deliberately leaves
    /// <see cref="LeaguePointsHome"/>/<see cref="LeaguePointsAway"/> untouched — allocating those
    /// from <c>SeasonConfiguration.LeaguePoints</c> is F-009.3/IT-40's own separate task. Safe to
    /// call repeatedly (a score recalculation re-deriving the same or a different result) — it
    /// simply overwrites every field here with the freshly given values, the same "re-derive from
    /// scratch" convention <c>GameweekScore.Recalculate</c> already established.
    /// </summary>
    public void CalculateResult(int homeFantasyPoints, int awayFantasyPoints)
    {
        HomeScore = homeFantasyPoints;
        AwayScore = awayFantasyPoints;
        Result = homeFantasyPoints.CompareTo(awayFantasyPoints) switch
        {
            > 0 => MatchResult.HomeWin,
            < 0 => MatchResult.AwayWin,
            _ => MatchResult.Draw,
        };
    }

    /// <summary>
    /// IT-40 (F-009.3, BR-115-BR-117/BR-291): awards each side its own configured League Points
    /// for the already-<see cref="CalculateResult"/>-derived <see cref="Result"/> — <paramref
    /// name="win"/>/<paramref name="draw"/>/<paramref name="loss"/> always come from that Season's
    /// own <c>SeasonConfiguration.LeaguePoints{Win,Draw,Loss}</c> (ADR-011), never a hard-coded
    /// `3`/`1`/`0` literal, so a League that configures different values (BR-291's own per-League/
    /// per-Season override) allocates correctly. Safe to call repeatedly, the same "re-derive from
    /// scratch" convention <see cref="CalculateResult"/> itself already established — a later call
    /// simply overwrites both fields with the freshly given values.
    /// </summary>
    public void AllocateLeaguePoints(int win, int draw, int loss)
    {
        if (Result is not { } result)
        {
            throw new InvalidOperationException(
                "Cannot allocate League Points before CalculateResult has determined this match's Result.");
        }

        (LeaguePointsHome, LeaguePointsAway) = result switch
        {
            MatchResult.HomeWin => (win, loss),
            MatchResult.AwayWin => (loss, win),
            MatchResult.Draw => (draw, draw),
            _ => throw new ArgumentOutOfRangeException(nameof(Result), result, "Unknown MatchResult."),
        };
    }
}

/// <summary>
/// Read-optimized, recalculated aggregate (Architecture §6.7) — a row per Gameweek, not only per
/// Season, so BR-217's point-in-time standings queries are a plain lookup.
/// </summary>
public class LeagueStanding
{
    public Guid SeasonId { get; set; }
    public Guid FantasyTeamId { get; set; }
    public Guid AsOfGameweekId { get; set; }
    public int LeaguePoints { get; set; }
    public int Played { get; set; }
    public int Won { get; set; }
    public int Drawn { get; set; }
    public int Lost { get; set; }
    public int FantasyGoalsFor { get; set; }
    public int FantasyGoalsAgainst { get; set; }
    public int FantasyGoalDifference { get; set; }
    public int CaptainPointsTotal { get; set; }
    public int Position { get; set; }

    /// <summary>
    /// IT-42 (F-010.1, BR-118/BR-215): a from-scratch tally for one FantasyTeam as of one
    /// Gameweek — <paramref name="fantasyGoalDifference"/> is taken as already computed by the
    /// caller (mirrors <c>GameweekScore.Calculate</c>'s own convention of accepting the derived
    /// value rather than recomputing it here). <see cref="Position"/> starts at 0 — it isn't
    /// knowable from a single FantasyTeam's own totals; it only exists once every FantasyTeam
    /// being ranked this Gameweek has been tallied and run through the ADR-008 tie-break pipeline
    /// (<see cref="AssignPosition"/>), a separate, necessarily-second pass over the full set.
    /// </summary>
    public static LeagueStanding Calculate(
        Guid seasonId,
        Guid fantasyTeamId,
        Guid asOfGameweekId,
        int leaguePoints,
        int played,
        int won,
        int drawn,
        int lost,
        int fantasyGoalsFor,
        int fantasyGoalsAgainst,
        int fantasyGoalDifference,
        int captainPointsTotal) =>
        new()
        {
            SeasonId = seasonId,
            FantasyTeamId = fantasyTeamId,
            AsOfGameweekId = asOfGameweekId,
            LeaguePoints = leaguePoints,
            Played = played,
            Won = won,
            Drawn = drawn,
            Lost = lost,
            FantasyGoalsFor = fantasyGoalsFor,
            FantasyGoalsAgainst = fantasyGoalsAgainst,
            FantasyGoalDifference = fantasyGoalDifference,
            CaptainPointsTotal = captainPointsTotal,
            Position = 0,
        };

    /// <summary>BR-118/BR-216: 1-based, assigned only after this FantasyTeam's own totals have been ranked against every other FantasyTeam in the same Gameweek snapshot via the full ADR-008 tie-break pipeline (never a partial/ad hoc ordering).</summary>
    public void AssignPosition(int position) => Position = position;
}

public class SeasonGoalPrediction
{
    public Guid PredictionId { get; set; }
    public Guid SeasonId { get; set; }
    public Guid FantasyTeamId { get; set; }
    public int PredictedEplGoals { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset LockedAt { get; set; }
    public int? FinalActualGoals { get; set; }
    public int? FinalAbsoluteDifference { get; set; }

    /// <summary>
    /// IT-07 (F-010.3, BR-126/BR-127/BR-299): the first submission for a (Season, FantasyTeam)
    /// pair. <paramref name="lockedAt"/> is computed by the caller (ISeasonGoalPredictionService) —
    /// the Season's own StartDate under normal submission, or <paramref name="now"/> itself under
    /// the BR-299 late-submission fallback (the Season has already started when this is the
    /// FantasyTeam's *first* submission) — this factory only establishes the row; deciding which
    /// applies needs the Season's StartDate, a cross-aggregate read this factory doesn't have.
    /// </summary>
    public static SeasonGoalPrediction Submit(
        Guid predictionId,
        Guid seasonId,
        Guid fantasyTeamId,
        int predictedEplGoals,
        DateTimeOffset now,
        DateTimeOffset lockedAt) =>
        new()
        {
            PredictionId = predictionId,
            SeasonId = seasonId,
            FantasyTeamId = fantasyTeamId,
            PredictedEplGoals = predictedEplGoals,
            SubmittedAt = now,
            LockedAt = lockedAt,
        };

    /// <summary>BR-128: once the Season begins, the prediction is locked — true from the moment `now` reaches <see cref="LockedAt"/>, never proactively flipped by a background job (mirrors IT-04's Invitation.IsAcceptable convention).</summary>
    public bool IsLocked(DateTimeOffset now) => now >= LockedAt;

    /// <summary>The PUT endpoint's "or update" half — changes an existing, not-yet-locked prediction. Throws <see cref="SeasonGoalPredictionLockedException"/> if already locked (BR-128).</summary>
    public void UpdatePrediction(int predictedEplGoals, DateTimeOffset now, DateTimeOffset lockedAt)
    {
        if (IsLocked(now))
        {
            throw new SeasonGoalPredictionLockedException();
        }

        PredictedEplGoals = predictedEplGoals;
        SubmittedAt = now;
        LockedAt = lockedAt;
    }
}

/// <summary>IT-07 (BR-127/BR-128): a well-behaved caller can legitimately hit this just by submitting after the Season has started (or after a prior BR-299 late-submission already locked it) — an expected, routine failure (OpenAPI's documented 409), not a caller mistake.</summary>
public sealed class SeasonGoalPredictionLockedException() : DomainException(
    "The Season Goal Prediction is locked and can no longer be changed.")
{
    public override string ErrorCode => "season_goal_prediction_locked";

    public override int StatusCode => 409;
}
