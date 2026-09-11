# Fantasy EPL League Manager
## Epic and Feature Backlog — Version 1.1

**Document Status:** Baseline Backlog
**Version:** 1.1
**Inputs:**
- Business Requirements Document v1.5 (`Fantasy EPL League Manager — Business Requirements Document v1.5.md`) — authoritative business rules (BR-001…BR-297); F-005.5's BR references were refreshed against BRD v1.9 (BR-310–BR-313) without otherwise changing this document's baseline
- Architecture and Domain Model v1.2 (`Fantasy EPL League Manager — Architecture and Domain Model v1.2.md`) — bounded contexts, aggregates, module map
- AIDLC Domain & Requirements Specification v1.0 — original Epic list (EPIC-001…EPIC-013), refined here into features

**Purpose:** Refine the Domain Specification's epic list into a prioritized, dependency-ordered feature backlog, tracing every feature back to the BR-### rules it implements. This is the artifact the Architecture doc's §16 recommends as the next step after architecture. It does **not** contain user stories or Gherkin-style acceptance criteria — those belong to the next artifact (feature-level behavior specifications), sequenced here but not yet written.

---

# 1. Prioritization Approach

Two orderings are used together:

1. **MoSCoW priority** — Must / Should / Could, scoped against BRD BR-255 ("Complete Season" acceptance criteria: League Creation → Invitation → Initial Draft → Squads → Weekly Rosters → Captain → FPL Scoring → H2H Results → Standings → Transfer Window → Secondary Draft → Replacement Selections → Final Standings → Historical Season). Anything required for that end-to-end flow to work is **Must Have (P0)** for the initial release; anything that improves the experience around that flow without blocking it is **Should Have (P1)**; anything that only matters once multiple seasons/leagues exist in production is **Could Have (P2)**.
2. **Dependency-respecting build order** — which features must exist before another can be built or tested, independent of MoSCoW priority or epic numbering. Section 5 gives the actual recommended build sequence; it does not match epic numbering, because some later-numbered epics (e.g., Secondary Draft) depend on earlier-numbered ones being complete (e.g., Standings), while some same-epic features can be parallelized.

Security, authorization, and audit requirements (BR-156–BR-182, AP-001–AP-005) are **not** a separate epic. They are cross-cutting acceptance criteria embedded in every feature below (per ADR "API as Security Boundary") — a feature is not done until its object-level authorization and, where applicable, audit logging are implemented and tested.

---

# 2. Epic List

| Epic | Scope | Phase | Priority |
|---|---|---|---|
| EPIC-001 Identity & Authentication | Accounts, authentication, retirement | 1 — Foundation | P0 |
| EPIC-002 User Profile & FantasyTeam | Global defaults plus league-specific username/icon/team identity | 1 — Foundation | P0 |
| EPIC-003 League & Season Management | League lifecycle, membership, invitations, seasons, **League/Season configuration** | 1 — Foundation | P0 |
| EPIC-004 EPL/FPL Data | Players, clubs, fixtures, gameweeks, official statistics | 1 — Foundation | P0 |
| EPIC-005 Initial Draft | 25-player (configurable) randomized snake draft | 2 — Draft & Squad | P0 |
| EPIC-006 Secondary & Replacement Draft | Standings-ordered draft and expanding squad replacements | 2 — Draft & Squad | P0 |
| EPIC-007 Gameweek Roster | 15-player (configurable) roster, positional minimums, captain, lock | 3 — Weekly Gameplay | P0 |
| EPIC-008 Scoring Engine | FPL points, top-11, captain, Fantasy Goals, overrides | 3 — Weekly Gameplay | P0 |
| EPIC-009 H2H Competition | Schedule, matchups, results, league points | 3 — Weekly Gameplay | P0 |
| EPIC-010 Standings & Tie-Breaks | Ranking, tie-break hierarchy, season goal prediction | 3 — Weekly Gameplay | P0 |
| EPIC-011 Corrections & Administration | Exceptional corrections, audit surfacing | 4 — Operations | P0/P1 (mixed, see below) |
| EPIC-012 Notifications | Email/SMS preferences and delivery | 4 — Operations | P1 |
| EPIC-013 Reporting & History | Current and historical competition information | 4 — Operations | P1/P2 (mixed) |

EPIC-003 absorbs the League/Season Configuration capability added in BRD v1.5 (BR-290–BR-297) as its own feature rather than a new epic, since `LeagueConfiguration`/`SeasonConfiguration` are owned by the League & Season bounded context (Architecture v1.2 §6.2).

---

# 3. Bounded-Context / Epic Alignment

| Epic | Primary Bounded Context (Architecture v1.2 §5) |
|---|---|
| EPIC-001 | Identity & User |
| EPIC-002 | Identity & User + Fantasy Team |
| EPIC-003 | League & Season |
| EPIC-004 | Player & EPL Data |
| EPIC-005, EPIC-006 | Draft Management |
| EPIC-007 | Roster Management |
| EPIC-008 | Scoring |
| EPIC-009, EPIC-010 | Competition |
| EPIC-011 | Corrections & Administration |
| EPIC-012 | Notifications |
| EPIC-013 | Reporting & History |

This lines up 1:1, confirming the epic boundaries don't cross module boundaries drawn in the architecture doc.

---

# 4. Features by Epic

Each feature lists: the BR-### rules it traces to, what it depends on, and a rough relative size (S/M/L) for later estimation refinement.

## EPIC-001 — Identity & Authentication

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-001.1 | User registration & login | BR-001–BR-004, BR-156, BR-158, BR-159 | — | P0 | M |
| F-001.2 | Password reset | BR-284, BR-285 | F-001.1 | P0 | S |
| F-001.3 | Username management (change, uniqueness) | BR-003, BR-004, BR-265, BR-266, BR-270 | F-001.1 | P0 | S |
| F-001.4 | User retirement (soft delete) | BR-013, BR-014, BR-175 | F-001.1 | P0 | S |
| F-001.5 | Private data protection (email/auth never league-facing) | BR-015 | F-001.1 | P0 | S |

## EPIC-002 — User Profile & FantasyTeam

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-002.1 | Default profile icon selection | BR-006, BR-011, BR-267 | F-001.1 | P0 | S |
| F-002.2 | League-specific icon override | BR-007–BR-010, BR-203, BR-268, BR-274–BR-276 | F-002.1, F-003.3 | P0 | M |
| F-002.3 | FantasyTeam creation & uniqueness | BR-018–BR-020, BR-193, BR-269, BR-277 | F-003.3, F-003.4 | P0 | M |
| F-002.4 | Profile/league context switching UI | BR-031, BR-199–BR-202 | F-002.3 | P0 | S |

## EPIC-003 — League & Season Management

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-003.1 | League creation | BR-023, BR-024, BR-026 | F-001.1 | P0 | M |
| F-003.2 | League invitation & acceptance | BR-027–BR-029, BR-293 | F-003.1 | P0 | M |
| F-003.3 | League membership management (join/leave, single admin) | BR-017, BR-020–BR-022, BR-025, BR-161, BR-162, BR-283 | F-003.1, F-003.2 | P0 | M |
| F-003.4 | Season creation & reuse | BR-032, BR-033, BR-092 | F-003.1 | P0 | M |
| F-003.5 | League/Season configuration management | BR-290–BR-297 | F-003.1, F-003.4 | P0 | L |
| F-003.6 | League messaging | BR-221–BR-223 | F-003.3 | P1 | S |

**Note:** F-003.5 is large because it must expose configuration surfaces for parameters that live conceptually in other epics (positional minimums, draft timer, league points, etc. — Architecture v1.2 §6.2). Build it alongside the owning epics rather than fully upfront: land the `LeagueConfiguration`/`SeasonConfiguration` value objects and the League-level default/audit/lock mechanics with F-003.5, then add each parameter's read path when its owning feature (e.g., F-005.1 for squad size, F-007.1 for roster size) is built.

## EPIC-004 — EPL/FPL Data

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-004.1 | Player/club reference data sync | BR-227, BR-228, BR-232, BR-233, BR-289 | — | P0 | M |
| F-004.2 | Fixture & gameweek sync | BR-092, BR-229, BR-230 | F-004.1 | P0 | M |
| F-004.3 | Player statistics sync (official scoring ingestion) | BR-076, BR-077, BR-231–BR-234 | F-004.2 | P0 | L |
| F-004.4 | EPL transfer handling (club moves, EPL exits) | BR-066, BR-070–BR-072, BR-259 | F-004.1 | P0 | M |
| F-004.5 | Postponed/abandoned fixture handling | BR-100–BR-106 | F-004.2 | P0 | M |

## EPIC-005 — Initial Draft

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-005.1 | Draft setup & randomized order | BR-053–BR-055, BR-291 (squad size) | F-003.5, F-004.1 | P0 | M |
| F-005.2 | Snake draft pick flow & ownership enforcement | BR-052, BR-059, BR-191, BR-192, AP-009, AP-010 | F-005.1 | P0 | L |
| F-005.3 | Draft timer & admin extension | BR-057, BR-058, BR-206 | F-005.2 | P0 | M |
| F-005.4 | Draft pick timeout / makeup-pick handling | BR-282 | F-005.3 | P0 | M |
| F-005.5 | Draft UX (order, current turn, history, ownership, player-pool filter/search/sort/stats) | BR-204, BR-205, BR-207, BR-208, BR-310–BR-313 | F-005.2 | P0 | M |

## EPIC-006 — Secondary & Replacement Draft

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-006.1 | Secondary draft scheduling | BR-069, BR-281 | F-004.2, F-003.5 | P0 | M |
| F-006.2 | Secondary draft order (standings snapshot) | BR-056, BR-136–BR-138 | F-010.1 (Standings), F-005.2 | P0 | M |
| F-006.3 | Secondary draft pick flow | BR-060–BR-062, BR-198 | F-005.2 (shared draft engine), F-006.2 | P0 | M |
| F-006.4 | Replacement eligibility & drafting | BR-063–BR-068, BR-260–BR-264, BR-287 | F-006.3 | P0 | L |

**Note the cross-epic dependency:** F-006.2 depends on Standings (EPIC-010, numbered later) already producing a valid ranking. Do not schedule Secondary Draft implementation before Standings — see Section 5's actual build order.

## EPIC-007 — Gameweek Roster

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-007.1 | Weekly roster submission | BR-037, BR-041, BR-196, BR-211, BR-279, BR-291 | F-005.2 (squad must exist) | P0 | L |
| F-007.2 | Captain selection | BR-045, BR-046, BR-195, BR-212 | F-007.1 | P0 | S |
| F-007.3 | Roster lock & deadline | BR-093–BR-096, BR-213, BR-214 | F-007.1, F-004.2 (kickoff times) | P0 | M |
| F-007.4 | Administrator roster correction | BR-097–BR-099, BR-146–BR-149 | F-007.3 | P0 | M |

## EPIC-008 — Scoring Engine

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-008.1 | Official FPL score ingestion & application | BR-073–BR-077, BR-079, BR-288 | F-004.3, F-007.3 | P0 | L |
| F-008.2 | Starting XI / bench determination | BR-038–BR-040, BR-043, BR-044 | F-008.1 | P0 | M |
| F-008.3 | Captain scoring | BR-047, BR-048, BR-050, BR-051 | F-008.2 | P0 | S |
| F-008.4 | Fantasy Goals For/Against calculation | BR-080–BR-091 | F-008.1 | P0 | L |
| F-008.5 | Score corrections & administrator overrides | BR-139–BR-145 | F-008.1 | P0 | L |

## EPIC-009 — H2H Competition

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-009.1 | Schedule generation | BR-107–BR-110 | F-002.3, F-003.4 | P0 | M |
| F-009.2 | Match result calculation | BR-111–BR-114 | F-009.1, F-008 (scores) | P0 | M |
| F-009.3 | League points allocation | BR-115–BR-117, BR-291 | F-009.2 | P0 | S |
| F-009.4 | Schedule & match result display | BR-218–BR-220 | F-009.2 | P0 | S |

## EPIC-010 — Standings & Tie-Breaks

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-010.1 | Standings calculation | BR-118, BR-215, BR-216 | F-009.3 | P0 | M |
| F-010.2 | Tie-break hierarchy engine | BR-119–BR-125, BR-280 | F-010.1 | P0 | L |
| F-010.3 | Season goal prediction | BR-126–BR-135 | F-003.4 (submission at season start) | P0 | M |
| F-010.4 | Standings display | BR-215, BR-217 | F-010.1 | P0 | S |

**Note:** F-010.3's *submission* capability must exist at Season start — build it alongside F-003.4/F-003.5, not deferred to when the rest of Standings is built — even though its tie-break *usage* naturally groups with F-010.2. Collecting predictions late means they can never be collected validly (BR-127/BR-128 require them locked at season start).

## EPIC-011 — Corrections & Administration

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-011.1 | Administrative audit log viewer | BR-177–BR-182, BR-239–BR-242 | F-007.4, F-008.5 | P1 | M |
| F-011.2 | Injury/replacement eligibility determination UX | BR-068, BR-260 | F-006.4 | P0 | S |

F-011.2 is P0 because BR-255's "Replacement Selections" step in the complete-season flow requires the League Administrator to actually be able to declare eligibility, not just have the underlying rule exist. F-011.1 (a dedicated audit *viewer*) is P1 — the audit rows are written from day one as part of every P0 feature that touches BR-149/BR-295, but a polished viewing UI can follow shortly after MVP.

## EPIC-012 — Notifications

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-012.1 | Notification preferences management | BR-150, BR-151, BR-155, BR-226 | F-001.1 | P1 | M |
| F-012.2 | Gameweek reminder notifications | BR-152 | F-012.1, F-007.3 | P1 | M |
| F-012.3 | Weekly score/standings notifications | BR-153, BR-154 | F-012.1, F-008, F-010 | P1 | M |
| F-012.4 | Notification delivery infrastructure (async outbox, retry) | BR-224, BR-225 | F-012.1 | P1 | L |

F-012.4 is additionally blocked on the still-open architecture decision (provider selection — Architecture v1.2 §15) — it can be designed against the outbox interface now, but final delivery integration waits on that choice.

## EPIC-013 — Reporting & History

| ID | Feature | Traces to | Depends On | Priority | Size |
|---|---|---|---|---|---|
| F-013.1 | Historical season archive | BR-174, BR-176, BR-217, BR-257, BR-296 | A completed Season (end of F-010 flow) | P1 | M |
| F-013.2 | Retired-user historical display | BR-014, BR-175, BR-272 | F-013.1 | P2 | S |

Current-season standings/schedule display is already covered by F-009.4/F-010.4 (P0); EPIC-013 is specifically about persisting and browsing data *after* a season completes, so it naturally lands after the first season finishes.

---

# 5. Recommended Build Sequence

This is the actual dependency-respecting order — it interleaves epics where a later-numbered epic's feature must be built before an earlier-numbered epic's feature can proceed (most notably: Standings before Secondary Draft ordering).

**P0 — MVP (required for BR-255's complete-season flow):**

1. F-001.1 User registration & login
2. F-003.1 League creation
3. F-003.2 League invitation & acceptance
4. F-003.3 League membership management
5. F-003.4 Season creation & reuse
6. F-010.3 Season goal prediction *(submission capability — build now, alongside season creation; see note in §4)*
7. F-003.5 League/Season configuration management *(core mechanics; parameter-specific read paths land with their owning feature below)*
8. F-002.1 Default profile icon selection
9. F-002.2 League-specific icon override
10. F-002.3 FantasyTeam creation & uniqueness
11. F-002.4 Profile/league context switching UI
12. F-001.2 Password reset
13. F-001.3 Username management
14. F-001.4 User retirement
15. F-001.5 Private data protection
16. F-004.1 Player/club reference data sync
17. F-004.2 Fixture & gameweek sync
18. F-004.3 Player statistics sync
19. F-004.4 EPL transfer handling
20. F-004.5 Postponed/abandoned fixture handling
21. F-005.1 Draft setup & randomized order
22. F-005.2 Snake draft pick flow & ownership enforcement
23. F-005.3 Draft timer & admin extension
24. F-005.4 Draft pick timeout / makeup-pick handling
25. F-005.5 Draft UX
26. F-007.1 Weekly roster submission
27. F-007.2 Captain selection
28. F-007.3 Roster lock & deadline
29. F-007.4 Administrator roster correction
30. F-008.1 Official FPL score ingestion & application
31. F-008.2 Starting XI / bench determination
32. F-008.3 Captain scoring
33. F-008.4 Fantasy Goals For/Against calculation
34. F-008.5 Score corrections & administrator overrides
35. F-009.1 Schedule generation
36. F-009.2 Match result calculation
37. F-009.3 League points allocation
38. F-009.4 Schedule & match result display
39. F-010.1 Standings calculation
40. F-010.2 Tie-break hierarchy engine
41. F-010.4 Standings display
42. F-006.1 Secondary draft scheduling
43. F-006.2 Secondary draft order (standings snapshot) — *now unblocked, since Standings (39–41) is complete*
44. F-006.3 Secondary draft pick flow
45. F-006.4 Replacement eligibility & drafting
46. F-011.2 Injury/replacement eligibility determination UX

**P1 — Should Have (post-MVP, near-term):**

47. F-013.1 Historical season archive
48. F-003.6 League messaging
49. F-011.1 Administrative audit log viewer
50. F-012.1 Notification preferences management
51. F-012.4 Notification delivery infrastructure
52. F-012.2 Gameweek reminder notifications
53. F-012.3 Weekly score/standings notifications

**P2 — Could Have (later):**

54. F-013.2 Retired-user historical display

---

# 6. Explicitly Out of Scope (Reminder)

Per BRD §2.2 (Non-Goals) and the Architecture doc's remaining open decisions (§15), the following are **not** in this backlog and should not be pulled in without a separate scoping decision:

- Auction or linear (non-snake) drafts, public/global matchmaking, automatic bench substitution, vice-captain, arbitrary user-uploaded profile images, commercial/public-league features.
- Mobile application (ADR-004 — deferred).
- Specific notification provider integration (Architecture v1.2 §15 — infrastructure choice, not yet made).
- Formal legal review of the FPL data source's terms of use (Architecture v1.2 §15/BR-289 — accepted risk, not yet resolved).

---

# 7. Next Steps

Per Architecture v1.2 §16, the next artifact is **feature-level behavior specifications with acceptance criteria**, written in the build-sequence order from Section 5 — starting with F-001.1 through F-003.5 (the Phase 1 foundation features), since every later feature depends on them.
