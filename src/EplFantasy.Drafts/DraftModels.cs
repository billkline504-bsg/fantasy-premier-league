using EplFantasy.SharedKernel;

namespace EplFantasy.Drafts;

// Persistence shapes for the Draft Management context (Architecture v1.15 §6.4; physical schema:
// 06-database-migrations/migrations/V006__draft.sql).

public enum DraftType
{
    Initial,
    Secondary,
    Replacement,
}

public enum DraftStatus
{
    Scheduled,
    InProgress,
    Paused,
    Completed,
}

public enum ReplacementGrantReason
{
    EplExit,
    SeasonEndingInjury,
}

public class Draft
{
    public Guid DraftId { get; set; }
    public Guid SeasonId { get; set; }
    public DraftType DraftType { get; set; }
    public DraftStatus Status { get; set; } = DraftStatus.Scheduled;

    /// <summary>FantasyTeamId sequence — randomized for Initial (BR-054), standings-derived for Secondary (BR-056).</summary>
    public List<Guid> DraftOrder { get; set; } = [];

    public int CurrentRound { get; set; }
    public int CurrentPickIndex { get; set; }
    public int TimerSeconds { get; set; }
    public DateTimeOffset? CurrentPickDeadline { get; set; }
    public DateTimeOffset? StandingsSnapshotTakenAt { get; set; }

    /// <summary>FantasyTeamIds queued for a makeup pick after the final round (BR-282/BR-324).</summary>
    public List<Guid> PendingMakeupPicks { get; set; } = [];

    /// <summary>DraftSelection is "an Entity within Draft aggregate" (Architecture §6.4) — loaded/saved as part of this aggregate.</summary>
    public List<DraftSelection> Selections { get; set; } = [];

    /// <summary>
    /// IT-23 (F-005.1, BR-053–BR-055): an Initial Draft has no `Scheduled` phase of its own — AC1
    /// creates it directly `InProgress`, Round 1/pick 0, with its own BR-057 timer already ticking
    /// against <paramref name="now"/>. <paramref name="draftOrder"/> is expected to already be a
    /// randomized permutation (IDraftOrderRandomizer's job, not this factory's — this just stores
    /// whatever order it's given, the same "factory establishes state, doesn't compute it" split
    /// every other aggregate factory in this codebase already uses).
    /// </summary>
    public static Draft CreateInitial(Guid draftId, Guid seasonId, IReadOnlyList<Guid> draftOrder, int timerSeconds, DateTimeOffset now) =>
        new()
        {
            DraftId = draftId,
            SeasonId = seasonId,
            DraftType = DraftType.Initial,
            Status = DraftStatus.InProgress,
            DraftOrder = draftOrder.ToList(),
            CurrentRound = 1,
            CurrentPickIndex = 0,
            TimerSeconds = timerSeconds,
            CurrentPickDeadline = now.AddSeconds(timerSeconds),
        };

    /// <summary>
    /// IT-46 (F-006.2, BR-056/BR-136-BR-138): a Secondary Draft's order comes from a one-time
    /// standings snapshot rather than a random shuffle — <paramref name="draftOrder"/> is expected
    /// to already be sequenced worst-place-first (BR-056), directly from
    /// <c>LeagueStanding.Position</c> descending. That field was itself already assigned by the full
    /// ADR-008 tie-break pipeline (<c>IStandingsCalculationService</c>, IT-42), so BR-137's "complete
    /// tie-break hierarchy" requirement is satisfied by construction — this never re-runs, or needs
    /// to re-run, that pipeline separately. <see cref="StandingsSnapshotTakenAt"/> is set once, here,
    /// and nothing in this aggregate ever revisits it afterward — a later
    /// <c>IStandingsCalculationService.CalculateAsync</c> call (e.g. from a late score override's
    /// own cascade) recomputes fresh <c>LeagueStanding</c> rows elsewhere entirely, but can never
    /// reach back into an already-persisted <see cref="Draft"/> row to reorder it (BR-138).
    /// </summary>
    public static Draft CreateSecondary(Guid draftId, Guid seasonId, IReadOnlyList<Guid> draftOrder, int timerSeconds, DateTimeOffset now) =>
        new()
        {
            DraftId = draftId,
            SeasonId = seasonId,
            DraftType = DraftType.Secondary,
            Status = DraftStatus.InProgress,
            DraftOrder = draftOrder.ToList(),
            CurrentRound = 1,
            CurrentPickIndex = 0,
            TimerSeconds = timerSeconds,
            CurrentPickDeadline = now.AddSeconds(timerSeconds),
            StandingsSnapshotTakenAt = now,
        };

    /// <summary>
    /// BR-053/BR-055's snake order: odd rounds (1-based) follow <see cref="DraftOrder"/> forward,
    /// even rounds reverse it. Pure sequencing math only — actually recording who picked what
    /// (DraftSelection, ownership enforcement) is F-005.2/IT-24's job, not this one's.
    /// </summary>
    public Guid FantasyTeamIdForPick(int round, int pickIndexInRound)
    {
        var isForwardRound = round % 2 == 1;
        var effectiveIndex = isForwardRound ? pickIndexInRound : DraftOrder.Count - 1 - pickIndexInRound;

        return DraftOrder[effectiveIndex];
    }

    /// <summary>
    /// IT-24 (F-005.2, BR-052/BR-053/BR-055); extended by IT-27 (F-005.4, BR-282) to also accept a
    /// makeup pick once the regular rounds are exhausted. Records <paramref name="fantasyTeamId"/>'s
    /// selection at the current turn and advances the Draft in snake order (or, once
    /// <see cref="CurrentRound"/> has passed <paramref name="totalRounds"/>, in
    /// <see cref="PendingMakeupPicks"/> order). Re-validates turn ownership itself (AC4) rather than
    /// trusting the caller — the same "an aggregate enforces its own invariants regardless of
    /// caller" principle DomainException's own remarks describe — even though the API layer's
    /// DraftTurnOwner authorization policy already gates this in practice. AC6: completes the Draft
    /// once the last regular-or-makeup pick resolves with no <see cref="PendingMakeupPicks"/> left.
    /// Creating the corresponding SquadPlayer is a sibling concern the calling application service
    /// handles in the same transaction (the breakdown's own "Persistence" bullet, separate from
    /// "Domain") — this method only ever touches the Draft aggregate itself.
    /// </summary>
    public DraftSelection MakePick(Guid fantasyTeamId, Guid playerId, int totalRounds, DateTimeOffset now)
    {
        if (Status != DraftStatus.InProgress)
        {
            throw new DraftNotInProgressException();
        }

        var isMakeupPick = CurrentRound > totalRounds;
        var expectedFantasyTeamId = isMakeupPick ? PendingMakeupPicks[0] : FantasyTeamIdForPick(CurrentRound, CurrentPickIndex);

        if (fantasyTeamId != expectedFantasyTeamId)
        {
            throw new NotYourTurnException();
        }

        // CurrentPickIndex doubles as "picks resolved so far in the current phase" once makeup
        // picks begin — it's reset to 0 by AdvanceRegularTurn the moment that phase starts, and
        // Round is pinned to totalRounds + 1 for every makeup pick, so (DraftId, Round, PickNumber)
        // still uniquely identifies the row (ux_draft_selections_round_pick, V006).
        var selection = new DraftSelection
        {
            DraftSelectionId = Guid.NewGuid(),
            DraftId = DraftId,
            FantasyTeamId = fantasyTeamId,
            PlayerId = playerId,
            Round = isMakeupPick ? totalRounds + 1 : CurrentRound,
            PickNumber = CurrentPickIndex + 1,
            SelectedAt = now,
            IsMakeupPick = isMakeupPick,
        };
        Selections.Add(selection);

        if (isMakeupPick)
        {
            PendingMakeupPicks.RemoveAt(0);
            CurrentPickIndex++;
            AdvanceMakeupPhase(now);
        }
        else
        {
            AdvanceRegularTurn(totalRounds, now);
        }

        return selection;
    }

    /// <summary>
    /// IT-27 (F-005.4 AC1/AC4, BR-282): called by the ADR-012 sweep once it has already found
    /// <see cref="CurrentPickDeadline"/> passed with no Administrator extension. No
    /// <see cref="DraftSelection"/> is recorded for the skipped turn — the on-the-clock FantasyTeam
    /// is queued for a makeup pick instead: appended fresh the first time a regular-round pick times
    /// out, or moved to the back of the (already makeup-phase) queue when a makeup pick itself times
    /// out again — AC4's "same rule, applied recursively." Returns the <see cref="DraftPickSkipped"/>
    /// event describing what happened; logging/dispatching it is the sweep handler's job
    /// (Infrastructure layer) — the same "constructed as a log entry, not yet a dispatched object"
    /// split <c>PlayerDrafted</c>'s own remarks describe.
    /// </summary>
    public DraftPickSkipped SkipCurrentPick(int totalRounds, DateTimeOffset now)
    {
        if (Status != DraftStatus.InProgress)
        {
            throw new DraftNotInProgressException();
        }

        Guid skippedFantasyTeamId;
        bool isRequeue;

        if (CurrentRound > totalRounds)
        {
            skippedFantasyTeamId = PendingMakeupPicks[0];
            PendingMakeupPicks.RemoveAt(0);
            PendingMakeupPicks.Add(skippedFantasyTeamId);
            isRequeue = true;
            AdvanceMakeupPhase(now);
        }
        else
        {
            skippedFantasyTeamId = FantasyTeamIdForPick(CurrentRound, CurrentPickIndex);
            PendingMakeupPicks.Add(skippedFantasyTeamId);
            isRequeue = false;
            AdvanceRegularTurn(totalRounds, now);
        }

        return new DraftPickSkipped(DraftId, skippedFantasyTeamId, isRequeue, now);
    }

    /// <summary>Shared by MakePick and SkipCurrentPick for whichever regular (non-makeup) turn just resolved.</summary>
    private void AdvanceRegularTurn(int totalRounds, DateTimeOffset now)
    {
        if (CurrentPickIndex + 1 < DraftOrder.Count)
        {
            CurrentPickIndex++;
            CurrentPickDeadline = now.AddSeconds(TimerSeconds);
        }
        else if (CurrentRound < totalRounds)
        {
            CurrentRound++;
            CurrentPickIndex = 0;
            CurrentPickDeadline = now.AddSeconds(TimerSeconds);
        }
        else
        {
            // The final regularly scheduled pick just resolved (made or skipped) — everything from
            // here on is a makeup pick (AC2), signaled by CurrentRound now exceeding totalRounds.
            CurrentRound = totalRounds + 1;
            CurrentPickIndex = 0;
            AdvanceMakeupPhase(now);
        }
    }

    /// <summary>Shared by MakePick and SkipCurrentPick for whichever makeup turn just resolved (AC3).</summary>
    private void AdvanceMakeupPhase(DateTimeOffset now)
    {
        if (PendingMakeupPicks.Count == 0)
        {
            CurrentPickDeadline = null;
            Status = DraftStatus.Completed;
        }
        else
        {
            CurrentPickDeadline = now.AddSeconds(TimerSeconds);
        }
    }

    /// <summary>
    /// IT-26 (F-005.3 AC2, BR-057/BR-058): pushes CurrentPickDeadline back by
    /// <paramref name="additionalSeconds"/> — a relative extension of whatever deadline is
    /// currently ticking, not a re-anchor to "now plus". Only meaningful while a pick is actually
    /// pending; an InProgress Draft always has a set CurrentPickDeadline (Draft.CreateInitial/
    /// MakePick both establish one), so there is no null case to invent a fallback for here.
    /// </summary>
    public void ExtendTimer(int additionalSeconds)
    {
        if (Status != DraftStatus.InProgress)
        {
            throw new DraftNotInProgressException();
        }

        CurrentPickDeadline = CurrentPickDeadline!.Value.AddSeconds(additionalSeconds);
    }
}

/// <summary>
/// BR-059/BR-191/BR-192 (AP-009/AP-010): a player already owned by another FantasyTeam in this
/// League+Season. Translated from the `ux_squad_players_owned` partial-unique-index violation —
/// the actual concurrency guarantee — rather than surfacing a raw database error; also raised
/// directly by an application-level pre-check for the common (non-race) case.
/// </summary>
public sealed class PlayerAlreadyOwnedException() : DomainException(
    "This player is already owned by another FantasyTeam in this League/Season.")
{
    public override string ErrorCode => "player_already_owned";

    public override int StatusCode => 409;
}

/// <summary>
/// AC4: rejected regardless of the target player's availability — the DraftTurnOwner authorization
/// policy already prevents a well-behaved caller from ever reaching this in practice; this is the
/// aggregate's own defense-in-depth copy of the same rule.
/// </summary>
public sealed class NotYourTurnException() : DomainException(
    "It is not this FantasyTeam's turn to pick.")
{
    public override string ErrorCode => "not_your_turn";
}

/// <summary>A pick was attempted against a Draft that isn't currently InProgress (e.g. already Completed).</summary>
public sealed class DraftNotInProgressException() : DomainException(
    "This Draft is not currently in progress.")
{
    public override string ErrorCode => "draft_not_in_progress";

    public override int StatusCode => 409;
}

/// <summary>BR-302: an Initial Draft may not start with fewer than two participating FantasyTeams.</summary>
public sealed class InsufficientFantasyTeamsException() : DomainException(
    "An Initial Draft requires at least two participating FantasyTeams.")
{
    public override string ErrorCode => "insufficient_fantasy_teams";

    public override int StatusCode => 409;
}

/// <summary>
/// IT-23 (F-005.1 AC1): the Initial Draft precondition — a Season not currently `Setup` either
/// already has an Initial Draft (or is further along than that), so a second one is rejected here
/// rather than silently creating a duplicate.
/// </summary>
public sealed class SeasonNotReadyForInitialDraftException() : DomainException(
    "This Season is not in Setup — it either already has an Initial Draft or is past that point.")
{
    public override string ErrorCode => "season_not_ready_for_initial_draft";

    public override int StatusCode => 409;
}

/// <summary>IT-46 (F-006.2, BR-136): a Secondary Draft's order is seeded from a standings snapshot — no <c>LeagueStanding</c> row has ever been computed for this Season (IStandingsCalculationService, IT-42), so there is nothing to snapshot yet.</summary>
public sealed class StandingsSnapshotUnavailableException() : DomainException(
    "No LeagueStanding snapshot exists yet for this Season — the Secondary Draft's order cannot be determined.")
{
    public override string ErrorCode => "standings_snapshot_unavailable";

    public override int StatusCode => 409;
}

public class DraftSelection
{
    public Guid DraftSelectionId { get; set; }
    public Guid DraftId { get; set; }
    public Guid FantasyTeamId { get; set; }
    public Guid PlayerId { get; set; }
    public int Round { get; set; }
    public int PickNumber { get; set; }
    public DateTimeOffset SelectedAt { get; set; }
    public bool IsMakeupPick { get; set; }
}

/// <summary>
/// Physical-design addition (Database Migration Strategy v1.0 §3): Architecture §6.3 describes a
/// replacement-selection opportunity being generated but names no persisted entity for it. IT-48
/// (F-006.4): deliberately independent of <see cref="Draft"/>/<see cref="DraftSelection"/> — no
/// FK to either exists in the physical schema (V006), and the Feature Behavior Spec's own
/// Technical tasks describe spending one as "a single ad hoc selection, not a full turn-based
/// draft flow." A FantasyTeam's own <c>IDraftService.MakeReplacementPickAsync</c> (Infrastructure)
/// spends one directly — never through <see cref="Draft.MakePick"/>.
/// </summary>
public class ReplacementOpportunity
{
    public Guid ReplacementOpportunityId { get; set; }
    public Guid FantasyTeamId { get; set; }
    public Guid SourcePlayerId { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public ReplacementGrantReason GrantReason { get; set; }
    public DateTimeOffset? SpentAt { get; set; }

    /// <summary>BR-307: never auto-expires — the only way this ever changes is an explicit spend, never a background sweep.</summary>
    public void MarkSpent(DateTimeOffset now)
    {
        if (SpentAt is not null)
        {
            throw new ReplacementOpportunityAlreadySpentException();
        }

        SpentAt = now;
    }
}

/// <summary>BR-063/BR-307: an opportunity is spent exactly once — a second attempt (e.g. a stale client retry) is rejected rather than silently re-recorded.</summary>
public sealed class ReplacementOpportunityAlreadySpentException() : DomainException(
    "This replacement-selection opportunity has already been spent.")
{
    public override string ErrorCode => "replacement_opportunity_already_spent";

    public override int StatusCode => 409;
}

/// <summary>IT-49 (F-011.2, BR-065): a SquadPlayer becomes replacement-eligible exactly once — a second determination (whether automatic EPL-exit, IT-21, or another Administrator injury declaration) against an already-eligible SquadPlayer is rejected rather than silently granting a second opportunity for the same underlying event.</summary>
public sealed class SquadPlayerAlreadyReplacementEligibleException() : DomainException(
    "This SquadPlayer is already replacement-eligible.")
{
    public override string ErrorCode => "squad_player_already_replacement_eligible";

    public override int StatusCode => 409;
}
