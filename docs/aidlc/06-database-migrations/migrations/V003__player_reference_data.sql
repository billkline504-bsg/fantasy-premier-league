-- Fantasy EPL League Manager — Database Migrations
-- V003: Player & EPL Data context — reference data (Architecture §6.10, §5).
--
-- Inputs: Architecture and Domain Model v1.15 §6.10, §9.2; BRD v1.17 BR-092, BR-100–BR-106,
-- BR-227–BR-234, BR-313, BR-329–BR-335.
--
-- Created before League & Season / FantasyTeam / Draft / Roster / Scoring (V004+) even though
-- Architecture §5's module-map table lists Player & EPL Data after Fantasy Team: that table
-- describes application-/service-layer dependency direction, not database foreign-key direction.
-- Players/Clubs/Fixtures/Gameweeks are pure reference data with no inbound FKs to League/Season/
-- FantasyTeam, so physically they must exist first to satisfy every later table's FK to them.

CREATE TYPE player_position AS ENUM ('gk', 'def', 'mid', 'fwd');
CREATE TYPE fixture_status AS ENUM ('scheduled', 'postponed', 'in_progress', 'completed', 'abandoned');
CREATE TYPE performance_source AS ENUM ('official_fpl', 'manual');

CREATE TABLE epl_seasons (
  epl_season_identifier text PRIMARY KEY -- e.g. '2026/27'
);
COMMENT ON TABLE epl_seasons IS
  'Physical-design addition: Architecture v1.15 §6.10/§9.2 exposes /api/v1/epl/seasons/{eplSeasonId}/... as a platform-level, non-League-scoped resource, but does not name a table for the identifier itself. This is that anchor, decoupled from the League-scoped `seasons` table (V004) — many Leagues'' Seasons can point at the same EPL season.';

CREATE TABLE clubs (
  club_id     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  epl_club_id text NOT NULL, -- external identifier; sync upserts on this, never blind-inserts (BR-232)
  name        text NOT NULL,
  short_name  text NOT NULL
);
CREATE UNIQUE INDEX ux_clubs_epl_club_id ON clubs (epl_club_id);

CREATE TABLE players (
  player_id       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  epl_player_id   text NOT NULL, -- external identifier; sync upserts on this (BR-232)
  name            text NOT NULL,
  "position"      player_position NOT NULL,
  current_club_id uuid REFERENCES clubs (club_id) -- null once the player has exited the EPL (BR-066)
);
CREATE UNIQUE INDEX ux_players_epl_player_id ON players (epl_player_id);
CREATE INDEX ix_players_current_club ON players (current_club_id);

CREATE TABLE gameweeks (
  gameweek_id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  epl_season_identifier text NOT NULL REFERENCES epl_seasons (epl_season_identifier),
  number                integer NOT NULL,
  roster_lock_deadline  timestamptz NOT NULL -- BR-093
);
CREATE UNIQUE INDEX ux_gameweeks_season_number ON gameweeks (epl_season_identifier, number);

CREATE TABLE fixtures (
  fixture_id     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  epl_fixture_id text NOT NULL, -- external identifier; sync upserts on this (BR-232)
  gameweek_id    uuid NOT NULL REFERENCES gameweeks (gameweek_id),
  home_club_id   uuid NOT NULL REFERENCES clubs (club_id),
  away_club_id   uuid NOT NULL REFERENCES clubs (club_id),
  kickoff_time   timestamptz NOT NULL,
  status         fixture_status NOT NULL DEFAULT 'scheduled', -- BR-100–BR-106: postponed/abandoned handling
  home_goals     integer,
  away_goals     integer,
  CHECK (home_club_id <> away_club_id)
);
CREATE UNIQUE INDEX ux_fixtures_epl_fixture_id ON fixtures (epl_fixture_id);
CREATE INDEX ix_fixtures_gameweek ON fixtures (gameweek_id);
CREATE INDEX ix_fixtures_home_club ON fixtures (home_club_id);
CREATE INDEX ix_fixtures_away_club ON fixtures (away_club_id);

CREATE TABLE club_standings (
  epl_season_identifier text NOT NULL REFERENCES epl_seasons (epl_season_identifier),
  club_id               uuid NOT NULL REFERENCES clubs (club_id),
  "position"            integer NOT NULL,
  played                integer NOT NULL,
  won                   integer NOT NULL,
  drawn                 integer NOT NULL,
  lost                  integer NOT NULL,
  goals_for             integer NOT NULL,
  goals_against         integer NOT NULL,
  goal_difference       integer GENERATED ALWAYS AS (goals_for - goals_against) STORED,
  points                integer NOT NULL,
  PRIMARY KEY (epl_season_identifier, club_id)
);
COMMENT ON TABLE club_standings IS
  'BR-329–BR-335: real-world EPL table, recomputed from fixtures as official data is synced — the same "recomputed read-model" pattern as league_standings (V009), but with no Administrator-override path (BR-334), since this reflects the real EPL, not Fantasy scoring.';
