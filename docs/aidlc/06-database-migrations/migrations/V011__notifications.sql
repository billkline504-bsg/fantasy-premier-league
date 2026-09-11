-- Fantasy EPL League Manager — Database Migrations
-- V011: Notifications context (Architecture §6.9, §12.2).
--
-- Inputs: Architecture and Domain Model v1.15 §6.9, §12.2, ADR-005; BRD v1.17 BR-150–BR-155,
-- BR-224–BR-226, BR-338–BR-339.

CREATE TYPE notification_event_type AS ENUM ('gameweek_reminder', 'weekly_score', 'weekly_standings');
CREATE TYPE notification_channel AS ENUM ('email', 'sms');
CREATE TYPE notification_status AS ENUM ('pending', 'sent', 'failed', 'suppressed');

CREATE TABLE notification_preferences (
  league_membership_id uuid NOT NULL REFERENCES league_memberships (league_membership_id), -- BR-338: per-Membership, not per-User — a User in three Leagues holds up to three independent rows per (event_type, channel)
  event_type           notification_event_type NOT NULL,
  channel              notification_channel NOT NULL,
  enabled              boolean NOT NULL DEFAULT false,
  PRIMARY KEY (league_membership_id, event_type, channel)
);
COMMENT ON TABLE notification_preferences IS
  'BR-338: seeded (all channels disabled) when a league_memberships row is created; no longer queried once that membership''s status is left. A screen showing all of a User''s preferences across Leagues joins through league_memberships.user_id, since there is no User-level rollup row.';

CREATE TABLE notification_requests (
  request_id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id              uuid NOT NULL REFERENCES users (user_id), -- delivery target: which inbox/phone, regardless of League
  league_membership_id uuid NOT NULL REFERENCES league_memberships (league_membership_id), -- BR-338: which League's notification_preferences row gates this request
  event_type           notification_event_type NOT NULL,
  channel              notification_channel NOT NULL,
  payload              jsonb NOT NULL,
  status               notification_status NOT NULL DEFAULT 'pending',
  attempts             integer NOT NULL DEFAULT 0,
  created_at           timestamptz NOT NULL DEFAULT now(),
  last_attempt_at      timestamptz
);
CREATE INDEX ix_notification_requests_pending ON notification_requests (status, created_at) WHERE status = 'pending';
COMMENT ON TABLE notification_requests IS
  'BR-224/BR-225: outbox pattern. A domain event handler (e.g. RosterLocked, GameweekScore calculated) writes a row here in the same transaction as the triggering change; a separate background worker drains status = pending rows, checks the matching notification_preferences row (BR-226 — a disabled channel/event is never sent), and retries failures with backoff. The specific email/SMS provider remains an open infrastructure decision (Architecture §15) independent of this schema.';
