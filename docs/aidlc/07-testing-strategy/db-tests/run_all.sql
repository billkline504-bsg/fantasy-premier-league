-- Runs every database invariant test file in this folder, in order, against a database that
-- already has migrations V001–V013 applied, then prints a pass/fail summary (and raises an
-- exception if anything failed, for CI use). Invoke with psql's -f flag from within this
-- directory (or via \i's paths, which are resolved relative to the invoking psql's cwd).

\i 000_test_harness.sql
\i 005_test_fixtures.sql
\i 010_identity_invariants.sql
\i 020_league_and_season_invariants.sql
\i 030_fantasy_team_and_squad_invariants.sql
\i 040_draft_invariants.sql
\i 050_roster_invariants.sql
\i 060_scoring_invariants.sql
\i 070_competition_invariants.sql
\i 080_administration_invariants.sql
\i 090_notifications_invariants.sql

SELECT test_summary();
