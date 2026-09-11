namespace EplFantasy.Competition;

/// <summary>
/// ADR-008: "each tier is a named IStandingsTieBreakRule evaluated in a versioned, persisted
/// sequence per Season" — a Season pins a ruleset by name via
/// <c>SeasonConfiguration.TieBreakRulesetVersion</c> (Architecture §6.2, ADR-011), so a future
/// regulatory change to the tie-break hierarchy ships as a *new* ruleset version rather than
/// editing this one out from under Seasons already using it.
/// </summary>
public interface IStandingsTieBreakRuleset
{
    /// <summary>Matches <c>SeasonConfiguration.TieBreakRulesetVersion</c> exactly.</summary>
    string Version { get; }

    /// <summary>Evaluated in order; the first tier that returns non-zero decides the comparison.</summary>
    IReadOnlyList<IStandingsTieBreakRule> Tiers { get; }
}

/// <summary>BRD v1.4 BR-280's resolved sequence — the only ruleset version that exists today.</summary>
public sealed class V1StandingsTieBreakRuleset : IStandingsTieBreakRuleset
{
    public string Version => "v1";

    public IReadOnlyList<IStandingsTieBreakRule> Tiers { get; } =
    [
        new LeaguePointsTieBreakRule(),
        new FantasyGoalDifferenceTieBreakRule(),
        new FantasyGoalsForTieBreakRule(),
        new HeadToHeadLeaguePointsTieBreakRule(),
        new CaptainPointsTieBreakRule(),
        new SeasonGoalPredictionTieBreakRule(),
        new RandomFallbackTieBreakRule(),
    ];
}
