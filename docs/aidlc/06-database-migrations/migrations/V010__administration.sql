-- Fantasy EPL League Manager — Database Migrations
-- V010: Corrections & Administration context (Architecture §6.8, §12.1).
--
-- Inputs: Architecture and Domain Model v1.15 §6.8, §8.1, §12.1, ADR-007, ADR-010; BRD v1.17
-- BR-097–BR-099, BR-139–BR-149, BR-168–BR-170, BR-177–BR-182, BR-295, BR-300–BR-301, BR-308,
-- BR-321–BR-322, BR-327–BR-328.

CREATE TYPE admin_action_type AS ENUM (
  'roster_correction',
  'score_override',
  'score_override_undo',
  'replacement_eligibility_granted',
  'season_ending_injury_declared',
  'draft_timer_extended',
  'configuration_changed',
  'other'
);
CREATE TYPE security_event_type AS ENUM ('rate_limit_blocked');

CREATE TABLE administrative_actions (
  action_id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  league_id            uuid NOT NULL REFERENCES leagues (league_id),
  acting_membership_id uuid REFERENCES league_memberships (league_membership_id), -- null = system-generated (e.g. BR-308's automatic EPL-exit eligibility grant); render as "System" (Architecture §6.8), never blank/erroring
  action_type          admin_action_type NOT NULL,
  target_entity_type   text NOT NULL, -- e.g. 'FantasyTeam', 'League', 'Season' — a ConfigurationChanged row targets the League/Season itself, not a FantasyTeam
  target_entity_id     uuid NOT NULL,
  before_state         jsonb NOT NULL,
  after_state          jsonb NOT NULL,
  reason               text,
  created_at           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_admin_actions_league_created ON administrative_actions (league_id, created_at DESC);
CREATE INDEX ix_admin_actions_type ON administrative_actions (league_id, action_type);
CREATE INDEX ix_admin_actions_target ON administrative_actions (target_entity_type, target_entity_id);
COMMENT ON TABLE administrative_actions IS
  'BR-149/AP-005: append-only audit log. Every administrator-privileged mutation writes exactly one row here in the same transaction as the underlying change, via the shared IAdministrativeActionRecorder decorator (Architecture §6.8) — never left to individual handlers to remember. BR-321/BR-322: a row with no resolvable FantasyTeamId (e.g. action_type = configuration_changed) is surfaced under the application-level pseudo-value "__league_settings__" for the audit viewer''s fantasyTeamId filter, not stored as such here. UPDATE/DELETE are revoked from the application role in V013 (ADR-010) — immutability is enforced at the database role level, not just by convention.';

CREATE TABLE security_events (
  security_event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  event_type         security_event_type NOT NULL,
  endpoint           text NOT NULL,
  scope              text NOT NULL, -- e.g. "IP 203.0.113.44 + username davecoach"
  detail             text NOT NULL,
  occurred_at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_security_events_occurred ON security_events (occurred_at DESC);
COMMENT ON TABLE security_events IS
  'BR-170/BR-327: platform-level (not League-scoped) log written directly by the rate-limiting middleware on every enforced block — unlike administrative_actions, this is not tied to a LeagueId and is not an administrator-privileged mutation. Read only via the System-Administrator-only /api/v1/admin/security/events endpoint.';
