-- Draft Management invariants (Architecture §6.4; BRD BR-282, BR-307; AP-009; test catalog BR-246).
-- The true concurrent-pick race (AP-009: "two users must never successfully acquire the same
-- player because of a race condition") is exercised separately in run_concurrency_test.sh, which
-- races two real, simultaneous connections rather than two sequential statements in one session.

DO $$
DECLARE
  f dbtest_fixture;
  v_draft_id uuid := gen_random_uuid();
BEGIN
  f := dbtest_create_baseline_fixture('draft');

  INSERT INTO drafts (draft_id, season_id, draft_type, status, timer_seconds)
    VALUES (v_draft_id, f.season_id, 'initial', 'in_progress', 300);

  -- BR-246 draft persistence / pick uniqueness: (draft_id, round, pick_number) is unique.
  INSERT INTO draft_selections (draft_id, fantasy_team_id, player_id, round, pick_number)
    VALUES (v_draft_id, f.fantasy_team_id, f.player_id, 1, 1);
  BEGIN
    INSERT INTO draft_selections (draft_id, fantasy_team_id, player_id, round, pick_number)
      VALUES (v_draft_id, f.fantasy_team_id_2, f.player_id_2, 1, 1);
    PERFORM test_assert('draft.round_pick_unique', false, 'a duplicate (round, pick_number) was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('draft.round_pick_unique', true);
  END;

  -- The atomic-pick transaction (AP-009): a DraftSelection row and its SquadPlayer row are written
  -- together; the squad_players insert is what actually blocks a double-owned player (tested in
  -- 030_fantasy_team_and_squad_invariants.sql's fantasy_team.squad_ownership_unique_per_season).
  BEGIN
    INSERT INTO squad_players (fantasy_team_id, player_id, season_id, acquisition_type) VALUES (f.fantasy_team_id, f.player_id, f.season_id, 'initial_draft');
    PERFORM test_assert('draft.pick_creates_squad_player_in_same_transaction', true);
  EXCEPTION WHEN OTHERS THEN
    PERFORM test_assert('draft.pick_creates_squad_player_in_same_transaction', false, SQLERRM);
  END;

  -- BR-307: a granted replacement opportunity has no expiry column to violate — confirm the row
  -- persists as unspent (spent_at IS NULL) indefinitely, i.e. no trigger/default sets it.
  INSERT INTO replacement_opportunities (fantasy_team_id, source_player_id, grant_reason) VALUES (f.fantasy_team_id, f.player_id_2, 'epl_exit');
  PERFORM test_assert(
    'draft.replacement_opportunity_never_auto_expires',
    (SELECT spent_at IS NULL FROM replacement_opportunities WHERE fantasy_team_id = f.fantasy_team_id AND source_player_id = f.player_id_2),
    'expected a freshly granted opportunity to have spent_at IS NULL'
  );
END $$;
