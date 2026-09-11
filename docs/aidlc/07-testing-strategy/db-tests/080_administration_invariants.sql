-- Corrections & Administration invariants (Architecture §6.8, §12.1; BRD BR-149; ADR-010; test catalog BR-254).

DO $$
DECLARE
  f dbtest_fixture;
  v_action_id uuid;
BEGIN
  f := dbtest_create_baseline_fixture('admin');

  INSERT INTO administrative_actions (action_id, league_id, acting_membership_id, action_type, target_entity_type, target_entity_id, before_state, after_state)
    VALUES (gen_random_uuid(), f.league_id, f.admin_membership_id, 'roster_correction', 'FantasyTeam', f.fantasy_team_id, '{}'::jsonb, '{}'::jsonb)
    RETURNING action_id INTO v_action_id;

  -- A null acting_membership_id represents a system-generated entry (BR-308) and must be allowed.
  BEGIN
    INSERT INTO administrative_actions (action_id, league_id, acting_membership_id, action_type, target_entity_type, target_entity_id, before_state, after_state)
      VALUES (gen_random_uuid(), f.league_id, NULL, 'replacement_eligibility_granted', 'FantasyTeam', f.fantasy_team_id, '{}'::jsonb, '{}'::jsonb);
    PERFORM test_assert('administration.null_acting_membership_allowed_for_system_actions', true);
  EXCEPTION WHEN OTHERS THEN
    PERFORM test_assert('administration.null_acting_membership_allowed_for_system_actions', false, SQLERRM);
  END;

  -- ADR-010/BR-149: the runtime application role must not be able to UPDATE or DELETE an
  -- administrative_actions row, at the database level, regardless of what the application code
  -- attempts. This is checked here by actually switching to that role (not just reading its grants).
  BEGIN
    SET ROLE eplfantasy_app;
    BEGIN
      UPDATE administrative_actions SET reason = 'tampered' WHERE action_id = v_action_id;
      PERFORM test_assert('administration.audit_log_immutable_at_role_level', false, 'eplfantasy_app was wrongly able to UPDATE administrative_actions');
    EXCEPTION WHEN insufficient_privilege THEN
      PERFORM test_assert('administration.audit_log_immutable_at_role_level', true);
    END;
    RESET ROLE;
  END;
END $$;
