-- Fantasy EPL League Manager — Database Migrations
-- V006: Draft Management context (Architecture §6.4).
--
-- Inputs: Architecture and Domain Model v1.15 §6.4, §8.1, ADR-012; BRD v1.17 BR-052–BR-069,
-- BR-191–BR-192, BR-198, BR-204–BR-208, BR-260–BR-264, BR-282, BR-287, BR-302, BR-307–BR-313,
-- BR-323–BR-325; AP-009, AP-010.

CREATE TYPE draft_type AS ENUM ('initial', 'secondary', 'replacement');
CREATE TYPE draft_status AS ENUM ('scheduled', 'in_progress', 'paused', 'completed');

CREATE TABLE drafts (
  draft_id                    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  season_id                   uuid NOT NULL REFERENCES seasons (season_id),
  draft_type                  draft_type NOT NULL,
  status                      draft_status NOT NULL DEFAULT 'scheduled',
  draft_order                 uuid[] NOT NULL DEFAULT '{}', -- FantasyTeamId sequence: randomized for Initial (BR-054), standings-derived for Secondary (BR-056); array membership is validated at the application layer, not FK-enforced
  current_round               integer NOT NULL DEFAULT 0,
  current_pick_index          integer NOT NULL DEFAULT 0,
  timer_seconds                integer NOT NULL, -- copied from SeasonConfiguration.draft_timer_seconds_by_type at Draft creation (BR-057)
  current_pick_deadline        timestamptz,
  standings_snapshot_taken_at   timestamptz, -- BR-136/BR-138: captured once for a Secondary Draft, immutable after
  pending_makeup_picks           uuid[] NOT NULL DEFAULT '{}' -- BR-282/BR-324: FantasyTeamIds queued for a makeup pick after the final round
);
CREATE INDEX ix_drafts_season ON drafts (season_id);
COMMENT ON TABLE drafts IS
  'BR-302: an Initial Draft may not transition Scheduled → InProgress with fewer than two FantasyTeams participating — enforced at the application layer against fantasy_teams, not by a CHECK here (participation is derived from squad_players/draft_selections, not a column on this row).';

CREATE TABLE draft_selections (
  draft_selection_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  draft_id           uuid NOT NULL REFERENCES drafts (draft_id),
  fantasy_team_id    uuid NOT NULL REFERENCES fantasy_teams (fantasy_team_id),
  player_id          uuid NOT NULL REFERENCES players (player_id),
  round              integer NOT NULL,
  pick_number        integer NOT NULL,
  selected_at        timestamptz NOT NULL DEFAULT now(),
  is_makeup_pick     boolean NOT NULL DEFAULT false -- BR-282
);
CREATE UNIQUE INDEX ux_draft_selections_round_pick ON draft_selections (draft_id, round, pick_number);
CREATE INDEX ix_draft_selections_fantasy_team ON draft_selections (fantasy_team_id);
COMMENT ON TABLE draft_selections IS
  'AP-009/Invariant (atomic pick): the application inserts this row and the corresponding squad_players row (V005) in one database transaction, guarded by ux_squad_players_owned — the loser of two concurrent picks for the same player receives a domain-level PlayerAlreadyOwnedException from that unique-index violation, not a raw constraint error.';

-- Physical-design addition: Architecture §6.3 describes PlayerMarkedReplacementEligible generating
-- "exactly one replacement-selection opportunity" for a FantasyTeam, but does not name a persisted
-- entity for it. This table is that entity, so an unspent opportunity (BR-307: never expires on its
-- own) is independently queryable ahead of the Replacement-type draft pick that spends it.
CREATE TABLE replacement_opportunities (
  replacement_opportunity_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  fantasy_team_id            uuid NOT NULL REFERENCES fantasy_teams (fantasy_team_id),
  source_player_id           uuid NOT NULL REFERENCES players (player_id), -- the player whose exit/injury generated this opportunity
  granted_at                 timestamptz NOT NULL DEFAULT now(),
  grant_reason               replacement_grant_reason NOT NULL, -- 'epl_exit' fires automatically off official data (BR-066/BR-308); 'season_ending_injury' is always Administrator-initiated (BR-067/BR-068/BR-308)
  spent_at                   timestamptz -- BR-307: null until spent via a Replacement-type draft pick; never auto-expires
);
CREATE INDEX ix_replacement_opportunities_team_unspent ON replacement_opportunities (fantasy_team_id) WHERE spent_at IS NULL;
COMMENT ON TABLE replacement_opportunities IS
  'BR-287: SeasonConfiguration.replacement_selection_cap (V004, league_configurations/season_configurations) is enforced at the application layer when a new opportunity would be granted — once a FantasyTeam''s unspent-plus-spent count reaches the cap, further eligibility events grant no additional row here.';
