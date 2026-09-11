using EplFantasy.SharedKernel;

namespace EplFantasy.Rosters;

// Persistence shapes for the Roster Management context (Architecture v1.15 §6.5; physical
// schema: 06-database-migrations/migrations/V007__roster.sql).

public enum RosterStatus
{
    Draft,
    Submitted,
    Locked,
    Scored,
}

public enum SelectionRole
{
    StartingXi,
    Bench,
}

/// <summary>
/// IT-29 (F-007.1 AC1/AC2, BR-041/BR-211/BR-279): the four BR-279 position categories, tallied
/// either as a Season's configured minimums or as a specific submission's actual counts — the same
/// shape either way. EplFantasy.Rosters has no project reference to EplFantasy.PlayerData (module-
/// dependency ordering, ModuleDependencyTests), so <see cref="GameweekRoster.Submit"/> never sees a
/// raw <c>PlayerPosition</c> — the calling application service tallies each submitted player's
/// already-resolved position into this shape first.
/// </summary>
public sealed record RosterPositionCounts(int Goalkeepers, int Defenders, int Midfielders, int Forwards);

public class GameweekRoster
{
    public Guid GameweekRosterId { get; set; }
    public Guid FantasyTeamId { get; set; }
    public Guid GameweekId { get; set; }
    public RosterStatus Status { get; set; } = RosterStatus.Draft;
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public Guid? CaptainPlayerId { get; set; }

    /// <summary>
    /// BR-305: true only when the ADR-012 sweep — not a user Submit — performed the
    /// Draft → Submitted transition by copying the FantasyTeam's last Locked roster. Suppresses
    /// the database's <c>trg_enforce_gameweek_roster_size</c> deferred-constraint trigger.
    /// </summary>
    public bool IsCarriedForward { get; set; }

    // No explicit concurrency-token property: the EF configuration (Infrastructure) calls
    // Npgsql's UseXminAsConcurrencyToken(), which maps PostgreSQL's built-in xmin system column
    // as a shadow property — Architecture §8.3, Database Migration Strategy v1.0 §3. Nothing to
    // declare here.

    public List<RosterPlayer> Players { get; set; } = [];

    /// <summary>
    /// IT-29 (F-007.1 AC1/AC2/AC5/AC6; F-007.2 AC1/AC2): validates and records a full-replace
    /// weekly roster submission — a fresh (Draft-status) roster's first submission, or a pre-lock
    /// resubmission that replaces the prior selection entirely, PUT-style (an omitted
    /// <paramref name="captainPlayerId"/> clears any previously-designated captain, it does not
    /// preserve it). The DB trigger (<c>trg_enforce_gameweek_roster_size</c>) only re-checks the
    /// *count*, deferred to COMMIT as defense-in-depth — positional-minimum composition and the
    /// captain-membership check have no DB-level equivalent at all, so this method is their only
    /// enforcement point. BR-299's cross-context precondition (a missing SeasonGoalPrediction) and
    /// BR-194's ownership check (every playerId a currently-owned SquadPlayer) both need data this
    /// aggregate can't see — the calling application service checks those itself, before ever
    /// reaching this method.
    /// </summary>
    public void Submit(
        IReadOnlyList<Guid> playerIds,
        Guid? captainPlayerId,
        int weeklyRosterSize,
        RosterPositionCounts positionalMinimums,
        RosterPositionCounts actualPositionCounts,
        DateTimeOffset now)
    {
        if (Status is RosterStatus.Locked or RosterStatus.Scored)
        {
            throw new GameweekRosterNotEditableException();
        }

        ValidateCompositionOrThrow(playerIds, captainPlayerId, weeklyRosterSize, positionalMinimums, actualPositionCounts);
        ReplacePlayers(playerIds, captainPlayerId);
        Status = RosterStatus.Submitted;
        SubmittedAt = now;
    }

    /// <summary>
    /// IT-30 (F-007.2 AC1/AC3/AC4, BR-045/BR-046/BR-195): designates one of this roster's
    /// already-selected players as Captain — narrower than <see cref="Submit"/>'s PUT-style full
    /// replace, it only ever touches <see cref="CaptainPlayerId"/>/<see cref="RosterPlayer.IsCaptain"/>,
    /// leaving Status/SubmittedAt/every player untouched. Works against a Draft or Submitted roster
    /// alike (AC1); a Locked/Scored roster is rejected the same way <see cref="Submit"/> rejects one —
    /// <see cref="CorrectCaptain"/> is the Administrator's own post-lock equivalent.
    /// </summary>
    public void SetCaptain(Guid captainPlayerId)
    {
        if (Status is RosterStatus.Locked or RosterStatus.Scored)
        {
            throw new GameweekRosterNotEditableException();
        }

        AssignCaptainOrThrow(captainPlayerId);
    }

    /// <summary>
    /// IT-31 (F-007.3 AC3, BR-093/BR-094): the ADR-012 sweep's own time-driven transition, once a
    /// Gameweek's RosterLockDeadline (BR-093) has passed for a roster the FantasyTeam actually
    /// submitted. Not exposed via any API endpoint — BR-095/BR-096 mean nothing but this sweep may
    /// ever lock a roster — so, unlike <see cref="Submit"/>/<see cref="SetCaptain"/>, this takes no
    /// precondition on <see cref="Status"/>: the sweep only ever calls it on a roster it has just
    /// queried as <see cref="RosterStatus.Submitted"/>, the same "caller already established the
    /// precondition" trust <see cref="CreateCarriedForward"/> extends to its own caller.
    /// </summary>
    public void Lock(DateTimeOffset now)
    {
        Status = RosterStatus.Locked;
        LockedAt = now;
    }

    /// <summary>
    /// IT-33 (F-008.1): the Locked → Scored transition, once official scoring has actually run for
    /// this roster (a <c>GameweekScore</c> row, EplFantasy.Scoring, now exists for it) — the exact
    /// condition <c>getGameweekScore</c>'s own 404 ("roster still Locked, not Scored") checks for.
    /// Not exposed via any API endpoint, the same "only the background process that earned this
    /// transition may make it" trust <see cref="Lock"/> itself already extends to its own caller
    /// (the ADR-012 sweep there; the scoring calculation service here) — so, like
    /// <see cref="Lock"/>, this takes no precondition on <see cref="Status"/>.
    /// </summary>
    public void MarkScored()
    {
        Status = RosterStatus.Scored;
    }

    /// <summary>
    /// IT-34 (F-008.2/F-008.3 AC1/AC2, BR-038-BR-040/BR-043/BR-044/BR-047/BR-303/BR-304): computes
    /// every RosterPlayer's <see cref="RosterPlayer.SelectionRole"/> from each one's raw official
    /// Gameweek points (<paramref name="officialPointsByPlayerId"/> — unmultiplied, straight from
    /// <c>IAuthoritativeValueResolver</c>). BR-304: the Captain's own value is doubled first
    /// (BR-047's official FPL captain multiplier — always a fixed ×2, "no application-defined
    /// multiplier" — so this aggregate applies it directly rather than accepting one as a
    /// parameter), and the Starting XI is ranked on that already-multiplied value, not a bare one
    /// multiplied only afterward. The top 11 (BR-044's own fixed EPL-standard number — never
    /// SeasonConfiguration-driven, unlike <see cref="RosterPositionCounts"/>'s WeeklyRosterSize)
    /// become <see cref="SelectionRole.StartingXi"/>, the remainder <see cref="SelectionRole.Bench"/>;
    /// an 11th/12th-place points tie breaks on a stable ascending PlayerId ordering (BR-303) —
    /// arbitrary but reproducible, never random or advantage-conferring. Returns each player's own
    /// final (post-multiplier) points, keyed by PlayerId, so the caller can sum the Starting XI's
    /// total and read the Captain's own contribution (BR-047's <c>GameweekScore.CaptainPoints</c>)
    /// without re-deriving either — the aggregate that just computed them is the only place that
    /// should.
    /// </summary>
    public IReadOnlyDictionary<Guid, int> DetermineSelectionRoles(IReadOnlyDictionary<Guid, int> officialPointsByPlayerId)
    {
        const int captainMultiplier = 2;
        const int startingXiSize = 11;

        var finalPointsByPlayerId = Players.ToDictionary(
            p => p.PlayerId,
            p => (p.PlayerId == CaptainPlayerId ? captainMultiplier : 1) * officialPointsByPlayerId.GetValueOrDefault(p.PlayerId));

        var ranked = Players
            .OrderByDescending(p => finalPointsByPlayerId[p.PlayerId])
            .ThenBy(p => p.PlayerId)
            .ToList();

        for (var i = 0; i < ranked.Count; i++)
        {
            ranked[i].SelectionRole = i < startingXiSize ? SelectionRole.StartingXi : SelectionRole.Bench;
        }

        return finalPointsByPlayerId;
    }

    /// <summary>
    /// IT-31 (F-007.3 AC6-AC8, BR-305): the ADR-012 sweep's carry-forward fallback for a
    /// FantasyTeam that never submitted a roster for this Gameweek before its deadline — there is
    /// no persisted Draft row to begin with (<see cref="Submit"/>'s Draft→Submitted transition
    /// never ran for it), so the sweep builds this row directly at Submitted,
    /// <see cref="IsCarriedForward"/> = true (suppressing the database's
    /// trg_enforce_gameweek_roster_size trigger, since the copied selection may legitimately be
    /// undersized or empty, AC7/AC8) — then the sweep immediately calls <see cref="Lock"/> on the
    /// result, the same "copied forward and locked" single operation AC6 describes.
    /// <paramref name="carriedForwardPlayerIds"/> is expected to already have any no-longer-owned
    /// player dropped (AC8) — this factory doesn't re-check SquadPlayer ownership itself, the
    /// calling sweep handler does, the same "aggregate can't see FantasyTeams data" split
    /// <see cref="Submit"/>'s own BR-194 ownership check already established.
    /// <paramref name="captainPlayerId"/> likewise carries over only if that player survived the
    /// ownership filter — a null Captain (the FantasyTeam's first-ever Gameweek, AC7, or every
    /// carried-forward player having since been dropped) is a legitimate outcome, not an error.
    /// </summary>
    public static GameweekRoster CreateCarriedForward(
        Guid gameweekRosterId,
        Guid fantasyTeamId,
        Guid gameweekId,
        IReadOnlyList<Guid> carriedForwardPlayerIds,
        Guid? captainPlayerId,
        DateTimeOffset now)
    {
        var roster = new GameweekRoster
        {
            GameweekRosterId = gameweekRosterId,
            FantasyTeamId = fantasyTeamId,
            GameweekId = gameweekId,
            Status = RosterStatus.Submitted,
            SubmittedAt = now,
            CaptainPlayerId = captainPlayerId,
            IsCarriedForward = true,
        };

        foreach (var playerId in carriedForwardPlayerIds)
        {
            roster.Players.Add(new RosterPlayer
            {
                GameweekRosterId = gameweekRosterId,
                PlayerId = playerId,
                IsCaptain = playerId == captainPlayerId,
            });
        }

        return roster;
    }

    /// <summary>
    /// IT-32 (F-007.4 AC1/AC2/AC4, BR-097/BR-098/BR-146-BR-149): a League Administrator's
    /// post-deadline correction — the one path allowed to replace player selection on a Locked (or
    /// Scored) roster, the exact state <see cref="Submit"/> refuses to touch
    /// (<see cref="GameweekRosterNotEditableException"/>) and <see cref="GameweekRosterNotLockedException"/>
    /// exists to require here. Reuses <see cref="Submit"/>'s own composition invariants (exact
    /// WeeklyRosterSize, PositionalMinimums, Captain membership) — not because BR-098 demands it,
    /// but because <c>trg_enforce_gameweek_roster_size</c> still applies to any UPDATE against an
    /// already-Locked, non-carried-forward roster, and because BR-098's "no special recalculation
    /// path" only makes sense if the corrected roster is one the eventual Starting XI/Bench split
    /// (BR-038-BR-040) can process exactly like a normal user submission. Marks the result no
    /// longer <see cref="IsCarriedForward"/> — whatever this roster was before, it is now an
    /// authoritative Administrator-set selection, not the sweep's own automatic continuation.
    /// Status/SubmittedAt/LockedAt are left exactly as they were (still Locked, or Scored) — a
    /// correction replaces data, it doesn't re-run the lock transition (BR-098).
    /// </summary>
    public void Correct(
        IReadOnlyList<Guid> playerIds,
        Guid? captainPlayerId,
        int weeklyRosterSize,
        RosterPositionCounts positionalMinimums,
        RosterPositionCounts actualPositionCounts)
    {
        if (Status is RosterStatus.Draft or RosterStatus.Submitted)
        {
            throw new GameweekRosterNotLockedException();
        }

        ValidateCompositionOrThrow(playerIds, captainPlayerId, weeklyRosterSize, positionalMinimums, actualPositionCounts);
        ReplacePlayers(playerIds, captainPlayerId);
    }

    /// <summary>
    /// IT-32 (F-007.4): the Administrator's own post-lock equivalent of <see cref="SetCaptain"/> —
    /// used when a correction only needs to change the Captain, leaving player selection (and
    /// therefore <see cref="IsCarriedForward"/>) untouched. <paramref name="captainPlayerId"/> may
    /// be null to clear the Captain outright, unlike <see cref="SetCaptain"/> which only ever
    /// designates one.
    /// </summary>
    public void CorrectCaptain(Guid? captainPlayerId)
    {
        if (Status is RosterStatus.Draft or RosterStatus.Submitted)
        {
            throw new GameweekRosterNotLockedException();
        }

        AssignCaptainOrThrow(captainPlayerId);
    }

    /// <summary>Shared by <see cref="Submit"/> and <see cref="Correct"/>: every BR-279/BR-046 structural rule a full player-list replacement must satisfy, collected rather than failing fast so a caller can fix every violation in one round trip.</summary>
    private static void ValidateCompositionOrThrow(
        IReadOnlyList<Guid> playerIds,
        Guid? captainPlayerId,
        int weeklyRosterSize,
        RosterPositionCounts positionalMinimums,
        RosterPositionCounts actualPositionCounts)
    {
        var violations = new List<string>();

        if (playerIds.Count != weeklyRosterSize)
        {
            violations.Add($"Exactly {weeklyRosterSize} players are required (submitted {playerIds.Count}).");
        }

        if (playerIds.Distinct().Count() != playerIds.Count)
        {
            violations.Add("Each player may be submitted only once.");
        }

        if (actualPositionCounts.Goalkeepers < positionalMinimums.Goalkeepers)
        {
            violations.Add($"At least {positionalMinimums.Goalkeepers} Goalkeeper(s) are required (submitted {actualPositionCounts.Goalkeepers}).");
        }

        if (actualPositionCounts.Defenders < positionalMinimums.Defenders)
        {
            violations.Add($"At least {positionalMinimums.Defenders} Defender(s) are required (submitted {actualPositionCounts.Defenders}).");
        }

        if (actualPositionCounts.Midfielders < positionalMinimums.Midfielders)
        {
            violations.Add($"At least {positionalMinimums.Midfielders} Midfielder(s) are required (submitted {actualPositionCounts.Midfielders}).");
        }

        if (actualPositionCounts.Forwards < positionalMinimums.Forwards)
        {
            violations.Add($"At least {positionalMinimums.Forwards} Forward(s) are required (submitted {actualPositionCounts.Forwards}).");
        }

        if (captainPlayerId is not null && !playerIds.Contains(captainPlayerId.Value))
        {
            violations.Add("The designated Captain must be one of the submitted players.");
        }

        if (violations.Count > 0)
        {
            throw new InvalidRosterCompositionException(violations);
        }
    }

    /// <summary>Shared by <see cref="Submit"/> and <see cref="Correct"/>: a full PUT-style replacement of Players/CaptainPlayerId — the caller has already validated composition. Clears IsCarriedForward, since a fresh player list is no longer whatever the sweep last copied.</summary>
    private void ReplacePlayers(IReadOnlyList<Guid> playerIds, Guid? captainPlayerId)
    {
        Players.Clear();
        foreach (var playerId in playerIds)
        {
            Players.Add(new RosterPlayer
            {
                GameweekRosterId = GameweekRosterId,
                PlayerId = playerId,
                IsCaptain = playerId == captainPlayerId,
            });
        }

        CaptainPlayerId = captainPlayerId;
        IsCarriedForward = false;
    }

    /// <summary>
    /// Shared by <see cref="SetCaptain"/> and <see cref="CorrectCaptain"/>: BR-046/Invariant 5's
    /// membership check, plus flipping every RosterPlayer.IsCaptain flag to match. A null
    /// <paramref name="captainPlayerId"/> always means "no Captain" — nothing to check membership
    /// against. This only updates in-memory state for correctness/DTO-building purposes — actually
    /// persisting a Captain *reassignment* (an existing true flag moving to a different row) needs
    /// care at the persistence layer (RosterService's own remarks) that has no place in a pure
    /// domain method.
    /// </summary>
    private void AssignCaptainOrThrow(Guid? captainPlayerId)
    {
        if (captainPlayerId is not null && Players.All(p => p.PlayerId != captainPlayerId))
        {
            throw new InvalidRosterCompositionException(["The designated Captain must be one of the roster's selected players."]);
        }

        foreach (var player in Players)
        {
            player.IsCaptain = player.PlayerId == captainPlayerId;
        }

        CaptainPlayerId = captainPlayerId;
    }
}

public class RosterPlayer
{
    public Guid GameweekRosterId { get; set; }
    public Guid PlayerId { get; set; }
    public bool IsCaptain { get; set; }

    /// <summary>Computed post-scoring (BR-044); null before the Gameweek is Scored.</summary>
    public SelectionRole? SelectionRole { get; set; }
}

/// <summary>BR-041/BR-211/BR-279 (AC2): the submitted roster fails one or more structural invariants — wrong size, a duplicate player, an unmet positional minimum, or a Captain not among the submitted players. Every violated rule is listed, not just the first one found, so a caller can fix the submission in one round trip rather than one rejection at a time.</summary>
public sealed class InvalidRosterCompositionException(IReadOnlyList<string> violations) : DomainException(
    string.Join(" ", violations))
{
    public IReadOnlyList<string> Violations { get; } = violations;

    public override string ErrorCode => "invalid_roster_composition";
}

/// <summary>A Submit was attempted against a GameweekRoster that is already Locked or Scored — that's F-007.4's post-lock correction path, a different operation entirely, not a resubmission.</summary>
public sealed class GameweekRosterNotEditableException() : DomainException(
    "This Gameweek roster is Locked and can no longer be edited by its owner.")
{
    public override string ErrorCode => "gameweek_roster_not_editable";

    public override int StatusCode => 409;
}

/// <summary>IT-32 (F-007.4, BR-146): an Administrator's Correct/CorrectCaptain was attempted against a GameweekRoster that is still Draft or Submitted — there is nothing to correct yet, since the FantasyTeam owner's own pre-lock Submit/SetCaptain already covers that roster.</summary>
public sealed class GameweekRosterNotLockedException() : DomainException(
    "An administrative correction can only be made to a Locked (or Scored) Gameweek roster.")
{
    public override string ErrorCode => "gameweek_roster_not_locked";

    public override int StatusCode => 409;
}

/// <summary>BR-299: this is the FantasyTeam's first-ever roster submission this Season, and no SeasonGoalPrediction (F-010.3) has been recorded yet.</summary>
public sealed class SeasonGoalPredictionRequiredException() : DomainException(
    "Submit your Season Goal Prediction before submitting your first Weekly Roster.")
{
    public override string ErrorCode => "season_goal_prediction_required";

    public override int StatusCode => 409;
}

/// <summary>Architecture §8.3: the caller's If-Match header named a version of this GameweekRoster that is no longer current (someone else's edit landed first) — or named one at all against a roster that doesn't exist yet.</summary>
public sealed class RosterConcurrencyConflictException() : DomainException(
    "This Gameweek roster has changed since you last read it — reload and resubmit.")
{
    public override string ErrorCode => "roster_concurrency_conflict";

    public override int StatusCode => 409;
}
