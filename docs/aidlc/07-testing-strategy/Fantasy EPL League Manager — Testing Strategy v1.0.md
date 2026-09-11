# Fantasy EPL League Manager
## Testing Strategy — Version 1.0

**Document Status:** Baseline Testing Strategy

**Version:** 1.0

**Inputs:**
- Business Requirements Document v1.17 (`Fantasy EPL League Manager — Business Requirements Document v1.17.md`) — §47 "Testing Requirements" (`BR-243`–`BR-254`), the authoritative list of required coverage categories this strategy operationalizes, and §51 "Architecture Principles" (`AP-001`–`AP-010`).
- Architecture and Domain Model v1.15 (`Fantasy EPL League Manager — Architecture and Domain Model v1.15.md`) — §13 "Testing Architecture Implications" (the seams this strategy is built against), §14 (the `tests/` solution structure this strategy fills in), §6 (aggregate invariants/state machines), §8.3 (concurrency strategy).
- OpenAPI Specification v1.0 (`../05-api-specification/...yaml`) — the contract-level test surface (request/response schemas, error shapes, idempotency headers).
- Database Migration Strategy v1.0 (`../06-database-migrations/...md`) — the physical schema this strategy's database-invariant tests run against, and the source of the `db-tests/` suite this document formalizes.

**Purpose:** Turn Architecture §16 item 6 — "Testing strategy operationalizing BR-243–BR-254 against the seams identified in Section 13" — into a concrete plan: which BR maps to which test type, at which layer, exercising which architectural seam, plus a genuinely runnable first slice (the database-invariant layer) proven against a real PostgreSQL instance rather than only described.

**Audience:** Backend developers implementing `EplFantasy.UnitTests`/`IntegrationTests`/`ApiTests` (Architecture §14), whoever sets up the CI pipeline, and the final AIDLC artifact (implementation task breakdown) that sequences the work this strategy describes.

**Scope:** Every BR-243–BR-254 coverage category and every AP-001–AP-010 principle. Non-functional testing (load/performance, chaos/failure-injection) is intentionally out of scope for v1.0 — BR-243–BR-254 do not ask for it, and Architecture names no seam for it; add it as a new artifact or a new version of this one if a future requirement calls for it, rather than scope-creeping this document past what BRD/Architecture actually specify.

---

## 1. Test Pyramid and Tooling

Architecture §14 already names the test project layout; this section pins down what runs in each and why, and is the first place a new BR-###/AP-### test lands.

| Layer | Project (Architecture §14) | Tooling | What it proves | Runs against |
|---|---|---|---|---|
| **Database invariant** | *(new — not named in Architecture §14, since it predates any C# code; see §5 below)* | Plain PL/pgSQL assertion scripts, no test framework/extension dependency | A constraint, partial unique index, trigger, or role grant behaves exactly as Architecture §8/ADR-010 specifies — before any application code exists to test | A disposable PostgreSQL 16 container (Docker), post-migration |
| **Unit** | `EplFantasy.UnitTests` | xUnit + FluentAssertions (or equivalent) | An aggregate's own invariant-enforcing methods (e.g. `GameweekRoster.Submit(...)`) reject/accept correctly, in-memory, no I/O | Nothing — pure domain objects, per Architecture §13's "aggregates expose behavior through methods that enforce their own invariants" |
| **Integration** | `EplFantasy.IntegrationTests` | xUnit + Testcontainers.PostgreSql + the real EF Core `DbContext` | A repository/application service round-trips correctly through the *real* schema (V001–V013) — the layer where the `db-tests/` invariant scripts' C# equivalents live once EF Core exists | A disposable PostgreSQL 16 container per test run (Testcontainers), same image family as `db-tests/` |
| **API** | `EplFantasy.ApiTests` | xUnit + `WebApplicationFactory<Program>` + the OpenAPI Specification v1.0 as a schema-validation oracle | An HTTP request through the full ASP.NET Core pipeline (authn/authz middleware, controllers, application services, the real database) produces the response shape, status code, and `errorCode` the OpenAPI spec promises | A disposable PostgreSQL 16 container + an in-process test server |

**Why a database-invariant layer exists at all, ahead of Unit/Integration/API:** every other layer needs C# application code that does not exist yet (this repository is "documentation- and mockup-only," per the top-level `README.md`). The physical schema (`06-database-migrations/`) *does* exist and is independently testable today — several of BR-243–BR-254's requirements (uniqueness, one-per-X, role-level immutability) are enforced at the database layer by design (Architecture §8.1/§8.3, ADR-010), not solely in application code, so they can and should be tested there directly rather than waiting for a C# `DbContext` to exist. See §5.

---

## 2. BR-243–BR-254 → Test Location Map

Each BRD bullet is expanded into one or more concrete test-case names below, tagged with the layer it belongs in (§1) and, where relevant, the architectural seam it exercises (Architecture §13) or the `db-tests/` file that already proves the database-level half of it.

### BR-243 — User Tests

| Scenario | Layer | Seam / Existing proof |
|---|---|---|
| Username uniqueness (active users only, case-insensitive) | DB invariant, Unit | `db-tests/010_identity_invariants.sql`: `identity.username_unique_among_active` |
| Username reusable immediately after retirement (BR-298) | DB invariant, Unit | `010_identity_invariants.sql`: `identity.username_reusable_after_retirement` |
| Username change opens/closes `UsernameHistory` correctly (BR-326) | DB invariant, Unit | `010_identity_invariants.sql`: `identity.username_history_one_open_row`, `identity.username_history_rollover_after_close` |
| Profile creation seeds a `UserProfile` with the default `ProfileIcon` | Integration | `UserRegistered` handler creates both rows in one transaction |
| Default icon selection (BR-006) | Unit | `UserProfile.SetDefaultIcon(iconId)` rejects an inactive/unknown `ProfileIconId` (BR-011) |
| League-specific icon selection, independent per League (BR-007–BR-009) | Unit, Integration | Changing `LeagueMembership.LeagueIconId` for League A must not affect League B's row |
| Icon fallback when no override exists (BR-008) | Unit | Read path resolves `LeagueIconId ?? UserProfile.DefaultIconId` |
| Retired user handling (historical display, BR-014/BR-175) | Integration | A retired `User`'s prior competitive rows still resolve a display name via `UsernameHistory`, never a broken/blank reference |

### BR-244 — League Tests

| Scenario | Layer | Seam / Existing proof |
|---|---|---|
| League creation, creator becomes Administrator (BR-023/BR-024) | DB invariant, Integration | `db-tests/020_league_and_season_invariants.sql`: `league.circular_fk_deferred_to_commit` |
| Membership (join, one active row per League, BR-020) | DB invariant, Integration | `020_league_and_season_invariants.sql`: `league.one_active_membership_per_user`, `league.rejoin_after_leaving_allowed` |
| Invitations (issue, accept, token single-use) | DB invariant, Integration | `020_league_and_season_invariants.sql`: `league.invitation_token_unique` |
| Invitation expiration (BR-029, 7 days default/configurable) | Unit | Given a fixed/injected clock, an `Invitation` past `ExpiresAt` is rejected by `AcceptInvitation` |
| Multiple leagues (BR-030/BR-031) | Integration | One `User` with two active `LeagueMembership` rows; the active-League-context switch (F-002.4) selects correctly |
| League authorization (only the Administrator manages members/messages/config) | API, Unit | See BR-254 below — authorization is tested as its own cross-cutting category, not duplicated per feature |

### BR-245 — FantasyTeam Tests

| Scenario | Layer | Seam / Existing proof |
|---|---|---|
| LeagueMembership/FantasyTeam relationship | Integration | A `FantasyTeam` cannot be created without an active `LeagueMembership` (FK) |
| One FantasyTeam per LeagueMembership/Season (BR-019/BR-193, Invariant 2) | DB invariant, Unit | `db-tests/030_fantasy_team_and_squad_invariants.sql`: `fantasy_team.one_per_membership_season` |
| Multiple leagues for one User | Integration | Two `FantasyTeam` rows for the same `User`, different Leagues, do not interfere |
| Multiple seasons | Integration | The same `LeagueMembership` gets a new `FantasyTeam` each Season (BR-032/BR-033); a prior Season's `FantasyTeam`/`Squad` remain untouched |
| Squad isolation (a Player owned in Season A is independent of Season B) | DB invariant, Unit | `030_fantasy_team_and_squad_invariants.sql`: `fantasy_team.squad_ownership_scoped_per_season` |

### BR-246 — Draft Tests

| Scenario | Layer | Seam / Existing proof |
|---|---|---|
| Random initial order (BR-054) | Unit | Given an injected RNG/seed, `Draft` order is a permutation of all participating FantasyTeamIds |
| Snake draft pick sequencing | Unit | Round *N* reverses round *N-1*'s order; `CurrentPickIndex`/`CurrentRound` advance correctly across a full draft |
| Ownership restrictions (BR-035/BR-191, AP-009) | DB invariant, Unit, Integration (concurrency) | `db-tests/030_fantasy_team_and_squad_invariants.sql`: `fantasy_team.squad_ownership_unique_per_season`; **live-concurrency proof:** `db-tests/run_concurrency_test.sh` |
| 25-player initial squad (BR-034, configurable via `SeasonConfiguration.InitialSquadSize`) | Unit | `Draft.Complete()` requires every FantasyTeam to have made exactly `InitialSquadSize` selections |
| Five-player secondary draft (BR-060, configurable) | Unit | Same shape as above, against `SecondaryDraftSelectionsPerTeam` |
| Standings-based secondary order (BR-056, BR-136–BR-138) | Integration | Secondary `Draft.DraftOrder` matches the Standings snapshot taken at `StandingsSnapshotTakenAt`, immutable after |
| Five-minute timer (BR-057, configurable) | Unit | `Draft.TimerSeconds` is read from `SeasonConfiguration.DraftTimerSecondsByType`, never a literal (ADR-011) |
| Timer extension (BR-058, Administrator only) | Unit, API (authorization) | A non-Administrator's extension attempt is rejected (BR-254) |
| Draft persistence (round/pick uniqueness) | DB invariant | `db-tests/040_draft_invariants.sql`: `draft.round_pick_unique` |
| Pick timeout / makeup-pick requeue (BR-282, BR-323–BR-325) | Unit | The ADR-012 sweep skips an expired pick, enqueues it onto `PendingMakeupPicks`, and raises `DraftPickSkipped` with the correct `IsRequeue` flag |
| Replacement opportunity never auto-expires (BR-307) | DB invariant | `db-tests/040_draft_invariants.sql`: `draft.replacement_opportunity_never_auto_expires` |

### BR-247 — Roster Tests

| Scenario | Layer | Seam / Existing proof |
|---|---|---|
| 15-player roster (BR-037/BR-196, configurable) | DB invariant, Unit | `db-tests/050_roster_invariants.sql`: `roster.size_enforced_on_submit` |
| Positional minimums (BR-041/BR-279, configurable) | Unit | `GameweekRoster.Submit(...)` rejects a submission short of `SeasonConfiguration.PositionalMinimums` — the DB trigger checks count only (§5), so the *positional* composition check is a Unit-test responsibility, not duplicated at the DB layer |
| No positional maximums (BR-042) | Unit | A roster with, e.g., 8 Defenders and only the required minimums elsewhere is accepted |
| Squad membership (BR-194, Invariant 6 — every `RosterPlayer` must be a currently-owned `SquadPlayer`) | Unit, Integration | Submitting a Player not currently owned by this FantasyTeam is rejected |
| Deadline locking (BR-093/BR-094, ADR-012) | Integration | Given an injected clock past `RosterLockDeadline`, the sweep transitions `Draft`/`Submitted` → `Locked`, including the BR-305 carry-forward fallback |
| — carry-forward exemption from the size check (BR-305) | DB invariant, Unit | `050_roster_invariants.sql`: `roster.carry_forward_exempt_from_size_check` |
| Captain eligibility (must be one of the 15 selected, BR-046, Invariant 5) | Unit | Setting a captain not in the roster is rejected |
| No automatic substitution (BR-039/BR-040) | Unit | A non-playing starting-XI player's points are not backfilled from the bench, ever |

### BR-248 — Captain Tests

| Scenario | Layer | Seam / Existing proof |
|---|---|---|
| Captain selection (BR-045/BR-046) | Unit, DB invariant | `db-tests/050_roster_invariants.sql`: `roster.one_captain_per_roster` |
| Captain multiplier (BR-047) | Unit | Official FPL multiplier applied directly, no application-defined multiplier (BR-047) |
| Non-playing Captain (0 minutes played) | Unit | Captain's multiplied value is `0 × multiplier = 0`, not silently reassigned to a vice-captain (there is none, BR-051) |
| No Vice Captain (BR-051) | Unit | Confirms no fallback-captain code path exists at all |
| Captain tie-break accumulation (feeds ADR-008 tier 5) | Integration | `LeagueStanding.CaptainPointsTotal` sums `GameweekScore.CaptainPoints` correctly across a Season |

### BR-249 — Scoring Tests

| Scenario | Layer | Seam / Existing proof |
|---|---|---|
| Official FPL points ingestion | Integration | `PlayerDataIntegration` sync upserts `PlayerPerformance` idempotently (AP-008) — re-running the same batch produces no change |
| Captain scoring | Unit | See BR-248 |
| Corrected scores (BR-139, BR-234) | Integration | A corrected upstream value raises `ScoreRecalculated`, cascading through `GameweekScore` → `HeadToHeadMatch` → `LeagueStanding` |
| Administrator overrides (BR-140–BR-145, Invariant 12) | Unit, DB invariant | `db-tests/060_scoring_invariants.sql`: `scoring.override_undo_is_a_timestamp_not_a_delete`; the `IAuthoritativeValueResolver` precedence (Active Override > Official > Calculated) is a Unit-test matrix per Architecture §13 |
| Undoing overrides (BR-145) | Unit, DB invariant | `060_scoring_invariants.sql`: same test — `UndoneAt` set, row never deleted |

### BR-250 — Fantasy Goal Tests

| Scenario | Layer | Seam / Existing proof |
|---|---|---|
| Goals For (BR-080) | Unit | Sum of Starting XI outfield players' goals |
| Goals Against (BR-086) | Unit | Sum of Starting XI goalkeeper/defenders' conceded-goals average |
| Goalkeeper goals (do/don't count toward Goals For per BR-08x) | Unit | Exact inclusion/exclusion per the relevant `BR-###` in BRD §"Fantasy Goals" |
| Defender averages | Unit | Average conceded across all Starting XI defenders |
| Integer rounding/truncation behavior (BR-087/BR-088 — **truncation toward zero, not rounding**) | Unit | `7 / 5 = 1.4 → 1` (BRD's own worked example) is a literal test case, plus a negative-adjacent boundary if one is reachable |
| Own goals (BR-08x) | Unit | Correct sign/attribution per BRD | 
| Penalty goals | Unit | Counted identically to open-play goals unless BRD specifies otherwise |
| Goal difference (BR-089) | Unit | `FantasyGoalsFor − FantasyGoalsAgainst`, recomputed whenever either input changes |

### BR-251 — Head-to-Head Tests

| Scenario | Layer | Seam / Existing proof |
|---|---|---|
| Schedule generation (BR-107–BR-110) | Unit | Every FantasyTeam plays every other exactly once per round-robin cycle (or the League's configured format) |
| Balanced scheduling / bye weeks (BR-306) | Unit, DB invariant | An odd FantasyTeam count leaves one team with **no** `HeadToHeadMatch` row for that Gameweek — `db-tests/070_competition_invariants.sql`'s uniqueness tests confirm no fabricated match can be inserted twice for one pairing |
| Win (BR-111–BR-117) | Unit | Higher `GameweekScore.FantasyPoints` → `Result = HomeWin`/`AwayWin`, `LeaguePoints` from `SeasonConfiguration.LeaguePoints.Win` |
| Draw | Unit | Equal scores → `Result = Draw`, both sides get `LeaguePoints.Draw` |
| Loss | Unit | Mirror of Win |
| League points (configurable, BR-291) | Unit | Values read from `SeasonConfiguration`, never a literal `3`/`1`/`0` |

### BR-252 — Tie-Break Tests

Every tier of the ADR-008 pipeline needs its own test, **plus** at least one test per tier confirming it is only reached when every earlier tier is tied (not evaluated independently):

| Tier | Layer | Notes |
|---|---|---|
| 1. League points | Unit | Highest points wins outright; ties fall through |
| 2. Fantasy Goal Difference | Unit | Only evaluated when tier 1 is tied |
| 3. Fantasy Goals For | Unit | Only evaluated when tiers 1–2 are tied |
| 4. Applicable EPL-derived criteria (Head-to-Head League Points between the tied teams only, per ADR-008's resolved sequence) | Unit | Confirms the pipeline falls through directly to tier 5 when this doesn't resolve it (away-goals/neutral-venue provisions are explicitly **not** implemented, per ADR-008) |
| 5. Captain points | Unit | See BR-248 |
| 6. Season Goal Prediction | Unit | See BR-253 |
| 7. Final randomized fallback | Unit | Given an injected RNG/seed, the fallback is deterministic *for testing purposes* — assert it only ever fires when tiers 1–6 are exhausted |
| Tier sequence is configuration, not code branching (ADR-008, BR-122) | Unit | Swapping `SeasonConfiguration.TieBreakRulesetVersion` changes the evaluated pipeline order without a code change |

### BR-253 — Prediction Tests

BRD's own worked examples are the literal test cases — copied verbatim so the test names trace 1:1 back to BR-253:

| Test case | Expected |
|---|---|
| `Actual=1234, Prediction=1230` | `Difference = 4` |
| `Actual=1234, Prediction=1235` | `Difference = 1` (and this FantasyTeam wins if no closer prediction exists) |
| `Actual=1234, PredictionA=1233, PredictionB=1235` | Both `Difference = 1`; **PredictionA wins** (BRD's tie-break-within-a-tie-break: closer-below beats closer-above at equal absolute difference — confirm this exact rule against BRD §"Season Goal Prediction" before implementing, since BR-253's example implies it but does not state it as its own numbered rule) |

### BR-254 — Authorization Tests

The single most cross-cutting category — every API operation in OpenAPI Specification v1.0 carries an `x-authorization` note specifically so this table has something concrete to assert against, operation by operation:

| Scenario | Layer | Seam / Existing proof |
|---|---|---|
| Cannot modify another user's profile | API | `PUT /users/me` acts only on the bearer token's own `sub` — there is no `userId` path parameter to spoof |
| Cannot modify another user's FantasyTeam | API | Every `.../fantasy-teams/{fantasyTeamId}/...` write checks the caller's `LeagueMembership` owns `fantasyTeamId` (ADR-007) |
| Cannot modify another user's roster | API | Same pattern, `PUT .../roster` |
| Cannot modify another user's Captain | API | Same pattern, `PUT .../roster/captain` |
| Cannot modify/read another League's data | API | Every `{leagueId}`-scoped operation checks active membership in *that* League — not just "is logged in" (BR-163) |
| Cannot invoke administrative functions without permission | API | League-Administrator-only operations (draft timer extend, roster correction, score override, config change, audit read) reject a non-Administrator caller; System-Administrator-only operations (`/admin/security/*`, user reactivation) reject a non-`IsSystemAdministrator` caller even if they are a League Administrator somewhere |
| A JWT never carries role/permission claims (ADR-007) | Unit | Token issuance includes only `sub`; authorization is always re-checked against current `LeagueMembership` data, never a cached claim |
| Database-level immutability of the audit log (BR-149, ADR-010) | DB invariant | `db-tests/080_administration_invariants.sql`: `administration.audit_log_immutable_at_role_level` — proven by actually switching to the `eplfantasy_app` role and attempting the forbidden `UPDATE` |

---

## 3. AP-001–AP-010 → Test Location Map

BR-243–BR-254 are functional; AP-001–AP-010 are architectural principles BRD §51 requires the *implementation* to follow. Each needs its own test(s), since violating one is a design defect BR-243–BR-254's functional scenarios won't necessarily catch:

| Principle | Test approach |
|---|---|
| AP-001 Domain-First Design | A Unit test suite that never touches ASP.NET Core/EF Core types is itself the proof — if a business rule can only be tested through a controller or a DbContext, that's a finding against AP-001, not just a coverage gap |
| AP-002 API as Security Boundary | Every BR-254 API-layer test (§2) doubles as proof of this — plus a static/architecture test (e.g., NetArchTest, already called for in Architecture §5 for module dependencies) asserting no domain method is `public` in a way a controller could bypass validation |
| AP-003 Client as Untrusted | An API test that sends a request already "valid" per client-side expectations but invalid per a server-side domain rule (e.g., a roster with 14 players, bypassing any client validation) must still be rejected server-side |
| AP-004 Immutable Historical Records | `db-tests/080_administration_invariants.sql`'s role-grant test is the concrete database-level proof; a corresponding Integration test confirms the application layer never issues an `UPDATE`/`DELETE` against a finalized competitive row, only new override/correction rows |
| AP-005 Explicit Auditability | Every Integration test for an Administrator-privileged operation asserts exactly one new `AdministrativeAction` row was written, in the same transaction, via `IAdministrativeActionRecorder` — not a spot-check on one or two features |
| AP-006 Configuration Over Hard Coding | A parameterized Unit test suite that runs the *same* squad-size/roster-size/positional-minimum/timer/league-points scenarios against two different `SeasonConfiguration` values and gets two different (correct) outcomes — proving the value is actually read from configuration, not a disguised literal |
| AP-007 External Data Isolation | A Unit test on `PlayerDataIntegration`'s anti-corruption layer confirms external DTO shapes never leak past its boundary — no other module's test fixtures should need to construct an external API shape |
| AP-008 Idempotent Synchronization | An Integration test runs the same sync batch twice and asserts identical resulting state (no duplicate rows, no changed `UpdatedAt` on unchanged data) |
| AP-009 Transactional Draft Selection | **Already proven live**, not merely planned: `db-tests/run_concurrency_test.sh` races two real, simultaneous connections for the same Player and asserts exactly one succeeds |
| AP-010 Concurrency Control | The `GameweekRoster` `ETag`/`If-Match` optimistic-concurrency contract (OpenAPI Specification v1.0, Architecture §8.3) needs an Integration test asserting a stale `If-Match` is rejected with `409 concurrency_conflict` |

---

## 4. Test Data and Fixture Strategy

- **Deterministic clock injection.** Every deadline/timer-dependent rule (BR-029 invitation expiration, BR-057/BR-058 draft timer, BR-093/BR-094 roster lock, BR-282 pick timeout, ADR-012's sweep) is untestable without control over "now." The domain/application layer must depend on an injectable `IClock` (or equivalent) from the start — this is an implementation-task-breakdown item (Architecture §16 item 7), called out here because a testing strategy that assumes `DateTimeOffset.UtcNow` is directly callable cannot actually test half of BR-246/BR-247's scenarios.
- **A fixture builder per bounded context**, mirroring `db-tests/005_test_fixtures.sql`'s `dbtest_create_baseline_fixture(suffix)` — a single call that wires up a League, two Memberships, a Season with resolved `SeasonConfiguration`, two FantasyTeams, a Club, a Gameweek, and two Players. Reimplement the same shape as a C# test-data builder (e.g. `BaselineFixtureBuilder`) once `EplFantasy.IntegrationTests` exists, rather than inventing a different fixture shape — consistency here is what makes a failing Integration test easy to compare against the already-proven `db-tests/` behavior.
- **EPL reference data.** Draft/Roster/Scoring tests all need `Player`/`Club`/`Fixture`/`Gameweek` rows; a small, fixed, checked-in reference dataset (a handful of clubs and ~20–30 players across all four positions) is more maintainable than generating random reference data per test run, and lets test assertions reference specific, memorable values (matching BR-253's own worked-example style).
- **Concurrency tests need real parallelism, not sequential-statement simulation.** `db-tests/run_concurrency_test.sh` demonstrates the pattern (two backgrounded, genuinely simultaneous connections) — the C# equivalent (AP-009/AP-010 Integration tests) must launch two real concurrent `DbContext`-backed calls (e.g. `Task.WhenAll` against two independent scopes), not two sequential calls in one `await` chain, which would never actually exercise the race.

---

## 5. What Is Already Executable Today

Everything above the database-invariant row in §1's table requires C# application code that doesn't exist yet. The database-invariant layer does not, and is checked in at `db-tests/` (this folder), alongside this document:

| File | Covers |
|---|---|
| `db-tests/000_test_harness.sql` | A minimal, dependency-free assertion framework (`test_assert`, `test_summary`) — no pgTAP or other extension required, since the hosting/image choice (ADR-005) isn't made yet |
| `db-tests/005_test_fixtures.sql` | `dbtest_create_baseline_fixture(suffix)` — one call wires up a full League/Season/FantasyTeam/Player baseline |
| `db-tests/010_identity_invariants.sql` – `090_notifications_invariants.sql` | 33 concrete assertions, one file per bounded context, covering the database-enforceable half of BR-243–BR-251 and BR-254 |
| `db-tests/run_all.sql` | Runs every file above in order and prints a pass/fail summary |
| `db-tests/run_concurrency_test.sh` | The AP-009/AP-010 live-concurrency race (§3) |

**Proof, not just presence:** every file above was executed against a disposable PostgreSQL 16 Docker container with migrations `V001`–`V013` (from `06-database-migrations/`) already applied. `run_all.sql` reports **33 passed, 0 failed**; `run_concurrency_test.sh` was run three consecutive times and passed every time (1 success, 1 rejection, 1 persisted row, each run). Re-run either yourself with:

```bash
docker run --rm -d --name eplfantasy-check -e POSTGRES_PASSWORD=test -p 55432:5432 postgres:16
docker exec eplfantasy-check pg_isready -U postgres  # wait until this succeeds
docker exec eplfantasy-check psql -U postgres -c "CREATE DATABASE migtest;"
# apply V001..V013 from ../06-database-migrations/migrations/, in order, against migtest
docker cp db-tests/. eplfantasy-check:/db-tests/
docker exec -w //db-tests eplfantasy-check psql -U postgres -d migtest -f run_all.sql
bash db-tests/run_concurrency_test.sh eplfantasy-check migtest
docker rm -f eplfantasy-check
```

(On Git Bash/MSYS, note the double leading slash in `-w //db-tests` — a single slash gets silently mangled into a Windows path by MSYS's automatic path conversion before reaching the container.)

No CI pipeline exists yet to run this automatically — that is Architecture §16 item 7, "Implementation task breakdown," which should include "wire `db-tests/run_all.sql` and `run_concurrency_test.sh` into CI against a disposable Postgres service container" as one of its very first tasks, ahead of any C# code, so the database layer stays regression-tested from day one of implementation.

---

## Version History

### Version 1.0

Initial testing strategy, operationalizing BRD v1.17 BR-243–BR-254 and AP-001–AP-010 against the seams Architecture v1.15 §13 identifies. Defines the four-layer test pyramid (database-invariant / unit / integration / API) matching Architecture §14's project structure, maps every BR-243–BR-254 bullet and every AP-001–AP-010 principle to a concrete test-case and layer, and specifies the fixture/clock-injection strategy needed to make deadline-driven rules testable. Ships a genuinely executable first slice — the `db-tests/` database-invariant suite (33 assertions across 9 files) and a live two-connection concurrency race for AP-009 — both run and confirmed passing against a real PostgreSQL 16 instance, since no C# application code exists yet for the Unit/Integration/API layers to run against.
