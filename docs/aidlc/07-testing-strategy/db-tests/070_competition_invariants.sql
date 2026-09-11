-- Competition invariants (Architecture §6.7; BRD BR-107–BR-135, BR-299, BR-306; test catalog BR-251, BR-252, BR-253).

DO $$
DECLARE
  f dbtest_fixture;
BEGIN
  f := dbtest_create_baseline_fixture('competition');

  -- A FantasyTeam can never play itself.
  BEGIN
    INSERT INTO head_to_head_matches (season_id, gameweek_id, home_fantasy_team_id, away_fantasy_team_id)
      VALUES (f.season_id, f.gameweek_id, f.fantasy_team_id, f.fantasy_team_id);
    PERFORM test_assert('competition.match_cannot_be_self_versus_self', false, 'a match with the same FantasyTeam on both sides was wrongly accepted');
  EXCEPTION WHEN check_violation THEN
    PERFORM test_assert('competition.match_cannot_be_self_versus_self', true);
  END;

  -- One match per (Season, Gameweek, Home, Away) — BR-306's bye week is the *absence* of a row,
  -- never a fabricated match, so there is no "bye week" row shape to test here beyond this uniqueness.
  INSERT INTO head_to_head_matches (season_id, gameweek_id, home_fantasy_team_id, away_fantasy_team_id)
    VALUES (f.season_id, f.gameweek_id, f.fantasy_team_id, f.fantasy_team_id_2);
  BEGIN
    INSERT INTO head_to_head_matches (season_id, gameweek_id, home_fantasy_team_id, away_fantasy_team_id)
      VALUES (f.season_id, f.gameweek_id, f.fantasy_team_id, f.fantasy_team_id_2);
    PERFORM test_assert('competition.match_unique_per_season_gameweek_pairing', false, 'a duplicate match for the same pairing/Gameweek was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('competition.match_unique_per_season_gameweek_pairing', true);
  END;

  -- BR-127/BR-299: one SeasonGoalPrediction per (Season, FantasyTeam).
  INSERT INTO season_goal_predictions (season_id, fantasy_team_id, predicted_epl_goals, locked_at)
    VALUES (f.season_id, f.fantasy_team_id, 1230, now());
  BEGIN
    INSERT INTO season_goal_predictions (season_id, fantasy_team_id, predicted_epl_goals, locked_at)
      VALUES (f.season_id, f.fantasy_team_id, 1250, now());
    PERFORM test_assert('competition.season_goal_prediction_unique_per_team', false, 'a second SeasonGoalPrediction for the same (season, team) was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('competition.season_goal_prediction_unique_per_team', true);
  END;

  -- BR-217: league_standings supports point-in-time snapshots — the same FantasyTeam may have a
  -- row for two different as_of_gameweek_id values in the same Season (the primary key includes it).
  INSERT INTO league_standings (season_id, fantasy_team_id, as_of_gameweek_id, league_points, played, won, drawn, lost, fantasy_goals_for, fantasy_goals_against, fantasy_goal_difference, captain_points_total, "position")
    VALUES (f.season_id, f.fantasy_team_id, f.gameweek_id, 3, 1, 1, 0, 0, 2, 1, 1, 5, 1);
  DECLARE
    v_gw2 uuid;
  BEGIN
    INSERT INTO gameweeks (gameweek_id, epl_season_identifier, number, roster_lock_deadline)
      VALUES (gen_random_uuid(), 'dbtest-competition', 2, now() + interval '14 days') RETURNING gameweek_id INTO v_gw2;
    INSERT INTO league_standings (season_id, fantasy_team_id, as_of_gameweek_id, league_points, played, won, drawn, lost, fantasy_goals_for, fantasy_goals_against, fantasy_goal_difference, captain_points_total, "position")
      VALUES (f.season_id, f.fantasy_team_id, v_gw2, 6, 2, 2, 0, 0, 4, 2, 2, 10, 1);
    PERFORM test_assert('competition.standings_supports_point_in_time_history', true);
  EXCEPTION WHEN OTHERS THEN
    PERFORM test_assert('competition.standings_supports_point_in_time_history', false, SQLERRM);
  END;
END $$;
