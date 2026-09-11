# Fantasy EPL League Manager
## Feature Behavior Specifications — Phase 3 (Weekly Gameplay) — Version 1.0

**Document Status:** Baseline Feature Specifications
**Version:** 1.0
**Inputs:**
- Business Requirements Document v1.6 — authoritative business rules (BR-001…BR-301)
- Architecture and Domain Model v1.3 — aggregates, invariants, module map
- Epic and Feature Backlog v1.0 — feature list, dependencies, build sequence
- Feature Behavior Specifications (Phase 1) v1.1 and (Phase 2) v1.0 — hard dependencies

**Scope:** EPIC-007 (Gameweek Roster: F-007.1–F-007.4), EPIC-008 (Scoring Engine: F-008.1–F-008.5), EPIC-009 (H2H Competition: F-009.1–F-009.4), and EPIC-010's remaining features (Standings & Tie-Breaks: F-010.1, F-010.2, F-010.4 — F-010.3, Season Goal Prediction submission, was already specified in Phase 1). Backlog positions 26–41.

Per the user's direction, all open items raised across Phase 2 and this phase are consolidated at the end of this document (Section "Consolidated Open Items") rather than resolved inline, to be addressed together once Phase 3 is complete.

---

# EPIC-007 — Gameweek Roster

## F-007.1 — Weekly Roster Submission
**Backlog position:** 26 · **Depends on:** F-005.2 (squad must exist)

**User Story:** As a FantasyTeam owner, I want to submit my 15-player roster for the upcoming gameweek from my squad, satisfying positional minimums, so my team can compete.

Acceptance criteria:
1. Given my FantasyTeam's squad, when I submit a weekly roster, then it must contain exactly `SeasonConfiguration.WeeklyRosterSize` (default 15) players, all currently-owned `SquadPlayer`s of my FantasyTeam (BR-037, BR-196, BR-194, Invariant 6).
2. Given the submitted roster, when validated, then it must satisfy `SeasonConfiguration.PositionalMinimums` (default 1 GK / 3 DEF / 2 MID / 1 FWD); submission is rejected with specific feedback identifying which minimum is unmet if not satisfied (BR-041, BR-211, BR-279).
3. Given no positional maximum is enforced (BR-042), when a roster stacks many players of one position — provided the minimums for other positions are still met and the total is exactly 15 — then it is accepted.
4. Given this is my FantasyTeam's first roster submission of the Season and no `SeasonGoalPrediction` has been recorded, when I attempt to submit, then the submission is rejected and I am prompted to submit my prediction first (BR-299 — see Phase 1 F-010.3).
5. Given a roster in `Draft` status, when I submit it before the deadline, then `GameweekRoster.Status` transitions to `Submitted` and `SubmittedAt` is set (Architecture §6.5).
6. Given I have already submitted a roster for this gameweek, when I resubmit before the deadline, then the resubmission replaces the prior selection — this is normal pre-lock editing, not an administrative correction (that's F-007.4, post-lock only).

**Security considerations:** object-level check — only the FantasyTeam owner (or an Administrator via F-007.4, post-lock only) may submit or modify a roster (BR-163).

**Technical tasks:** `GameweekRoster.Submit()` domain method enforcing size/positional invariants; cross-context precondition check against `SeasonGoalPrediction` (Architecture §6.5/§6.7); resubmission (upsert) semantics pre-lock.

---

## F-007.2 — Captain Selection
**Backlog position:** 27 · **Depends on:** F-007.1

**User Story:** As a FantasyTeam owner, I want to designate one player from my submitted roster as Captain so they receive the captain scoring multiplier.

Acceptance criteria:
1. Given a roster (Draft or Submitted status), when I designate a Captain, then the selected player must be among that roster's 15 players — otherwise rejected (BR-046, BR-195, Invariant 5).
2. Given a roster, when submitted, then exactly one Captain must be designated — submission without a captain is rejected (BR-045, Invariant 4).
3. Given I change my Captain selection before the deadline, when I do so, then the new selection replaces the old one, same as any other pre-lock roster edit.
4. Given the roster screen, when I view it, then I can clearly select and see which player is currently my Captain (BR-212).
5. Given there is no Vice Captain concept in this application (BR-049), when the roster/captain UI and API are designed, then no secondary-captain field is exposed anywhere.

**Security considerations:** same object-level ownership check as F-007.1.

**Technical tasks:** `RosterPlayer.IsCaptain` flag plus a single-captain invariant enforced within `GameweekRoster.Submit()`/`SetCaptain()`; no vice-captain field anywhere in the data model.

---

## F-007.3 — Roster Lock & Deadline
**Backlog position:** 28 · **Depends on:** F-007.1, F-004.2 (kickoff times)

**User Story:** As a FantasyTeam owner, I want to see my gameweek's submission deadline prominently, and know my roster becomes locked and unchangeable once that passes.

Acceptance criteria:
1. Given a Gameweek's first EPL fixture kickoff time (from F-004.2), when the deadline is computed, then `RosterLockDeadline = first kickoff − SeasonConfiguration.GameweekRosterLockOffsetBeforeKickoff` (default 1 hour) (BR-093).
2. Given the deadline, when I view my roster screen, then it is prominently displayed, including a countdown (BR-213).
3. Given the deadline passes, when reached, then `GameweekRoster.Status` transitions to `Locked` automatically — a time-driven transition, not one requiring any user action (BR-094).
4. Given a `Locked` roster, when I attempt to modify my roster or Captain, then the attempt is rejected with a clear "roster is locked" message (BR-095, BR-096).
5. Given a `Locked` roster, when displayed, then the UI clearly identifies it as locked, visually distinct from an editable `Draft`/`Submitted` roster (BR-214).
6. Given a FantasyTeam never submits any roster before the deadline for a gameweek, when the deadline passes, then a `GameweekRoster` still transitions to `Locked` in an empty/unsubmitted state — **the BRD does not specify the scoring consequence of a never-submitted roster; flag as a consolidated open item.**

**Security considerations:** none beyond standard authorization already covered.

**Technical tasks:** `RosterLockDeadline` computation; a lock-transition detection mechanism — the same underlying scheduler question raised for draft-pick timeouts (Phase 2, Open Item #2); build once and reuse for both.

---

## F-007.4 — Administrator Roster Correction
**Backlog position:** 29 · **Depends on:** F-007.3

**User Story:** As a League Administrator, I want to make an exceptional correction to a FantasyTeam's locked roster — e.g., they missed the deadline by accident, or picked the wrong player — so fairness is preserved without bypassing the scoring engine's normal rules.

Acceptance criteria:
1. Given a `Locked` `GameweekRoster`, when the Administrator makes an authorized correction (changing roster players and/or captain), then the change is applied directly to the roster data — no special recalculation path exists; downstream scoring recalculates using the same rules as normal gameplay (BR-098, BR-148).
2. Given a correction is made, when persisted, then an `AdministrativeAction` (`ActionType = RosterCorrection`) is recorded in the same transaction, capturing before/after state, administrator, timestamp, and reason (BR-099, BR-146, BR-149).
3. Given a correction changes roster composition, when Fantasy Points, Fantasy Goals, Captain Points, Match Result, League Points, and Standings for that gameweek depend on it, then recalculation propagates consistently through all of them (BR-147).
4. Given the correction does not alter official FPL player statistics, when applied, then `PlayerPerformance` data is untouched — only the FantasyTeam's roster selection changes (BR-098).
5. Given a correction is made, when queried later (once the audit viewer, F-011.1, is built), then it is fully auditable.

**Security considerations:** Administrator-only (BR-162); routed through `IAdministrativeActionRecorder` (Architecture §6.8/§12.1).

**Technical tasks:** Administrator-only correction endpoint bypassing the lock guard; the recalculation-cascade trigger this depends on is shared with F-008.5 (score overrides) — build it once as a common mechanism.

---

# EPIC-008 — Scoring Engine

## F-008.1 — Official FPL Score Ingestion & Application
**Backlog position:** 30 · **Depends on:** F-004.3 (player statistics sync), F-007.3 (roster locked)

**User Story:** As the system, I want to apply official FPL fantasy points to each locked roster's players so gameweek scoring reflects the authoritative source.

Acceptance criteria:
1. Given a `Locked` `GameweekRoster` and official `PlayerPerformance` data available for that gameweek, when scoring runs, then each `RosterPlayer`'s points reflect the official FPL value, read via `IAuthoritativeValueResolver` (respecting any active `ScoreOverride` precedence, BR-140–BR-145) (BR-075, BR-076, BR-077).
2. Given a player recorded statistics across multiple clubs or fixtures within one gameweek, when scored, then the combined official total is used, never split or reduced (BR-074, BR-288).
3. Given official scoring has been finalized for a gameweek, when later read, then the finalized result is preserved — not silently recalculated — unless a new override or corrected official data explicitly triggers recalculation (BR-079).
4. Given a Gameweek whose lock deadline has not yet passed, when scoring is attempted, then it does not run — only `Locked` rosters are scored.

**Security considerations:** this is a system/background process, not user-invoked; any exposed manual "recalculate now" trigger should be Administrator-only.

**Technical tasks:** `GameweekScore` calculation service triggered on `PlayerPerformance` sync completion (F-004.3) for already-locked rosters; `IAuthoritativeValueResolver` integration.

---

## F-008.2 — Starting XI / Bench Determination
**Backlog position:** 31 · **Depends on:** F-008.1

**User Story:** As the system, I want to automatically determine each FantasyTeam's highest-scoring 11 from their submitted 15 so formation-free scoring works as intended.

Acceptance criteria:
1. Given all 15 `RosterPlayer` point values are known for a gameweek, when the Starting XI is computed, then the 11 highest-scoring players are marked `SelectionRole = StartingXI` and the remaining 4 are `Bench` (BR-043, BR-044).
2. Given ties in points among players at the 11th/12th-place boundary, when resolved, then a deterministic tie-break applies — **the BRD does not specify one; flag as a consolidated open item.**
3. Given a `Bench` player, when scoring completes, then their points never count toward the FantasyTeam's total, and they are never automatically substituted in for a non-playing Starting XI player (BR-038, BR-039, BR-040).
4. Given a player did not play (zero minutes/no official appearance), when included in the calculation, then their official score (typically 0) is used as-is.

**Security considerations:** none beyond standard.

**Technical tasks:** top-11 selection algorithm operating on computed per-player points; the tie-break rule for AC-2 depends on resolving the interaction with F-008.3 below (see that feature's note) before this can be fully implemented.

---

## F-008.3 — Captain Scoring
**Backlog position:** 32 · **Depends on:** F-008.2

**User Story:** As a FantasyTeam owner, I want my Captain's points multiplied per official FPL rules, with no compensation if they don't play.

**Significant design ambiguity surfaced here:** official FPL guarantees the Captain is in the user's starting lineup, because the user explicitly picks a formation. This application has no formation — the Starting XI is auto-selected as the 11 highest-scoring players from the 15 submitted (BR-044). The BRD never states whether the Captain's multiplier is applied **before** ranking (so a strong captain pick can help lift them into the counted 11) or only applies **after** the top-11 is already determined by raw, unmultiplied points (in which case a Captain who scores just outside the top 11 on raw points gets excluded from scoring entirely, multiplier and all — effectively the same outcome as a non-playing Captain, BR-048, but for a different reason). This is a genuine scoring-correctness question, not just a UX nicety, and materially affects both this feature and F-008.2. The acceptance criteria below assume the **apply-multiplier-before-ranking** interpretation as the more defensible default (it's the one that makes captaincy actually function as a strategic choice, consistent with the spirit of BR-047), but this is flagged as a consolidated open item requiring explicit confirmation before either F-008.2 or this feature is implemented.

Acceptance criteria:
1. Given the Captain is part of the roster, when the Starting XI ranking (F-008.2) is computed, then the Captain's per-player point value used for ranking already includes the captain multiplier — **assumed default; confirm before implementation** (BR-044, BR-047).
2. Given the Captain lands in the computed Starting XI under that ranking, when points are totaled, then the Captain's contribution is their official FPL points × the official FPL captain multiplier (BR-047, BR-078).
3. Given the Captain does not play (zero official appearance), when scored, then the Captain contributes zero points — there is no replacement Captain and no fallback to a second player (BR-048).
4. Given cumulative Captain points across gameweeks, when tracked, then they are retained per FantasyTeam and available to the Season-level tie-break hierarchy (BR-050, BR-051).

**Security considerations:** none.

**Technical tasks:** the scoring engine must apply the captain multiplier before (not after) top-11 ranking, per the assumed resolution above — **do not implement until confirmed.**

---

## F-008.4 — Fantasy Goals For/Against Calculation
**Backlog position:** 33 · **Depends on:** F-008.1

**User Story:** As the system, I want to calculate Fantasy Goals For and Against for each FantasyTeam's gameweek so goal-based tie-breaks and league display work correctly.

Acceptance criteria:
1. Given the *entire* submitted 15-player roster — not just the Starting XI — when Fantasy Goals For is calculated, then goals scored by **all 15** players count, regardless of `SelectionRole` (BR-080 explicitly scopes this to "the submitted 15-player gameweek roster," unlike Fantasy Points which only counts the Starting XI — this distinction is easy to miss and should be called out clearly in implementation).
2. Given goals scored by any position, including goalkeepers and defenders, when tallied, then all count toward Fantasy Goals For, including penalty goals (BR-081–BR-085).
3. Given the roster's goalkeeper, when Goals Against is calculated, then their official goals-conceded count is included (BR-086's first component).
4. Given the roster's selected defenders' clubs, when Goals Against is calculated, then the average goals conceded across those clubs (total conceded ÷ number of defenders) is computed and **truncated toward zero** to an integer — not rounded (BR-086's second component, BR-087, BR-088's worked example: 7 ÷ 5 = 1.4 → 1).
5. Given an own goal scored by a roster player, when calculated, then it increases Goals Against (via the applicable component) and is **not** counted as a goal scored by that player for Goals For purposes (BR-090, BR-091).
6. Given Goals For and Goals Against, when combined, then Fantasy Goal Difference = Goals For − Goals Against (BR-089).

**Security considerations:** none.

**Technical tasks:** Goals-For aggregation across all 15 roster players (explicitly *not* filtered to Starting XI); Goals-Against aggregation (goalkeeper conceded + truncated defender-club average); own-goal handling routed to Against, excluded from For.

---

## F-008.5 — Score Corrections & Administrator Overrides
**Backlog position:** 34 · **Depends on:** F-008.1

**User Story:** As a League Administrator, I want to manually override a player's statistics when official data is wrong or delayed, with the override taking precedence until I undo it.

Acceptance criteria:
1. Given official FPL data is later corrected upstream, when detected (via F-004.3's delta detection), then the application applies the correction automatically before any manual override is needed (BR-139).
2. Given the Administrator manually overrides a player's statistic for a gameweek, when applied, then a `ScoreOverride` is created recording the original value, override value, administrator, timestamp, and optional reason; this override takes precedence over official data for all downstream scoring (BR-140, BR-141, BR-144, BR-145).
3. Given an active override, when official data is subsequently corrected upstream, then the override still takes precedence until explicitly undone (BR-141, Invariant 12).
4. Given the Administrator undoes an override, when undone, then `UndoneAt` is set, `IsActive` becomes false, and official data (as currently known, including any correction applied since the override was created) becomes authoritative again (BR-142, BR-143).
5. Given any override is applied or undone, when downstream `GameweekScore`/`HeadToHeadMatch`/`LeagueStanding` depend on it, then recalculation propagates consistently through all of them — the same recalculation-cascade mechanism as F-007.4 (BR-148-style consistency).

**Security considerations:** Administrator-only (BR-162); routed through `IAdministrativeActionRecorder`.

**Technical tasks:** `ScoreOverride` aggregate; `IAuthoritativeValueResolver` precedence logic (Active Override > Official > Calculated); recalculation-cascade trigger shared with F-007.4.

---

# EPIC-009 — H2H Competition

## F-009.1 — Schedule Generation
**Backlog position:** 35 · **Depends on:** F-002.3 (FantasyTeams exist), F-003.4 (Season)

**User Story:** As the system, I want to generate a randomized, balanced head-to-head schedule at the start of a Season so every FantasyTeam has a fair set of matchups.

Acceptance criteria:
1. Given a Season with all FantasyTeams established, when the schedule is generated, then opponents are randomly assigned per gameweek (BR-107, BR-108).
2. Given the number of FantasyTeams and gameweeks, when the schedule is built, then it distributes matchups so every pair of FantasyTeams meets the same number of times as mathematically possible (BR-109).
3. Given the schedule is generated, when persisted, then it does not change thereafter except via an explicit administrative action — normal gameplay never regenerates it (BR-110).
4. Given an odd number of FantasyTeams, when scheduling a given gameweek, then one team necessarily has no opponent that week — **the BRD does not address how a "bye" week is scored or displayed; flag as a consolidated open item.**

**Security considerations:** schedule generation is a Season-setup-time system/administrator action (BR-162).

**Technical tasks:** a balanced round-robin-style scheduling algorithm; persistence of the generated schedule as immutable once created; bye-week handling pending resolution.

---

## F-009.2 — Match Result Calculation
**Backlog position:** 36 · **Depends on:** F-009.1, F-008 (scores)

**User Story:** As the system, I want to determine each gameweek matchup's winner based on the two FantasyTeams' weekly fantasy scores.

Acceptance criteria:
1. Given both FantasyTeams' `GameweekScore.FantasyPoints` for the gameweek, when compared, then the higher-scoring team wins (BR-112).
2. Given equal scores, when compared, then the match is a draw (BR-113).
3. Given the lower-scoring team, when the result is set, then they are recorded as the loss (BR-114).
4. Given a `HeadToHeadMatch` whose both FantasyTeams have `Locked` and `Scored` rosters, when this state is reached, then the match result calculates automatically — no manual trigger required for the normal case.

**Security considerations:** none beyond standard.

**Technical tasks:** `HeadToHeadMatch.CalculateResult()` reading both FantasyTeams' `GameweekScore`.

---

## F-009.3 — League Points Allocation
**Backlog position:** 37 · **Depends on:** F-009.2

**User Story:** As the system, I want to award league points per this Season's configured win/draw/loss values.

Acceptance criteria:
1. Given a match result of Win, when league points are allocated, then the winning FantasyTeam receives `SeasonConfiguration.LeaguePoints.Win` (default 3) (BR-115).
2. Given a Draw, when allocated, then both teams receive `SeasonConfiguration.LeaguePoints.Draw` (default 1) (BR-116).
3. Given a Loss, when allocated, then the losing team receives `SeasonConfiguration.LeaguePoints.Loss` (default 0) (BR-117).

**Security considerations:** none.

**Technical tasks:** read league point values from `SeasonConfiguration` (locked at Season start per BR-293) — never a hard-coded literal (ADR-011).

---

## F-009.4 — Schedule & Match Result Display
**Backlog position:** 38 · **Depends on:** F-009.2

**User Story:** As a FantasyTeam owner, I want to view my full season schedule and each completed matchup's result.

Acceptance criteria:
1. Given my FantasyTeam, when I view my schedule, then all gameweek matchups for the Season are displayed (BR-218).
2. Given an opponent in any matchup, when displayed, then they are shown via their FantasyTeam username and applicable League icon — never their account email (BR-219, BR-015).
3. Given a completed matchup, when displayed, then the relevant fantasy scores and result (win/draw/loss) are shown (BR-220).

**Security considerations:** League-membership-scoped read access (BR-161).

**Technical tasks:** schedule/result read-model query endpoints.

---

# EPIC-010 — Standings & Tie-Breaks (remaining features)

*F-010.3 (Season Goal Prediction submission) was already specified in Phase 1, pulled forward per the Backlog's note that its submission path must exist at Season start.*

## F-010.1 — Standings Calculation
**Backlog position:** 39 · **Depends on:** F-009.3

**User Story:** As the system, I want to compute League Standings ordered primarily by accumulated league points.

Acceptance criteria:
1. Given all completed `HeadToHeadMatch` results and league points for a Season up to a given gameweek, when standings are calculated, then FantasyTeams are ranked primarily by accumulated League Points (BR-118).
2. Given the standings, when displayed, then they include Position, Played/Won/Drawn/Lost, League Points, Fantasy Goals For/Against/Difference, and Captain Points (BR-215).
3. Given standings at any point in the Season, when queried, then the configured tie-break hierarchy (F-010.2) is always applied for ordering — never a partial or ad hoc ordering (BR-216).

**Security considerations:** League-membership-scoped read access.

**Technical tasks:** `LeagueStanding` aggregate/read-model, recalculated after each gameweek's results finalize (or on demand); `AsOfGameweekId` support for point-in-time queries (needed by F-006.2's standings snapshot, Phase 2).

---

## F-010.2 — Tie-Break Hierarchy Engine
**Backlog position:** 40 · **Depends on:** F-010.1

**User Story:** As the system, I want to resolve ties in the standings using the full configured tie-break hierarchy, in order, until a distinct ranking is achieved.

Acceptance criteria:
1. Given two or more FantasyTeams tied on League Points, when compared, then Fantasy Goal Difference breaks the tie, higher ranking higher (BR-120).
2. Given a further tie on Goal Difference, when compared, then Fantasy Goals For breaks the tie (BR-121).
3. Given a further tie, when compared, then Head-to-Head League Points *between the tied teams only* breaks the tie (BR-122, BR-280 — no away-goals or playoff provisions implemented).
4. Given a further tie, when compared, then cumulative Captain Points breaks the tie (BR-123).
5. Given a further tie, when compared, then the Season Goal Prediction's absolute-difference-from-actual breaks the tie (closest wins; an equal-distance tie favors the prediction ≤ actual) (BR-124, BR-131–BR-134).
6. Given all deterministic criteria remain tied, when this occurs, then a randomized tie-break serves as the final fallback (BR-125).
7. Given the tie-break pipeline executes, when each tier runs, then it compares only the subset of FantasyTeams still tied after the prior tier — never the whole league — so Head-to-Head Points (AC-3) is correctly scoped to "between the tied teams only" (BR-280).

**Security considerations:** none.

**Technical tasks:** `IStandingsTieBreakRule` pipeline (ADR-008) implementing this 7-tier chain exactly, with each stage narrowing the tied set before invoking the next.

---

## F-010.4 — Standings Display
**Backlog position:** 41 · **Depends on:** F-010.1

**User Story:** As a FantasyTeam owner, I want to view current league standings, correctly ordered.

Acceptance criteria:
1. Given current standings, when I view the League Table, then all fields from BR-215 are shown, ordered per the tie-break hierarchy (F-010.2).
2. Given a completed Season, when I view its final standings, then they remain accessible — this feature covers *display* only; full historical archival persistence is EPIC-013/F-013.1, a later phase (BR-217).

**Security considerations:** League-membership-scoped read access.

**Technical tasks:** standings display endpoint/UI.

---

# Consolidated Open Items (Phase 2 + Phase 3)

Per your direction, these are gathered here to resolve as a batch now that Phase 3 is complete.

**Carried forward from Phase 2:**
1. **F-005.1 (Draft Setup):** whether a minimum of two FantasyTeams is required to start an Initial Draft.
2. **F-005.4 (Draft Timeout):** whether expired-deadline detection is a scheduled background sweep or a lazy check on next relevant access.
3. **F-006.4 (Replacement Drafting):** whether a replacement-selection opportunity ever expires if unused.
4. **F-011.2 (Eligibility Determination):** whether an EPL-exit-triggered replacement eligibility grant requires an Administrator confirmation step, or is meant to be fully automatic (tension between BR-065 and BR-066).

**Raised in Phase 3:**
5. **F-007.3 (Roster Lock):** what happens, scoring-wise, to a FantasyTeam that never submits any roster for a gameweek before the deadline.
6. **F-008.2 (Starting XI):** the tie-break rule for players tied in points at the 11th/12th-place boundary.
7. **F-008.2/F-008.3 (Captain Scoring, significant):** whether the captain multiplier is applied *before* Starting XI ranking (so captaincy can lift a player into the counted 11) or *after* (so a Captain outside the raw top 11 scores nothing, multiplier included). This spec assumed "before" as the working default — it's the one that makes captaincy function as an actual strategic choice — but it's a real scoring-correctness decision, not a formality, and should be confirmed explicitly.
8. **F-009.1 (Schedule Generation):** how a "bye" week is scored/displayed when the League has an odd number of FantasyTeams.

Item 7 is the highest-stakes of these — it changes what the scoring engine actually computes, not just an edge case's presentation.
