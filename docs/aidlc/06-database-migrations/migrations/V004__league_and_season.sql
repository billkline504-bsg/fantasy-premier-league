-- Fantasy EPL League Manager — Database Migrations
-- V004: League & Season context (Architecture §6.2).
--
-- Inputs: Architecture and Domain Model v1.15 §6.2, §8.1, ADR-011; BRD v1.17 BR-017–BR-033,
-- BR-161–BR-162, BR-221–BR-223, BR-283, BR-290–BR-297.

CREATE TYPE league_status AS ENUM ('active', 'archived');
CREATE TYPE membership_status AS ENUM ('invited', 'active', 'left');
CREATE TYPE season_status AS ENUM ('setup', 'draft_in_progress', 'in_season', 'completed');
CREATE TYPE invitation_status AS ENUM ('pending', 'accepted', 'expired', 'revoked');
CREATE TYPE invitation_channel AS ENUM ('email', 'sms');

CREATE TABLE leagues (
  league_id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name                     text NOT NULL,
  description              text,
  status                   league_status NOT NULL DEFAULT 'active',
  created_by_membership_id uuid NOT NULL, -- FK added below (mutually referential with league_memberships, BR-024)
  created_at               timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE league_memberships (
  league_membership_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  league_id            uuid NOT NULL REFERENCES leagues (league_id),
  user_id              uuid NOT NULL REFERENCES users (user_id),
  is_administrator     boolean NOT NULL DEFAULT false, -- BR-024/BR-025: per-League, not global (ADR-007)
  league_icon_id       uuid REFERENCES profile_icons (profile_icon_id), -- BR-007–BR-009: override of the User's default icon
  status               membership_status NOT NULL DEFAULT 'invited',
  joined_at            timestamptz,
  left_at              timestamptz
);

-- Leagues and LeagueMemberships are mutually referential: League creation and its founding
-- Membership (BR-024, the creator becomes Administrator) are inserted in the same transaction,
-- each carrying the other's already-generated id. DEFERRABLE lets Postgres validate this FK only
-- at COMMIT, once both rows exist, instead of rejecting the first INSERT.
ALTER TABLE leagues
  ADD CONSTRAINT fk_leagues_created_by_membership
  FOREIGN KEY (created_by_membership_id) REFERENCES league_memberships (league_membership_id)
  DEFERRABLE INITIALLY DEFERRED;

-- BR-020: a User may have only one ACTIVE membership per League (may still rejoin across seasons
-- after leaving — a new row with status back to 'active' is permitted once the old one is 'left').
CREATE UNIQUE INDEX ux_league_memberships_active ON league_memberships (league_id, user_id) WHERE status <> 'left';
-- BR-283: exactly one League Administrator per League (initial release scope).
CREATE UNIQUE INDEX ux_league_memberships_one_admin ON league_memberships (league_id) WHERE is_administrator;
CREATE INDEX ix_league_memberships_user ON league_memberships (user_id);

-- ADR-011: League-level default configuration, one column per configurable parameter — never a
-- literal scattered through domain/application code. Mutable at any time (BR-292); changes only
-- affect Seasons created afterward or not-yet-locked fields of an in-progress one (season_configurations
-- below carries the actual lock state per Season).
CREATE TABLE league_configurations (
  league_id                                         uuid PRIMARY KEY REFERENCES leagues (league_id),
  initial_squad_size                                integer NOT NULL DEFAULT 25,  -- BR-034/BR-052/BR-197
  weekly_roster_size                                integer NOT NULL DEFAULT 15,  -- BR-037/BR-196
  positional_minimum_gk                             integer NOT NULL DEFAULT 1,   -- BR-279
  positional_minimum_def                            integer NOT NULL DEFAULT 3,
  positional_minimum_mid                            integer NOT NULL DEFAULT 2,
  positional_minimum_fwd                            integer NOT NULL DEFAULT 1,
  draft_timer_seconds_initial                       integer NOT NULL DEFAULT 300, -- BR-057
  draft_timer_seconds_secondary                     integer NOT NULL DEFAULT 300,
  draft_timer_seconds_replacement                   integer NOT NULL DEFAULT 300,
  secondary_draft_selections_per_team               integer NOT NULL DEFAULT 5,   -- BR-060
  secondary_draft_scheduling_offset_days            integer NOT NULL DEFAULT 1,   -- BR-069/BR-281
  gameweek_roster_lock_offset_before_kickoff_minutes integer NOT NULL DEFAULT 60,  -- BR-093
  league_points_win                                 integer NOT NULL DEFAULT 3,   -- BR-115–BR-117
  league_points_draw                                integer NOT NULL DEFAULT 1,
  league_points_loss                                integer NOT NULL DEFAULT 0,
  invitation_expiration_days                        integer NOT NULL DEFAULT 7,   -- BR-029
  replacement_selection_cap                         integer,                      -- null = uncapped (BR-287)
  gameweek_reminder_lead_time_hours                 integer NOT NULL DEFAULT 24,  -- BR-152/BR-309
  tie_break_ruleset_version                         text NOT NULL DEFAULT 'v1',   -- ADR-008
  updated_at                                        timestamptz NOT NULL DEFAULT now(),
  updated_by_membership_id                          uuid REFERENCES league_memberships (league_membership_id)
);
COMMENT ON TABLE league_configurations IS
  'BR-290–BR-292/ADR-011: League-level default configuration. Every table below that depends on a configurable parameter reads it from here (or from season_configurations once a Season exists), never from a literal.';

CREATE TABLE seasons (
  season_id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  league_id             uuid NOT NULL REFERENCES leagues (league_id),
  epl_season_identifier text NOT NULL REFERENCES epl_seasons (epl_season_identifier),
  status                season_status NOT NULL DEFAULT 'setup',
  start_date            date NOT NULL,
  end_date              date
);
CREATE INDEX ix_seasons_league ON seasons (league_id);

-- Copied from league_configurations at Season creation (BR-292) and then independently overridable
-- per field, up until each field's own lock point (BR-291/BR-293) — tracked in locked_fields.
-- A completed Season's row is retained permanently (BR-296) and never deleted or overwritten.
CREATE TABLE season_configurations (
  season_id                                         uuid PRIMARY KEY REFERENCES seasons (season_id),
  initial_squad_size                                integer NOT NULL,
  weekly_roster_size                                integer NOT NULL,
  positional_minimum_gk                             integer NOT NULL,
  positional_minimum_def                            integer NOT NULL,
  positional_minimum_mid                            integer NOT NULL,
  positional_minimum_fwd                            integer NOT NULL,
  draft_timer_seconds_initial                       integer NOT NULL,
  draft_timer_seconds_secondary                     integer NOT NULL,
  draft_timer_seconds_replacement                   integer NOT NULL,
  secondary_draft_selections_per_team               integer NOT NULL,
  secondary_draft_scheduling_offset_days            integer NOT NULL,
  gameweek_roster_lock_offset_before_kickoff_minutes integer NOT NULL,
  league_points_win                                 integer NOT NULL,
  league_points_draw                                integer NOT NULL,
  league_points_loss                                integer NOT NULL,
  invitation_expiration_days                        integer NOT NULL,
  replacement_selection_cap                         integer,
  gameweek_reminder_lead_time_hours                 integer NOT NULL,
  tie_break_ruleset_version                         text NOT NULL,
  locked_fields                                     text[] NOT NULL DEFAULT '{}' -- BR-293/BR-294: field names past their "Locks At" point
);
COMMENT ON TABLE season_configurations IS
  'BR-293/BR-294: a PUT against a field already present in locked_fields must be rejected (409) at the application layer — this table records lock state but does not enforce it via a CHECK, since the lock points differ per field and are time/event-driven (Architecture §6.2), not statically derivable from the row alone.';

CREATE TABLE invitations (
  invitation_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  league_id     uuid NOT NULL REFERENCES leagues (league_id),
  season_id     uuid REFERENCES seasons (season_id), -- BR-033: invitations are resent per Season
  token         text NOT NULL,
  destination   text NOT NULL, -- BR-028: email or phone, per channel
  channel       invitation_channel NOT NULL,
  created_at    timestamptz NOT NULL DEFAULT now(),
  expires_at    timestamptz NOT NULL, -- created_at + LeagueConfiguration.invitation_expiration_days at issuance (BR-029)
  status        invitation_status NOT NULL DEFAULT 'pending'
);
CREATE UNIQUE INDEX ux_invitations_token ON invitations (token);
CREATE INDEX ix_invitations_league ON invitations (league_id);

CREATE TABLE league_messages (
  league_message_id    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  league_id            uuid NOT NULL REFERENCES leagues (league_id),
  author_membership_id uuid NOT NULL REFERENCES league_memberships (league_membership_id), -- BR-221: must be the Administrator (checked at the application layer)
  body                 text NOT NULL, -- BR-167: output-encoded on every render, never interpreted as markup
  published_at         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_league_messages_league ON league_messages (league_id, published_at DESC);
