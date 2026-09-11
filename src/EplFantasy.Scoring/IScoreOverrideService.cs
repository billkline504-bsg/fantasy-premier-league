namespace EplFantasy.Scoring;

/// <summary>
/// IT-37 (F-008.5, BR-139-BR-145): a League Administrator's manual correction to an official
/// <see cref="PlayerPerformance"/> statistic. Unlike every other Administrator-privileged operation
/// in this codebase, the entity being corrected (PlayerPerformance) is platform-level, not scoped to
/// any one League — createScoreOverride's own OpenAPI request carries no <c>leagueId</c>, so
/// <paramref name="leagueId"/> here identifies which of the caller's own League Administrator
/// memberships this correction is acting/audited under (ADR-007: "checked... against the specific
/// LeagueId... in the URL/body" — this is the "body" half of that, since the route itself is flat,
/// <c>/admin/score-overrides</c>). The correction's actual *effect* is global regardless: every
/// League that happens to roster the affected player sees it via
/// <see cref="IAuthoritativeValueResolver"/>.
/// </summary>
public interface IScoreOverrideService
{
    /// <summary>
    /// <paramref name="overrideValue"/> is keyed by whichever PlayerPerformance field(s) this
    /// corrects (e.g. <c>{"goals": 2}</c>) — GameweekScoreCalculationService's own
    /// ExtractOverriddenStat already expects exactly this shape. Snapshots each named field's
    /// current value as <see cref="ScoreOverride.OriginalValueJson"/> before applying the override
    /// (BR-145), then triggers the recalculation cascade (AC5) for every already-Scored FantasyTeam
    /// this player's Gameweek performance affects.
    /// </summary>
    Task<ScoreOverride> CreateAsync(
        Guid playerPerformanceId,
        Guid leagueId,
        IReadOnlyDictionary<string, int> overrideValue,
        string? reason,
        Guid actingUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// BR-142/BR-143: undoing an override makes official data (as currently known) authoritative
    /// again and re-triggers the same recalculation cascade <see cref="CreateAsync"/> does. Unlike
    /// <see cref="CreateAsync"/>, the acting League Administrator's own League is unambiguous here
    /// — it's whichever League the original override was created under
    /// (<see cref="ScoreOverride.AdministratorMembershipId"/>'s own League), so this takes no
    /// separate leagueId parameter.
    /// </summary>
    Task<ScoreOverride> UndoAsync(Guid scoreOverrideId, Guid actingUserId, CancellationToken cancellationToken = default);
}
