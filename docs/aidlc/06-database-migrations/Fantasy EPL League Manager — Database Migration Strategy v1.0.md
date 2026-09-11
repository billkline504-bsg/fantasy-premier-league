# Fantasy EPL League Manager
## Database Migration Strategy — Version 1.0

**Document Status:** Baseline Database Migrations

**Version:** 1.0

**Inputs:**
- Architecture and Domain Model v1.15 (`Fantasy EPL League Manager — Architecture and Domain Model v1.15.md`) — §6 (aggregate-level domain model), §8 (logical data architecture, key tables/constraints, historical data strategy, concurrency strategy), ADR-002 (.NET/EF Core/Npgsql), ADR-003 (PostgreSQL), ADR-008 (tie-break ruleset as configuration), ADR-010 (soft delete / append-only for competitive entities), ADR-011 (configuration as a first-class value object), ADR-012 (deadline-sweep architecture).
- Business Requirements Document v1.17 (`Fantasy EPL League Manager — Business Requirements Document v1.17.md`) — authoritative business rules, cited throughout by `BR-###`.
- OpenAPI Specification v1.0 (`../05-api-specification/Fantasy EPL League Manager — OpenAPI Specification v1.0.yaml`) — cross-checked so every schema this API returns has a physical column to back it.

**Purpose:** Turn Architecture §8's logical data design into physical, runnable PostgreSQL DDL — the artifact Architecture §16 lists as recommended-next-artifact #4. Every table, type, index, and constraint below is checked against a real PostgreSQL 16 instance (see "Validation," below), not just written to look plausible.

**Audience:** Backend developers wiring up `EplFantasy.Infrastructure`'s EF Core `DbContext`, whoever owns the deployment pipeline's migration step, and the next AIDLC artifacts that depend on a concrete schema (testing strategy, implementation task breakdown).

**Scope:** Physical PostgreSQL 16 DDL for every bounded context in Architecture §5/§6 — Identity & User, League & Season, Fantasy Team, Player & EPL Data, Draft Management, Roster Management, Scoring, Competition, Corrections & Administration, Notifications. Reporting & History is out of scope for its own migration file because Architecture §5 defines it as read-model projections only, with no aggregate roots of its own (`player_season_statistics`, the one such projection needed so far, is created in `V008__scoring.sql` next to the table it's read from).

---

## 1. How this artifact is organized

Unlike the prior AIDLC artifacts (each a single append-only Markdown/YAML file), a database migration is inherently a *sequence* of files — each one immutable once applied, matching this project's existing "never edit a published version" discipline even more literally than Markdown versioning does. This document is the versioned companion describing the *why*; the actual DDL lives in `migrations/`, one file per bounded context, applied strictly in order:

| File | Bounded Context (Architecture §5/§6) | Depends on |
|---|---|---|
| `V001__extensions_and_roles.sql` | — (platform) | — |
| `V002__identity.sql` | Identity & User (§6.1) | V001 |
| `V003__player_reference_data.sql` | Player & EPL Data (§6.10) | V001 |
| `V004__league_and_season.sql` | League & Season (§6.2) | V002, V003 |
| `V005__fantasy_team.sql` | Fantasy Team (§6.3) | V003, V004 |
| `V006__draft.sql` | Draft Management (§6.4) | V003, V004, V005 |
| `V007__roster.sql` | Roster Management (§6.5) | V003, V004, V005 |
| `V008__scoring.sql` | Scoring (§6.6) | V003, V004, V005 |
| `V009__competition.sql` | Competition (§6.7) | V003, V004, V005 |
| `V010__administration.sql` | Corrections & Administration (§6.8) | V004 |
| `V011__notifications.sql` | Notifications (§6.9) | V002, V004 |
| `V012__seed_reference_data.sql` | — (seed data) | V002 |
| `V013__grants.sql` | — (platform) | all of the above |

**Naming convention:** `V{NNN}__{description}.sql`, the naming scheme used by common migration runners (Flyway, DbUp) — each file is a permanent, immutable step; a later change is always a *new* `V{NNN+1}` file, never an edit to a published one, exactly like every other artifact in this pipeline (`CONTRIBUTING.md`'s "never edit a published version" rule). There are no corresponding "down" scripts: this project's forward-only versioning discipline (append, don't rewrite history) extends naturally to forward-only migrations — a mistake is corrected by a new migration, not a rollback script.

### Why the file order differs from Architecture §5's module-map order

Architecture §5 lists bounded contexts in this order: Identity & User, League & Season, Fantasy Team, **Player & EPL Data**, Draft, Roster, Scoring, Competition, Administration, Notifications, Reporting. That table documents *application-layer* dependency direction (which module's application services may call which). It is not a valid database foreign-key creation order: `SquadPlayer.PlayerId`, `DraftSelection.PlayerId`, `RosterPlayer.PlayerId`, and several others all reference `players`, which belongs to Player & EPL Data — a context the module-map table lists *after* League & Season and Fantasy Team. Player, Club, Fixture, and Gameweek are pure reference data with no inbound foreign keys to anything League-scoped, so physically they must be created before any table that references them. `V003__player_reference_data.sql` therefore runs right after Identity, ahead of League & Season — the only place this migration's ordering diverges from the module map, and it diverges for a concrete, checkable reason (a `REFERENCES` clause), not a stylistic one.

### EF Core's own migrations

ADR-002 selects EF Core as the ORM. This SQL is the authoritative physical design — the schema EF Core's own `Migrations/` folder (generated later, during implementation) must reconcile against, not a replacement for it. Keeping the design in reviewable raw SQL first, checked against a real Postgres instance before any C# exists, is what makes the schema decisions in this document (see §3) reviewable independently of EF Core's model-diffing behavior.

---

## 2. Validation

Every migration file in `migrations/` was applied, in order, to a disposable PostgreSQL 16 container (`postgres:16` via Docker) and confirmed to run without error. Beyond "does it parse," three invariants central to the domain model were exercised with real inserts against the resulting schema, in one transaction each:

1. **The circular `leagues` ↔ `league_memberships` foreign key** (BR-024: a League's creator becomes its founding Administrator in the same operation that creates the League) — inserting a `leagues` row and its founding `league_memberships` row, each referencing the other's already-generated id, succeeds inside one transaction because `fk_leagues_created_by_membership` is `DEFERRABLE INITIALLY DEFERRED` (checked at `COMMIT`, not per-statement).
2. **The Gameweek roster-size trigger** (`trg_enforce_gameweek_roster_size`, BR-279) — submitting a roster with only 1 of the required 15 players is rejected with a `check_violation`; the identical under-sized submission with `is_carried_forward = true` is accepted, confirming the BR-305 carry-forward exemption actually takes effect rather than being dead code.
3. **The squad-ownership partial unique index** (`ux_squad_players_owned`, BR-035/BR-191, Invariant 1) — giving the same Player to a second FantasyTeam in the same Season is rejected with a `unique_violation`, the exact mechanism AP-009/AP-010's atomic-draft-pick guarantee depends on.
4. **`administrative_actions` role-level immutability** (BR-149, ADR-010) — `information_schema.role_table_grants` was queried after `V013__grants.sql` to confirm the `eplfantasy_app` role holds only `INSERT`/`SELECT` on that table, with no `UPDATE`/`DELETE` grant at all — enforced by the database, not merely by application convention.

No CI/build pipeline exists yet to re-run this automatically (that arrives with the "Implementation task breakdown" artifact, Architecture §16 item 7); until then, re-run the check manually with a disposable container before trusting a new migration file:

```bash
docker run --rm -d --name eplfantasy-migration-check -e POSTGRES_PASSWORD=test -p 55432:5432 postgres:16
# then, in order, for each V*.sql file:
docker exec -i eplfantasy-migration-check psql -U postgres -v ON_ERROR_STOP=1 -f - < migrations/V001__extensions_and_roles.sql
# ...
docker rm -f eplfantasy-migration-check
```

---

## 3. Design decisions and physical-design additions

Architecture §8.1 explicitly says its key-tables table is "non-exhaustive — full DDL is a follow-on artifact." Turning it into runnable DDL required a handful of concrete choices Architecture left open, and, in a few places, closing small modeling gaps the same way Architecture's own version history repeatedly closed gaps surfaced by a downstream artifact (e.g., its Version 1.5 adding the `LeagueMessage` aggregate). Each is called out here, and again as a `COMMENT ON TABLE`/`COMMENT ON COLUMN` at its point of use in the SQL itself, so a reader of the schema alone (not just this document) sees the same rationale.

| Decision | Rationale |
|---|---|
| **Native PostgreSQL `ENUM` types**, one per BR-defined closed set (`user_status`, `draft_status`, `admin_action_type`, etc.), rather than `text` + `CHECK` | Matches each field's closed value set 1:1 with the OpenAPI Specification v1.0's own enum schemas; Npgsql (ADR-002) maps native PG enums directly. A `CHECK`-constrained `text` column was the alternative but adds a redundant constraint for no benefit once EF Core owns the mapping. |
| **`epl_seasons` reference table** (`V003`) | Architecture §6.10/§9.2 treats `GET /api/v1/epl/seasons/{eplSeasonId}/...` as a platform-level, non-League-scoped resource, but never names a table for the identifier itself — only `ClubStanding.SeasonId` and each League's own `Season.EplSeasonIdentifier` reference it informally. `epl_seasons` is that anchor, decoupled from the League-scoped `seasons` table, since many Leagues' Seasons can point at the same real-world EPL season. |
| **`replacement_opportunities` table** (`V006`) | Architecture §6.3 describes `PlayerMarkedReplacementEligible` generating "exactly one replacement-selection opportunity" per FantasyTeam but names no persisted entity for it. Without one, BR-307's "never expires on its own, stays usable until spent" behavior and F-006.4/F-011.2's need to list a FantasyTeam's unspent opportunities have nothing to query. |
| **`refresh_tokens` and `password_reset_tokens` tables** (`V002`) | F-001.1/F-001.2 (BR-159 rotating refresh tokens, BR-284 time-limited single-use reset links) clearly require persisted state; Architecture §9.1/§11 describes the *behavior* but, being scoped to architecture rather than physical design, names no entities. Both store only a hash of the token value, consistent with BR-171 (no secrets in logs) applied to storage as well. |
| **Gameweek roster-size enforcement as a `DEFERRABLE` constraint trigger, not a plain `CHECK`** (`V007`) | Architecture §8.1 literally calls for "a check constraint enforcing exactly 15 `roster_players` rows at `Submitted`+ status (enforced at application layer + a deferred trigger as defense-in-depth)." A plain `CHECK` cannot count sibling rows in another table or read a Season's *configured* roster size (BR-279 is enforced against `SeasonConfiguration.WeeklyRosterSize`, not a hard-coded 15, per ADR-011) — hence a `plpgsql` constraint trigger, deferred to `COMMIT` so it sees the fully-populated roster rather than firing mid-transaction. It explicitly exempts `is_carried_forward = true` rosters (BR-305's edge case, where a carry-forward roster may legitimately lock with fewer players, or none). |
| **Optimistic concurrency via PostgreSQL's built-in `xmin`, no explicit `row_version` column** | Architecture §8.3 calls for "a `row_version`/`xmin`-based check" on aggregates like `GameweekRoster`. Npgsql (ADR-002) supports mapping `xmin` directly as an EF Core concurrency token, so no redundant application-maintained column is needed for it. |
| **`administrative_actions` UPDATE/DELETE revoked at the role level** (`V013`) | The one table Architecture §8.1 explicitly calls out for database-role-level (not just application-convention) immutability: "no update/delete grants at the database role level (ADR-010-style immutability, BR-149)." No other "historical" table (`gameweek_scores`, `head_to_head_matches`, etc.) gets this treatment, because Architecture §8.2 only asks that they not be mutated *after finalization* — a business-process rule enforced by the application only ever creating new override/audit rows, not a literal database-role restriction the way `administrative_actions` gets. Extending role-level revocation to those tables would be over-scoping past what Architecture actually specifies. |
| **`eplfantasy_app` is a `NOLOGIN` group role**, not a role with an embedded password | BR-173 ("secrets... never committed to source control"). An ops-provisioned login role is granted membership in `eplfantasy_app` at deploy time, entirely outside this migration; the migrations never contain a credential. |
| **One flat column per configurable parameter on `league_configurations`/`season_configurations`**, not a single `jsonb` blob | ADR-011 / Architecture §8.1 literally says "one column per configurable parameter." Flat, typed columns also make BR-293's per-field lock tracking (`locked_fields text[]`) meaningful — a `jsonb` blob would make "is this one field locked" an awkward path query instead of a plain column check. |
| **`player_season_statistics` as a plain `VIEW`, not a `MATERIALIZED VIEW`** | Architecture §6.6 is explicit that this read-model is "recomputed... not a separately persisted aggregate with its own invariants." A plain view is the simplest correct v1.0; promoting it to a materialized view refreshed by the official-data sync job is a pure performance optimization to make later, not a v1.0 requirement. |

---

## 4. Traceability

Every table and every nontrivial constraint carries a `COMMENT ON TABLE`/`COMMENT ON COLUMN` citing the `BR-###` rule(s) and/or Architecture section it implements, directly in the SQL — so `\d+ <table>` (or any schema-introspection tool) surfaces the same rationale a reader of this document gets, without needing both open side by side. This mirrors the OpenAPI Specification v1.0's use of `description` fields for the same purpose.

---

## Version History

### Version 1.0

Initial physical PostgreSQL migration set, generated from Architecture and Domain Model v1.15 §6/§8 and cross-checked against OpenAPI Specification v1.0. Covers every bounded context except Reporting & History (read-model projections only, no dedicated migration). Validated end-to-end against a disposable PostgreSQL 16 container, including the circular League/LeagueMembership foreign key, the Gameweek roster-size trigger (both its rejection and carry-forward-exemption paths), the squad-ownership partial unique index, and `administrative_actions`'s role-level immutability grant (§2). Four modeling gaps not named as entities in Architecture v1.15 were closed with physical-design additions, documented in §3: `epl_seasons`, `replacement_opportunities`, `refresh_tokens`, `password_reset_tokens`.
