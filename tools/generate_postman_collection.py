"""
Regenerates EplFantasy.postman_collection.json (repo root) from the endpoint table below.

The endpoint list here is hand-maintained to mirror src/EplFantasy.Api/Controllers/ and
API_ENDPOINTS.md -- there is no reflection over the running app. When a controller gains, loses,
or reshapes an endpoint, update the corresponding entry in FOLDERS and re-run:

    python tools/generate_postman_collection.py

from the repo root (it writes EplFantasy.postman_collection.json next to itself's parent).
"""

import json
import os
import uuid

def uid():
    return str(uuid.uuid4())

# ---------------------------------------------------------------------------
# Endpoint data: (name, method, path, auth, body, query, description)
#   auth: "bearer" (default, inherits collection auth) or "noauth"
#   body: dict (JSON) or None
#   query: list of (key, value, disabled) or None
# path uses {placeholder} which is converted to {{placeholder}} for Postman.
# ---------------------------------------------------------------------------

FOLDERS = [
    ("Identity & User", [
        ("Register", "POST", "/api/v1/auth/register", "noauth",
         {"username": "jsmith", "email": "jsmith@example.com", "password": "Str0ngPassw0rd!"},
         None, "Create a new user account.", "auth"),
        ("Login", "POST", "/api/v1/auth/login", "noauth",
         {"usernameOrEmail": "jsmith", "password": "Str0ngPassw0rd!"},
         None, "Authenticate with username/password; returns an access token + refresh token.", "auth"),
        ("Refresh Token", "POST", "/api/v1/auth/refresh", "noauth",
         {"refreshToken": "{{refreshToken}}"},
         None, "Exchange a valid refresh token for a new access/refresh token pair (rotates the refresh token).", "auth"),
        ("Logout", "POST", "/api/v1/auth/logout", "bearer",
         {"refreshToken": "{{refreshToken}}"},
         None, "Revoke the caller's current refresh token.", "auth"),
        ("Request Password Reset", "POST", "/api/v1/auth/password-reset/request", "noauth",
         {"email": "jsmith@example.com"},
         None, "Request a password reset link for an email (always responds the same way, whether or not the email matches an account).", "auth"),
        ("Confirm Password Reset", "POST", "/api/v1/auth/password-reset/confirm", "noauth",
         {"resetToken": "{{resetToken}}", "newPassword": "N3wStr0ngPassw0rd!"},
         None, "Complete a password reset using the token from the request step.", "auth"),
        ("Get Current User", "GET", "/api/v1/users/me", "bearer", None, None,
         "Get the caller's own user profile.", "auth"),
        ("Update Username", "PUT", "/api/v1/users/me", "bearer",
         {"username": "jsmith2"}, None, "Change the caller's own username.", "auth"),
        ("Update Default Profile Icon", "PUT", "/api/v1/users/me/icon", "bearer",
         {"profileIconId": "{{profileIconId}}"}, None,
         "Set the caller's platform-wide default profile icon.", "auth"),
        ("List Profile Icons", "GET", "/api/v1/profile-icons", "noauth", None, None,
         "List the catalog of available profile icons.", "auth"),
    ]),
    ("League & Season", [
        ("Create League", "POST", "/api/v1/leagues", "bearer",
         {"name": "The Gaffers League", "description": "A friendly league among coworkers."}, None,
         "Create a new League (caller becomes its founding Administrator).", None),
        ("List My Leagues", "GET", "/api/v1/leagues", "bearer", None, None,
         "List the caller's own League memberships.", None),
        ("Get League", "GET", "/api/v1/leagues/{leagueId}", "bearer", None, None,
         "Get a League's details.", None),
        ("Update League", "PUT", "/api/v1/leagues/{leagueId}", "bearer",
         {"name": "The Gaffers League", "description": "Updated description.", "status": "Active"}, None,
         "Update a League's details.", None),
        ("Create Invitation", "POST", "/api/v1/leagues/{leagueId}/invitations", "bearer",
         {"destination": "invitee@example.com", "channel": "Email"}, None,
         "Invite a user to the League. `seasonId` is optional (omitted here) -- add it back to scope the invitation to one Season instead of the League as a whole.", None),
        ("List Invitations", "GET", "/api/v1/leagues/{leagueId}/invitations", "bearer", None, None,
         "List a League's outstanding invitations.", None),
        ("Revoke Invitation", "DELETE", "/api/v1/leagues/{leagueId}/invitations/{invitationId}", "bearer",
         None, None, "Revoke an invitation.", None),
        ("Accept Invitation", "POST", "/api/v1/invitations/{invitationToken}/accept", "bearer",
         None, None, "Accept an invitation by its unguessable token (the token itself, not a membership check, is this endpoint's real security boundary). No endpoint returns this token -- it's delivered out-of-band (email/SMS); set {{invitationToken}} manually from wherever your environment surfaces it (e.g. a dev-only log line or direct DB read).", None),
        ("List Memberships", "GET", "/api/v1/leagues/{leagueId}/memberships", "bearer", None, None,
         "List a League's memberships.", None),
        ("Get Membership", "GET", "/api/v1/leagues/{leagueId}/memberships/{membershipId}", "bearer", None, None,
         "Get one membership.", None),
        ("Leave League", "POST", "/api/v1/leagues/{leagueId}/memberships/{membershipId}/leave", "bearer",
         None, None, "Leave a League (self, or an Administrator removing another member).", None),
        ("Set League Icon Override", "PUT", "/api/v1/leagues/{leagueId}/memberships/{membershipId}/icon", "bearer",
         {"profileIconId": "{{profileIconId}}"}, None,
         "Set a League-specific profile icon override.", None),
        ("Clear League Icon Override", "DELETE", "/api/v1/leagues/{leagueId}/memberships/{membershipId}/icon", "bearer",
         None, None, "Clear the League-specific icon override (falls back to the platform-wide default).", None),
        ("Get Notification Preferences", "GET", "/api/v1/leagues/{leagueId}/memberships/{membershipId}/notification-preferences", "bearer",
         None, None, "Get this membership's notification preferences.", None),
        ("Update Notification Preferences", "PUT", "/api/v1/leagues/{leagueId}/memberships/{membershipId}/notification-preferences", "bearer",
         [{"eventType": "WeeklyScore", "channel": "Email", "enabled": True},
          {"eventType": "WeeklyStandings", "channel": "InApp", "enabled": True}], None,
         "Update this membership's notification preferences (array of {eventType, channel, enabled}).", None),
        ("Create Season", "POST", "/api/v1/leagues/{leagueId}/seasons", "bearer",
         {"eplSeasonIdentifier": "{{eplSeasonIdentifier}}", "startDate": "2026-08-15"}, None,
         "Create a new Season for a League. eplSeasonIdentifier must already exist as an epl_seasons row (seeded via tools/EplFantasy.SeedTool, or your own sync) -- it's a platform-level anchor, not something this call creates for you.", None),
        ("List Seasons", "GET", "/api/v1/leagues/{leagueId}/seasons", "bearer", None,
         [("status", "", True)], "List a League's Seasons (optionally filtered by status).", None),
        ("Get Season", "GET", "/api/v1/leagues/{leagueId}/seasons/{seasonId}", "bearer", None, None,
         "Get one Season.", None),
        ("Get League Configuration", "GET", "/api/v1/leagues/{leagueId}/configuration", "bearer", None, None,
         "Get a League's default configuration values.", None),
        ("Update League Configuration", "PUT", "/api/v1/leagues/{leagueId}/configuration", "bearer",
         {
             "initialSquadSize": 15, "weeklyRosterSize": 11,
             "positionalMinimums": {"gk": 1, "def": 3, "mid": 2, "fwd": 1},
             "draftTimerSecondsByType": {"initial": 60, "secondary": 60, "replacement": 60},
             "secondaryDraftSelectionsPerTeam": 2, "secondaryDraftSchedulingOffsetDays": 7,
             "gameweekRosterLockOffsetBeforeKickoffMinutes": 60,
             "leaguePoints": {"win": 3, "draw": 1, "loss": 0},
             "invitationExpirationDays": 7, "replacementSelectionCap": 5,
             "gameweekReminderLeadTimeHours": 24, "tieBreakRulesetVersion": "v1",
         }, None, "Update a League's default configuration values.", None),
        ("Get Season Configuration", "GET", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/configuration", "bearer",
         None, None, "Get a Season's own (possibly overridden, possibly field-locked) configuration.", None),
        ("Update Season Configuration", "PUT", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/configuration", "bearer",
         {
             "initialSquadSize": 15, "weeklyRosterSize": 11,
             "positionalMinimums": {"gk": 1, "def": 3, "mid": 2, "fwd": 1},
             "draftTimerSecondsByType": {"initial": 60, "secondary": 60, "replacement": 60},
             "secondaryDraftSelectionsPerTeam": 2, "secondaryDraftSchedulingOffsetDays": 7,
             "gameweekRosterLockOffsetBeforeKickoffMinutes": 60,
             "leaguePoints": {"win": 3, "draw": 1, "loss": 0},
             "invitationExpirationDays": 7, "replacementSelectionCap": 5,
             "gameweekReminderLeadTimeHours": 24, "tieBreakRulesetVersion": "v1",
         }, None, "Update a Season's configuration (rejected whole if any changed field is locked).", None),
        ("List League Messages", "GET", "/api/v1/leagues/{leagueId}/messages", "bearer", None, None,
         "List a League's announcement messages.", None),
        ("Create League Message", "POST", "/api/v1/leagues/{leagueId}/messages", "bearer",
         {"body": "Welcome to the league! Draft night is Friday at 8pm."}, None,
         "Post a new League announcement message.", None),
        ("Get Audit Log", "GET", "/api/v1/leagues/{leagueId}/audit", "bearer", None,
         [("limit", "50", True), ("cursor", "", True)],
         "Paginated administrative-action audit log for a League.", None),
    ]),
    ("Fantasy Team", [
        ("Create Fantasy Team", "POST", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", "bearer",
         None, None, "Create the caller's FantasyTeam for a Season.", None),
        ("List Fantasy Teams", "GET", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", "bearer",
         None, None, "List every FantasyTeam in a Season.", None),
        ("Get Fantasy Team", "GET", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}", "bearer",
         None, None, "Get one FantasyTeam.", None),
        ("Get Squad", "GET", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/squad", "bearer",
         None, [("position", "", True), ("search", "", True), ("sort", "", True)],
         "Get a FantasyTeam's full squad, including released-player history (filterable/sortable by position/search/sort).", None),
        ("Declare Season-Ending Injury", "POST", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/squad-players/{squadPlayerId}/declare-season-ending-injury", "bearer",
         {"reason": "Season-ending ACL injury."}, None,
         "Mark a squad player as season-ending-injured, granting a replacement opportunity.", None),
        ("List Replacement Opportunities", "GET", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/replacement-opportunities", "bearer",
         None, None, "List a FantasyTeam's unspent replacement opportunities.", None),
        ("Spend Replacement Opportunity", "POST", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/replacement-opportunities/{replacementOpportunityId}/spend", "bearer",
         {"playerId": "{{playerId}}"}, None, "Spend a replacement opportunity to pick a new player.", None),
        ("Get Season Goal Prediction", "GET", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction", "bearer",
         None, None, "Get a FantasyTeam's season goal prediction.", None),
        ("Submit Season Goal Prediction", "PUT", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction", "bearer",
         {"predictedEplGoals": 95}, None, "Submit/update a FantasyTeam's season goal prediction.", None),
    ]),
    ("Draft Management", [
        ("Create Draft", "POST", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", "bearer",
         {"draftType": "Initial"}, None,
         "Start a Draft (only draftType: Initial is supported today). Requires at least 2 FantasyTeams already created for the Season (BR-302) -- a single-user run through this collection only creates one, so expect a 409 insufficient_fantasy_teams until a second user/FantasyTeam exists.", None),
        ("List Drafts", "GET", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/drafts", "bearer",
         None, [("draftType", "", True)], "List a Season's Drafts (optionally filtered by draftType).", None),
        ("Get Draft", "GET", "/api/v1/drafts/{draftId}", "bearer", None, None,
         "Get a Draft's current state (turn, timer, pending makeup picks, etc.).", None),
        ("Make Draft Pick", "POST", "/api/v1/drafts/{draftId}/picks", "bearer",
         {"playerId": "{{playerId}}"}, None,
         "Make the current pick (only the FantasyTeam currently on the clock may call this). Include an Idempotency-Key header for safe retries.", None),
        ("Extend Draft Timer", "POST", "/api/v1/drafts/{draftId}/timer/extend", "bearer",
         {"additionalSeconds": 60}, None, "Extend the current pick's timer deadline.", None),
        ("List Draft Selections", "GET", "/api/v1/drafts/{draftId}/selections", "bearer",
         None, [("limit", "50", True), ("cursor", "", True)],
         "Paginated list of picks made so far, in order.", None),
        ("Get Draft Player Pool", "GET", "/api/v1/drafts/{draftId}/player-pool", "bearer",
         None, [("position", "", True), ("search", "", True), ("sort", "", True)],
         "List undrafted players still available (filterable/sortable by position/search/sort).", None),
    ]),
    ("Roster Management", [
        ("Get Gameweek Roster", "GET", "/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", "bearer",
         None, None, "Get a FantasyTeam's roster for a Gameweek.", None),
        ("Submit Gameweek Roster", "PUT", "/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", "bearer",
         {"playerIds": ["{{playerId}}"], "captainPlayerId": "{{playerId}}"}, None,
         "Submit/replace a FantasyTeam's roster for a Gameweek (rejected once locked). Supports an If-Match header carrying the row's xmin for optimistic concurrency.", None),
        ("Set Captain", "PUT", "/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster/captain", "bearer",
         {"captainPlayerId": "{{playerId}}"}, None,
         "Change the roster's designated Captain without touching the rest of the roster.", None),
        ("Correct Roster (Admin)", "POST", "/api/v1/admin/rosters/{gameweekRosterId}/correct", "bearer",
         {"playerIds": ["{{playerId}}"], "captainPlayerId": "{{playerId}}", "reason": "Correcting a data entry error."}, None,
         "Administrator correction of an already-locked/scored roster's players and/or Captain.", None),
    ]),
    ("Scoring", [
        ("Get Gameweek Score", "GET", "/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/score", "bearer",
         None, None, "Get a FantasyTeam's calculated score for a Gameweek.", None),
        ("Create Score Override (Admin)", "POST", "/api/v1/admin/score-overrides", "bearer",
         {"playerPerformanceId": "{{playerPerformanceId}}", "leagueId": "{{leagueId}}",
          "overrideValue": {"goals": 2}, "reason": "Official data correction."}, None,
         "Create a manual score override for a player's Gameweek performance.", None),
        ("Undo Score Override (Admin)", "POST", "/api/v1/admin/score-overrides/{scoreOverrideId}/undo", "bearer",
         None, None, "Undo (deactivate) an existing score override.", None),
    ]),
    ("Competition", [
        ("Get Schedule", "GET", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/schedule", "bearer",
         None, [("gameweekId", "", True)], "Get the head-to-head match schedule (optionally filtered to one Gameweek).", None),
        ("Get Match", "GET", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/matches/{matchId}", "bearer",
         None, None, "Get one head-to-head match's result.", None),
        ("Get Standings", "GET", "/api/v1/leagues/{leagueId}/seasons/{seasonId}/standings", "bearer",
         None, [("asOfGameweekId", "", True)], "Get the League's current (or as-of-a-past-Gameweek) standings table.", None),
    ]),
    ("Player & EPL Reference Data", [
        ("List Clubs", "GET", "/api/v1/epl/clubs", "bearer", None, None, "List all EPL clubs.", None),
        ("List Players", "GET", "/api/v1/epl/players", "bearer", None,
         [("clubId", "", True), ("position", "", True), ("search", "", True)],
         "List EPL players (filterable by clubId, position, search).", None),
        ("Get Player", "GET", "/api/v1/epl/players/{playerId}", "bearer", None, None,
         "Get one EPL player.", None),
        ("List Gameweeks", "GET", "/api/v1/epl/gameweeks", "bearer", None,
         [("eplSeasonIdentifier", "{{eplSeasonIdentifier}}", False)],
         "List Gameweeks for a given EPL season.", None),
        ("Get Gameweek Fixtures", "GET", "/api/v1/epl/gameweeks/{gameweekId}/fixtures", "bearer", None, None,
         "List a Gameweek's fixtures.", None),
        ("Get EPL Table", "GET", "/api/v1/epl/seasons/{eplSeasonIdentifier}/table", "bearer", None, None,
         "Get the real-world EPL league table for a season.", None),
    ]),
    ("System Administration", [
        ("Get Rate Limit Configuration", "GET", "/api/v1/admin/security/rate-limits", "bearer", None, None,
         "View the current rate-limiting configuration.", None),
        ("Get Security Events", "GET", "/api/v1/admin/security/events", "bearer", None,
         [("limit", "50", True), ("cursor", "", True)],
         "Paginated feed of recorded security events (rate-limit blocks, etc.).", None),
        ("Get CSRF Status", "GET", "/api/v1/admin/security/csrf-status", "bearer", None, None,
         "Report whether cookie auth / CSRF middleware is active (informational; this API is bearer-JWT only today).", None),
    ]),
]

# Collection-level variables users fill in as they exercise the API.
COLLECTION_VARIABLES = [
    ("baseUrl", "http://localhost:5088"),
    ("accessToken", ""),
    ("refreshToken", ""),
    ("resetToken", ""),
    ("leagueId", ""),
    ("seasonId", ""),
    ("fantasyTeamId", ""),
    ("membershipId", ""),
    ("invitationId", ""),
    ("invitationToken", ""),
    ("draftId", ""),
    ("gameweekId", ""),
    ("gameweekRosterId", ""),
    ("scoreOverrideId", ""),
    ("replacementOpportunityId", ""),
    ("squadPlayerId", ""),
    ("playerId", ""),
    ("playerPerformanceId", ""),
    ("clubId", ""),
    ("matchId", ""),
    ("profileIconId", ""),
    ("eplSeasonIdentifier", "2026-27"),
]

AUTH_TOKEN_CAPTURE_SCRIPT = [
    "if ([200, 201].includes(pm.response.code)) {",
    "    const json = pm.response.json();",
    "    if (json.accessToken) pm.collectionVariables.set('accessToken', json.accessToken);",
    "    if (json.refreshToken) pm.collectionVariables.set('refreshToken', json.refreshToken);",
    "}",
]

# Requests whose response carries an id worth auto-saving into a collection variable, so the next
# request down the chain (e.g. Get League after Create League) already has a real value to use.
ID_CAPTURE = {
    "Create League": ("leagueId", "leagueId"),
    "Create Season": ("seasonId", "seasonId"),
    "Create Fantasy Team": ("fantasyTeamId", "fantasyTeamId"),
    "Create Draft": ("draftId", "draftId"),
    "Create Invitation": ("invitationId", "invitationId"),
}


def id_capture_script(response_field, variable_name):
    return [
        "if ([200, 201].includes(pm.response.code)) {",
        "    const json = pm.response.json();",
        f"    if (json.{response_field}) pm.collectionVariables.set('{variable_name}', json.{response_field});",
        "}",
    ]


def to_postman_path(path: str) -> str:
    # {leagueId} -> {{leagueId}}
    out = []
    i = 0
    while i < len(path):
        c = path[i]
        if c == "{":
            end = path.index("}", i)
            var = path[i + 1:end]
            out.append("{{" + var + "}}")
            i = end + 1
        else:
            out.append(c)
            i += 1
    return "".join(out)


def build_url(path: str, query):
    pm_path = to_postman_path(path)
    raw = "{{baseUrl}}" + pm_path
    url = {
        "raw": raw,
        "host": ["{{baseUrl}}"],
        "path": [p for p in pm_path.lstrip("/").split("/") if p != ""],
    }
    if query:
        raw_qs = []
        q_list = []
        for key, value, disabled in query:
            q_list.append({"key": key, "value": value, "disabled": disabled})
            if not disabled:
                raw_qs.append(f"{key}={value}")
        url["query"] = q_list
        if raw_qs:
            url["raw"] = raw + "?" + "&".join(raw_qs)
    return url


def build_request(name, method, path, auth, body, query, description, script_tag):
    request = {
        "method": method,
        "header": [],
        "url": build_url(path, query),
        "description": description,
    }
    if body is not None:
        request["header"].append({"key": "Content-Type", "value": "application/json"})
        request["body"] = {
            "mode": "raw",
            "raw": json.dumps(body, indent=2),
            "options": {"raw": {"language": "json"}},
        }
    if name == "Make Draft Pick":
        request["header"].append({"key": "Idempotency-Key", "value": "{{$guid}}"})
    if auth == "noauth":
        request["auth"] = {"type": "noauth"}

    item = {
        "name": name,
        "request": request,
        "response": [],
    }
    if script_tag == "auth" and name in ("Register", "Login", "Refresh Token"):
        item["event"] = [{
            "listen": "test",
            "script": {"type": "text/javascript", "exec": AUTH_TOKEN_CAPTURE_SCRIPT},
        }]
    elif name in ID_CAPTURE:
        response_field, variable_name = ID_CAPTURE[name]
        item["event"] = [{
            "listen": "test",
            "script": {"type": "text/javascript", "exec": id_capture_script(response_field, variable_name)},
        }]
    return item


def build_collection():
    items = []
    for folder_name, endpoints in FOLDERS:
        folder_items = []
        for entry in endpoints:
            name, method, path, auth, body, query, description, script_tag = entry
            folder_items.append(build_request(name, method, path, auth, body, query, description, script_tag))
        items.append({
            "name": folder_name,
            "item": folder_items,
        })

    # Top-level health check, outside any folder.
    health = {
        "name": "Health Check",
        "request": {
            "method": "GET",
            "header": [],
            "url": build_url("/api/v1/health", None),
            "description": "Confirms the API is running and its database connection is healthy. No auth required.",
            "auth": {"type": "noauth"},
        },
        "response": [],
    }
    items.insert(0, health)

    # Retiring the account is a destructive, one-way action (BR-013 soft delete) -- deliberately
    # placed last so a top-to-bottom collection run exercises every other endpoint as the same
    # active user first, instead of retiring mid-run (JWTs stay valid after retirement, so nothing
    # downstream would technically break, but running "create a league" AFTER "retire my account"
    # reads as confusing/wrong to anyone stepping through the collection in order).
    retire = build_request(
        "Retire Current User", "POST", "/api/v1/users/me/retire", "bearer", None, None,
        "Soft-delete (retire) the caller's own account; username becomes reusable. Deliberately "
        "the last request in this collection -- run it only when you're done exercising everything "
        "else as this user.",
        None,
    )
    items.append(retire)

    collection = {
        "info": {
            "_postman_id": uid(),
            "name": "Fantasy EPL League Manager API",
            "description": (
                "Every HTTP endpoint currently implemented in EplFantasy.Api, generated from "
                "API_ENDPOINTS.md / src/EplFantasy.Api/Controllers. Auth uses a bearer JWT set at "
                "the collection level via {{accessToken}} -- run 'Register' or 'Login' first and "
                "their test scripts will populate {{accessToken}}/{{refreshToken}} automatically "
                "for every other request. 'Create League', 'Create Season', 'Create Fantasy Team', "
                "'Create Draft', and 'Create Invitation' likewise auto-save their new resource's id "
                "into {{leagueId}}/{{seasonId}}/{{fantasyTeamId}}/{{draftId}}/{{invitationId}} so "
                "the natural creation order (League -> Season -> Fantasy Team -> Draft) chains "
                "through the rest of the collection with no manual copy-pasting. Everything else "
                "(playerId, clubId, gameweekId, membershipId, etc.) needs to be filled in by hand "
                "from an earlier response, since this collection doesn't try to guess which "
                "specific player/club/gameweek you want to exercise."
            ),
            "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
        },
        "auth": {
            "type": "bearer",
            "bearer": [{"key": "token", "value": "{{accessToken}}", "type": "string"}],
        },
        "variable": [{"key": k, "value": v, "type": "string"} for k, v in COLLECTION_VARIABLES],
        "item": items,
    }
    return collection


if __name__ == "__main__":
    collection = build_collection()
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_path = os.path.join(repo_root, "EplFantasy.postman_collection.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(collection, f, indent=2)
    # Count total requests for a sanity check.
    total = 0
    def count(items):
        global total
        for it in items:
            if "item" in it:
                count(it["item"])
            else:
                total += 1
    count(collection["item"])
    print(f"Wrote {out_path} with {total} requests")
