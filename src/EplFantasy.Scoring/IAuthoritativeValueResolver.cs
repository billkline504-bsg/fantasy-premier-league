namespace EplFantasy.Scoring;

/// <summary>
/// Invariant 12 (BR-140–BR-145): precedence for any player-statistic read is
/// Active ScoreOverride &gt; Official PlayerPerformance &gt; Application Calculation — implemented
/// once, here, so it "cannot drift between modules" (Architecture §6.6) rather than being
/// re-decided inline at every call site that needs a player statistic.
///
/// This resolver owns only the precedence and the data access to determine which tier applies —
/// never a domain-specific formula. A ScoreOverride's payload is shaped per-override (whichever
/// field(s) it corrects), and "application calculation" differs per statistic (Fantasy Points is
/// a derived formula, Goals Against a truncated average, etc.) — the caller supplies how to
/// extract its specific value from whichever tier actually applies, via the three delegates
/// below, none of which run unless that tier is the one selected.
/// </summary>
public interface IAuthoritativeValueResolver
{
    /// <param name="playerPerformanceId">Which PlayerPerformance row (i.e., which Player in which Gameweek) to resolve a value for.</param>
    /// <param name="fromActiveOverride">Called only if an active (not undone) ScoreOverride exists for this PlayerPerformance — the most recently created one, if more than one somehow exists.</param>
    /// <param name="fromOfficialData">Called only if no active override exists and the PlayerPerformance row is official (IsOfficial = true).</param>
    /// <param name="applicationCalculation">Called only if neither of the above applies — no override, and no official data yet (or a non-official/Manual row).</param>
    Task<TValue> ResolveAsync<TValue>(
        Guid playerPerformanceId,
        Func<ScoreOverride, TValue> fromActiveOverride,
        Func<PlayerPerformance, TValue> fromOfficialData,
        Func<TValue> applicationCalculation,
        CancellationToken cancellationToken = default);
}
