# Fantasy EPL League Manager
## Implementation Task Breakdown — Version 1.0

**Document Status:** Baseline Implementation Task Breakdown

**Version:** 1.0

**Inputs:**
- Architecture and Domain Model v1.15 (`../02-architecture/...md`) — §5 (module map), §6 (aggregates), §14 (solution structure), all ADRs.
- Epic and Feature Backlog v1.10 (`../03-epics-and-backlog/...md`) — the 57-feature list (`F-###.#`), its dependency graph, and its P0/P1/P2 priorities and S/M/L sizes, which this document decomposes into engineering tasks rather than re-deriving.
- OpenAPI Specification v1.0 (`../05-api-specification/...yaml`) — `operationId`s cited per task.
- Database Migration Strategy v1.0 (`../06-database-migrations/...md`) — `V0##` migration files cited per task.
- Testing Strategy v1.0 (`../07-testing-strategy/...md`) — the BR/AP-to-test-case map each task's "Tests" line points back into.

**Purpose:** Turn Architecture §16 item 7 — "Implementation task breakdown and sequencing, starting with Identity/League/FantasyTeam since every other context depends on them" — into the actual sequence of engineering tasks a developer (or an AI pair-programmer) picks up one at a time to build `EplFantasy.*`. This is the last AIDLC artifact; there is no further document between this one and writing application code.

**Audience:** Whoever implements `EplFantasy.*` next, and whoever plans the work into sprints/milestones.

**Scope:** Every one of the Backlog's 57 features, resequenced into a single dependency-correct build order (§2 explains why this differs from the Backlog's own numbering), plus 14 foundational/cross-cutting tasks the feature tasks all depend on but that own no single `F-###.#` (§1).

---

## 1. Foundational / Cross-Cutting Tasks

These exist before any `F-###.#` feature can be built — they are the shared infrastructure Architecture's ADRs and §12 describe once, used by many features rather than owned by one. Every feature task below lists which of these it depends on.

| Task | Title | Depends On | What it establishes |
|---|---|---|---|
| **IT-F01** | Solution scaffolding | — | `EplFantasy.sln` and the project skeletons Architecture §14 names: `EplFantasy.Api`, one project per bounded context (`Identity`, `Leagues`, `FantasyTeams`, `PlayerData`, `Drafts`, `Rosters`, `Scoring`, `Competition`, `Administration`, `Notifications`, `Reporting`), `SharedKernel`, `Infrastructure`, and the three test projects. Wires the module-dependency rule (Architecture §5) as an enforced NetArchTest suite in CI, not just a convention. |
| **IT-F02** | `SharedKernel` base types | IT-F01 | `Entity`, `AggregateRoot`, `ValueObject`, `IDomainEvent`/domain-event dispatch, `Result`/error types (Architecture §14: "shared identity/value-object plumbing only, never business rules"). |
| **IT-F03** | EF Core `DbContext` + Npgsql wiring | IT-F01, IT-F02 | One `DbContext` in `EplFantasy.Infrastructure` with an entity configuration per table in `06-database-migrations/migrations/V001`–`V013`. Confirms `dotnet ef dbcontext scaffold`/`migrations` reconciles against the hand-written physical schema rather than diverging from it (Database Migration Strategy §1, "EF Core's own migrations"). Maps `xmin` as the `GameweekRoster` concurrency token (ADR-002, Migration Strategy §3). |
| **IT-F04** | `IClock` abstraction | IT-F02 | Injectable "now" for every deadline/timer rule — required before F-003.2 (invitation expiry) is the first feature that needs it (Testing Strategy §4). |
| **IT-F05** | Authentication infrastructure | IT-F02, IT-F03 | `IAuthenticationService`, `ICurrentUserAccessor`, JWT access/refresh issuance and rotation against `refresh_tokens`/`password_reset_tokens` (ADR-006, Architecture §9.1/§11; Migration Strategy §3's physical-design addition). |
| **IT-F06** | Authorization pipeline | IT-F05 | A shared authorization-handler middleware that enforces the object-level check named in each OpenAPI operation's `x-authorization` note — "is an active member of `{leagueId}`," "is the League Administrator," "is `IsSystemAdministrator`" — before the application service runs (ADR-007, AP-002). No controller re-implements this per endpoint. |
| **IT-F07** | `IAdministrativeActionRecorder` decorator | IT-F03 | The shared mechanism every Administrator-privileged application-service method routes through to write an `administrative_actions` row atomically with its change (Architecture §6.8/§12.1, AP-005, BR-149). |
| **IT-F08** | ADR-012 deadline-sweep background service | IT-F03, IT-F04 | One `IDeadlineSweepJob`-shaped hosted service, parameterized per aggregate type (`Draft` pick timeout, `GameweekRoster` lock), on a 15–30s interval. |
| **IT-F09** | `IAuthoritativeValueResolver` | IT-F03 | Active `ScoreOverride` > official `PlayerPerformance` > application calculation precedence (Architecture §6.6, Invariant 12), used everywhere a player statistic is read for scoring — never inlined per call site. |
| **IT-F10** | `IStandingsTieBreakRule` pipeline | IT-F03 | The ADR-008 versioned, configuration-driven tie-break ruleset (reads `SeasonConfiguration.TieBreakRulesetVersion`), not a hard-coded `if/else` chain. |
| **IT-F11** | `PlayerDataIntegration` anti-corruption layer | IT-F03 | The module boundary translating external FPL/EPL shapes into internal models (ADR-009, AP-007), plus the shared idempotent-upsert sync framework (AP-008) every EPL/FPL Data feature (F-004.x) plugs into. |
| **IT-F12** | Notification outbox dispatcher skeleton | IT-F03, IT-F04 | The background worker that drains `notification_requests` (Architecture §12.2) — built provider-agnostic since the actual email/SMS provider is still an open decision (Architecture §15); F-012.4 later plugs a real provider into this skeleton. |
| **IT-F13** | CI pipeline | IT-F01 | Wires `db-tests/run_all.sql` and `run_concurrency_test.sh` (`07-testing-strategy/db-tests/`) into CI against a disposable Postgres service container **immediately** — before any C# exists — so the database layer is regression-tested from day one, per Testing Strategy §5's explicit ask. Adds `dotnet test` for the three test projects once IT-F01 creates them. |
| **IT-F14** | Global API conventions | IT-F01 | `ProblemDetails` error middleware (with `errorCode`), correlation-ID middleware (BR-241), rate-limiting middleware (BR-169, feeding `security_events`), `Idempotency-Key` handling, versioned routing under `/api/v1` (Architecture §9.3/§11). |

---

## 2. Why This Sequence Differs From the Backlog's Own Numbering

The Backlog's own build sequence (§5) already resolves the one big cross-epic dependency (Standings before Secondary Draft) but leaves four features attached at the *end* of its numbered list — `F-007.5`, `F-011.3`, `F-004.6`, and (implicitly, via its numbering gaps) a few others — with an explicit note that they're "listed here only because it was identified later, not because of a real ordering constraint," and a standing instruction to slot each one in wherever its actual dependencies allow. This document does that slotting, once, so a developer doesn't have to re-derive it from four separate footnotes while implementing. Every feature below is numbered `IT-01`…`IT-57` in the actual dependency-respecting order verified against the Backlog's own "Depends On" column (§4 of that document) — each task's "Depends on" line names the earlier `IT-##` task(s), never a later one.

The two visible differences from the Backlog's own numbering:
- `F-007.5` (Squad View) moves from Backlog position 55 to right after `F-005.2` (its only real dependency), instead of after all of Phase 3.
- `F-011.3` (Security & Abuse Monitoring) moves from Backlog position 56 to right after `F-001.1` (its only real dependency), instead of after all of Phases 2–4.
- `F-004.6` (EPL Table & Fixtures) moves from Backlog position 57 to right after `F-004.2` (its real dependencies), instead of after all of Phase 1's other features.
- The Secondary/Replacement Draft features (`F-006.1`–`F-006.4`) and `F-011.2` are pulled out of numeric Epic order into their correct position *after* Standings (`F-010.1`) — the Backlog's own §4 note already calls this out; this document just executes it as a single flat sequence instead of leaving it as a caveat on top of the numbered list.

---

## 3. Definition of Done (applies to every feature task below)

A task is not complete until all of the following hold — this is intentionally generic and not repeated per task:

1. **Domain rule implemented at the aggregate**, not the controller (AP-001) — the aggregate's own method throws a domain exception for an invalid transition rather than allowing bad state to be persisted (Architecture §13).
2. **EF Core mapping matches its `V0##` migration exactly** — no schema drift between this document's C# model and `06-database-migrations/`.
3. **OpenAPI contract conformance** — response shape, status codes, and `errorCode`s match `05-api-specification/...yaml` exactly for every `operationId` the task lists.
4. **Object-level authorization** implemented per the OpenAPI operation's `x-authorization` note (IT-F06), not assumed from "is logged in" alone.
5. **Administrator-privileged operations** write exactly one `administrative_actions` row via IT-F07, in the same transaction as the change (AP-005).
6. **Tests pass at every layer the feature touches** — the relevant row(s) in Testing Strategy §2/§3, plus any already-passing `db-tests/` assertion the feature's migration file backs (Testing Strategy §5).
7. **Configuration, not literals** (AP-006) — any value BRD v1.5 (`BR-290`–`BR-297`) names as configurable is read from `SeasonConfiguration`/`LeagueConfiguration`, never hard-coded.

---

## 4. Milestone 1 — Foundation (Identity, League & Season, Profile/FantasyTeam, EPL/FPL Data)

Per Architecture §16 item 7's own instruction ("starting with Identity/League/FantasyTeam... since every other context depends on them"). Everything in Milestone 2 onward depends on something here.

### IT-01 — F-001.1 User registration & login
**Depends on:** IT-F02, IT-F03, IT-F04, IT-F05, IT-F14 · **Migration:** V002 · **API:** `registerUser`, `login`, `refreshToken`, `logout`
**Traces to:** BR-001–BR-004, BR-156, BR-158, BR-159
- Domain: `User`/`UserProfile` aggregates, `UserRegistered` event; username uniqueness and password-strength (BR-285) as domain-level checks, not just a DB constraint
- Persistence: EF Core mapping for `users`, `user_profiles`, `username_history` (opens the first row at registration), `refresh_tokens`
- API: the four auth endpoints; JWT carries only `sub` (ADR-007)
- Tests: unit (uniqueness, password strength); integration (registration round-trip); `db-tests/010_identity_invariants.sql` already proves the DB-level half

### IT-02 — F-011.3 Security & abuse monitoring
**Depends on:** IT-01, IT-F06, IT-F14 · **Migration:** V010 · **API:** `getRateLimitConfiguration`, `getSecurityEvents`, `getCsrfStatus`
**Traces to:** BR-168, BR-169, BR-170, BR-327, BR-328
- Domain: none (read-only, platform-level, not League-scoped)
- Persistence: `security_events` written directly by the rate-limiting middleware (IT-F14) on every enforced block
- API: three `System Administrator`-only endpoints (`IsSystemAdministrator`, never a League role)
- Tests: API (non-`SystemAdministrator` caller rejected); integration (a triggered rate-limit block produces a row)

### IT-03 — F-003.1 League creation
**Depends on:** IT-01, IT-F03, IT-F06, IT-F07 · **Migration:** V004 · **API:** `createLeague`, `getLeague`, `updateLeague`
**Traces to:** BR-023, BR-024, BR-026
- Domain: `League`/`LeagueMembership` aggregates created together; creator's membership gets `IsAdministrator = true`
- Persistence: the `leagues` ↔ `league_memberships` **deferred circular FK** — both rows in one transaction (Migration Strategy §2 already proves this at the DB level)
- API: three endpoints; `updateLeague` is Administrator-only
- Tests: `db-tests/020_league_and_season_invariants.sql`'s `league.circular_fk_deferred_to_commit`, `league.one_administrator_per_league`

### IT-04 — F-003.2 League invitation & acceptance
**Depends on:** IT-03, IT-F04 · **Migration:** V004 · **API:** `createInvitation`, `listInvitations`, `revokeInvitation`, `acceptInvitation`
**Traces to:** BR-027–BR-029, BR-293
- Domain: `Invitation` aggregate; `ExpiresAt = CreatedAt + LeagueConfiguration.InvitationExpirationDays` at issuance (BR-293: a later config change never rewrites an already-issued invitation)
- Persistence: token uniqueness (`db-tests/020...sql`: `league.invitation_token_unique`)
- API: issue/list/revoke are Administrator-only; accept is public (token-authenticated, not bearer-authenticated)
- Tests: unit (expiry against IT-F04's injected clock); API (expired/revoked token → `410`)

### IT-05 — F-003.3 League membership management
**Depends on:** IT-03, IT-04 · **Migration:** V004 · **API:** `listMemberships`, `getMembership`, `leaveLeague`
**Traces to:** BR-017, BR-020–BR-022, BR-025, BR-161, BR-162, BR-283
- Domain: join (via invitation acceptance, IT-04)/leave; sole-Administrator-cannot-leave-without-transfer guard (BR-025)
- Persistence: one active membership per (League, User); rejoin-after-leave — both already proven in `db-tests/020...sql`
- API: `leaveLeague` self-service or Administrator-on-behalf-of
- Tests: unit (sole-admin guard); `db-tests` (`league.one_active_membership_per_user`, `league.rejoin_after_leaving_allowed`)

### IT-06 — F-003.4 Season creation & reuse
**Depends on:** IT-03 · **Migration:** V004 · **API:** `createSeason`, `listSeasons`, `getSeason`
**Traces to:** BR-032, BR-033, BR-092
- Domain: `Season` aggregate; a League may be reused across Seasons; membership is **not** auto-carried — invitations are resent (IT-04) each Season
- Persistence: `seasons.epl_season_identifier` FK to the platform-level `epl_seasons` (Migration Strategy §3's `epl_seasons` addition)
- API: Administrator-only create; members can list/read
- Tests: integration (a second Season for the same League doesn't touch the first's data)

### IT-07 — F-010.3 Season goal prediction (submission capability)
**Depends on:** IT-06 · **Migration:** V009 · **API:** `getSeasonGoalPrediction`, `submitSeasonGoalPrediction`
**Traces to:** BR-126–BR-135
- Domain: `SeasonGoalPrediction` aggregate; locks at Season start (BR-127/BR-128) or at submission time under the BR-299 late-submission fallback (wired properly once IT-29/F-007.1 exists — this task builds submission only, per the Backlog's own note that collection must start now even though *usage* groups with Milestone 3's tie-break work)
- Persistence: one prediction per (Season, FantasyTeam) — `db-tests/070_competition_invariants.sql`: `competition.season_goal_prediction_unique_per_team`
- API: submit is FantasyTeam-owner-only
- Tests: unit (BR-253's three literal worked examples — see Testing Strategy §2)

### IT-08 — F-003.5 League/Season configuration management (core mechanics)
**Depends on:** IT-06 · **Migration:** V004 · **API:** `getLeagueConfiguration`, `updateLeagueConfiguration`, `getSeasonConfiguration`, `updateSeasonConfiguration`
**Traces to:** BR-290–BR-297
- Domain: `LeagueConfiguration`/`SeasonConfiguration` value objects (ADR-011); copy-at-Season-creation (BR-292); per-field lock tracking (BR-293/BR-294)
- Persistence: flat, typed columns already in V004 — one per configurable parameter, matching ADR-011 exactly (no `jsonb` blob)
- API: config reads open to members, writes Administrator-only; every write routes through IT-F07 (`ConfigurationChanged` action type)
- Tests: unit (a locked field's PUT → `409`); this is *core mechanics only* — each parameter's owning feature (IT-23 for squad size, IT-29 for roster size, etc.) adds its own read path against this, per the Backlog's own note

### IT-09 — F-002.1 Default profile icon selection
**Depends on:** IT-01 · **Migration:** V002 · **API:** `updateDefaultIcon`, `listProfileIcons`
**Traces to:** BR-006, BR-011, BR-267
- Domain: `UserProfile.SetDefaultIcon(iconId)` rejects an inactive/unknown `ProfileIconId` (BR-011 — never a client-supplied path)
- Persistence: `profile_icons` already seeded (`06-database-migrations/migrations/V012`)
- Tests: unit (rejecting an inactive icon)

### IT-10 — F-002.2 League-specific icon override
**Depends on:** IT-09, IT-05 · **Migration:** V004 · **API:** `setLeagueIcon`, `clearLeagueIcon`
**Traces to:** BR-007–BR-010, BR-203, BR-268, BR-274–BR-276
- Domain: `LeagueMembership.LeagueIconId` override, independent per League (BR-009); `clear` reverts to the global default (BR-010)
- API: member-owns-membership authorization only (no Administrator involvement)
- Tests: integration (changing League A's icon never touches League B's row for the same User)

### IT-11 — F-002.3 FantasyTeam creation & uniqueness
**Depends on:** IT-05, IT-06 · **Migration:** V005 · **API:** `createFantasyTeam`, `listFantasyTeams`, `getFantasyTeam`
**Traces to:** BR-018–BR-020, BR-193, BR-269, BR-277
- Domain: `FantasyTeam` aggregate; one per (LeagueMembership, Season) — Invariant 2
- Persistence: `db-tests/030_fantasy_team_and_squad_invariants.sql`: `fantasy_team.one_per_membership_season`
- Tests: already proven at the DB layer; add the application-service round-trip in Integration

### IT-12 — F-002.4 Profile/league context switching UI
**Depends on:** IT-11 · **Migration:** — (read-only composition) · **API:** `listMyLeagues`, `getMembership`
**Traces to:** BR-031, BR-199–BR-202
- Domain: none — a UI/read-model concern selecting which active League's FantasyTeam the caller is managing
- Tests: API (a User with two active Leagues gets both back; selecting one scopes subsequent calls correctly)

### IT-13 — F-001.2 Password reset
**Depends on:** IT-01, IT-F04 · **Migration:** V002 · **API:** `requestPasswordReset`, `confirmPasswordReset`
**Traces to:** BR-284, BR-285
- Domain: time-limited, single-use reset token (Migration Strategy §3's `password_reset_tokens` addition)
- API: `requestPasswordReset` always returns `202` regardless of email match (no account-existence leak)
- Tests: unit (expired/already-used token rejected); `db-tests/010...sql` proves hash-uniqueness

### IT-14 — F-001.3 Username management
**Depends on:** IT-01 · **Migration:** V002 · **API:** `updateUsername`
**Traces to:** BR-003, BR-004, BR-265, BR-266, BR-270, BR-326
- Domain: `UsernameChanged` event closes the open `UsernameHistory` row and opens a new one, in the same transaction
- Tests: `db-tests/010...sql`: `identity.username_history_one_open_row`, `identity.username_history_rollover_after_close`

### IT-15 — F-001.4 User retirement
**Depends on:** IT-01 · **Migration:** V002 · **API:** `retireCurrentUser`
**Traces to:** BR-013, BR-014, BR-175
- Domain: soft-delete only — `Status = Retired`, never a physical delete (ADR-010)
- Tests: `db-tests/010...sql`: `identity.username_reusable_after_retirement` (BR-298)

### IT-16 — F-001.5 Private data protection
**Depends on:** IT-01 · **Migration:** — (a DTO-shape constraint, not new persistence) · **API:** cross-cutting — every League-facing DTO
**Traces to:** BR-015
- Domain: none — this is a *DTO design rule*: `UserSelfDto` (self-access) may include email; every League-facing DTO (`LeagueMembership`, standings rows, etc.) never does
- Tests: API — a contract test asserting no League-facing response schema in the OpenAPI spec contains an `email`/`passwordHash`/token field (a single automated check catches every future regression here, cheaper than testing each endpoint individually)

### IT-17 — F-004.1 Player/club reference data sync
**Depends on:** IT-F11 · **Migration:** V003 · **API:** `listClubs`, `listPlayers`, `getPlayer` (reads only; sync itself is a background job, no write endpoint)
**Traces to:** BR-227, BR-228, BR-232, BR-233, BR-289
- Domain: the sync job upserts on `epl_club_id`/`epl_player_id` (external identifiers) — never blind-inserts (BR-232, AP-008)
- Persistence: `clubs`, `players` (V003)
- Tests: integration (running the same sync batch twice produces no duplicate/changed rows — AP-008)

### IT-18 — F-004.2 Fixture & gameweek sync
**Depends on:** IT-17 · **Migration:** V003 · **API:** `listGameweeks`, `getGameweekFixtures`
**Traces to:** BR-092, BR-229, BR-230
- Domain: `Gameweek`/`Fixture` reference data; `RosterLockDeadline` per Gameweek (feeds IT-31)
- Tests: integration (idempotent re-sync, same as IT-17)

### IT-19 — F-004.6 EPL league table & fixtures display
**Depends on:** IT-17, IT-18 · **Migration:** V003 · **API:** `getEplTable` (`getGameweekFixtures` already exists from IT-18)
**Traces to:** BR-329–BR-335
- Domain: `ClubStanding` read-model, recomputed from `Fixture` results as they sync — no Administrator-override path (BR-334, distinct from Fantasy `LeagueStanding`)
- Tests: integration (table recomputes correctly after a fixture result is synced)

### IT-20 — F-004.3 Player statistics sync
**Depends on:** IT-18 · **Migration:** V008 · **API:** none directly (feeds `player_performances`, read via later scoring/squad endpoints)
**Traces to:** BR-076, BR-077, BR-231–BR-234
**Note:** this is the largest Phase-1 item (Backlog: Size L) — the sync job that must also handle **reconciliation** of corrected historical data (BR-234), not just first-time ingestion.
- Domain: `PlayerPerformance` kept separate from any derived score (Architecture §6.6) so raw official data is never overwritten by calculation
- Persistence: `db-tests/060_scoring_invariants.sql`: `scoring.performance_unique_per_gameweek_player`
- Tests: integration (a corrected upstream value raises `ScoreRecalculated` rather than silently overwriting)

### IT-21 — F-004.4 EPL transfer handling
**Depends on:** IT-17 · **Migration:** V003, V006 · **API:** none directly (feeds `players.current_club_id` and `replacement_opportunities`)
**Traces to:** BR-066, BR-070–BR-072, BR-259
- Domain: an EPL-exit sync event nulls `Player.CurrentClubId` and automatically grants a `ReplacementOpportunity` (BR-066/BR-308) — the one `grant_reason = 'epl_exit'` path, distinct from the Administrator-driven injury path (IT-49)
- Tests: integration (exit sync produces exactly one opportunity, `acting_membership_id IS NULL` on the resulting `AdministrativeAction`)

### IT-22 — F-004.5 Postponed/abandoned fixture handling
**Depends on:** IT-18 · **Migration:** V003 · **API:** reflected via `fixtures.status` in `getGameweekFixtures`
**Traces to:** BR-100–BR-106
- Domain: `Fixture.Status` transitions (`scheduled → postponed/abandoned`); a postponed fixture's player has no opponent shown that Gameweek (feeds IT-29's BR-337 display)
- Tests: unit (roster-screen opponent resolution shows nothing, not a stale value, for a postponed fixture)

---

## 5. Milestone 2a — Draft & Squad, part A (Initial Draft)

Secondary/Replacement Draft (`F-006.x`) is **not** here — it needs Standings (Milestone 3) first, per the Backlog's own cross-epic dependency note. It resumes as Milestone 2b, §7.

### IT-23 — F-005.1 Draft setup & randomized order
**Depends on:** IT-08, IT-17 · **Migration:** V006 · **API:** `createDraft`
**Traces to:** BR-053–BR-055, BR-291
- Domain: `Draft` aggregate; `DraftOrder` a random permutation (injectable RNG for testability); `InitialSquadSize` read from `SeasonConfiguration` (IT-08), never a literal `25`
- Tests: unit (order is a true permutation of all participating FantasyTeamIds)

### IT-24 — F-005.2 Snake draft pick flow & ownership enforcement
**Depends on:** IT-23 · **Migration:** V005, V006 · **API:** `makeDraftPick`
**Traces to:** BR-052, BR-059, BR-191, BR-192, AP-009, AP-010
**This is the highest-risk task in Milestone 2** — the atomic-pick guarantee (AP-009) everything downstream in Drafts/Squad relies on.
- Domain: `Draft.MakePick(fantasyTeamId, playerId)` snake-order turn validation; `PlayerDrafted` event
- Persistence: `draft_selections` insert + `squad_players` insert in **one transaction**, guarded by `ux_squad_players_owned` — a losing concurrent pick surfaces as `409 player_already_owned`, not a raw constraint error
- API: `Idempotency-Key` header handling (BR-237)
- Tests: **`db-tests/run_concurrency_test.sh` is the proof this must be reproduced in `EplFantasy.IntegrationTests`** — two real concurrent `DbContext`-backed calls (`Task.WhenAll`, two independent scopes), asserting exactly one succeeds; do not settle for two sequential calls, which would never exercise the actual race

### IT-25 — F-007.5 Squad View
**Depends on:** IT-24 · **Migration:** V005, V008 · **API:** `getSquad`
**Traces to:** BR-209, BR-314–BR-320
- Domain: none new — a read-model composing `SquadPlayer` (acquisition type), `PlayerSeasonStatistics` (the `player_season_statistics` view from Migration V008), and current-Gameweek `RosterPlayer` membership (built once IT-29 exists; until then, the roster-indicator column resolves to false for every row)
- API: shares its `position`/`search`/`sort` query-parameter implementation with `getDraftPlayerPool` (IT-28) — one implementation, two read-models, per Architecture §6.3
- Tests: integration (a released player shows `isCurrentlyOwned = false`, still visible in history)

### IT-26 — F-005.3 Draft timer & admin extension
**Depends on:** IT-24, IT-F08 · **Migration:** V006 · **API:** `extendDraftTimer`
**Traces to:** BR-057, BR-058, BR-206
- Domain: `TimerSeconds` from `SeasonConfiguration.DraftTimerSecondsByType` (ADR-011); extension is Administrator-only, writes an `AdministrativeAction` (`DraftTimerExtended`)
- Tests: unit (extension by a non-Administrator rejected)

### IT-27 — F-005.4 Draft pick timeout / makeup-pick handling
**Depends on:** IT-26 · **Migration:** V006 · **API:** reflected via `getDraft`'s `pendingMakeupPicks`
**Traces to:** BR-282
- Domain: the ADR-012 sweep (IT-F08) skips an expired pick (no `DraftSelection` recorded), enqueues it onto `PendingMakeupPicks`, raises `DraftPickSkipped`; makeup picks inserted after the final regular round
- Tests: integration (a full draft with one deliberately-expired pick still reaches `Completed` with every FantasyTeam at its required selection count)

### IT-28 — F-005.5 Draft UX
**Depends on:** IT-24 · **Migration:** V006, V008 · **API:** `getDraft`, `listDraftSelections`, `getDraftPlayerPool`
**Traces to:** BR-204, BR-205, BR-207, BR-208, BR-310–BR-313, BR-323–BR-325
- Domain: none new — composes `Draft`, `DraftSelection` history, and the player-pool read-model (statistics resolve to zero before any Gameweek is scored, per Architecture §6.6)
- Tests: API (player-pool `position`/`search`/`sort` parameters, shared implementation with IT-25)

---

## 6. Milestone 3 — Weekly Gameplay

### IT-29 — F-007.1 Weekly roster submission
**Depends on:** IT-24, IT-18, IT-08 · **Migration:** V007 · **API:** `getGameweekRoster`, `submitGameweekRoster`
**Traces to:** BR-037, BR-041, BR-196, BR-211, BR-279, BR-291, BR-336, BR-337
**Second-highest-risk task** — the deferred-trigger/positional-minimum interplay already validated at the DB layer must be matched exactly at the domain layer.
- Domain: `GameweekRoster.Submit(players, captainId)` enforces exactly `WeeklyRosterSize` players satisfying `PositionalMinimums` (both from `SeasonConfiguration`, IT-08) — the DB trigger (`trg_enforce_gameweek_roster_size`) only checks the *count*, so positional composition is a domain/Unit-test responsibility, not a duplicate DB check; BR-299 precondition (a missing `SeasonGoalPrediction`, IT-07) blocks a FantasyTeam's first submission of the Season
- Persistence: `ETag`/`If-Match` optimistic concurrency (`xmin`, IT-F03)
- API: response includes Gameweek fixtures + per-player opponent (BR-336/BR-337, reusing IT-18's fixture data — no new query shape)
- Tests: `db-tests/050_roster_invariants.sql`: `roster.size_enforced_on_submit`; unit (positional-minimum rejection, BR-299 precondition)

### IT-30 — F-007.2 Captain selection
**Depends on:** IT-29 · **Migration:** V007 · **API:** `setCaptain`
**Traces to:** BR-045, BR-046, BR-195, BR-212
- Domain: captain must be one of the roster's 15 players (Invariant 5)
- Tests: `db-tests/050...sql`: `roster.one_captain_per_roster`

### IT-31 — F-007.3 Roster lock & deadline
**Depends on:** IT-29, IT-18, IT-F08 · **Migration:** V007 · **API:** reflected via `getGameweekRoster.status`
**Traces to:** BR-093–BR-096, BR-213, BR-214
- Domain: the ADR-012 sweep locks a `Submitted` roster past its deadline; a still-`Draft` roster gets the **BR-305 carry-forward fallback** — copy the FantasyTeam's last `Locked` roster (including captain), or lock empty on a first Gameweek, or drop a no-longer-owned carried-forward player, all **without** re-running the size/positional check (that check is for user submissions only)
- Tests: `db-tests/050...sql`: `roster.carry_forward_exempt_from_size_check` — this is the single most detail-sensitive rule in the whole domain model; do not generalize the size check to "always applies," it explicitly must not for a carry-forward roster

### IT-32 — F-007.4 Administrator roster correction
**Depends on:** IT-31, IT-F07 · **Migration:** V007 · **API:** `correctRoster`
**Traces to:** BR-097–BR-099, BR-146–BR-149
- Domain: Administrator-only, post-lock; writes `AdministrativeAction` (`RosterCorrection`) atomically
- Tests: unit (non-Administrator rejected); integration (audit row written in the same transaction)

### IT-33 — F-008.1 Official FPL score ingestion & application
**Depends on:** IT-20, IT-31 · **Migration:** V008 · **API:** reflected via `getGameweekScore` (read); ingestion itself is a background job
**Traces to:** BR-073–BR-077, BR-079, BR-288
- Domain: double-gameweek handling requires no special logic — official FPL already sums multi-fixture totals per Gameweek before this reads it (BR-288)
- Tests: `db-tests/060_scoring_invariants.sql`: `scoring.gameweek_score_unique_per_team_and_gameweek`

### IT-34 — F-008.2 Starting XI / bench determination
**Depends on:** IT-33 · **Migration:** V007 (writes `roster_players.selection_role`) · **API:** reflected via `getGameweekRoster`/`getGameweekScore`
**Traces to:** BR-038–BR-040, BR-043, BR-044
- Domain: captain multiplier applied **before** ranking (BR-304 — captain competes for a Starting XI spot on the multiplied value); top 11 → `StartingXI`, remainder → `Bench`; an 11th/12th-place tie breaks on a stable `PlayerId` ordering, never random (BR-303)
- Tests: unit (the exact tie-break boundary case; captain-multiplier-before-ranking case)

### IT-35 — F-008.3 Captain scoring
**Depends on:** IT-34 · **Migration:** V008 · **API:** reflected via `getGameweekScore.captainPoints`
**Traces to:** BR-047, BR-048, BR-050, BR-051
- Domain: official FPL multiplier applied directly (BR-047, no application-defined multiplier); no vice-captain fallback exists (BR-051)
- Tests: unit (a non-playing captain scores 0×multiplier, no fallback)

### IT-36 — F-008.4 Fantasy Goals For/Against calculation
**Depends on:** IT-33 · **Migration:** V008 · **API:** reflected via `getGameweekScore`
**Traces to:** BR-080–BR-091
- Domain: Goals Against uses **truncation toward zero**, not rounding (BR-087/BR-088's own worked example: `7/5 = 1.4 → 1`)
- Tests: unit — the BRD worked example is a literal test case (Testing Strategy §2, BR-250)

### IT-37 — F-008.5 Score corrections & administrator overrides
**Depends on:** IT-33, IT-F09 · **Migration:** V008 · **API:** `createScoreOverride`, `undoScoreOverride`
**Traces to:** BR-139–BR-145
- Domain: routes every read through `IAuthoritativeValueResolver` (IT-F09); an override triggers `ScoreRecalculated` cascading through `GameweekScore` → `HeadToHeadMatch` → `LeagueStanding`
- Tests: `db-tests/060_scoring_invariants.sql`: `scoring.override_undo_is_a_timestamp_not_a_delete`

### IT-38 — F-009.1 Schedule generation
**Depends on:** IT-11, IT-06 · **Migration:** V009 · **API:** reflected via `getSchedule`
**Traces to:** BR-107–BR-110
- Domain: round-robin generation; an odd FantasyTeam count leaves one team with **no** `HeadToHeadMatch` row that Gameweek (bye week, BR-306) — never a fabricated match
- Tests: `db-tests/070_competition_invariants.sql`: `competition.match_unique_per_season_gameweek_pairing`, `competition.match_cannot_be_self_versus_self`

### IT-39 — F-009.2 Match result calculation
**Depends on:** IT-38, IT-33–IT-37 (scores) · **Migration:** V009 · **API:** `getMatch`
**Traces to:** BR-111–BR-114
- Domain: triggered by `GameweekScore` calculation (cascade from IT-33/IT-37)
- Tests: integration (a score recalculation correctly re-derives the match result)

### IT-40 — F-009.3 League points allocation
**Depends on:** IT-39 · **Migration:** V009 · **API:** reflected via `getMatch`/`getSchedule`
**Traces to:** BR-115–BR-117, BR-291
- Domain: `LeaguePoints.{Win,Draw,Loss}` from `SeasonConfiguration` (IT-08), never literals `3`/`1`/`0`
- Tests: unit (two different configured point values produce two different, correct outcomes — AP-006)

### IT-41 — F-009.4 Schedule & match result display
**Depends on:** IT-39 · **Migration:** V009 · **API:** `getSchedule`, `getMatch`
**Traces to:** BR-218–BR-220
- Domain: none new (read composition)
- Tests: API (contract conformance only)

### IT-42 — F-010.1 Standings calculation
**Depends on:** IT-40 · **Migration:** V009 · **API:** reflected via `getStandings`
**Traces to:** BR-118, BR-215, BR-216
- Domain: `LeagueStanding` recomputed per Gameweek (not only once per Season) so point-in-time queries (BR-217) are a plain lookup, never a recompute
- Tests: `db-tests/070_competition_invariants.sql`: `competition.standings_supports_point_in_time_history`

### IT-43 — F-010.2 Tie-break hierarchy engine
**Depends on:** IT-42, IT-F10 · **Migration:** — (uses V009's `league_standings.position`) · **API:** reflected via `getStandings` ordering
**Traces to:** BR-119–BR-125, BR-280
**This is the largest single test-writing effort in the whole breakdown** (Backlog: Size L) — every tier of Testing Strategy §2's BR-252 table needs its own test, plus a test per tier confirming it only fires when every earlier tier is tied.
- Domain: the full ADR-008 pipeline via IT-F10 — League Points → Fantasy Goal Difference → Fantasy Goals For → Head-to-Head League Points (tied teams only) → Captain Points → Season Goal Prediction → random fallback (away-goals/neutral-venue provisions explicitly **not** implemented)
- Tests: unit — all 7 tiers plus the "only-when-earlier-tiers-tied" assertion per tier (Testing Strategy §2, BR-252)

### IT-44 — F-010.4 Standings display
**Depends on:** IT-42 · **Migration:** V009 · **API:** `getStandings`
**Traces to:** BR-215, BR-217
- Domain: none new
- Tests: API (contract conformance; `asOfGameweekId` point-in-time query parameter)

---

## 7. Milestone 2b — Secondary & Replacement Draft

Unblocked only now that Milestone 3's Standings (IT-42) exists — this is the Backlog's own flagged cross-epic dependency, executed here rather than left as a caveat.

### IT-45 — F-006.1 Secondary draft scheduling
**Depends on:** IT-18, IT-08 · **Migration:** V006 · **API:** `createDraft` (`draftType: Secondary`)
**Traces to:** BR-069, BR-281
- Domain: proposed start date = transfer-window close + `SecondaryDraftSchedulingOffsetDays`, advanced past any day with a scheduled EPL fixture (a Season-level application service, not the `Draft` aggregate itself — no `Draft` exists yet at this point)
- Tests: unit (offset calculation against a fixed fixture calendar)

### IT-46 — F-006.2 Secondary draft order (standings snapshot)
**Depends on:** IT-42, IT-24 · **Migration:** V006 · **API:** reflected via `getDraft.draftOrder`
**Traces to:** BR-056, BR-136–BR-138
- Domain: `StandingsSnapshotTakenAt` captured once, immutable after — a later standings recalculation (e.g., from a late score override) must **not** reorder an already-snapshotted Secondary Draft
- Tests: integration (snapshot immutability under a subsequent standings recalculation)

### IT-47 — F-006.3 Secondary draft pick flow
**Depends on:** IT-24, IT-46 · **Migration:** V005, V006 · **API:** `makeDraftPick` (shared engine, `draftType: Secondary`)
**Traces to:** BR-060–BR-062, BR-198
- Domain: reuses IT-24's atomic-pick transaction verbatim — same engine, same `ux_squad_players_owned` guard, different `Draft.DraftType`
- Tests: the same concurrency proof as IT-24, parameterized for `Secondary`

### IT-48 — F-006.4 Replacement eligibility & drafting
**Depends on:** IT-47 · **Migration:** V006 · **API:** `listReplacementOpportunities`, `makeDraftPick` (`draftType: Replacement`)
**Traces to:** BR-063–BR-068, BR-260–BR-264, BR-287
- Domain: an opportunity never auto-expires (BR-307 — already proven, `db-tests/040_draft_invariants.sql`: `draft.replacement_opportunity_never_auto_expires`); `SeasonConfiguration.ReplacementSelectionCap` (null = uncapped) enforced at grant time, not spend time
- Tests: unit (capped vs. uncapped League behave differently — AP-006)

### IT-49 — F-011.2 Injury/replacement eligibility determination UX
**Depends on:** IT-48 · **Migration:** V006, V010 · **API:** `declareSeasonEndingInjury`
**Traces to:** BR-068, BR-260
- Domain: the **Administrator-initiated** eligibility path — distinct from IT-21's automatic EPL-exit path; writes `AdministrativeAction` (`SeasonEndingInjuryDeclared`) with a real `acting_membership_id`, unlike IT-21's system-generated (`null`) entries
- Tests: `db-tests/080_administration_invariants.sql`: `administration.null_acting_membership_allowed_for_system_actions` proves the *other* half of this distinction

---

## 8. Milestone 4 — Operations (P1/P2)

Everything here is P1 or P2 (Backlog §2) — valuable, but not required for BR-255's complete-season flow, which is fully covered by Milestones 1–3 and 2b.

### IT-50 — F-013.1 Historical season archive
**Depends on:** IT-44, IT-48 (a fully completed Season, end-to-end) · **Migration:** V004, V009 (already retains data permanently; no new schema) · **API:** `listSeasons` (`status=completed`), `getStandings` (historical `asOfGameweekId`)
**Traces to:** BR-174, BR-176, BR-217, BR-257, BR-296
- Domain: none new — this task is really "confirm nothing purges/archives a completed Season's data," since the schema already retains everything permanently (Migration Strategy §1's historical-data strategy)
- Tests: integration (a completed Season's standings/schedule remain queryable byte-for-byte after a new Season starts)

### IT-51 — F-003.6 League messaging
**Depends on:** IT-05 · **Migration:** V004 · **API:** `listLeagueMessages`, `createLeagueMessage`
**Traces to:** BR-221–BR-223
- Domain: Administrator-only authorship (BR-221); output-encoded on every render (BR-167 — a client-side concern, but the API must never pre-render/interpret the body as markup)
- Tests: API (non-Administrator author rejected)

### IT-52 — F-011.1 Administrative audit log viewer
**Depends on:** IT-32, IT-37 · **Migration:** V010 · **API:** `getAuditLog`
**Traces to:** BR-177–BR-182, BR-321–BR-322
- Domain: none new — `BeforeState`/`AfterState` are already returned with every row (IT-F07), so per-entry detail expansion needs no new endpoint (BR-322)
- Tests: API (`actionType`/`from`/`to`/`fantasyTeamId` filters; a `ConfigurationChanged` row surfaces under the `"__league_settings__"` pseudo-value for the `fantasyTeamId` filter, per OpenAPI Specification v1.0)

### IT-53 — F-012.1 Notification preferences management
**Depends on:** IT-01, IT-05, IT-12 · **Migration:** V011 · **API:** `getNotificationPreferences`, `updateNotificationPreferences`
**Traces to:** BR-150, BR-151, BR-155, BR-226, BR-338, BR-339
- Domain: keyed per `LeagueMembership`, not per `User` (BR-338) — seeded all-disabled at membership creation
- Tests: `db-tests/090_notifications_invariants.sql`: `notifications.preference_independent_per_league_membership`

### IT-54 — F-012.4 Notification delivery infrastructure
**Depends on:** IT-53, IT-F12 · **Migration:** V011 · **API:** none (outbox internals)
**Traces to:** BR-224, BR-225, BR-338
**Blocked on an open architecture decision** (Architecture §15: notification provider selection) for final delivery — build against IT-F12's provider-agnostic outbox interface now; swap in a real provider once that decision is made, without touching this task's domain logic.
- Domain: retry-with-backoff on `notification_requests.status = failed`
- Tests: integration (a disabled preference row suppresses delivery — `status = suppressed`, never sent)

### IT-55 — F-012.2 Gameweek reminder notifications
**Depends on:** IT-53, IT-31 · **Migration:** V011 · **API:** none (background job)
**Traces to:** BR-152, BR-338
- Domain: fires at `RosterLockDeadline − GameweekReminderLeadTime` (`SeasonConfiguration`, IT-08)
- Tests: integration (reminder suppressed for a disabled preference)

### IT-56 — F-012.3 Weekly score/standings notifications
**Depends on:** IT-53, IT-33–IT-37, IT-42–IT-44 · **Migration:** V011 · **API:** none (background job)
**Traces to:** BR-153, BR-154, BR-338
- Domain: fires from `GameweekScore`/`LeagueStanding` recalculation events
- Tests: integration (same suppression check as IT-55)

### IT-57 — F-013.2 Retired-user historical display
**Depends on:** IT-50 · **Migration:** V002 (reads `username_history`) · **API:** reflected across every historical read endpoint
**Traces to:** BR-014, BR-175, BR-272, BR-326
- Domain: none new — resolves a historical record's display username via `UsernameHistory` (IT-14) rather than joining `users.username` directly, so a retired (or since-renamed) User's historical rows never break
- Tests: integration (a retired User's Season-1 standings row still shows their Season-1-era username after retirement)

---

## 9. Milestone Summary

| Milestone | Tasks | Feature count | Blocks / blocked by |
|---|---|---|---|
| Foundational | IT-F01–IT-F14 | — (14 cross-cutting tasks) | Blocks everything |
| 1 — Foundation | IT-01–IT-22 | 22 | Blocks all of 2a, 3, 4 |
| 2a — Initial Draft | IT-23–IT-28 | 6 | Blocks 3 (rosters need a squad) |
| 3 — Weekly Gameplay | IT-29–IT-44 | 16 | Blocks 2b (Secondary Draft needs Standings) and most of 4 |
| 2b — Secondary/Replacement Draft | IT-45–IT-49 | 5 | Blocked by 3 |
| 4 — Operations | IT-50–IT-57 | 8 | Blocked by 2b and 3 (varies per task) |

57 feature tasks + 14 foundational tasks = 71 total. This is the complete decomposition of the Backlog's entire feature list (all 57 `F-###.#` entries) — nothing from `03-epics-and-backlog/` was left unscheduled.

---

## Version History

### Version 1.0

Initial implementation task breakdown, decomposing all 57 features from Epic and Feature Backlog v1.10 into 71 total engineering tasks (57 feature tasks + 14 foundational/cross-cutting tasks), resequenced into a single dependency-correct build order verified against the Backlog's own "Depends On" column (§2 documents the four features this moves from the Backlog's own numbering, and why). Every task cites its Architecture §6 aggregate, its `06-database-migrations/` `V0##` file, its `05-api-specification/` `operationId`(s), its `BR-###`/`AP-###` traceability, and the `07-testing-strategy/` test-case(s) — including `db-tests/` assertions already proven passing — that verify it. Defines a generic Definition of Done (§3) applying to every task rather than repeating it 57 times.
