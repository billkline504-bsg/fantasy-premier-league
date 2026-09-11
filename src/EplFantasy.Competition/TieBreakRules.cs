using System.Security.Cryptography;

namespace EplFantasy.Competition;

// The seven tiers of ADR-008's resolved sequence (BRD v1.4 BR-280): League Points → Fantasy Goal
// Difference → Fantasy Goals For → Head-to-Head League Points (tied teams only) → Captain Points
// → Season Goal Prediction → random fallback. The official EPL's away-goals/neutral-venue-playoff
// provisions are intentionally not implemented (DEC-058) — the pipeline falls through directly
// from tier 4 to tier 5 when still tied.
//
// Sign convention throughout: IComparer-style — negative means `a` outranks `b` (higher points,
// smaller goal-average, etc. sort first), zero means this tier cannot separate them and the
// pipeline should fall through to the next one.

public sealed class LeaguePointsTieBreakRule : IStandingsTieBreakRule
{
    public string Name => "LeaguePoints";

    public int Compare(LeagueStanding a, LeagueStanding b, IStandingsTieBreakContext context) =>
        b.LeaguePoints.CompareTo(a.LeaguePoints);
}

public sealed class FantasyGoalDifferenceTieBreakRule : IStandingsTieBreakRule
{
    public string Name => "FantasyGoalDifference";

    public int Compare(LeagueStanding a, LeagueStanding b, IStandingsTieBreakContext context) =>
        b.FantasyGoalDifference.CompareTo(a.FantasyGoalDifference);
}

public sealed class FantasyGoalsForTieBreakRule : IStandingsTieBreakRule
{
    public string Name => "FantasyGoalsFor";

    public int Compare(LeagueStanding a, LeagueStanding b, IStandingsTieBreakContext context) =>
        b.FantasyGoalsFor.CompareTo(a.FantasyGoalsFor);
}

/// <summary>BR-122/DEC-058: head-to-head League Points between exactly the two tied FantasyTeams — no broader sub-table for a 3+-way tie, since this is evaluated pairwise during the overall sort.</summary>
public sealed class HeadToHeadLeaguePointsTieBreakRule : IStandingsTieBreakRule
{
    public string Name => "HeadToHeadLeaguePoints";

    public int Compare(LeagueStanding a, LeagueStanding b, IStandingsTieBreakContext context)
    {
        var pointsForA = context.HeadToHeadLeaguePoints(a.FantasyTeamId, b.FantasyTeamId, a.FantasyTeamId);
        var pointsForB = context.HeadToHeadLeaguePoints(a.FantasyTeamId, b.FantasyTeamId, b.FantasyTeamId);

        if (pointsForA is null || pointsForB is null)
        {
            return 0; // no completed match between this specific pair yet.
        }

        return pointsForB.Value.CompareTo(pointsForA.Value);
    }
}

public sealed class CaptainPointsTieBreakRule : IStandingsTieBreakRule
{
    public string Name => "CaptainPoints";

    public int Compare(LeagueStanding a, LeagueStanding b, IStandingsTieBreakContext context) =>
        b.CaptainPointsTotal.CompareTo(a.CaptainPointsTotal);
}

/// <summary>BR-132/BR-133: smallest ABS(Prediction − Actual) wins; at an equal difference, the prediction that was less-than-or-equal-to the actual total wins over one that was over.</summary>
public sealed class SeasonGoalPredictionTieBreakRule : IStandingsTieBreakRule
{
    public string Name => "SeasonGoalPrediction";

    public int Compare(LeagueStanding a, LeagueStanding b, IStandingsTieBreakContext context)
    {
        var predictionA = context.SeasonGoalPredictionFor(a.FantasyTeamId);
        var predictionB = context.SeasonGoalPredictionFor(b.FantasyTeamId);

        if (predictionA?.FinalAbsoluteDifference is null || predictionB?.FinalAbsoluteDifference is null)
        {
            return 0; // Season hasn't ended, or a prediction is missing — nothing to compare yet.
        }

        var byDifference = predictionA.FinalAbsoluteDifference.Value.CompareTo(predictionB.FinalAbsoluteDifference.Value);
        if (byDifference != 0)
        {
            return byDifference; // BR-132: smaller difference wins outright — ascending compare already ranks it first.
        }

        // BR-133: equally close — the at-or-under prediction wins over the over prediction.
        if (predictionA.FinalActualGoals is null)
        {
            return 0;
        }

        var aIsAtOrUnder = predictionA.PredictedEplGoals <= predictionA.FinalActualGoals.Value;
        var bIsAtOrUnder = predictionB.PredictedEplGoals <= predictionA.FinalActualGoals.Value;

        return (aIsAtOrUnder, bIsAtOrUnder) switch
        {
            (true, false) => -1,
            (false, true) => 1,
            _ => 0, // both at-or-under, or both over — BR-133 doesn't separate that; fall through.
        };
    }
}

/// <summary>
/// BR-125: "If all defined deterministic criteria remain tied, the application shall use a
/// randomized tie-break as the final fallback." Deterministic-per-Season via a stable hash
/// (SeasonId, FantasyTeamId) rather than true nondeterministic randomness — a recalculation of
/// already-decided standings (e.g. triggered by an unrelated score correction elsewhere) must not
/// silently reshuffle a random tiebreak that was already final for this Season, and the same
/// property makes this tier deterministically testable without an injected RNG.
/// </summary>
public sealed class RandomFallbackTieBreakRule : IStandingsTieBreakRule
{
    public string Name => "RandomFallback";

    public int Compare(LeagueStanding a, LeagueStanding b, IStandingsTieBreakContext context)
    {
        var hashA = ComputeHash(context.SeasonId, a.FantasyTeamId);
        var hashB = ComputeHash(context.SeasonId, b.FantasyTeamId);
        return hashA.CompareTo(hashB);
    }

    private static ulong ComputeHash(Guid seasonId, Guid fantasyTeamId)
    {
        Span<byte> input = stackalloc byte[32];
        seasonId.TryWriteBytes(input[..16]);
        fantasyTeamId.TryWriteBytes(input[16..]);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        return BitConverter.ToUInt64(hash);
    }
}
