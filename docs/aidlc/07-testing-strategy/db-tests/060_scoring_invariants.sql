-- Scoring invariants (Architecture §6.6; BRD BR-140–BR-145; test catalog BR-249).

DO $$
DECLARE
  f dbtest_fixture;
  v_pp_id uuid;
BEGIN
  f := dbtest_create_baseline_fixture('scoring');

  -- One PlayerPerformance row per (Gameweek, Player).
  INSERT INTO player_performances (player_performance_id, gameweek_id, player_id, source)
    VALUES (gen_random_uuid(), f.gameweek_id, f.player_id, 'official_fpl') RETURNING player_performance_id INTO v_pp_id;
  BEGIN
    INSERT INTO player_performances (gameweek_id, player_id, source) VALUES (f.gameweek_id, f.player_id, 'manual');
    PERFORM test_assert('scoring.performance_unique_per_gameweek_player', false, 'a duplicate PlayerPerformance row for the same (gameweek, player) was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('scoring.performance_unique_per_gameweek_player', true);
  END;

  -- One GameweekScore row per (FantasyTeam, Gameweek).
  INSERT INTO gameweek_scores (fantasy_team_id, gameweek_id, fantasy_points, captain_points, fantasy_goals_for, fantasy_goals_against, fantasy_goal_difference)
    VALUES (f.fantasy_team_id, f.gameweek_id, 0, 0, 0, 0, 0);
  BEGIN
    INSERT INTO gameweek_scores (fantasy_team_id, gameweek_id, fantasy_points, captain_points, fantasy_goals_for, fantasy_goals_against, fantasy_goal_difference)
      VALUES (f.fantasy_team_id, f.gameweek_id, 5, 5, 1, 1, 0);
    PERFORM test_assert('scoring.gameweek_score_unique_per_team_and_gameweek', false, 'a duplicate GameweekScore row was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('scoring.gameweek_score_unique_per_team_and_gameweek', true);
  END;

  -- Invariant 12 precedence groundwork: an active ScoreOverride (undone_at IS NULL) must be
  -- insertable against an existing PlayerPerformance, and "undoing" it is just setting undone_at.
  DECLARE
    v_admin_membership uuid := f.admin_membership_id;
    v_override_id uuid;
  BEGIN
    INSERT INTO score_overrides (score_override_id, player_performance_id, administrator_membership_id, original_value, override_value, reason)
      VALUES (gen_random_uuid(), v_pp_id, v_admin_membership, '{"goals":0}'::jsonb, '{"goals":1}'::jsonb, 'video review')
      RETURNING score_override_id INTO v_override_id;
    UPDATE score_overrides SET undone_at = now() WHERE score_override_id = v_override_id;
    PERFORM test_assert(
      'scoring.override_undo_is_a_timestamp_not_a_delete',
      (SELECT count(*) = 1 FROM score_overrides WHERE score_override_id = v_override_id AND undone_at IS NOT NULL),
      'expected the override row to persist with undone_at set, not be deleted'
    );
  END;
END $$;
