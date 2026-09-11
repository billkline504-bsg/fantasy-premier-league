#!/usr/bin/env bash
# AP-009/AP-010 concurrency test: races two REAL, simultaneous database connections attempting to
# acquire the same Player for two different FantasyTeams in the same Season, and asserts that
# exactly one succeeds and the other is rejected by ux_squad_players_owned — not merely that two
# sequential statements in one session behave correctly (that weaker check lives in
# 030_fantasy_team_and_squad_invariants.sql). Requires 000_test_harness.sql and
# 005_test_fixtures.sql to already be loaded in the target database (run_all.sql does this).
#
# Usage: run_concurrency_test.sh <container_name> <database_name>

set -euo pipefail
CONTAINER="${1:?container name required}"
DB="${2:?database name required}"

psql() { docker exec -i "$CONTAINER" psql -U postgres -d "$DB" -v ON_ERROR_STOP=1 "$@"; }

echo "=== AP-009/AP-010 concurrency test: racing two connections for one Player ==="

# Build one fresh fixture and stage its ids in a real (non-temp) table so the two racer
# connections below — each its own new backend — can read the same ids.
# Unique per invocation so re-running this script against an already-used (non-disposable) dev
# database doesn't collide with a previous run's fixture rows (username/epl_*_id are real-value
# unique keys, not surrogate ids).
SUFFIX="concurrency_$(date +%s%N)"

psql -v suffix="$SUFFIX" <<'SQL'
DROP TABLE IF EXISTS dbtest_concurrency_fixture;
-- NOTE: "SELECT (f()).*" is a well-known PostgreSQL pitfall — it re-invokes f() once per
-- expanded column instead of once total. Calling the function in the FROM clause instead
-- (any function is usable as a one-row set there) evaluates it exactly once.
CREATE TABLE dbtest_concurrency_fixture AS
SELECT * FROM dbtest_create_baseline_fixture(:'suffix');
SQL

# Two racer transactions: each opens, sleeps briefly to maximize overlap with the other, then
# attempts to insert a squad_players row for the SAME player_id/season_id under a DIFFERENT
# fantasy_team_id. Launched into the background nearly simultaneously from the shell.
race_insert() {
  local team_col="$1"
  local out_file="$2"
  psql <<SQL > "$out_file" 2>&1
BEGIN;
SELECT pg_sleep(0.5);
INSERT INTO squad_players (fantasy_team_id, player_id, season_id, acquisition_type)
SELECT ${team_col}, player_id, season_id, 'initial_draft' FROM dbtest_concurrency_fixture;
COMMIT;
SQL
}

OUT_A=$(mktemp)
OUT_B=$(mktemp)
race_insert "fantasy_team_id"   "$OUT_A" &
PID_A=$!
race_insert "fantasy_team_id_2" "$OUT_B" &
PID_B=$!
wait "$PID_A" "$PID_B" || true

SUCCESSES=0
FAILURES=0
for f in "$OUT_A" "$OUT_B"; do
  if grep -q "^INSERT 0 1$" "$f"; then
    SUCCESSES=$((SUCCESSES + 1))
  elif grep -qi "duplicate key value violates unique constraint \"ux_squad_players_owned\"" "$f"; then
    FAILURES=$((FAILURES + 1))
  else
    echo "!!! UNEXPECTED output, neither a clean success nor the expected unique_violation:"
    cat "$f"
    exit 1
  fi
done

ROW_COUNT=$(psql -Atc "SELECT count(*) FROM squad_players sp JOIN dbtest_concurrency_fixture f ON f.player_id = sp.player_id AND f.season_id = sp.season_id;")

echo "Racer A output:"; cat "$OUT_A"
echo "Racer B output:"; cat "$OUT_B"
echo "successes=$SUCCESSES failures=$FAILURES persisted_squad_player_rows=$ROW_COUNT"

if [ "$SUCCESSES" -eq 1 ] && [ "$FAILURES" -eq 1 ] && [ "$ROW_COUNT" -eq 1 ]; then
  echo "PASS: concurrency.exactly_one_of_two_racing_picks_succeeds"
  rm -f "$OUT_A" "$OUT_B"
  exit 0
else
  echo "FAIL: concurrency.exactly_one_of_two_racing_picks_succeeds (expected 1 success, 1 failure, 1 persisted row)"
  rm -f "$OUT_A" "$OUT_B"
  exit 1
fi
