-- Roster Management invariants (Architecture §6.5, §8.1; BRD BR-046, BR-279, BR-305; test catalog BR-247).

DO $$
DECLARE
  f dbtest_fixture;
  v_gr_id uuid;
BEGIN
  f := dbtest_create_baseline_fixture('roster');

  -- BR-279 (via trg_enforce_gameweek_roster_size, deferred to COMMIT/SET CONSTRAINTS IMMEDIATE):
  -- a Gameweek roster submitted with fewer than SeasonConfiguration.weekly_roster_size players
  -- (default 15) must be rejected.
  BEGIN
    v_gr_id := gen_random_uuid();
    INSERT INTO gameweek_rosters (gameweek_roster_id, fantasy_team_id, gameweek_id, status, submitted_at)
      VALUES (v_gr_id, f.fantasy_team_id, f.gameweek_id, 'submitted', now());
    INSERT INTO roster_players (gameweek_roster_id, player_id) VALUES (v_gr_id, f.player_id);
    SET CONSTRAINTS trg_enforce_gameweek_roster_size IMMEDIATE;
    PERFORM test_assert('roster.size_enforced_on_submit', false, 'an under-sized roster submission was wrongly accepted');
  EXCEPTION WHEN check_violation THEN
    PERFORM test_assert('roster.size_enforced_on_submit', true);
    SET CONSTRAINTS trg_enforce_gameweek_roster_size DEFERRED;
  END;

  -- BR-305: the same under-sized roster is accepted when is_carried_forward = true (the ADR-012
  -- sweep's automatic carry-forward path, not a user Submit).
  BEGIN
    v_gr_id := gen_random_uuid();
    INSERT INTO gameweek_rosters (gameweek_roster_id, fantasy_team_id, gameweek_id, status, submitted_at, is_carried_forward)
      VALUES (v_gr_id, f.fantasy_team_id, f.gameweek_id, 'submitted', now(), true);
    INSERT INTO roster_players (gameweek_roster_id, player_id) VALUES (v_gr_id, f.player_id);
    SET CONSTRAINTS trg_enforce_gameweek_roster_size IMMEDIATE;
    PERFORM test_assert('roster.carry_forward_exempt_from_size_check', true);
    SET CONSTRAINTS trg_enforce_gameweek_roster_size DEFERRED;
  EXCEPTION WHEN OTHERS THEN
    PERFORM test_assert('roster.carry_forward_exempt_from_size_check', false, SQLERRM);
  END;

  -- BR-046, Invariant 5: at most one captain per roster.
  UPDATE roster_players SET is_captain = true WHERE gameweek_roster_id = v_gr_id AND player_id = f.player_id;
  INSERT INTO roster_players (gameweek_roster_id, player_id) VALUES (v_gr_id, f.player_id_2);
  BEGIN
    UPDATE roster_players SET is_captain = true WHERE gameweek_roster_id = v_gr_id AND player_id = f.player_id_2;
    PERFORM test_assert('roster.one_captain_per_roster', false, 'a second captain on the same roster was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('roster.one_captain_per_roster', true);
  END;

  -- One GameweekRoster per (FantasyTeam, Gameweek).
  BEGIN
    INSERT INTO gameweek_rosters (gameweek_roster_id, fantasy_team_id, gameweek_id, status)
      VALUES (gen_random_uuid(), f.fantasy_team_id, f.gameweek_id, 'draft');
    PERFORM test_assert('roster.unique_per_team_and_gameweek', false, 'a second GameweekRoster for the same (team, gameweek) was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('roster.unique_per_team_and_gameweek', true);
  END;
END $$;
