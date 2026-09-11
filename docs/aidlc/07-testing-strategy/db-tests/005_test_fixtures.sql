-- Shared fixture builder for the invariant tests in this folder. Run after 000_test_harness.sql.
-- Each test file calls dbtest_create_baseline_fixture(<unique suffix>) to get a fully wired
-- League/Season/FantasyTeam/Player baseline without repeating the same ~20 lines of setup INSERTs
-- in every file. p_suffix must be unique per call within one test run (used to keep natural-key
-- columns like username/epl_*_id collision-free across files and across repeated runs).

CREATE TYPE dbtest_fixture AS (
  league_id             uuid,
  admin_membership_id   uuid,
  member_membership_id  uuid,
  season_id             uuid,
  fantasy_team_id       uuid, -- owned by the admin membership
  fantasy_team_id_2     uuid, -- owned by the plain member membership
  club_id               uuid,
  gameweek_id           uuid,
  player_id             uuid,
  player_id_2           uuid
);

CREATE OR REPLACE FUNCTION dbtest_create_baseline_fixture(p_suffix text) RETURNS dbtest_fixture AS $$
DECLARE
  r dbtest_fixture;
  v_user1 uuid;
  v_user2 uuid;
BEGIN
  r.league_id            := gen_random_uuid();
  r.admin_membership_id   := gen_random_uuid();
  r.member_membership_id  := gen_random_uuid();

  INSERT INTO users (user_id, username, email, password_hash)
    VALUES (gen_random_uuid(), 'dbtest_' || p_suffix || '_u1', 'dbtest_' || p_suffix || '_u1@example.com', 'h') RETURNING user_id INTO v_user1;
  INSERT INTO users (user_id, username, email, password_hash)
    VALUES (gen_random_uuid(), 'dbtest_' || p_suffix || '_u2', 'dbtest_' || p_suffix || '_u2@example.com', 'h') RETURNING user_id INTO v_user2;

  INSERT INTO leagues (league_id, name, created_by_membership_id) VALUES (r.league_id, 'DB Test League ' || p_suffix, r.admin_membership_id);
  INSERT INTO league_memberships (league_membership_id, league_id, user_id, is_administrator, status, joined_at)
    VALUES (r.admin_membership_id, r.league_id, v_user1, true, 'active', now());
  INSERT INTO league_memberships (league_membership_id, league_id, user_id, is_administrator, status, joined_at)
    VALUES (r.member_membership_id, r.league_id, v_user2, false, 'active', now());

  INSERT INTO league_configurations (league_id) VALUES (r.league_id);

  INSERT INTO epl_seasons (epl_season_identifier) VALUES ('dbtest-' || p_suffix);
  r.season_id := gen_random_uuid();
  INSERT INTO seasons (season_id, league_id, epl_season_identifier, start_date) VALUES (r.season_id, r.league_id, 'dbtest-' || p_suffix, '2026-08-01');
  INSERT INTO season_configurations
    SELECT r.season_id, initial_squad_size, weekly_roster_size, positional_minimum_gk, positional_minimum_def,
           positional_minimum_mid, positional_minimum_fwd, draft_timer_seconds_initial, draft_timer_seconds_secondary,
           draft_timer_seconds_replacement, secondary_draft_selections_per_team, secondary_draft_scheduling_offset_days,
           gameweek_roster_lock_offset_before_kickoff_minutes, league_points_win, league_points_draw, league_points_loss,
           invitation_expiration_days, replacement_selection_cap, gameweek_reminder_lead_time_hours, tie_break_ruleset_version, '{}'
    FROM league_configurations WHERE league_id = r.league_id;

  r.fantasy_team_id := gen_random_uuid();
  INSERT INTO fantasy_teams (fantasy_team_id, league_membership_id, season_id) VALUES (r.fantasy_team_id, r.admin_membership_id, r.season_id);
  r.fantasy_team_id_2 := gen_random_uuid();
  INSERT INTO fantasy_teams (fantasy_team_id, league_membership_id, season_id) VALUES (r.fantasy_team_id_2, r.member_membership_id, r.season_id);

  r.club_id := gen_random_uuid();
  INSERT INTO clubs (club_id, epl_club_id, name, short_name) VALUES (r.club_id, 'dbtest-club-' || p_suffix, 'DB Test Club', 'DTC');

  r.gameweek_id := gen_random_uuid();
  INSERT INTO gameweeks (gameweek_id, epl_season_identifier, number, roster_lock_deadline) VALUES (r.gameweek_id, 'dbtest-' || p_suffix, 1, now() + interval '7 days');

  r.player_id := gen_random_uuid();
  INSERT INTO players (player_id, epl_player_id, name, "position", current_club_id) VALUES (r.player_id, 'dbtest-p1-' || p_suffix, 'DB Test Player One', 'gk', r.club_id);
  r.player_id_2 := gen_random_uuid();
  INSERT INTO players (player_id, epl_player_id, name, "position", current_club_id) VALUES (r.player_id_2, 'dbtest-p2-' || p_suffix, 'DB Test Player Two', 'def', r.club_id);

  RETURN r;
END;
$$ LANGUAGE plpgsql;
