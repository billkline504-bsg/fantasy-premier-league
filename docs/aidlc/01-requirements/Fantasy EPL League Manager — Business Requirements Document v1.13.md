# Fantasy EPL League Manager
## Business Requirements Document — Version 1.13

**Document Status:** Baseline Requirements  
**Version:** 1.13  
**Purpose:** AIDLC input for generation of architecture, domain models, use cases, user stories, API specifications, database design, automated tests, UI/mobile requirements, and implementation tasks.

---

# 1. Executive Summary

The Fantasy EPL League Manager is a private fantasy football application designed around the English Premier League (EPL).

The application allows users to create and participate in private fantasy leagues in which:

1. Users draft EPL players into a fantasy squad.
2. Each user maintains a squad of at least 25 players following the initial draft.
3. Each gameweek, each user submits a 15-player roster from their squad.
4. The submitted 15-player roster is scored using official Fantasy Premier League (FPL) fantasy points.
5. The highest-scoring 11 players from the submitted 15-player roster constitute the user's scoring lineup.
6. One player may be designated captain and receives the applicable captain multiplier.
7. Users compete in head-to-head matches.
8. Fantasy Goals For and Fantasy Goals Against are calculated separately from FPL fantasy points.
9. League standings are determined using league points and an EPL-inspired tie-break hierarchy.
10. A secondary draft occurs following the official EPL transfer window.
11. Players who leave the EPL or are determined by the league administrator to have a season-ending injury may subsequently be replaced.
12. Historical results are retained for at least ten years.
13. Users may participate in multiple leagues and may have different league-specific icons in each league.

The system is initially intended for personal/private use but shall be designed so that it can evolve into a broader application without requiring fundamental architectural redesign.

---

# 2. Product Goals

## 2.1 Primary Goals

The application shall:

- Provide a secure fantasy EPL league-management platform.
- Closely mirror relevant EPL/FPL concepts.
- Automate player scoring and league standings.
- Minimize manual league administration.
- Provide a reliable historical record of league activity.
- Support multiple leagues per user.
- Support multiple seasons.
- Provide responsive web access and a native mobile experience.
- Be designed using modern security, architectural, and software-engineering practices.
- Provide sufficient domain separation to allow future expansion.

## 2.2 Non-Goals for Initial Release

The initial release will not attempt to:

- Replace the official FPL application.
- Provide public/global matchmaking.
- Support auction drafts.
- Support linear drafts.
- Allow fantasy-player ownership across multiple users within the same league.
- Automatically substitute bench players.
- Provide vice-captain functionality.
- Allow arbitrary user-uploaded profile images.
- Become a commercial product unless that is explicitly decided later.

---

# 3. Terminology

| Term | Definition |
|---|---|
| EPL | English Premier League |
| FPL | Official Fantasy Premier League |
| Season | One EPL season |
| League | A private fantasy competition |
| User | Global application account |
| League Membership | A user's participation in a specific league |
| Fantasy Team | A user's team within a specific league and season |
| Squad | All players owned by a Fantasy Team |
| Gameweek | An official FPL/EPL gameweek |
| Weekly Roster | The 15 players submitted by a Fantasy Team for a gameweek |
| Starting XI | The highest-scoring 11 players from the submitted 15 |
| Bench | The five submitted players who are not among the highest-scoring 11 |
| Captain | A player selected to receive the captain multiplier |
| Secondary Draft | Draft conducted after the EPL transfer window |
| Replacement Draft | Additional selection process for players who leave the EPL or are declared season-ending injured |
| Fantasy Goals For | Goals attributed to the user's selected players |
| Fantasy Goals Against | Goals calculated using goalkeeper goals conceded and defender team performance |
| League Points | Head-to-head competition points |
| Goal Difference | Fantasy Goals For minus Fantasy Goals Against |

---

# 4. User and Identity Model

## BR-001 — User Account

The application shall maintain a unique User account for each registered individual.

## BR-002 — Immutable User Identifier

Every User shall have an immutable internal identifier.

All domain relationships shall use the immutable identifier rather than the username.

## BR-003 — Username

Every active User shall have a unique username.

The username shall be used as the normal user-facing identity in:

- League standings
- Head-to-head schedules
- Match results
- Draft screens
- League messages
- Historical results
- Other league-facing displays

## BR-004 — Username Uniqueness

Usernames shall be globally unique among active users.

Uniqueness shall be enforced at the persistence layer.

## BR-005 — User Profile

Each User shall have an associated UserProfile.

The UserProfile shall contain global profile preferences rather than league-specific fantasy information.

## BR-006 — Default Profile Icon

Each User shall have a default profile icon selected from the application's predefined icon collection.

## BR-007 — League-Specific Icon

A User shall be able to select a different icon for each League in which they participate.

## BR-008 — League Icon Override

A League-specific icon shall override the User's default icon for that League.

If no League-specific icon exists, the default icon shall be displayed.

## BR-009 — Independent League Icons

Changing an icon in one League shall not change the icon displayed in another League.

## BR-010 — Icon Removal

A User shall be able to remove a League-specific icon and revert to the global default icon.

## BR-011 — Icon Validation

Icons shall be selected from an application-controlled set of valid ProfileIcon records.

Clients shall not be permitted to specify arbitrary image paths or resources.

## BR-012 — Historical User Identity

Historical records shall reference immutable User identifiers.

Changes to username or profile icon shall not break historical relationships.

## BR-013 — User Retirement

User deletion shall be implemented as a soft delete.

The User shall be marked as retired rather than physically deleted.

## BR-014 — Historical Retired Users

Historical standings and reports shall continue to identify retired users.

## BR-015 — Private User Information

Email address, phone number, authentication information, and other private account data shall not be exposed through league-facing APIs.

---

# 5. Domain Architecture

The fundamental domain relationship shall be:

```text
User
 |
 +-- UserProfile
 |
 +-- LeagueMembership
       |
       +-- League
       |
       +-- FantasyTeam
             |
             +-- Season
             |
             +-- Squad
             |
             +-- GameweekRoster
             |
             +-- CaptainSelection
             |
             +-- DraftHistory
             |
             +-- Scores
             |
             +-- HeadToHeadResults
```

## BR-016 — Global User Identity

User represents the global application identity.

## BR-017 — League Membership

LeagueMembership shall represent a User's participation in a particular League.

## BR-018 — Fantasy Team

FantasyTeam shall represent the user's fantasy competition identity within a specific League and Season.

## BR-019 — Fantasy Team Ownership

A FantasyTeam shall belong to exactly one LeagueMembership and exactly one Season.

## BR-020 — Multiple League Participation

A User may have multiple LeagueMembership records for the same Season.

## BR-021 — League Isolation

Each FantasyTeam shall maintain independent:

- Squad
- Weekly roster
- Captain selections
- Draft history
- Scores
- Head-to-head results
- Standings
- League-specific configuration

## BR-022 — No Cross-League Roster Sharing

A User's squad in one League shall not automatically affect their squad in another League.

---

# 6. Core Domain Entities

## 6.1 User

```text
User
----
UserId
Username
Status
CreatedAt
UpdatedAt
RetiredAt
```

## 6.2 UserProfile

```text
UserProfile
-----------
UserId
DefaultIconId
CreatedAt
UpdatedAt
```

## 6.3 ProfileIcon

```text
ProfileIcon
-----------
ProfileIconId
Name
AssetIdentifier
IsActive
SortOrder
```

## 6.4 League

```text
League
------
LeagueId
Name
Description
LeagueAdministratorMembershipId
CreatedAt
Status
```

## 6.5 LeagueMembership

```text
LeagueMembership
----------------
LeagueMembershipId
LeagueId
UserId
LeagueIconId
Status
JoinedAt
LeftAt
```

## 6.6 Season

```text
Season
------
SeasonId
Name
EplSeasonIdentifier
StartDate
EndDate
Status
```

## 6.7 FantasyTeam

```text
FantasyTeam
-----------
FantasyTeamId
LeagueMembershipId
SeasonId
CreatedAt
Status
```

A unique constraint shall exist on:

```text
LeagueMembershipId + SeasonId
```

A user therefore has at most one FantasyTeam in a given League for a given Season.

---

# 7. League Management

## BR-023 — League Creation

Anyone with access to the main website may create a League during the initial release.

## BR-024 — League Administrator

The League creator shall initially become the League Administrator.

## BR-025 — League Administrator Permissions

The League Administrator shall be able to:

- Manage league members
- Manage league messages
- Make authorized post-deadline roster corrections
- Manage administrative scoring overrides
- Determine replacement eligibility
- Determine season-ending injury eligibility
- Manage draft timing
- Extend a draft timer
- Perform other league-management functions as the application evolves

## BR-026 — Private League

Leagues shall initially operate as private leagues.

## BR-027 — League Invitation

Users shall join a private League through an invitation link.

## BR-028 — Invitation Channels

The invitation link may be distributed through:

- Email
- Text message
- Other normal messaging mechanisms

## BR-029 — Invitation Expiration

League invitation links shall expire after one week.

## BR-030 — Multiple Leagues

A User may participate in multiple Leagues during the same Season.

## BR-031 — Active League Context

When a User has multiple active Leagues, the application shall require the User to select the League whose FantasyTeam they are managing.

## BR-032 — Multiple Seasons

A League may be reused for a subsequent EPL Season.

## BR-033 — Season Reinvitation

League invitations shall be resent for each new Season because League membership may change.

---

# 8. Squad Structure

## BR-034 — Initial Squad Size

Each FantasyTeam shall select 25 players during the initial draft.

## BR-035 — Squad Ownership

Within a League, an EPL player may be owned by only one FantasyTeam.

## BR-036 — Full Squad

The 25-player squad represents the user's complete fantasy squad.

## BR-037 — Weekly Roster

Each gameweek, the User shall submit exactly 15 players from their squad.

## BR-038 — Bench

The remaining players in the 15-player submitted roster shall be considered the bench.

## BR-039 — No Automatic Substitution

Bench players shall never automatically replace players who do not play.

## BR-040 — No Post-Deadline Automatic Changes

Once gameplay begins, the submitted roster is considered fixed unless changed by an authorized League Administrator.

---

# 9. Positional Requirements

## BR-041 — Weekly Roster Minimums

Positional minimum requirements shall apply to the 15-player weekly roster.

## BR-042 — No Positional Maximums

There shall be no maximum number of players for a position beyond the requirement that the roster contain exactly 15 players.

## BR-043 — Starting XI

The application shall not require the User to specify a formation.

## BR-044 — Highest-Scoring XI

The Starting XI shall be determined by selecting the highest-scoring 11 players from the submitted 15-player roster.

---

# 10. Captain

## BR-045 — Captain Selection

Each User shall be permitted to designate one player as Captain for each gameweek.

## BR-046 — Captain Eligibility

The Captain must be included in the submitted 15-player roster.

## BR-047 — Captain Multiplier

The Captain shall receive the official FPL captain scoring multiplier.

## BR-048 — Non-Playing Captain

If the selected Captain does not play, the Captain receives zero Captain points.

There shall be no replacement Captain.

## BR-049 — No Vice Captain

Users shall not designate a Vice Captain.

## BR-050 — Captain History

Captain selections shall be retained as part of historical gameweek records.

## BR-051 — Captain Tie-Break Statistic

The cumulative Captain points earned by a FantasyTeam shall be retained and shall participate in the league tie-break hierarchy as defined later in this document.

---

# 11. Initial Draft

## BR-052 — Initial Draft

Each FantasyTeam shall select 25 players in the initial draft.

## BR-053 — Draft Style

The initial draft shall use a snake draft.

## BR-054 — Draft Order

The initial draft order shall be randomized.

## BR-055 — Draft Rounds

The draft shall continue through the required number of selections to populate each FantasyTeam's 25-player squad.

## BR-056 — Secondary Draft Order

The secondary draft shall use the standings at the time the secondary draft begins to determine draft order.

The FantasyTeam in last place shall receive the first selection.

The FantasyTeam in first place shall receive the final selection of the first round.

The secondary draft shall then proceed using snake-draft rules.

Example:

```text
Standings:
1. Team A
2. Team B
3. Team C
4. Team D

Round 1:
D → C → B → A

Round 2:
A → B → C → D
```

The standings used to establish the secondary draft order shall be captured at the beginning of the secondary draft.

## BR-057 — Draft Timer

The default draft selection timer shall be five minutes.

## BR-058 — Draft Timeout

If a User does not make a selection before the timer expires, the League Administrator may extend the timer.

## BR-059 — Draft Player Availability

An EPL player already owned by another FantasyTeam in the League shall not be available for selection.

---

# 12. Secondary Draft

## BR-060 — Secondary Draft Size

Each FantasyTeam shall receive five selections during the secondary draft.

## BR-061 — Secondary Draft Eligibility

All currently unowned EPL players shall be eligible for the secondary draft.

## BR-062 — Secondary Draft Squad Expansion

The five secondary-draft selections shall be added to the FantasyTeam's existing squad.

The secondary draft does not replace players.

The normal result is therefore:

```text
25 initial players
+ 5 secondary-draft players
= 30 players
```

The squad may subsequently exceed 30 players because of replacement selections.

## BR-063 — Replacement Players

Players who leave the EPL or are determined to have a season-ending injury may become eligible for additional replacement selections.

## BR-064 — Replacement Does Not Require Immediate Exchange

A User does not need to replace an eligible player before making another secondary-draft selection.

Replacement selections expand the squad.

## BR-065 — Replacement Eligibility

A player becomes replacement eligible when the League Administrator determines that the applicable eligibility requirements have been met.

## BR-066 — EPL Transfer-Out Eligibility

Any fantasy-owned player who transfers out of the EPL becomes eligible for replacement.

## BR-067 — Season-Ending Injury Eligibility

A fantasy-owned player determined to have a season-ending injury becomes eligible for replacement.

## BR-068 — Injury Authority

The League Administrator shall determine whether an injury qualifies as season-ending.

The decision shall be based on consensus among league users.

## BR-069 — Secondary Draft Timing

The secondary draft shall normally occur as soon as practical after the official EPL transfer window closes.

The preferred timing is the day after the transfer window closes when practical.

If that day conflicts with EPL fixtures or other scheduling constraints, the League Administrator/application shall schedule the draft on the next practical day.

A Tuesday following the transfer-window close is an example of an acceptable fallback where appropriate.

---

# 13. EPL Player Transfers

## BR-070 — Player Transfer Between EPL Clubs

If a fantasy-owned player transfers from one EPL club to another EPL club, the fantasy ownership shall remain unchanged.

## BR-071 — EPL Transfer-Out

If a fantasy-owned player transfers out of the EPL, the player remains part of the historical FantasyTeam record but becomes eligible for replacement.

## BR-072 — Player Identity

A player transferring between EPL clubs remains the same fantasy player entity.

## BR-073 — Gameweek Transfer Scoring

The player's official score for a gameweek shall be used as provided by the official FPL data.

## BR-074 — Multiple Club Appearance

If official FPL data records a player as earning points while associated with more than one club during a gameweek, those points shall be credited to the FantasyTeam owning that player.

This is considered beneficial to the FantasyTeam and shall not be artificially reduced.

---

# 14. Player Scoring

## BR-075 — Official FPL Scoring

The application shall use official FPL fantasy points directly.

## BR-076 — FPL Authority

Official FPL data shall be the authoritative source for fantasy player scoring whenever available.

## BR-077 — Player Gameweek Score

Each player's official gameweek fantasy score shall be retained.

## BR-078 — Captain Scoring

Captain scoring shall follow official FPL scoring rules.

## BR-079 — Finalized Score

Once official scoring has been finalized, the application shall preserve the finalized result.

---

# 15. Fantasy Goals

Fantasy Goals are separate from official FPL fantasy points.

## BR-080 — Fantasy Goals For

Fantasy Goals For shall equal the actual number of goals scored by players represented in the User's submitted 15-player gameweek roster.

All goals count regardless of position.

## BR-081 — Goalkeeper Goals

Goals scored by goalkeepers count toward Fantasy Goals For.

## BR-082 — Defender Goals

Goals scored by defenders count toward Fantasy Goals For.

## BR-083 — Midfielder Goals

Goals scored by midfielders count toward Fantasy Goals For.

## BR-084 — Forward Goals

Goals scored by forwards count toward Fantasy Goals For.

## BR-085 — Penalty Goals

Penalty goals count as normal goals toward Fantasy Goals For.

## BR-086 — Fantasy Goals Against

Fantasy Goals Against shall consist of:

1. Goals conceded by the goalkeeper represented in the submitted roster.
2. The average number of goals scored against the EPL clubs represented by the selected defenders.

## BR-087 — Defender Average

The defender component shall be calculated as:

```text
Total goals conceded by defender clubs
/
Number of selected defenders
```

The result shall be expressed as an integer.

## BR-088 — Defender Example

If the selected defenders represent:

```text
Chelsea       1 goal conceded
Tottenham     2 goals conceded
Arsenal       1 goal conceded
Arsenal       1 goal conceded
Tottenham     2 goals conceded
```

Then:

```text
Total = 7
Average = 7 / 5 = 1.4
Integer result = 1
```

The resulting defender contribution is 1.

## BR-089 — Fantasy Goal Difference

Fantasy Goal Difference shall equal:

```text
Fantasy Goals For - Fantasy Goals Against
```

## BR-090 — Own Goals

Own goals shall increase the Fantasy Goals Against calculation.

## BR-091 — Own Goals and Fantasy Goals For

An own goal shall not count as a goal scored by the player credited with the own goal.

Instead, it shall contribute to the applicable Goals Against calculation.

---

# 16. Gameweek Management

## BR-092 — Official Gameweek

Application gameweeks shall correspond to official FPL gameweeks.

## BR-093 — Gameweek Deadline

The weekly roster deadline shall be one hour before kickoff of the first EPL fixture in that gameweek.

## BR-094 — Roster Lock

Once the deadline passes, the FantasyTeam's submitted 15-player roster shall be locked.

## BR-095 — Captain Lock

The Captain selection shall be locked at the same deadline.

## BR-096 — No Late User Changes

Users shall not be able to modify their roster after the deadline.

## BR-097 — Administrator Changes

The League Administrator may make a post-deadline correction in exceptional circumstances.

Typical examples include:

- User accidentally missing the deadline
- User accidentally selecting the wrong player

## BR-098 — Administrative Changes

An authorized administrative correction shall replace the affected roster information.

It shall not alter the underlying official FPL player scoring.

## BR-099 — Administrative Audit

Administrative changes shall be auditable.

---

# 17. Postponed Fixtures

## BR-100 — Postponed Fixture

If an EPL fixture is postponed and rescheduled into another gameweek, scoring shall follow the official FPL treatment.

## BR-101 — User Responsibility

Users are expected to account for known rescheduled fixtures when submitting their weekly roster.

## BR-102 — Rescheduled Fixture Opportunity

A User may select a player for a later gameweek when the rescheduled fixture occurs.

## BR-103 — Gameweek Assignment

The application shall use the official FPL gameweek assignment for rescheduled fixtures.

---

# 18. Abandoned Fixtures

## BR-104 — Official FPL Treatment

If official FPL records player statistics from an abandoned fixture and those statistics count toward FPL scoring, the application shall count them.

## BR-105 — Replay

If a fixture is abandoned and subsequently replayed and official FPL scoring does not recognize the original statistics, the application shall wait for the replayed fixture.

## BR-106 — Source of Truth

The official FPL treatment shall determine the fantasy scoring outcome.

---

# 19. Head-to-Head Competition

## BR-107 — Head-to-Head Schedule

Each FantasyTeam shall play against other FantasyTeams in its League according to a schedule generated at the beginning of the Season.

## BR-108 — Random Scheduling

Opponents shall be randomly determined.

## BR-109 — Balanced Schedule

The schedule shall attempt to ensure every FantasyTeam plays every other FantasyTeam the same number of times as mathematically possible.

## BR-110 — Schedule Persistence

Once generated, the schedule shall be persisted.

## BR-111 — Weekly Match

Each gameweek shall produce a head-to-head matchup for each participating FantasyTeam where fixtures permit.

## BR-112 — Match Winner

The FantasyTeam with the higher weekly fantasy score shall win.

## BR-113 — Match Draw

Equal scores shall result in a draw.

## BR-114 — Match Loss

The lower-scoring FantasyTeam shall lose.

---

# 20. League Points

## BR-115 — Win

A head-to-head win awards three league points.

## BR-116 — Draw

A head-to-head draw awards one league point.

## BR-117 — Loss

A head-to-head loss awards zero league points.

## BR-118 — Standings

League standings shall be ordered primarily by accumulated league points.

---

# 21. EPL-Style Tie-Break Hierarchy

The application shall use an EPL-style tie-breaking hierarchy.

## BR-119 — Primary Tie-Break

Higher league points rank higher.

## BR-120 — Goal Difference

If league points are equal, higher Fantasy Goal Difference ranks higher.

## BR-121 — Goals Scored

If league points and Fantasy Goal Difference are equal, higher Fantasy Goals For ranks higher.

## BR-122 — Head-to-Head / Additional Official EPL Criteria

The application shall implement the applicable additional EPL competition tie-break criteria represented by the official EPL regulations for the applicable season.

The precise implementation shall be configurable rather than hard-coded so that regulatory changes can be accommodated.

## BR-123 — Captain Points

Cumulative Captain points shall be used as an additional application-specific tie-break after the applicable EPL-derived criteria.

## BR-124 — Season Goal Prediction

The season goal prediction shall be the final deterministic tie-break.

## BR-125 — Final Randomization

No random tie-break shall be required if the Season Goal Prediction resolves the tie.

If all defined deterministic criteria remain tied, the application shall use a randomized tie-break as the final fallback.

---

# 22. Season Goal Prediction Tie-Break

## BR-126 — Prediction Entry

Each User shall enter a prediction for the total number of EPL goals scored during the Season.

## BR-127 — Prediction Timing

The prediction shall be entered at the beginning of the Season.

## BR-128 — Prediction Immutability

Once the Season begins, the prediction shall be locked.

## BR-129 — Prediction Storage

The prediction shall be stored permanently with the Season/FantasyTeam record.

## BR-130 — Actual Goal Total

The application shall determine the actual total number of EPL goals scored during the Season.

## BR-131 — Prediction Difference

The absolute difference shall be calculated as:

```text
ABS(Prediction - Actual)
```

## BR-132 — Closest Prediction

The prediction with the smallest absolute difference wins the tie-break.

## BR-133 — Under/Over Tie

If two predictions are equally close to the actual total, the prediction that is **less than or equal to the actual total** shall win.

Example:

```text
Actual = 1,234

User A = 1,233
Difference = 1

User B = 1,235
Difference = 1

Winner = User A
```

## BR-134 — Over Prediction Can Win

A prediction may exceed the actual total and still win if it is closer than all lower predictions.

Example:

```text
Actual = 1,234

User A = 1,230
Difference = 4

User B = 1,235
Difference = 1

Winner = User B
```

## BR-135 — Prediction Audit

The original prediction and calculated difference shall be retained for audit purposes.

---

# 23. Secondary Draft Standings Snapshot

## BR-136 — Draft Standings Snapshot

The standings used to determine the secondary draft order shall be captured at the beginning of the draft.

## BR-137 — Tie in Draft Order

If two FantasyTeams are tied when determining secondary draft order, the complete league tie-break hierarchy shall be used.

## BR-138 — Draft Order Persistence

The resulting draft order shall be persisted and shall not change because subsequent scores change.

---

# 24. Score Corrections

## BR-139 — Official Correction

The application shall attempt to leverage official FPL data to detect and apply scoring corrections.

## BR-140 — Administrator Override

The League Administrator shall be able to manually override player statistics when necessary.

## BR-141 — Override Priority

An administrator override takes precedence over official data.

## BR-142 — Undo Override

The League Administrator shall be able to remove/undo an override.

## BR-143 — Official Data After Override

If an administrator removes the override after official data has been corrected, the official data shall become authoritative again.

## BR-144 — Override Identification

Any manually overridden value shall be clearly identifiable.

## BR-145 — Override Audit

The application shall record:

- Original value
- Override value
- User making the override
- Timestamp
- Reason, where provided
- Whether the override remains active

---

# 25. Administrative Corrections

## BR-146 — Post-Deadline Administrative Correction

The League Administrator may correct a roster after the gameweek deadline in exceptional circumstances.

## BR-147 — Correction Scope

An administrative correction may affect:

- Weekly score
- Opponent score
- Match result
- League points
- Goal Difference
- Captain points
- Standings
- Historical results

## BR-148 — No Special Recalculation Rule

Administrative changes shall be processed using the same domain calculation rules as normal gameplay.

## BR-149 — Auditability

Every administrative change shall be auditable.

---

# 26. Notifications

## BR-150 — Notification Preferences

Users shall be able to select which notifications they receive.

## BR-151 — Notification Channels

Users shall be able to select:

- Email
- Text message

for supported notification types.

## BR-152 — Gameweek Reminder

Users may receive reminders to submit their weekly roster.

## BR-153 — Weekly Score Notification

Users may receive their weekly score.

## BR-154 — Weekly Standings Notification

Users may receive their weekly league-table position.

## BR-155 — Notification Preferences by Event

Notification preferences shall be configurable independently by event type and channel.

---

# 27. Authentication and Authorization

## BR-156 — Application-Managed Authentication

The initial application shall manage authentication internally.

## BR-157 — Future Identity Provider

The authentication architecture shall permit future migration to an external Identity Provider.

## BR-158 — Password Security

Passwords shall never be stored in plaintext.

Passwords shall be stored using a modern adaptive password-hashing mechanism.

## BR-159 — Authentication Tokens

Authenticated API access shall use secure, appropriately scoped authentication tokens.

## BR-160 — Authorization

Authorization shall be enforced server-side.

The client application shall never be considered authoritative for permission decisions.

## BR-161 — League Authorization

Users shall only access league data for leagues in which they have appropriate membership or administrative privileges.

## BR-162 — Administrator Authorization

League Administrator functions shall require explicit authorization.

## BR-163 — Object-Level Authorization

APIs shall validate that the authenticated user is authorized to access or modify the specific User, LeagueMembership, FantasyTeam, roster, draft, or other domain object involved.

---

# 28. Security

## BR-164 — Secure Transport

All production application communication shall use HTTPS.

## BR-165 — Input Validation

All external input shall be validated.

## BR-166 — Server-Side Validation

Business rules shall be enforced server-side regardless of client validation.

## BR-167 — Injection Protection

The application shall protect against SQL injection, command injection, XSS, and other common injection attacks.

## BR-168 — CSRF

Browser-based state-changing operations shall implement appropriate CSRF protections where applicable.

## BR-169 — Rate Limiting

Authentication and other abuse-prone endpoints shall support rate limiting.

## BR-170 — Security Logging

Security-relevant events shall be logged.

## BR-171 — Sensitive Data Logging

Passwords, authentication secrets, tokens, and other sensitive information shall not be written to application logs.

## BR-172 — Least Privilege

Services and database users shall operate using the minimum permissions required.

## BR-173 — Secrets Management

Secrets shall not be committed to source control.

---

# 29. Data Retention

## BR-174 — Historical Retention

Historical league and player data shall be retained for at least ten years.

## BR-175 — Soft Deleted Users

Historical records associated with retired users shall remain available.

## BR-176 — Historical Integrity

Historical results shall not be changed merely because current User profile information changes.

---

# 30. Audit

## BR-177 — Administrative Audit

Administrative actions affecting competitive results shall be audited.

## BR-178 — Draft Audit

Draft selections shall be retained.

## BR-179 — Roster Audit

Submitted rosters shall be retained.

## BR-180 — Captain Audit

Captain selections shall be retained.

## BR-181 — Score Audit

Score changes shall be retained.

## BR-182 — Override Audit

Manual statistical overrides shall be retained.

## BR-321 — Audit Log Filtering

The Administrative Audit Log shall support filtering entries by action type, by a date range, and by the affected FantasyTeam — or, for an action not scoped to a specific FantasyTeam (e.g., a League/Season configuration change), by "League Settings."

## BR-322 — Audit Log Entry Detail Expansion

Each Administrative Audit Log entry shall support expanding, in place within the chronological list, to reveal its full recorded before/after state, without requiring navigation away from the list.

---

# 31. Architecture

## BR-183 — API Architecture

The application shall expose business functionality through a secure API layer.

## BR-184 — Domain Separation

Business rules shall not be embedded exclusively in UI code.

## BR-185 — Domain Model

The architecture shall maintain clear separation between:

- Presentation
- Application/services
- Domain
- Infrastructure/data access

## BR-186 — Client Independence

The backend shall not assume a particular client technology.

## BR-187 — Web Application

The application shall provide a responsive web interface.

## BR-188 — Mobile Application

The application shall provide a native mobile experience.

## BR-189 — Cross-Platform Mobile Architecture

The mobile implementation should use a modern cross-platform native approach unless architectural analysis demonstrates a compelling reason to use separate native applications.

The final technology selection shall be treated as an architecture decision rather than a business requirement.

## BR-190 — API Versioning

The API shall support versioning so future mobile/web clients can evolve without immediately breaking existing clients.

---

# 32. Domain/Data Integrity

## BR-191 — Unique Player Ownership

Within a League and Season, an EPL player may be associated with no more than one FantasyTeam.

## BR-192 — Squad Integrity

A player may not be added to a FantasyTeam if already owned by another FantasyTeam in the same League and Season.

## BR-193 — FantasyTeam Uniqueness

A LeagueMembership may have only one FantasyTeam for a particular Season.

## BR-194 — Roster Membership

A weekly roster player must belong to the FantasyTeam's squad.

## BR-195 — Captain Membership

The Captain must belong to the submitted weekly roster.

## BR-196 — Roster Size

A submitted weekly roster must contain exactly 15 players.

## BR-197 — Squad Size

The initial draft must result in exactly 25 players.

## BR-198 — Secondary Draft

Secondary-draft selections are added to the existing squad.

---

# 33. Data Model — Recommended Aggregate Structure

The domain should be modeled approximately as follows:

```text
User
 |
 +-- UserProfile
 |
 +--< LeagueMembership >-- League
          |
          +-- FantasyTeam
                 |
                 +-- Season
                 |
                 +-- SquadPlayer
                 |
                 +-- GameweekRoster
                 |      |
                 |      +-- RosterPlayer
                 |      +-- CaptainSelection
                 |
                 +-- Draft
                 |      |
                 |      +-- DraftSelection
                 |
                 +-- GameweekScore
                 |
                 +-- FantasyGoals
                 |
                 +-- HeadToHeadMatch
                 |
                 +-- StandingsSnapshot
```

---

# 34. Recommended Entity Details

## User

```text
User
----
UserId
Username
Status
CreatedAt
UpdatedAt
RetiredAt
```

## UserProfile

```text
UserProfile
-----------
UserId
DefaultIconId
CreatedAt
UpdatedAt
```

## LeagueMembership

```text
LeagueMembership
----------------
LeagueMembershipId
LeagueId
UserId
LeagueIconId
Status
JoinedAt
LeftAt
```

## FantasyTeam

```text
FantasyTeam
-----------
FantasyTeamId
LeagueMembershipId
SeasonId
Status
CreatedAt
UpdatedAt
```

## SquadPlayer

```text
SquadPlayer
-----------
SquadPlayerId
FantasyTeamId
PlayerId
AcquisitionType
AcquiredAt
ReleasedAt
IsCurrentlyOwned
```

`AcquisitionType` may include:

```text
InitialDraft
SecondaryDraft
Replacement
```

## GameweekRoster

```text
GameweekRoster
--------------
GameweekRosterId
FantasyTeamId
GameweekId
SubmittedAt
LockedAt
Status
```

## RosterPlayer

```text
RosterPlayer
------------
GameweekRosterId
PlayerId
IsCaptain
FantasyPoints
GoalsScored
GoalsConceded
```

## Draft

```text
Draft
-----
DraftId
LeagueId
SeasonId
DraftType
Status
StartTime
EndTime
```

## DraftSelection

```text
DraftSelection
--------------
DraftSelectionId
DraftId
FantasyTeamId
PlayerId
Round
PickNumber
SelectedAt
```

## GameweekScore

```text
GameweekScore
-------------
GameweekScoreId
FantasyTeamId
GameweekId
FantasyPoints
CaptainPoints
FantasyGoalsFor
FantasyGoalsAgainst
FantasyGoalDifference
```

## HeadToHeadMatch

```text
HeadToHeadMatch
---------------
MatchId
LeagueId
SeasonId
GameweekId
HomeFantasyTeamId
AwayFantasyTeamId
HomeScore
AwayScore
Result
```

---

# 35. League/Season/FantasyTeam Relationship

The relationship between these entities is fundamental:

```text
User
 |
 | 1:N
 v
LeagueMembership
 |
 | N:1
 v
League

LeagueMembership
 |
 | 1:N
 v
FantasyTeam
 |
 | N:1
 v
Season
```

However, the business rule shall constrain:

```text
LeagueMembership + Season = one FantasyTeam
```

Therefore:

```text
User
 ├── League A
 │    └── FantasyTeam — 2026/27
 │
 ├── League B
 │    └── FantasyTeam — 2026/27
 │
 └── League A
      └── FantasyTeam — 2027/28
```

This allows the same User to participate in:

- Multiple leagues during one season.
- Multiple seasons within the same league.
- Different leagues with different squads and statistics.

---

# 36. Mobile/Web User Experience

## BR-199 — Active League Selection

When multiple leagues are available, the application shall clearly identify the currently selected League.

## BR-200 — Active FantasyTeam

The application shall clearly identify the FantasyTeam being managed.

## BR-201 — League Context

League-specific operations shall display sufficient League context to prevent accidental changes to the wrong League.

## BR-202 — Profile Context

Users shall be able to distinguish their global profile settings from League-specific settings.

## BR-203 — League Icon

The League-specific icon shall be displayed wherever the User is represented within that League.

---

# 37. Draft User Experience

## BR-204 — Draft Order Display

The application shall display the current draft order.

## BR-205 — Current Selection

The application shall clearly identify whose turn it is.

## BR-206 — Draft Timer

The five-minute timer shall be visible during a draft.

## BR-207 — Draft History

Users shall be able to view completed selections.

## BR-208 — Ownership Visibility

The application shall clearly indicate which players are already owned.

## BR-310 — Draft Player Pool Position Filter

The application shall allow the draft player pool to be filtered to a single position (Goalkeeper, Defender, Midfielder, or Forward) or to an unfiltered "All" view, so a User can narrow the pool to the position they intend to select next.

## BR-311 — Draft Player Pool Search

The application shall allow the draft player pool to be searched by player name, so a User can quickly locate a specific player rather than browsing the full pool.

## BR-312 — Draft Player Pool Sorting

The application shall allow the draft player pool to be sorted by any displayed column, including player name, club, position, and the statistics required by BR-313, so a User can rank available players by the criterion most relevant to their decision.

## BR-313 — Draft Player Pool Statistics Display

The draft player pool shall display each player's season-to-date Minutes Played, Games Played, and Official FPL Fantasy Points, sourced from the same official FPL data used for scoring (BR-075, BR-076).

These statistics are most valuable during the Secondary Draft and Replacement selections (Section 12), when meaningful season-to-date statistics already exist for most players. Because the player pool, its filtering (BR-310), search (BR-311), and sorting (BR-312) are shared across all Draft types, these same columns shall also appear during the Initial Draft; before a Season's first Gameweek has been scored, all three statistics shall display as zero for every player rather than being hidden or omitted.

## BR-323 — Skipped Pick Visibility

When a FantasyTeam's pick is skipped due to timeout (BR-282), the application shall visibly indicate the skip to Draft participants, distinct from a completed selection.

## BR-324 — Pending Makeup Pick Queue Display

The application shall display the current queue of pending makeup picks, including which FantasyTeams are waiting for a makeup turn and the order in which they will pick, once the final regularly scheduled round has completed.

## BR-325 — Makeup Pick Re-Queue Visibility

If a makeup pick is itself skipped and re-queued (BR-282), the application shall visibly indicate the re-queue event, distinct from an initial skip.

---

# 38. Weekly Roster User Experience

## BR-209 — Squad View

Users shall be able to view their complete squad.

## BR-314 — Squad View Position Filter

The application shall allow the Squad View to be filtered to a single position (Goalkeeper, Defender, Midfielder, or Forward) or to an unfiltered "All" view, mirroring BR-310's Draft Player Pool position filter.

## BR-315 — Squad View Search

The application shall allow the Squad View to be searched by player name, mirroring BR-311's Draft Player Pool search.

## BR-316 — Squad View Sorting

The application shall allow the Squad View to be sorted by any displayed column, including player name, club, position, and the statistics required by BR-317, mirroring BR-312's Draft Player Pool sorting.

## BR-317 — Squad View Statistics Display

The Squad View shall display each player's season-to-date Minutes Played, Games Played, and Official FPL Fantasy Points, sourced from the same data used to satisfy BR-313's Draft Player Pool statistics display.

## BR-318 — Squad View Acquisition Type Display

The Squad View shall display how each player was acquired (Initial Draft, Secondary Draft, or Replacement), consistent with the acquisition history already required to be retained by BR-264.

## BR-319 — Squad View Current-Gameweek Roster Indicator

The Squad View shall indicate, for each squad player, whether that player is included in the FantasyTeam's current Gameweek roster.

## BR-320 — Squad View Replacement-Eligibility Indicator

The Squad View shall indicate which squad players are currently replacement-eligible (BR-065), so a User can see at a glance which unused replacement-selection opportunities (BR-307) are available to them.

## BR-210 — Weekly Roster Selection

Users shall select 15 players for each gameweek.

## BR-211 — Positional Validation

The application shall prevent submission when positional minimums are not satisfied.

## BR-212 — Captain Selection

The User shall select one Captain.

## BR-213 — Deadline Display

The application shall prominently display the gameweek submission deadline.

## BR-214 — Locked Roster

Once locked, the application shall clearly identify the roster as locked.

---

# 39. Standings

## BR-215 — League Table

The application shall display:

- Position
- Username
- League icon
- Played
- Wins
- Draws
- Losses
- League points
- Fantasy Goals For
- Fantasy Goals Against
- Fantasy Goal Difference
- Captain points
- Other applicable tie-break statistics

## BR-216 — Standings Ordering

Standings shall always use the configured tie-break hierarchy.

## BR-217 — Historical Standings

Historical standings shall remain accessible for completed seasons.

---

# 40. Head-to-Head Schedule

## BR-218 — Schedule

Users shall be able to view their complete season schedule.

## BR-219 — Opponent Identity

Opponents shall be displayed using their username and applicable League icon.

## BR-220 — Match Result

Each completed matchup shall display the relevant fantasy scores and result.

---

# 41. League Messaging

## BR-221 — League Messages

The League Administrator shall be able to publish league messages.

## BR-222 — Message Visibility

League messages shall only be visible to appropriate League members.

## BR-223 — Message History

League messages shall be retained according to the application's historical retention policy.

---

# 42. Notifications Architecture

## BR-224 — Asynchronous Notifications

Notification delivery should be asynchronous and should not unnecessarily block gameplay operations.

## BR-225 — Delivery Failure

Notification failures shall be logged and retried according to configured policies.

## BR-226 — User Preference Enforcement

Disabled notification types shall not be sent through the disabled channel.

---

# 43. External EPL/FPL Integration

## BR-227 — External Data Integration

The application shall integrate with the official FPL data source where technically and legally feasible.

## BR-228 — Player Data

The system shall retrieve current EPL/FPL player information.

## BR-229 — Fixture Data

The system shall retrieve EPL/FPL fixture information.

## BR-230 — Gameweek Data

The system shall retrieve official gameweek information.

## BR-231 — Player Statistics

The system shall retrieve official player statistics required for scoring.

## BR-232 — Data Synchronization

External data synchronization shall be repeatable and idempotent.

## BR-233 — External Failure

Temporary external API failures shall not corrupt existing league data.

## BR-234 — Data Reconciliation

The system shall be capable of reconciling local data with subsequently corrected official data.

---

# 44. External Data Authority

The system shall distinguish:

```text
Official Data
Administrator Override
Calculated Fantasy Data
Historical Snapshot
```

The precedence shall be:

```text
Administrator Override
        >
Official FPL Data
        >
Application Calculation
```

When an administrator override is removed:

```text
Official FPL Data
        >
Application Calculation
```

---

# 45. Performance and Reliability

## BR-235 — Responsive API

Normal user operations should return within acceptable interactive response times.

## BR-236 — Background Processing

Long-running operations such as external-data synchronization should execute asynchronously where appropriate.

## BR-237 — Idempotency

Data synchronization and scoring operations should be safe to retry.

## BR-238 — Transactional Integrity

Draft selections, roster submissions, scoring updates, and administrative corrections shall maintain transactional consistency.

---

# 46. Observability

## BR-239 — Application Logging

The application shall provide structured application logging.

## BR-240 — Error Logging

Unhandled application errors shall be logged with sufficient diagnostic information.

## BR-241 — Correlation IDs

API requests should support correlation identifiers for troubleshooting.

## BR-242 — Audit Logs

Competitive-impacting administrative operations shall be separately auditable.

---

# 47. Testing Requirements

The AIDLC-generated implementation shall include automated tests covering:

## BR-243 — User Tests

- Username uniqueness
- Username changes
- Profile creation
- Default icon selection
- League-specific icon selection
- Icon fallback
- Retired user handling

## BR-244 — League Tests

- League creation
- Membership
- Invitations
- Invitation expiration
- Multiple leagues
- League authorization

## BR-245 — FantasyTeam Tests

- LeagueMembership/FantasyTeam relationship
- One FantasyTeam per LeagueMembership/Season
- Multiple leagues for one User
- Multiple seasons
- Squad isolation

## BR-246 — Draft Tests

- Random initial order
- Snake draft
- Ownership restrictions
- 25-player initial squad
- Five-player secondary draft
- Standings-based secondary order
- Five-minute timer
- Timer extension
- Draft persistence

## BR-247 — Roster Tests

- 15-player roster
- Positional minimums
- No positional maximums
- Squad membership
- Deadline locking
- Captain eligibility
- No automatic substitution

## BR-248 — Captain Tests

- Captain selection
- Captain multiplier
- Non-playing Captain
- No Vice Captain
- Captain tie-break accumulation

## BR-249 — Scoring Tests

- Official FPL points
- Captain scoring
- Corrected scores
- Administrator overrides
- Undoing overrides

## BR-250 — Fantasy Goal Tests

- Goals For
- Goals Against
- Goalkeeper goals
- Defender averages
- Integer rounding/truncation behavior
- Own goals
- Penalty goals
- Goal difference

## BR-251 — Head-to-Head Tests

- Schedule generation
- Balanced scheduling
- Win
- Draw
- Loss
- League points

## BR-252 — Tie-Break Tests

Tests shall cover every tie-break level, including:

1. League points
2. Fantasy Goal Difference
3. Fantasy Goals For
4. Applicable EPL-derived criteria
5. Captain points
6. Season Goal Prediction
7. Final randomized fallback

## BR-253 — Prediction Tests

At minimum:

```text
Actual = 1234
Prediction = 1230
Difference = 4

Prediction = 1235
Difference = 1
Winner = 1235
```

and:

```text
Actual = 1234
Prediction A = 1233
Difference = 1

Prediction B = 1235
Difference = 1

Winner = 1233
```

## BR-254 — Authorization Tests

Tests shall ensure that users cannot modify:

- Another user's profile
- Another user's FantasyTeam
- Another user's roster
- Another user's Captain
- Another League's data
- Administrative functions without permission

---

# 48. Acceptance Criteria

## BR-255 — Complete Season

The application shall be capable of representing a complete EPL Season from:

```text
League Creation
        ↓
User Invitation
        ↓
Initial Draft
        ↓
25-player Squads
        ↓
Weekly 15-player Rosters
        ↓
Captain Selection
        ↓
FPL Scoring
        ↓
Head-to-Head Results
        ↓
League Standings
        ↓
EPL Transfer Window
        ↓
Secondary Draft
        ↓
Replacement Selections
        ↓
Final Standings
        ↓
Historical Season
```

## BR-256 — Multiple League Isolation

A User participating in two leagues shall be able to maintain completely different squads, rosters, captains, scores, and standings in each League.

## BR-257 — Historical Integrity

Changing a User's username or icon shall not corrupt or remove historical league information.

## BR-258 — Administrator Override

An administrator shall be able to correct an official statistic and later remove the override so that corrected official data becomes authoritative.

---

# 49. Additional Domain Rules

## BR-259 — Player Leaving EPL

When a player leaves the EPL, their historical ownership shall remain intact.

Their active ownership status shall indicate that the player is no longer eligible for normal roster selection when applicable.

## BR-260 — Replacement Availability

Replacement eligibility shall be determined by League Administrator action.

## BR-261 — Replacement Ownership

A replacement player must be unowned within the League.

## BR-262 — No Duplicate Ownership

The system shall prevent two FantasyTeams from owning the same player in the same League/Season.

## BR-263 — Historical Squad

The system shall preserve the acquisition history of all players.

## BR-264 — Acquisition Type

Every squad player shall identify how the player was acquired:

- Initial Draft
- Secondary Draft
- Replacement

---

# 50. User Profile Requirements

## BR-265 — Username

Each User shall have a unique username that identifies them within the application.

The username shall be used when displaying the User in league-facing functionality.

## BR-266 — Username Uniqueness

Username uniqueness shall be globally enforced.

## BR-267 — Default User Icon

Each User shall have a default profile icon selected from the application's predefined collection.

## BR-268 — League-Specific Icon

A User may select a different icon for each League.

## BR-269 — League Identity

A User's participation in a League shall be represented by LeagueMembership.

The LeagueMembership shall contain League-specific presentation information.

## BR-270 — Username Changes

A User may change their username subject to application rules.

Changing the username shall not change the underlying User identity.

## BR-271 — Historical Identity

Historical records shall preserve sufficient information to identify the User regardless of subsequent profile changes.

## BR-326 — Historical Username Display Policy

Finalized historical records (standings, drafts, rosters, head-to-head results, and other competition history) shall display the username the User held at the time the record was created, not the User's current username.

Historical records shall continue to resolve by the User's immutable identifier (BR-278) regardless of subsequent username changes; only the displayed label is affected by this policy, never which User a record belongs to.

This resolves the "Historical Username Display" item previously listed as an open decision (Section 54).

## BR-272 — Retired Users

Retired Users shall remain represented in historical league records.

## BR-273 — Profile Visibility

Only information explicitly intended for League visibility shall be exposed to League members.

## BR-274 — League Icon Validation

League-specific icons shall be validated against the application's valid icon collection.

## BR-275 — League Icon Removal

Users may remove a League-specific icon and revert to the default icon.

## BR-276 — Default Icon Changes

Changing a User's default icon shall not change an existing League-specific icon.

If no League-specific icon exists, the new default icon shall be displayed.

## BR-277 — User Display Identity

Within a League, the normal User representation shall be:

```text
[League Icon] [Username]
```

## BR-278 — Stable Internal Identity

User-facing identifiers shall not be used as relational database keys.

Immutable internal identifiers shall be used for domain relationships.

---

# 51. Architecture Principles

The AIDLC-generated solution shall follow these principles.

## AP-001 — Domain-First Design

Business rules shall be represented in the domain/application layer rather than being scattered throughout controllers and UI components.

## AP-002 — API as Security Boundary

The API shall enforce all authorization and business rules.

## AP-003 — Client as Untrusted

Web and mobile clients shall be treated as untrusted.

## AP-004 — Immutable Historical Records

Competitive results shall be treated as historical records rather than mutable profile data.

## AP-005 — Explicit Auditability

Competitive-impacting changes shall be traceable.

## AP-006 — Configuration Over Hard Coding

Rules likely to change between EPL seasons should be configurable where practical.

## AP-007 — External Data Isolation

External FPL integration shall be isolated behind an application service/interface so the remainder of the domain is not tightly coupled to a specific provider.

## AP-008 — Idempotent Synchronization

External data imports shall be safely repeatable.

## AP-009 — Transactional Draft Selection

A draft selection shall be atomic.

Two users must never successfully acquire the same player because of a race condition.

## AP-010 — Concurrency Control

Draft and roster operations shall account for concurrent requests.

---

# 52. Recommended Bounded Contexts

The initial architecture should consider the following logical bounded contexts:

```text
Identity
   |
League Management
   |
Season Management
   |
Player / EPL Data
   |
Draft Management
   |
Squad Management
   |
Gameweek Management
   |
Scoring
   |
Head-to-Head Competition
   |
Standings
   |
Notifications
   |
Administration / Audit
```

These do not necessarily require separate deployable services.

For the initial application, a modular monolith is likely preferable to prematurely introducing microservices.

The architecture shall preserve clear module boundaries so services can be extracted later if justified.

---

# 53. Decision Register

The following decisions are incorporated into this BRD.

| ID | Decision |
|---|---|
| DEC-001 | An EPL player may be owned by only one User/FantasyTeam within a League |
| DEC-002 | No positional maximums |
| DEC-003 | Initial draft size = 25 |
| DEC-004 | Secondary draft = 5 selections |
| DEC-005 | Initial draft order = randomized |
| DEC-006 | Draft style = snake |
| DEC-007 | Normal transfers = 0 |
| DEC-008 | Transfer window follows official EPL transfer window |
| DEC-009 | Loss = 0 league points |
| DEC-010 | Head-to-head opponents randomly scheduled with balanced frequency |
| DEC-011 | EPL-style standings tie-break hierarchy |
| DEC-012 | Official FPL fantasy points are used |
| DEC-013 | Weekly roster locks one hour before first gameweek kickoff |
| DEC-014 | No late automatic roster changes |
| DEC-015 | Official FPL corrections preferred; admin override available |
| DEC-016 | Anyone with website access may initially create a League |
| DEC-017 | League invitation via email/text link |
| DEC-018 | League Administrator has broad league-management authority |
| DEC-019 | Users may participate in multiple leagues |
| DEC-020 | Existing leagues may be reused for future seasons |
| DEC-021 | User-configurable notifications |
| DEC-022 | Native/cross-platform mobile architecture to be selected during architecture phase |
| DEC-023 | Application-managed authentication initially |
| DEC-024 | Ten-year data retention |
| DEC-025 | User deletion = soft retirement |
| DEC-026 | 25-player squad; 15-player weekly roster |
| DEC-028 | Fantasy Goals For/Against as defined in this BRD |
| DEC-029 | EPL club transfer does not affect ownership; leaving EPL enables replacement |
| DEC-030 | Administrator override has priority over official correction until override is removed |
| DEC-031 | No automatic bench substitution |
| DEC-033 | Non-playing Captain receives zero; no replacement |
| DEC-034 | No Vice Captain |
| DEC-035 | Highest-scoring 11 determine scoring lineup |
| DEC-036 | Admin may extend draft timer |
| DEC-037 | Invitation expires after one week |
| DEC-040 | Secondary draft timing is calendar-dependent and normally occurs as soon as practical after EPL window |
| DEC-041 | EPL exits and season-ending injuries qualify for replacement |
| DEC-042 | Player scoring follows official FPL data |
| DEC-043 | All player goals count toward Fantasy Goals For |
| DEC-044 | Own goals affect Goals Against |
| DEC-045 | Penalty goals count normally |
| DEC-046 | Postponed fixtures follow official FPL treatment |
| DEC-047 | Abandoned fixtures follow official FPL scoring treatment |
| DEC-048 | Gameweeks align with official FPL gameweeks |
| DEC-049 | Complete tie-break hierarchy applies to final standings |
| DEC-050 | Positional minimums apply to 15-player weekly roster |
| DEC-051 | All unowned EPL players eligible for secondary draft |
| DEC-052 | Secondary draft expands squad rather than replacing players |
| DEC-053 | League Admin determines season-ending injury |
| DEC-054 | Draft timer = 5 minutes |
| DEC-055 | League Admin determines replacement eligibility |
| DEC-056 | Season goal prediction is stored at beginning of season and used as deterministic tie-break |
| DEC-057 | Weekly roster positional minimums = 1 Goalkeeper / 3 Defenders / 2 Midfielders / 1 Forward; no maximums beyond BR-042 |
| DEC-058 | Tie-break tier four (BR-122) = head-to-head League Points between tied FantasyTeams only; official EPL playoff/away-goals provisions are not implemented |
| DEC-059 | Secondary draft date = day after transfer window closes, rolled forward past any day with scheduled EPL fixtures; League Administrator may override |
| DEC-060 | Draft pick timeout (no administrator extension) = current pick skipped and requeued as a makeup pick after the final regular round |
| DEC-061 | Exactly one League Administrator per League for the initial release; no co-administrator or granular admin roles |
| DEC-062 | Password reset via emailed time-limited single-use link; password strength evaluated by a strength-estimation mechanism; multi-factor authentication not required at launch |
| DEC-063 | No fixed per-season cap on replacement selections; one opportunity per replacement-eligibility event |
| DEC-064 | Double gameweek scoring uses the official FPL combined-fixture total for the gameweek; captain multiplier applies to that combined total |
| DEC-065 | Numeric/positional/date thresholds (squad size, roster size, secondary draft size, draft timer, positional minimums, roster-lock offset, invitation expiration, league point values, secondary-draft scheduling offset, replacement cap) are configurable per League and per Season rather than fixed constants |
| DEC-066 | Official FPL scoring rules (including the captain multiplier), password/MFA policy, and historical retention duration remain fixed, platform-wide values and are explicitly excluded from League/Season configuration |
| DEC-067 | Configurable parameters lock at a defined point in the Season/Draft lifecycle so a mid-process change cannot retroactively alter an already-locked value |
| DEC-068 | A retired user's username becomes available for reuse by any user (including a different individual), since BR-004 uniqueness applies only among active users |
| DEC-069 | If a FantasyTeam has not submitted its Season Goal Prediction by Season start, submission is required (and prompted) before that FantasyTeam's first Gameweek roster can be submitted |
| DEC-070 | A platform-level System Administrator role exists, distinct from the per-league League Administrator role (BR-283) |
| DEC-071 | Retired User accounts may be reactivated only by a System Administrator; there is no self-service reactivation in the initial release |
| DEC-072 | An Initial Draft requires at least two participating FantasyTeams to start |
| DEC-073 | Starting XI boundary ties (11th/12th place) are broken by a deterministic, arbitrary, no-advantage criterion |
| DEC-074 | The Captain's multiplier is applied before Starting XI ranking, so a captained player competes for a scoring-11 spot using their multiplied value |
| DEC-075 | A FantasyTeam that misses a Gameweek roster deadline has its most recent locked roster automatically resubmitted; first-Gameweek non-submission scores zero; a carried-forward player no longer in the squad is dropped from the carried-forward roster |
| DEC-076 | A bye week (odd number of FantasyTeams) produces no match and does not count toward Played/Won/Drawn/Lost |
| DEC-077 | Replacement-selection opportunities never expire once granted |
| DEC-078 | EPL-exit replacement eligibility is granted automatically from official data; season-ending-injury eligibility continues to require League Administrator determination |
| DEC-079 | Gameweek reminder lead time is a League/Season-configurable parameter (default 24 hours before the roster lock deadline), following the same model as BR-290–BR-297 |
| DEC-080 | The draft player pool supports position filtering, name search, and column sorting, and displays each player's season-to-date Minutes Played, Games Played, and Official FPL Points (zero before the Season's first Gameweek is scored) |
| DEC-081 | The Squad View (BR-209) supports the same position filtering, name search, and column sorting as the draft player pool, and additionally displays each player's season-to-date statistics, acquisition type, current-Gameweek roster membership, and replacement-eligibility status |
| DEC-082 | The Administrative Audit Log supports filtering by action type, date range, and affected FantasyTeam (or "League Settings" for non-team-scoped actions), and each entry can expand in place to reveal its full before/after state |
| DEC-083 | A skipped Draft pick (BR-282) is visibly indicated to participants, distinct from a completed selection; the pending makeup-pick queue is displayed once the final regular round completes; and a makeup pick that is itself skipped and re-queued is visibly distinguished from an initial skip |
| DEC-084 | Historical records display the username a User held at the time the record was created, not their current username (resolves BRD §54's "Historical Username Display" open decision) |

---

# 54. Open / Future Decisions

The following areas remain intentionally open for later architecture/product decisions.

## Future Decision — Commercial/Public League Model

Determine how League creation should work if the application becomes publicly available.

Potential future controls may include:

- Authentication requirement
- Account age requirement
- League creation limits
- Private/public league settings
- Moderation
- Abuse prevention
- Paid league options

## Future Decision — Username Change Rules

Determine whether usernames may be changed:

- Anytime
- Once per season
- Once per month
- Only between seasons

The underlying UserId will remain immutable regardless of the selected policy.

## Future Decision — Official EPL Tie-Break Evolution

The application shall periodically verify the official EPL regulations so that the configured tie-break hierarchy can be updated when league rules change.

*(A fourth item, "Historical Username Display," previously listed here, was resolved in Version 1.13 as BR-326 — see Section 70.)*

---

# 55. AIDLC Generation Requirements

The AIDLC process consuming this BRD shall generate, at minimum:

## Architecture

- System architecture
- Component architecture
- Domain architecture
- Data architecture
- API architecture
- Web architecture
- Mobile architecture
- Authentication architecture
- Authorization architecture
- External FPL integration architecture
- Notification architecture
- Audit architecture

## Domain Design

- Entity definitions
- Value objects
- Aggregates
- Domain services
- Application services
- Repositories
- Domain events where appropriate
- State machines where appropriate
- Invariants

## API

- Endpoint definitions
- Request/response models
- Validation
- Authorization rules
- Error handling
- API versioning
- OpenAPI specification

## Database

- Logical schema
- Physical schema
- Primary keys
- Foreign keys
- Unique constraints
- Indexes
- Concurrency strategy
- Audit tables
- Historical data strategy
- Migration scripts

## User Stories

Stories shall be generated from the BR requirements and grouped by:

- Epic
- Feature
- User story
- Acceptance criteria
- Dependencies
- Security considerations
- Technical tasks

## Testing

The AIDLC process shall generate:

- Unit tests
- Integration tests
- API tests
- Database tests
- Authorization tests
- Concurrency tests
- External integration tests
- End-to-end tests
- Mobile UI tests
- Web UI tests

---

# 56. Critical Invariants

The following invariants are considered especially important.

### Invariant 1 — Player Ownership

```text
One Player
+
One League
+
One Season
=
Maximum one FantasyTeam owner
```

### Invariant 2 — FantasyTeam

```text
One LeagueMembership
+
One Season
=
Maximum one FantasyTeam
```

### Invariant 3 — Weekly Roster

```text
Exactly 15 players
```

### Invariant 4 — Captain

```text
Exactly one Captain per submitted weekly roster
```

### Invariant 5 — Captain Membership

```text
Captain ∈ SubmittedRoster
```

### Invariant 6 — Roster Membership

```text
Every roster player ∈ FantasyTeam Squad
```

### Invariant 7 — Initial Squad

```text
Initial Draft = 25 players
```

### Invariant 8 — Secondary Draft

```text
Secondary Draft = 5 additional players
```

### Invariant 9 — No Automatic Substitution

```text
Bench player ≠ automatic replacement
```

### Invariant 10 — Historical Identity

```text
UserId never changes
```

### Invariant 11 — League Isolation

```text
FantasyTeam A in League A
≠
FantasyTeam B in League B
```

even when both belong to the same User.

### Invariant 12 — Override Precedence

```text
Active Administrator Override
>
Official FPL Data
>
Calculated Data
```

### Invariant 13 — Prediction Tie-Break

```text
Minimum ABS(Prediction - Actual)
```

wins.

If the absolute differences are equal:

```text
Prediction <= Actual
```

wins.

---

# 57. Example End-to-End User

A single User named `BillG` participates in two leagues:

```text
User
└── BillG
    └── Default Icon: ⚽

    League A
    ├── League Icon: 🐓
    └── FantasyTeam
        ├── Season: 2026/27
        ├── Squad: 30+
        ├── Weekly Roster: 15
        ├── Captain
        ├── Scores
        └── Head-to-Head Results

    League B
    ├── League Icon: 🦁
    └── FantasyTeam
        ├── Season: 2026/27
        ├── Squad: 30+
        ├── Weekly Roster: 15
        ├── Captain
        ├── Scores
        └── Head-to-Head Results
```

The two FantasyTeams are completely independent despite belonging to the same User.

---

# 58. Architectural Principle for Future Development

The most important structural rule introduced in BRD v1.3 is:

> **User identity is global. Fantasy identity is contextual to League + Season.**

Therefore:

```text
User
    = Who you are

LeagueMembership
    = Your participation in a league

FantasyTeam
    = Your competitive identity within that league and season
```

This separation shall be preserved throughout the generated architecture, database schema, APIs, UI, mobile application, tests, and security model.

---

# 59. BRD Version History

## Version 1.0

Initial business requirements for the Fantasy EPL League Manager.

## Version 1.1

Expanded draft, roster, scoring, league, and administrative requirements.

## Version 1.2

Added:

- Captain functionality
- Captain points
- Complete tie-break hierarchy
- Season goal prediction tie-break
- Secondary draft rules
- Fantasy Goals
- EPL transfer handling
- Postponed/abandoned fixture handling
- Administrative scoring overrides

## Version 1.3

Added and formalized:

- Global User identity
- Username
- Default profile icon
- League-specific icons
- LeagueMembership
- FantasyTeam
- Multiple league participation
- Multiple season participation
- League/FantasyTeam isolation
- Historical identity handling
- User profile requirements
- FantasyTeam-centered domain architecture
- Updated domain entities
- Additional security and authorization requirements
- AIDLC generation requirements
- Critical domain invariants

## Version 1.4

Resolved open decisions carried forward from the AIDLC Domain & Requirements Specification v1.0 and the Architecture and Domain Model v1.0, formalized as BR-279–BR-289 (see §61):

- Weekly roster positional minimums
- Tie-break tier four (EPL-derived criteria) scope
- Secondary draft scheduling algorithm
- Draft pick timeout behavior
- Single League Administrator scope for the initial release
- Password reset and multi-factor authentication policy
- Replacement selection cap
- Double gameweek scoring treatment
- External data source risk acknowledgment

## Version 1.5

Formalized League/Season configurability for numeric, positional, and date/time thresholds previously stated as fixed constants, added as BR-290–BR-297 (see §62):

- General principle and enumerated parameter table (default values, scope, locking point)
- Configuration defaults, locking, and audit rules
- League-level defaults vs. independent Season overrides
- Historical configuration retention
- Explicit exclusions that remain fixed platform-wide values

## Version 1.6

Resolved three items surfaced while writing the Phase 1 feature behavior specifications, added as BR-298–BR-301 (see §63):

- Username availability for reuse after User retirement
- Season Goal Prediction late-submission fallback, enforced at first Gameweek roster submission
- Introduction of a platform-level System Administrator role, distinct from the per-league League Administrator
- Retired User account reactivation authority

## Version 1.7

Resolved seven items surfaced while writing the Phase 2 and Phase 3 feature behavior specifications, added as BR-302–BR-308 (see §64):

- Minimum FantasyTeams required to start an Initial Draft
- Starting XI boundary tie-break rule
- Captain multiplier applied before Starting XI ranking (resolves a genuine scoring-engine ambiguity)
- Roster carry-forward fallback for a missed Gameweek deadline, including the first-Gameweek and squad-change edge cases
- Bye week handling for an odd number of FantasyTeams
- Replacement-selection opportunities never expire
- Automatic (not Administrator-confirmed) EPL-exit replacement eligibility

## Version 1.8

Resolved one item surfaced while writing the Phase 4 feature behavior specifications, added as BR-309 (see §65):

- Gameweek reminder lead time added to the League/Season-configurable parameter set

## Version 1.9

Added four Draft Player Pool UX capabilities identified while reviewing an illustrative HTML mock-up of the Draft Board screen (F-005.5), formalized as BR-310–BR-313 (see §66):

- Position filter (All / Goalkeeper / Defender / Midfielder / Forward)
- Name search
- Sortable columns
- Season-to-date player statistics display (Minutes Played, Games Played, Official FPL Points) — most useful during the Secondary Draft and Replacement selections, and displayed as zero during the Initial Draft

## Version 1.10

Formalized the Squad View (BR-209) to the same standard as the Draft Player Pool, adding BR-314–BR-320 (see §67):

- Position filter, name search, and column sorting, mirroring BR-310–BR-312
- Season-to-date player statistics display, mirroring BR-313
- Acquisition type display (Initial Draft / Secondary Draft / Replacement)
- Current-Gameweek roster membership indicator
- Replacement-eligibility indicator

## Version 1.11

Formalized the Administrative Audit Log Viewer's filtering and detail-expansion behavior, adding BR-321–BR-322 (see §68). This also corrects a pre-existing citation gap: Feature Behavior Specifications (Phase 4) v1.1's F-011.1 had already described action-type/date-range/FantasyTeam filtering in its acceptance criteria, but cited BR-239–BR-242 in support of it — those four rules are actually general application/error-logging and observability requirements (structured logging, error logging, correlation IDs, and the general auditability of competitive-impacting operations) and never established a filtering requirement. BR-321 now provides the filtering rule F-011.1 was actually relying on; BR-322 additionally formalizes the per-entry detail-expansion interaction surfaced while reviewing an illustrative HTML mock-up of the audit log screen.

## Version 1.12

Formalized the visibility requirements around Draft pick timeouts and makeup picks (F-005.4/BR-282), adding BR-323–BR-325 (see §69):

- A skipped pick is visibly indicated to participants, distinct from a completed selection
- The pending makeup-pick queue is displayed once the final regular round completes
- A makeup pick that is itself skipped and re-queued is visibly distinguished from an initial skip

Unlike BR-282 itself, which is a backend behavior rule, these three are UX/display requirements — identified while reviewing an illustrative HTML mock-up of a Draft Timeouts & Makeup Picks screen — and are folded into the Draft UX feature (F-005.5), the same feature BR-310–BR-313 extended.

## Version 1.13

Resolves the "Historical Username Display" open decision (Section 54), adding BR-326 (see §70): finalized historical records display the username a User held at the time the record was created, not their current username — like a sports record book. Historical records continue to resolve by immutable identifier (BR-278) regardless of this choice; only the displayed label changes. This is the first BRD §54 open item resolved since the section was introduced in Version 1.3.

---

# 60. Final Baseline

BRD v1.13 represents the current functional baseline for AIDLC generation.

The implementation should **not** treat the User entity as the owner of fantasy-team data.

The authoritative relationship is:

```text
User
  ↓
LeagueMembership
  ↓
FantasyTeam
  ↓
Season
  ↓
Squad / Rosters / Captains / Scores / Results
```

This structure is the foundation for future stories, use cases, API contracts, database design, automated tests, and application architecture.

---

# 61. Version 1.4 — Resolved Open Decisions

This section formalizes items previously identified as open in the AIDLC Domain & Requirements Specification v1.0 (§24) and the Architecture and Domain Model v1.0 (§15).

## BR-279 — Weekly Roster Positional Minimums

The 15-player weekly roster shall satisfy the following minimum positional counts:

- At least 1 Goalkeeper
- At least 3 Defenders
- At least 2 Midfielders
- At least 1 Forward

Consistent with BR-042, no positional maximum shall apply beyond these minimums and the fixed roster size of 15.

## BR-280 — Tie-Break Tier Four: EPL-Derived Criteria

Where BR-119–BR-121 leave FantasyTeams tied, the next applicable tie-break criterion under BR-122 shall be the cumulative head-to-head League Points earned between the tied FantasyTeams during the Season.

The official EPL away-goals and neutral-venue-playoff provisions do not translate to a private fantasy league and are not implemented. If FantasyTeams remain tied after head-to-head League Points, the hierarchy proceeds to Captain Points (BR-123).

## BR-281 — Secondary Draft Scheduling Algorithm

The secondary draft shall be automatically scheduled for the day immediately following the close of the official EPL transfer window.

If one or more EPL fixtures are scheduled on that day, the scheduled date shall advance one day at a time until a day with no scheduled EPL fixtures is identified.

The League Administrator may override the automatically determined date.

## BR-282 — Draft Pick Timeout

If a FantasyTeam does not make a selection before its draft timer expires and the League Administrator does not extend the timer, that FantasyTeam shall be skipped for the current pick.

The skipped pick shall be queued and appended, in the order skipped, as a makeup pick after the final regularly scheduled round, so that every FantasyTeam completes its required number of selections (BR-052, BR-060, as applicable) before the Draft is marked complete.

## BR-283 — Single League Administrator (Initial Release)

Each League shall have exactly one League Administrator during the initial release.

Delegated co-administrator roles and granular administrative permission subsets are non-goals for the initial release.

## BR-284 — Password Reset

Users shall be able to reset a forgotten password using a time-limited, single-use reset link delivered to their registered email address.

## BR-285 — Password Strength Policy

Password strength shall be evaluated using a strength-estimation mechanism rather than fixed composition rules (e.g., mandatory symbol/number counts), with a minimum effective length of twelve characters.

## BR-286 — Multi-Factor Authentication (Initial Release)

Multi-factor authentication shall not be required for the initial release.

## BR-287 — Replacement Selection Cap

There shall be no fixed per-season cap on the number of replacement selections available to a FantasyTeam.

Each player who becomes replacement-eligible (BR-065) shall generate exactly one replacement-selection opportunity for the owning FantasyTeam.

## BR-288 — Double Gameweek Scoring

When official FPL data records more than one fixture for a player within a single application Gameweek ("double gameweek"), the player's official FPL fantasy points for that Gameweek shall reflect the combined total across all such fixtures, consistent with official FPL treatment.

The Captain multiplier (BR-047) shall apply to that combined total.

## BR-289 — External Data Source Risk Acknowledgment

The official FPL data source used to satisfy BR-227 is a publicly accessible but unofficial and undocumented interface, with no published terms of use, rate limits, or availability guarantee.

Its terms of use shall be periodically reverified, and the application shall degrade gracefully (BR-233) if the source becomes unavailable or changes without notice.

---

# 63. Version 1.6 — Resolved Items from Phase 1 Feature Specification

This section resolves three items raised while writing the Phase 1 (Foundation) feature behavior specifications.

## BR-298 — Username Availability After Retirement

A username held by a retired User (BR-013) shall become available for use by any User, including a different individual, immediately upon that retirement. This is a clarification of BR-004's existing scope ("unique among active users"), not a new constraint: retired users are not counted in the uniqueness check.

A User rejoining the application after retirement (e.g., participating in a League one Season, not the next, then rejoining a later Season) has no special claim to their prior username if it has since been taken by another User.

## BR-299 — Season Goal Prediction Late-Submission Fallback

If a FantasyTeam has not submitted its Season Goal Prediction by the start of the Season (BR-127), the FantasyTeam's owner shall be prompted to submit it at the time they attempt to submit their first Gameweek roster for that Season.

The application shall not accept that first Gameweek roster submission until the Season Goal Prediction has been provided.

Once submitted under this fallback, the prediction shall lock immediately (consistent with BR-128) and shall be treated identically to a prediction submitted at Season start for all subsequent tie-break purposes (BR-131–BR-135).

## BR-300 — System Administrator Role

A platform-level System Administrator role shall exist, distinct from the per-League League Administrator role (BR-283). The System Administrator role is not tied to any specific League and is not exposed to League members as part of normal league-facing functionality.

## BR-301 — Retired User Account Reactivation

A retired User account may be reactivated only by a System Administrator. Self-service reactivation by the affected User is not supported in the initial release.

If the User's prior username is unavailable at the time of reactivation (BR-298), the System Administrator shall assign, or prompt for, a new username as part of the reactivation.

---

# 64. Version 1.7 — Resolved Items from Phase 2 and Phase 3 Feature Specification

This section resolves seven items raised while writing the Phase 2 (Draft & Squad) and Phase 3 (Weekly Gameplay) feature behavior specifications.

## BR-302 — Minimum FantasyTeams for Initial Draft

An Initial Draft shall not be permitted to start with fewer than two participating FantasyTeams.

## BR-303 — Starting XI Boundary Tie-Break

When two or more players are tied in points at the boundary between the 11th and 12th highest-scoring submitted roster player, the tie shall be broken using a deterministic, arbitrary criterion (e.g., a stable Player identifier) that confers no competitive advantage, so that exactly 11 players are selected for the Starting XI (BR-044).

## BR-304 — Captain Multiplier Applied Before Starting XI Ranking

The Captain's official FPL point value shall be multiplied by the applicable captain multiplier (BR-047) **before** the Starting XI is determined (BR-044). The Captain therefore competes for inclusion in the Starting XI using their multiplied point value, rather than being ranked on an unmultiplied value with the multiplier applied only afterward.

## BR-305 — Roster Carry-Forward Fallback

If a FantasyTeam does not submit a Gameweek roster before that Gameweek's lock deadline, the FantasyTeam's most recently locked Gameweek roster — including its Captain selection — shall automatically be resubmitted as that Gameweek's roster at the lock deadline. This is an automatic submission occurring at the deadline, not a change after locking (consistent with BR-094/BR-096).

If the FantasyTeam has no prior-Gameweek roster for the Season (i.e., this is that FantasyTeam's first Gameweek), its roster for that Gameweek shall be considered empty and its Fantasy Points for that Gameweek shall be zero.

If a player included in the carried-forward roster is no longer part of the FantasyTeam's current squad (e.g., released via a replacement selection), that player shall be omitted from the carried-forward roster. The resulting roster shall be scored as-is even if it no longer satisfies the roster size or positional minimums that would be required of a manually submitted roster, since this is an automatic system continuation rather than a new user submission.

## BR-306 — Bye Week Handling

In a Season with an odd number of FantasyTeams, the FantasyTeam without an opponent in a given Gameweek (a "bye") shall have no Head-to-Head match for that Gameweek. That Gameweek shall not count toward the FantasyTeam's Played, Won, Drawn, or Lost totals.

## BR-307 — Replacement Selection Opportunity Persistence

A replacement-selection opportunity (BR-063–BR-068) shall remain available to the owning FantasyTeam indefinitely until used. It shall not expire.

## BR-308 — Automatic EPL-Exit Replacement Eligibility

A fantasy-owned player confirmed by official data to have transferred out of the EPL (BR-066) shall become replacement-eligible automatically, without requiring League Administrator confirmation.

This is distinct from a season-ending injury determination (BR-067, BR-068), which continues to require League Administrator judgment informed by league-user consensus.

---

# 65. Version 1.8 — Resolved Item from Phase 4 Feature Specification

## BR-309 — Gameweek Reminder Lead Time Configurability

The lead time before a Gameweek's roster lock deadline at which a Gameweek Reminder notification (BR-152) is triggered shall be a League/Season-configurable parameter, following the same default/override/audit/locking model established in BR-290–BR-297.

The application default shall be 24 hours before the roster lock deadline.

---

# 62. Version 1.5 — Configurable League/Season Parameters

Numerous business rules in this document state a specific number, position count, or duration (e.g., "25 players," "five minutes," "one hour before kickoff"). This section establishes that those figures are **application defaults**, not fixed constants: each League may configure its own values, and each Season within a League may independently override the League's defaults.

## BR-290 — Configurable League/Season Parameters (Principle)

Numeric, positional, and time-based thresholds identified in the table under BR-291 shall be configurable per League and, within a League, independently overridable per Season, rather than fixed system-wide constants.

## BR-291 — Configurable Parameter Table

| Parameter | Originating Rule(s) | Default | Locks At |
|---|---|---|---|
| Initial squad size | BR-034, BR-052, BR-197 | 25 | Start of the Initial Draft |
| Weekly roster size | BR-037, BR-196 | 15 | Start of the Season |
| Weekly roster positional minimums | BR-279 | 1 GK / 3 DEF / 2 MID / 1 FWD | Start of the Season |
| Draft pick timer | BR-057 | 5 minutes | Start of the applicable Draft (Initial/Secondary/Replacement may be configured independently; absent an independent value, all draft types use the same timer) |
| Secondary draft selections per FantasyTeam | BR-060 | 5 | Start of the Secondary Draft |
| Secondary draft scheduling offset (days after transfer window close before rolling forward past fixture days, per BR-281) | BR-069, BR-281 | 1 day | Start of the Season |
| Gameweek roster lock offset before kickoff | BR-093 | 1 hour | Start of the Season |
| League points: Win / Draw / Loss | BR-115, BR-116, BR-117 | 3 / 1 / 0 | Start of the Season |
| Invitation expiration | BR-029 | 7 days | Applies prospectively only — see BR-293 |
| Replacement selection cap | BR-287 | None (uncapped) | Start of the Season |

The standings tie-break ruleset (BR-122) is already required to be configurable rather than hard-coded; it follows the same League-default/Season-override/lock-at-Season-start pattern described in this section.

## BR-292 — Configuration Defaults

Each configurable parameter shall have an application-defined default matching the value stated in the table under BR-291. A League or Season that does not explicitly override a parameter shall use that default.

## BR-293 — Season Configuration Snapshot and Locking

Each Season shall capture its own effective configuration values no later than the "Locks At" point specified in BR-291 for each parameter. Once a phase has locked its dependent configuration, subsequent changes to a League's stored defaults or to that Season's configuration shall not retroactively alter the values already locked for that Season.

Invitation expiration is the one parameter without a lifecycle-phase lock: a change to it applies only to invitations issued after the change. An invitation already issued retains the expiration duration that was in effect at the time it was issued.

## BR-294 — League-Level Defaults Independent of Season Overrides

A Season-specific configuration override shall not change the League's stored default, and shall not affect any other Season of the same League. This is consistent with BR-032 (a League may be reused for a subsequent Season) — different Seasons of the same League may run under different configured values.

## BR-295 — Configuration Change Audit

Changes to League or Season configuration shall be audited consistent with BR-149/AP-005, recording the parameter changed, its prior value, its new value, the administrator responsible, and the timestamp of the change.

## BR-296 — Historical Configuration Retention

The effective configuration for a completed Season shall be retained indefinitely alongside that Season's historical results, consistent with BR-174, so that historical squads, drafts, rosters, and standings remain interpretable under the rules that actually applied to that Season.

## BR-297 — Non-Configurable Exclusions

The following remain fixed, platform-wide values and are explicitly excluded from League/Season configuration:

- Official FPL scoring, including the Captain multiplier (BR-047), which shall always follow official FPL rules rather than an application- or League-defined value.
- Password policy and multi-factor authentication requirements (BR-284–BR-286), which are platform-wide security settings, not competitive/league rules.
- Historical data retention duration (BR-174), which is a platform-wide compliance policy.

---

# 66. Version 1.9 — Resolved Items from Draft Board Mock-Up Review

This section formalizes four Draft Player Pool capabilities identified while reviewing an illustrative HTML mock-up built against the Draft User Experience requirements (Section 37, BR-204–BR-208; feature F-005.5). None of these contradict a prior rule — they were simply not yet stated as explicit business rules.

## BR-310 — Draft Player Pool Position Filter

The application shall allow the draft player pool to be filtered to a single position (Goalkeeper, Defender, Midfielder, or Forward) or to an unfiltered "All" view, so a User can narrow the pool to the position they intend to select next.

## BR-311 — Draft Player Pool Search

The application shall allow the draft player pool to be searched by player name, so a User can quickly locate a specific player rather than browsing the full pool.

## BR-312 — Draft Player Pool Sorting

The application shall allow the draft player pool to be sorted by any displayed column, including player name, club, position, and the statistics required by BR-313, so a User can rank available players by the criterion most relevant to their decision.

## BR-313 — Draft Player Pool Statistics Display

The draft player pool shall display each player's season-to-date Minutes Played, Games Played, and Official FPL Fantasy Points, sourced from the same official FPL data used for scoring (BR-075, BR-076).

These statistics are most valuable during the Secondary Draft and Replacement selections (Section 12), when meaningful season-to-date statistics already exist for most players. Because the player pool, its filtering (BR-310), search (BR-311), and sorting (BR-312) are shared across all Draft types, these same columns shall also appear during the Initial Draft; before a Season's first Gameweek has been scored, all three statistics shall display as zero for every player rather than being hidden or omitted.

These four rules apply identically to the Initial Draft and to the Secondary/Replacement Draft, since both present the player pool through the same shared Draft UX (F-005.5).

---

# 67. Version 1.10 — Resolved Items from Squad View Mock-Up Review

This section formalizes the Squad View (BR-209, Section 38; feature F-007.5) to the same standard established for the Draft Player Pool in Section 66, following the same review process: building an illustrative HTML mock-up of the Squad View surfaced capabilities the existing BR-209 implied but had not yet stated explicitly.

## BR-314 — Squad View Position Filter

The application shall allow the Squad View to be filtered to a single position (Goalkeeper, Defender, Midfielder, or Forward) or to an unfiltered "All" view, mirroring BR-310's Draft Player Pool position filter.

## BR-315 — Squad View Search

The application shall allow the Squad View to be searched by player name, mirroring BR-311's Draft Player Pool search.

## BR-316 — Squad View Sorting

The application shall allow the Squad View to be sorted by any displayed column, including player name, club, position, and the statistics required by BR-317, mirroring BR-312's Draft Player Pool sorting.

## BR-317 — Squad View Statistics Display

The Squad View shall display each player's season-to-date Minutes Played, Games Played, and Official FPL Fantasy Points, sourced from the same data used to satisfy BR-313's Draft Player Pool statistics display. As with the Draft Player Pool, these figures display as zero before a Season's first Gameweek has been scored, rather than being hidden.

## BR-318 — Squad View Acquisition Type Display

The Squad View shall display how each player was acquired (Initial Draft, Secondary Draft, or Replacement), consistent with the acquisition history already required to be retained by BR-264. This is a display requirement for data BR-264 already requires the application to track — it does not introduce any new data to be captured.

## BR-319 — Squad View Current-Gameweek Roster Indicator

The Squad View shall indicate, for each squad player, whether that player is included in the FantasyTeam's current Gameweek roster, so a User can distinguish their currently-rostered players from the rest of their squad without switching screens.

## BR-320 — Squad View Replacement-Eligibility Indicator

The Squad View shall indicate which squad players are currently replacement-eligible (BR-065), so a User can see at a glance which unused replacement-selection opportunities (BR-307) are available to them, and which specific player triggered each one.

These seven rules apply to the single Squad View screen (BR-209), which is not itself Gameweek-specific — a FantasyTeam has one Squad View, not one per Gameweek — though BR-319's roster indicator is necessarily evaluated against whichever Gameweek is currently open for roster submission.

---

# 68. Version 1.11 — Resolved Items from Audit Log Mock-Up Review

This section formalizes the Administrative Audit Log Viewer (BR-177–BR-182, Section 30; feature F-011.1) to the same standard established for the Draft Player Pool (Section 66) and the Squad View (Section 67), following the same review process: building an illustrative HTML mock-up of the audit log surfaced capabilities the existing feature description implied but no BR actually established.

Unlike the two prior rounds, this one also closes a citation gap rather than a pure feature gap: Feature Behavior Specifications (Phase 4) v1.1's F-011.1 already described filtering by action type, date range, and affected FantasyTeam in its acceptance criteria — but supported that description with BR-239–BR-242, which are general application/error-logging and observability rules (structured logging, error logging, correlation IDs, and competitive-impacting operations being "separately auditable" in principle) and do not themselves establish any filtering requirement. BR-321 below is the rule that acceptance criterion was actually relying on.

## BR-321 — Audit Log Filtering

The Administrative Audit Log shall support filtering entries by action type, by a date range, and by the affected FantasyTeam — or, for an action not scoped to a specific FantasyTeam (e.g., a League/Season configuration change), by "League Settings."

## BR-322 — Audit Log Entry Detail Expansion

Each Administrative Audit Log entry shall support expanding, in place within the chronological list, to reveal its full recorded before/after state, without requiring navigation away from the list. This refines, rather than replaces, the existing requirement that before/after state be shown (BR-177–BR-182, BR-149): a summarized description of the change is always visible in the list; the complete recorded values are available on demand via this expansion.

---

# 69. Version 1.12 — Resolved Items from Draft Timeouts & Makeup Picks Mock-Up Review

This section formalizes the visibility requirements around Draft pick timeouts and makeup picks (BR-282, Section 61; feature F-005.4), following the same review process as Sections 66–68: building an illustrative HTML mock-up of a dedicated Draft Timeouts & Makeup Picks screen surfaced capabilities BR-282 required to happen, but never required to be *visible* to Draft participants.

Unlike BR-282 itself — a backend behavior rule governing the `Draft` aggregate — these three rules are UX/display requirements, and belong to the Draft UX feature (F-005.5) rather than to F-005.4, the same feature BR-310–BR-313 (Section 66) extended.

## BR-323 — Skipped Pick Visibility

When a FantasyTeam's pick is skipped due to timeout (BR-282), the application shall visibly indicate the skip to Draft participants, distinct from a completed selection. Without this, a skip is otherwise invisible to other participants — BR-207's existing requirement to view "completed selections" does not cover a pick that was explicitly *not* completed.

## BR-324 — Pending Makeup Pick Queue Display

The application shall display the current queue of pending makeup picks, including which FantasyTeams are waiting for a makeup turn and the order in which they will pick, once the final regularly scheduled round has completed.

## BR-325 — Makeup Pick Re-Queue Visibility

If a makeup pick is itself skipped and re-queued (BR-282), the application shall visibly indicate the re-queue event, distinct from an initial skip — so participants can tell "skipped for the first time" apart from "skipped again, now at the back of the queue."

---

# 70. Version 1.13 — Resolved: Historical Username Display

Section 54 ("Open / Future Decisions") has carried an unresolved item since Version 1.3: whether finalized historical records should display the username a User held at the time of the event, or their current username. Reviewing an illustrative HTML mock-up built specifically to compare the two options — using a concrete example of a manager who renamed mid-career — made the tradeoff concrete enough to decide rather than continue deferring it.

## BR-326 — Historical Username Display Policy

Finalized historical records (standings, drafts, rosters, head-to-head results, and other competition history) shall display the username the User held at the time the record was created, not the User's current username.

Historical records shall continue to resolve by the User's immutable identifier (BR-278) regardless of subsequent username changes; only the displayed label is affected by this policy, never which User a record belongs to.

**Rationale:** this application closely mirrors EPL/FPL concepts (BR-076) and explicitly models itself on sports record-keeping (Section 39, Section 48's historical-season acceptance criteria) — in that convention, a competitor's record shows the name they competed under, not a name they adopted afterward. The alternative (always showing the current username) is simpler to implement, since it requires no snapshot mechanism, but it would mean old standings, drafts, and results visually change every time a user renames — for a ten-year retention window (BR-174), that instability was judged to outweigh the implementation simplicity.

This decision does not alter F-001.3's existing rule that a username change never touches `UserId` (BR-270), nor F-013.2's rule that retired users' history must still resolve correctly (BR-014/BR-175/BR-272) — those invariants hold regardless of which display policy was chosen. What changes is purely which label a historical screen renders for a given historical record.