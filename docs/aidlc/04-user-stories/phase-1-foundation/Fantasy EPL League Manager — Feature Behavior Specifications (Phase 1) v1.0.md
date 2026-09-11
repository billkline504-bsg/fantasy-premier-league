# Fantasy EPL League Manager
## Feature Behavior Specifications — Phase 1 (Foundation) — Version 1.0

**Document Status:** Baseline Feature Specifications
**Version:** 1.0
**Inputs:**
- Business Requirements Document v1.5 — authoritative business rules (BR-001…BR-297)
- Architecture and Domain Model v1.2 — aggregates, invariants, module map
- Epic and Feature Backlog v1.0 — feature list, dependencies, build sequence

**Scope:** This document covers **build-sequence positions 1–20** from the Backlog's §5 — every P0 feature in Phase 1 (Foundation): all of EPIC-001 (Identity & Authentication), EPIC-002 (User Profile & FantasyTeam), EPIC-003's P0 features (League & Season Management, excluding P1 League Messaging), EPIC-004 (EPL/FPL Data), and F-010.3 (Season Goal Prediction submission — pulled forward from EPIC-010 per the Backlog's explicit note that its submission path must exist at Season start). Phases 2–4 (Draft, Weekly Gameplay, Operations) are follow-on documents, written in the same format once this phase is implementation-ready.

Each feature specifies: user story/stories, acceptance criteria (Given/When/Then), security considerations, and technical tasks — the grouping the BRD (§55) calls for. Acceptance criteria are traced inline to BR-### where the behavior originates.

---

# EPIC-001 — Identity & Authentication

## F-001.1 — User Registration & Login
**Build sequence position:** 1 · **Depends on:** none

**User Story 1:** As a prospective user, I want to register an account with a unique username and a password so that I can access the application.

Acceptance criteria:
1. Given a username that is not currently used by any active user, when I register with that username, a valid email, and a password, then my account is created with `Status = Active` and an immutable `UserId` (BR-001, BR-002).
2. Given a username already in use by another active user, when I attempt to register with it, then registration is rejected with a clear "username taken" error and no account is created (BR-004).
3. Given a username used only by a *retired* user, when I attempt to register with it, then registration is rejected — usernames are not recycled from retired accounts within the uniqueness index unless the product owner later decides otherwise (BR-004, conservative default; flag for confirmation if this is too strict).
4. Given any successful registration, when the account is persisted, then the password is stored only as an adaptive hash (e.g., Argon2id/bcrypt) — the plaintext password is never persisted or logged (BR-158, BR-171).
5. Given a registered user, when they submit correct username/password credentials to log in, then the API issues a short-lived JWT access token plus a refresh token, and the token payload carries only `UserId` — no role or League Administrator claims (BR-159, ADR-007).
6. Given a registered user, when they submit an incorrect password, then the API returns a generic "invalid credentials" error (never "wrong password" specifically, to avoid username enumeration) and the attempt is logged for rate-limiting purposes (BR-169, BR-170).
7. Given repeated failed login attempts for the same username or IP within a short window, when the configured threshold is exceeded, then further attempts are rate-limited (BR-169).

**Security considerations:** adaptive password hashing (BR-158); no plaintext/secret values in logs (BR-171); rate limiting on `/auth/login` and `/auth/register` (BR-169); generic error messages to prevent user enumeration; HTTPS-only (BR-164).

**Technical tasks:** `Identity` module — `User` aggregate, registration/login application services, `IAuthenticationService`/`ICurrentUserAccessor` abstractions (ADR-006), JWT issuance/refresh endpoint, unique partial index on `(username) WHERE status = 'active'`.

---

## F-001.2 — Password Reset
**Build sequence position:** 12 · **Depends on:** F-001.1

**User Story:** As a user who has forgotten my password, I want to request a reset link sent to my registered email so that I can regain access to my account.

Acceptance criteria:
1. Given a registered email address, when I request a password reset, then a time-limited, single-use reset token is generated and emailed to that address (BR-284).
2. Given an email address that is *not* registered, when I request a password reset, then the API returns the same generic "if this email is registered, a reset link has been sent" response as for a registered email — the response never reveals whether the account exists (prevents user enumeration).
3. Given a valid, unexpired, unused reset token, when I submit a new password with it, then my password is updated (subject to AC-4 below) and the token is immediately invalidated so it cannot be reused.
4. Given a new password submitted via reset (or at registration), when it is evaluated, then it must meet the configured strength threshold (minimum twelve characters, evaluated by a strength-estimation mechanism rather than fixed composition rules) or the request is rejected with actionable feedback (BR-285).
5. Given an expired or already-used reset token, when I attempt to use it, then the request is rejected and no password change occurs.

**Security considerations:** reset tokens are single-use and time-limited; no user-enumeration leak; password strength evaluated server-side regardless of any client-side check (BR-166).

**Technical tasks:** reset-token generation/storage/expiry, email delivery hook (provider TBD — Architecture v1.2 §15), strength-estimator integration, invalidate-on-use logic.

---

## F-001.3 — Username Management
**Build sequence position:** 13 · **Depends on:** F-001.1

**User Story:** As a user, I want to change my username so that I can update my public identity without losing my account history.

Acceptance criteria:
1. Given a desired new username not currently used by any active user, when I change my username, then the change succeeds, `UserId` is unchanged, and all historical relationships (past drafts, rosters, standings) continue to resolve correctly by `UserId` (BR-270, BR-271, BR-278).
2. Given a desired new username already in use by another active user, when I attempt the change, then it is rejected with a clear error (BR-004, BR-266).
3. Given a completed historical Season referencing my old username in a snapshot/report, when I later change my username, then that historical snapshot's display behavior follows the still-open BRD §54 "Historical Username Display" decision — implementers should treat this as **explicitly undecided** and must not hard-code either behavior without revisiting that open item.

**Security considerations:** uniqueness re-validated server-side on every change (BR-166); no authorization gap allowing a user to rename another account (object-level check — AP-002).

**Technical tasks:** username-change endpoint, re-validation against the active-user unique index, no `UserId` mutation path exists in the domain model (defense against accidental identity change).

---

## F-001.4 — User Retirement
**Build sequence position:** 14 · **Depends on:** F-001.1

**User Story:** As a user, I want to close my account so that I stop being an active participant, while my historical league contributions remain intact for other members.

Acceptance criteria:
1. Given an active user requests account retirement, when the request is processed, then `User.Status` transitions to `Retired` and `RetiredAt` is set — the row is never physically deleted (BR-013).
2. Given a retired user, when they attempt to log in, then authentication is rejected.
3. Given a retired user who previously participated in a League, when other members view historical standings, schedules, or draft history, then that user's historical username/icon still display correctly, clearly identifiable as retired if the UI chooses to flag it (BR-014, BR-175, BR-272).
4. Given a retired user's username, when a *different* prospective user attempts to register with that same username, then registration is rejected (per F-001.1 AC-3) — retirement does not free the username for reuse.

**Security considerations:** retirement is irreversible from the API's perspective within this release (no self-service "un-retire" flow specified) — confirm with the user before assuming reactivation is out of scope, or add it as a future feature.

**Technical tasks:** status-transition method on `User` aggregate guarded so no code path can hard-delete a `User` row; login-guard check on `Status`.

---

## F-001.5 — Private Data Protection
**Build sequence position:** 15 · **Depends on:** F-001.1

**User Story:** As any user, I want assurance that my email, phone number, and authentication details are never exposed through league-facing screens or APIs, so that my private account information stays private even from fellow league members.

Acceptance criteria:
1. Given any league-facing endpoint (standings, schedule, draft screens, match results, league messages, historical results), when its response is serialized, then it contains no `Email`, `PasswordHash`, token, or other private `User` field — only `Username` and the applicable icon (BR-015, BR-277).
2. Given a request for another user's profile via a league-facing endpoint, when the response is built, then only fields explicitly designated as League-visible are included (BR-273).
3. Given a code review or automated contract test, when response DTOs for league-facing endpoints are inspected, then a shared allow-list/DTO-projection pattern is used (not manual field-by-field omission) so a newly added private field cannot leak by default.

**Security considerations:** this feature is the concrete enforcement mechanism for BR-015/BR-273; recommend an automated test that fails the build if a league-facing DTO's shape includes any field outside an explicit allow-list.

**Technical tasks:** define explicit "public/league-facing" DTOs distinct from internal `User` entities across all API responses; add a contract/shape test enforcing the allow-list.

---

# EPIC-002 — User Profile & FantasyTeam

## F-002.1 — Default Profile Icon Selection
**Build sequence position:** 8 · **Depends on:** F-001.1

**User Story:** As a user, I want to choose a default profile icon from the application's predefined collection so that I have a visual identity across leagues that don't set their own icon.

Acceptance criteria:
1. Given the predefined, active `ProfileIcon` catalog, when I select one as my default, then `UserProfile.DefaultIconId` is updated to that icon (BR-006, BR-267).
2. Given a request that supplies an icon identifier not present (or marked inactive) in the `ProfileIcon` catalog, when I attempt to set it as my default, then the request is rejected — the client can never specify an arbitrary image path (BR-011).
3. Given a user with no League-specific icon override in a given League, when they are displayed anywhere in that League, then their default icon is shown (BR-008).

**Security considerations:** icon values are validated against a server-controlled catalog, never a client-supplied path or URL (BR-011) — this is also an injection/content-integrity control, not just a UX rule.

**Technical tasks:** `ProfileIcon` reference-data table and seed set; `UserProfile` update endpoint with catalog validation.

---

## F-002.2 — League-Specific Icon Override
**Build sequence position:** 9 · **Depends on:** F-002.1, F-003.3

**User Story:** As a user participating in multiple leagues, I want to set a different icon for each league so that changing my look in one league doesn't affect how I appear in another.

Acceptance criteria:
1. Given an active `LeagueMembership`, when I set a League-specific icon from the `ProfileIcon` catalog, then `LeagueMembership.LeagueIconId` is updated and that icon is shown wherever I'm represented in that League only (BR-007, BR-203).
2. Given a League-specific icon set in League A, when I view my representation in League B, then League B still shows my default icon (or League B's own override, if set) — League A's change has no effect on League B (BR-009).
3. Given a League-specific icon is set, when I remove it, then the League reverts to displaying my current default icon (BR-010, BR-275).
4. Given I later change my *default* icon, when a League already has its own League-specific icon set, then that League continues showing its own icon, unaffected by the default change (BR-276).
5. Given an icon identifier not present in the active `ProfileIcon` catalog, when I attempt to set it as a League-specific icon, then the request is rejected (BR-274).

**Security considerations:** same catalog-validation control as F-002.1, applied per membership; object-level check that the caller can only modify their own `LeagueMembership` icon (BR-163).

**Technical tasks:** `LeagueMembership.LeagueIconId` update endpoint with catalog validation and null-clears-to-default semantics.

---

## F-002.3 — FantasyTeam Creation & Uniqueness
**Build sequence position:** 10 · **Depends on:** F-003.3, F-003.4

**User Story:** As a user with an active League membership in a given Season, I want a FantasyTeam automatically established as my competitive identity so that I can be drafted into and compete in that Season.

Acceptance criteria:
1. Given an active `LeagueMembership` and an active `Season` for that League, when no `FantasyTeam` yet exists for that combination, then one is created with `Status = Active` (BR-018, BR-019).
2. Given a `LeagueMembership` that already has a `FantasyTeam` for a given `Season`, when creation is attempted again for the same combination, then it is rejected — the unique `(LeagueMembershipId, SeasonId)` constraint prevents a duplicate (BR-193, Invariant 2).
3. Given a `User` with multiple active `LeagueMembership` records (different Leagues) for the same Season, when each membership's `FantasyTeam` is created, then each is fully independent — no squad, roster, or score data is shared between them (BR-020, BR-021, BR-022, BR-256).
4. Given a `FantasyTeam`, when it is displayed anywhere in its League, then its League-facing identity is `[League Icon] [Username]`, never the account email (BR-277).

**Security considerations:** object-level authorization ensures a user can only create/act on a `FantasyTeam` tied to their own `LeagueMembership` (BR-163).

**Technical tasks:** `FantasyTeam` aggregate + unique constraint migration; creation trigger point (on invitation acceptance, or explicitly at draft setup — confirm exact trigger timing against F-003.2/F-005.1 when Phase 2 specs are written).

---

## F-002.4 — Profile/League Context Switching UI
**Build sequence position:** 11 · **Depends on:** F-002.3

**User Story:** As a user active in multiple leagues, I want to clearly see and switch which League/FantasyTeam I'm currently managing so that I never accidentally act on the wrong league's data.

Acceptance criteria:
1. Given a user with more than one active League, when they open any FantasyTeam-scoped screen, then the application requires an explicit currently-selected League/FantasyTeam context before showing or accepting league-specific actions (BR-031, BR-199).
2. Given a selected League/FantasyTeam context, when the user navigates the application, then the active League and FantasyTeam are visibly and persistently identified in the UI (BR-200, BR-201).
3. Given the user's global profile settings screen, when compared to a League-specific settings screen, then the two are visually and functionally distinguishable so the user cannot confuse "change my default icon" with "change my League A icon" (BR-202).

**Security considerations:** the selected context is a UI/session convenience only — every API call still independently authorizes against the specific `LeagueId`/`FantasyTeamId` in the request, never trusting a client-side "currently selected" value as an authorization signal (AP-003).

**Technical tasks:** client-side context selector/state; server-side context is derived per-request from the URL/payload, never from a stored "current league" session value used for authorization decisions.

---

# EPIC-003 — League & Season Management

## F-003.1 — League Creation
**Build sequence position:** 2 · **Depends on:** F-001.1

**User Story:** As a registered user, I want to create a new private League so that I can invite others to compete in it.

Acceptance criteria:
1. Given a registered, active user, when they create a League with a name (and optional description), then the League is created with `Status = Active`, and a `LeagueMembership` is created for the creator with `IsAdministrator = true` (BR-023, BR-024, BR-283).
2. Given a newly created League, when its visibility is checked, then it defaults to private — joinable only via invitation, never publicly discoverable (BR-026).
3. Given a League has just been created, when a second `LeagueMembership` is created for that same League with `IsAdministrator = true`, then the operation is rejected by the partial unique index `(LeagueId) WHERE is_administrator` (BR-283).

**Security considerations:** League creation requires an authenticated, active user (BR-160); no anonymous League creation.

**Technical tasks:** `League` and `LeagueMembership` aggregates; creation transaction that atomically creates both the League and the creator's administrator membership.

---

## F-003.2 — League Invitation & Acceptance
**Build sequence position:** 3 · **Depends on:** F-003.1

**User Story 1:** As a League Administrator, I want to generate an invitation link so that I can invite specific people to my private League via email or text.

**User Story 2:** As an invited person, I want to accept an invitation link so that I become a member of the League.

Acceptance criteria:
1. Given a League Administrator, when they issue an invitation, then an `Invitation` is created with a single-use `Token` and `ExpiresAt = CreatedAt + <this League's configured InvitationExpiration>` (default 7 days) (BR-027–BR-029, BR-291).
2. Given a valid, unexpired invitation token, when the recipient accepts it, then an active `LeagueMembership` is created for them (or reactivated if they previously left) and the `Invitation.Status` becomes `Accepted` (BR-027).
3. Given an invitation token past its `ExpiresAt`, when acceptance is attempted, then it is rejected with a clear "invitation expired" message, and no membership is created (BR-029).
4. Given an already-accepted or revoked invitation token, when acceptance is attempted again, then it is rejected (single-use enforcement).
5. Given a League Administrator changes the League's configured `InvitationExpiration` value, when an invitation issued *before* that change is later evaluated for expiry, then it still uses the expiration duration that was in effect when it was issued, not the newly configured value (BR-293).

**Security considerations:** invitation tokens are opaque, unguessable, single-use, and time-limited; acceptance requires the recipient to be authenticated (registered) first — no unauthenticated League data exposure through an invitation link alone.

**Technical tasks:** `Invitation` aggregate; token generation (cryptographically random, not sequential); acceptance endpoint; expiration check against the value captured at issuance time, not a live re-read of current configuration.

---

## F-003.3 — League Membership Management
**Build sequence position:** 4 · **Depends on:** F-003.1, F-003.2

**User Story:** As a League Administrator, I want to manage who is active in my League — including confirming there is exactly one Administrator — so that league operations run smoothly and unambiguously.

Acceptance criteria:
1. Given an active `LeagueMembership`, when the member chooses to leave the League, then `Status` transitions to `Left` and `LeftAt` is set — the membership row is retained, not deleted (supports historical integrity, BR-012, BR-257).
2. Given a League with exactly one Administrator membership, when any operation attempts to create or flag a second `IsAdministrator = true` membership for that League, then it is rejected (BR-283 — single administrator for the initial release; co-administration is explicitly out of scope per the Backlog §6).
3. Given a caller who is not the League Administrator, when they attempt an Administrator-only action (e.g., managing another member, issuing corrections), then the request is rejected with an authorization error (BR-025, BR-161, BR-162).
4. Given a user who previously left a League, when they are re-invited and accept a new invitation for a later Season, then a new/reactivated `LeagueMembership` is established without resurrecting their old membership's prior state incorrectly (BR-020).

**Security considerations:** every Administrator-only action is checked against the specific `LeagueId` in the request — never a cached "is admin somewhere" claim (ADR-007); this is the concrete enforcement point for BR-163/BR-254's authorization tests.

**Technical tasks:** membership status-transition methods; Administrator-only authorization policy/handler applied consistently across all League-scoped write endpoints.

---

## F-003.4 — Season Creation & Reuse
**Build sequence position:** 5 · **Depends on:** F-003.1

**User Story:** As a League Administrator, I want to create a new Season for my League — including reusing an existing League for a new EPL Season — so that the League can compete again.

Acceptance criteria:
1. Given an active League, when the Administrator creates a Season tied to a specific EPL season identifier (e.g., "2027/28"), then the Season is created with `Status = Setup` (BR-032, BR-092).
2. Given a League being reused for a subsequent Season, when the new Season is created, then new invitations are issued/resent to prior members rather than assuming continued membership (BR-033) — this Season's membership roster starts from acceptance of those new invitations, not automatically copied from the prior Season.
3. Given a League with a prior completed Season, when a new Season is created, then the prior Season's historical data (standings, rosters, scores) remains fully intact and unaffected (BR-032, AP-004).

**Security considerations:** Season creation is Administrator-only (BR-162).

**Technical tasks:** `Season` aggregate; Season-creation triggers the invitation-resend workflow (F-003.2) for the League's known prior members, per BR-033.

---

## F-010.3 — Season Goal Prediction (Submission Path Only)
**Build sequence position:** 6 · **Depends on:** F-003.4

*(Pulled forward from EPIC-010 per Backlog §4 note: submission must exist at Season start even though tie-break usage is specified alongside Standings in the Phase 3 spec.)*

**User Story:** As a FantasyTeam owner, I want to submit my prediction for the total number of EPL goals to be scored during the Season so that it can be used as a tie-break if needed later.

Acceptance criteria:
1. Given an active `Season` in `Status = Setup` and a `FantasyTeam` within it, when the owner submits a `SeasonGoalPrediction` (a single integer), then it is recorded with `SubmittedAt` set (BR-126, BR-127).
2. Given the Season transitions out of `Setup` (i.e., begins), when any further prediction submission or change is attempted, then it is rejected — the prediction is locked at `LockedAt = Season start` (BR-128).
3. Given a `FantasyTeam` that did not submit a prediction before Season start, when the Season begins, then the system must have a defined default behavior (e.g., no prediction recorded, or a null/last-resort value) — **this exact fallback is not yet specified in the BRD and should be raised as an open question before implementation**, since BR-124/BR-125 assume every FantasyTeam has a comparable prediction value for the tie-break to function.
4. Given a submitted prediction, when it is persisted, then both the original value and its audit trail (who/when) are retained even after final tie-break resolution at Season end (BR-135, BR-296).

**Security considerations:** a `FantasyTeam` owner can only submit/view their own prediction until the Season's standings are finalized — predictions should not be visible to other League members before they'd otherwise be relevant, to avoid one user anchoring on another's guess (recommend hiding predictions from other members until Season end; not an explicit BR requirement, flagged as a UX/design recommendation to confirm).

**Technical tasks:** `SeasonGoalPrediction` aggregate; lock-on-season-start guard; **flag AC-3's open fallback-behavior question to the product owner before this feature is built.**

---

## F-003.5 — League/Season Configuration Management
**Build sequence position:** 7 · **Depends on:** F-003.1, F-003.4

**User Story:** As a League Administrator, I want to configure numeric, positional, and date/time parameters for my League — and independently override them per Season — so that I can tailor the rules without waiting on the application's hard-coded defaults.

Acceptance criteria:
1. Given a newly created League, when its `LeagueConfiguration` is initialized, then every configurable parameter (BR-291's table) is set to its application default (BR-292).
2. Given a new Season is created for a League, when its `SeasonConfiguration` is initialized, then it is copied from the League's current `LeagueConfiguration` at that moment (BR-292, BR-294).
3. Given a League Administrator changes a `SeasonConfiguration` value before that parameter's "Locks At" point (BR-291), then the new value takes effect for that Season only — the League's stored default and any other Season of the same League are unaffected (BR-294).
4. Given a configurable parameter past its "Locks At" point for a given Season (e.g., the Initial Draft has started, so squad size is locked), when a change to that field is attempted, then it is rejected (BR-293).
5. Given any successful configuration change (League- or Season-level), when it is persisted, then an `AdministrativeAction` row with `ActionType = ConfigurationChanged` is written in the same transaction, recording the parameter, prior value, new value, administrator, and timestamp (BR-295).
6. Given a completed Season, when its `SeasonConfiguration` is queried, then it still returns the exact values that applied to that Season, unaffected by any subsequent League-level default changes (BR-296).
7. Given a League Administrator changes the League's `InvitationExpiration` default, when this is evaluated against already-issued invitations, then it does not retroactively change their expiration (BR-293 — this is the one parameter with a prospective-only rule rather than a lifecycle-phase lock).

**Security considerations:** configuration changes are Administrator-only (BR-162); every change is audited (BR-295) — this feature is the concrete implementation of BR-295, not just a cross-reference to it.

**Technical tasks:** `LeagueConfiguration`/`SeasonConfiguration` value objects (Architecture v1.2 §6.2); copy-on-Season-creation logic; per-field lock-state tracking (`LockedFields`); routing through `IAdministrativeActionRecorder` (Architecture v1.2 §6.8/§12.1). Note: this feature's full acceptance testing can only be completed incrementally as each owning feature (squad size with F-005.1, roster size with F-007.1, etc.) lands — see Backlog §4 note on F-003.5.

---

# EPIC-004 — EPL/FPL Data

## F-004.1 — Player/Club Reference Data Sync
**Build sequence position:** 16 · **Depends on:** none (external)

**User Story:** As the system, I want to synchronize official EPL player and club reference data on a scheduled basis so that the rest of the application always has current, accurate player/club information to draft, own, and score against.

Acceptance criteria:
1. Given a scheduled sync run, when it completes successfully, then all currently active EPL players and clubs are present in the internal `Player`/`Club` reference tables, upserted by their external identifier (BR-228, BR-232).
2. Given the same external dataset is synced twice in a row (no upstream change), when the second run completes, then no duplicate rows are created and no existing data is altered — the sync is idempotent (BR-232, AP-008).
3. Given the external data source is temporarily unavailable or returns an error, when a sync run fails, then no partial data is committed, and previously synced data remains untouched and usable (BR-233).
4. Given the external source's data shape, when it is consumed, then it passes through the `PlayerDataIntegration` anti-corruption layer and is never referenced directly by any other module's domain code (ADR-009).

**Security considerations:** this feature depends on an unofficial, undocumented external interface with no published terms of use or rate-limit guarantee (BR-289) — implement defensive polling (reasonable interval, backoff on failure) rather than aggressive/high-frequency requests, and treat any change in the external response shape as a signal to halt sync (fail safe, not fail open) rather than silently corrupting internal data.

**Technical tasks:** `PlayerDataIntegration` module; scheduled background job (not request-time, BR-236); upsert-by-external-id sync logic; transactional batch commit/rollback.

---

## F-004.2 — Fixture & Gameweek Sync
**Build sequence position:** 17 · **Depends on:** F-004.1

**User Story:** As the system, I want to synchronize official EPL fixtures and gameweek boundaries so that roster deadlines, draft scheduling, and scoring windows all align with the real competition calendar.

Acceptance criteria:
1. Given a scheduled sync run, when it completes, then internal `Gameweek` records exist for each official FPL gameweek, each with a computed `RosterLockDeadline` (BR-092, BR-093 — first-kickoff time minus this Season's configured `GameweekRosterLockOffsetBeforeKickoff`).
2. Given internal `Fixture` records, when synced, then each is associated with its official gameweek assignment, including any officially rescheduled fixture moved to a different gameweek (BR-103).
3. Given the same fixture/gameweek dataset synced twice, when the second run completes, then the result is identical to the first (idempotent upsert by external fixture/gameweek identifier, BR-232).

**Security considerations:** same as F-004.1 (shared external-source risk).

**Technical tasks:** `Fixture`/`Gameweek` reference-data sync; `RosterLockDeadline` computation reading from `SeasonConfiguration.GameweekRosterLockOffsetBeforeKickoff`.

---

## F-004.3 — Player Statistics Sync (Official Scoring Ingestion)
**Build sequence position:** 18 · **Depends on:** F-004.2

**User Story:** As the system, I want to synchronize official FPL player statistics for each gameweek so that fantasy scoring always reflects official, authoritative data.

Acceptance criteria:
1. Given a scheduled sync run after a gameweek's fixtures have official data available, when it completes, then a `PlayerPerformance` row exists for each player with recorded statistics for that gameweek, tagged `Source = OfficialFpl`, `IsOfficial = true`, and a `RetrievedAt` timestamp (BR-076, BR-077, BR-231).
2. Given official data is later corrected upstream (e.g., a statistic changes after initial publication), when the next sync run detects the delta against the previously stored value, then a new/updated `PlayerPerformance` state is recorded and a `ScoreRecalculated` cascade is triggered downstream rather than silently overwriting consumed data (BR-139, BR-234).
3. Given a player recorded statistics across more than one club within a single gameweek (mid-gameweek transfer) or across more than one fixture (double gameweek), when the sync stores the gameweek total, then it reflects the combined official total for that player for that gameweek, consistent with official FPL treatment (BR-074, BR-288).
4. Given this sync depends on F-004.2's gameweek boundaries, when a gameweek's `RosterLockDeadline` has not yet passed, then no scoring-relevant statistics sync is expected to run yet (statistics only become authoritative post-fixture).

**Security considerations:** same external-source risk as F-004.1; additionally, this data feeds `ScoreOverride` precedence logic (Architecture v1.2 §6.6) — any code path that reads a player statistic for scoring purposes must go through the single `IAuthoritativeValueResolver`, never read `PlayerPerformance` directly, so override precedence can never be bypassed (BR-140–BR-145).

**Technical tasks:** `PlayerPerformance` aggregate and sync job; delta-detection against previously stored values; `ScoreRecalculated` domain event wiring (consumed by Phase 3 Scoring features, not yet built).

---

## F-004.4 — EPL Transfer Handling
**Build sequence position:** 19 · **Depends on:** F-004.1

**User Story:** As the system, I want to correctly handle players moving between EPL clubs or leaving the EPL entirely so that fantasy ownership and replacement eligibility stay accurate.

Acceptance criteria:
1. Given a fantasy-owned player transfers from one EPL club to another EPL club, when the sync detects this, then the player's `SquadPlayer` ownership is unaffected — only the `Player.CurrentClubId` reference is updated (BR-070, BR-072).
2. Given a fantasy-owned player transfers out of the EPL entirely, when the sync detects this, then the player's `EPLStatus` reflects that departure, the player remains part of the FantasyTeam's historical squad record, and the player becomes eligible for replacement determination by a League Administrator (BR-071, BR-259, BR-065 — actual replacement-drafting flow is a Phase 2 feature; this feature only covers detecting and flagging eligibility candidacy).
3. Given a player's gameweek statistics are recorded while associated with more than one club during that gameweek, when scoring reads that data (via F-004.3), then all such points are credited to the FantasyTeam owning the player — never reduced or split (BR-074).

**Security considerations:** none beyond the shared external-source risk (F-004.1).

**Technical tasks:** club-transfer detection during sync; `Player.EPLStatus` transition; emit a candidate event for League Administrator review (actual eligibility-grant action is F-006.4/F-011.2, Phase 2/4).

---

## F-004.5 — Postponed/Abandoned Fixture Handling
**Build sequence position:** 20 · **Depends on:** F-004.2

**User Story:** As the system, I want to follow the official FPL treatment for postponed and abandoned fixtures so that fantasy scoring never diverges from the official record.

Acceptance criteria:
1. Given an EPL fixture is postponed and officially rescheduled into a different gameweek, when the sync processes the fixture, then it is (re-)assigned to the official gameweek FPL uses for scoring purposes, not the originally scheduled gameweek (BR-100, BR-103).
2. Given a user is expected to plan around a known rescheduled fixture, when they view their squad/roster planning screens, then the rescheduled fixture's actual assigned gameweek is what's displayed — the application does not need to auto-adjust past roster submissions, since users are responsible for accounting for known reschedules (BR-101, BR-102).
3. Given a fixture is abandoned and its in-progress statistics are recognized by official FPL scoring, when the sync processes it, then those statistics count (BR-104).
4. Given a fixture is abandoned and subsequently replayed, and official FPL scoring does not recognize the original (abandoned) statistics, when the sync processes the fixture, then the application waits for and uses the replayed fixture's official data instead (BR-105, BR-106).

**Security considerations:** none beyond the shared external-source risk (F-004.1).

**Technical tasks:** gameweek-reassignment handling for rescheduled fixtures; abandoned/replayed fixture state tracking, driven entirely by what the official data source reports rather than independent application logic.

---

# Open Items Raised While Writing This Phase

Two items surfaced during specification that weren't previously flagged and should be resolved before their features are implemented:

1. **F-001.3 (Username Management):** whether a retired user's username becomes available for reuse. This spec assumes **not reusable** as the conservative default — confirm or override.
2. **F-010.3 (Season Goal Prediction):** what happens for a FantasyTeam that never submits a prediction before Season start (AC-3) — the BRD's tie-break rules (BR-124/BR-131) assume every FantasyTeam has a comparable value, but no default/fallback is specified for a non-submission.

Also carried forward from F-001.4: whether account "un-retirement" (self-service reactivation) is in scope — currently assumed out of scope for this release.
