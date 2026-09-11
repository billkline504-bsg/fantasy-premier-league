namespace EplFantasy.Drafts;

/// <summary>IT-23/IT-46/IT-48 (F-005.1/F-006.2/F-006.4): Initial and Secondary Draft creation, and Replacement-opportunity spending (a standalone mechanism, not a third turn-based Draft type — see ReplacementOpportunity's own remarks).</summary>
public interface IDraftService
{
    /// <summary>
    /// BR-053–BR-055: creates the Season's Initial Draft directly `InProgress` with a randomized
    /// <see cref="Draft.DraftOrder"/>, locking `SeasonConfiguration.InitialSquadSize` (BR-291/BR-293)
    /// in the same transaction. Throws <see cref="SeasonNotReadyForInitialDraftException"/> if the
    /// Season isn't `Setup`, or <see cref="InsufficientFantasyTeamsException"/> (BR-302) if fewer
    /// than two FantasyTeams participate.
    /// </summary>
    Task<Draft> CreateInitialDraftAsync(Guid seasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// IT-46 (F-006.2, BR-056/BR-136-BR-138): creates the Season's Secondary Draft directly
    /// `InProgress`, its <see cref="Draft.DraftOrder"/> derived from the Season's own *current*
    /// `LeagueStanding` snapshot — worst `Position` picks first — captured once via
    /// <see cref="Draft.StandingsSnapshotTakenAt"/> and never revisited afterward, even if standings
    /// are later recalculated. Throws <see cref="StandingsSnapshotUnavailableException"/> if no
    /// `LeagueStanding` row has ever been computed for this Season yet.
    /// </summary>
    Task<Draft> CreateSecondaryDraftAsync(Guid seasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// IT-24 (F-005.2): resolves <paramref name="callerUserId"/>'s own FantasyTeam for this Draft's
    /// Season, then submits its pick — the caller is always "whoever is asking," never a
    /// FantasyTeamId the request itself supplies, since submitting on behalf of a different
    /// FantasyTeam is exactly what DraftTurnOwner's own authorization check (and MakePick's
    /// internal re-check) both exist to prevent.
    /// </summary>
    Task<DraftSelection> MakePickAsync(Guid draftId, Guid callerUserId, Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// IT-26 (F-005.3 AC2): League-Administrator-only (BR-162) — <paramref name="actingUserId"/>
    /// must resolve to an active, administrator LeagueMembership of the Draft's own League, the
    /// same "acting membership" every other Administrator-privileged application service resolves
    /// before writing its own AdministrativeAction row.
    /// </summary>
    Task<Draft> ExtendTimerAsync(Guid draftId, int additionalSeconds, Guid actingUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// IT-48 (F-006.4, BR-063/BR-064/BR-198/BR-261/BR-262): spends one of
    /// <paramref name="fantasyTeamId"/>'s own unspent <see cref="ReplacementOpportunity"/>s on
    /// <paramref name="playerId"/> — a single ad hoc selection, reusing only the same atomic
    /// ownership-guarded insert <see cref="MakePickAsync"/> uses (never <see cref="Draft.MakePick"/>
    /// itself, since a Replacement pick has no turn/timer to advance). Throws
    /// <see cref="ReplacementOpportunityAlreadySpentException"/> if already spent, or
    /// <see cref="PlayerAlreadyOwnedException"/> if <paramref name="playerId"/> is already owned by
    /// any FantasyTeam in this League/Season (BR-261/BR-262 — replacement-eligible does not mean
    /// unowned; the original FantasyTeam must actually release the player first).
    /// </summary>
    Task<ReplacementOpportunity> MakeReplacementPickAsync(Guid fantasyTeamId, Guid replacementOpportunityId, Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// IT-49 (F-011.2, BR-065/BR-067/BR-068): the Administrator-initiated eligibility path — distinct
    /// from IT-21's automatic EPL-exit path (which writes its own AdministrativeAction with a
    /// <c>null</c> <c>actingMembershipId</c>, BR-308) — sets <c>SquadPlayer.ReplacementEligibleAt</c>
    /// and grants exactly one <see cref="ReplacementOpportunity"/> to the owning FantasyTeam,
    /// respecting <c>SeasonConfiguration.ReplacementSelectionCap</c> the same way that automatic
    /// path already does (<see cref="ReplacementOpportunityPolicy"/>) — returns null rather than an
    /// opportunity when the cap has already been reached (BR-287), even though eligibility itself is
    /// still marked. Always writes a <c>SeasonEndingInjuryDeclared</c> AdministrativeAction with the
    /// real, resolved acting League Administrator's own membership id. Throws
    /// <see cref="SquadPlayerAlreadyReplacementEligibleException"/> if this SquadPlayer is already
    /// eligible (from either path).
    /// </summary>
    Task<ReplacementOpportunity?> DeclareSeasonEndingInjuryAsync(Guid leagueId, Guid squadPlayerId, string? reason, Guid actingUserId, CancellationToken cancellationToken = default);
}
