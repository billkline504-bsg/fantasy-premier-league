using EplFantasy.Rosters;
using Xunit;

namespace EplFantasy.UnitTests.Rosters;

public class GameweekRosterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly RosterPositionCounts DefaultMinimums = new(Goalkeepers: 1, Defenders: 3, Midfielders: 2, Forwards: 1);

    private static GameweekRoster NewDraftRoster() => new()
    {
        GameweekRosterId = Guid.NewGuid(),
        FantasyTeamId = Guid.NewGuid(),
        GameweekId = Guid.NewGuid(),
    };

    private static List<Guid> PlayerIds(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

    [Fact]
    public void Submit_records_the_players_and_transitions_to_Submitted()
    {
        var roster = NewDraftRoster();
        var playerIds = PlayerIds(7);
        var captainId = playerIds[0];
        var counts = new RosterPositionCounts(1, 3, 2, 1);

        roster.Submit(playerIds, captainId, weeklyRosterSize: 7, DefaultMinimums, counts, Now);

        Assert.Equal(RosterStatus.Submitted, roster.Status);
        Assert.Equal(Now, roster.SubmittedAt);
        Assert.Equal(captainId, roster.CaptainPlayerId);
        Assert.Equal(playerIds.ToHashSet(), roster.Players.Select(p => p.PlayerId).ToHashSet());
        Assert.Single(roster.Players, p => p.IsCaptain);
        Assert.False(roster.IsCarriedForward);
    }

    [Fact]
    public void Submit_allows_a_null_Captain()
    {
        // OpenAPI Specification v1.0's own RosterSubmissionRequest note: "Optional at submission
        // time; may instead be set via PUT .../roster/captain" (IT-30) — the more specific,
        // concrete wire-contract source wins over F-007.2 AC2's more general framing, the same
        // "more detailed source wins" judgment call this codebase has made before.
        var roster = NewDraftRoster();
        var playerIds = PlayerIds(7);
        var counts = new RosterPositionCounts(1, 3, 2, 1);

        roster.Submit(playerIds, captainPlayerId: null, weeklyRosterSize: 7, DefaultMinimums, counts, Now);

        Assert.Equal(RosterStatus.Submitted, roster.Status);
        Assert.Null(roster.CaptainPlayerId);
        Assert.DoesNotContain(roster.Players, p => p.IsCaptain);
    }

    [Fact]
    public void Submit_rejects_a_roster_that_is_not_exactly_the_configured_size()
    {
        var roster = NewDraftRoster();
        var playerIds = PlayerIds(6);
        var counts = new RosterPositionCounts(1, 3, 2, 0);

        var ex = Assert.Throws<InvalidRosterCompositionException>(
            () => roster.Submit(playerIds, null, weeklyRosterSize: 7, DefaultMinimums, counts, Now));

        Assert.Contains(ex.Violations, v => v.Contains("Exactly 7 players"));
        Assert.Equal(RosterStatus.Draft, roster.Status);
    }

    [Fact]
    public void Submit_rejects_a_duplicate_player()
    {
        var roster = NewDraftRoster();
        var repeated = Guid.NewGuid();
        var playerIds = new List<Guid> { repeated, repeated }.Concat(PlayerIds(5)).ToList();
        var counts = new RosterPositionCounts(1, 3, 2, 1);

        var ex = Assert.Throws<InvalidRosterCompositionException>(
            () => roster.Submit(playerIds, null, weeklyRosterSize: 7, DefaultMinimums, counts, Now));

        Assert.Contains(ex.Violations, v => v.Contains("only once"));
    }

    [Theory]
    [InlineData(0, 3, 2, 1, "Goalkeeper")]
    [InlineData(1, 2, 2, 1, "Defender")]
    [InlineData(1, 3, 1, 1, "Midfielder")]
    [InlineData(1, 3, 2, 0, "Forward")]
    public void Submit_rejects_an_unmet_positional_minimum_with_specific_feedback(int gk, int def, int mid, int fwd, string expectedMention)
    {
        var roster = NewDraftRoster();
        var total = gk + def + mid + fwd + 1; // one extra player so the total still matches weeklyRosterSize.
        var playerIds = PlayerIds(total);
        var counts = new RosterPositionCounts(gk, def, mid, fwd);

        var ex = Assert.Throws<InvalidRosterCompositionException>(
            () => roster.Submit(playerIds, null, weeklyRosterSize: total, DefaultMinimums, counts, Now));

        Assert.Contains(ex.Violations, v => v.Contains(expectedMention));
    }

    [Fact]
    public void Submit_accepts_a_roster_stacking_one_position_with_no_maximum()
    {
        // BR-042/AC3: minimums satisfied, total exactly WeeklyRosterSize, but Forwards stacked well
        // beyond its own minimum — still accepted.
        var roster = NewDraftRoster();
        var playerIds = PlayerIds(15);
        var counts = new RosterPositionCounts(Goalkeepers: 1, Defenders: 3, Midfielders: 2, Forwards: 9);

        roster.Submit(playerIds, null, weeklyRosterSize: 15, DefaultMinimums, counts, Now);

        Assert.Equal(RosterStatus.Submitted, roster.Status);
    }

    [Fact]
    public void Submit_rejects_a_Captain_not_among_the_submitted_players()
    {
        var roster = NewDraftRoster();
        var playerIds = PlayerIds(7);
        var counts = new RosterPositionCounts(1, 3, 2, 1);

        var ex = Assert.Throws<InvalidRosterCompositionException>(
            () => roster.Submit(playerIds, Guid.NewGuid(), weeklyRosterSize: 7, DefaultMinimums, counts, Now));

        Assert.Contains(ex.Violations, v => v.Contains("Captain"));
    }

    [Fact]
    public void Submit_on_resubmission_fully_replaces_the_prior_players_and_captain()
    {
        var roster = NewDraftRoster();
        var firstPlayerIds = PlayerIds(7);
        var counts = new RosterPositionCounts(1, 3, 2, 1);
        roster.Submit(firstPlayerIds, firstPlayerIds[0], weeklyRosterSize: 7, DefaultMinimums, counts, Now);

        var secondPlayerIds = PlayerIds(7);
        var laterNow = Now.AddMinutes(5);
        roster.Submit(secondPlayerIds, null, weeklyRosterSize: 7, DefaultMinimums, counts, laterNow);

        Assert.Equal(secondPlayerIds.ToHashSet(), roster.Players.Select(p => p.PlayerId).ToHashSet());
        Assert.Null(roster.CaptainPlayerId); // PUT-style full replace — an omitted Captain clears the prior one.
        Assert.DoesNotContain(roster.Players, p => p.IsCaptain);
        Assert.Equal(laterNow, roster.SubmittedAt);
    }

    [Theory]
    [InlineData(RosterStatus.Locked)]
    [InlineData(RosterStatus.Scored)]
    public void Submit_rejects_a_roster_that_is_already_Locked_or_Scored(RosterStatus status)
    {
        var roster = NewDraftRoster();
        roster.Status = status;
        var playerIds = PlayerIds(7);
        var counts = new RosterPositionCounts(1, 3, 2, 1);

        Assert.Throws<GameweekRosterNotEditableException>(
            () => roster.Submit(playerIds, null, weeklyRosterSize: 7, DefaultMinimums, counts, Now));
    }

    [Fact]
    public void SetCaptain_designates_the_given_player_and_clears_any_prior_Captain()
    {
        var roster = NewDraftRoster();
        var playerIds = PlayerIds(7);
        var counts = new RosterPositionCounts(1, 3, 2, 1);
        roster.Submit(playerIds, playerIds[0], weeklyRosterSize: 7, DefaultMinimums, counts, Now);

        roster.SetCaptain(playerIds[3]);

        Assert.Equal(playerIds[3], roster.CaptainPlayerId);
        Assert.Single(roster.Players, p => p.IsCaptain);
        Assert.True(roster.Players.Single(p => p.PlayerId == playerIds[3]).IsCaptain);
        Assert.False(roster.Players.Single(p => p.PlayerId == playerIds[0]).IsCaptain);
    }

    [Fact]
    public void SetCaptain_rejects_a_player_not_among_the_roster_selected_players()
    {
        var roster = NewDraftRoster();
        var playerIds = PlayerIds(7);
        var counts = new RosterPositionCounts(1, 3, 2, 1);
        roster.Submit(playerIds, null, weeklyRosterSize: 7, DefaultMinimums, counts, Now);

        var ex = Assert.Throws<InvalidRosterCompositionException>(() => roster.SetCaptain(Guid.NewGuid()));

        Assert.Contains(ex.Violations, v => v.Contains("Captain"));
        Assert.Null(roster.CaptainPlayerId);
    }

    [Theory]
    [InlineData(RosterStatus.Locked)]
    [InlineData(RosterStatus.Scored)]
    public void SetCaptain_rejects_a_roster_that_is_already_Locked_or_Scored(RosterStatus status)
    {
        var roster = NewDraftRoster();
        var playerIds = PlayerIds(7);
        var counts = new RosterPositionCounts(1, 3, 2, 1);
        roster.Submit(playerIds, null, weeklyRosterSize: 7, DefaultMinimums, counts, Now);
        roster.Status = status;

        Assert.Throws<GameweekRosterNotEditableException>(() => roster.SetCaptain(playerIds[0]));
    }

    [Fact]
    public void Lock_transitions_Submitted_to_Locked_and_stamps_LockedAt()
    {
        var roster = NewDraftRoster();
        var playerIds = PlayerIds(7);
        var counts = new RosterPositionCounts(1, 3, 2, 1);
        roster.Submit(playerIds, playerIds[0], weeklyRosterSize: 7, DefaultMinimums, counts, Now);
        var lockedAt = Now.AddHours(1);

        roster.Lock(lockedAt);

        Assert.Equal(RosterStatus.Locked, roster.Status);
        Assert.Equal(lockedAt, roster.LockedAt);
        Assert.Equal(playerIds[0], roster.CaptainPlayerId); // untouched by Lock itself.
    }

    [Fact]
    public void MarkScored_transitions_Locked_to_Scored()
    {
        var roster = NewDraftRoster();
        var playerIds = PlayerIds(7);
        var counts = new RosterPositionCounts(1, 3, 2, 1);
        roster.Submit(playerIds, playerIds[0], weeklyRosterSize: 7, DefaultMinimums, counts, Now);
        roster.Lock(Now.AddHours(1));

        roster.MarkScored();

        Assert.Equal(RosterStatus.Scored, roster.Status);
        Assert.Equal(playerIds[0], roster.CaptainPlayerId); // untouched by MarkScored itself.
    }

    [Fact]
    public void CreateCarriedForward_copies_the_given_players_and_captain_as_Submitted_and_IsCarriedForward()
    {
        var fantasyTeamId = Guid.NewGuid();
        var gameweekId = Guid.NewGuid();
        var carriedForwardPlayerIds = PlayerIds(3);

        var roster = GameweekRoster.CreateCarriedForward(
            Guid.NewGuid(), fantasyTeamId, gameweekId, carriedForwardPlayerIds, carriedForwardPlayerIds[1], Now);

        Assert.Equal(fantasyTeamId, roster.FantasyTeamId);
        Assert.Equal(gameweekId, roster.GameweekId);
        Assert.Equal(RosterStatus.Submitted, roster.Status);
        Assert.Equal(Now, roster.SubmittedAt);
        Assert.True(roster.IsCarriedForward);
        Assert.Equal(carriedForwardPlayerIds[1], roster.CaptainPlayerId);
        Assert.Equal(carriedForwardPlayerIds.ToHashSet(), roster.Players.Select(p => p.PlayerId).ToHashSet());
        Assert.True(roster.Players.Single(p => p.PlayerId == carriedForwardPlayerIds[1]).IsCaptain);
    }

    [Fact]
    public void CreateCarriedForward_allows_an_empty_player_list_and_a_null_Captain()
    {
        // AC7: the FantasyTeam's first-ever Gameweek of the Season has no prior roster to carry
        // forward from — an empty, captain-less roster is a legitimate outcome, not an error, and
        // is deliberately exempt from Submit's own size/positional-minimum invariants (BR-305).
        var roster = GameweekRoster.CreateCarriedForward(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [], null, Now);

        Assert.Equal(RosterStatus.Submitted, roster.Status);
        Assert.True(roster.IsCarriedForward);
        Assert.Null(roster.CaptainPlayerId);
        Assert.Empty(roster.Players);
    }

    private static GameweekRoster NewLockedRoster(out List<Guid> playerIds)
    {
        var roster = NewDraftRoster();
        playerIds = PlayerIds(7);
        var counts = new RosterPositionCounts(1, 3, 2, 1);
        roster.Submit(playerIds, playerIds[0], weeklyRosterSize: 7, DefaultMinimums, counts, Now);
        roster.Lock(Now.AddHours(1));
        return roster;
    }

    [Fact]
    public void Correct_replaces_players_and_Captain_on_a_Locked_roster_and_clears_IsCarriedForward()
    {
        var roster = NewLockedRoster(out _);
        var correctedPlayerIds = PlayerIds(7);
        var counts = new RosterPositionCounts(1, 3, 2, 1);

        roster.Correct(correctedPlayerIds, correctedPlayerIds[2], weeklyRosterSize: 7, DefaultMinimums, counts);

        Assert.Equal(RosterStatus.Locked, roster.Status); // Correct doesn't re-run the lock transition (BR-098).
        Assert.Equal(correctedPlayerIds[2], roster.CaptainPlayerId);
        Assert.Equal(correctedPlayerIds.ToHashSet(), roster.Players.Select(p => p.PlayerId).ToHashSet());
        Assert.False(roster.IsCarriedForward);
    }

    [Theory]
    [InlineData(RosterStatus.Draft)]
    [InlineData(RosterStatus.Submitted)]
    public void Correct_rejects_a_roster_that_is_not_yet_Locked_or_Scored(RosterStatus status)
    {
        var roster = NewDraftRoster();
        roster.Status = status;
        var playerIds = PlayerIds(7);
        var counts = new RosterPositionCounts(1, 3, 2, 1);

        Assert.Throws<GameweekRosterNotLockedException>(
            () => roster.Correct(playerIds, playerIds[0], weeklyRosterSize: 7, DefaultMinimums, counts));
    }

    [Fact]
    public void Correct_enforces_the_same_composition_invariants_as_Submit()
    {
        var roster = NewLockedRoster(out _);
        var undersized = PlayerIds(6);
        var counts = new RosterPositionCounts(1, 3, 2, 0);

        var ex = Assert.Throws<InvalidRosterCompositionException>(
            () => roster.Correct(undersized, null, weeklyRosterSize: 7, DefaultMinimums, counts));

        Assert.Contains(ex.Violations, v => v.Contains("Exactly 7 players"));
    }

    [Fact]
    public void CorrectCaptain_designates_a_new_Captain_on_a_Locked_roster_without_touching_players()
    {
        var roster = NewLockedRoster(out var playerIds);

        roster.CorrectCaptain(playerIds[4]);

        Assert.Equal(RosterStatus.Locked, roster.Status);
        Assert.Equal(playerIds[4], roster.CaptainPlayerId);
        Assert.Equal(playerIds.ToHashSet(), roster.Players.Select(p => p.PlayerId).ToHashSet());
        Assert.True(roster.Players.Single(p => p.PlayerId == playerIds[4]).IsCaptain);
        Assert.False(roster.Players.Single(p => p.PlayerId == playerIds[0]).IsCaptain);
    }

    [Fact]
    public void CorrectCaptain_allows_clearing_the_Captain()
    {
        var roster = NewLockedRoster(out _);

        roster.CorrectCaptain(null);

        Assert.Null(roster.CaptainPlayerId);
        Assert.DoesNotContain(roster.Players, p => p.IsCaptain);
    }

    [Fact]
    public void CorrectCaptain_rejects_a_player_not_among_the_roster_selected_players()
    {
        var roster = NewLockedRoster(out _);

        var ex = Assert.Throws<InvalidRosterCompositionException>(() => roster.CorrectCaptain(Guid.NewGuid()));

        Assert.Contains(ex.Violations, v => v.Contains("Captain"));
    }

    [Theory]
    [InlineData(RosterStatus.Draft)]
    [InlineData(RosterStatus.Submitted)]
    public void CorrectCaptain_rejects_a_roster_that_is_not_yet_Locked_or_Scored(RosterStatus status)
    {
        var roster = NewDraftRoster();
        roster.Status = status;

        Assert.Throws<GameweekRosterNotLockedException>(() => roster.CorrectCaptain(Guid.NewGuid()));
    }

    /// <summary>A stable, ascending-orderable PlayerId, so tie-break assertions can name the exact winner without depending on random Guid ordering.</summary>
    private static Guid SequentialId(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static GameweekRoster NewRosterWithPlayers(IReadOnlyList<Guid> playerIds, Guid? captainPlayerId = null)
    {
        var roster = NewDraftRoster();
        foreach (var playerId in playerIds)
        {
            roster.Players.Add(new RosterPlayer { GameweekRosterId = roster.GameweekRosterId, PlayerId = playerId });
        }

        roster.CaptainPlayerId = captainPlayerId;
        return roster;
    }

    [Fact]
    public void DetermineSelectionRoles_marks_the_top_11_StartingXi_and_the_rest_Bench()
    {
        // 13 players, deliberately added out of points order, with strictly distinct point values
        // so there's no tie to worry about here (that's the next test's own concern).
        var playerIds = Enumerable.Range(0, 13).Select(SequentialId).ToList();
        var roster = NewRosterWithPlayers(playerIds);
        var pointsByPlayerId = playerIds
            .Select((id, index) => (id, points: 13 - index)) // player 0 scores 13 (highest) down to player 12 scoring 1 (lowest).
            .ToDictionary(x => x.id, x => x.points);

        roster.DetermineSelectionRoles(pointsByPlayerId);

        var startingXi = roster.Players.Where(p => p.SelectionRole == SelectionRole.StartingXi).Select(p => p.PlayerId).ToHashSet();
        var bench = roster.Players.Where(p => p.SelectionRole == SelectionRole.Bench).Select(p => p.PlayerId).ToHashSet();
        Assert.Equal(11, startingXi.Count);
        Assert.Equal(2, bench.Count);
        Assert.Equal(playerIds.Take(11).ToHashSet(), startingXi); // the 11 highest-scoring players (BR-044).
        Assert.Equal(playerIds.Skip(11).ToHashSet(), bench);
    }

    [Fact]
    public void DetermineSelectionRoles_breaks_an_11th_12th_place_tie_using_ascending_PlayerId()
    {
        // BR-303: ten players are unambiguously ahead on points; two more (tenthPlace/eleventhPlace
        // by id, both scoring 10) are genuinely tied for the single remaining Starting XI spot.
        var clearlyAhead = Enumerable.Range(0, 10).Select(SequentialId).ToList();
        var tiedLower = SequentialId(10);
        var tiedHigher = SequentialId(11);
        var roster = NewRosterWithPlayers([.. clearlyAhead, tiedLower, tiedHigher]);

        var pointsByPlayerId = clearlyAhead
            .Select((id, index) => (id, points: 20 - index)) // 20 down to 11 — comfortably clear of the tie below.
            .ToDictionary(x => x.id, x => x.points);
        pointsByPlayerId[tiedLower] = 10;
        pointsByPlayerId[tiedHigher] = 10;

        roster.DetermineSelectionRoles(pointsByPlayerId);

        // The lower (ascending-first) PlayerId of the tied pair wins the boundary spot — arbitrary,
        // but deterministic and reproducible (BR-303), never random.
        Assert.Equal(SelectionRole.StartingXi, roster.Players.Single(p => p.PlayerId == tiedLower).SelectionRole);
        Assert.Equal(SelectionRole.Bench, roster.Players.Single(p => p.PlayerId == tiedHigher).SelectionRole);
    }

    [Fact]
    public void DetermineSelectionRoles_applies_the_Captain_multiplier_before_ranking_so_it_can_win_a_Starting_XI_spot()
    {
        // BR-304: 11 non-Captain players comfortably occupy the Starting XI on raw points (20 down
        // to 10); the Captain's own raw points (6) would leave them 12th — Bench — on an unmultiplied
        // ranking. Doubled (BR-047) to 12, the Captain's value overtakes the lowest of those 11
        // (10), displacing that player to Bench instead.
        var otherPlayers = Enumerable.Range(0, 11).Select(SequentialId).ToList();
        var captainId = SequentialId(11);
        var roster = NewRosterWithPlayers([.. otherPlayers, captainId], captainPlayerId: captainId);

        var pointsByPlayerId = otherPlayers
            .Select((id, index) => (id, points: 20 - index)) // 20 down to 10.
            .ToDictionary(x => x.id, x => x.points);
        pointsByPlayerId[captainId] = 6;

        var finalPoints = roster.DetermineSelectionRoles(pointsByPlayerId);

        Assert.Equal(12, finalPoints[captainId]); // 6 x 2 (BR-047), not the bare 6.
        Assert.Equal(SelectionRole.StartingXi, roster.Players.Single(p => p.PlayerId == captainId).SelectionRole);
        var lowestOtherPlayer = otherPlayers[^1]; // scored 10 — the one the multiplied Captain now overtakes.
        Assert.Equal(SelectionRole.Bench, roster.Players.Single(p => p.PlayerId == lowestOtherPlayer).SelectionRole);
    }

    [Fact]
    public void DetermineSelectionRoles_returns_every_players_final_points_unmultiplied_for_non_Captains()
    {
        var playerIds = Enumerable.Range(0, 3).Select(SequentialId).ToList();
        var captainId = playerIds[0];
        var roster = NewRosterWithPlayers(playerIds, captainId);
        var pointsByPlayerId = new Dictionary<Guid, int> { [playerIds[0]] = 5, [playerIds[1]] = 3, [playerIds[2]] = 0 };

        var finalPoints = roster.DetermineSelectionRoles(pointsByPlayerId);

        Assert.Equal(10, finalPoints[playerIds[0]]); // Captain: 5 x 2.
        Assert.Equal(3, finalPoints[playerIds[1]]);
        Assert.Equal(0, finalPoints[playerIds[2]]); // AC4: a non-playing player's official (zero) score is used as-is.
    }

    [Fact]
    public void DetermineSelectionRoles_a_non_playing_Captain_contributes_zero_points_with_no_fallback()
    {
        // IT-35 (F-008.3 AC3, BR-048): the designated Captain has no official points at all this
        // Gameweek (zero official appearance) — 0 x the captain multiplier is still 0, and BR-049/
        // BR-051 mean there is no second player anywhere in this model for the multiplier to fall
        // back to instead.
        var playerIds = Enumerable.Range(0, 3).Select(SequentialId).ToList();
        var captainId = playerIds[0];
        var roster = NewRosterWithPlayers(playerIds, captainId);
        var pointsByPlayerId = new Dictionary<Guid, int> { [playerIds[0]] = 0, [playerIds[1]] = 8, [playerIds[2]] = 6 };

        var finalPoints = roster.DetermineSelectionRoles(pointsByPlayerId);

        Assert.Equal(0, finalPoints[captainId]); // 0 x 2 (BR-047), not a fallback to any other player's value.
        Assert.Equal(8, finalPoints[playerIds[1]]);
        Assert.Equal(6, finalPoints[playerIds[2]]);
    }
}
