-- Fantasy EPL League Manager — Database Migrations
-- V009: Competition context (Architecture §6.7).
--
-- Inputs: Architecture and Domain Model v1.15 §6.7, §8.1, ADR-008; BRD v1.17 BR-107–BR-135,
-- BR-215–BR-220, BR-299, BR-306.

CREATE TYPE match_result AS ENUM ('home_win', 'away_win', 'draw');

CREATE TABLE head_to_head_matches (
  match_id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  season_id            uuid NOT NULL REFERENCES seasons (season_id),
  gameweek_id          uuid NOT NULL REFERENCES gameweeks (gameweek_id),
  home_fantasy_team_id uuid NOT NULL REFERENCES fantasy_teams (fantasy_team_id),
  away_fantasy_team_id uuid NOT NULL REFERENCES fantasy_teams (fantasy_team_id),
  home_score           integer,
  away_score           integer,
  result               match_result,
  league_points_home   integer, -- from SeasonConfiguration.league_points at calculation time (BR-115–BR-117)
  league_points_away   integer,
  CHECK (home_fantasy_team_id <> away_fantasy_team_id)
);
CREATE UNIQUE INDEX ux_h2h_matches_unique ON head_to_head_matches (season_id, gameweek_id, home_fantasy_team_id, away_fantasy_team_id);
COMMENT ON TABLE head_to_head_matches IS
  'BR-306: a bye week (odd number of FantasyTeams in a Season) is represented by the absence of a row for that FantasyTeam/Gameweek — never a fabricated match — and is excluded from that FantasyTeam''s Played/Won/Drawn/Lost totals in league_standings.';

-- Read-optimized, recalculated aggregate (Architecture §6.7) — never directly user-editable. A row
-- is (re)written per Gameweek rather than only once per Season, so BR-217's point-in-time/historical
-- standings queries (?asOfGameweekId=) are served by a plain lookup instead of a recomputation.
CREATE TABLE league_standings (
  season_id               uuid NOT NULL REFERENCES seasons (season_id),
  fantasy_team_id          uuid NOT NULL REFERENCES fantasy_teams (fantasy_team_id),
  as_of_gameweek_id        uuid NOT NULL REFERENCES gameweeks (gameweek_id),
  league_points            integer NOT NULL,
  played                   integer NOT NULL,
  won                      integer NOT NULL,
  drawn                    integer NOT NULL,
  lost                     integer NOT NULL,
  fantasy_goals_for        integer NOT NULL,
  fantasy_goals_against    integer NOT NULL,
  fantasy_goal_difference  integer NOT NULL,
  captain_points_total     integer NOT NULL,
  "position"               integer NOT NULL, -- computed via the ADR-008 tie-break pipeline (an ordered, versioned IStandingsTieBreakRule sequence, not hard-coded branching)
  PRIMARY KEY (season_id, fantasy_team_id, as_of_gameweek_id)
);

CREATE TABLE season_goal_predictions (
  prediction_id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  season_id                  uuid NOT NULL REFERENCES seasons (season_id),
  fantasy_team_id            uuid NOT NULL REFERENCES fantasy_teams (fantasy_team_id),
  predicted_epl_goals        integer NOT NULL,
  submitted_at               timestamptz NOT NULL DEFAULT now(),
  locked_at                  timestamptz NOT NULL, -- = Season start under normal submission (BR-127/BR-128), or = submitted_at under the late-submission fallback (BR-299)
  final_actual_goals         integer,
  final_absolute_difference integer -- BR-131/BR-135: computed and stored once, at season end
);
CREATE UNIQUE INDEX ux_season_goal_predictions_team ON season_goal_predictions (season_id, fantasy_team_id);
COMMENT ON TABLE season_goal_predictions IS
  'BR-299: the Roster Management application service checks for a row here before accepting a FantasyTeam''s first GameweekRoster submission of the Season — a cross-context precondition enforced in the application layer, not a database FK from gameweek_rosters to this table.';
