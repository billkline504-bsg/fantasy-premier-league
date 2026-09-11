namespace EplFantasy.Rosters;

/// <summary>IT-29 (F-007.1): weekly roster submission for a FantasyTeam's own Gameweek.</summary>
public interface IRosterService
{
    /// <summary>
    /// Creates (on a FantasyTeam's first-ever submission for this Gameweek) or updates (a pre-lock
    /// resubmission) the <see cref="GameweekRoster"/> named by <paramref name="fantasyTeamId"/>/
    /// <paramref name="gameweekId"/>, fully replacing its player selection — PUT semantics, not a
    /// partial patch. <paramref name="ifMatchXmin"/>, when supplied, must match the roster's current
    /// concurrency token or <see cref="RosterConcurrencyConflictException"/> is thrown (Architecture
    /// §8.3); omitted, no staleness check is performed beyond EF's own per-request protection.
    /// Throws <see cref="SeasonGoalPredictionRequiredException"/> (BR-299) if this is the
    /// FantasyTeam's first-ever roster submission of the Season and no SeasonGoalPrediction is on
    /// file, or <see cref="InvalidRosterCompositionException"/> (BR-194/BR-279) if any submitted
    /// player isn't a currently-owned SquadPlayer of this FantasyTeam, or if
    /// <see cref="GameweekRoster.Submit"/>'s own structural invariants reject it.
    /// </summary>
    Task<GameweekRoster> SubmitAsync(
        Guid fantasyTeamId,
        Guid gameweekId,
        IReadOnlyList<Guid> playerIds,
        Guid? captainPlayerId,
        uint? ifMatchXmin,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// IT-30 (F-007.2): designates <paramref name="captainPlayerId"/> as the Captain of the
    /// FantasyTeam's already-existing Gameweek roster (Draft or Submitted). Throws
    /// <see cref="InvalidRosterCompositionException"/> (BR-046) if no roster exists yet for this
    /// FantasyTeam/Gameweek, or if <paramref name="captainPlayerId"/> is not one of that roster's
    /// selected players, or <see cref="GameweekRosterNotEditableException"/> if it is already
    /// Locked/Scored.
    /// </summary>
    Task<GameweekRoster> SetCaptainAsync(
        Guid fantasyTeamId,
        Guid gameweekId,
        Guid captainPlayerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// IT-32 (F-007.4): a League Administrator's post-deadline correction of the
    /// <see cref="GameweekRoster"/> named by <paramref name="gameweekRosterId"/> — the one path
    /// allowed to touch a Locked (or Scored) roster. <paramref name="playerIds"/> null means "leave
    /// player selection unchanged" (a Captain-only correction, via <see cref="GameweekRoster.CorrectCaptain"/>);
    /// non-null fully replaces it via <see cref="GameweekRoster.Correct"/>, re-validated against
    /// BR-194/BR-279 the same way <see cref="SubmitAsync"/> does. <paramref name="captainPlayerId"/>
    /// null always means "no Captain", whether or not <paramref name="playerIds"/> is also supplied
    /// — the same convention <see cref="SubmitAsync"/> already establishes. Writes an
    /// AdministrativeAction (<c>ActionType = RosterCorrection</c>) in the same transaction as the
    /// change (BR-099/BR-146-BR-149). Throws <see cref="GameweekRosterNotLockedException"/> if the
    /// roster is still Draft/Submitted.
    /// </summary>
    Task<GameweekRoster> CorrectAsync(
        Guid gameweekRosterId,
        IReadOnlyList<Guid>? playerIds,
        Guid? captainPlayerId,
        string reason,
        Guid actingUserId,
        CancellationToken cancellationToken = default);
}
