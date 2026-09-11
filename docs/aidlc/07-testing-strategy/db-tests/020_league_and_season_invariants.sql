-- League & Season invariants (Architecture §6.2; BRD BR-020, BR-024, BR-029, BR-283; test catalog BR-244).

DO $$
DECLARE
  v_user1 uuid; v_user2 uuid;
  v_league_id uuid := gen_random_uuid();
  v_membership1 uuid := gen_random_uuid();
  v_membership2 uuid := gen_random_uuid();
BEGIN
  -- BR-024: League and its founding LeagueMembership are mutually referential; both insert in one
  -- transaction, relying on fk_leagues_created_by_membership being DEFERRABLE INITIALLY DEFERRED.
  BEGIN
    INSERT INTO users (user_id, username, email, password_hash) VALUES (gen_random_uuid(), 'dbtest_league_admin', 'dbtest_league_admin@example.com', 'h') RETURNING user_id INTO v_user1;
    INSERT INTO leagues (league_id, name, created_by_membership_id) VALUES (v_league_id, 'DB Test League', v_membership1);
    INSERT INTO league_memberships (league_membership_id, league_id, user_id, is_administrator, status, joined_at)
      VALUES (v_membership1, v_league_id, v_user1, true, 'active', now());
    PERFORM test_assert('league.circular_fk_deferred_to_commit', true);
  EXCEPTION WHEN OTHERS THEN
    PERFORM test_assert('league.circular_fk_deferred_to_commit', false, SQLERRM);
  END;

  -- BR-283: exactly one League Administrator per League.
  INSERT INTO users (user_id, username, email, password_hash) VALUES (gen_random_uuid(), 'dbtest_league_member', 'dbtest_league_member@example.com', 'h') RETURNING user_id INTO v_user2;
  INSERT INTO league_memberships (league_membership_id, league_id, user_id, is_administrator, status, joined_at)
    VALUES (v_membership2, v_league_id, v_user2, false, 'active', now());
  BEGIN
    UPDATE league_memberships SET is_administrator = true WHERE league_membership_id = v_membership2;
    PERFORM test_assert('league.one_administrator_per_league', false, 'a second League Administrator was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('league.one_administrator_per_league', true);
  END;

  -- BR-020: a User may have only one ACTIVE membership per League — a duplicate active row for
  -- the same (league, user) must be rejected...
  BEGIN
    INSERT INTO league_memberships (league_membership_id, league_id, user_id, is_administrator, status, joined_at)
      VALUES (gen_random_uuid(), v_league_id, v_user2, false, 'active', now());
    PERFORM test_assert('league.one_active_membership_per_user', false, 'a second active membership for the same (league, user) was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('league.one_active_membership_per_user', true);
  END;

  -- ...but rejoining after leaving (a new row once the old one is 'left') must be allowed.
  UPDATE league_memberships SET status = 'left', left_at = now() WHERE league_membership_id = v_membership2;
  BEGIN
    INSERT INTO league_memberships (league_membership_id, league_id, user_id, is_administrator, status, joined_at)
      VALUES (gen_random_uuid(), v_league_id, v_user2, false, 'active', now());
    PERFORM test_assert('league.rejoin_after_leaving_allowed', true);
  EXCEPTION WHEN OTHERS THEN
    PERFORM test_assert('league.rejoin_after_leaving_allowed', false, SQLERRM);
  END;

  -- BR-029: invitation tokens are unique.
  INSERT INTO invitations (league_id, token, destination, channel, expires_at)
    VALUES (v_league_id, 'dbtest-fixed-token', 'invitee@example.com', 'email', now() + interval '7 days');
  BEGIN
    INSERT INTO invitations (league_id, token, destination, channel, expires_at)
      VALUES (v_league_id, 'dbtest-fixed-token', 'invitee2@example.com', 'email', now() + interval '7 days');
    PERFORM test_assert('league.invitation_token_unique', false, 'duplicate invitation token was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('league.invitation_token_unique', true);
  END;
END $$;
