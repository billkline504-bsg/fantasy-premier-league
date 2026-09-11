namespace EplFantasy.Competition;

/// <summary>
/// ADR-008's "ordered list of pluggable comparator strategies loaded from configuration/database,
/// not a hard-coded if/else chain" — resolves a Season's configured ruleset version into a single
/// <see cref="IComparer{T}"/> that chains every tier in order, stopping at the first one that
/// separates two FantasyTeams. Pure and synchronous by design (see IStandingsTieBreakRule's own
/// remarks) — building the comparer never touches the database itself; the caller loads
/// <see cref="IStandingsTieBreakContext"/> once upfront (via
/// <c>IStandingsTieBreakContextLoader</c>, EplFantasy.Infrastructure) for however many FantasyTeams
/// it's about to rank.
/// </summary>
public sealed class StandingsTieBreakPipeline(IEnumerable<IStandingsTieBreakRuleset> rulesets)
{
    public IComparer<LeagueStanding> GetComparer(string rulesetVersion, IStandingsTieBreakContext context)
    {
        var ruleset = rulesets.FirstOrDefault(r => r.Version == rulesetVersion)
            ?? throw new InvalidOperationException($"Unknown standings tie-break ruleset version \"{rulesetVersion}\".");

        return Comparer<LeagueStanding>.Create((a, b) =>
        {
            foreach (var tier in ruleset.Tiers)
            {
                var result = tier.Compare(a, b, context);
                if (result != 0)
                {
                    return result;
                }
            }

            // Unreachable in practice: the last tier (RandomFallbackTieBreakRule) is a total
            // order over distinct FantasyTeamIds and should never itself return 0.
            return 0;
        });
    }
}
