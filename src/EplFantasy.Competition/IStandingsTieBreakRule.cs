namespace EplFantasy.Competition;

/// <summary>
/// ADR-008: one tier of the standings tie-break hierarchy, implemented as a pluggable comparator
/// strategy rather than a hard-coded if/else chain (BR-122, AP-006 — "configurable... regulatory
/// changes can be accommodated"). Deliberately synchronous and side-effect-free: all data a tier
/// might need (head-to-head match results, season goal predictions) is loaded once upfront into
/// an <see cref="IStandingsTieBreakContext"/> before any comparisons run, so ranking a Season's
/// FantasyTeams is one set of queries, not one query per pairwise comparison during a sort.
/// </summary>
public interface IStandingsTieBreakRule
{
    /// <summary>A short, stable name for logging/diagnostics — e.g. "LeaguePoints", "HeadToHeadLeaguePoints".</summary>
    string Name { get; }

    /// <summary>
    /// Standard <see cref="IComparer{T}"/> convention: negative if <paramref name="a"/> ranks
    /// ahead of <paramref name="b"/> (i.e. <paramref name="a"/> is the "better" team at this
    /// tier), positive if <paramref name="b"/> ranks ahead, zero if this tier cannot separate them
    /// (the pipeline falls through to the next tier).
    /// </summary>
    int Compare(LeagueStanding a, LeagueStanding b, IStandingsTieBreakContext context);
}

/// <summary>
/// Everything a tier beyond the plain <see cref="LeagueStanding"/> fields might need, pre-loaded
/// for every FantasyTeam being ranked in one Season — see
/// <c>IStandingsTieBreakContextLoader</c> (EplFantasy.Infrastructure) for how this gets built.
/// </summary>
public interface IStandingsTieBreakContext
{
    Guid SeasonId { get; }

    /// <summary>BR-122/DEC-058: head-to-head League Points between exactly two FantasyTeams, keyed by the unordered pair. Looked up regardless of which side was "home" for a given match.</summary>
    int? HeadToHeadLeaguePoints(Guid fantasyTeamIdA, Guid fantasyTeamIdB, Guid forFantasyTeamId);

    /// <summary>BR-131's stored FinalAbsoluteDifference/FinalActualGoals for a FantasyTeam's SeasonGoalPrediction, or null if not yet available (prediction missing, or the Season hasn't ended).</summary>
    SeasonGoalPrediction? SeasonGoalPredictionFor(Guid fantasyTeamId);
}
