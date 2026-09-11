-- Fantasy EPL League Manager — Database Migrations
-- V005: Fantasy Team context (Architecture §6.3).
--
-- Inputs: Architecture and Domain Model v1.15 §6.3, §8.1; BRD v1.17 BR-018–BR-020, BR-035–BR-036,
-- BR-063–BR-068, BR-191, BR-193, BR-260–BR-264, BR-287, BR-307–BR-308.

CREATE TYPE fantasy_team_status AS ENUM ('active', 'withdrawn');
CREATE TYPE acquisition_type AS ENUM ('initial_draft', 'secondary_draft', 'replacement');
CREATE TYPE replacement_grant_reason AS ENUM ('epl_exit', 'season_ending_injury');

CREATE TABLE fantasy_teams (
  fantasy_team_id      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  league_membership_id uuid NOT NULL REFERENCES league_memberships (league_membership_id),
  season_id            uuid NOT NULL REFERENCES seasons (season_id),
  status               fantasy_team_status NOT NULL DEFAULT 'active',
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now()
);
-- BR-019/BR-193, Invariant 2: one FantasyTeam per (LeagueMembership, Season).
CREATE UNIQUE INDEX ux_fantasy_teams_membership_season ON fantasy_teams (league_membership_id, season_id);

CREATE TABLE squad_players (
  squad_player_id       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  fantasy_team_id       uuid NOT NULL REFERENCES fantasy_teams (fantasy_team_id),
  player_id             uuid NOT NULL REFERENCES players (player_id),
  season_id             uuid NOT NULL REFERENCES seasons (season_id), -- denormalized from fantasy_team_id for indexability (Architecture §6.3)
  acquisition_type      acquisition_type NOT NULL, -- BR-264
  acquired_at           timestamptz NOT NULL DEFAULT now(),
  released_at           timestamptz,
  is_currently_owned    boolean NOT NULL DEFAULT true,
  replacement_eligible_at timestamptz -- set when eligibility is granted (BR-065); see replacement_opportunities (V006) for the spendable opportunity itself
);
-- BR-035/BR-191/BR-262, Invariant 1: a Player may have at most one currently-owned SquadPlayer
-- per (League, Season) — this is the constraint AP-009/AP-010's atomic draft-pick transaction relies on.
CREATE UNIQUE INDEX ux_squad_players_owned ON squad_players (player_id, season_id) WHERE is_currently_owned;
CREATE INDEX ix_squad_players_fantasy_team ON squad_players (fantasy_team_id);
COMMENT ON TABLE squad_players IS
  'BR-287/BR-307: replacement_selection opportunities are tracked independently in replacement_opportunities (V006), not as a count on this table, since an opportunity can exist and remain unspent before any corresponding SquadPlayer row is created.';
