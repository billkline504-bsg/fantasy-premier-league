namespace EplFantasy.Infrastructure.Authorization;

/// <summary>
/// Policy name constants a controller action references via <c>[Authorize(Policy = ...)]</c> —
/// this is the "every controller action declares the object-level check it performs" half of
/// Architecture §9.3; the handlers in this folder are the "shared authorization-handler pipeline
/// enforces it" half. No controller re-implements a membership/administrator/system-administrator
/// check inline.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>Caller must be an active member (BR-163) of the League named by the route's "leagueId" value. Satisfied by the League Administrator too — administrator is a property of an active membership, not a separate, larger membership.</summary>
    public const string ActiveLeagueMember = "ActiveLeagueMember";

    /// <summary>Caller must be the League Administrator (ADR-007) of the League named by the route's "leagueId" value.</summary>
    public const string LeagueAdministrator = "LeagueAdministrator";

    /// <summary>Caller must have User.IsSystemAdministrator = true (ADR-007) — platform-level, never League-scoped, never granted by any self-service flow.</summary>
    public const string SystemAdministrator = "SystemAdministrator";

    /// <summary>Caller must own the FantasyTeam named by the route's "fantasyTeamId" value — i.e. the LeagueMembership behind it belongs to the caller.</summary>
    public const string FantasyTeamOwner = "FantasyTeamOwner";

    /// <summary>F-003.3 (leaveLeague): caller must own the route's "membershipId" value, or be the League Administrator of the route's "leagueId" value (removing another member).</summary>
    public const string MembershipOwnerOrLeagueAdministrator = "MembershipOwnerOrLeagueAdministrator";

    /// <summary>F-002.2 (setLeagueIcon/clearLeagueIcon): caller must own the route's "membershipId" value — no Administrator-on-behalf-of fallback (unlike MembershipOwnerOrLeagueAdministrator), since only the member themselves picks their own icon.</summary>
    public const string MembershipOwner = "MembershipOwner";

    /// <summary>F-010.3 (getSeasonGoalPrediction): caller must own the route's "fantasyTeamId" value, or be an active member of the route's "leagueId" value.</summary>
    public const string FantasyTeamOwnerOrActiveLeagueMember = "FantasyTeamOwnerOrActiveLeagueMember";

    /// <summary>F-005.2 (makeDraftPick): caller must own the FantasyTeam whose turn it currently is in the Draft named by the route's "draftId" value — resolved from the Draft's own state (CurrentRound/CurrentPickIndex), not a route value, since the route names no fantasyTeamId at all.</summary>
    public const string DraftTurnOwner = "DraftTurnOwner";

    /// <summary>F-005.3 (extendDraftTimer): caller must be the League Administrator of the Draft named by the route's "draftId" value — resolved via Draft.SeasonId → Season.LeagueId, since the route names no leagueId at all.</summary>
    public const string DraftLeagueAdministrator = "DraftLeagueAdministrator";

    /// <summary>F-005.5 (getDraft/listDraftSelections/getDraftPlayerPool): caller must be an active member of the Draft named by the route's "draftId" value — resolved the same Draft.SeasonId → Season.LeagueId way as DraftLeagueAdministrator, just checking active membership rather than administrator status.</summary>
    public const string DraftLeagueMember = "DraftLeagueMember";

    /// <summary>F-007.4 (correctRoster): caller must be the League Administrator of the GameweekRoster named by the route's "gameweekRosterId" value — resolved via GameweekRoster.FantasyTeamId → FantasyTeam.LeagueMembershipId → LeagueMembership.LeagueId, since the route names no leagueId at all.</summary>
    public const string RosterLeagueAdministrator = "RosterLeagueAdministrator";

    /// <summary>F-008.1 (getGameweekScore): caller must be an active member of the FantasyTeam's League named by the route's "fantasyTeamId" value — no owner shortcut, unlike FantasyTeamOwnerOrActiveLeagueMember.</summary>
    public const string FantasyTeamLeagueMember = "FantasyTeamLeagueMember";

    /// <summary>F-008.5 (undoScoreOverride): caller must be the League Administrator of the ScoreOverride named by the route's "scoreOverrideId" value — resolved via ScoreOverride.AdministratorMembershipId → LeagueMembership.LeagueId, since the route names no leagueId at all. createScoreOverride has no analogous route-based policy — see IScoreOverrideService's own remarks on why its League Administrator check happens inline in the controller instead.</summary>
    public const string ScoreOverrideLeagueAdministrator = "ScoreOverrideLeagueAdministrator";

    /// <summary>F-006.4 (listReplacementOpportunities): caller must own the route's "fantasyTeamId" value, or be the League Administrator of the route's "leagueId" value — both are present directly in this route, unlike FantasyTeamOwnerOrActiveLeagueMember's own indirect-resolution cases.</summary>
    public const string FantasyTeamOwnerOrLeagueAdministrator = "FantasyTeamOwnerOrLeagueAdministrator";
}
