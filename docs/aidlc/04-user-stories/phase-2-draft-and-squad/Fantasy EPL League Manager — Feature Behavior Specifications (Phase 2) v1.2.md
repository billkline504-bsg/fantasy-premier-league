# Fantasy EPL League Manager
## Feature Behavior Specifications — Phase 2 (Draft & Squad) — Version 1.2

**Document Status:** Baseline Feature Specifications
**Version:** 1.2
**Inputs:**
- Business Requirements Document v1.9 — authoritative business rules (BR-001…BR-313)
- Architecture and Domain Model v1.7 — aggregates, invariants, module map
- Epic and Feature Backlog v1.1 — feature list, dependencies, build sequence
- Feature Behavior Specifications (Phase 1) v1.1 — Foundation phase, a hard dependency for everything below

**Scope:** EPIC-005 (Initial Draft: F-005.1–F-005.5) and EPIC-006 (Secondary & Replacement Draft: F-006.1–F-006.4), plus F-011.2 (Injury/Replacement Eligibility Determination UX), which the Backlog places immediately after F-006.4 in the build sequence because it's the Administrator-facing action that feeds directly into replacement drafting.

**Important build-order caveat (carried from Backlog §4/§5):** F-006.2, F-006.3, and F-006.4 cannot actually be *built* until Phase 3's Standings feature (F-010.1/F-010.2) exists, because Secondary Draft order is seeded from a standings snapshot (BR-056, BR-136). **Writing behavior specifications now is fine and intentional** — it doesn't require the dependency to exist yet — but implementers should not schedule EPIC-006 work before Phase 3 is done. F-005.1–F-005.5 (Initial Draft) have no such constraint and can be built immediately after Phase 1.

---

# EPIC-005 — Initial Draft

## F-005.1 — Draft Setup & Randomized Order
**Backlog position:** 21 · **Depends on:** F-003.5 (configuration), F-004.1 (player reference data)

**User Story:** As a League Administrator, I want to start the Initial Draft for my League's Season so that FantasyTeams can begin selecting players.

Acceptance criteria:
1. Given a Season in `Status = Setup` with all expected `FantasyTeam` records created, when the Administrator starts the Initial Draft, then a `Draft` aggregate is created with `DraftType = Initial`, `Status` transitions to `InProgress`, and `DraftOrder` is randomized across all participating FantasyTeams (BR-054).
2. Given the Draft's squad-size target, when determined, then it reads `SeasonConfiguration.InitialSquadSize` (default 25), which locks at this point per BR-293 — later changes to the League's default do not retroactively affect this Season's Draft.
3. Given the randomized order, when the Draft runs, then the number of rounds equals the configured squad size (one pick per FantasyTeam per round), and pick sequencing is snake-ordered: Round 1 follows `DraftOrder`, Round 2 reverses it, and so on (BR-053, BR-055).
4. Given a League with fewer than two FantasyTeams for the Season, when Draft start is attempted, then it is rejected — an Initial Draft requires at least two competing teams (BR-302, BRD v1.7).

**Security considerations:** starting a Draft is Administrator-only (BR-162).

**Technical tasks:** `Draft` aggregate creation service; `DraftOrder` randomization (a simple shuffle — no cryptographic requirement); round/pick-sequence computation derived from configured squad size.

---

## F-005.2 — Snake Draft Pick Flow & Ownership Enforcement
**Backlog position:** 22 · **Depends on:** F-005.1

**User Story:** As a FantasyTeam owner, I want to select an available EPL player on my turn so that my squad grows toward the required size, with no risk of ending up with a player someone else also claimed.

Acceptance criteria:
1. Given it is my FantasyTeam's turn, when I submit a pick for a player currently unowned within this League+Season, then a `DraftSelection` is recorded and a corresponding `SquadPlayer` (`AcquisitionType = InitialDraft`) is created atomically in the same transaction (BR-052, BR-264).
2. Given a player already owned by another FantasyTeam in this League+Season, when I attempt to pick them, then the pick is rejected (BR-059, BR-191, BR-192).
3. Given two FantasyTeams' pick requests for the same player arrive concurrently, when both are processed, then only one succeeds — enforced by the database's partial unique index on `(PlayerId, SeasonId) WHERE IsCurrentlyOwned`, not solely an application-level check, so a race condition can never produce duplicate ownership (AP-009, AP-010).
4. Given it is *not* my FantasyTeam's turn, when I attempt to submit a pick, then it is rejected regardless of the target player's availability.
5. Given a successful pick, when the Draft advances, then `CurrentRound`/`CurrentPickIndex` move to the next FantasyTeam in snake order and a `PlayerDrafted` domain event fires.
6. Given the final regularly scheduled pick completes, when there are no entries in `PendingMakeupPicks` (F-005.4), then `Draft.Status` transitions to `Completed`; if makeup picks are pending, completion is deferred until they resolve.

**Security considerations:** object-level check that only the `User` behind the FantasyTeam currently on the clock can submit that specific pick (BR-163).

**Technical tasks:** pick endpoint with turn validation; transactional insert guarded by the partial unique index; translate a constraint violation into a domain-level `PlayerAlreadyOwnedException` rather than surfacing a raw database error (Architecture v1.3 §6.4).

---

## F-005.3 — Draft Timer & Administrator Extension
**Backlog position:** 23 · **Depends on:** F-005.2

**User Story:** As a FantasyTeam owner, I want a visible, server-authoritative countdown for my pick; as a League Administrator, I want to extend it when needed.

Acceptance criteria:
1. Given a FantasyTeam's turn begins, when the pick window opens, then `CurrentPickDeadline = now + SeasonConfiguration.DraftTimerSecondsByType[Initial]` (default 300 seconds) (BR-057).
2. Given the Administrator extends the timer before it expires, when the extension is applied, then `CurrentPickDeadline` is pushed back by the requested amount, and a `DraftTimerExtended` administrative action is recorded (BR-058, BR-295-style audit pattern).
3. Given the timer as displayed to users, when rendered, then it reflects the server-authoritative `CurrentPickDeadline` — the client never runs its own independent countdown as the source of truth, to avoid clock-skew disputes (BR-206).
4. Given the timer expires with no extension, when this occurs, then control passes to F-005.4 — this feature does not itself define timeout behavior.

**Security considerations:** timer extension is Administrator-only (BR-162).

**Technical tasks:** `CurrentPickDeadline` field and extension endpoint; client polls/subscribes to server-authoritative deadline rather than computing its own.

---

## F-005.4 — Draft Pick Timeout / Makeup-Pick Handling
**Backlog position:** 24 · **Depends on:** F-005.3

**User Story:** As the system, when a FantasyTeam misses its pick window without an extension, I want to skip that pick and queue a makeup pick so the Draft keeps moving and every FantasyTeam still ends up with the required squad size.

Acceptance criteria:
1. Given `CurrentPickDeadline` has passed with no administrator extension, when this is detected, then the current pick is skipped — no `DraftSelection` is recorded for that turn — and the FantasyTeamId is appended to `Draft.PendingMakeupPicks` (BR-282).
2. Given the Draft reaches what would otherwise be its final regular-round pick, when `PendingMakeupPicks` is non-empty, then makeup picks are inserted, in the order they were skipped, immediately after the final regular round, each subject to the same timer/ownership rules as a normal pick (F-005.2/F-005.3).
3. Given all regular and makeup picks are complete, when the last makeup pick resolves, then `Draft.Status` transitions to `Completed`, and every FantasyTeam has exactly `SeasonConfiguration.InitialSquadSize` players (BR-052, BR-197).
4. Given a makeup pick is itself skipped (timed out again), when this occurs, then it is re-queued at the end of the (now-extended) makeup sequence, applying the same rule recursively.

**Security considerations:** none beyond standard pick authorization (F-005.2).

**Technical tasks:** `PendingMakeupPicks` queue on the `Draft` aggregate; expired-deadline detection is handled by the shared scheduled sweep introduced in Architecture v1.4 ADR-012 (also used for Gameweek roster locking, F-007.3), not a bespoke mechanism for this feature alone.

---

## F-005.5 — Draft UX
**Backlog position:** 25 · **Depends on:** F-005.2

**User Story:** As a participant, I want to see the draft order, whose turn it is, the history of completed picks, and which players are already owned, so I can plan my own picks.

Acceptance criteria:
1. Given an in-progress Draft, when I view the draft screen, then the full `DraftOrder` — including each round's snake reversal — is displayed (BR-204).
2. Given the current pick, when displayed, then the FantasyTeam on the clock is clearly and unambiguously highlighted (BR-205).
3. Given completed picks, when I view draft history, then all prior `DraftSelection` rows are listed in the order they occurred, with makeup picks (`IsMakeupPick = true`) visually distinguished (BR-207).
4. Given the player pool, when displayed, then already-owned players are clearly marked unavailable, distinct from currently-selectable players (BR-208).
5. Given the player pool, when I filter by position, then only players matching the selected position (Goalkeeper, Defender, Midfielder, Forward) are shown, or all positions if "All" is selected (BR-310).
6. Given the player pool, when I enter a search term, then the pool narrows to players whose name matches the term as a case-insensitive substring (BR-311).
7. Given the player pool, when I select a column to sort by (name, club, position, Minutes Played, Games Played, or Official FPL Points), then the pool re-orders by that column, toggling between ascending and descending on repeated selection (BR-312).
8. Given the player pool, when displayed, then each player's season-to-date Minutes Played, Games Played, and Official FPL Points are shown, sourced from `PlayerSeasonStatistics` (Architecture v1.7 §6.6); before the Season's first Gameweek has been scored — i.e., throughout the Initial Draft — these values display as zero rather than being hidden (BR-313).

Acceptance criteria 5–8 apply identically to the Initial Draft (this feature) and to the Secondary/Replacement Draft (F-006.2–F-006.4), since both present the player pool through this same shared Draft UX. They are most valuable during the Secondary Draft, when meaningful season-to-date statistics already exist for most players.

**Security considerations:** draft screen data is scoped to League membership — only members of the specific League (or its Administrator) may view it (BR-161).

**Technical tasks:** read-model/query endpoints for draft order, current turn, pick history, and player-ownership status, extended with `position`, `search`, and `sort` query parameters (Architecture v1.7 §6.4) and season-to-date statistics sourced from `PlayerSeasonStatistics` (Architecture v1.7 §6.6).

---

# EPIC-006 — Secondary & Replacement Draft

## F-006.1 — Secondary Draft Scheduling
**Backlog position:** 42 · **Depends on:** F-004.2 (fixture calendar), F-003.5 (configuration)

**User Story:** As the system, I want to automatically propose the Secondary Draft's date relative to the EPL transfer window closing, so the League Administrator doesn't have to track the fixture calendar manually.

Acceptance criteria:
1. Given the official EPL transfer window's confirmed close date, when the scheduling service runs, then it proposes a Secondary Draft start date equal to the close date plus `SeasonConfiguration.SecondaryDraftSchedulingOffsetDays` (default 1 day) (BR-069, BR-281).
2. Given that proposed date has one or more scheduled EPL fixtures (per F-004.2's fixture calendar), when the scheduling service evaluates it, then it advances one day at a time until a fixture-free day is found (BR-281).
3. Given a proposed date, when the Administrator reviews it, then they may override it with a different date before the Draft is created (BR-069).
4. Given the transfer window's close date is not yet confirmed via official data, when the scheduling service would otherwise run, then it does not propose a premature date.

**Security considerations:** overriding the proposed date is Administrator-only (BR-162).

**Technical tasks:** scheduling application service combining the `PlayerData` context's fixture calendar with `SeasonConfiguration`; Administrator override endpoint.

---

## F-006.2 — Secondary Draft Order (Standings Snapshot)
**Backlog position:** 43 · **Depends on:** F-010.1/F-010.2 (Standings — **Phase 3, not yet built**), F-005.2 (shared draft engine)

**User Story:** As the system, I want to determine Secondary Draft order from current League Standings — worst-placed team picks first — so that lower-performing teams get a compensating advantage.

Acceptance criteria:
1. Given the Secondary Draft is about to start, when the draft order is computed, then it is captured as a one-time snapshot of current League Standings: last place picks first, first place picks last in round one, and snake ordering continues through subsequent rounds (BR-056, BR-136).
2. Given two or more FantasyTeams are tied in standings at the moment of the snapshot, when order is resolved, then the complete League tie-break hierarchy (BR-119–BR-125, BR-280) is applied to break the tie for draft-order purposes only (BR-137).
3. Given the draft order snapshot has been captured, when subsequent gameweek results change the actual standings, then the already-captured Secondary Draft order does not change retroactively (BR-138).

**Security considerations:** none beyond standard League-scoped read access.

**Technical tasks:** standings-snapshot query at draft-start time; reuse the `IStandingsTieBreakRule` pipeline (Architecture ADR-008) for tie resolution rather than a separate implementation.

---

## F-006.3 — Secondary Draft Pick Flow
**Backlog position:** 44 · **Depends on:** F-005.2 (shared engine), F-006.2

**User Story:** As a FantasyTeam owner, I want to make my configured number of Secondary Draft selections from all currently unowned EPL players so I can add depth to my squad for the rest of the season.

Acceptance criteria:
1. Given the Secondary Draft has started with its snapshot order, when each FantasyTeam picks, then the same snake-draft pick/ownership/timer/timeout mechanics as the Initial Draft (F-005.2/F-005.3/F-005.4) apply, reused rather than reimplemented for a second `DraftType`.
2. Given the number of rounds, when determined, then it equals `SeasonConfiguration.SecondaryDraftSelectionsPerTeam` (default 5) (BR-060).
3. Given any EPL player not currently owned by any FantasyTeam in this League+Season, when the pool is evaluated, then that player is eligible for selection regardless of position (BR-061).
4. Given a Secondary Draft selection is made, when the corresponding `SquadPlayer` is created, then `AcquisitionType = SecondaryDraft` and the player is *added* to the existing squad rather than replacing anyone — a FantasyTeam's total squad size grows past its Initial Draft size (BR-062, BR-198).

**Security considerations:** same as F-005.2.

**Technical tasks:** parameterize the shared draft engine by `DraftType` so the Secondary Draft reuses F-005.2/F-005.3/F-005.4's mechanics with its own configured round count and its order sourced from F-006.2 instead of a random shuffle.

---

## F-006.4 — Replacement Eligibility & Drafting
**Backlog position:** 45 · **Depends on:** F-006.3

**User Story:** As a FantasyTeam owner whose player has left the EPL or been declared season-ending injured, I want an additional replacement selection so I can maintain a competitive squad.

Acceptance criteria:
1. Given a League Administrator grants replacement eligibility for a fantasy-owned player (via F-011.2, below), when the grant is recorded, then that player's `SquadPlayer.ReplacementEligibleAt` is set and exactly one replacement-selection opportunity is generated for the owning FantasyTeam (BR-065, BR-066, BR-067).
2. Given a replacement-selection opportunity exists, when the FantasyTeam owner uses it, then they may select any currently-unowned EPL player, added via `AcquisitionType = Replacement` — using the opportunity does not require first releasing the now-eligible player, nor does any release happen automatically (BR-063, BR-064).
3. Given a FantasyTeam owner does not immediately use an available opportunity, when time passes, then the opportunity remains available indefinitely until used — there is no use-it-or-lose-it deadline (BR-307, BRD v1.7).
4. Given `SeasonConfiguration.ReplacementSelectionCap` is configured to a fixed number rather than the uncapped default, when a FantasyTeam has already reached that cap, then further eligibility events do not generate additional opportunities for it (BR-287, BR-291).
5. Given a replacement selection, when the squad's acquisition history is queried, then it is retained and clearly distinguishable from `InitialDraft`/`SecondaryDraft` acquisitions (BR-263, BR-264).
6. Given a replacement-eligible player has not actually been released by their original FantasyTeam, when a *different* FantasyTeam attempts to select that same player as a replacement, then the attempt is rejected — "replacement-eligible" does not mean "unowned"; only genuinely unowned players are selectable as replacements (BR-261, BR-262).

**Security considerations:** eligibility-granting is Administrator-only (BR-162); the replacement pick itself is owner-only, object-level checked (BR-163).

**Technical tasks:** per-FantasyTeam opportunity queue; replacement-pick endpoint reusing the ownership-enforcement mechanics from F-005.2 (a single ad hoc selection, not a full turn-based draft flow).

---

## F-011.2 — Injury/Replacement Eligibility Determination UX
**Backlog position:** 46 · **Depends on:** F-006.4

*(Pulled in from EPIC-011 per the Backlog's note that it's the Administrator-facing action directly feeding F-006.4.)*

**User Story:** As a League Administrator, I want a clear way to review and record season-ending-injury and EPL-exit eligibility determinations — informed by league-user consensus for injuries — so that replacement opportunities are granted correctly and auditably.

Acceptance criteria:
1. Given a fantasy-owned player is detected as having transferred out of the EPL (via F-004.4), when this is confirmed by official data, then replacement eligibility is granted automatically — no Administrator confirmation step exists for this path (BR-066, BR-308, BRD v1.7). The eligibility queue may still *display* these candidates for visibility, but does not gate them on an approval action.
2. Given league discussion (outside the application) reaches consensus that a fantasy-owned player has a season-ending injury, when the Administrator records that determination, then the player becomes replacement-eligible and the determination is captured as an `AdministrativeAction`, including who made it, when, and any recorded rationale (BR-067, BR-068, BR-149). This path is unchanged — it remains Administrator-judgment-based, distinct from the automatic EPL-exit path in AC-1.
3. Given an eligibility determination has been made, when it is viewed later (by the Administrator or in an audit review), then it is fully auditable (BR-149, BR-295-style pattern) — including automatically granted EPL-exit eligibility, which is still recorded as an auditable event even without an approval action.

**Security considerations:** Administrator-only (BR-162); every determination is audited (BR-149).

**Technical tasks:** eligibility-queue UI/endpoint combining auto-flagged EPL-exit candidates and manual injury-declaration entry; routes every grant through `IAdministrativeActionRecorder` (Architecture §6.8/§12.1).

---

# Open Items Raised While Writing This Phase

All four items raised in the v1.0 pass were resolved in BRD v1.7 (§64) and Architecture v1.4:

1. **F-005.1 (Draft Setup):** minimum of two FantasyTeams required (BR-302). Resolved.
2. **F-005.4 (Draft Timeout):** shared scheduled background sweep, also used by F-007.3 (ADR-012). Resolved.
3. **F-006.4 (Replacement Drafting):** no expiration on unused opportunities (BR-307). Resolved.
4. **F-011.2 (Eligibility Determination):** EPL-exit eligibility is fully automatic; season-ending-injury eligibility remains Administrator-determined (BR-308). Resolved.

No open items remain from this phase.

**Added in v1.2 (not an open item raised while originally writing this phase):** reviewing an illustrative HTML mock-up of F-005.5's Draft Board screen surfaced four player-pool capabilities the mock-up implied but the spec had not yet stated explicitly — position filtering, name search, column sorting, and season-to-date statistics display. These are now formalized as BR-310–BR-313 (BRD v1.9) and folded into F-005.5's acceptance criteria above, with supporting domain-model changes in Architecture v1.7 §6.4/§6.6.
