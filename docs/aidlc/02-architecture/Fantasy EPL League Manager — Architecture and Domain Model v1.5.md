# Fantasy EPL League Manager
## Architecture and Domain Model — Version 1.5

**Document Status:** Baseline Architecture
**Version:** 1.5
**Inputs:**
- Business Requirements Document v1.7 (`Fantasy EPL League Manager — Business Requirements Document v1.7.md`) — authoritative business rules (BR-001…BR-308)
- AIDLC Domain & Requirements Specification v1.0 (`EPL_Fantasy_AIDLC_Domain_Requirements_Specification_v1.0.docx`) — prior domain decomposition (BR-DOM-001…BR-DOM-043, UC-001…UC-026, EPIC-001…EPIC-013)

**Purpose:** Establish the system architecture, bounded-context/module structure, aggregate-level domain model, data architecture, and API architecture that the BRD and domain specification are generated into. This is the artifact BR-183–BR-190, AP-001–AP-010, and Domain Spec §25 ("Recommended Next AIDLC Artifacts" item 4) call for.

**Audience:** Architects, developers, and subsequent AIDLC generation steps (API spec, database migrations, feature specs, tests, implementation tasks).

---

# 1. Scope and Relationship to Prior Artifacts

This document does not restate business rules; it cites them by ID (`BR-###`) and adds the structural decisions needed to implement them. Where the BRD (v1.7) and the earlier Domain Specification (v1.0) differ, **the BRD is authoritative** (it is the later, more detailed baseline — see BRD §60, "Final Baseline"). Section 2 reconciles the two.

This document covers:

- Technology and hosting decisions (ADR-001–ADR-010)
- System, module, and layering architecture
- Bounded-context-to-module mapping
- Aggregate-level domain model (entities, invariants, domain events, state machines)
- Logical data architecture
- API architecture and versioning
- External FPL integration architecture
- Security/authorization architecture
- Concurrency, audit, and notification architecture

This document does **not** cover: physical DDL/migration scripts, OpenAPI schemas, UI/UX design, or implementation task breakdown. Those are later AIDLC artifacts (Section 16).

---

# 2. Reconciliation with the Domain Specification v1.0

The Domain Specification's §24 ("Open Items for Next AIDLC Phase") listed 14 unresolved questions. Cross-checking against BRD v1.3:

| Domain Spec Open Item | Status | Resolution |
|---|---|---|
| Exact positional minimums for the 15-player roster | **Still open** | BRD BR-041 requires minimums to exist but does not enumerate them. Carried forward — see Section 15. |
| Exact captain multiplier/scoring treatment | **Resolved** | BRD BR-047: use the official FPL captain multiplier directly (no application-defined multiplier). |
| Win/draw/loss league-point allocation | **Resolved** | BRD BR-115–BR-117: Win = 3, Draw = 1, Loss = 0. |
| Authoritative EPL/FPL tie-break mapping and versioning | **Still open** | BRD BR-122 requires it to be configurable, not hard-coded, but does not enumerate the current EPL rule text. Carried forward. |
| Secondary-draft calendar algorithm | **Partially resolved** | BRD BR-069: prefer the day after the transfer window closes; fall back to next practical day (Tuesday given as an example). No formal algorithm — carried forward as a configuration/policy, not hard logic. |
| Number/cutoff rules for replacement selections | **Still open** | BRD does not cap replacement selections. Carried forward. |
| Rounding behavior for the Fantasy Goals Against integer average | **Resolved** | BRD BR-087–BR-088 example (7/5 = 1.4 → 1) establishes **truncation toward zero**, not rounding. |
| Double-gameweek treatment | **Still open** | Not addressed in BRD. Carried forward. |
| Administrator roles beyond primary League Administrator | **Still open** | BRD BR-025 defines League Administrator powers but no secondary admin roles. Carried forward. |
| Notification providers/delivery requirements | **Still open** | BRD BR-150–BR-155 define preferences and channels (email/text) but not vendor/provider. Carried forward as an infrastructure choice (Section 12.2). |
| Account recovery, password policy, MFA | **Still open** | BRD BR-158 requires adaptive password hashing only. Carried forward. |
| Username uniqueness scope (global vs. per-LeagueSeason) | **Resolved** | BRD BR-004: usernames are globally unique among active users. |
| Icon catalog / custom uploads | **Resolved** | BRD BR-011 and the Non-Goals section (§2.2): icons come only from an application-controlled set; no arbitrary user uploads. |
| Official data API terms/constraints | **Still open** | Legal/commercial research item, not an architecture decision. Carried forward. |

Remaining open items are restated in Section 15 as decisions the *next* phase (feature specs) must close before affected features reach implementation readiness (per Domain Spec §26, "Open Decisions").

The Domain Specification's bounded contexts, aggregate roots, and entity list (its §4–§7) are adopted as the starting point for Section 6, with names aligned to the BRD's terminology (e.g., BRD's `Season` = Domain Spec's `LeagueSeason`; this document uses **`Season`** to match the BRD, scoped per League).

---

# 3. Architecture Decision Records

## ADR-001 — Modular Monolith, Not Microservices

**Decision:** The system is built as a single deployable modular monolith with clearly separated modules corresponding to the bounded contexts in Section 5.

**Rationale:** BRD §52 explicitly recommends this ("a modular monolith is likely preferable to prematurely introducing microservices"). The initial user base is small (private leagues), and module boundaries preserve the option to extract services later without a rewrite.

**Consequence:** Cross-module calls happen in-process through application-service interfaces, not network calls. Module boundaries are enforced by project/assembly references, not runtime infrastructure.

## ADR-002 — Backend Platform: .NET 8 / C# / ASP.NET Core

**Decision:** The backend is implemented in C# on ASP.NET Core (.NET 8 LTS or later LTS at build time), using EF Core as the ORM.

**Rationale:** Strong static typing and a mature ecosystem for domain-driven, invariant-heavy modeling (this domain has many cross-entity invariants — Section 56 of the BRD). EF Core's change tracking and migrations fit the modular-monolith/PostgreSQL combination well.

## ADR-003 — Database: PostgreSQL

**Decision:** PostgreSQL is the system of record for all transactional data.

**Rationale:** Open-source, strong constraint/window-function support (useful for standings/tie-break queries and partial-unique constraints such as "one active FantasyTeam per LeagueMembership+Season" — BR-193), no per-core licensing cost. EF Core's Npgsql provider is production-mature.

## ADR-004 — Mobile Architecture Deferred

**Decision:** No mobile framework (React Native, Flutter, or separate native apps) is selected in this document.

**Rationale:** BRD BR-189/DEC-022 explicitly defer this to a later architecture decision. The API (Section 9) is designed client-agnostic (BR-186) and versioned (BR-190) so the decision can be made independently later without redesigning the backend.

## ADR-005 — Hosting/Deployment: Cloud-Agnostic

**Decision:** This document does not commit to AWS, Azure, or GCP. Infrastructure needs are described generically: a managed PostgreSQL instance, a container or app-hosting runtime for the ASP.NET Core API, a message/queue mechanism for asynchronous notification and external-sync jobs (Section 12), and object storage only if/when file assets are introduced (none are required today — profile icons are an application-controlled, pre-defined set per BR-011, not user uploads).

**Rationale:** Avoids locking the project to a vendor before a hosting decision is explicitly made; keeps the architecture portable.

## ADR-006 — Authentication: Application-Managed Initially, IdP-Ready

**Decision:** The application manages its own authentication (credential storage, adaptive password hashing, token issuance) initially (BR-156/DEC-023), but the authentication module is isolated behind an interface so an external Identity Provider (OIDC/SAML) can be introduced later (BR-157) without touching the rest of the domain.

**Consequence:** `IAuthenticationService` and `ICurrentUserAccessor` abstractions are the only points where the rest of the system depends on "who is logged in." No module reads cookies/JWTs directly.

## ADR-007 — Authorization Model: Claims + Object-Level Checks

**Decision:** Three coarse roles exist — `User` (default), `LeagueAdministrator` (a property of a `LeagueMembership`, not a global role), and `SystemAdministrator` (a platform-level, non-League-scoped role — BR-300, BRD v1.6) — plus mandatory object-level authorization checks on every API operation that touches a specific League, FantasyTeam, roster, or draft (BR-163, BR-254, AP-002).

`SystemAdministrator` is not exposed through any League-facing screen or API and is not granted by any self-service flow — it is provisioned out-of-band (e.g., direct data change by an operator) and used only for platform-support actions such as reactivating a retired `User` account (BR-301). It should not be treated as a general escalation path; if future needs expand its scope, that is a new architecture decision, not an assumption baked in here.

**Rationale:** "League Administrator" is per-league (BR-024), not a system-wide role, so it must be modeled as an attribute of `LeagueMembership`, checked per request against the specific `LeagueId`/`FantasyTeamId` in the URL/body — never inferred from a cached global claim alone.

## ADR-008 — Standings Tie-Break Ruleset Is Configuration, Not Code Branching

**Decision:** The tie-break hierarchy (BRD §21, BR-119–BR-125) is implemented as an ordered list of pluggable comparator strategies loaded from configuration/database, not a hard-coded `if/else` chain.

**Rationale:** BRD BR-122 and AP-006 require this explicitly ("configurable rather than hard-coded... regulatory changes can be accommodated"). Each tier is a named `IStandingsTieBreakRule` evaluated in a versioned, persisted sequence per Season (Domain Spec §9 note: "should be versioned").

**Resolved tier sequence (BRD v1.4 BR-280):** League Points → Fantasy Goal Difference → Fantasy Goals For → Head-to-Head League Points (between the tied FantasyTeams only) → Captain Points → Season Goal Prediction → Random fallback. The official EPL's away-goals and neutral-venue-playoff provisions do not translate to a private league and are intentionally not implemented — the pipeline falls through directly from Head-to-Head League Points to Captain Points when still tied.

**Configuration (BRD v1.5 BR-291):** the ruleset identifier is stored as `SeasonConfiguration.TieBreakRulesetVersion`, following the same League-default/Season-override/lock-at-Season-start pattern as every other configurable parameter (Section 6.2) — it is not a special case relative to squad size, roster size, etc.

## ADR-009 — External FPL Data Behind an Anti-Corruption Layer

**Decision:** All official FPL/EPL data enters through a dedicated `PlayerDataIntegration` module that translates external API/CSV shapes into internal domain models. No other module references external DTOs (BR-227–BR-234, AP-007, Domain Spec §18).

**Rationale:** Isolates the domain from upstream schema changes and allows idempotent, replayable synchronization (AP-008) plus reconciliation of corrected historical data (BR-234) without touching scoring/roster logic.

## ADR-010 — Soft Delete / Retirement, Never Physical Delete, for Competitive Entities

**Decision:** `User`, `LeagueMembership`, and any entity that participates in historical competitive results are never physically deleted. They carry a status/retirement timestamp instead (BR-013, BR-175, AP-004).

**Rationale:** Ten-year historical retention (BR-174) and referential integrity of historical records (BR-012, BR-257) require immutable identifiers to keep resolving even after an account is "removed."

## ADR-011 — League/Season Configuration as a First-Class Value Object, Not Scattered Literals

**Decision:** Every parameter listed in BRD v1.5 BR-291 (squad size, roster size, positional minimums, draft timer, secondary draft size, secondary-draft scheduling offset, roster-lock offset, league point values, invitation expiration, replacement cap) is read at runtime from a `SeasonConfiguration` value object (Section 6.2), never hard-coded as a literal in domain or application code.

**Rationale:** BRD BR-290 requires these to be configurable per League and independently overridable per Season. Reading every one of these values from a single resolved configuration object — rather than scattering `if (count == 25)`-style literals across the codebase — is what makes BR-292's default/override behavior and BR-293's locking rule enforceable in one place instead of dozens.

**Consequence:** Any domain or application code that previously assumed a fixed number (e.g., "25-player squad," "five-minute timer") is written against `SeasonConfiguration` instead. Section 6 below reflects this — aggregates that depend on a configurable parameter reference `SeasonConfiguration` rather than embedding the number directly.

## ADR-012 — Deadline Expiry Detection via Scheduled Background Sweep

**Decision:** Both draft-pick timeout detection (BR-282) and Gameweek roster-lock detection (BR-093/BR-094) are implemented as a single shared, short-interval scheduled background sweep (e.g., every 15–30 seconds) that scans for aggregates whose deadline has passed and applies the appropriate transition (skip-and-requeue for a draft pick; lock, or lock-with-carry-forward, for a roster) — not a lazy check performed only when a user happens to make a request.

**Rationale:** A lazy, request-triggered check would leave stale state visible on read-only screens (e.g., another League member viewing a draft board, or a Standings page) until *someone* happens to hit an endpoint that triggers the check, which is unpredictable and depends on unrelated user traffic. A shared scheduled sweep keeps both mechanisms — which are structurally identical ("has a deadline passed, and if so, what fallback applies?") — consistent and predictable, and lets read-only queries always reflect current, correct state without a request-time side effect.

**Consequence:** One `IDeadlineSweepJob`-style background service, parameterized per aggregate type (`Draft`, `GameweekRoster`), rather than two independently reinvented mechanisms.

---

# 4. System Architecture Overview

```text
                         ┌─────────────────────────────┐
                         │   Clients (untrusted)        │
                         │  Responsive Web  |  (Mobile — deferred, ADR-004) │
                         └───────────────┬──────────────┘
                                         │ HTTPS (BR-164), versioned REST (/api/v1)
                         ┌───────────────▼──────────────┐
                         │   API Layer (ASP.NET Core)    │
                         │  - AuthN/AuthZ middleware      │
                         │  - Request validation (BR-165) │
                         │  - Versioning, error model      │
                         └───────────────┬──────────────┘
                                         │
                         ┌───────────────▼──────────────┐
                         │  Application Service Layer     │
                         │  (one service set per bounded  │
                         │   context — Section 5)         │
                         └───────────────┬──────────────┘
                                         │
                         ┌───────────────▼──────────────┐
                         │  Domain Layer                  │
                         │  Aggregates, invariants,       │
                         │  domain events, tie-break       │
                         │  strategies                     │
                         └───────────────┬──────────────┘
                                         │
                         ┌───────────────▼──────────────┐
                         │  Infrastructure Layer           │
                         │  EF Core repositories,          │
                         │  PostgreSQL, outbox/queue,       │
                         │  FPL integration client          │
                         └──────────────────────────────┘
```

This is the four-layer separation BR-185 requires: Presentation / Application / Domain / Infrastructure. AP-001 ("Domain-First Design") and AP-002 ("API as Security Boundary") are enforced by construction: controllers in the API layer contain no business rules — they call application services, which orchestrate domain aggregates.

---

# 5. Bounded Contexts and Module Map

Adopting the Domain Specification's 11 bounded contexts (its §4), each maps to one .NET project/namespace within the monolith:

| Bounded Context | Module (namespace) | Owns (aggregate roots) |
|---|---|---|
| Identity & User | `EplFantasy.Identity` | User, UserProfile, ProfileIcon |
| League & Season | `EplFantasy.Leagues` | League, LeagueMembership, Season, Invitation |
| Fantasy Team | `EplFantasy.FantasyTeams` | FantasyTeam, Squad (SquadPlayer) |
| Player & EPL Data | `EplFantasy.PlayerData` | Player, Club, Fixture, Gameweek (reference data; see ADR-009) |
| Draft Management | `EplFantasy.Drafts` | Draft, DraftSelection |
| Roster Management | `EplFantasy.Rosters` | GameweekRoster, RosterPlayer, CaptainSelection |
| Scoring | `EplFantasy.Scoring` | PlayerPerformance, GameweekScore, ScoreOverride, FantasyGoals |
| Competition | `EplFantasy.Competition` | HeadToHeadMatch, LeagueStanding, SeasonGoalPrediction |
| Corrections & Administration | `EplFantasy.Administration` | AdministrativeAction (audit aggregate; cross-cutting, Section 12.1) |
| Notifications | `EplFantasy.Notifications` | NotificationPreference, NotificationRequest |
| Reporting & History | `EplFantasy.Reporting` | Read-model projections only — no aggregate roots of its own |

**Module dependency rule:** Dependencies point inward/downward per the table order above (e.g., `Rosters` may depend on `FantasyTeams` and `PlayerData`, but `PlayerData` never depends on `Rosters`). `Administration` and `Reporting` may read from any module (they are cross-cutting/read-oriented) but no module depends on them. This is enforced with architecture/dependency tests (e.g., NetArchTest) in CI, not just convention.

---

# 6. Domain Model

Each aggregate below lists its identity, key attributes/types, invariants (mapped to BR-### and the BRD's numbered Invariants in §56), and domain events it raises. Pseudocode uses C# record/class shape for clarity, not final implementation.

## 6.1 Identity Context

### User (Aggregate Root)

```text
User
----
UserId          : Guid            (immutable, BR-002)
Username        : string           (unique among active users, BR-004)
Email           : string           (private, BR-015 — never returned by league-facing APIs)
PasswordHash    : string           (adaptive hash, BR-158)
Status          : UserStatus       (Active | Retired)
IsSystemAdministrator : bool = false  (BR-300, BRD v1.6 — platform-level, not League-scoped; never settable via any self-service API)
CreatedAt       : DateTimeOffset
UpdatedAt       : DateTimeOffset
RetiredAt       : DateTimeOffset?
```

Invariants: BR-004 (uniqueness enforced at the persistence layer — a unique index on `Username` filtered to `Status = Active`, so a retired user's username is immediately available for reuse by any User per BR-298, BRD v1.6); BR-013 (retirement is a status transition, never a row delete).

Reactivation of a `Status = Retired` user (BR-301, BRD v1.6) is only callable by a `SystemAdministrator`-authorized request; if the prior `Username` has since been taken by another active User, reactivation requires assigning a new one as part of the same operation.

Domain events: `UserRegistered`, `UsernameChanged`, `UserRetired`, `UserReactivated`.

### UserProfile (Aggregate Root, 1:1 with User)

```text
UserProfile
-----------
UserId          : Guid  (PK/FK)
DefaultIconId   : Guid  (FK → ProfileIcon, BR-006)
NotificationPreferencesSummary : (owned by Notifications context; see 6.9)
CreatedAt / UpdatedAt
```

### ProfileIcon (Reference/Value entity, application-controlled)

```text
ProfileIcon
-----------
ProfileIconId   : Guid
Name            : string
AssetIdentifier : string   (server-controlled path, BR-011 — never client-supplied)
IsActive        : bool
SortOrder       : int
```

## 6.2 League & Season Context

### League (Aggregate Root)

```text
League
------
LeagueId        : Guid
Name            : string
Description     : string
Status          : LeagueStatus     (Active | Archived)
CreatedByMembershipId : Guid       (the creator becomes Administrator, BR-024)
CreatedAt       : DateTimeOffset
```

### LeagueMembership (Aggregate Root)

```text
LeagueMembership
----------------
LeagueMembershipId : Guid
LeagueId           : Guid (FK)
UserId             : Guid (FK)
IsAdministrator    : bool    (BR-024/BR-025 — per-league, not global; ADR-007)
LeagueIconId       : Guid?   (FK → ProfileIcon, overrides default per BR-008)
Status             : MembershipStatus (Invited | Active | Left)
JoinedAt / LeftAt
```

Invariants: unique `(LeagueId, UserId)` for non-`Left` memberships (supports BR-020 — a user may have only one active membership per league, but may rejoin a league across seasons); at most one `LeagueMembership` per `League` may have `IsAdministrator = true` (BR-283, BRD v1.4 — exactly one League Administrator per League for the initial release; enforced by a partial unique index on `(LeagueId) WHERE is_administrator`).

### Season (Aggregate Root — BRD's "Season", Domain Spec's "LeagueSeason")

```text
Season
------
SeasonId            : Guid
LeagueId            : Guid (FK)
EplSeasonIdentifier : string   (e.g. "2026/27")
Status              : SeasonStatus (Setup | DraftInProgress | InSeason | Completed)
StartDate / EndDate
```

### LeagueConfiguration (Value object owned by League — BRD v1.5 BR-290–BR-292)

```text
LeagueConfiguration
-------------------
LeagueId                      : Guid (FK, 1:1 with League)
InitialSquadSize               : int = 25                (BR-034/BR-052/BR-197)
WeeklyRosterSize                : int = 15                (BR-037/BR-196)
PositionalMinimums              : { Gk: int = 1, Def: int = 3, Mid: int = 2, Fwd: int = 1 }  (BR-279)
DraftTimerSecondsByType          : { Initial: int = 300, Secondary: int = 300, Replacement: int = 300 }  (BR-057)
SecondaryDraftSelectionsPerTeam  : int = 5                (BR-060)
SecondaryDraftSchedulingOffsetDays : int = 1              (BR-069/BR-281)
GameweekRosterLockOffsetBeforeKickoff : TimeSpan = 1 hour  (BR-093)
LeaguePoints                    : { Win: int = 3, Draw: int = 1, Loss: int = 0 }  (BR-115–117)
InvitationExpiration             : TimeSpan = 7 days       (BR-029)
ReplacementSelectionCap          : int? = null (null = uncapped)  (BR-287)
TieBreakRulesetVersion           : string                 (ADR-008 default ruleset)
UpdatedAt / UpdatedByMembershipId
```

This is the League-level **default** set (BR-292). It is mutable at any time by the League Administrator; changing it only affects Seasons created afterward or Seasons that have not yet locked the relevant parameter (BR-293/BR-294) — it never rewrites an already-locked `SeasonConfiguration`.

### SeasonConfiguration (Value object owned by Season — BRD v1.5 BR-293–BR-294)

```text
SeasonConfiguration
-------------------
SeasonId                       : Guid (FK, 1:1 with Season)
(same fields as LeagueConfiguration, one column/field per configurable parameter)
LockedFields                   : Set<string>   (which fields have passed their BR-291 "Locks At" point and can no longer change for this Season)
```

`SeasonConfiguration` is created by copying `LeagueConfiguration` at Season creation (BR-292's default-inheritance behavior) and can be overridden per field by the League Administrator up until that field's lock point (BR-291/BR-293). Every aggregate below that previously referenced a literal constant instead reads the corresponding field from this Season's `SeasonConfiguration` (ADR-011). A completed Season's `SeasonConfiguration` is retained permanently alongside its historical results (BR-296) and is never deleted or overwritten after `Season.Status = Completed`.

### Invitation (Aggregate Root)

```text
Invitation
----------
InvitationId  : Guid
LeagueId      : Guid (FK)
SeasonId      : Guid? (FK — invitations are resent per season, BR-033)
Token         : string   (opaque, single-use)
Destination   : string   (email or phone — delivery channel, BR-028)
CreatedAt / ExpiresAt   (ExpiresAt = CreatedAt + LeagueConfiguration.InvitationExpiration, default 7 days, BR-029; a change to the League's configured expiration applies only to Invitations issued afterward, BR-293)
Status        : InvitationStatus (Pending | Accepted | Expired | Revoked)
```

### LeagueMessage (Aggregate Root — added in v1.5 to support F-003.6, BR-221–BR-223)

```text
LeagueMessage
-------------
LeagueMessageId    : Guid
LeagueId           : Guid (FK)
AuthorMembershipId : Guid (FK → LeagueMembership — must be the Administrator, BR-221)
Body               : string    (user-supplied; output-encoded on every render, BR-167)
PublishedAt        : DateTimeOffset
```

Invariant: only the League's Administrator `LeagueMembership` may author a `LeagueMessage` (BR-221, BR-162). Retained under the same historical-retention policy as other league data (BR-223, BR-174) — never purged.

Domain events: `LeagueCreated`, `MembershipJoined`, `InvitationIssued`, `InvitationAccepted`, `InvitationExpired`, `LeagueMessagePublished`.

## 6.3 Fantasy Team Context

### FantasyTeam (Aggregate Root)

```text
FantasyTeam
-----------
FantasyTeamId      : Guid
LeagueMembershipId : Guid (FK)
SeasonId           : Guid (FK)
Status             : FantasyTeamStatus (Active | Withdrawn)
CreatedAt / UpdatedAt
```

Invariant (BR-019, BR-193, Invariant 2): unique `(LeagueMembershipId, SeasonId)` — enforced via a database unique constraint, not application logic alone (AP-009-style defense in depth).

### Squad / SquadPlayer

```text
SquadPlayer
-----------
SquadPlayerId     : Guid
FantasyTeamId     : Guid (FK)
PlayerId          : Guid (FK → Player, PlayerData context)
AcquisitionType   : AcquisitionType (InitialDraft | SecondaryDraft | Replacement)  BR-264
AcquiredAt        : DateTimeOffset
ReleasedAt        : DateTimeOffset?
IsCurrentlyOwned  : bool
ReplacementEligibleAt : DateTimeOffset?   (set when Administrator marks eligible, BR-065)
```

Invariant (BR-035, BR-191, BR-262, Invariant 1): a `Player` may have at most one `SquadPlayer` row with `IsCurrentlyOwned = true` **per (League, Season)** — enforced by a partial unique index on `(PlayerId, SeasonId) WHERE IsCurrentlyOwned`, scoped through a `SeasonId` denormalized onto `SquadPlayer` for indexability.

Replacement selections (`AcquisitionType = Replacement`) default to no per-season cap (BR-287, BRD v1.4): each `PlayerMarkedReplacementEligible` event (Section 7) generates exactly one replacement-selection opportunity for the owning `FantasyTeam`. A League may instead configure `SeasonConfiguration.ReplacementSelectionCap` to a fixed number (BRD v1.5 BR-291); once that cap is reached for a `FantasyTeam`, further eligibility events do not generate additional opportunities for it.

A granted replacement-selection opportunity never expires on its own (BR-307, BRD v1.7) — it remains usable until the FantasyTeam chooses to spend it, with no deadline-tracking field required.

`PlayerMarkedReplacementEligible` fires differently depending on the eligibility path (BR-308, BRD v1.7): for an EPL-exit (BR-066), it fires automatically the moment the `PlayerData` sync (Section 10) confirms the departure — no League Administrator action is involved. For a season-ending-injury declaration (BR-067, BR-068), it fires only when a League Administrator explicitly records that determination (F-011.2), since that judgment cannot be automated from official data.

Domain events: `PlayerAcquired`, `PlayerReleased`, `PlayerMarkedReplacementEligible`.

## 6.4 Draft Context

### Draft (Aggregate Root)

```text
Draft
-----
DraftId              : Guid
SeasonId             : Guid (FK)
DraftType            : DraftType (Initial | Secondary | Replacement)
Status               : DraftStatus (Scheduled | InProgress | Paused | Completed)
DraftOrder           : Guid[]        (FantasyTeamId sequence, randomized for Initial — BR-054;
                                       standings-derived for Secondary — BR-056)
CurrentRound         : int
CurrentPickIndex     : int
TimerSeconds         : int           (from this Season's SeasonConfiguration.DraftTimerSecondsByType[DraftType], default 300 — BR-057, ADR-011)
CurrentPickDeadline  : DateTimeOffset?
StandingsSnapshotTakenAt : DateTimeOffset?   (BR-136, BR-138 — captured once, immutable after)
PendingMakeupPicks   : FantasyTeamId[]        (BR-282 — skipped picks queued for after the final round)
```

**State machine:**

```text
Scheduled ──start──▶ InProgress ──(admin pause)──▶ Paused ──(resume)──▶ InProgress
InProgress ──(all picks made, including makeup picks)──▶ Completed
```

Invariant (BR-302, BRD v1.7): a `Draft` with `DraftType = Initial` may not transition from `Scheduled` to `InProgress` if fewer than two `FantasyTeam` records participate in it.

**Pick timeout (BRD v1.4 BR-282):** if a `FantasyTeam`'s timer expires with no administrator extension (BR-058), the current pick is skipped — no `DraftSelection` is recorded for that turn — and the pick is enqueued onto a `PendingMakeupPicks` list carried on the `Draft` aggregate. Makeup picks are inserted, in the order they were skipped, immediately after the final regularly scheduled round, so every `FantasyTeam` still reaches its required selection count (BR-052 for the initial draft, BR-060 for the secondary draft) before `Draft.Status` transitions to `Completed`.

**Secondary draft scheduling (BRD v1.4 BR-281, offset configurable per BRD v1.5 BR-291):** a `Season`-level application service (not the `Draft` aggregate itself, since no `Draft` exists yet at this point) determines the secondary draft's date: target the transfer-window close date plus this Season's `SeasonConfiguration.SecondaryDraftSchedulingOffsetDays` (default 1 day), then advance one day at a time while the `PlayerData` context's fixture calendar shows any scheduled EPL fixture on the candidate day, stopping at the first fixture-free day. This produces a proposed `Draft.StartTime`; the League Administrator may override it before the draft is created.

Invariant (AP-009, Invariant — atomic pick): a `DraftSelection` insert and the corresponding `SquadPlayer` creation happen in one database transaction, guarded by the same partial-unique-ownership index as 6.3, so two concurrent picks for the same player cannot both succeed — the loser receives a domain-level `PlayerAlreadyOwnedException`, not a raw constraint violation.

### DraftSelection (Entity within Draft aggregate)

```text
DraftSelection
--------------
DraftSelectionId : Guid
DraftId          : Guid (FK)
FantasyTeamId    : Guid (FK)
PlayerId         : Guid (FK)
Round            : int
PickNumber       : int
SelectedAt       : DateTimeOffset
IsMakeupPick     : bool   (true if this selection was queued after a timeout, BR-282)
```

Domain events: `DraftStarted`, `DraftTimerExtended` (BR-058), `PlayerDrafted`, `DraftCompleted`.

## 6.5 Roster Management Context

### GameweekRoster (Aggregate Root)

```text
GameweekRoster
--------------
GameweekRosterId : Guid
FantasyTeamId    : Guid (FK)
GameweekId       : Guid (FK → Gameweek, PlayerData context)
Status           : RosterStatus (Draft | Submitted | Locked | Scored)
SubmittedAt      : DateTimeOffset?
LockedAt         : DateTimeOffset?   (= Gameweek.RosterLockDeadline, BR-093)
CaptainPlayerId  : Guid?             (must be in RosterPlayers, BR-046, Invariant 5)
```

**State machine:**

```text
Draft ──submit (exactly 15 valid players + captain, BR-196/BR-041)──▶ Submitted
Draft ──deadline reached, no submission──▶ Submitted  (carry-forward fallback, BR-305, BRD v1.7 — see below)
        ──▶ Locked                                     (BR-094, automatic, time-driven, detected by ADR-012's sweep)
Locked ──official scores finalized──▶ Scored       (BR-079)
Locked ──administrator correction (BR-097)──▶ Locked  (self-transition; audited, Section 12.1)
```

**Carry-forward fallback (BR-305, BRD v1.7):** when the ADR-012 deadline sweep finds a `GameweekRoster` still in `Draft` status past its `LockedAt` deadline, it does not simply lock an empty roster. Instead, it looks up the FantasyTeam's most recently `Locked` `GameweekRoster` (if any) and copies its `RosterPlayer` set — including which player `IsCaptain` — into this Gameweek's roster before locking it. Two edge cases:
- If no prior-Gameweek roster exists for this FantasyTeam this Season (i.e., this is its first Gameweek), the roster locks empty, and the FantasyTeam's `GameweekScore.FantasyPoints` for that Gameweek is zero.
- If a carried-forward player is no longer a currently-owned `SquadPlayer` for this FantasyTeam (e.g., released via a replacement selection since the prior Gameweek), that player is omitted from the copy. The resulting roster locks as-is, even if it now has fewer than `SeasonConfiguration.WeeklyRosterSize` players or no longer satisfies `PositionalMinimums` — the size/positional invariants in F-007.1 apply to *user* submissions, not to this automatic continuation.

### RosterPlayer (Entity within GameweekRoster)

```text
RosterPlayer
------------
GameweekRosterId : Guid (FK)
PlayerId         : Guid (FK)
IsCaptain        : bool
SelectionRole    : SelectionRole   (computed post-scoring: StartingXI | Bench — BR-044, not chosen by the user)
```

Invariant (BR-194, Invariant 6): every `RosterPlayer.PlayerId` must be a currently-owned `SquadPlayer` of the same `FantasyTeam`.

Invariant (BR-279, BRD v1.4; configurable per BRD v1.5 BR-291): a roster may transition from `Draft` to `Submitted` only if it contains exactly `SeasonConfiguration.WeeklyRosterSize` players (default 15) satisfying `SeasonConfiguration.PositionalMinimums` (default at least 1 Goalkeeper, 3 Defenders, 2 Midfielders, and 1 Forward). As per BR-042, no positional maximum is enforced.

Precondition (BR-299, BRD v1.6): if this is the `FantasyTeam`'s first `GameweekRoster` submission of the Season and no `SeasonGoalPrediction` (Section 6.7) has been recorded for it, the submission is rejected until one is provided.

**Starting XI selection (BR-303, BR-304, BRD v1.7):** `SelectionRole` is computed by the Scoring context (Section 6.6), not this aggregate, but the rule lives here because it governs `RosterPlayer`: (1) the Captain's point value is multiplied by the captain multiplier (BR-047) *before* the 15 players are ranked — the Captain competes for a `StartingXI` spot on their multiplied value, not their raw value (BR-304); (2) the top 11 by that (possibly multiplied) value are marked `StartingXI`, the remaining 4 `Bench` (BR-044); (3) a tie at the 11th/12th-place boundary is broken by a deterministic, advantage-free criterion — a stable `PlayerId` ordering — never a random or non-reproducible tiebreak (BR-303).

Domain events: `RosterSubmitted`, `RosterLocked`, `RosterCarriedForward`, `CaptainSelected`, `RosterAdministrativelyCorrected`.

## 6.6 Scoring Context

### PlayerPerformance (Aggregate Root — raw authoritative data, Domain Spec §12)

```text
PlayerPerformance
-----------------
PlayerPerformanceId : Guid
GameweekId          : Guid (FK)
PlayerId            : Guid (FK)
FantasyPoints        : int
Goals                : int
GoalsConceded        : int
OwnGoals             : int
Source               : DataSource (OfficialFpl | Manual)
IsOfficial           : bool
RetrievedAt          : DateTimeOffset
```

This is kept **separate** from `GameweekScore` (below) so that raw official data is never overwritten by derived/competition calculations — supporting reproducibility (Domain Spec §21, "Recoverability").

### GameweekScore (derived, one per FantasyTeam per Gameweek)

```text
GameweekScore
-------------
GameweekScoreId       : Guid
FantasyTeamId         : Guid (FK)
GameweekId            : Guid (FK)
FantasyPoints         : int      (sum over StartingXI + captain multiplier, BR-044/BR-047)
CaptainPoints         : int
FantasyGoalsFor       : int      (BR-080)
FantasyGoalsAgainst   : int      (BR-086, truncated integer average, BR-087)
FantasyGoalDifference : int      (BR-089)
CalculatedAt          : DateTimeOffset
RecalculatedCount     : int      (incremented on every override-driven recompute, supports BR-148)
```

### ScoreOverride (Aggregate Root — audit-critical)

```text
ScoreOverride
-------------
ScoreOverrideId       : Guid
PlayerPerformanceId   : Guid (FK)
AdministratorMembershipId : Guid (FK → LeagueMembership)
OriginalValue         : jsonb    (snapshot of the overridden fields)
OverrideValue         : jsonb
Reason                : string?
CreatedAt             : DateTimeOffset
UndoneAt              : DateTimeOffset?
IsActive              : bool     (computed: UndoneAt is null)
```

Invariant (BR-140–BR-145, Invariant 12, ADR-009): precedence is `Active ScoreOverride > Official PlayerPerformance > Application Calculation`. This is implemented as a single `IAuthoritativeValueResolver` used everywhere a player statistic is read for scoring — never inlined per call site — so precedence cannot drift between modules.

Domain events: `ScoreCalculated`, `ScoreOverrideApplied`, `ScoreOverrideUndone`, `ScoreRecalculated`.

## 6.7 Competition Context

### HeadToHeadMatch (Aggregate Root)

```text
HeadToHeadMatch
---------------
MatchId           : Guid
SeasonId          : Guid (FK)
GameweekId        : Guid (FK)
HomeFantasyTeamId : Guid (FK)
AwayFantasyTeamId : Guid (FK)
HomeScore         : int?
AwayScore         : int?
Result            : MatchResult? (HomeWin | AwayWin | Draw)
LeaguePointsHome  : int?          (from this Season's SeasonConfiguration.LeaguePoints, default Win=3/Draw=1/Loss=0 — BR-115–117, configurable per BRD v1.5 BR-291)
LeaguePointsAway  : int?
```

**Bye weeks (BR-306, BRD v1.7):** if a Season has an odd number of FantasyTeams, schedule generation leaves one FantasyTeam without a `HeadToHeadMatch` for a given Gameweek rather than fabricating a match. That Gameweek is excluded from the FantasyTeam's Played/Won/Drawn/Lost totals in `LeagueStanding` — it is neither a win, loss, nor draw.

Domain events: `ScheduleGenerated`, `MatchResultCalculated`.

### LeagueStanding (Read-optimized aggregate, recalculated — not user-editable)

```text
LeagueStanding
--------------
SeasonId              : Guid (FK)
FantasyTeamId         : Guid (FK)
LeaguePoints          : int
Played / Won / Drawn / Lost : int
FantasyGoalsFor / FantasyGoalsAgainst / FantasyGoalDifference : int
CaptainPointsTotal    : int
Position              : int         (computed via ADR-008 tie-break pipeline)
AsOfGameweekId        : Guid (FK)   (supports historical/point-in-time standings, BR-217)
```

### SeasonGoalPrediction (Aggregate Root)

```text
SeasonGoalPrediction
--------------------
PredictionId       : Guid
SeasonId           : Guid (FK)
FantasyTeamId      : Guid (FK)
PredictedEplGoals  : int
SubmittedAt        : DateTimeOffset
LockedAt           : DateTimeOffset   (= Season start if submitted on time; otherwise = SubmittedAt under the late-submission fallback, BR-299, BRD v1.6)
FinalActualGoals   : int?             (populated at season end)
FinalAbsoluteDifference : int?        (BR-131, computed and stored once — BR-135, BR-DOM-043)
```

**Late-submission fallback (BR-299, BRD v1.6):** if no `SeasonGoalPrediction` exists for a `FantasyTeam` by Season start, the Roster Management application service that handles a `GameweekRoster` submission (Section 6.5) checks for one before accepting that `FantasyTeam`'s **first** Gameweek roster of the Season; if none exists, the submission is rejected with a prompt to provide the prediction first, rather than silently allowing a Season to proceed with a permanently missing tie-break input. This is a cross-context precondition (Competition context gating a Roster Management action) enforced in the application-service layer, not by one aggregate calling another directly (Section 4's layering).

## 6.8 Corrections & Administration Context (cross-cutting)

### AdministrativeAction (Aggregate Root — the audit log for competitive changes)

```text
AdministrativeAction
---------------------
ActionId               : Guid
LeagueId               : Guid (FK)
ActingMembershipId     : Guid? (FK — nullable; null means system-generated, e.g. BR-308's automatic EPL-exit eligibility grant, added in v1.5)
ActionType             : AdminActionType (RosterCorrection | ScoreOverride | ScoreOverrideUndo |
                                            ReplacementEligibilityGranted | SeasonEndingInjuryDeclared |
                                            DraftTimerExtended | ConfigurationChanged | Other)
TargetEntityType       : string
TargetEntityId         : Guid
BeforeState            : jsonb
AfterState             : jsonb
Reason                 : string?
CreatedAt              : DateTimeOffset
```

Every mutation listed in BR-147/BR-177 writes exactly one `AdministrativeAction` row in the same transaction as the underlying change (BR-099, BR-149, BR-182, AP-005). This is enforced by routing all administrator-privileged application-service methods through a shared `IAdministrativeActionRecorder` decorator, not by relying on each handler to remember to log.

A `null` `ActingMembershipId` (v1.5) represents a system-generated entry — currently only BR-308's automatic EPL-exit replacement-eligibility grant, which is confirmed by official data rather than a human decision. The audit log viewer (F-011.1) must render these as "System," not as a blank or erroring actor field.

Changes to `LeagueConfiguration` or `SeasonConfiguration` (Section 6.2) route through the same decorator, recording `ActionType = ConfigurationChanged` with the parameter name, prior value, and new value in `BeforeState`/`AfterState` (BR-295, BRD v1.5).

## 6.9 Notifications Context

```text
NotificationPreference
-----------------------
UserId       : Guid (FK)
EventType    : NotificationEventType (GameweekReminder | WeeklyScore | WeeklyStandings)
Channel      : NotificationChannel (Email | Sms)
Enabled      : bool
```

```text
NotificationRequest   (outbox entry — Section 12.2)
-------------------
RequestId    : Guid
UserId       : Guid (FK)
EventType    : NotificationEventType
Channel      : NotificationChannel
Payload      : jsonb
Status       : NotificationStatus (Pending | Sent | Failed | Suppressed)
Attempts     : int
CreatedAt / LastAttemptAt
```

---

# 7. Domain Event Catalog (Summary)

| Context | Events |
|---|---|
| Identity | `UserRegistered`, `UsernameChanged`, `UserRetired`, `UserReactivated` |
| League & Season | `LeagueCreated`, `MembershipJoined`, `InvitationIssued`, `InvitationAccepted`, `InvitationExpired`, `SeasonStarted`, `SeasonCompleted`, `LeagueMessagePublished` |
| Fantasy Team | `PlayerAcquired`, `PlayerReleased`, `PlayerMarkedReplacementEligible` |
| Draft | `DraftStarted`, `DraftTimerExtended`, `PlayerDrafted`, `DraftCompleted` |
| Roster | `RosterSubmitted`, `RosterLocked`, `RosterCarriedForward`, `CaptainSelected`, `RosterAdministrativelyCorrected` |
| Scoring | `ScoreCalculated`, `ScoreOverrideApplied`, `ScoreOverrideUndone`, `ScoreRecalculated` |
| Competition | `ScheduleGenerated`, `MatchResultCalculated`, `StandingsRecalculated`, `SeasonGoalPredictionFinalized` |
| Administration | `AdministrativeActionRecorded` |

These events drive: (a) recalculation cascades (e.g., `ScoreOverrideApplied` → recompute `GameweekScore` → recompute `HeadToHeadMatch` result → recompute `LeagueStanding`), and (b) the notification outbox (Section 12.2). They are dispatched in-process (ADR-001 — no message broker required for this, though the outbox table itself may be drained by a background worker).

---

# 8. Data Architecture (Logical)

This section describes logical schema intent; physical DDL/migrations are a later artifact (Section 16).

## 8.1 Key Tables and Constraints (non-exhaustive — full DDL is a follow-on artifact)

| Table | Primary Key | Notable Constraints |
|---|---|---|
| `users` | `user_id (uuid)` | Unique partial index `(username) WHERE status = 'active'` (BR-004) |
| `league_configurations` | `league_id` | 1:1 with `leagues`; one column per configurable parameter (BR-291) |
| `season_configurations` | `season_id` | 1:1 with `seasons`; same column shape as `league_configurations` plus a `locked_fields` column (BR-293); copied from `league_configurations` at Season creation, then independently mutable per field until each field's lock point |
| `league_memberships` | `league_membership_id` | Unique `(league_id, user_id) WHERE status <> 'left'`; partial unique index `(league_id) WHERE is_administrator` (BR-283) |
| `fantasy_teams` | `fantasy_team_id` | Unique `(league_membership_id, season_id)` (BR-193, Invariant 2) |
| `squad_players` | `squad_player_id` | Unique partial index `(player_id, season_id) WHERE is_currently_owned` (BR-191, Invariant 1) |
| `draft_selections` | `draft_selection_id` | Unique `(draft_id, round, pick_number)`; FK to `squad_players` created in same transaction |
| `gameweek_rosters` | `gameweek_roster_id` | Unique `(fantasy_team_id, gameweek_id)`; check constraint enforcing exactly 15 `roster_players` rows at `Submitted`+ status (enforced at application layer + a deferred trigger as defense-in-depth) |
| `roster_players` | `(gameweek_roster_id, player_id)` composite | At most one row per roster with `is_captain = true` (partial unique index) |
| `score_overrides` | `score_override_id` | None beyond FK; `is_active` is a generated column from `undone_at IS NULL` |
| `head_to_head_matches` | `match_id` | Unique `(season_id, gameweek_id, home_fantasy_team_id, away_fantasy_team_id)` |
| `administrative_actions` | `action_id` | Append-only; no update/delete grants at the database role level (ADR-010-style immutability, BR-149) |

## 8.2 Historical Data Strategy

Per BR-174/AP-004, competitive tables (`gameweek_rosters`, `roster_players`, `gameweek_scores`, `head_to_head_matches`, `league_standings`, `draft_selections`, `administrative_actions`) are **never** deleted or hard-updated in place after finalization; corrections create new audited rows/overrides rather than mutating history. A ten-year retention policy is a *lack* of a deletion job, not an active archival requirement at this scale — archival tiering can be introduced later if storage volume warrants it.

## 8.3 Concurrency Strategy

- **Optimistic concurrency** (a `row_version`/`xmin`-based check) on aggregates edited by their own owner concurrently from multiple devices (e.g., `GameweekRoster` before lock).
- **Pessimistic/transactional exclusivity** for draft picks and squad-player creation, backed by the partial unique indexes in 8.1 (AP-009/AP-010) — the database is the final arbiter of "who got the player," with the application translating a constraint violation into a friendly `PlayerAlreadyOwnedException`.

---

# 9. API Architecture

## 9.1 Style and Versioning

- REST over HTTPS only (BR-164), JSON payloads.
- URL-path versioning: `/api/v1/...` (BR-190). A version is never silently broken; a new version is introduced for breaking changes.
- Authentication: bearer JWT access tokens (short-lived) issued by the application's own auth module (ADR-006), refreshed via a refresh-token flow. Tokens carry `sub` (UserId) only — never roles/permissions as claims, since League Administrator status is per-league and must be checked against current data (ADR-007), not cached in a token.

## 9.2 Resource Groups (one per bounded context; illustrative, not exhaustive)

| Context | Example Endpoints |
|---|---|
| Identity | `POST /api/v1/auth/register`, `POST /api/v1/auth/login`, `GET/PUT /api/v1/users/me`, `PUT /api/v1/users/me/icon` |
| League & Season | `POST /api/v1/leagues`, `POST /api/v1/leagues/{leagueId}/invitations`, `POST /api/v1/invitations/{token}/accept`, `POST /api/v1/leagues/{leagueId}/seasons` |
| Fantasy Team | `GET /api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/squad` |
| Draft | `POST /api/v1/drafts/{draftId}/start`, `POST /api/v1/drafts/{draftId}/picks`, `POST /api/v1/drafts/{draftId}/timer/extend` |
| Roster | `PUT /api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster`, `PUT .../roster/captain` |
| Scoring | `GET /api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/score`, `POST /api/v1/admin/score-overrides` |
| Competition | `GET /api/v1/leagues/{leagueId}/seasons/{seasonId}/standings`, `GET .../schedule` |
| Administration | `GET /api/v1/leagues/{leagueId}/audit` |

## 9.3 Cross-Cutting API Conventions

- **Error model:** a consistent `ProblemDetails`-style JSON error body (type, title, status, detail, instance, and a stable machine-readable `errorCode` for client-side handling).
- **Validation:** all external input validated server-side regardless of client-side validation (BR-166); DTO-level validation (shape/format) is separate from domain-invariant validation (business rules), and both must pass — client validation is UX-only, never trusted (AP-003).
- **Idempotency:** state-changing endpoints for draft picks and roster submission accept an `Idempotency-Key` header so a retried request (e.g., after a timeout) cannot double-submit (BR-237, AP-008-adjacent).
- **Correlation IDs:** every request is tagged with a correlation ID (generated if absent) and propagated into logs (BR-241).
- **Authorization:** every controller action declares the object-level check it performs (e.g., "caller must be an active member of `LeagueId`, or its Administrator") and a shared authorization-handler pipeline enforces it before the application service is invoked — this is not left to be implemented ad hoc per endpoint (BR-163, AP-002).

---

# 10. External FPL/EPL Integration Architecture

```text
┌────────────────────┐     ┌───────────────────────────┐     ┌───────────────────────┐
│  Official FPL API /  │ →   │  PlayerDataIntegration     │ →   │  Domain (PlayerData,    │
│  data source          │     │  module (anti-corruption   │     │  Scoring contexts)      │
│  (external, ADR-009)  │     │  layer): maps external      │     │                         │
│                        │     │  shapes → internal models   │     │                         │
└────────────────────┘     └───────────────────────────┘     └───────────────────────┘
```

- A scheduled background job (not a request-time call — BR-236) pulls players, clubs, fixtures, gameweeks, and player statistics on a polling cadence appropriate to the FPL update cycle.
- Synchronization is **idempotent**: re-running a sync for the same official dataset produces the same internal state (BR-232, AP-008) — achieved by upserting on the external identifier (`EplPlayerId`, `EplFixtureId`, etc.), never blind-inserting.
- **Reconciliation:** when official data is corrected after the fact (BR-139, BR-234), the sync job detects the delta against stored `PlayerPerformance.RetrievedAt`/value pairs and raises a `ScoreRecalculated` cascade (Section 7) rather than silently overwriting already-consumed data.
- **Failure isolation:** a failed or partial sync never partially commits — the whole batch is transactional per gameweek/source-batch, and a failure leaves prior data untouched (BR-233).
- **Source attribution:** every `PlayerPerformance` row records its `Source` and `RetrievedAt` so administrator overrides (Section 6.6) can be reasoned about against a specific data snapshot.
- **Double gameweeks (BR-288, BRD v1.4):** require no special handling — official FPL already sums a player's statistics across all fixtures within a single official gameweek into one total, and the Captain multiplier applies to that combined total. `PlayerPerformance` and `GameweekScore` (Section 6.6) consume that already-combined official value directly, the same way BR-074 (multi-club appearance) is handled.
- **Risk acknowledgment (BR-289, BRD v1.4):** the FPL data source is a publicly accessible but unofficial, undocumented interface with no published terms of use, rate limits, or availability guarantee. This is an accepted risk, mitigated by the idempotent/transactional sync design above, not eliminated by it. The terms of use should be periodically reverified (product/legal responsibility, not an architecture concern) and are tracked as an open item (Section 15).

---

# 11. Security & Authorization Architecture

- **Transport:** HTTPS everywhere in production (BR-164); HSTS enabled.
- **Password storage:** adaptive hashing (e.g., Argon2id or bcrypt with a modern work factor) — never reversible encryption, never plaintext (BR-158).
- **Password strength:** evaluated via a strength-estimation mechanism (e.g., zxcvbn-style scoring) rather than fixed composition rules, with a minimum effective length of twelve characters (BR-285, BRD v1.4).
- **Password recovery:** a time-limited, single-use reset link delivered to the user's registered email address (BR-284, BRD v1.4).
- **Multi-factor authentication:** not required for the initial release (BR-286, BRD v1.4); the authentication module (ADR-006) is structured so an MFA step can be inserted later without a redesign.
- **Tokens:** short-lived JWT access tokens + rotating refresh tokens, scoped to the API only (BR-159).
- **Authorization enforcement point:** server-side only, in the application-service layer behind the API — the client is never authoritative (BR-160, AP-003).
- **Object-level checks:** every request that names a `LeagueId`, `FantasyTeamId`, `GameweekRosterId`, or `DraftId` is checked against the caller's actual membership/ownership of that specific object, not just "is logged in" (BR-163, BR-254).
- **Injection protection:** parameterized queries via EF Core (no raw SQL string concatenation), output encoding on any user-supplied text rendered in the web client, CSRF protection on cookie-based flows if the web client ever uses cookies instead of bearer tokens (BR-167, BR-168).
- **Rate limiting:** applied to `/auth/*` endpoints and other abuse-prone surfaces (BR-169) via ASP.NET Core's built-in rate-limiting middleware.
- **Logging hygiene:** passwords, tokens, and other secrets are excluded from structured logs by a redaction convention enforced in the logging pipeline, not left to individual call sites (BR-171).
- **Least privilege:** the application's database role has only the CRUD/DML grants it needs; migrations run under a separate, more privileged role (BR-172).
- **Secrets management:** connection strings, signing keys, and third-party credentials are supplied via environment/secret-store injection at deploy time, never committed to source control (BR-173).

---

# 12. Cross-Cutting Concerns

## 12.1 Audit

All administrator-privileged mutations are routed through the `IAdministrativeActionRecorder` decorator described in 6.8, guaranteeing the audit row and the underlying change are committed atomically (BR-099, BR-149, BR-182, AP-005). Read access to `/api/v1/leagues/{leagueId}/audit` is itself an authorization-checked, League-Administrator-only operation.

## 12.2 Notifications

Notifications are **asynchronous** (BR-224) via an outbox pattern: the domain event handlers (Section 7) that care about notifications (e.g., `RosterLocked` for a "did you submit?" reminder window, `GameweekScore` calculation for "your weekly score") write a `NotificationRequest` row in the same transaction as the triggering change, and a separate background worker drains that table, respects `NotificationPreference` (BR-226 — a disabled channel/event is never sent), and retries failures with backoff (BR-225). The specific email/SMS provider is an infrastructure choice deferred alongside hosting (ADR-005), not a domain concern.

## 12.3 Observability

- Structured logging (BR-239) with correlation IDs (BR-241) threaded from the API layer through application services to infrastructure calls.
- Unhandled exceptions are logged with enough context to reproduce (stack trace, correlation ID, sanitized request context) without leaking secrets (BR-240, 11 above).
- Domain events (Section 7) double as a natural place to emit business-level metrics (e.g., drafts completed, overrides applied) without instrumenting each call site separately.

---

# 13. Testing Architecture Implications

This document does not define the test plan (BR-243–BR-254 already enumerate required coverage categories, and Section 16 lists "Testing strategy" as a follow-on artifact), but the architecture choices here are made to make that testing tractable:

- Aggregates expose behavior through methods that enforce their own invariants (e.g., `GameweekRoster.Submit(players, captainId)` throws domain exceptions rather than allowing an invalid state to be persisted) — this is what makes BR-247/BR-248-style unit tests meaningful without spinning up the database.
- The `IAuthoritativeValueResolver` (6.6) and `IStandingsTieBreakRule` pipeline (ADR-008) are natural seams for the scoring/tie-break test matrices required by BR-249/BR-252.
- Partial unique indexes (Section 8) are exercised by concurrency tests (AP-009/AP-010, BR-246 "Timer extension"/ownership tests) that assert only one of two concurrent draft picks succeeds.

---

# 14. Recommended Solution Structure (.NET Projects)

```text
EplFantasy.sln
 ├─ src/
 │   ├─ EplFantasy.Api                (ASP.NET Core host, controllers, middleware)
 │   ├─ EplFantasy.Identity            (domain + application services)
 │   ├─ EplFantasy.Leagues
 │   ├─ EplFantasy.FantasyTeams
 │   ├─ EplFantasy.PlayerData
 │   ├─ EplFantasy.Drafts
 │   ├─ EplFantasy.Rosters
 │   ├─ EplFantasy.Scoring
 │   ├─ EplFantasy.Competition
 │   ├─ EplFantasy.Administration
 │   ├─ EplFantasy.Notifications
 │   ├─ EplFantasy.Reporting
 │   ├─ EplFantasy.SharedKernel         (base entity/value-object types, domain event base, result/error types)
 │   └─ EplFantasy.Infrastructure       (EF Core DbContext(s), repositories, FPL client, outbox dispatcher)
 └─ tests/
     ├─ EplFantasy.UnitTests            (per-module, mirrors src/ structure)
     ├─ EplFantasy.IntegrationTests     (real PostgreSQL via test containers)
     └─ EplFantasy.ApiTests             (end-to-end HTTP-level tests)
```

`EplFantasy.SharedKernel` is intentionally small — shared identity/value-object plumbing only, never business rules, so it cannot become a dumping ground that couples every module together.

---

# 15. Remaining Open Decisions (Carried Forward)

BRD v1.4 (§61) resolved eight of the ten items originally listed here: positional minimums (BR-279), the tie-break tier-four scope (BR-280), the secondary-draft scheduling algorithm (BR-281), draft pick timeout behavior (BR-282), the single-administrator scope (BR-283), password reset/strength/MFA policy (BR-284–BR-286), the replacement-selection cap (BR-287), and double-gameweek scoring (BR-288) — each incorporated into Section 6 above. Two items remain genuinely open:

1. **Notification delivery provider(s)** for email/SMS — an infrastructure/vendor choice deferred alongside the hosting decision (ADR-005), not a domain concern; revisit when hosting is selected.
2. **Official FPL data source terms of use** (BR-289) — the source is confirmed to be an unofficial, undocumented interface with no published terms or rate limits (Section 10). This is accepted as a standing risk, mitigated but not eliminated by the idempotent-sync design; its terms should be periodically reverified as a product/legal action item, not an architecture task.

---

# 16. Recommended Next AIDLC Artifacts

In priority order, consistent with Domain Spec §25:

1. **Resolve Section 15 open decisions** (or explicitly defer each with an owner/date) — several downstream artifacts depend on them.
2. **Prioritized Epic/Feature backlog**, refining Domain Spec's EPIC-001–013 into sequenced features.
3. **OpenAPI specification** for the endpoints sketched in Section 9, including request/response DTOs and validation rules.
4. **Physical database migration scripts** (PostgreSQL DDL) implementing Section 8's logical design.
5. **Feature-level behavior specifications with acceptance criteria**, tracing back to BR-### and this document's aggregate invariants.
6. **Testing strategy** operationalizing BR-243–BR-254 against the seams identified in Section 13.
7. **Implementation task breakdown and sequencing**, starting with Identity/League/FantasyTeam (Sections 6.1–6.3) since every other context depends on them.

---

# 17. Document Version History

## Version 1.0

Initial architecture and domain model, derived from BRD v1.3 and reconciled against AIDLC Domain & Requirements Specification v1.0. Establishes technology decisions (ADR-001–ADR-010), bounded-context/module map, aggregate-level domain model with invariants and domain events, logical data architecture, API architecture, external integration architecture, and security architecture.

## Version 1.1

Incorporates BRD v1.4's resolution of eight of the ten open decisions carried in v1.0's Section 15: weekly roster positional minimums (BR-279), the tie-break tier-four scope (BR-280), the secondary-draft scheduling algorithm (BR-281), draft pick timeout/makeup-pick behavior (BR-282), the single-League-Administrator scope (BR-283), password reset/strength/MFA policy (BR-284–BR-286), the uncapped replacement-selection model (BR-287), and double-gameweek scoring (BR-288). Also formalizes the external-data-source risk acknowledgment (BR-289). Updates ADR-008's tie-break sequence, the `LeagueMembership`, `Draft`, `DraftSelection`, `GameweekRoster`, and `SquadPlayer` domain-model entries, and the security and external-integration architecture sections accordingly. Two items remain open (notification provider selection, FPL data source terms of use).

## Version 1.2

Incorporates BRD v1.5's League/Season configurability requirement (BR-290–BR-297): adds ADR-011 and the `LeagueConfiguration`/`SeasonConfiguration` value objects (Section 6.2), and updates every aggregate that previously embedded a fixed constant — `Draft` (timer), `GameweekRoster` (roster size and positional minimums), `SquadPlayer` (replacement cap), `HeadToHeadMatch` (league point values), `Invitation` (expiration), and the standings tie-break ruleset version (ADR-008) — to read that value from the Season's resolved configuration instead. Adds `league_configurations`/`season_configurations` tables (Section 8.1), a `ConfigurationChanged` administrative-action type (Section 6.8/12.1), and the single-administrator partial-unique-index invariant (BR-283) to the data architecture.

## Version 1.3

Incorporates BRD v1.6's three fixes surfaced while writing the Phase 1 feature specifications (BR-298–BR-301): confirms username-reuse-after-retirement is already the correct reading of the existing `Status = 'active'`-filtered unique index (BR-298); adds the Season Goal Prediction late-submission fallback as a cross-context precondition on a `FantasyTeam`'s first `GameweekRoster` submission (BR-299, Sections 6.5/6.7); and introduces the platform-level `SystemAdministrator` role (ADR-007) plus a `User.IsSystemAdministrator` flag and `UserReactivated` domain event to support System-Administrator-only account reactivation (BR-300, BR-301).

## Version 1.4

Incorporates BRD v1.7's seven fixes surfaced while writing the Phase 2 and Phase 3 feature specifications (BR-302–BR-308): adds ADR-012 (a shared scheduled sweep for both draft-pick-timeout and roster-lock deadline detection); a minimum-two-FantasyTeams invariant on `Draft` (BR-302); the `GameweekRoster` carry-forward fallback state transition, including its first-Gameweek and squad-change edge cases (BR-305); the Starting XI computation rule — captain multiplier applied before ranking, deterministic tie-break at the 11th/12th boundary (BR-303, BR-304); bye-week handling on `HeadToHeadMatch` (BR-306); removal of any expiration on replacement-selection opportunities (BR-307); and the automatic-vs-Administrator-determined distinction between EPL-exit and season-ending-injury eligibility (BR-308).

## Version 1.5

Fixes two structural completeness gaps surfaced while writing the Phase 4 feature specifications, neither requiring a BRD change (no new business rule, just closing a modeling gap): adds the previously undefined `LeagueMessage` aggregate (§6.2, supporting F-003.6/BR-221–BR-223) with its `LeagueMessagePublished` domain event; and makes `AdministrativeAction.ActingMembershipId` nullable (§6.8) to represent system-generated audit entries (currently only BR-308's automatic EPL-exit eligibility grant), with the audit log viewer (F-011.1) required to render a null actor as "System."
