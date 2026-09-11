-- Fantasy EPL League Manager — Database Migrations
-- V007: Roster Management context (Architecture §6.5).
--
-- Inputs: Architecture and Domain Model v1.15 §6.5, §8.1, §8.3, ADR-012; BRD v1.17 BR-037–BR-046,
-- BR-093–BR-099, BR-146–BR-149, BR-194–BR-196, BR-211–BR-214, BR-279, BR-299, BR-303–BR-305,
-- BR-336–BR-337.

CREATE TYPE roster_status AS ENUM ('draft', 'submitted', 'locked', 'scored');
CREATE TYPE selection_role AS ENUM ('starting_xi', 'bench');

CREATE TABLE gameweek_rosters (
  gameweek_roster_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  fantasy_team_id    uuid NOT NULL REFERENCES fantasy_teams (fantasy_team_id),
  gameweek_id        uuid NOT NULL REFERENCES gameweeks (gameweek_id),
  status             roster_status NOT NULL DEFAULT 'draft',
  submitted_at       timestamptz,
  locked_at          timestamptz,
  captain_player_id  uuid REFERENCES players (player_id), -- BR-046, Invariant 5: must be one of this roster's players — checked at the application layer, not by a DB constraint (roster_players rows are written in the same transaction)
  is_carried_forward boolean NOT NULL DEFAULT false -- BR-305: true only when the ADR-012 deadline sweep — not a user Submit — performed the Draft → Submitted transition, by copying the FantasyTeam's last Locked roster
);
CREATE UNIQUE INDEX ux_gameweek_rosters_team_gameweek ON gameweek_rosters (fantasy_team_id, gameweek_id);
COMMENT ON TABLE gameweek_rosters IS
  'Architecture §8.3: optimistic concurrency uses PostgreSQL''s built-in xmin system column (exposed to EF Core/Npgsql via UseXminAsConcurrencyToken, per ADR-002) — no explicit row_version column is needed. BR-305: is_carried_forward suppresses trg_enforce_gameweek_roster_size below, since a carry-forward roster may legitimately lock with fewer than weekly_roster_size players (or empty, on a FantasyTeam''s first Gameweek).';

CREATE TABLE roster_players (
  gameweek_roster_id uuid NOT NULL REFERENCES gameweek_rosters (gameweek_roster_id),
  player_id          uuid NOT NULL REFERENCES players (player_id),
  is_captain         boolean NOT NULL DEFAULT false,
  selection_role     selection_role, -- BR-044: computed post-scoring; null before the Gameweek is Scored, not chosen by the user
  PRIMARY KEY (gameweek_roster_id, player_id)
);
-- BR-046, Invariant 5: at most one captain per roster.
CREATE UNIQUE INDEX ux_roster_players_one_captain ON roster_players (gameweek_roster_id) WHERE is_captain;

-- Architecture §8.1: "check constraint enforcing exactly 15 roster_players rows at Submitted+
-- status (enforced at application layer + a deferred trigger as defense-in-depth)". Implemented as
-- a constraint trigger rather than a plain CHECK because it must count sibling roster_players rows
-- (a cross-table invariant) and read the Season's *configured* roster size (BR-279/ADR-011) rather
-- than a hard-coded 15. Skipped entirely for a carry-forward roster (BR-305 edge cases).
CREATE OR REPLACE FUNCTION enforce_gameweek_roster_size() RETURNS trigger AS $$
DECLARE
  required_size integer;
  actual_size   integer;
BEGIN
  IF NEW.status IN ('submitted', 'locked', 'scored') AND NOT NEW.is_carried_forward THEN
    SELECT sc.weekly_roster_size INTO required_size
    FROM fantasy_teams ft
    JOIN season_configurations sc ON sc.season_id = ft.season_id
    WHERE ft.fantasy_team_id = NEW.fantasy_team_id;

    SELECT count(*) INTO actual_size
    FROM roster_players
    WHERE gameweek_roster_id = NEW.gameweek_roster_id;

    IF actual_size IS DISTINCT FROM required_size THEN
      RAISE EXCEPTION 'gameweek_roster % has % player(s), expected % (BR-279)',
        NEW.gameweek_roster_id, actual_size, required_size
        USING ERRCODE = 'check_violation';
    END IF;
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- DEFERRABLE INITIALLY DEFERRED: the application updates gameweek_rosters.status and inserts all
-- roster_players rows within one transaction; deferring the check to COMMIT lets it see the final,
-- fully-populated roster rather than firing mid-transaction against a partially written one.
CREATE CONSTRAINT TRIGGER trg_enforce_gameweek_roster_size
  AFTER INSERT OR UPDATE ON gameweek_rosters
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW
  EXECUTE FUNCTION enforce_gameweek_roster_size();
