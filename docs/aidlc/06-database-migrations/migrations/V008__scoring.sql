-- Fantasy EPL League Manager — Database Migrations
-- V008: Scoring context (Architecture §6.6).
--
-- Inputs: Architecture and Domain Model v1.15 §6.6, §8.1; BRD v1.17 BR-044, BR-047, BR-073–BR-091,
-- BR-139–BR-149, BR-234, BR-303–BR-304, BR-313.

CREATE TABLE player_performances (
  player_performance_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  gameweek_id           uuid NOT NULL REFERENCES gameweeks (gameweek_id),
  player_id             uuid NOT NULL REFERENCES players (player_id),
  minutes_played        integer NOT NULL DEFAULT 0, -- BR-313
  fantasy_points        integer NOT NULL DEFAULT 0,
  goals                 integer NOT NULL DEFAULT 0,
  goals_conceded        integer NOT NULL DEFAULT 0,
  own_goals             integer NOT NULL DEFAULT 0,
  source                performance_source NOT NULL,
  is_official           boolean NOT NULL DEFAULT true,
  retrieved_at          timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_player_performances_gameweek_player ON player_performances (gameweek_id, player_id);
COMMENT ON TABLE player_performances IS
  'Kept separate from gameweek_scores (below) so raw official data is never overwritten by derived/competition calculations (Domain Spec §21, "Recoverability"). BR-234: a corrected historical value is reconciled by the sync job comparing against retrieved_at, not by blind-overwriting this row.';

-- Read-model, per Architecture §6.6: recomputed from player_performances, not a separately
-- persisted aggregate with its own invariants. Before a Season's first Gameweek is scored, no rows
-- exist yet, so a LEFT JOIN from squad_players/draft player-pool queries resolves all three figures
-- to zero rather than omitting the player — the application is responsible for that COALESCE, since
-- this view naturally has no row for a not-yet-scored player.
CREATE VIEW player_season_statistics AS
SELECT
  g.epl_season_identifier,
  pp.player_id,
  sum(pp.minutes_played)                                  AS minutes_played,
  count(*) FILTER (WHERE pp.minutes_played > 0)            AS games_played,
  sum(pp.fantasy_points)                                   AS fantasy_points,
  -- uuid has no natural ordering/MAX(); pick the gameweek_id belonging to the highest Gameweek
  -- number instead, via an ORDER BY'd array_agg (Postgres has no argmax()/last() aggregate either).
  (array_agg(pp.gameweek_id ORDER BY g.number DESC))[1]    AS as_of_gameweek_id
FROM player_performances pp
JOIN gameweeks g ON g.gameweek_id = pp.gameweek_id
GROUP BY g.epl_season_identifier, pp.player_id;
COMMENT ON VIEW player_season_statistics IS
  'BR-313: season-to-date Minutes Played/Games Played/Fantasy Points per Player, backing the Draft Player Pool (F-005.5) and Squad View (F-007.5) statistics columns. A plain view for v1.0; promote to a MATERIALIZED VIEW refreshed by the official-data sync job (V003/F-004.3) if read latency becomes a concern at scale.';

CREATE TABLE gameweek_scores (
  gameweek_score_id     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  fantasy_team_id       uuid NOT NULL REFERENCES fantasy_teams (fantasy_team_id),
  gameweek_id           uuid NOT NULL REFERENCES gameweeks (gameweek_id),
  fantasy_points        integer NOT NULL, -- sum over StartingXI + captain multiplier (BR-044/BR-047), captain competes on the multiplied value (BR-304)
  captain_points        integer NOT NULL,
  fantasy_goals_for     integer NOT NULL, -- BR-080
  fantasy_goals_against integer NOT NULL, -- BR-086/BR-087: truncated integer average, not rounded
  fantasy_goal_difference integer NOT NULL, -- BR-089
  calculated_at         timestamptz NOT NULL DEFAULT now(),
  recalculated_count    integer NOT NULL DEFAULT 0 -- BR-148: incremented on every override-driven recompute
);
CREATE UNIQUE INDEX ux_gameweek_scores_team_gameweek ON gameweek_scores (fantasy_team_id, gameweek_id);

CREATE TABLE score_overrides (
  score_override_id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  player_performance_id       uuid NOT NULL REFERENCES player_performances (player_performance_id),
  administrator_membership_id uuid NOT NULL REFERENCES league_memberships (league_membership_id),
  original_value               jsonb NOT NULL, -- snapshot of the overridden field(s)
  override_value               jsonb NOT NULL,
  reason                       text,
  created_at                   timestamptz NOT NULL DEFAULT now(),
  undone_at                    timestamptz
);
CREATE INDEX ix_score_overrides_performance_active ON score_overrides (player_performance_id) WHERE undone_at IS NULL;
COMMENT ON TABLE score_overrides IS
  'Invariant 12 (BR-140–BR-145): precedence for any read of a player statistic is Active ScoreOverride (undone_at IS NULL) > Official PlayerPerformance > Application Calculation, resolved through a single IAuthoritativeValueResolver at the application layer (Architecture §6.6) — never inlined per call site, so precedence cannot drift between modules.';
