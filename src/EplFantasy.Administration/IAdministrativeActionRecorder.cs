namespace EplFantasy.Administration;

/// <summary>
/// The shared mechanism every Administrator-privileged application-service method routes through
/// to write an <c>administrative_actions</c> row (Architecture §6.8/§12.1, AP-005, BR-149) — "not
/// by relying on each handler to remember to log." Recording a change is deliberately not
/// combined with persisting it: this call only stages the audit row on the caller's own
/// <c>DbContext</c> (via constructor injection, both resolved from the same DI scope); the actual
/// commit happens when the caller's own <c>SaveChangesAsync()</c> runs, which is what makes the
/// audit row and the underlying domain change atomic (BR-099, BR-149, BR-182) — two separate
/// <c>SaveChangesAsync()</c> calls would not be atomic, so callers must make exactly one, covering
/// both the domain change and this call's staged row.
///
/// Also used for League/Season configuration changes (BR-295, <c>ActionType.ConfigurationChanged</c>)
/// — not a special case, just another call with that action type and the parameter name/old/new
/// value captured in <paramref name="beforeState"/>/<paramref name="afterState"/> — and for
/// system-generated entries (BR-308) by passing <c>actingMembershipId: null</c>.
/// </summary>
public interface IAdministrativeActionRecorder
{
    /// <param name="leagueId">The League this action belongs to.</param>
    /// <param name="actingMembershipId">The Administrator's LeagueMembership, or null for a system-generated entry (BR-308) — rendered as "System" by the audit viewer (F-011.1), never blank.</param>
    /// <param name="actionType">Which kind of administrative action this is.</param>
    /// <param name="targetEntityType">The kind of thing changed (e.g. "FantasyTeam", "League" for a configuration change) — generic, since not every action targets a FantasyTeam.</param>
    /// <param name="targetEntityId">The specific entity changed.</param>
    /// <param name="beforeState">Serialized to JSON as-is; snapshot the relevant fields before mutating them.</param>
    /// <param name="afterState">Serialized to JSON as-is; snapshot the relevant fields after mutating them.</param>
    /// <param name="reason">An optional human-supplied justification (BR-098).</param>
    void Record(
        Guid leagueId,
        Guid? actingMembershipId,
        AdminActionType actionType,
        string targetEntityType,
        Guid targetEntityId,
        object beforeState,
        object afterState,
        string? reason = null);
}
