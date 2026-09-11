-- Identity & User invariants (Architecture §6.1; BRD BR-004, BR-298, BR-326; test catalog BR-243).
-- Run after 000_test_harness.sql and after migrations V001–V013 are applied.

DO $$
DECLARE
  v_user_a uuid;
  v_user_b uuid;
BEGIN
  -- BR-004: username unique among ACTIVE users (case-insensitive).
  INSERT INTO users (user_id, username, email, password_hash) VALUES (gen_random_uuid(), 'dbtest_alpha', 'dbtest_alpha@example.com', 'h') RETURNING user_id INTO v_user_a;
  BEGIN
    INSERT INTO users (user_id, username, email, password_hash) VALUES (gen_random_uuid(), 'DBTest_Alpha', 'dbtest_alpha2@example.com', 'h');
    PERFORM test_assert('identity.username_unique_among_active', false, 'duplicate active username (case-insensitive) was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('identity.username_unique_among_active', true);
  END;

  -- BR-004: same guard applies to email.
  BEGIN
    INSERT INTO users (user_id, username, email, password_hash) VALUES (gen_random_uuid(), 'dbtest_alpha_2', 'DBTest_Alpha@example.com', 'h');
    PERFORM test_assert('identity.email_unique_among_active', false, 'duplicate active email (case-insensitive) was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('identity.email_unique_among_active', true);
  END;

  -- BR-298: retiring a user immediately frees its username for reuse by anyone.
  UPDATE users SET status = 'retired', retired_at = now() WHERE user_id = v_user_a;
  BEGIN
    INSERT INTO users (user_id, username, email, password_hash) VALUES (gen_random_uuid(), 'dbtest_alpha', 'dbtest_alpha_new@example.com', 'h') RETURNING user_id INTO v_user_b;
    PERFORM test_assert('identity.username_reusable_after_retirement', true);
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('identity.username_reusable_after_retirement', false, 'retired username was wrongly still reserved');
  END;

  -- BR-326: at most one open (effective_to IS NULL) UsernameHistory row per User.
  INSERT INTO username_history (user_id, username, effective_from) VALUES (v_user_b, 'dbtest_alpha', now());
  BEGIN
    INSERT INTO username_history (user_id, username, effective_from) VALUES (v_user_b, 'dbtest_alpha_renamed', now());
    PERFORM test_assert('identity.username_history_one_open_row', false, 'a second open UsernameHistory row was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('identity.username_history_one_open_row', true);
  END;

  -- Closing the open row and opening a new one (the app's actual UsernameChanged handling) must succeed.
  BEGIN
    UPDATE username_history SET effective_to = now() WHERE user_id = v_user_b AND effective_to IS NULL;
    INSERT INTO username_history (user_id, username, effective_from) VALUES (v_user_b, 'dbtest_alpha_renamed', now());
    PERFORM test_assert('identity.username_history_rollover_after_close', true);
  EXCEPTION WHEN OTHERS THEN
    PERFORM test_assert('identity.username_history_rollover_after_close', false, SQLERRM);
  END;

  -- Refresh/password-reset token hashes must be unique (physical-design additions, V002).
  DECLARE
    v_hash text := 'dbtest-fixed-hash-value';
  BEGIN
    INSERT INTO refresh_tokens (user_id, token_hash, expires_at) VALUES (v_user_b, v_hash, now() + interval '30 days');
    BEGIN
      INSERT INTO refresh_tokens (user_id, token_hash, expires_at) VALUES (v_user_b, v_hash, now() + interval '30 days');
      PERFORM test_assert('identity.refresh_token_hash_unique', false, 'duplicate refresh token hash was wrongly accepted');
    EXCEPTION WHEN unique_violation THEN
      PERFORM test_assert('identity.refresh_token_hash_unique', true);
    END;
  END;
END $$;
