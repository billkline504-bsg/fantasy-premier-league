-- Fantasy Team invariants (Architecture §6.3; BRD BR-019, BR-035, BR-191, BR-193; test catalog BR-245).

DO $$
DECLARE
  f dbtest_fixture;
BEGIN
  f := dbtest_create_baseline_fixture('squad');

  -- BR-019/BR-193, Invariant 2: one FantasyTeam per (LeagueMembership, Season) — the fixture
  -- already has one team for admin_membership_id; a second for the same pair must be rejected.
  BEGIN
    INSERT INTO fantasy_teams (fantasy_team_id, league_membership_id, season_id) VALUES (gen_random_uuid(), f.admin_membership_id, f.season_id);
    PERFORM test_assert('fantasy_team.one_per_membership_season', false, 'a second FantasyTeam for the same (membership, season) was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('fantasy_team.one_per_membership_season', true);
  END;

  -- BR-035/BR-191, Invariant 1 (the AP-009/AP-010 draft-pick guarantee rests on this): a Player
  -- may have at most one currently-owned SquadPlayer per (League, Season).
  INSERT INTO squad_players (fantasy_team_id, player_id, season_id, acquisition_type) VALUES (f.fantasy_team_id, f.player_id, f.season_id, 'initial_draft');
  BEGIN
    INSERT INTO squad_players (fantasy_team_id, player_id, season_id, acquisition_type) VALUES (f.fantasy_team_id_2, f.player_id, f.season_id, 'initial_draft');
    PERFORM test_assert('fantasy_team.squad_ownership_unique_per_season', false, 'the same Player was wrongly owned by two FantasyTeams in one Season');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('fantasy_team.squad_ownership_unique_per_season', true);
  END;

  -- The same Player may be owned by two different FantasyTeams across two *different* Seasons —
  -- uniqueness is scoped per Season, not global.
  DECLARE
    f2 dbtest_fixture;
  BEGIN
    f2 := dbtest_create_baseline_fixture('squad_other_season');
    INSERT INTO squad_players (fantasy_team_id, player_id, season_id, acquisition_type) VALUES (f2.fantasy_team_id, f.player_id, f2.season_id, 'initial_draft');
    PERFORM test_assert('fantasy_team.squad_ownership_scoped_per_season', true);
  EXCEPTION WHEN OTHERS THEN
    PERFORM test_assert('fantasy_team.squad_ownership_scoped_per_season', false, SQLERRM);
  END;

  -- Releasing a player (is_currently_owned = false) must free the ownership slot for another team.
  UPDATE squad_players SET is_currently_owned = false, released_at = now() WHERE fantasy_team_id = f.fantasy_team_id AND player_id = f.player_id;
  BEGIN
    INSERT INTO squad_players (fantasy_team_id, player_id, season_id, acquisition_type) VALUES (f.fantasy_team_id_2, f.player_id, f.season_id, 'replacement');
    PERFORM test_assert('fantasy_team.squad_ownership_freed_on_release', true);
  EXCEPTION WHEN OTHERS THEN
    PERFORM test_assert('fantasy_team.squad_ownership_freed_on_release', false, SQLERRM);
  END;
END $$;
