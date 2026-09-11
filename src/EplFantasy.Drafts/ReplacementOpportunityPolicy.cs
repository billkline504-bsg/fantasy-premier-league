namespace EplFantasy.Drafts;

/// <summary>
/// IT-48 (F-006.4, BR-287/BR-291): a pure, DB-free calculation extracted from the grant path
/// (originally IT-21's <c>PlayerDataSyncService.GrantReplacementEligibilityForExitAsync</c>, now
/// also IT-49's Administrator-initiated injury path) so both call one shared, unit-testable rule
/// rather than duplicating the same two-line check. BR-287's own default is uncapped — a null
/// <c>SeasonConfiguration.ReplacementSelectionCap</c> — but a League may configure a fixed cap
/// instead, enforced here at *grant* time (never at spend time — an opportunity already granted
/// remains spendable indefinitely regardless of later cap changes, BR-307).
/// </summary>
public static class ReplacementOpportunityPolicy
{
    /// <summary>
    /// <paramref name="alreadyGrantedCount"/> counts every opportunity ever granted to this
    /// FantasyTeam this Season, spent or not (the physical schema's own comment on
    /// replacement_opportunities: "once a FantasyTeam's unspent-plus-spent count reaches the cap").
    /// </summary>
    public static bool CanGrantAnotherOpportunity(int? replacementSelectionCap, int alreadyGrantedCount) =>
        replacementSelectionCap is not { } cap || alreadyGrantedCount < cap;
}
