# Fantasy EPL League Manager
## Feature Behavior Specifications — Phase 4 (Operations) — Version 1.0

**Document Status:** Baseline Feature Specifications
**Version:** 1.0
**Inputs:**
- Business Requirements Document v1.7 — authoritative business rules (BR-001…BR-308)
- Architecture and Domain Model v1.4 — aggregates, invariants, module map
- Epic and Feature Backlog v1.0 — feature list, dependencies, build sequence
- Feature Behavior Specifications (Phase 1) v1.1, (Phase 2) v1.1, (Phase 3) v1.1 — hard dependencies

**Scope:** The remaining P1/P2 features from the Backlog: F-013.1 (Historical Season Archive), F-003.6 (League Messaging), F-011.1 (Administrative Audit Log Viewer), F-012.1/F-012.4/F-012.2/F-012.3 (Notifications), and F-013.2 (Retired-User Historical Display). Backlog positions 47–54. This is the final phase — every feature in the Epic and Feature Backlog v1.0 will have a specification once this document is complete.

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
1. Given `AdministrativeAction` rows recorded by every admin-privileged feature built so far, when the Administrator views the audit log, then all are listed chronologically with actor, action type, before/after state, and reason where provided (BR-177–BR-182, BR-149).
2. Given a large volume of actions, when viewed, then the log supports filtering by action type, date range, and affected FantasyTeam (BR-239–BR-242).
3. Given a non-Administrator user, when they attempt to view the League's audit log, then access is denied (BR-161, BR-162).
4. Given an entry generated automatically with no human actor (e.g., BR-308's automatic EPL-exit eligibility grant), when displayed, then it is clearly labeled as system-generated rather than attributed to any specific person (Architecture v1.5 §6.8 — `ActingMembershipId` is nullable for exactly this case).

**Security considerations:** Administrator-only, League-scoped (BR-162, BR-163) — this endpoint is itself a sensitive one, since it exposes the full history of corrections and overrides.

**Technical tasks:** audit-log query/filter endpoint over `AdministrativeAction`; UI for chronological display; explicit handling of a null `ActingMembershipId` as "System."

---

# EPIC-012 — Notifications

## F-012.1 — Notification Preferences Management
**Backlog position:** 50 · **Depends on:** F-001.1

**User Story:** As a user, I want to choose which notification events I receive and through which channel(s) so I control how the application contacts me.

Acceptance criteria:
1. Given the notification event types (Gameweek Reminder, Weekly Score, Weekly Standings), when I configure preferences, then each is independently toggleable per channel — Email or SMS/Text (BR-150, BR-151, BR-155).
2. Given I disable a specific event/channel combination, when that event later fires, then no notification is sent to me on that channel — enforced at delivery time (F-012.4), not merely at preference-storage time (BR-226).
3. Given I change a preference, when the change is saved, then it takes effect immediately for future notifications; any already-queued or already-sent notification is unaffected.

**Security considerations:** preferences are self-service and per-user; object-level check ensures a user can only view/edit their own preferences (BR-163).

**Technical tasks:** `NotificationPreference` CRUD endpoint (aggregate already defined, Architecture §6.9).

---

## F-012.4 — Notification Delivery Infrastructure
**Backlog position:** 51 · **Depends on:** F-012.1

**User Story:** As the system, I want to deliver notifications asynchronously with retry-on-failure so gameplay operations are never blocked by notification delivery.

Acceptance criteria:
1. Given a domain event that should trigger a notification (e.g., a Gameweek deadline approaching, a `GameweekScore` calculation completing), when it fires, then a `NotificationRequest` row is written in the **same transaction** as the triggering change — the outbox pattern — rather than sent synchronously inline (BR-224).
2. Given a `NotificationRequest`, when a background worker processes it, then it checks `NotificationPreference` before sending — a disabled channel/event combination is never sent (BR-226).
3. Given delivery fails (e.g., a provider error), when this happens, then the failure is logged and retried per a configured backoff policy, up to a maximum attempt count, without blocking any user-facing gameplay operation (BR-225).
4. Given the specific email/SMS provider has not yet been selected (Architecture §15's remaining open item), when this feature is built, then it is built against a provider-agnostic sending interface so the concrete integration can be swapped in later without touching the outbox/retry mechanics.

**Security considerations:** delivery failures and retries must not leak notification content or recipient contact details into logs beyond what's operationally necessary (BR-171).

**Technical tasks:** `NotificationRequest` outbox table and background dispatcher; a provider-agnostic `INotificationSender`-style interface; configurable retry/backoff policy.

---

## F-012.2 — Gameweek Reminder Notifications
**Backlog position:** 52 · **Depends on:** F-012.1, F-007.3

**User Story:** As a user, I want a reminder before my gameweek roster deadline so I don't forget to submit.

Acceptance criteria:
1. Given a FantasyTeam has not yet submitted a valid roster as a Gameweek's deadline approaches, when the configured reminder lead time before the deadline is reached, then a `GameweekReminder` notification is queued for that user, subject to their preferences (BR-152, BR-150).
2. Given the FantasyTeam has already submitted a valid roster, when the reminder window is reached, then no reminder is sent — avoiding unnecessary noise.
3. Given the reminder lead time (how far before the deadline the reminder fires), when configured, then **the BRD does not currently list this as one of the League/Season-configurable parameters (BR-291's table) — flag as an open item: should it be configurable per League/Season, or a fixed system-wide default?**

**Security considerations:** none beyond the preference-based suppression already covered by F-012.1/F-012.4.

**Technical tasks:** a scheduled check (reusing the same scheduled-sweep pattern as ADR-012) evaluating unsubmitted rosters against the reminder lead time.

---

## F-012.3 — Weekly Score/Standings Notifications
**Backlog position:** 53 · **Depends on:** F-012.1, F-008 (Scoring), F-010 (Standings)

**User Story:** As a user, I want to receive my weekly score and league table position after they're finalized, without having to log in to check.

Acceptance criteria:
1. Given a FantasyTeam's `GameweekScore` is finalized for a gameweek, when this occurs, then a `WeeklyScore` notification is queued for that user, subject to preferences (BR-153).
2. Given `LeagueStanding` is recalculated after a gameweek's results finalize, when this occurs, then a `WeeklyStandings` notification (including the user's current position) is queued, subject to preferences (BR-154).
3. Given both notifications could fire from the same gameweek's finalization, when queued, then they are distinct notification events — a user may enable one and not the other (BR-155).

**Security considerations:** none beyond preference-based suppression.

**Technical tasks:** hook into the existing `ScoreCalculated` and `StandingsRecalculated` domain events (Architecture §7) to enqueue `NotificationRequest` rows — no new scoring/standings logic required, purely a consumer of events that already exist.

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
1. Given a retired User who participated in a historical Season, when their historical records (drafts, rosters, standings) are displayed, then they resolve correctly by `UserId` and display that User's identity as it was at the applicable point (BR-014, BR-175, BR-272) — the exact historical-username-display behavior (username-at-event-time vs. current username) is governed by the still-open BRD §54 "Historical Username Display" decision, not newly resolved here.
2. Given the UI displays a retired user, when rendered, then it may (not must) visually flag them as retired — a display nicety, not a strict business rule.
3. Given a retired user's username has since been reused by a different individual (BR-298), when historical records are displayed, then the two individuals' histories are never conflated — display logic must join on `UserId`, never on the username string (cross-reference F-001.3 AC-3, Phase 1).

**Security considerations:** same League-scoped read access as other historical data.

**Technical tasks:** display logic joins strictly on `UserId`; do not implement a design for BR-§54's open decision without first resolving it (both display modes should remain feasible until it's settled).

---

# Open Items Raised While Writing This Phase

1. **F-012.2 (Gameweek Reminder):** whether the reminder lead time (how far before the deadline it fires) should be added to the League/Season-configurable parameter table (BRD BR-291) or remain a fixed system-wide default. This wasn't part of the original configurability request; flagging it now since it's genuinely the same *kind* of parameter as the ones already made configurable.

No other new items surfaced in this phase — the two structural architecture gaps found (missing `LeagueMessage` aggregate, non-nullable `ActingMembershipId`) were fixed directly rather than flagged as open questions, since they're completeness fixes, not business decisions.

This completes specifications for every feature in the Epic and Feature Backlog v1.0.
