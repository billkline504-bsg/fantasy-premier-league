# API Endpoint Reference

An onboarding-friendly index of every HTTP endpoint currently implemented in `EplFantasy.Api`, generated directly from the controllers in [`src/EplFantasy.Api/Controllers/`](src/EplFantasy.Api/Controllers/) — so it reflects what's actually built and runnable today, not just what's designed. For the authoritative, contract-level description of each endpoint (request/response schemas, error codes, examples), see the [OpenAPI Specification v1.0](docs/aidlc/05-api-specification/Fantasy%20EPL%20League%20Manager%20—%20OpenAPI%20Specification%20v1.0.yaml). There is no Swagger/OpenAPI UI wired up in the running app (no `/swagger` route) — this document and the YAML spec are the two ways to browse the surface without reading controller source directly.

## Conventions

- **Base path:** every route is versioned under `/api/v1/...` (Architecture §9.1's URL-path versioning convention).
- **Auth:** all endpoints require a JSON Web Token bearer token (`Authorization: Bearer <token>`) obtained from `POST /api/v1/auth/login`, **except** the handful explicitly marked *Anonymous* below (registration/login itself, password reset, invitation acceptance's own token-based flow's outer shell, and the public profile icon catalog).
- **Authorization policies:** most endpoints layer an *object-level* policy on top of "is this a valid token" — e.g. `ActiveLeagueMember` (caller belongs to the League named by the route), `LeagueAdministrator` (caller administers it), `FantasyTeamOwner` (caller owns that specific FantasyTeam). These are the policy names referenced in the tables below; see [`src/EplFantasy.Infrastructure/Authorization/`](src/EplFantasy.Infrastructure/Authorization/) for each handler's actual rule.
- **Rate limiting:** every `/api/v1/auth/*` endpoint additionally carries the `"auth"` rate-limiting policy (BR-169) — repeated failures are throttled and logged as a `security_event`.
- **Errors:** every error response is a `ProblemDetails` body carrying an `errorCode` and `correlationId` (no bare 500s/404s without a body).
- **Health check:** `GET /api/v1/health` → `{ "status": "ok" }` — anonymous, no auth required. The fastest way to confirm the API is actually running against a working database connection.

---

## Identity & User

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/api/v1/auth/register` | Anonymous | Create a new user account. |
| POST | `/api/v1/auth/login` | Anonymous | Authenticate with username/password; returns an access token + refresh token. |
| POST | `/api/v1/auth/refresh` | Anonymous | Exchange a valid refresh token for a new access/refresh token pair (rotates the refresh token). |
| POST | `/api/v1/auth/logout` | Authenticated | Revoke the caller's current refresh token. |
| POST | `/api/v1/auth/password-reset/request` | Anonymous | Request a password reset link for an email (always responds the same way, whether or not the email matches an account). |
| POST | `/api/v1/auth/password-reset/confirm` | Anonymous | Complete a password reset using the token from the request step. |
| GET | `/api/v1/users/me` | Authenticated | Get the caller's own user profile. |
| PUT | `/api/v1/users/me` | Authenticated | Change the caller's own username. |
| POST | `/api/v1/users/me/retire` | Authenticated | Soft-delete (retire) the caller's own account; username becomes reusable. |
| PUT | `/api/v1/users/me/icon` | Authenticated | Set the caller's platform-wide default profile icon. |
| GET | `/api/v1/profile-icons` | Anonymous | List the catalog of available profile icons. |

## League & Season

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/api/v1/leagues` | Authenticated | Create a new League (caller becomes its founding Administrator). |
| GET | `/api/v1/leagues` | Authenticated | List the caller's own League memberships. |
| GET | `/api/v1/leagues/{leagueId}` | ActiveLeagueMember | Get a League's details. |
| PUT | `/api/v1/leagues/{leagueId}` | LeagueAdministrator | Update a League's details. |
| POST | `/api/v1/leagues/{leagueId}/invitations` | LeagueAdministrator | Invite a user to the League. |
| GET | `/api/v1/leagues/{leagueId}/invitations` | LeagueAdministrator | List a League's outstanding invitations. |
| DELETE | `/api/v1/leagues/{leagueId}/invitations/{invitationId}` | LeagueAdministrator | Revoke an invitation. |
| POST | `/api/v1/invitations/{token}/accept` | Authenticated | Accept an invitation by its unguessable token (the token itself, not a membership check, is this endpoint's real security boundary). |
| GET | `/api/v1/leagues/{leagueId}/memberships` | ActiveLeagueMember | List a League's memberships. |
| GET | `/api/v1/leagues/{leagueId}/memberships/{membershipId}` | ActiveLeagueMember | Get one membership. |
| POST | `/api/v1/leagues/{leagueId}/memberships/{membershipId}/leave` | MembershipOwnerOrLeagueAdministrator | Leave a League (self, or an Administrator removing another member). |
| PUT | `/api/v1/leagues/{leagueId}/memberships/{membershipId}/icon` | MembershipOwner | Set a League-specific profile icon override. |
| DELETE | `/api/v1/leagues/{leagueId}/memberships/{membershipId}/icon` | MembershipOwner | Clear the League-specific icon override (falls back to the platform-wide default). |
| GET | `/api/v1/leagues/{leagueId}/memberships/{membershipId}/notification-preferences` | MembershipOwner | Get this membership's notification preferences. |
| PUT | `/api/v1/leagues/{leagueId}/memberships/{membershipId}/notification-preferences` | MembershipOwner | Update this membership's notification preferences. |
| POST | `/api/v1/leagues/{leagueId}/seasons` | LeagueAdministrator | Create a new Season for a League. |
| GET | `/api/v1/leagues/{leagueId}/seasons` | ActiveLeagueMember | List a League's Seasons (optionally filtered by `status`). |
| GET | `/api/v1/leagues/{leagueId}/seasons/{seasonId}` | ActiveLeagueMember | Get one Season. |
| GET | `/api/v1/leagues/{leagueId}/configuration` | ActiveLeagueMember | Get a League's default configuration values. |
| PUT | `/api/v1/leagues/{leagueId}/configuration` | LeagueAdministrator | Update a League's default configuration values. |
| GET | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/configuration` | ActiveLeagueMember | Get a Season's own (possibly overridden, possibly field-locked) configuration. |
| PUT | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/configuration` | LeagueAdministrator | Update a Season's configuration (rejected whole if any changed field is locked). |
| GET | `/api/v1/leagues/{leagueId}/messages` | ActiveLeagueMember | List a League's announcement messages. |
| POST | `/api/v1/leagues/{leagueId}/messages` | LeagueAdministrator | Post a new League announcement message. |
| GET | `/api/v1/leagues/{leagueId}/audit` | LeagueAdministrator | Paginated administrative-action audit log for a League. |

## Fantasy Team

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams` | ActiveLeagueMember | Create the caller's FantasyTeam for a Season. |
| GET | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams` | ActiveLeagueMember | List every FantasyTeam in a Season. |
| GET | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}` | ActiveLeagueMember | Get one FantasyTeam. |
| GET | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/squad` | FantasyTeamOwner | Get a FantasyTeam's full squad, including released-player history (filterable/sortable by position/search/sort). |
| POST | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/squad-players/{squadPlayerId}/declare-season-ending-injury` | LeagueAdministrator | Mark a squad player as season-ending-injured, granting a replacement opportunity. |
| GET | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/replacement-opportunities` | FantasyTeamOwnerOrLeagueAdministrator | List a FantasyTeam's unspent replacement opportunities. |
| POST | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/replacement-opportunities/{replacementOpportunityId}/spend` | FantasyTeamOwner | Spend a replacement opportunity to pick a new player. |
| GET | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction` | FantasyTeamOwnerOrActiveLeagueMember | Get a FantasyTeam's season goal prediction. |
| PUT | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction` | FantasyTeamOwner | Submit/update a FantasyTeam's season goal prediction. |

## Draft Management

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts` | LeagueAdministrator | Start a Draft (only `draftType: Initial` is supported today). |
| GET | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts` | ActiveLeagueMember | List a Season's Drafts (optionally filtered by `draftType`). |
| GET | `/api/v1/drafts/{draftId}` | DraftLeagueMember | Get a Draft's current state (turn, timer, pending makeup picks, etc.). |
| POST | `/api/v1/drafts/{draftId}/picks` | DraftTurnOwner | Make the current pick (only the FantasyTeam currently on the clock may call this). |
| POST | `/api/v1/drafts/{draftId}/timer/extend` | DraftLeagueAdministrator | Extend the current pick's timer deadline. |
| GET | `/api/v1/drafts/{draftId}/selections` | DraftLeagueMember | Paginated list of picks made so far, in order. |
| GET | `/api/v1/drafts/{draftId}/player-pool` | DraftLeagueMember | List undrafted players still available (filterable/sortable by position/search/sort). |

## Roster Management

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster` | FantasyTeamOwnerOrActiveLeagueMember | Get a FantasyTeam's roster for a Gameweek. |
| PUT | `/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster` | FantasyTeamOwner | Submit/replace a FantasyTeam's roster for a Gameweek (rejected once locked). |
| PUT | `/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster/captain` | FantasyTeamOwner | Change the roster's designated Captain without touching the rest of the roster. |
| POST | `/api/v1/admin/rosters/{gameweekRosterId}/correct` | RosterLeagueAdministrator | Administrator correction of an already-locked/scored roster's players and/or Captain. |

## Scoring

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/score` | FantasyTeamLeagueMember | Get a FantasyTeam's calculated score for a Gameweek. |
| POST | `/api/v1/admin/score-overrides` | Authenticated (League Administrator enforced in-service) | Create a manual score override for a player's Gameweek performance. |
| POST | `/api/v1/admin/score-overrides/{scoreOverrideId}/undo` | ScoreOverrideLeagueAdministrator | Undo (deactivate) an existing score override. |

## Competition

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/schedule` | ActiveLeagueMember | Get the head-to-head match schedule (optionally filtered to one Gameweek). |
| GET | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/matches/{matchId}` | ActiveLeagueMember | Get one head-to-head match's result. |
| GET | `/api/v1/leagues/{leagueId}/seasons/{seasonId}/standings` | ActiveLeagueMember | Get the League's current (or as-of-a-past-Gameweek) standings table. |

## Player & EPL Reference Data

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/v1/epl/clubs` | Authenticated | List all EPL clubs. |
| GET | `/api/v1/epl/players` | Authenticated | List EPL players (filterable by `clubId`, `position`, `search`). |
| GET | `/api/v1/epl/players/{playerId}` | Authenticated | Get one EPL player. |
| GET | `/api/v1/epl/gameweeks?eplSeasonIdentifier=` | Authenticated | List Gameweeks for a given EPL season. |
| GET | `/api/v1/epl/gameweeks/{gameweekId}/fixtures` | Authenticated | List a Gameweek's fixtures. |
| GET | `/api/v1/epl/seasons/{eplSeasonId}/table` | Authenticated | Get the real-world EPL league table for a season. |

*(This data is populated by [`tools/EplFantasy.SeedTool`](tools/EplFantasy.SeedTool/) or a future scheduled sync — see the README's "Local database setup" section — not by any endpoint in this table; the whole "Player & EPL Reference Data" surface is read-only by design.)*

## System Administration

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/api/v1/admin/security/rate-limits` | SystemAdministrator | View the current rate-limiting configuration. |
| GET | `/api/v1/admin/security/events` | SystemAdministrator | Paginated feed of recorded security events (rate-limit blocks, etc.). |
| GET | `/api/v1/admin/security/csrf-status` | SystemAdministrator | Report whether cookie auth / CSRF middleware is active (informational; this API is bearer-JWT only today). |

---

**Total: 71 endpoints** across 23 controllers, plus the anonymous health check. Counts will drift as new feature tasks land — regenerate this table from `src/EplFantasy.Api/Controllers/` rather than trusting it blindly once the codebase has moved on.
