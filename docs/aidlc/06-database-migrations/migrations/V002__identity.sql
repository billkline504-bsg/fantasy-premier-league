-- Fantasy EPL League Manager — Database Migrations
-- V002: Identity & User context (Architecture §6.1).
--
-- Inputs: Architecture and Domain Model v1.15 §6.1, §8.1; BRD v1.17 BR-001–BR-015, BR-158,
-- BR-159, BR-284–BR-286, BR-298, BR-300–BR-301, BR-326.

CREATE TYPE user_status AS ENUM ('active', 'retired');

CREATE TABLE profile_icons (
  profile_icon_id  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name             text NOT NULL,
  asset_identifier text NOT NULL, -- BR-011: server-controlled path, never client-supplied
  is_active        boolean NOT NULL DEFAULT true,
  sort_order       integer NOT NULL DEFAULT 0
);
COMMENT ON TABLE profile_icons IS
  'BR-011: application-controlled icon catalog. Rows are seeded/managed by operators (see V012), never created via a user-facing API.';

CREATE TABLE users (
  user_id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(), -- BR-002: immutable identifier
  username                text NOT NULL,
  email                   text NOT NULL,
  password_hash           text NOT NULL, -- BR-158: adaptive hash only, never reversible/plaintext
  status                  user_status NOT NULL DEFAULT 'active',
  is_system_administrator boolean NOT NULL DEFAULT false, -- BR-300: platform-level; never settable via any self-service API
  created_at              timestamptz NOT NULL DEFAULT now(),
  updated_at              timestamptz NOT NULL DEFAULT now(),
  retired_at              timestamptz
);
COMMENT ON COLUMN users.username IS
  'BR-003/BR-004: unique among ACTIVE Users only — enforced by ux_users_username_active below, not a table-level UNIQUE, because a retired user''s username must become immediately reusable (BR-298).';

-- BR-004/BR-298: uniqueness among active users only; case-insensitive to avoid look-alike collisions.
CREATE UNIQUE INDEX ux_users_username_active ON users (lower(username)) WHERE status = 'active';
CREATE UNIQUE INDEX ux_users_email_active ON users (lower(email)) WHERE status = 'active';

CREATE TABLE username_history (
  username_history_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id              uuid NOT NULL REFERENCES users (user_id),
  username             text NOT NULL,
  effective_from       timestamptz NOT NULL,
  effective_to         timestamptz -- null = currently active
);
COMMENT ON TABLE username_history IS
  'BR-326: append-only per-User username timeline, opened at UserRegistered and rolled forward on every UsernameChanged. Historical screens resolve a display username by finding the row effective at a record''s own timestamp, rather than joining directly to users.username, so no historical table needs a username column of its own.';

-- BR-326: at most one open (currently-active) row per User.
CREATE UNIQUE INDEX ux_username_history_open ON username_history (user_id) WHERE effective_to IS NULL;
CREATE INDEX ix_username_history_user_range ON username_history (user_id, effective_from, effective_to);

CREATE TABLE user_profiles (
  user_id         uuid PRIMARY KEY REFERENCES users (user_id),
  default_icon_id uuid NOT NULL REFERENCES profile_icons (profile_icon_id), -- BR-006
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE user_profiles IS
  'BR-005: global profile preferences only. No NotificationPreferencesSummary column — Architecture v1.15 §6.1 removed that field; notification preferences are per-LeagueMembership (see V011, BR-338).';

-- Physical-design additions: F-001.1/F-001.2 (BR-156, BR-159, BR-284) clearly require persisted
-- token state, but Architecture v1.15 names only the *behavior*, not an entity for it. These two
-- tables are that entity, sized and indexed the same way as every other token-bearing table here.

CREATE TABLE refresh_tokens (
  refresh_token_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id          uuid NOT NULL REFERENCES users (user_id),
  token_hash       text NOT NULL, -- store only a hash of the token value, never the raw token (BR-171)
  issued_at        timestamptz NOT NULL DEFAULT now(),
  expires_at       timestamptz NOT NULL,
  revoked_at       timestamptz
);
CREATE UNIQUE INDEX ux_refresh_tokens_hash ON refresh_tokens (token_hash);
CREATE INDEX ix_refresh_tokens_user ON refresh_tokens (user_id) WHERE revoked_at IS NULL;

CREATE TABLE password_reset_tokens (
  password_reset_token_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id                 uuid NOT NULL REFERENCES users (user_id),
  token_hash              text NOT NULL, -- BR-284: time-limited, single-use; store only a hash
  created_at              timestamptz NOT NULL DEFAULT now(),
  expires_at              timestamptz NOT NULL,
  used_at                 timestamptz
);
CREATE UNIQUE INDEX ux_password_reset_tokens_hash ON password_reset_tokens (token_hash);
CREATE INDEX ix_password_reset_tokens_user ON password_reset_tokens (user_id) WHERE used_at IS NULL;
