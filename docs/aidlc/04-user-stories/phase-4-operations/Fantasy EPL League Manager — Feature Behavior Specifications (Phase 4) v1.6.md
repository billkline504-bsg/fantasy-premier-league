# Fantasy EPL League Manager
## Feature Behavior Specifications — Phase 4 (Operations) — Version 1.6

**Document Status:** Baseline Feature Specifications
**Version:** 1.6
**Inputs:**
- Business Requirements Document v1.17 — authoritative business rules (BR-001…BR-339)
- Architecture and Domain Model v1.15 — aggregates, invariants, module map
- Epic and Feature Backlog v1.10 — feature list, dependencies, build sequence
- Feature Behavior Specifications (Phase 1) v1.3, (Phase 2) v1.3, (Phase 3) v1.3 — hard dependencies

**Scope:** The remaining P1/P2 features from the Backlog: F-013.1 (Historical Season Archive), F-003.6 (League Messaging), F-011.1 (Administrative Audit Log Viewer), F-011.3 (Security & Abuse Monitoring — added in v1.4), F-012.1/F-012.4/F-012.2/F-012.3 (Notifications), and F-013.2 (Retired-User Historical Display). Backlog positions 47–54, plus 56 (F-011.3, appended to the Backlog after this phase's original numbering was finalized). F-012.1's acceptance criteria were extended in v1.5 (BR-338–BR-339, BRD v1.17); this version propagates the same League-scoping to F-012.2/F-012.3/F-012.4, whose acceptance criteria still described a single global preference check. No new feature or position is added. This is the final phase — every feature in the Epic and Feature Backlog v1.0 will have a specification once this document is complete.

**Note on architecture completeness:** two structural gaps surfaced while writing this phase, both fixed directly in Architecture v1.5 (not treated as open business questions, since they're completeness fixes rather than decisions): a `LeagueMessage` aggregate was never defined despite EPIC-003 always having included messaging in its scope, and `AdministrativeAction.ActingMembershipId` needed to become nullable to represent system-generated entries (e.g., BR-308's automatic EPL-exit eligibility grants, which have no human actor). See Architecture v1.5 §6.2/§6.8.

---

# EPIC-003 — League & Season Management (remaining feature)

## F-003.6 — League Messaging
**Backlog position:** 48 · **Depends on:** F-003.3

**User Story:** As a League Administrator, I want to publish messages visible to my league members so I can communicate announcements and rule reminders.

Acceptance criteria:
1. Given the Administrator, when they publish a League message, then it is stored as a `LeagueMessage` and visible to all active members of that League (BR-221).
2. Given a message published in League A, when a User who is not a member of League A attempts to view it, then access is denied (BR-222, BR-161).
3. Given the application's historical retention policy, when messages age, then they are retained under the same policy as other historical league data (BR-223, BR-174).
4. Given a message, when displayed, then it shows the Administrator's League-facing identity (`[League Icon] [Username]`, per BR-277) and a timestamp — never the raw account email (BR-015).

**Security considerations:** publishing is Administrator-only (BR-162); viewing is League-membership-scoped (BR-161, BR-222); message body text is user-supplied content and must be treated as untrusted input — validated server-side and output-encoded on display to prevent stored XSS (BR-165, BR-167).

**Technical tasks:** `LeagueMessage` aggregate (new — added to Architecture v1.5 §6.2); publish/list endpoints scoped by `LeagueId`.

---

# EPIC-011 — Corrections & Administration (remaining feature)

## F-011.1 — Administrative Audit Log Viewer
**Backlog position:** 49 · **Depends on:** F-007.4, F-008.5 (and, by extension, every admin-privileged feature already built: F-003.5 configuration changes, F-005.3 timer extensions, F-006.4/F-011.2 eligibility grants)

**User Story:** As a League Administrator, I want to view a chronological, filterable log of administrative actions taken in my League so I can review what changed, when, and why.

Acceptance criteria:
1. Given `AdministrativeAction` rows recorded by every admin-privileged feature built so far, when the Administrator views the audit log, then all are listed chronologically with actor, action type, a summarized description of the change, and reason where provided (BR-177–BR-182, BR-149).
2. Given a large volume of actions, when viewed, then the log supports filtering by action type, date range, and affected FantasyTeam — or "League Settings" for an action not scoped to a specific FantasyTeam, such as a `ConfigurationChanged` entry (BR-321; corrects the v1.1 citation of BR-239–BR-242, which are general logging/observability rules and do not themselves establish a filtering requirement).
3. Given a non-Administrator user, when they attempt to view the League's audit log, then access is denied (BR-161, BR-162).
4. Given an entry generated automatically with no human actor (e.g., BR-308's automatic EPL-exit eligibility grant), when displayed, then it is clearly labeled as system-generated rather than attributed to any specific person (Architecture v1.5 §6.8 — `ActingMembershipId` is nullable for exactly this case).
5. Given any entry, when the Administrator expands it, then its full recorded `BeforeState`/`AfterState` is revealed in place, without navigating away from the list (BR-322).

**Security considerations:** Administrator-only, League-scoped (BR-162, BR-163) — this endpoint is itself a sensitive one, since it exposes the full history of corrections and overrides.

**Technical tasks:** audit-log query/filter endpoint over `AdministrativeAction`, parameterized by `actionType`/`from`/`to`/`fantasyTeamId` (Architecture v1.9 §6.8/§9.2); UI for chronological display with per-entry detail expansion, backed entirely by data the endpoint already returns — no separate detail call; explicit handling of a null `ActingMembershipId` as "System," and of a non-FantasyTeam-scoped `TargetEntityType` as "League Settings" for filtering purposes.

---

## F-011.3 — Security & Abuse Monitoring (System Administrator)
**Backlog position:** 56 (added in Backlog v1.6 — see that document's build-sequence note) · **Depends on:** F-001.1 (authentication must exist)

**User Story:** As a System Administrator, I want visibility into the platform's rate-limiting and CSRF protections — configuration and recent enforcement events, not just the fact that they exist — so I can confirm abuse protection is actually working and investigate anything that looks off.

Acceptance criteria:
1. Given the platform's configured rate limits (per endpoint group — auth endpoints, draft picks, general authenticated traffic), when a System Administrator views this screen, then the current limit, window, and scope for each is displayed (BR-327).
2. Given a rate limit is enforced (a request blocked), when this occurs, then a `SecurityEvent` row is written (`EventType = RateLimitBlocked`) capturing the endpoint, scope (e.g., IP + username), a human-readable detail, and timestamp (BR-327, BR-170).
3. Given recorded `SecurityEvent` rows, when a System Administrator views this screen, then recent events are listed in reverse-chronological order.
4. Given the platform's current authentication model, when a System Administrator views this screen, then it shows whether cookie-based authentication flows are in use and, if so, confirms anti-CSRF protections are active for them (BR-328).
5. Given a caller who is not a System Administrator (including a League Administrator), when they attempt to view this screen or its underlying endpoints, then access is denied — this is platform-level visibility, not exposed through any League-scoped screen (BR-300).

**Security considerations:** System-Administrator-only (BR-300, same authorization gate as F-001.4's reactivation endpoint); this endpoint is itself sensitive, since it describes the platform's abuse-protection posture — read-only, no action is taken from this screen.

**Technical tasks:** `SecurityEvent` read-model populated by the rate-limiting middleware on every enforced block (Architecture v1.12 §6.8); `GET /api/v1/admin/security/rate-limits`, `GET /api/v1/admin/security/events`, and `GET /api/v1/admin/security/csrf-status` endpoints (Architecture v1.12 §9.2), all gated on `IsSystemAdministrator`.

---

# EPIC-012 — Notifications

## F-012.1 — Notification Preferences Management
**Backlog position:** 50 · **Depends on:** F-001.1, F-003.3 (LeagueMembership must exist, BR-338), F-002.4 (per-League Profile section to hold these settings, BR-339)

**User Story:** As a user who may belong to more than one League, I want to choose which notification events I receive and through which channel(s) independently for each League, so a busy League doesn't force notification settings on a quieter one I'm also part of.

Acceptance criteria:
1. Given the notification event types (Gameweek Reminder, Weekly Score, Weekly Standings), when I configure preferences for one of my Leagues, then each is independently toggleable per channel — Email or SMS/Text — for that League only (BR-150, BR-151, BR-155).
2. Given I belong to more than one League, when I change a preference for one League, then the same event/channel combination in any other League I belong to is unaffected — each League's preferences are a fully independent set (BR-338).
3. Given I disable a specific event/channel combination for a given League, when that event later fires for that League, then no notification is sent to me on that channel — enforced at delivery time (F-012.4), not merely at preference-storage time (BR-226).
4. Given I change a preference, when the change is saved, then it takes effect immediately for future notifications in that League; any already-queued or already-sent notification is unaffected.
5. Given the Profile screen, when I view it, then each League I belong to has its own "Notifications for This League" section, alongside that League's icon settings, rather than a single combined notifications screen (BR-339).

**Security considerations:** preferences are self-service and scoped to one League Membership; object-level check ensures a user can only view/edit `NotificationPreference` rows for their own `LeagueMembershipId`s (BR-163).

**Technical tasks:** `NotificationPreference` CRUD endpoint, keyed by `LeagueMembershipId` (Architecture §6.9); seed a disabled-by-default row per event type/channel when a `LeagueMembership` is created; Profile screen renders one "Notifications for This League" block per League Membership (Architecture v1.15 §6.1, §6.9).

---

## F-012.4 — Notification Delivery Infrastructure
**Backlog position:** 51 · **Depends on:** F-012.1

**User Story:** As the system, I want to deliver notifications asynchronously with retry-on-failure so gameplay operations are never blocked by notification delivery.

Acceptance criteria:
1. Given a domain event that should trigger a notification (e.g., a Gameweek deadline approaching, a `GameweekScore` calculation completing), when it fires, then a `NotificationRequest` row is written in the **same transaction** as the triggering change — the outbox pattern — rather than sent synchronously inline (BR-224). The triggering event always occurs within one specific `LeagueMembership` context (the FantasyTeam whose deadline is approaching, or whose score was just calculated), so `NotificationRequest.LeagueMembershipId` is populated from that context, never left blank or inferred later (BR-338).
2. Given a `NotificationRequest`, when a background worker processes it, then it looks up `NotificationPreference` by that request's `LeagueMembershipId` (not by `UserId` alone) before sending — a disabled channel/event combination *for that League* is never sent, and a User's setting in a different League has no bearing on this request (BR-226, BR-338).
3. Given delivery fails (e.g., a provider error), when this happens, then the failure is logged and retried per a configured backoff policy, up to a maximum attempt count, without blocking any user-facing gameplay operation (BR-225).
4. Given the specific email/SMS provider has not yet been selected (Architecture §15's remaining open item), when this feature is built, then it is built against a provider-agnostic sending interface so the concrete integration can be swapped in later without touching the outbox/retry mechanics.

**Security considerations:** delivery failures and retries must not leak notification content or recipient contact details into logs beyond what's operationally necessary (BR-171).

**Technical tasks:** `NotificationRequest` outbox table (now `LeagueMembershipId`-bearing, Architecture v1.15 §6.9) and background dispatcher; preference lookup keyed on `(LeagueMembershipId, EventType, Channel)`, not `(UserId, EventType, Channel)`; a provider-agnostic `INotificationSender`-style interface; configurable retry/backoff policy.

---

## F-012.2 — Gameweek Reminder Notifications
**Backlog position:** 52 · **Depends on:** F-012.1, F-007.3

**User Story:** As a user, I want a reminder before my gameweek roster deadline in each League I play in, so I don't forget to submit — independently of whatever I've set for my other Leagues.

Acceptance criteria:
1. Given a FantasyTeam has not yet submitted a valid roster as a Gameweek's deadline approaches, when the configured reminder lead time before the deadline is reached, then a `GameweekReminder` notification is queued with that FantasyTeam's `LeagueMembershipId`, subject to that League Membership's preferences — not the User's preferences in any other League they belong to (BR-152, BR-150, BR-338).
2. Given the FantasyTeam has already submitted a valid roster, when the reminder window is reached, then no reminder is sent — avoiding unnecessary noise.
3. Given the reminder lead time, when determined, then it reads `SeasonConfiguration.GameweekReminderLeadTime` (default 24 hours before the roster lock deadline) — a League/Season-configurable parameter, consistent with every other numeric/date threshold in the application (BR-309, BRD v1.8).
4. Given a User has multiple FantasyTeams across multiple Leagues with independent Gameweek schedules, when each League's deadline approaches, then each reminder is evaluated and queued (or suppressed) using that FantasyTeam's own League Membership's preferences, entirely independently of the others (BR-338).

**Security considerations:** none beyond the preference-based suppression already covered by F-012.1/F-012.4.

**Technical tasks:** a scheduled check (reusing the same scheduled-sweep pattern as ADR-012) evaluating unsubmitted rosters against `SeasonConfiguration.GameweekReminderLeadTime`, per FantasyTeam; the resulting `NotificationRequest` carries that FantasyTeam's `LeagueMembershipId` (BR-338).

---

## F-012.3 — Weekly Score/Standings Notifications
**Backlog position:** 53 · **Depends on:** F-012.1, F-008 (Scoring), F-010 (Standings)

**User Story:** As a user, I want to receive my weekly score and league table position after they're finalized in each League I play in, without having to log in to check — and without one League's noisy notifications forcing my hand in another.

Acceptance criteria:
1. Given a FantasyTeam's `GameweekScore` is finalized for a gameweek, when this occurs, then a `WeeklyScore` notification is queued with that FantasyTeam's `LeagueMembershipId`, subject to that League's preferences (BR-153, BR-338).
2. Given `LeagueStanding` is recalculated after a gameweek's results finalize, when this occurs, then a `WeeklyStandings` notification (including the user's current position in that League) is queued with that same `LeagueMembershipId`, subject to that League's preferences (BR-154, BR-338).
3. Given both notifications could fire from the same gameweek's finalization, when queued, then they are distinct notification events — a user may enable one and not the other, independently per League (BR-155, BR-338).
4. Given a User plays in more than one League, when one League's Gameweek finalizes before another's (e.g., different Season schedules), then that League's notifications are evaluated and sent on its own preferences, without waiting for or being affected by any other League (BR-338).

**Security considerations:** none beyond preference-based suppression.

**Technical tasks:** hook into the existing `ScoreCalculated` and `StandingsRecalculated` domain events (Architecture §7) to enqueue `NotificationRequest` rows carrying the triggering FantasyTeam's `LeagueMembershipId` (BR-338) — no new scoring/standings logic required, purely a consumer of events that already exist.

---

# EPIC-013 — Reporting & History

## F-013.1 — Historical Season Archive
**Backlog position:** 47 · **Depends on:** a completed Season (end of the Phase 3 flow)

**User Story:** As a League member, I want completed seasons' standings, rosters, drafts, and scores to remain accessible indefinitely so I can look back at league history.

Acceptance criteria:
1. Given a Season transitions to `Status = Completed`, when historical data is queried, then `Squad`, `Draft`, `GameweekRoster`, `GameweekScore`, `HeadToHeadMatch`, and `LeagueStanding` records for that Season remain fully queryable and unaltered (BR-174, BR-217).
2. Given the minimum ten-year retention period, when data reaches that age, then it is **not** automatically purged — no deletion job exists for any competitive/historical table (BR-174, Architecture §8.2).
3. Given a User's username or icon changes after a Season completes, when historical records for that Season are viewed, then they are unaffected by the change (BR-176, BR-257).
4. Given a completed Season's `SeasonConfiguration`, when queried, then it still returns exactly the values that applied to that Season, regardless of later League-level default changes (BR-296).
5. Given multiple completed Seasons for the same League, when browsing history, then each Season's data is independently accessible and clearly distinguishable.

**Security considerations:** League-membership-scoped read access (BR-161) — historical data is still league-facing, so BR-015's private-field exclusions apply exactly as they do to current-season data.

**Technical tasks:** read-model/query endpoints for historical Season browsing; a defensive check confirming no destructive retention/cleanup job exists anywhere in the codebase (this is a thing to verify is *absent*, not a thing to build).

---

## F-013.2 — Retired-User Historical Display
**Backlog position:** 54 · **Depends on:** F-013.1

**User Story:** As a League member viewing historical results, I want retired users' contributions still clearly and correctly displayed, so history remains accurate even after someone leaves the platform.

Acceptance criteria:
1. Given a retired User who participated in a historical Season, when their historical records (drafts, rosters, standings) are displayed, then they resolve correctly by `UserId` and display the username that was active in `UsernameHistory` at the time each record was created (BR-014, BR-175, BR-272, BR-326) — the same resolution mechanism used for any User, retired or not.
2. Given the UI displays a retired user, when rendered, then it may (not must) visually flag them as retired — a display nicety, not a strict business rule.
3. Given a retired user's username has since been reused by a different individual (BR-298), when historical records are displayed, then the two individuals' histories are never conflated — display logic must join on `UserId`, never on the username string (cross-reference F-001.3 AC-3, Phase 1), and each individual's `UsernameHistory` rows are scoped to their own `UserId` regardless of which username string they held at any given time.

**Security considerations:** same League-scoped read access as other historical data.

**Technical tasks:** display logic joins strictly on `UserId`, resolving the displayed username via `UsernameHistory` (Architecture v1.11 §6.1) rather than `User.Username` directly — this was previously left open pending BRD §54, now resolved as BR-326.

---

# Resolved Items

The one item raised while writing this phase was resolved in BRD v1.8 (§65) and Architecture v1.6:

1. **F-012.2 (Gameweek Reminder):** the reminder lead time is now a League/Season-configurable parameter, `SeasonConfiguration.GameweekReminderLeadTime`, defaulting to 24 hours before the roster lock deadline (BR-309).

The two structural architecture gaps found (missing `LeagueMessage` aggregate, non-nullable `ActingMembershipId`) were fixed directly in Architecture v1.5 rather than treated as open questions, since they were completeness fixes, not business decisions.

**Added in v1.2 (not an open item raised while originally writing this phase):** reviewing an illustrative HTML mock-up of F-011.1's audit log screen surfaced two things — a genuine new capability (per-entry detail expansion) and a citation defect (this feature's filtering acceptance criterion cited BR-239–BR-242, which are general application/error-logging and observability rules that never actually established a filtering requirement). Both are now closed as BR-321–BR-322 (BRD v1.11), with F-011.1's acceptance criteria and technical tasks above updated accordingly, following the same formalization process used for the Draft Player Pool (Phase 2 v1.2) and Squad View (Phase 3 v1.2) gaps.

**Added in v1.3:** F-013.2 AC-1 previously deferred to the open BRD §54 "Historical Username Display" decision, and its technical tasks explicitly warned against implementing either display mode before that decision was made. Reviewing an illustrative HTML mock-up built to compare the two options against a concrete renamed-manager example made the tradeoff clear enough to resolve: BR-326 (BRD v1.13) settles it as username-at-time-of-event, backed by the new `UsernameHistory` entity (Architecture v1.11 §6.1). F-013.2's acceptance criteria and technical tasks above are updated accordingly — this is the first BRD §54 item resolved since Phase 1 first flagged it as open.

**Added in v1.4 (not an open item raised while originally writing this phase):** reviewing an illustrative HTML mock-up of a System Administrator "Security & Abuse Protection" screen surfaced a real gap — BR-168 (CSRF) and BR-169 (rate limiting) have existed since early in the BRD, but neither ever said anyone could actually *see* the resulting configuration or enforcement events. Closed as BR-327–BR-328 (BRD v1.14) and a brand-new feature, F-011.3, since nothing in the existing Backlog owned this — the same kind of gap as F-007.5 (Squad View, Phase 3 v1.2), where a real capability existed with no owning Feature at all.

No open items remain in any phase. This completes specifications for every feature in the Epic and Feature Backlog v1.0.

**Added in v1.5 (not an open item raised while originally writing this phase):** reviewing an illustrative HTML mock-up of the Profile screen surfaced that Section 26's notification rules (BR-150–BR-155) never said whether preferences were global per-User or independent per-League, and the mock-up had already built them the latter way — nested inside each League's own section of the Profile screen. Closed as BR-338–BR-339 (BRD v1.17), extending F-012.1's existing acceptance criteria (AC-2 and AC-5 above are new; AC-1/AC-3/AC-4 are reworded to be explicit about per-League scope) rather than creating a new feature, since F-012.1 already owns this capability. `NotificationPreference` is re-keyed from `UserId` to `LeagueMembershipId` (Architecture v1.15 §6.9) — existing rows would need a one-time migration seeding one row per (User, LeagueMembership, EventType, Channel) at deploy time, copying the prior global value as each League's starting point.

**Added in v1.6 (not an open item raised while originally writing this phase, and not itself a new business rule):** v1.5 re-keyed `NotificationPreference`/`NotificationRequest` to `LeagueMembershipId` and updated F-012.1's acceptance criteria accordingly, but left F-012.2, F-012.3, and F-012.4 — the three features that actually *generate* and *deliver* notifications — still describing a single, ambiguous "the user's preferences" check. This revision propagates the same League-scoping into all three: each queued `NotificationRequest` now explicitly carries the triggering FantasyTeam's `LeagueMembershipId` (F-012.2 AC-1/AC-4, F-012.3 AC-1/AC-2/AC-4, F-012.4 AC-1), and the delivery-time preference lookup is keyed on that field rather than `UserId` alone (F-012.4 AC-2). No new BR was needed — BR-338 (BRD v1.17) already establishes the rule; this was purely a downstream-consistency gap across sibling features that share the same underlying entity.
