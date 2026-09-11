using EplFantasy.Drafts;
using Xunit;

namespace EplFantasy.UnitTests.Drafts;

public class DraftTests
{
    [Fact]
    public void CreateInitial_establishes_the_correct_initial_state()
    {
        var draftId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var draftOrder = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        var draft = Draft.CreateInitial(draftId, seasonId, draftOrder, timerSeconds: 300, now);

        Assert.Equal(draftId, draft.DraftId);
        Assert.Equal(seasonId, draft.SeasonId);
        Assert.Equal(DraftType.Initial, draft.DraftType);
        Assert.Equal(DraftStatus.InProgress, draft.Status); // AC1: no Scheduled phase for an Initial Draft.
        Assert.Equal(draftOrder, draft.DraftOrder);
        Assert.Equal(1, draft.CurrentRound);
        Assert.Equal(0, draft.CurrentPickIndex);
        Assert.Equal(300, draft.TimerSeconds);
        Assert.Equal(now.AddSeconds(300), draft.CurrentPickDeadline);
    }

    [Fact]
    public void CreateSecondary_establishes_the_correct_initial_state_and_captures_the_standings_snapshot_timestamp()
    {
        var draftId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var draftOrder = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }; // already worst-place-first (BR-056).
        var now = new DateTimeOffset(2027, 2, 1, 9, 0, 0, TimeSpan.Zero);

        var draft = Draft.CreateSecondary(draftId, seasonId, draftOrder, timerSeconds: 300, now);

        Assert.Equal(draftId, draft.DraftId);
        Assert.Equal(seasonId, draft.SeasonId);
        Assert.Equal(DraftType.Secondary, draft.DraftType);
        Assert.Equal(DraftStatus.InProgress, draft.Status);
        Assert.Equal(draftOrder, draft.DraftOrder);
        Assert.Equal(1, draft.CurrentRound);
        Assert.Equal(0, draft.CurrentPickIndex);
        Assert.Equal(300, draft.TimerSeconds);
        Assert.Equal(now.AddSeconds(300), draft.CurrentPickDeadline);
        Assert.Equal(now, draft.StandingsSnapshotTakenAt); // BR-136: captured at creation time.
    }

    [Fact]
    public void FantasyTeamIdForPick_follows_DraftOrder_forward_on_odd_rounds()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var teamC = Guid.NewGuid();
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB, teamC], 300, DateTimeOffset.UtcNow);

        Assert.Equal(teamA, draft.FantasyTeamIdForPick(round: 1, pickIndexInRound: 0));
        Assert.Equal(teamB, draft.FantasyTeamIdForPick(round: 1, pickIndexInRound: 1));
        Assert.Equal(teamC, draft.FantasyTeamIdForPick(round: 1, pickIndexInRound: 2));
    }

    [Fact]
    public void FantasyTeamIdForPick_reverses_DraftOrder_on_even_rounds()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var teamC = Guid.NewGuid();
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB, teamC], 300, DateTimeOffset.UtcNow);

        Assert.Equal(teamC, draft.FantasyTeamIdForPick(round: 2, pickIndexInRound: 0));
        Assert.Equal(teamB, draft.FantasyTeamIdForPick(round: 2, pickIndexInRound: 1));
        Assert.Equal(teamA, draft.FantasyTeamIdForPick(round: 2, pickIndexInRound: 2));

        // Round 3 (odd) returns to forward order — snake, not a one-time reversal.
        Assert.Equal(teamA, draft.FantasyTeamIdForPick(round: 3, pickIndexInRound: 0));
    }

    [Fact]
    public void MakePick_records_the_selection_and_advances_within_the_round()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB], 300, now);

        var laterNow = now.AddSeconds(10);
        var selection = draft.MakePick(teamA, playerId, totalRounds: 2, laterNow);

        Assert.Equal(teamA, selection.FantasyTeamId);
        Assert.Equal(playerId, selection.PlayerId);
        Assert.Equal(1, selection.Round);
        Assert.Equal(1, selection.PickNumber);
        Assert.Equal(laterNow, selection.SelectedAt);
        Assert.False(selection.IsMakeupPick);
        Assert.Contains(selection, draft.Selections);

        Assert.Equal(1, draft.CurrentRound); // still round 1 — team B hasn't picked yet.
        Assert.Equal(1, draft.CurrentPickIndex);
        Assert.Equal(DraftStatus.InProgress, draft.Status);
        Assert.Equal(laterNow.AddSeconds(300), draft.CurrentPickDeadline); // a fresh timer for the next pick.
    }

    [Fact]
    public void MakePick_advances_to_the_next_round_in_snake_order_once_every_team_has_picked()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB], 300, now);

        draft.MakePick(teamA, Guid.NewGuid(), totalRounds: 2, now);
        draft.MakePick(teamB, Guid.NewGuid(), totalRounds: 2, now);

        Assert.Equal(2, draft.CurrentRound);
        Assert.Equal(0, draft.CurrentPickIndex);
        // Round 2 is reversed — Team B (last in round 1) picks first in round 2.
        Assert.Equal(teamB, draft.FantasyTeamIdForPick(draft.CurrentRound, draft.CurrentPickIndex));
    }

    [Fact]
    public void MakePick_completes_the_Draft_on_the_final_regularly_scheduled_pick_with_no_makeup_picks_pending()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB], 300, now);

        draft.MakePick(teamA, Guid.NewGuid(), totalRounds: 1, now);
        draft.MakePick(teamB, Guid.NewGuid(), totalRounds: 1, now);

        Assert.Equal(DraftStatus.Completed, draft.Status);
        Assert.Null(draft.CurrentPickDeadline);
    }

    [Fact]
    public void MakePick_defers_completion_while_makeup_picks_are_pending()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB], 300, now);
        draft.PendingMakeupPicks.Add(teamA); // F-005.4's own concern — simulated here only to prove the deferral.

        draft.MakePick(teamA, Guid.NewGuid(), totalRounds: 1, now);
        draft.MakePick(teamB, Guid.NewGuid(), totalRounds: 1, now);

        Assert.Equal(DraftStatus.InProgress, draft.Status); // deferred, not Completed.
    }

    [Fact]
    public void MakePick_rejects_a_pick_from_a_FantasyTeam_that_is_not_currently_on_the_clock()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB], 300, DateTimeOffset.UtcNow);

        Assert.Throws<NotYourTurnException>(() => draft.MakePick(teamB, Guid.NewGuid(), totalRounds: 2, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MakePick_rejects_a_pick_against_a_Draft_that_is_not_InProgress()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB], 300, DateTimeOffset.UtcNow);
        draft.MakePick(teamA, Guid.NewGuid(), totalRounds: 1, DateTimeOffset.UtcNow);
        draft.MakePick(teamB, Guid.NewGuid(), totalRounds: 1, DateTimeOffset.UtcNow); // completes the Draft.

        Assert.Throws<DraftNotInProgressException>(() => draft.MakePick(teamA, Guid.NewGuid(), totalRounds: 1, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ExtendTimer_pushes_CurrentPickDeadline_back_by_the_requested_amount()
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], 300, now);
        var originalDeadline = draft.CurrentPickDeadline;

        draft.ExtendTimer(120);

        Assert.Equal(originalDeadline!.Value.AddSeconds(120), draft.CurrentPickDeadline);
    }

    [Fact]
    public void ExtendTimer_rejects_a_Draft_that_is_not_InProgress()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB], 300, DateTimeOffset.UtcNow);
        draft.MakePick(teamA, Guid.NewGuid(), totalRounds: 1, DateTimeOffset.UtcNow);
        draft.MakePick(teamB, Guid.NewGuid(), totalRounds: 1, DateTimeOffset.UtcNow); // completes the Draft.

        Assert.Throws<DraftNotInProgressException>(() => draft.ExtendTimer(60));
    }

    [Fact]
    public void SkipCurrentPick_queues_the_on_the_clock_FantasyTeam_and_advances_the_turn()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB], 300, now);

        var laterNow = now.AddSeconds(400); // past the deadline
        var skipped = draft.SkipCurrentPick(totalRounds: 2, laterNow);

        Assert.Equal(teamA, skipped.FantasyTeamId);
        Assert.False(skipped.IsRequeue);
        Assert.Equal([teamA], draft.PendingMakeupPicks);
        Assert.Empty(draft.Selections); // AC1: no DraftSelection is recorded for a skipped turn.
        Assert.Equal(1, draft.CurrentRound);
        Assert.Equal(1, draft.CurrentPickIndex); // advanced past teamA — teamB is now on the clock.
        Assert.Equal(laterNow.AddSeconds(300), draft.CurrentPickDeadline);
    }

    [Fact]
    public void SkipCurrentPick_on_the_final_regular_pick_enters_the_makeup_phase_instead_of_completing()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB], 300, now);
        draft.MakePick(teamA, Guid.NewGuid(), totalRounds: 1, now); // teamB is now on the clock for the final regular pick.

        var skipped = draft.SkipCurrentPick(totalRounds: 1, now);

        Assert.Equal(teamB, skipped.FantasyTeamId);
        Assert.False(skipped.IsRequeue);
        Assert.Equal([teamB], draft.PendingMakeupPicks);
        Assert.Equal(DraftStatus.InProgress, draft.Status); // AC2: deferred, not Completed.
        Assert.NotNull(draft.CurrentPickDeadline);

        // AC2/AC3: the queued team can now make its makeup pick, completing the Draft.
        var makeupSelection = draft.MakePick(teamB, Guid.NewGuid(), totalRounds: 1, now);
        Assert.True(makeupSelection.IsMakeupPick);
        Assert.Empty(draft.PendingMakeupPicks);
        Assert.Equal(DraftStatus.Completed, draft.Status);
        Assert.Null(draft.CurrentPickDeadline);
    }

    [Fact]
    public void SkipCurrentPick_during_the_makeup_phase_requeues_at_the_back_of_the_queue()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var teamC = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB, teamC], 300, now);
        draft.MakePick(teamA, Guid.NewGuid(), totalRounds: 1, now);
        draft.MakePick(teamB, Guid.NewGuid(), totalRounds: 1, now);
        draft.SkipCurrentPick(totalRounds: 1, now); // teamC's final regular pick times out — enters makeup phase, queue: [teamC].
        draft.PendingMakeupPicks.Add(teamA); // simulate an earlier skip during the regular rounds too — queue: [teamC, teamA].

        var skipped = draft.SkipCurrentPick(totalRounds: 1, now); // teamC's makeup pick also times out.

        Assert.Equal(teamC, skipped.FantasyTeamId);
        Assert.True(skipped.IsRequeue); // AC4: re-queued, distinct from an initial skip.
        Assert.Equal([teamA, teamC], draft.PendingMakeupPicks); // teamA moves up; teamC goes to the back.
        Assert.Equal(DraftStatus.InProgress, draft.Status);
    }

    [Fact]
    public void SkipCurrentPick_rejects_a_Draft_that_is_not_InProgress()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var draft = Draft.CreateInitial(Guid.NewGuid(), Guid.NewGuid(), [teamA, teamB], 300, DateTimeOffset.UtcNow);
        draft.MakePick(teamA, Guid.NewGuid(), totalRounds: 1, DateTimeOffset.UtcNow);
        draft.MakePick(teamB, Guid.NewGuid(), totalRounds: 1, DateTimeOffset.UtcNow); // completes the Draft.

        Assert.Throws<DraftNotInProgressException>(() => draft.SkipCurrentPick(totalRounds: 1, DateTimeOffset.UtcNow));
    }
}
