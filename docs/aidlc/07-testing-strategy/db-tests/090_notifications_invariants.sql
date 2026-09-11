-- Notifications invariants (Architecture §6.9; BRD BR-338; test catalog covered under BR-244's
-- League-authorization tests, since preferences are League-Membership-scoped).

DO $$
DECLARE
  f dbtest_fixture;
BEGIN
  f := dbtest_create_baseline_fixture('notify');

  -- BR-338: preferences are keyed per LeagueMembership, not per User — one row per (membership, event, channel).
  INSERT INTO notification_preferences (league_membership_id, event_type, channel, enabled) VALUES (f.admin_membership_id, 'gameweek_reminder', 'email', true);
  BEGIN
    INSERT INTO notification_preferences (league_membership_id, event_type, channel, enabled) VALUES (f.admin_membership_id, 'gameweek_reminder', 'email', false);
    PERFORM test_assert('notifications.preference_unique_per_membership_event_channel', false, 'a duplicate preference row was wrongly accepted');
  EXCEPTION WHEN unique_violation THEN
    PERFORM test_assert('notifications.preference_unique_per_membership_event_channel', true);
  END;

  -- The same User's second LeagueMembership (a different League) must be able to hold an
  -- independent preference row for the same event/channel — proving scoping is per-Membership,
  -- not accidentally per-User (BR-338's whole point).
  DECLARE
    f2 dbtest_fixture;
  BEGIN
    f2 := dbtest_create_baseline_fixture('notify_other_league');
    INSERT INTO notification_preferences (league_membership_id, event_type, channel, enabled) VALUES (f2.admin_membership_id, 'gameweek_reminder', 'email', false);
    PERFORM test_assert('notifications.preference_independent_per_league_membership', true);
  EXCEPTION WHEN OTHERS THEN
    PERFORM test_assert('notifications.preference_independent_per_league_membership', false, SQLERRM);
  END;
END $$;
